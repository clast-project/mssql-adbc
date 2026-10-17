// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;
using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Scalars.Variant;
using Apache.Arrow.Types;
using Clast.Drivers.SqlServer.Tds;

namespace Clast.Drivers.SqlServer.Arrow;

/// <summary>
/// vector (float32 elements), to <c>fixed_size_list&lt;float32&gt;[dimensions]</c>.
/// Each value is an 8-byte header followed by the little-endian floats.
/// </summary>
internal sealed class VectorDecoder : ColumnDecoder
{
    private const byte LayoutFormat = 0xA9;

    private readonly int _dimensions;
    private ValueBuffer _values;
    private ValidityBuffer _validity;

    public VectorDecoder(TdsColumn column, int capacity)
        : base(column, CreateType(column))
    {
        _dimensions = column.VectorDimensions;
        _values = new ValueBuffer(capacity * _dimensions * 4);
    }

    private static FixedSizeListType CreateType(TdsColumn column)
    {
        if (column.Scale != 0)
        {
            throw new AdbcException(
                $"Column '{column.Name}' is a vector with element type 0x{column.Scale:X2}; only float32 is supported",
                AdbcStatusCode.NotImplemented);
        }
        return new FixedSizeListType(new Field("item", FloatType.Default, false), column.VectorDimensions);
    }

    public override void ReadValue(PacketReader reader)
    {
        int length = reader.ReadUInt16();
        if (length == 0xFFFF)
        {
            AppendNull();
            return;
        }

        ReadOnlySpan<byte> header = reader.ReadSpan(TdsColumn.VectorHeaderLength);
        if (header[0] != LayoutFormat || BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(2)) != _dimensions || header[4] != 0 ||
            length != TdsColumn.VectorHeaderLength + _dimensions * 4)
        {
            throw new TdsProtocolException($"Unexpected vector header in column '{Column.Name}'");
        }
        _values.Append(reader.ReadSpan(_dimensions * 4));
        _validity.AppendValid();
    }

    public override void AppendNull()
    {
        _values.AppendZeros(_dimensions * 4);
        _validity.AppendNull();
    }

    public override void Dispose() => _values.Dispose();

    public override IArrowArray Build()
    {
        int length = _validity.Length;
        int nullCount = _validity.NullCount;
        ArrowBuffer validity = _validity.Build();
        IArrowArray floats = BuildArray(FloatType.Default, length * _dimensions, 0, ArrowBuffer.Empty, _values.Build());
        return new FixedSizeListArray(ArrowType, length, floats, validity, nullCount);
    }
}

/// <summary>
/// sql_variant, to <c>arrow.parquet.variant</c> (unshredded). Each value
/// carries its own base type and is encoded as the matching variant
/// primitive:
/// <list type="bullet">
/// <item>tinyint, smallint, int, bigint become int16, int16, int32, int64.</item>
/// <item>real and float become float and double; bit becomes boolean.</item>
/// <item>decimal and numeric become decimal4/8/16 by precision; smallmoney and
/// money become decimal8 and decimal16, with scale 4.</item>
/// <item>date becomes date; time becomes time (us).</item>
/// <item>datetime, smalldatetime and datetime2 become timestamp_ntz (us).</item>
/// <item>datetimeoffset becomes timestamp (us, UTC); its offset is not kept.</item>
/// <item>uniqueidentifier becomes uuid; character types become string;
/// binary types become binary.</item>
/// </list>
/// </summary>
internal sealed class VariantDecoder : ColumnDecoder
{
    /// <summary>Metadata for values without objects: version 1, an empty dictionary.</summary>
    private static readonly byte[] s_emptyMetadata = [0x01, 0x00, 0x00];

    private readonly byte[] _defaultCollation;
    private ValueBuffer _metadataOffsets;
    private ValueBuffer _metadata;
    private ValueBuffer _valueOffsets;
    private ValueBuffer _values;
    private ValidityBuffer _validity;
    private byte[] _utf8 = new byte[256];
    private char[] _chars = new char[128];
    private ulong _collationKey = ulong.MaxValue;
    private System.Text.Encoding? _collationEncoding;

