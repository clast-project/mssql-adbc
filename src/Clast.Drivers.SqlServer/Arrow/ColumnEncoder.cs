// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;
using Apache.Arrow;
using Apache.Arrow.Arrays;
using Apache.Arrow.Adbc;
using Apache.Arrow.Types;
using Clast.Drivers.SqlServer.Tds;

namespace Clast.Drivers.SqlServer.Arrow;

/// <summary>
/// Writes one Arrow column's values as TDS bulk-load row values for a target
/// column. The target type comes from the destination table; the encoder
/// converts the Arrow source type to it.
/// </summary>
internal abstract class ColumnEncoder
{
    protected ColumnEncoder(TdsColumn target, int sourceIndex)
    {
        Target = target;
        SourceIndex = sourceIndex;
    }

    /// <summary>The destination column, normalized to a nullable wire type.</summary>
    public TdsColumn Target { get; }

    /// <summary>Index of the source column in the bound record batches.</summary>
    public int SourceIndex { get; }

    /// <summary>Points the encoder at the next batch's column.</summary>
    public abstract void Bind(IArrowArray array);

    /// <summary>Writes the value at <paramref name="row"/>, including its length prefix.</summary>
    public abstract void Write(PacketWriter writer, int row);

    protected AdbcException Overflow(int row) =>
        new AdbcException(
            $"Value in row {row} does not fit target column '{Target.Name}' ({Target.SqlTypeName})",
            AdbcStatusCode.InvalidData);
}

internal static class EncoderFactory
{
    public static ColumnEncoder Create(TdsColumn target, Field source, int sourceIndex)
    {
        IArrowType type = source.DataType;
        ColumnEncoder? encoder = target.Type switch
        {
            TdsDataType.IntN when IsInteger(type) => new IntegerEncoder(target, sourceIndex),
            TdsDataType.BitN when type is BooleanType => new BooleanEncoder(target, sourceIndex),
            TdsDataType.FloatN when type is FloatType or DoubleType => new FloatEncoder(target, sourceIndex),
            TdsDataType.DecimalN or TdsDataType.NumericN when type is Decimal128Type d && d.Scale <= target.Scale =>
                new DecimalEncoder(target, sourceIndex, d.Scale),
            TdsDataType.NVarChar or TdsDataType.NChar when type is StringType => new StringEncoder(target, sourceIndex, null),
            TdsDataType.BigVarChar or TdsDataType.BigChar when type is StringType => new StringEncoder(target, sourceIndex, target.Collation),
            TdsDataType.BigVarBinary or TdsDataType.BigBinary when type is BinaryType => new BinaryEncoder(target, sourceIndex),
            TdsDataType.Guid when type is GuidType || type is FixedSizeBinaryType { ByteWidth: 16 } => new GuidEncoder(target, sourceIndex),
            TdsDataType.DateN when type is Date32Type or Date64Type || type is TimestampType => new TemporalEncoder(target, sourceIndex, type),
            TdsDataType.DateTime2N or TdsDataType.DateTimeN when type is TimestampType || type is Date32Type or Date64Type =>
                new TemporalEncoder(target, sourceIndex, type),
            TdsDataType.DateTimeOffsetN when type is TimestampWithOffsetType || type is TimestampType { IsTimeZoneAware: true } =>
                new TemporalEncoder(target, sourceIndex, type),
            TdsDataType.TimeN when type is Time32Type or Time64Type => new TemporalEncoder(target, sourceIndex, type),
            _ => null,
        };

        return encoder ?? throw new AdbcException(
            $"Cannot ingest Arrow type {Describe(type)} into column '{target.Name}' ({target.SqlTypeName})",
            AdbcStatusCode.NotImplemented);
    }

    private static bool IsInteger(IArrowType type) => type.TypeId is
        ArrowTypeId.Int8 or ArrowTypeId.Int16 or ArrowTypeId.Int32 or ArrowTypeId.Int64 or
        ArrowTypeId.UInt8 or ArrowTypeId.UInt16 or ArrowTypeId.UInt32 or ArrowTypeId.UInt64;

