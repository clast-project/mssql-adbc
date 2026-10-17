// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Unicode;
using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Scalars.Variant;
using Apache.Arrow.Types;
using Clast.Drivers.SqlServer.Tds;

namespace Clast.Drivers.SqlServer.Arrow;

/// <summary>
/// <c>fixed_size_list&lt;float32&gt;</c> (or <c>list&lt;float32&gt;</c> with
/// the right length) into vector: an 8-byte header, then the floats.
/// </summary>
internal sealed class VectorEncoder : ColumnEncoder
{
    private readonly int _dimensions;
    private IArrowArray _array = null!;
    private FloatArray _values = null!;
    private int[]? _listOffsets;

    public VectorEncoder(TdsColumn target, int sourceIndex) : base(target, sourceIndex)
    {
        _dimensions = target.VectorDimensions;
    }

    public override void Bind(IArrowArray array)
    {
        _array = array;
        switch (array)
        {
            case FixedSizeListArray fixedList:
                _values = (FloatArray)fixedList.Values;
                _listOffsets = null;
                break;
            case ListArray list:
                _values = (FloatArray)list.Values;
                _listOffsets = list.ValueOffsets.ToArray();
                break;
            default:
                throw new InvalidOperationException();
        }
        if (_values.NullCount > 0)
        {
            throw new AdbcException($"Vector elements for column '{Target.Name}' cannot be null", AdbcStatusCode.InvalidData);
        }
    }

    public override void Write(PacketWriter writer, int row)
    {
        if (_array.IsNull(row))
        {
            writer.WriteUInt16(0xFFFF);
            return;
        }

        int start;
        if (_listOffsets != null)
        {
            start = _listOffsets[row];
            if (_listOffsets[row + 1] - start != _dimensions)
            {
                throw new AdbcException(
                    $"Row {row} has {_listOffsets[row + 1] - start} elements, but column '{Target.Name}' is {Target.SqlTypeName}",
                    AdbcStatusCode.InvalidData);
            }
        }
        else
        {
            start = (_array.Offset + row) * _dimensions;
        }

        writer.WriteUInt16((ushort)(TdsColumn.VectorHeaderLength + _dimensions * 4));
        Span<byte> header = stackalloc byte[TdsColumn.VectorHeaderLength];
        header.Clear();
        header[0] = 0xA9; // layout format
        header[1] = 0x01; // layout version
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(2), (ushort)_dimensions);
        writer.WriteBytes(header);
        writer.WriteBytes(MemoryMarshal.AsBytes(_values.Values.Slice(start, _dimensions)));
    }
}

/// <summary>
/// <c>arrow.parquet.variant</c> into sql_variant. Each variant primitive is
/// sent as the closest base type:
/// <list type="bullet">
/// <item>int8 and int16 become smallint; int32 and int64 become int and bigint.</item>
/// <item>float and double become real and float; booleans become bit.</item>
/// <item>decimal4/8/16 become decimal(9/18/38, scale).</item>
/// <item>date becomes date; time becomes time(6).</item>
/// <item>timestamp_ntz becomes datetime2 and timestamp becomes datetimeoffset
/// at +00:00, with scale 6 (7 for nanosecond values, truncated to 100 ns).</item>
/// <item>uuid becomes uniqueidentifier; string becomes nvarchar; binary becomes
/// varbinary.</item>
/// <item>Variant null becomes NULL.</item>
/// </list>
/// Objects and arrays have no sql_variant form and are rejected.
/// </summary>
internal sealed class VariantEncoder : ColumnEncoder
{
    private const int MaxDataLength = 8000;
    private readonly byte[] _collation;
    private VariantArray _array = null!;
    private byte[] _utf16 = new byte[512];

    public VariantEncoder(TdsColumn target, int sourceIndex, byte[] defaultCollation) : base(target, sourceIndex)
    {
        _collation = defaultCollation.Length == 5 ? defaultCollation : new byte[5];
    }

    public override void Bind(IArrowArray array) => _array = (VariantArray)array;

