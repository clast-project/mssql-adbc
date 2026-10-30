// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
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
/// <para>Rows are written one at a time across all columns (TDS rows are
/// row-major), so <see cref="Write"/> runs once per value and is kept lean:
/// <see cref="Bind"/> pins the column's buffers once per batch and resolves
/// its types, and values are read through pointers rather than through
/// <see cref="ArrowBuffer"/> spans, which cost a virtual call each.</para>
/// </summary>
internal abstract unsafe class ColumnEncoder : IDisposable
{
    private MemoryHandle[] _pins = new MemoryHandle[4];
    private int _pinCount;
    private byte* _validity;
    private int _validityOffset;

    protected ColumnEncoder(TdsColumn target, int sourceIndex)
    {
        Target = target;
        SourceIndex = sourceIndex;
    }

    /// <summary>The destination column, normalized to a nullable wire type.</summary>
    public TdsColumn Target { get; }

    /// <summary>Index of the source column in the bound record batches.</summary>
    public int SourceIndex { get; }

    /// <summary>Points the encoder at the next batch's column, releasing the previous one's buffers.</summary>
    public abstract void Bind(IArrowArray array);

    /// <summary>Writes the value at <paramref name="row"/>, including its length prefix.</summary>
    public abstract void Write(PacketWriter writer, int row);

    /// <summary>Releases the bound column's pinned buffers; call before the batch is disposed.</summary>
    public void Release()
    {
        for (int i = 0; i < _pinCount; i++)
        {
            _pins[i].Dispose();
            _pins[i] = default;
        }
        _pinCount = 0;
        _validity = null;
    }

    public void Dispose() => Release();

    /// <summary>Pins a buffer until <see cref="Release"/>; native buffers (the usual case) need no GC pin.</summary>
    protected byte* Pin(ArrowBuffer buffer)
    {
        if (_pinCount == _pins.Length)
        {
            System.Array.Resize(ref _pins, _pins.Length * 2);
        }
        MemoryHandle handle = buffer.Memory.Pin();
        _pins[_pinCount++] = handle;
        return (byte*)handle.Pointer;
    }

