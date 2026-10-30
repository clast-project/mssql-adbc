// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;
using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Types;
using Clast.Drivers.SqlServer.Tds;

namespace Clast.Drivers.SqlServer.Arrow;

/// <summary>
/// Fixed-width little-endian values that map bit-for-bit onto an Arrow
/// primitive type: the integer and floating-point types.
/// </summary>
internal sealed class PrimitiveDecoder<T> : ColumnDecoder where T : unmanaged
{
    private readonly bool _lengthPrefixed;
    private ValueBuffer _values;
    private ValidityBuffer _validity;

    public PrimitiveDecoder(TdsColumn column, IArrowType arrowType, int capacity)
        : base(column, arrowType)
    {
        _lengthPrefixed = column.LengthKind == TdsLengthKind.ByteLength;
        _values = new ValueBuffer(capacity * Unsafe.SizeOf<T>());
    }

    public override void ReadValue(PacketReader reader)
    {
        if (_lengthPrefixed)
        {
            int length = reader.ReadByte();
            if (length == 0)
            {
                AppendNull();
                return;
            }
            if (length != Unsafe.SizeOf<T>())
            {
                throw new TdsProtocolException($"Unexpected length {length} for column '{Column.Name}'");
            }
        }
        _values.Append(MemoryMarshal.Read<T>(reader.ReadSpan(Unsafe.SizeOf<T>())));
        _validity.AppendValid();
    }

    public override void AppendNull()
    {
        _values.Append(default(T));
        _validity.AppendNull();
    }

    public override void Dispose()
    {
        _values.Dispose();
    }

    public override IArrowArray Build()
    {
        int length = _validity.Length;
        int nullCount = _validity.NullCount;
        return BuildArray(ArrowType, length, nullCount, _validity.Build(), _values.Build());
    }
}

/// <summary>bit: one byte per value on the wire, one bit per value in Arrow.</summary>
internal sealed class BooleanDecoder : ColumnDecoder
{
    private readonly bool _lengthPrefixed;
    private byte[] _bits;
    private ValidityBuffer _validity;
    private readonly int _capacity;

    public BooleanDecoder(TdsColumn column, int capacity)
        : base(column, BooleanType.Default)
    {
        _lengthPrefixed = column.LengthKind == TdsLengthKind.ByteLength;
        _capacity = capacity;
        _bits = new byte[(capacity + 7) / 8];
    }

    public override void ReadValue(PacketReader reader)
    {
        if (_lengthPrefixed && reader.ReadByte() == 0)
        {
            AppendNull();
            return;
        }
        int index = _validity.Length;
        EnsureCapacity(index);
        if (reader.ReadByte() != 0)
        {
            _bits[index >> 3] |= (byte)(1 << (index & 7));
        }
        _validity.AppendValid();
    }

    public override void AppendNull()
    {
        EnsureCapacity(_validity.Length);
        _validity.AppendNull();
    }

    private void EnsureCapacity(int index)
    {
        if ((index >> 3) >= _bits.Length)
        {
            System.Array.Resize(ref _bits, _bits.Length * 2);
        }
    }

    public override IArrowArray Build()
    {
        int length = _validity.Length;
        int nullCount = _validity.NullCount;
        var values = new ArrowBuffer(_bits.AsMemory(0, (length + 7) / 8));
        _bits = new byte[(_capacity + 7) / 8];
        return BuildArray(ArrowType, length, nullCount, _validity.Build(), values);
    }
}

/// <summary>decimal and numeric: sign byte plus little-endian magnitude, to decimal128.</summary>
internal sealed class DecimalDecoder : ColumnDecoder
{
    private ValueBuffer _values;
    private ValidityBuffer _validity;

    public DecimalDecoder(TdsColumn column, int capacity)
        : base(column, new Decimal128Type(column.Precision, column.Scale))
    {
        _values = new ValueBuffer(capacity * 16);
    }