    public override void Write(PacketWriter writer, int row)
    {
        if (_array.IsNull(row))
        {
            writer.WriteInt32(0);
            return;
        }

        VariantReader value = _array.GetVariantReader(row);
        if (value.IsNull)
        {
            writer.WriteInt32(0);
            return;
        }
        if (value.IsObject || value.IsArray)
        {
            throw new AdbcException(
                $"Row {row} of column '{Target.Name}' holds a variant {(value.IsObject ? "object" : "array")}, which sql_variant cannot store",
                AdbcStatusCode.InvalidData);
        }
        if (value.IsString)
        {
            WriteString(writer, row, value.GetStringBytes());
            return;
        }

        Span<byte> data = stackalloc byte[16];
        switch (value.PrimitiveType)
        {
            case VariantPrimitiveType.BooleanTrue:
            case VariantPrimitiveType.BooleanFalse:
                data[0] = value.GetBoolean() ? (byte)1 : (byte)0;
                Write(writer, TdsDataType.Bit, [], data.Slice(0, 1));
                break;
            case VariantPrimitiveType.Int8:
                BinaryPrimitives.WriteInt16LittleEndian(data, value.GetInt8());
                Write(writer, TdsDataType.Int2, [], data.Slice(0, 2));
                break;
            case VariantPrimitiveType.Int16:
                BinaryPrimitives.WriteInt16LittleEndian(data, value.GetInt16());
                Write(writer, TdsDataType.Int2, [], data.Slice(0, 2));
                break;
            case VariantPrimitiveType.Int32:
                BinaryPrimitives.WriteInt32LittleEndian(data, value.GetInt32());
                Write(writer, TdsDataType.Int4, [], data.Slice(0, 4));
                break;
            case VariantPrimitiveType.Int64:
                BinaryPrimitives.WriteInt64LittleEndian(data, value.GetInt64());
                Write(writer, TdsDataType.Int8, [], data.Slice(0, 8));
                break;
            case VariantPrimitiveType.Float:
                BinaryPrimitives.WriteSingleLittleEndian(data, value.GetFloat());
                Write(writer, TdsDataType.Float4, [], data.Slice(0, 4));
                break;
            case VariantPrimitiveType.Double:
                BinaryPrimitives.WriteDoubleLittleEndian(data, value.GetDouble());
                Write(writer, TdsDataType.Float8, [], data.Slice(0, 8));
                break;

            case VariantPrimitiveType.Decimal4:
            case VariantPrimitiveType.Decimal8:
            case VariantPrimitiveType.Decimal16:
                WriteDecimal(writer, value);
                break;

            case VariantPrimitiveType.Date:
                WriteUInt24(data, value.GetDateDays() + TemporalDecoder.DaysTo1970From0001);
                Write(writer, TdsDataType.DateN, [], data.Slice(0, 3));
                break;
            case VariantPrimitiveType.TimeNtz:
                WriteTime(data, value.GetTimeNtzMicros(), 5);
                Write(writer, TdsDataType.TimeN, [6], data.Slice(0, 5));
                break;
            case VariantPrimitiveType.TimestampNtz:
                WriteDateTime2(writer, value.GetTimestampNtzMicros() * 10, 6, offset: false);
                break;
            case VariantPrimitiveType.Timestamp:
                WriteDateTime2(writer, value.GetTimestampMicros() * 10, 6, offset: true);
                break;
            case VariantPrimitiveType.TimestampNtzNanos:
                WriteDateTime2(writer, FloorDiv(value.GetTimestampNtzNanos(), 100), 7, offset: false);
                break;
            case VariantPrimitiveType.TimestampTzNanos:
                WriteDateTime2(writer, FloorDiv(value.GetTimestampTzNanos(), 100), 7, offset: true);
                break;

            case VariantPrimitiveType.Uuid:
            {
                ReadOnlySpan<byte> s = value.GetUuidBytes();
                data[0] = s[3];
                data[1] = s[2];
                data[2] = s[1];
                data[3] = s[0];
                data[4] = s[5];
                data[5] = s[4];
                data[6] = s[7];
                data[7] = s[6];
                s.Slice(8, 8).CopyTo(data.Slice(8));
                Write(writer, TdsDataType.Guid, [], data);
                break;
            }

            case VariantPrimitiveType.Binary:
            {
                ReadOnlySpan<byte> bytes = value.GetBinary();
                if (bytes.Length > MaxDataLength)
                {
                    throw TooLong(row);
                }
                Span<byte> properties = stackalloc byte[2];
                BinaryPrimitives.WriteUInt16LittleEndian(properties, MaxDataLength);
                Write(writer, TdsDataType.BigVarBinary, properties, bytes);
                break;
            }

            default:
                throw new AdbcException(
                    $"Row {row} of column '{Target.Name}' holds a variant {value.PrimitiveType}, which is not supported for sql_variant",
                    AdbcStatusCode.NotImplemented);
        }
    }