    public VariantDecoder(TdsColumn column, int capacity, byte[] defaultCollation)
        : base(column, VariantType.Default)
    {
        _defaultCollation = defaultCollation;
        _metadataOffsets = new ValueBuffer((capacity + 1) * 4);
        _metadata = new ValueBuffer(capacity * s_emptyMetadata.Length);
        _valueOffsets = new ValueBuffer((capacity + 1) * 4);
        _values = new ValueBuffer(capacity * 10);
    }

    public override void ReadValue(PacketReader reader)
    {
        int total = reader.ReadInt32();
        if (total == 0)
        {
            AppendNull();
            return;
        }

        var baseType = (TdsDataType)reader.ReadByte();
        int propertyLength = reader.ReadByte();
        Span<byte> properties = stackalloc byte[8];
        if (propertyLength > properties.Length || total < 2 + propertyLength)
        {
            throw new TdsProtocolException($"Invalid sql_variant value in column '{Column.Name}'");
        }
        reader.ReadBytes(properties.Slice(0, propertyLength));
        ReadOnlySpan<byte> data = reader.ReadSpan(total - 2 - propertyLength);

        Encode(baseType, properties.Slice(0, propertyLength), data);
        EndValue();
        _validity.AppendValid();
    }

    private void Encode(TdsDataType baseType, ReadOnlySpan<byte> properties, ReadOnlySpan<byte> data)
    {
        switch (baseType)
        {
            case TdsDataType.Int1:
                Primitive(VariantPrimitiveType.Int16, (short)data[0]);
                break;
            case TdsDataType.Bit:
                Header(data[0] != 0 ? VariantPrimitiveType.BooleanTrue : VariantPrimitiveType.BooleanFalse);
                break;
            case TdsDataType.Int2:
                Primitive(VariantPrimitiveType.Int16, BinaryPrimitives.ReadInt16LittleEndian(data));
                break;
            case TdsDataType.Int4:
                Primitive(VariantPrimitiveType.Int32, BinaryPrimitives.ReadInt32LittleEndian(data));
                break;
            case TdsDataType.Int8:
                Primitive(VariantPrimitiveType.Int64, BinaryPrimitives.ReadInt64LittleEndian(data));
                break;
            case TdsDataType.Float4:
                Primitive(VariantPrimitiveType.Float, BinaryPrimitives.ReadSingleLittleEndian(data));
                break;
            case TdsDataType.Float8:
                Primitive(VariantPrimitiveType.Double, BinaryPrimitives.ReadDoubleLittleEndian(data));
                break;

            case TdsDataType.Money:
            {
                long value = ((long)BinaryPrimitives.ReadInt32LittleEndian(data) << 32) | BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4));
                Decimal(VariantPrimitiveType.Decimal16, 4, value);
                break;
            }
            case TdsDataType.Money4:
                Decimal(VariantPrimitiveType.Decimal8, 4, BinaryPrimitives.ReadInt32LittleEndian(data));
                break;
            case TdsDataType.DecimalN:
            case TdsDataType.NumericN:
            {
                byte precision = properties[0];
                byte scale = properties[1];
                Span<byte> padded = stackalloc byte[16];
                padded.Clear();
                data.Slice(1).CopyTo(padded);
                Int128 magnitude = (Int128)BinaryPrimitives.ReadUInt128LittleEndian(padded);
                Int128 value = data[0] != 0 ? magnitude : -magnitude;
                Decimal(precision <= 9 ? VariantPrimitiveType.Decimal4 : precision <= 18 ? VariantPrimitiveType.Decimal8 : VariantPrimitiveType.Decimal16, scale, value);
                break;
            }