    public override void ReadValue(PacketReader reader)
    {
        int length = reader.ReadByte();
        if (length == 0)
        {
            AppendNull();
            return;
        }

        ReadOnlySpan<byte> bytes = reader.ReadSpan(length);
        bool positive = bytes[0] != 0;
        ReadOnlySpan<byte> magnitude = bytes.Slice(1);

        UInt128 value;
        if (magnitude.Length == 16)
        {
            value = BinaryPrimitives.ReadUInt128LittleEndian(magnitude);
        }
        else
        {
            Span<byte> padded = stackalloc byte[16];
            padded.Clear();
            magnitude.CopyTo(padded);
            value = BinaryPrimitives.ReadUInt128LittleEndian(padded);
        }

        Int128 signed = positive ? (Int128)value : -(Int128)value;
        _values.Append(signed);
        _validity.AppendValid();
    }

    public override void AppendNull()
    {
        _values.Append(default(Int128));
        _validity.AppendNull();
    }

    public override void Dispose()
    {
        _values.Dispose();
    }

    public override IArrowArray Build()
    {
        int length = _validity.Length;
        int nullCount = _validity.NullCount;
        return BuildArray(ArrowType, length, nullCount, _validity.Build(), _values.Build());
    }
}

/// <summary>money and smallmoney: scaled integers, to decimal128(19,4) or (10,4).</summary>
internal sealed class MoneyDecoder : ColumnDecoder
{
    private readonly bool _lengthPrefixed;
    private readonly int _fixedSize;
    private ValueBuffer _values;
    private ValidityBuffer _validity;

    public MoneyDecoder(TdsColumn column, int capacity)
        : base(column, new Decimal128Type(column.MaxLength == 4 ? 10 : 19, 4))
    {
        _lengthPrefixed = column.LengthKind == TdsLengthKind.ByteLength;
        _fixedSize = column.MaxLength;
        _values = new ValueBuffer(capacity * 16);
    }

    public override void ReadValue(PacketReader reader)
    {
        int length = _fixedSize;
        if (_lengthPrefixed)
        {
            length = reader.ReadByte();
            if (length == 0)
            {
                AppendNull();
                return;
            }
        }

        long value;
        if (length == 4)
        {
            value = reader.ReadInt32();
        }
        else
        {
            long high = reader.ReadInt32();
            uint low = reader.ReadUInt32();
            value = (high << 32) | low;
        }
        _values.Append((Int128)value);
        _validity.AppendValid();
    }

    public override void AppendNull()
    {
        _values.Append(default(Int128));
        _validity.AppendNull();
    }

    public override void Dispose()
    {
        _values.Dispose();
    }

    public override IArrowArray Build()
    {
        int length = _validity.Length;
        int nullCount = _validity.NullCount;
        return BuildArray(ArrowType, length, nullCount, _validity.Build(), _values.Build());
    }
}

/// <summary>
/// Variable-length strings and binary, to Arrow utf8 or binary (or an
/// extension type stored as one, such as <c>arrow.json</c>). Handles
/// two-byte length-prefixed values, PLP (<c>max</c>, json, CLR types) values
/// and the legacy text/ntext/image framing.
/// </summary>
internal sealed class VariableDecoder : ColumnDecoder
{
    private enum Encoding
    {
        Binary,
        Utf16,
        Utf8,
        CodePage,
        HierarchyId,
    }

    private readonly Encoding _encoding;
    /// <summary>Whether text is written as UTF-16 (<see cref="Utf16StringType"/>) rather than UTF-8.</summary>
    private readonly bool _utf16;
    private readonly System.Text.Encoding? _codePage;
    private readonly bool _plp;
    private readonly bool _text;
    private ValueBuffer _offsets;
    private ValueBuffer _data;
    private ValidityBuffer _validity;
    private byte[] _scratch = [];
    private char[] _chars = [];
    private byte[] _hierarchyIdText = [];