    private static string Describe(IArrowType type) => type is ExtensionType e ? e.Name : type.Name;
}

/// <summary>Any Arrow integer type into tinyint, smallint, int or bigint.</summary>
internal sealed class IntegerEncoder : ColumnEncoder
{
    private readonly int _size;
    private IArrowArray _array = null!;
    private Func<int, long> _get = null!;

    public IntegerEncoder(TdsColumn target, int sourceIndex) : base(target, sourceIndex)
    {
        _size = target.MaxLength;
    }

    public override void Bind(IArrowArray array)
    {
        _array = array;
        _get = array switch
        {
            Int8Array a => i => a.Values[i],
            Int16Array a => i => a.Values[i],
            Int32Array a => i => a.Values[i],
            Int64Array a => i => a.Values[i],
            UInt8Array a => i => a.Values[i],
            UInt16Array a => i => a.Values[i],
            UInt32Array a => i => a.Values[i],
            UInt64Array a => i => checked((long)a.Values[i]),
            _ => throw new InvalidOperationException(),
        };
    }

    public override void Write(PacketWriter writer, int row)
    {
        if (_array.IsNull(row))
        {
            writer.WriteByte(0);
            return;
        }

        long value = _get(row);
        writer.WriteByte((byte)_size);
        switch (_size)
        {
            case 1:
                if ((ulong)value > byte.MaxValue) throw Overflow(row);
                writer.WriteByte((byte)value);
                break;
            case 2:
                if (value is < short.MinValue or > short.MaxValue) throw Overflow(row);
                writer.WriteUInt16(unchecked((ushort)(short)value));
                break;
            case 4:
                if (value is < int.MinValue or > int.MaxValue) throw Overflow(row);
                writer.WriteInt32((int)value);
                break;
            default:
                writer.WriteInt64(value);
                break;
        }
    }
}

internal sealed class BooleanEncoder : ColumnEncoder
{
    private BooleanArray _array = null!;

    public BooleanEncoder(TdsColumn target, int sourceIndex) : base(target, sourceIndex)
    {
    }

    public override void Bind(IArrowArray array) => _array = (BooleanArray)array;

    public override void Write(PacketWriter writer, int row)
    {
        bool? value = _array.GetValue(row);
        if (value == null)
        {
            writer.WriteByte(0);
            return;
        }
        writer.WriteByte(1);
        writer.WriteByte(value.Value ? (byte)1 : (byte)0);
    }
}

internal sealed class FloatEncoder : ColumnEncoder
{
    private readonly int _size;
    private IArrowArray _array = null!;

    public FloatEncoder(TdsColumn target, int sourceIndex) : base(target, sourceIndex)
    {
        _size = target.MaxLength;
    }

    public override void Bind(IArrowArray array) => _array = array;

    public override void Write(PacketWriter writer, int row)
    {
        if (_array.IsNull(row))
        {
            writer.WriteByte(0);
            return;
        }

        double value = _array is FloatArray f ? f.Values[row] : ((DoubleArray)_array).Values[row];
        writer.WriteByte((byte)_size);
        if (_size == 4)
        {
            writer.WriteInt32(BitConverter.SingleToInt32Bits((float)value));
        }
        else
        {
            writer.WriteInt64(BitConverter.DoubleToInt64Bits(value));
        }
    }
}

/// <summary>decimal128 into decimal/numeric, rescaling up to the target scale.</summary>
internal sealed class DecimalEncoder : ColumnEncoder
{
    private static readonly Int128 MaxMagnitude = Int128.Parse("99999999999999999999999999999999999999");

    private readonly int _length;
    private readonly Int128 _scaleFactor;
    private readonly Int128 _maxForPrecision;
    private Decimal128Array _array = null!;

    public DecimalEncoder(TdsColumn target, int sourceIndex, int sourceScale) : base(target, sourceIndex)
    {
        _length = target.MaxLength;
        _scaleFactor = Pow10(target.Scale - sourceScale);
        _maxForPrecision = target.Precision >= 38 ? MaxMagnitude : Pow10(target.Precision) - 1;
    }