            case TdsDataType.DateTime:
                Primitive(VariantPrimitiveType.TimestampNtz, TemporalDecoder.ReadDateTime(data));
                break;
            case TdsDataType.DateTime4:
                Primitive(VariantPrimitiveType.TimestampNtz, TemporalDecoder.ReadSmallDateTime(data));
                break;
            case TdsDataType.DateN:
                Primitive(VariantPrimitiveType.Date, TemporalDecoder.ReadUInt24(data) - TemporalDecoder.DaysTo1970From0001);
                break;
            case TdsDataType.TimeN:
                Primitive(VariantPrimitiveType.TimeNtz, ToMicroseconds(TemporalDecoder.ReadTime(data, data.Length), properties[0]));
                break;
            case TdsDataType.DateTime2N:
            case TdsDataType.DateTimeOffsetN:
            {
                // datetimeoffset holds UTC date and time, then the offset (not kept).
                int timeLength = data.Length - (baseType == TdsDataType.DateTime2N ? 3 : 5);
                long time = ToMicroseconds(TemporalDecoder.ReadTime(data, timeLength), properties[0]);
                long days = TemporalDecoder.ReadUInt24(data.Slice(timeLength)) - TemporalDecoder.DaysTo1970From0001;
                Primitive(baseType == TdsDataType.DateTime2N ? VariantPrimitiveType.TimestampNtz : VariantPrimitiveType.Timestamp,
                    days * TemporalDecoder.MicrosecondsPerDay + time);
                break;
            }

            case TdsDataType.Guid:
            {
                // RFC 4122 byte order; the first three fields are little-endian on the wire.
                Span<byte> uuid = stackalloc byte[16];
                uuid[0] = data[3];
                uuid[1] = data[2];
                uuid[2] = data[1];
                uuid[3] = data[0];
                uuid[4] = data[5];
                uuid[5] = data[4];
                uuid[6] = data[7];
                uuid[7] = data[6];
                data.Slice(8, 8).CopyTo(uuid.Slice(8));
                Primitive(VariantPrimitiveType.Uuid, uuid);
                break;
            }

            case TdsDataType.BigVarBinary:
            case TdsDataType.BigBinary:
                LengthPrefixed(VariantPrimitiveType.Binary, data);
                break;
            case TdsDataType.NVarChar:
            case TdsDataType.NChar:
                String(FromUtf16(MemoryMarshal.Cast<byte, char>(data)));
                break;
            case TdsDataType.BigVarChar:
            case TdsDataType.BigChar:
                String(FromCodePage(properties.Slice(0, 5), data));
                break;