    /// <param name="hierarchyIdText">For a hierarchyid column: decode to its text form rather than keep its serialized bytes.</param>
    public VariableDecoder(TdsColumn column, IArrowType arrowType, int capacity, byte[] defaultCollation, bool hierarchyIdText = false)
        : base(column, arrowType)
    {
        _utf16 = arrowType is Utf16StringType;
        _plp = column.LengthKind == TdsLengthKind.Plp;
        _text = column.LengthKind == TdsLengthKind.Text;
        _encoding = column.Type switch
        {
            TdsDataType.NVarChar or TdsDataType.NChar or TdsDataType.Xml or TdsDataType.NText => Encoding.Utf16,
            TdsDataType.BigVarChar or TdsDataType.BigChar or TdsDataType.Text => Encoding.CodePage,
            TdsDataType.Json => Encoding.Utf8,
            _ => hierarchyIdText ? Encoding.HierarchyId : Encoding.Binary,
        };

        if (_encoding == Encoding.CodePage)
        {
            TdsCollation collation = new TdsCollation(column.Collation.Length == 5 ? column.Collation : defaultCollation);
            if (collation.IsUtf8)
            {
                _encoding = Encoding.Utf8;
            }
            else
            {
                _codePage = collation.GetEncoding();
                if (_codePage.CodePage == 65001)
                {
                    _encoding = Encoding.Utf8;
                }
            }
        }

        _offsets = new ValueBuffer((capacity + 1) * 4);
        int estimate = column.MaxLength is > 0 and < 0xFFFF ? Math.Min(column.MaxLength, 64) : 64;
        _data = new ValueBuffer(capacity * estimate);
    }

    public override void ReadValue(PacketReader reader)
    {
        if (_plp)
        {
            ReadPlpValue(reader);
            return;
        }
        if (_text)
        {
            ReadTextValue(reader);
            return;
        }

        int length = reader.ReadUInt16();
        if (length == 0xFFFF)
        {
            AppendNull();
            return;
        }
        Append(reader.ReadSpan(length));
    }

    private void ReadPlpValue(PacketReader reader)
    {
        ulong total = reader.ReadUInt64();
        if (total == TdsColumn.PlpNull)
        {
            AppendNull();
            return;
        }

        // Gather the chunks; a UTF-16 code unit may be split across chunks.
        int length = 0;
        if (total != TdsColumn.PlpUnknownLength)
        {
            EnsureScratch(checked((int)total));
        }
        uint chunk;
        while ((chunk = reader.ReadUInt32()) != 0)
        {
            int chunkLength = checked((int)chunk);
            EnsureScratch(length + chunkLength);
            reader.ReadBytes(_scratch.AsSpan(length, chunkLength));
            length += chunkLength;
        }
        Append(_scratch.AsSpan(0, length));
    }

    /// <summary>text, ntext and image: text pointer (absent for NULL), timestamp, four-byte length, data.</summary>
    private void ReadTextValue(PacketReader reader)
    {
        int pointerLength = reader.ReadByte();
        if (pointerLength == 0)
        {
            AppendNull();
            return;
        }
        reader.Skip(pointerLength + 8);
        int length = reader.ReadInt32();
        EnsureScratch(length);
        reader.ReadBytes(_scratch.AsSpan(0, length));
        Append(_scratch.AsSpan(0, length));
    }