    private static Int128 Pow10(int exponent)
    {
        Int128 result = 1;
        for (int i = 0; i < exponent; i++)
        {
            result *= 10;
        }
        return result;
    }

    public override void Bind(IArrowArray array) => _array = (Decimal128Array)array;

    public override void Write(PacketWriter writer, int row)
    {
        if (_array.IsNull(row))
        {
            writer.WriteByte(0);
            return;
        }

        Int128 value = BinaryPrimitives.ReadInt128LittleEndian(_array.ValueBuffer.Span.Slice((_array.Offset + row) * 16, 16));
        bool negative = value < 0;
        Int128 magnitude = negative ? -value : value;
        if (magnitude > _maxForPrecision / _scaleFactor)
        {
            throw Overflow(row);
        }
        magnitude *= _scaleFactor;

        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt128LittleEndian(bytes, (UInt128)magnitude);
        writer.WriteByte((byte)_length);
        writer.WriteByte(negative ? (byte)0 : (byte)1);
        writer.WriteBytes(bytes.Slice(0, _length - 1));
    }
}

/// <summary>utf8 into nvarchar/nchar (UTF-16) or varchar/char (UTF-8 collations pass through; others use the code page).</summary>
internal sealed class StringEncoder : ColumnEncoder
{
    private readonly bool _plp;
    private readonly bool _utf16;
    private readonly Encoding? _codePage;
    private StringArray _array = null!;
    private char[] _chars = new char[256];
    private byte[] _bytes = new byte[256];

    public StringEncoder(TdsColumn target, int sourceIndex, byte[]? collation) : base(target, sourceIndex)
    {
        _plp = target.LengthKind == TdsLengthKind.Plp;
        _utf16 = collation == null;
        if (collation != null)
        {
            var tdsCollation = new TdsCollation(collation);
            if (!tdsCollation.IsUtf8)
            {
                Encoding encoding = tdsCollation.GetEncoding();
                _codePage = encoding.CodePage == 65001 ? null : encoding;
            }
        }
    }

    public override void Bind(IArrowArray array) => _array = (StringArray)array;

    public override void Write(PacketWriter writer, int row)
    {
        if (_array.IsNull(row))
        {
            WriteNull(writer);
            return;
        }

        ReadOnlySpan<byte> utf8 = _array.GetBytes(row);
        ReadOnlySpan<byte> payload;
        if (_utf16)
        {
            payload = MemoryMarshal.AsBytes(ToUtf16(utf8));
        }
        else if (_codePage == null || Ascii.IsValid(utf8))
        {
            payload = utf8;
        }
        else
        {
            ReadOnlySpan<char> chars = ToUtf16(utf8);
            int count = _codePage.GetByteCount(chars);
            if (_bytes.Length < count)
            {
                _bytes = new byte[Math.Max(count, _bytes.Length * 2)];
            }
            payload = _bytes.AsSpan(0, _codePage.GetBytes(chars, _bytes));
        }

        WriteValue(writer, payload, _plp);
    }

    private ReadOnlySpan<char> ToUtf16(ReadOnlySpan<byte> utf8)
    {
        if (_chars.Length < utf8.Length)
        {
            _chars = new char[Math.Max(utf8.Length, _chars.Length * 2)];
        }
        Utf8.ToUtf16(utf8, _chars, out _, out int written, replaceInvalidSequences: true);
        return _chars.AsSpan(0, written);
    }

    private void WriteNull(PacketWriter writer)
    {
        if (_plp)
        {
            writer.WriteInt64(-1);
        }
        else
        {
            writer.WriteUInt16(0xFFFF);
        }
    }

    internal static void WriteValue(PacketWriter writer, ReadOnlySpan<byte> payload, bool plp)
    {
        if (!plp)
        {
            writer.WriteUInt16(checked((ushort)payload.Length));
            writer.WriteBytes(payload);
            return;
        }

        // The server's bulk-load parser expects an unknown total length, as SqlClient sends.
        writer.WriteUInt64(TdsColumn.PlpUnknownLength);
        if (payload.Length > 0)
        {
            writer.WriteInt32(payload.Length);
            writer.WriteBytes(payload);
        }
        writer.WriteInt32(0); // PLP terminator
    }
}