    /// <summary>Writes a sql_variant value: total length, base type, properties, data.</summary>
    private static void Write(PacketWriter writer, TdsDataType baseType, ReadOnlySpan<byte> properties, ReadOnlySpan<byte> data)
    {
        writer.WriteInt32(2 + properties.Length + data.Length);
        writer.WriteByte((byte)baseType);
        writer.WriteByte((byte)properties.Length);
        writer.WriteBytes(properties);
        writer.WriteBytes(data);
    }

    private void WriteString(PacketWriter writer, int row, ReadOnlySpan<byte> utf8)
    {
        if (_utf16.Length < utf8.Length * 2)
        {
            _utf16 = new byte[utf8.Length * 2];
        }
        Utf8.ToUtf16(utf8, MemoryMarshal.Cast<byte, char>(_utf16.AsSpan()), out _, out int chars, replaceInvalidSequences: true);
        if (chars * 2 > MaxDataLength)
        {
            throw TooLong(row);
        }
        Span<byte> properties = stackalloc byte[7];
        _collation.CopyTo(properties);
        BinaryPrimitives.WriteUInt16LittleEndian(properties.Slice(5), MaxDataLength);
        Write(writer, TdsDataType.NVarChar, properties, _utf16.AsSpan(0, chars * 2));
    }

    /// <summary>decimal(9/18/38, scale): sign byte, then the magnitude in 4, 8 or 16 bytes.</summary>
    private static void WriteDecimal(PacketWriter writer, VariantReader value)
    {
        // Encoded as header, scale, then the little-endian unscaled integer.
        ReadOnlySpan<byte> raw = value.Value;
        byte scale = raw[1];
        ReadOnlySpan<byte> unscaled = raw.Slice(2);
        Int128 integer = unscaled.Length switch
        {
            4 => BinaryPrimitives.ReadInt32LittleEndian(unscaled),
            8 => BinaryPrimitives.ReadInt64LittleEndian(unscaled),
            _ => BinaryPrimitives.ReadInt128LittleEndian(unscaled),
        };
        byte precision = unscaled.Length switch { 4 => 9, 8 => 18, _ => 38 };
        int magnitudeLength = unscaled.Length;

        Span<byte> data = stackalloc byte[17];
        data[0] = integer < 0 ? (byte)0 : (byte)1;
        BinaryPrimitives.WriteUInt128LittleEndian(data.Slice(1), (UInt128)(integer < 0 ? -integer : integer));
        Write(writer, TdsDataType.DecimalN, [precision, scale], data.Slice(0, 1 + magnitudeLength));
    }

    /// <summary>datetime2 or datetimeoffset (+00:00) from 100-ns ticks since 1970, at the given scale.</summary>
    private static void WriteDateTime2(PacketWriter writer, long ticks, byte scale, bool offset)
    {
        const long TicksPerDay = 864_000_000_000L;
        long days = FloorDiv(ticks, TicksPerDay);
        long timeOfDay = ticks - days * TicksPerDay;
        long units = scale == 7 ? timeOfDay : timeOfDay / 10;
        int timeLength = 5;

        Span<byte> data = stackalloc byte[10];
        WriteTime(data, units, timeLength);
        WriteUInt24(data.Slice(timeLength), checked((int)(days + TemporalDecoder.DaysTo1970From0001)));
        int length = timeLength + 3;
        if (offset)
        {
            data[length] = 0;
            data[length + 1] = 0;
            length += 2;
        }
        Write(writer, offset ? TdsDataType.DateTimeOffsetN : TdsDataType.DateTime2N, [scale], data.Slice(0, length));
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

    private static void WriteTime(Span<byte> target, long value, int length)
    {
        for (int i = 0; i < length; i++)
        {
            target[i] = (byte)(value >> (8 * i));
        }
    }

    private AdbcException TooLong(int row) =>
        new($"Row {row} of column '{Target.Name}' is longer than the 8000 bytes sql_variant can hold", AdbcStatusCode.InvalidData);
}