    /// <summary>Releases the previous column and captures this one's validity bitmap (none when it has no nulls).</summary>
    protected void BindNulls(IArrowArray array)
    {
        Release();
        ArrowBuffer bitmap = array.Data.Buffers[0];
        _validity = array.NullCount != 0 && !bitmap.IsEmpty ? Pin(bitmap) : null;
        _validityOffset = array.Offset;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected bool IsNullAt(int row)
    {
        if (_validity == null)
        {
            return false;
        }
        int i = _validityOffset + row;
        return (_validity[i >> 3] & (1 << (i & 7))) == 0;
    }

    protected AdbcException Overflow(int row) =>
        new AdbcException(
            $"Value in row {row} does not fit target column '{Target.Name}' ({Target.SqlTypeName})",
            AdbcStatusCode.InvalidData);
}

internal static class EncoderFactory
{
    /// <param name="defaultCollation">The database's default collation, for values that carry their own (sql_variant strings).</param>
    public static ColumnEncoder Create(TdsColumn target, Field source, int sourceIndex, byte[]? defaultCollation = null)
    {
        IArrowType type = source.DataType;
        bool text = type is StringType or JsonType or Utf16StringType;
        ColumnEncoder? encoder = target.Type switch
        {
            TdsDataType.IntN when IsInteger(type) => CreateInteger(target, sourceIndex, type),
            TdsDataType.BitN when type is BooleanType => new BooleanEncoder(target, sourceIndex),
            TdsDataType.FloatN when type is FloatType or DoubleType => new FloatEncoder(target, sourceIndex),
            TdsDataType.DecimalN or TdsDataType.NumericN when type is Decimal128Type d && d.Scale <= target.Scale =>
                new DecimalEncoder(target, sourceIndex, d.Scale),
            TdsDataType.NVarChar or TdsDataType.NChar when text => new StringEncoder(target, sourceIndex, null),
            TdsDataType.BigVarChar or TdsDataType.BigChar when text => new StringEncoder(target, sourceIndex, target.Collation),
            TdsDataType.Vector when IsFloatList(type, target.VectorDimensions) => new VectorEncoder(target, sourceIndex),
            TdsDataType.Variant when type is VariantType => new VariantEncoder(target, sourceIndex, defaultCollation ?? []),
            // xml and hierarchyid targets, sent as varbinary: UTF-8 text as is, and hierarchyid's text form serialized.
            TdsDataType.BigVarBinary when type is StringType or Utf16StringType && target.TableType == TdsDataType.Xml => new BinaryEncoder(target, sourceIndex),
            TdsDataType.BigVarBinary when type is StringType or Utf16StringType && DecoderFactory.IsHierarchyId(target) => new HierarchyIdEncoder(target, sourceIndex),
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

    private static ColumnEncoder CreateInteger(TdsColumn target, int sourceIndex, IArrowType type) => type.TypeId switch
    {
        ArrowTypeId.Int8 => new IntegerEncoder<sbyte>(target, sourceIndex),
        ArrowTypeId.Int16 => new IntegerEncoder<short>(target, sourceIndex),
        ArrowTypeId.Int32 => new IntegerEncoder<int>(target, sourceIndex),
        ArrowTypeId.Int64 => new IntegerEncoder<long>(target, sourceIndex),
        ArrowTypeId.UInt8 => new IntegerEncoder<byte>(target, sourceIndex),
        ArrowTypeId.UInt16 => new IntegerEncoder<ushort>(target, sourceIndex),
        ArrowTypeId.UInt32 => new IntegerEncoder<uint>(target, sourceIndex),
        _ => new IntegerEncoder<ulong>(target, sourceIndex),
    };

    private static bool IsFloatList(IArrowType type, int dimensions) => type switch
    {
        FixedSizeListType f => f.ValueDataType is FloatType && f.ListSize == dimensions,
        ListType l => l.ValueDataType is FloatType,
        _ => false,
    };

    private static bool IsInteger(IArrowType type) => type.TypeId is
        ArrowTypeId.Int8 or ArrowTypeId.Int16 or ArrowTypeId.Int32 or ArrowTypeId.Int64 or
        ArrowTypeId.UInt8 or ArrowTypeId.UInt16 or ArrowTypeId.UInt32 or ArrowTypeId.UInt64;

    private static string Describe(IArrowType type) => type is ExtensionType e ? e.Name : type.Name;
}

/// <summary>
/// An Arrow integer type into tinyint, smallint, int or bigint. Generic over
/// the source type, so the JIT (and NativeAOT) compile a read of exactly that
/// width for each.
/// </summary>
internal sealed unsafe class IntegerEncoder<T> : ColumnEncoder where T : unmanaged
{
    private readonly int _size;
    private T* _values;

    public IntegerEncoder(TdsColumn target, int sourceIndex) : base(target, sourceIndex)
    {
        _size = target.MaxLength;
    }

    public override void Bind(IArrowArray array)
    {
        BindNulls(array);
        _values = (T*)Pin(array.Data.Buffers[1]) + array.Offset;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long Read(int row)
    {
        T value = _values[row];
        if (typeof(T) == typeof(sbyte)) return Unsafe.As<T, sbyte>(ref value);
        if (typeof(T) == typeof(short)) return Unsafe.As<T, short>(ref value);
        if (typeof(T) == typeof(int)) return Unsafe.As<T, int>(ref value);
        if (typeof(T) == typeof(long)) return Unsafe.As<T, long>(ref value);
        if (typeof(T) == typeof(byte)) return Unsafe.As<T, byte>(ref value);
        if (typeof(T) == typeof(ushort)) return Unsafe.As<T, ushort>(ref value);
        if (typeof(T) == typeof(uint)) return Unsafe.As<T, uint>(ref value);
        ulong unsigned = Unsafe.As<T, ulong>(ref value);
        return unsigned > long.MaxValue ? throw Overflow(row) : (long)unsigned;
    }

    public override void Write(PacketWriter writer, int row)
    {
        if (IsNullAt(row))
        {
            writer.WriteByte(0);
            return;
        }

        long value = Read(row);
        Span<byte> wire = writer.GetSpan(9);
        wire[0] = (byte)_size;
        switch (_size)
        {
            case 1:
                if ((ulong)value > byte.MaxValue) throw Overflow(row);
                wire[1] = (byte)value;
                break;
            case 2:
                if (value is < short.MinValue or > short.MaxValue) throw Overflow(row);
                BinaryPrimitives.WriteInt16LittleEndian(wire.Slice(1), (short)value);
                break;
            case 4:
                if (value is < int.MinValue or > int.MaxValue) throw Overflow(row);
                BinaryPrimitives.WriteInt32LittleEndian(wire.Slice(1), (int)value);
                break;
            default:
                BinaryPrimitives.WriteInt64LittleEndian(wire.Slice(1), value);
                break;
        }
        writer.Advance(1 + _size);
    }
}

internal sealed unsafe class BooleanEncoder : ColumnEncoder
{
    private byte* _values;
    private int _offset;

    public BooleanEncoder(TdsColumn target, int sourceIndex) : base(target, sourceIndex)
    {
    }

    public override void Bind(IArrowArray array)
    {
        BindNulls(array);
        _values = Pin(array.Data.Buffers[1]);
        _offset = array.Offset;
    }

    public override void Write(PacketWriter writer, int row)
    {
        if (IsNullAt(row))
        {
            writer.WriteByte(0);
            return;
        }
        int i = _offset + row;
        Span<byte> wire = writer.GetSpan(2);
        wire[0] = 1;
        wire[1] = (byte)((_values[i >> 3] >> (i & 7)) & 1);
        writer.Advance(2);
    }
}

internal sealed unsafe class FloatEncoder : ColumnEncoder
{
    private readonly int _size;
    private byte* _values;
    private bool _single;

    public FloatEncoder(TdsColumn target, int sourceIndex) : base(target, sourceIndex)
    {
        _size = target.MaxLength;
    }

    public override void Bind(IArrowArray array)
    {
        BindNulls(array);
        _single = array is FloatArray;
        _values = Pin(array.Data.Buffers[1]) + array.Offset * (_single ? 4 : 8);
    }

    public override void Write(PacketWriter writer, int row)
    {
        if (IsNullAt(row))
        {
            writer.WriteByte(0);
            return;
        }

        double value = _single ? ((float*)_values)[row] : ((double*)_values)[row];
        Span<byte> wire = writer.GetSpan(9);
        wire[0] = (byte)_size;
        if (_size == 4)
        {
            BinaryPrimitives.WriteInt32LittleEndian(wire.Slice(1), BitConverter.SingleToInt32Bits((float)value));
        }
        else
        {
            BinaryPrimitives.WriteInt64LittleEndian(wire.Slice(1), BitConverter.DoubleToInt64Bits(value));
        }
        writer.Advance(1 + _size);
    }
}

/// <summary>decimal128 into decimal/numeric, rescaling up to the target scale.</summary>
internal sealed unsafe class DecimalEncoder : ColumnEncoder
{
    private static readonly Int128 MaxMagnitude = Int128.Parse("99999999999999999999999999999999999999");

    private readonly int _length;
    private readonly Int128 _scaleFactor;
    private readonly bool _rescale;
    /// <summary>The largest source magnitude that still fits the target's precision after rescaling.</summary>
    private readonly Int128 _limit;
    private Int128* _values;

    public DecimalEncoder(TdsColumn target, int sourceIndex, int sourceScale) : base(target, sourceIndex)
    {
        _length = target.MaxLength;
        _scaleFactor = Pow10(target.Scale - sourceScale);
        _rescale = target.Scale != sourceScale;
        Int128 maxForPrecision = target.Precision >= 38 ? MaxMagnitude : Pow10(target.Precision) - 1;
        _limit = maxForPrecision / _scaleFactor;
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

    public override void Bind(IArrowArray array)
    {
        BindNulls(array);
        _values = (Int128*)Pin(array.Data.Buffers[1]) + array.Offset;
    }

    public override void Write(PacketWriter writer, int row)
    {
        if (IsNullAt(row))
        {
            writer.WriteByte(0);
            return;
        }

        Int128 value = Unsafe.ReadUnaligned<Int128>(_values + row);
        bool negative = value < 0;
        Int128 magnitude = negative ? -value : value;
        if (magnitude > _limit)
        {
            throw Overflow(row);
        }
        if (_rescale)
        {
            magnitude *= _scaleFactor;
        }

        Span<byte> wire = writer.GetSpan(1 + _length);
        wire[0] = (byte)_length;
        wire[1] = negative ? (byte)0 : (byte)1;
        new ReadOnlySpan<byte>(&magnitude, _length - 1).CopyTo(wire.Slice(2));
        writer.Advance(1 + _length);
    }
}

/// <summary>
/// utf8 or clast.utf16 into nvarchar/nchar (UTF-16) or varchar/char (UTF-8
/// collations as UTF-8; others in the code page). Each source passes through
/// to the target of its own encoding.
/// </summary>
internal sealed unsafe class StringEncoder : ColumnEncoder
{
    private readonly bool _plp;
    private readonly bool _utf16;
    private readonly Encoding? _codePage;
    private bool _sourceUtf16;
    private int* _offsets;
    private byte* _data;
    private char[] _chars = new char[256];
    private byte[] _bytes = new byte[256];

    /// <param name="collation">The target's collation for varchar/char; null for nvarchar/nchar (UTF-16).</param>
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

    public override void Bind(IArrowArray array)
    {
        BinaryArray strings = array switch
        {
            JsonArray json => json.StorageArray,
            Utf16StringArray utf16 => utf16.StorageArray,
            _ => (BinaryArray)array,
        };
        _sourceUtf16 = array is Utf16StringArray;
        BindNulls(strings);
        _offsets = (int*)Pin(strings.Data.Buffers[1]) + strings.Offset;
        _data = Pin(strings.Data.Buffers[2]);
    }

    public override void Write(PacketWriter writer, int row)
    {
        if (IsNullAt(row))
        {
            WriteNull(writer, _plp);
            return;
        }

        int start = _offsets[row];
        var utf8 = new ReadOnlySpan<byte>(_data + start, _offsets[row + 1] - start);
        if (_sourceUtf16)
        {
            WriteUtf16(writer, utf8, row);
        }
        else if (_utf16)
        {
            if (!_plp)
            {
                // Transcode straight into the packet: UTF-16 takes at most two bytes per UTF-8 byte.
                Span<byte> wire = writer.GetSpan(2 + 2 * utf8.Length);
                Utf8.ToUtf16(utf8, MemoryMarshal.Cast<byte, char>(wire.Slice(2)), out _, out int chars, replaceInvalidSequences: true);
                BinaryPrimitives.WriteUInt16LittleEndian(wire, checked((ushort)(chars * 2)));
                writer.Advance(2 + chars * 2);
                return;
            }
            WriteValue(writer, MemoryMarshal.AsBytes(ToUtf16(utf8)), plp: true);
        }
        else if (_codePage == null || Ascii.IsValid(utf8))
        {
            WriteValue(writer, utf8, _plp);
        }
        else
        {
            WriteCodePage(writer, ToUtf16(utf8));
        }
    }

    /// <summary>A clast.utf16 value: as is into nvarchar, else transcoded (unpaired surrogates become U+FFFD or the code page's default).</summary>
    private void WriteUtf16(PacketWriter writer, ReadOnlySpan<byte> utf16, int row)
    {
        if ((utf16.Length & 1) != 0)
        {
            throw new AdbcException(
                $"Value in row {row} for column '{Target.Name}' is not UTF-16: it has an odd number of bytes",
                AdbcStatusCode.InvalidData);
        }
        if (_utf16)
        {
            WriteValue(writer, utf16, _plp);
            return;
        }
        ReadOnlySpan<char> chars = MemoryMarshal.Cast<byte, char>(utf16);
        if (_codePage != null)
        {
            WriteCodePage(writer, chars);
            return;
        }
        // UTF-8 takes at most three bytes per UTF-16 code unit.
        EnsureBytes(chars.Length * 3);
        Utf8.FromUtf16(chars, _bytes, out _, out int written, replaceInvalidSequences: true);
        WriteValue(writer, _bytes.AsSpan(0, written), _plp);
    }

    private void WriteCodePage(PacketWriter writer, ReadOnlySpan<char> chars)
    {
        Encoding codePage = _codePage!;
        EnsureBytes(codePage.GetByteCount(chars));
        WriteValue(writer, _bytes.AsSpan(0, codePage.GetBytes(chars, _bytes)), _plp);
    }

    private void EnsureBytes(int count)
    {
        if (_bytes.Length < count)
        {
            _bytes = new byte[Math.Max(count, _bytes.Length * 2)];
        }
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

    internal static void WriteNull(PacketWriter writer, bool plp)
    {
        if (plp)
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
            Span<byte> wire = writer.GetSpan(2 + payload.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(wire, checked((ushort)payload.Length));
            payload.CopyTo(wire.Slice(2));
            writer.Advance(2 + payload.Length);
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

/// <summary>Binary values, and text into xml (sent as varbinary): UTF-8 as is, clast.utf16 behind a byte order mark.</summary>
internal sealed unsafe class BinaryEncoder : ColumnEncoder
{
    private readonly bool _plp;
    private int* _offsets;
    private byte* _data;
    private bool _utf16;
    private byte[] _marked = [];

    public BinaryEncoder(TdsColumn target, int sourceIndex) : base(target, sourceIndex)
    {
        _plp = target.LengthKind == TdsLengthKind.Plp;
    }

    public override void Bind(IArrowArray array)
    {
        _utf16 = array is Utf16StringArray;
        if (array is Utf16StringArray utf16)
        {
            array = utf16.StorageArray;
        }
        BindNulls(array);
        _offsets = (int*)Pin(array.Data.Buffers[1]) + array.Offset;
        _data = Pin(array.Data.Buffers[2]);
    }

    public override void Write(PacketWriter writer, int row)
    {
        if (IsNullAt(row))
        {
            StringEncoder.WriteNull(writer, _plp);
            return;
        }
        int start = _offsets[row];
        var value = new ReadOnlySpan<byte>(_data + start, _offsets[row + 1] - start);
        if (_utf16)
        {
            // The server reads xml bytes as UTF-8 unless they start with a UTF-16 byte order mark.
            if (_marked.Length < value.Length + 2)
            {
                _marked = new byte[Math.Max(value.Length + 2, _marked.Length * 2)];
            }
            _marked[0] = 0xFF;
            _marked[1] = 0xFE;
            value.CopyTo(_marked.AsSpan(2));
            value = _marked.AsSpan(0, value.Length + 2);
        }
        StringEncoder.WriteValue(writer, value, _plp);
    }
}

/// <summary>Text (<c>/1/2.5/</c>) into hierarchyid, sent as its serialized bytes.</summary>
internal sealed unsafe class HierarchyIdEncoder : ColumnEncoder
{
    private readonly bool _plp;
    private readonly byte[] _bytes = new byte[HierarchyId.MaxBytes];
    private int* _offsets;
    private byte* _data;
    private bool _utf16;
    private byte[] _text = [];

    public HierarchyIdEncoder(TdsColumn target, int sourceIndex) : base(target, sourceIndex)
    {
        _plp = target.LengthKind == TdsLengthKind.Plp;
    }

    public override void Bind(IArrowArray array)
    {
        _utf16 = array is Utf16StringArray;
        if (array is Utf16StringArray utf16)
        {
            array = utf16.StorageArray;
        }
        BindNulls(array);
        _offsets = (int*)Pin(array.Data.Buffers[1]) + array.Offset;
        _data = Pin(array.Data.Buffers[2]);
    }

    public override void Write(PacketWriter writer, int row)
    {
        if (IsNullAt(row))
        {
            StringEncoder.WriteNull(writer, _plp);
            return;
        }
        int start = _offsets[row];
        var text = new ReadOnlySpan<byte>(_data + start, _offsets[row + 1] - start);
        if (_utf16)
        {
            text = Narrow(text);
        }
        if (!HierarchyId.TryParse(text, _bytes, out int length, out string? error))
        {
            throw new AdbcException(
                $"Value in row {row} is not a hierarchyid for column '{Target.Name}': {error}",
                AdbcStatusCode.InvalidData);
        }
        StringEncoder.WriteValue(writer, _bytes.AsSpan(0, length), _plp);
    }

    /// <summary>UTF-16 text as ASCII bytes; anything else becomes 0xFF, which doesn't parse.</summary>
    private ReadOnlySpan<byte> Narrow(ReadOnlySpan<byte> utf16)
    {
        ReadOnlySpan<char> chars = MemoryMarshal.Cast<byte, char>(utf16);
        if (_text.Length < chars.Length)
        {
            _text = new byte[Math.Max(chars.Length, _text.Length * 2)];
        }
        for (int i = 0; i < chars.Length; i++)
        {
            _text[i] = chars[i] < 0x80 ? (byte)chars[i] : (byte)0xFF;
        }
        return _text.AsSpan(0, chars.Length);
    }
}

/// <summary>arrow.uuid (RFC 4122 byte order) into uniqueidentifier (mixed-endian).</summary>
internal sealed unsafe class GuidEncoder : ColumnEncoder
{
    private byte* _values;

    public GuidEncoder(TdsColumn target, int sourceIndex) : base(target, sourceIndex)
    {
    }

    public override void Bind(IArrowArray array)
    {
        FixedSizeBinaryArray storage = array is GuidArray g ? g.StorageArray : (FixedSizeBinaryArray)array;
        BindNulls(storage);
        _values = Pin(storage.Data.Buffers[1]) + storage.Offset * 16;
    }

    public override void Write(PacketWriter writer, int row)
    {
        if (IsNullAt(row))
        {
            writer.WriteByte(0);
            return;
        }

        byte* s = _values + row * 16;
        Span<byte> wire = writer.GetSpan(17);
        wire[0] = 16;
        wire[1] = s[3];
        wire[2] = s[2];
        wire[3] = s[1];
        wire[4] = s[0];
        wire[5] = s[5];
        wire[6] = s[4];
        wire[7] = s[7];
        wire[8] = s[6];
        new ReadOnlySpan<byte>(s + 8, 8).CopyTo(wire.Slice(9));
        writer.Advance(17);
    }
}

/// <summary>
/// Arrow dates, timestamps and times into date, datetime2, datetime,
/// datetimeoffset and time.
/// </summary>
internal sealed unsafe class TemporalEncoder : ColumnEncoder
{
    private const long DaysTo1970From0001 = 719162;
    private const long DaysTo1970From1900 = 25567;
    private const long TicksPerDay = 864_000_000_000L; // 100 ns

    /// <summary>The source's representation, resolved at <see cref="Bind"/>.</summary>
    private enum Source
    {
        Days32,     // date32
        Millis64,   // date64
        Value32,    // time32: value * _multiplier
        Value64,    // time64, timestamp: value * _multiplier, or / 100 for nanoseconds
        Nanos64,
    }

    private readonly TdsDataType _targetType;
    private readonly int _timeLength;
    private readonly long _ticksPerUnit; // 100-ns ticks per 10^-scale unit
    private Source _source;
    private long _multiplier;
    private byte* _values;
    private short* _offsets;

    public TemporalEncoder(TdsColumn target, int sourceIndex, IArrowType sourceType) : base(target, sourceIndex)
    {
        _targetType = target.Type;
        int scale = target.Scale;
        _timeLength = scale <= 2 ? 3 : scale <= 4 ? 4 : 5;
        long ticksPerUnit = 1;
        for (int i = scale; i < 7; i++)
        {
            ticksPerUnit *= 10;
        }
        _ticksPerUnit = ticksPerUnit;
    }

    public override void Bind(IArrowArray array)
    {
        BindNulls(array);
        _offsets = null;
        IArrowArray values = array;
        if (array is TimestampWithOffsetArray withOffset)
        {
            var storage = (StructArray)withOffset.Storage;
            values = storage.Fields[0];
            var offsets = (Int16Array)storage.Fields[1];
            _offsets = (short*)Pin(offsets.Data.Buffers[1]) + offsets.Offset;
        }

        switch (values)
        {
            case Date32Array:
                _source = Source.Days32;
                break;
            case Date64Array:
                _source = Source.Millis64;
                break;
            case Time32Array t:
                _source = Source.Value32;
                _multiplier = ((Time32Type)t.Data.DataType).Unit == TimeUnit.Second ? 10_000_000L : 10_000L;
                break;
            case Time64Array t:
                _source = ((Time64Type)t.Data.DataType).Unit == TimeUnit.Nanosecond ? Source.Nanos64 : Source.Value64;
                _multiplier = 10;
                break;
            default:
                TimeUnit unit = ((TimestampType)values.Data.DataType).Unit;
                _source = unit == TimeUnit.Nanosecond ? Source.Nanos64 : Source.Value64;
                _multiplier = unit switch
                {
                    TimeUnit.Second => 10_000_000L,
                    TimeUnit.Millisecond => 10_000L,
                    _ => 10L,
                };
                break;
        }
        int width = _source is Source.Days32 or Source.Value32 ? 4 : 8;
        _values = Pin(values.Data.Buffers[1]) + values.Offset * width;
    }

    /// <summary>Source value as 100-ns ticks since 1970-01-01 (UTC for zoned values).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long GetTicks(int row) => _source switch
    {
        Source.Days32 => ((int*)_values)[row] * TicksPerDay,
        Source.Millis64 => ((long*)_values)[row] * 10_000L,
        Source.Value32 => ((int*)_values)[row] * _multiplier,
        Source.Nanos64 => ((long*)_values)[row] / 100,
        _ => checked(((long*)_values)[row] * _multiplier),
    };

    public override void Write(PacketWriter writer, int row)
    {
        if (IsNullAt(row))
        {
            writer.WriteByte(0);
            return;
        }

        long ticks = GetTicks(row);
        Span<byte> wire = writer.GetSpan(11);
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
                WriteTime(wire.Slice(1), ToUnits(ticks));
                length = _timeLength;
                break;
            }
            case TdsDataType.DateTime2N:
            case TdsDataType.DateTimeOffsetN:
            {
                long days = FloorDiv(ticks, TicksPerDay);
                long timeOfDay = ticks - days * TicksPerDay;
                WriteTime(wire.Slice(1), ToUnits(timeOfDay));
                WriteUInt24(wire.Slice(1 + _timeLength), CheckDays(days + DaysTo1970From0001, row));
                length = _timeLength + 3;
                if (_targetType == TdsDataType.DateTimeOffsetN)
                {
                    short offset = _offsets != null ? _offsets[row] : (short)0;
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
        writer.Advance(length + 1);
    }

    /// <summary>Ticks in the target's units (10^-scale s); scale 7 needs no division.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long ToUnits(long ticks) => _ticksPerUnit == 1 ? ticks : ticks / _ticksPerUnit;

    private int CheckDays(long days, int row)
    {
        if (days is < 0 or > 3652058)
        {
            throw Overflow(row);
        }
        return (int)days;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