internal sealed class BinaryEncoder : ColumnEncoder
{
    private readonly bool _plp;
    private BinaryArray _array = null!;

    public BinaryEncoder(TdsColumn target, int sourceIndex) : base(target, sourceIndex)
    {
        _plp = target.LengthKind == TdsLengthKind.Plp;
    }

    public override void Bind(IArrowArray array) => _array = (BinaryArray)array;

    public override void Write(PacketWriter writer, int row)
    {
        if (_array.IsNull(row))
        {
            if (_plp)
            {
                writer.WriteInt64(-1);
            }
            else
            {
                writer.WriteUInt16(0xFFFF);
            }
            return;
        }
        StringEncoder.WriteValue(writer, _array.GetBytes(row), _plp);
    }
}

/// <summary>arrow.uuid (RFC 4122 byte order) into uniqueidentifier (mixed-endian).</summary>
internal sealed class GuidEncoder : ColumnEncoder
{
    private FixedSizeBinaryArray _array = null!;

    public GuidEncoder(TdsColumn target, int sourceIndex) : base(target, sourceIndex)
    {
    }

    public override void Bind(IArrowArray array) =>
        _array = array is GuidArray g ? g.StorageArray : (FixedSizeBinaryArray)array;

    public override void Write(PacketWriter writer, int row)
    {
        if (_array.IsNull(row))
        {
            writer.WriteByte(0);
            return;
        }

        ReadOnlySpan<byte> s = _array.GetBytes(row);
        Span<byte> wire = stackalloc byte[17];
        wire[0] = 16;
        wire[1] = s[3];
        wire[2] = s[2];
        wire[3] = s[1];
        wire[4] = s[0];
        wire[5] = s[5];
        wire[6] = s[4];
        wire[7] = s[7];
        wire[8] = s[6];
        s.Slice(8, 8).CopyTo(wire.Slice(9));
        writer.WriteBytes(wire);
    }
}

/// <summary>
/// Arrow dates, timestamps and times into date, datetime2, datetime,
/// datetimeoffset and time.
/// </summary>
internal sealed class TemporalEncoder : ColumnEncoder
{
    private const long DaysTo1970From0001 = 719162;
    private const long DaysTo1970From1900 = 25567;
    private const long TicksPerDay = 864_000_000_000L; // 100 ns

    private readonly TdsDataType _targetType;
    private readonly int _scale;
    private readonly int _timeLength;
    private readonly long _ticksPerUnit; // 100-ns ticks per 10^-scale unit
    private readonly IArrowType _sourceType;
    private IArrowArray _array = null!;
    private TimestampArray? _timestamps;
    private Int16Array? _offsets;

    public TemporalEncoder(TdsColumn target, int sourceIndex, IArrowType sourceType) : base(target, sourceIndex)
    {
        _targetType = target.Type;
        _scale = target.Scale;
        _timeLength = _scale <= 2 ? 3 : _scale <= 4 ? 4 : 5;
        long ticksPerUnit = 1;
        for (int i = _scale; i < 7; i++)
        {
            ticksPerUnit *= 10;
        }
        _ticksPerUnit = ticksPerUnit;
        _sourceType = sourceType;
    }

    public override void Bind(IArrowArray array)
    {
        _array = array;
        if (array is TimestampWithOffsetArray withOffset)
        {
            var storage = (StructArray)withOffset.Storage;
            _timestamps = (TimestampArray)storage.Fields[0];
            _offsets = (Int16Array)storage.Fields[1];
        }
        else
        {
            _timestamps = array as TimestampArray;
            _offsets = null;
        }
    }