    private void EnsureScratch(int length)
    {
        if (_scratch.Length < length)
        {
            System.Array.Resize(ref _scratch, Math.Max(length, _scratch.Length * 2));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Append(ReadOnlySpan<byte> bytes)
    {
        switch (_encoding)
        {
            case Encoding.Binary:
                _data.Append(bytes);
                break;
            case Encoding.Utf8:
                if (_utf16)
                {
                    AppendUtf8AsUtf16(bytes);
                }
                else
                {
                    _data.Append(bytes);
                }
                break;
            case Encoding.Utf16:
                if (_utf16)
                {
                    _data.Append(bytes);
                }
                else
                {
                    AppendUtf16(bytes);
                }
                break;
            case Encoding.CodePage:
                AppendCodePage(bytes);
                break;
            case Encoding.HierarchyId:
                AppendHierarchyId(bytes);
                break;
        }
        AppendOffset();
        _validity.AppendValid();
    }

    private void AppendHierarchyId(ReadOnlySpan<byte> bytes)
    {
        int maxLength = HierarchyId.MaxTextLength(bytes.Length);
        if (!_utf16)
        {
            if (!HierarchyId.TryFormat(bytes, _data.GetSpan(maxLength), out int written))
            {
                throw InvalidHierarchyId();
            }
            _data.Advance(written);
            return;
        }

        // The text is ASCII: format it, then widen it.
        if (_hierarchyIdText.Length < maxLength)
        {
            _hierarchyIdText = new byte[Math.Max(maxLength, _hierarchyIdText.Length * 2)];
        }
        if (!HierarchyId.TryFormat(bytes, _hierarchyIdText, out int length))
        {
            throw InvalidHierarchyId();
        }
        Span<char> destination = MemoryMarshal.Cast<byte, char>(_data.GetSpan(length * 2));
        for (int i = 0; i < length; i++)
        {
            destination[i] = (char)_hierarchyIdText[i];
        }
        _data.Advance(length * 2);
    }

    private TdsProtocolException InvalidHierarchyId() =>
        new($"Column '{Column.Name}' holds a value that is not a valid hierarchyid");

    /// <summary>UTF-8 text (a UTF-8 collation's varchar) as UTF-16, which never takes more code units than the UTF-8 has bytes.</summary>
    private void AppendUtf8AsUtf16(ReadOnlySpan<byte> bytes)
    {
        Span<char> destination = MemoryMarshal.Cast<byte, char>(_data.GetSpan(bytes.Length * 2));
        Utf8.ToUtf16(bytes, destination, out _, out int written, replaceInvalidSequences: true);
        _data.Advance(written * 2);
    }

    private void AppendUtf16(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<char> chars = MemoryMarshal.Cast<byte, char>(bytes);
        Span<byte> destination = _data.GetSpan(chars.Length * 3);
        OperationStatus status = Utf8.FromUtf16(chars, destination, out _, out int written, replaceInvalidSequences: true);
        if (status != OperationStatus.Done)
        {
            throw new TdsProtocolException($"Could not transcode a value of column '{Column.Name}'");
        }
        _data.Advance(written);
    }

    private void AppendCodePage(ReadOnlySpan<byte> bytes)
    {
        if (_utf16)
        {
            Span<char> utf16 = MemoryMarshal.Cast<byte, char>(_data.GetSpan(_codePage!.GetMaxCharCount(bytes.Length) * 2));
            _data.Advance(_codePage.GetChars(bytes, utf16) * 2);
            return;
        }
        if (Ascii.IsValid(bytes))
        {
            _data.Append(bytes);
            return;
        }

        int charCount = _codePage!.GetCharCount(bytes);
        if (_chars.Length < charCount)
        {
            _chars = new char[Math.Max(charCount, _chars.Length * 2)];
        }
        int decoded = _codePage!.GetChars(bytes, _chars);
        ReadOnlySpan<char> chars = _chars.AsSpan(0, decoded);
        Span<byte> destination = _data.GetSpan(decoded * 3);
        Utf8.FromUtf16(chars, destination, out _, out int written, replaceInvalidSequences: true);
        _data.Advance(written);
    }

    public override void AppendNull()
    {
        AppendOffset();
        _validity.AppendNull();
    }

    /// <summary>Appends the end offset of the current value, starting the batch's offsets with 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AppendOffset()
    {
        if (_offsets.Length == 0)
        {
            _offsets.Append(0);
        }
        _offsets.Append(_data.Length);
    }

    public override void Dispose()
    {
        _offsets.Dispose();
        _data.Dispose();
    }

    public override IArrowArray Build()
    {
        int length = _validity.Length;
        int nullCount = _validity.NullCount;
        ArrowBuffer validity = _validity.Build();
        ArrowBuffer offsets = _offsets.Build();
        ArrowBuffer data = _data.Build();
        if (ArrowType is ExtensionType extension)
        {
            return extension.CreateArray(BuildArray(extension.StorageType, length, nullCount, validity, offsets, data));
        }
        return BuildArray(ArrowType, length, nullCount, validity, offsets, data);
    }
}

/// <summary>uniqueidentifier: mixed-endian on the wire, RFC 4122 order in <c>arrow.uuid</c>.</summary>
internal sealed class GuidDecoder : ColumnDecoder
{
    private ValueBuffer _values;
    private ValidityBuffer _validity;

    public GuidDecoder(TdsColumn column, int capacity)
        : base(column, GuidType.Default)
    {
        _values = new ValueBuffer(capacity * 16);
    }

    public override void ReadValue(PacketReader reader)
    {
        int length = reader.ReadByte();
        if (length == 0)
        {
            AppendNull();
            return;
        }
        if (length != 16)
        {
            throw new TdsProtocolException($"Unexpected length {length} for column '{Column.Name}'");
        }

        ReadOnlySpan<byte> source = reader.ReadSpan(16);
        Span<byte> target = _values.GetSpan(16);
        // First three fields are little-endian on the wire.
        target[0] = source[3];
        target[1] = source[2];
        target[2] = source[1];
        target[3] = source[0];
        target[4] = source[5];
        target[5] = source[4];
        target[6] = source[7];
        target[7] = source[6];
        source.Slice(8, 8).CopyTo(target.Slice(8));
        _values.Advance(16);
        _validity.AppendValid();
    }

    public override void AppendNull()
    {
        _values.AppendZeros(16);
        _validity.AppendNull();
    }

    public override void Dispose()
    {
        _values.Dispose();
    }

    public override IArrowArray Build()
    {
        int length = _validity.Length;
        int nullCount = _validity.NullCount;
        var storage = BuildArray(GuidType.Default.StorageType, length, nullCount, _validity.Build(), _values.Build());
        return new GuidArray(storage);
    }
}

/// <summary>
/// The temporal types: date, time, datetime2, datetimeoffset, datetime and
/// smalldatetime.
/// </summary>
internal sealed class TemporalDecoder : ColumnDecoder
{
    // Days from 0001-01-01 and from 1900-01-01 to 1970-01-01.
    internal const int DaysTo1970From0001 = 719162;
    private const int DaysTo1970From1900 = 25567;
    internal const long MicrosecondsPerDay = 86_400_000_000L;

    private readonly TdsDataType _type;
    private readonly bool _lengthPrefixed;
    private readonly int _fixedSize;
    private readonly int _scale;
    private readonly long _timeMultiplier;
    private readonly long _timeDivisor;
    private ValueBuffer _values;
    private ValueBuffer _offsets; // datetimeoffset only
    private ValidityBuffer _validity;

    public TemporalDecoder(TdsColumn column, IArrowType arrowType, int capacity)
        : base(column, arrowType)
    {
        _type = column.Type;
        _lengthPrefixed = column.LengthKind == TdsLengthKind.ByteLength;
        _fixedSize = column.FixedSize;
        _scale = column.Scale;

        // time stores units of 10^-scale seconds: convert to nanoseconds for
        // time, microseconds for datetime2 and datetimeoffset.
        int targetDigits = _type == TdsDataType.TimeN ? 9 : 6;
        if (_scale <= targetDigits)
        {
            _timeMultiplier = Pow10(targetDigits - _scale);
            _timeDivisor = 1;
        }
        else
        {
            _timeMultiplier = 1;
            _timeDivisor = Pow10(_scale - targetDigits);
        }

        int width = _type == TdsDataType.DateN ? 4 : 8;
        _values = new ValueBuffer(capacity * width);
        if (_type == TdsDataType.DateTimeOffsetN)
        {
            _offsets = new ValueBuffer(capacity * 2);
        }
    }

    internal static long Pow10(int exponent)
    {
        long result = 1;
        for (int i = 0; i < exponent; i++)
        {
            result *= 10;
        }
        return result;
    }

    public override void ReadValue(PacketReader reader)
    {
        int length = _fixedSize;
        if (_lengthPrefixed)
        {
            length = reader.ReadByte();
            if (length == 0)
            {
                AppendNull();
                return;
            }
        }

        ReadOnlySpan<byte> bytes = reader.ReadSpan(length);
        switch (_type)
        {
            case TdsDataType.DateN:
                _values.Append(ReadUInt24(bytes) - DaysTo1970From0001);
                break;

            case TdsDataType.TimeN:
                _values.Append(ReadTime(bytes, length) * _timeMultiplier / _timeDivisor);
                break;

            case TdsDataType.DateTime2N:
            {
                int timeLength = length - 3;
                long time = ReadTime(bytes, timeLength);
                long days = ReadUInt24(bytes.Slice(timeLength)) - DaysTo1970From0001;
                _values.Append(days * MicrosecondsPerDay + time * _timeMultiplier / _timeDivisor);
                break;
            }

            case TdsDataType.DateTimeOffsetN:
            {
                int timeLength = length - 5;
                long time = ReadTime(bytes, timeLength);
                long days = ReadUInt24(bytes.Slice(timeLength)) - DaysTo1970From0001;
                _values.Append(days * MicrosecondsPerDay + time * _timeMultiplier / _timeDivisor);
                _offsets.Append(BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(timeLength + 3)));
                break;
            }

            case TdsDataType.DateTime:
            case TdsDataType.DateTime4:
            case TdsDataType.DateTimeN:
                _values.Append(length == 8 ? ReadDateTime(bytes) : ReadSmallDateTime(bytes));
                break;
        }
        _validity.AppendValid();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int ReadUInt24(ReadOnlySpan<byte> bytes) => bytes[0] | (bytes[1] << 8) | (bytes[2] << 16);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long ReadTime(ReadOnlySpan<byte> bytes, int length)
    {
        long value = 0;
        for (int i = length - 1; i >= 0; i--)
        {
            value = (value << 8) | bytes[i];
        }
        return value;
    }

    /// <summary>datetime: days since 1900 and 1/300-second ticks, rounded to milliseconds as SQL Server does.</summary>
    internal static long ReadDateTime(ReadOnlySpan<byte> bytes)
    {
        long days = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        long ticks = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4));
        long milliseconds = (ticks * 20 + 3) / 6;
        return (days - DaysTo1970From1900) * MicrosecondsPerDay + milliseconds * 1000;
    }