            default:
                throw new AdbcException(
                    $"Column '{Column.Name}' has a sql_variant value of type 0x{(byte)baseType:X2}, which is not supported",
                    AdbcStatusCode.NotImplemented);
        }
    }

    private static long ToMicroseconds(long value, int scale) =>
        scale <= 6 ? value * TemporalDecoder.Pow10(6 - scale) : value / TemporalDecoder.Pow10(scale - 6);

    private void Header(VariantPrimitiveType type)
    {
        _values.Append((byte)((int)type << 2));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Primitive<T>(VariantPrimitiveType type, T value) where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();
        Span<byte> span = _values.GetSpan(1 + size);
        span[0] = (byte)((int)type << 2);
        Unsafe.WriteUnaligned(ref span[1], value);
        _values.Advance(1 + size);
    }

    private void Primitive(VariantPrimitiveType type, ReadOnlySpan<byte> payload)
    {
        Span<byte> span = _values.GetSpan(1 + payload.Length);
        span[0] = (byte)((int)type << 2);
        payload.CopyTo(span.Slice(1));
        _values.Advance(1 + payload.Length);
    }

    private void Decimal(VariantPrimitiveType type, byte scale, Int128 value)
    {
        int size = type switch
        {
            VariantPrimitiveType.Decimal4 => 4,
            VariantPrimitiveType.Decimal8 => 8,
            _ => 16,
        };
        Span<byte> span = _values.GetSpan(2 + size);
        span[0] = (byte)((int)type << 2);
        span[1] = scale;
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteInt128LittleEndian(bytes, value);
        bytes.Slice(0, size).CopyTo(span.Slice(2));
        _values.Advance(2 + size);
    }

    private void LengthPrefixed(VariantPrimitiveType type, ReadOnlySpan<byte> payload)
    {
        Span<byte> span = _values.GetSpan(5 + payload.Length);
        span[0] = (byte)((int)type << 2);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(1), payload.Length);
        payload.CopyTo(span.Slice(5));
        _values.Advance(5 + payload.Length);
    }

    /// <summary>A short string (one header byte) when under 64 bytes, else a length-prefixed string.</summary>
    private void String(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length < 64)
        {
            Span<byte> span = _values.GetSpan(1 + utf8.Length);
            span[0] = (byte)((utf8.Length << 2) | (int)VariantBasicType.ShortString);
            utf8.CopyTo(span.Slice(1));
            _values.Advance(1 + utf8.Length);
        }
        else
        {
            LengthPrefixed(VariantPrimitiveType.String, utf8);
        }
    }

    private ReadOnlySpan<byte> FromUtf16(ReadOnlySpan<char> chars)
    {
        if (_utf8.Length < chars.Length * 3)
        {
            _utf8 = new byte[chars.Length * 3];
        }
        Utf8.FromUtf16(chars, _utf8, out _, out int written, replaceInvalidSequences: true);
        return _utf8.AsSpan(0, written);
    }

    private ReadOnlySpan<byte> FromCodePage(ReadOnlySpan<byte> collationBytes, ReadOnlySpan<byte> data)
    {
        var collation = new TdsCollation(collationBytes.Length == 5 ? collationBytes : _defaultCollation);
        if (collation.IsUtf8 || Ascii.IsValid(data))
        {
            return data;
        }

        ulong key = collationBytes.Length == 5 ? BinaryPrimitives.ReadUInt32LittleEndian(collationBytes) | ((ulong)collationBytes[4] << 32) : 0;
        if (key != _collationKey)
        {
            _collationEncoding = collation.GetEncoding();
            _collationKey = key;
        }
        if (_collationEncoding!.CodePage == 65001)
        {
            return data;
        }
        int count = _collationEncoding.GetCharCount(data);
        if (_chars.Length < count)
        {
            _chars = new char[count];
        }
        int decoded = _collationEncoding.GetChars(data, _chars);
        return FromUtf16(_chars.AsSpan(0, decoded));
    }

    /// <summary>Ends the current row: its metadata and the value's end offset.</summary>
    private void EndValue()
    {
        if (_metadataOffsets.Length == 0)
        {
            _metadataOffsets.Append(0);
            _valueOffsets.Append(0);
        }
        _metadata.Append(s_emptyMetadata);
        _metadataOffsets.Append(_metadata.Length);
        _valueOffsets.Append(_values.Length);
    }

    public override void AppendNull()
    {
        // The struct slot is null; its children still hold a well-formed variant null.
        Header(VariantPrimitiveType.NullType);
        EndValue();
        _validity.AppendNull();
    }

    public override void Dispose()
    {
        _metadataOffsets.Dispose();
        _metadata.Dispose();
        _valueOffsets.Dispose();
        _values.Dispose();
    }

    public override IArrowArray Build()
    {
        int length = _validity.Length;
        int nullCount = _validity.NullCount;
        ArrowBuffer validity = _validity.Build();
        IArrowArray metadata = BuildArray(BinaryType.Default, length, 0, ArrowBuffer.Empty, _metadataOffsets.Build(), _metadata.Build());
        IArrowArray values = BuildArray(BinaryType.Default, length, 0, ArrowBuffer.Empty, _valueOffsets.Build(), _values.Build());
        var type = VariantType.Default;
        var storage = new StructArray(type.StorageType, length, [metadata, values], validity, nullCount);
        return new VariantArray(type, storage);
    }
}