    /// <summary>Source value as 100-ns ticks since 1970-01-01 (UTC for zoned values).</summary>
    private long GetTicks(int row)
    {
        switch (_array)
        {
            case Date32Array d:
                return d.Values[row] * TicksPerDay;
            case Date64Array d:
                return d.Values[row] * 10_000L;
            case Time32Array t:
                return ((Time32Type)t.Data.DataType).Unit == TimeUnit.Second ? t.Values[row] * 10_000_000L : t.Values[row] * 10_000L;
            case Time64Array t:
                return ((Time64Type)t.Data.DataType).Unit == TimeUnit.Nanosecond ? t.Values[row] / 100 : t.Values[row] * 10L;
        }

        TimestampArray timestamps = _timestamps!;
        long value = timestamps.Values[row];
        return ((TimestampType)timestamps.Data.DataType).Unit switch
        {
            TimeUnit.Second => checked(value * 10_000_000L),
            TimeUnit.Millisecond => checked(value * 10_000L),
            TimeUnit.Microsecond => checked(value * 10L),
            _ => value / 100,
        };
    }

    public override void Write(PacketWriter writer, int row)
    {
        if (_array.IsNull(row))
        {
            writer.WriteByte(0);
            return;
        }

        long ticks = GetTicks(row);
        Span<byte> wire = stackalloc byte[11];
        int length;

        switch (_targetType)
        {
            case TdsDataType.DateN:
            {
                long days = FloorDiv(ticks, TicksPerDay) + DaysTo1970From0001;
                WriteUInt24(wire.Slice(1), CheckDays(days, row));
                length = 3;
                break;
            }
            case TdsDataType.TimeN:
            {
                WriteTime(wire.Slice(1), ticks / _ticksPerUnit);
                length = _timeLength;
                break;
            }
            case TdsDataType.DateTime2N:
            case TdsDataType.DateTimeOffsetN:
            {
                long days = FloorDiv(ticks, TicksPerDay);
                long timeOfDay = ticks - days * TicksPerDay;
                WriteTime(wire.Slice(1), timeOfDay / _ticksPerUnit);
                WriteUInt24(wire.Slice(1 + _timeLength), CheckDays(days + DaysTo1970From0001, row));
                length = _timeLength + 3;
                if (_targetType == TdsDataType.DateTimeOffsetN)
                {
                    short offset = _offsets?.Values[row] ?? 0;
                    BinaryPrimitives.WriteInt16LittleEndian(wire.Slice(1 + length), offset);
                    length += 2;
                }
                break;
            }
            default: // datetime
            {
                long days = FloorDiv(ticks, TicksPerDay);
                long timeOfDay = ticks - days * TicksPerDay;
                // 1/300 s units, rounded to nearest.
                long sqlTicks = (timeOfDay * 3 + 50_000) / 100_000;
                days += DaysTo1970From1900;
                if (sqlTicks >= 300L * 86400)
                {
                    sqlTicks -= 300L * 86400;
                    days++;
                }
                if (days is < -53690 or > 2958463)
                {
                    throw Overflow(row);
                }
                BinaryPrimitives.WriteInt32LittleEndian(wire.Slice(1), (int)days);
                BinaryPrimitives.WriteUInt32LittleEndian(wire.Slice(5), (uint)sqlTicks);
                length = 8;
                break;
            }
        }

        wire[0] = (byte)length;
        writer.WriteBytes(wire.Slice(0, length + 1));
    }

    private int CheckDays(long days, int row)
    {
        if (days is < 0 or > 3652058)
        {
            throw Overflow(row);
        }
        return (int)days;
    }

    private static long FloorDiv(long value, long divisor)
    {
        long quotient = value / divisor;
        return (value % divisor) < 0 ? quotient - 1 : quotient;
    }

    private static void WriteUInt24(Span<byte> target, int value)
    {
        target[0] = (byte)value;
        target[1] = (byte)(value >> 8);
        target[2] = (byte)(value >> 16);
    }

    private void WriteTime(Span<byte> target, long value)
    {
        for (int i = 0; i < _timeLength; i++)
        {
            target[i] = (byte)(value >> (8 * i));
        }
    }
}