    /// <summary>smalldatetime: days since 1900 and minutes since midnight.</summary>
    internal static long ReadSmallDateTime(ReadOnlySpan<byte> bytes)
    {
        long days = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        long minutes = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(2));
        return (days - DaysTo1970From1900) * MicrosecondsPerDay + minutes * 60_000_000L;
    }

    public override void AppendNull()
    {
        if (_type == TdsDataType.DateN)
        {
            _values.Append(0);
        }
        else
        {
            _values.Append(0L);
        }
        if (_type == TdsDataType.DateTimeOffsetN)
        {
            _offsets.Append((short)0);
        }
        _validity.AppendNull();
    }

    public override void Dispose()
    {
        _values.Dispose();
        _offsets.Dispose();
    }

    public override IArrowArray Build()
    {
        int length = _validity.Length;
        int nullCount = _validity.NullCount;
        ArrowBuffer validity = _validity.Build();

        if (_type != TdsDataType.DateTimeOffsetN)
        {
            return BuildArray(ArrowType, length, nullCount, validity, _values.Build());
        }

        var type = (TimestampWithOffsetType)ArrowType;
        var storageType = (StructType)type.StorageType;
        IArrowArray timestamps = BuildArray(storageType.Fields[0].DataType, length, 0, ArrowBuffer.Empty, _values.Build());
        IArrowArray offsets = BuildArray(storageType.Fields[1].DataType, length, 0, ArrowBuffer.Empty, _offsets.Build());
        var storage = new StructArray(storageType, length, [timestamps, offsets], validity, nullCount);
        return new TimestampWithOffsetArray(type, storage);
    }
}

/// <summary>
/// Collation as sent in TYPE_INFO and ENVCHANGE: LCID and flags, then sort ID.
/// </summary>
internal readonly struct TdsCollation
{
    private const uint Utf8Flag = 0x04000000;

    private readonly uint _info;
    private readonly byte _sortId;

    static TdsCollation()
    {
        // .NET Core has only the Unicode and Latin-1 code pages built in (on .NET Framework, this adds nothing).
        System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public TdsCollation(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 5)
        {
            _info = 0;
            _sortId = 0;
            return;
        }
        _info = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        _sortId = bytes[4];
    }

    public bool IsUtf8 => (_info & Utf8Flag) != 0;

    public int Lcid => (int)(_info & 0xFFFFF);

    public System.Text.Encoding GetEncoding()
    {
        if (!LocalesHelper.TryGetCodePage(Lcid, _sortId, out int codePage))
        {
            throw new AdbcException($"Unsupported collation (LCID {Lcid}, sort ID {_sortId})", AdbcStatusCode.NotImplemented);
        }
        return System.Text.Encoding.GetEncoding(codePage);
    }
}
