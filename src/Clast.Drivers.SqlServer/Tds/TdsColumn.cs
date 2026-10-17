// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow.Adbc;

namespace Clast.Drivers.SqlServer.Tds;

/// <summary>TDS data type identifiers (MS-TDS 2.2.5.4).</summary>
internal enum TdsDataType : byte
{
    Null = 0x1F,
    Int1 = 0x30,
    Bit = 0x32,
    Int2 = 0x34,
    Int4 = 0x38,
    DateTime4 = 0x3A,
    Float4 = 0x3B,
    Money = 0x3C,
    DateTime = 0x3D,
    Float8 = 0x3E,
    Money4 = 0x7A,
    Int8 = 0x7F,

    Guid = 0x24,
    IntN = 0x26,
    Decimal = 0x37,
    Numeric = 0x3F,
    BitN = 0x68,
    DecimalN = 0x6A,
    NumericN = 0x6C,
    FloatN = 0x6D,
    MoneyN = 0x6E,
    DateTimeN = 0x6F,
    DateN = 0x28,
    TimeN = 0x29,
    DateTime2N = 0x2A,
    DateTimeOffsetN = 0x2B,
    Char = 0x2F,
    VarChar = 0x27,
    Binary = 0x2D,
    VarBinary = 0x25,

    BigVarBinary = 0xA5,
    BigVarChar = 0xA7,
    BigBinary = 0xAD,
    BigChar = 0xAF,
    NVarChar = 0xE7,
    NChar = 0xEF,
    Xml = 0xF1,
    Udt = 0xF0,

    Text = 0x23,
    Image = 0x22,
    NText = 0x63,
    Variant = 0x62,

    /// <summary>json (SQL Server 2025), sent as such once the JSON feature extension is acknowledged.</summary>
    Json = 0xF4,

    /// <summary>vector (SQL Server 2025), sent as such once the vector feature extension is acknowledged.</summary>
    Vector = 0xF5,
}

/// <summary>How a column's values are framed in a row.</summary>
internal enum TdsLengthKind
{
    /// <summary>Fixed-size value with no length prefix; never null.</summary>
    Fixed,

    /// <summary>One-byte length prefix; zero means null.</summary>
    ByteLength,

    /// <summary>Two-byte length prefix; 0xFFFF means null.</summary>
    UShortLength,

    /// <summary>Partially length-prefixed (the <c>max</c> types and xml).</summary>
    Plp,

    /// <summary>text, ntext and image: text pointer, timestamp, four-byte length.</summary>
    Text,

    /// <summary>sql_variant: four-byte length; zero means null.</summary>
    Variant,
}

/// <summary>
/// One column from COLMETADATA.
/// </summary>
internal sealed class TdsColumn
{
    public string Name { get; private set; } = "";
    public TdsDataType Type { get; private set; }
    public uint UserType { get; private set; }
    public ushort Flags { get; private set; }
    public bool IsNullable => (Flags & 0x0001) != 0;

    /// <summary>Maximum length in bytes from TYPE_INFO; 0xFFFF for <c>max</c> types.</summary>
    public int MaxLength { get; private set; }

    public byte Precision { get; private set; }
    public byte Scale { get; private set; }

    /// <summary>Five-byte collation for character types; otherwise empty.</summary>
    public byte[] Collation { get; private set; } = [];

    public TdsLengthKind LengthKind { get; private set; }

    /// <summary>The type name of a CLR user-defined type (hierarchyid, geometry, ...); otherwise null.</summary>
    public string? UdtTypeName { get; private set; }

    /// <summary>The column type to declare in INSERT BULK, when it differs from <see cref="SqlTypeName"/>.</summary>
    private string? _bulkTypeName;

    /// <summary>The column type declared in INSERT BULK.</summary>
    public string BulkTypeName => _bulkTypeName ?? SqlTypeName;

    /// <summary>For vector columns, the number of float32 dimensions.</summary>
    public int VectorDimensions => (MaxLength - VectorHeaderLength) / 4;

    public const int VectorHeaderLength = 8;

    /// <summary>Byte size of a <see cref="TdsLengthKind.Fixed"/> value.</summary>
    public int FixedSize { get; private set; }

    /// <summary>A column (or RPC parameter) of a nullable, length-prefixed wire type.</summary>
    public static TdsColumn Create(string name, TdsDataType type, int maxLength, byte precision = 0, byte scale = 0, byte[]? collation = null) => new()
    {
        Name = name,
        Type = type,
        Flags = 0x0001,
        MaxLength = maxLength,
        Precision = precision,
        Scale = scale,
        Collation = collation ?? [],
        LengthKind = maxLength == 0xFFFF ? TdsLengthKind.Plp
            : type is TdsDataType.BigVarChar or TdsDataType.BigChar or TdsDataType.NVarChar or TdsDataType.NChar or TdsDataType.BigVarBinary or TdsDataType.BigBinary
                ? TdsLengthKind.UShortLength
                : TdsLengthKind.ByteLength,
    };

    /// <summary>True when values of the two columns have the same wire format and Arrow type.</summary>
    public bool SameTypeAs(TdsColumn other) =>
        Type == other.Type && MaxLength == other.MaxLength && Precision == other.Precision && Scale == other.Scale &&
        IsNullable == other.IsNullable && Collation.AsSpan().SequenceEqual(other.Collation);

    /// <summary>Reads the body of a COLMETADATA token (after the token byte). Returns null for "no metadata".</summary>
    public static TdsColumn[]? ReadMetadata(PacketReader reader)
    {
        int count = reader.ReadUInt16();
        if (count == 0xFFFF)
        {
            return null;
        }

        var columns = new TdsColumn[count];
        for (int i = 0; i < count; i++)
        {
            columns[i] = Read(reader);
        }
        return columns;
    }

    private static TdsColumn Read(PacketReader reader)
    {
        var column = new TdsColumn
        {
            UserType = reader.ReadUInt32(),
            Flags = reader.ReadUInt16(),
            Type = (TdsDataType)reader.ReadByte(),
        };
        column.ReadTypeInfo(reader);
        column.Name = reader.ReadBVarChar();
        return column;
    }

    private void ReadTypeInfo(PacketReader reader)
    {
        switch (Type)
        {
            case TdsDataType.Null:
                SetFixed(0);
                break;
            case TdsDataType.Int1:
            case TdsDataType.Bit:
                SetFixed(1);
                break;
            case TdsDataType.Int2:
                SetFixed(2);
                break;
            case TdsDataType.Int4:
            case TdsDataType.DateTime4:
            case TdsDataType.Float4:
            case TdsDataType.Money4:
                SetFixed(4);
                break;
            case TdsDataType.Money:
            case TdsDataType.DateTime:
            case TdsDataType.Float8:
            case TdsDataType.Int8:
                SetFixed(8);
                break;

            case TdsDataType.Guid:
            case TdsDataType.IntN:
            case TdsDataType.BitN:
            case TdsDataType.FloatN:
            case TdsDataType.MoneyN:
            case TdsDataType.DateTimeN:
            case TdsDataType.Char:
            case TdsDataType.VarChar:
            case TdsDataType.Binary:
            case TdsDataType.VarBinary:
                MaxLength = reader.ReadByte();
                LengthKind = TdsLengthKind.ByteLength;
                break;

            case TdsDataType.Decimal:
            case TdsDataType.Numeric:
            case TdsDataType.DecimalN:
            case TdsDataType.NumericN:
                MaxLength = reader.ReadByte();
                Precision = reader.ReadByte();
                Scale = reader.ReadByte();
                LengthKind = TdsLengthKind.ByteLength;
                break;

            case TdsDataType.DateN:
                MaxLength = 3;
                LengthKind = TdsLengthKind.ByteLength;
                break;

            case TdsDataType.TimeN:
            case TdsDataType.DateTime2N:
            case TdsDataType.DateTimeOffsetN:
                Scale = reader.ReadByte();
                LengthKind = TdsLengthKind.ByteLength;
                break;

            case TdsDataType.BigVarChar:
            case TdsDataType.BigChar:
            case TdsDataType.NVarChar:
            case TdsDataType.NChar:
                MaxLength = reader.ReadUInt16();
                Collation = reader.ReadByteArray(5);
                LengthKind = MaxLength == 0xFFFF ? TdsLengthKind.Plp : TdsLengthKind.UShortLength;
                break;

            case TdsDataType.BigVarBinary:
            case TdsDataType.BigBinary:
                MaxLength = reader.ReadUInt16();
                LengthKind = MaxLength == 0xFFFF ? TdsLengthKind.Plp : TdsLengthKind.UShortLength;
                break;

            case TdsDataType.Xml:
                if (reader.ReadByte() != 0)
                {
                    reader.ReadBVarChar();   // database
                    reader.ReadBVarChar();   // owning schema
                    reader.ReadUsVarChar();  // schema collection
                }
                MaxLength = 0xFFFF;
                LengthKind = TdsLengthKind.Plp;
                break;

            case TdsDataType.Udt:
                MaxLength = reader.ReadUInt16();
                reader.ReadBVarChar();   // database
                reader.ReadBVarChar();   // schema
                UdtTypeName = reader.ReadBVarChar();
                reader.ReadUsVarChar();  // assembly-qualified name
                LengthKind = TdsLengthKind.Plp;
                break;

            case TdsDataType.Json:
                MaxLength = 0xFFFF;
                LengthKind = TdsLengthKind.Plp;
                break;

            case TdsDataType.Vector:
                MaxLength = reader.ReadUInt16();
                Scale = reader.ReadByte(); // element type: 0 = float32
                LengthKind = TdsLengthKind.UShortLength;
                break;

            case TdsDataType.Text:
            case TdsDataType.NText:
            case TdsDataType.Image:
                MaxLength = reader.ReadInt32();
                if (Type != TdsDataType.Image)
                {
                    Collation = reader.ReadByteArray(5);
                }
                int parts = reader.ReadByte();
                for (int i = 0; i < parts; i++)
                {
                    reader.ReadUsVarChar();
                }
                LengthKind = TdsLengthKind.Text;
                break;

            case TdsDataType.Variant:
                MaxLength = reader.ReadInt32();
                LengthKind = TdsLengthKind.Variant;
                break;

            default:
                throw new AdbcException($"Unsupported TDS data type 0x{(byte)Type:X2}", AdbcStatusCode.NotImplemented);
        }
    }

    private void SetFixed(int size)
    {
        LengthKind = TdsLengthKind.Fixed;
        FixedSize = size;
        MaxLength = size;
    }

    /// <summary>Skips one value of this column in a row.</summary>
    public void SkipValue(PacketReader reader)
    {
        switch (LengthKind)
        {
            case TdsLengthKind.Fixed:
                reader.Skip(FixedSize);
                break;
            case TdsLengthKind.ByteLength:
                reader.Skip(reader.ReadByte());
                break;
            case TdsLengthKind.UShortLength:
                int length = reader.ReadUInt16();
                if (length != 0xFFFF)
                {
                    reader.Skip(length);
                }
                break;
            case TdsLengthKind.Plp:
                if (reader.ReadUInt64() != PlpNull)
                {
                    uint chunk;
                    while ((chunk = reader.ReadUInt32()) != 0)
                    {
                        reader.Skip(checked((int)chunk));
                    }
                }
                break;
            case TdsLengthKind.Text:
                int pointerLength = reader.ReadByte();
                if (pointerLength != 0)
                {
                    reader.Skip(pointerLength + 8);
                    reader.Skip(reader.ReadInt32());
                }
                break;
            case TdsLengthKind.Variant:
                reader.Skip(reader.ReadInt32());
                break;
        }
    }

    /// <summary>
    /// Returns this column as a bulk-load target: fixed-length types become
    /// their nullable (length-prefixed) equivalents so every value carries a
    /// length and NULL is always representable.
    /// </summary>
    public TdsColumn ToBulkTarget()
    {
        TdsDataType type = Type switch
        {
            TdsDataType.Int1 or TdsDataType.Int2 or TdsDataType.Int4 or TdsDataType.Int8 => TdsDataType.IntN,
            TdsDataType.Bit => TdsDataType.BitN,
            TdsDataType.Float4 or TdsDataType.Float8 => TdsDataType.FloatN,
            TdsDataType.Money or TdsDataType.Money4 => TdsDataType.MoneyN,
            TdsDataType.DateTime or TdsDataType.DateTime4 => TdsDataType.DateTimeN,
            TdsDataType.Decimal => TdsDataType.DecimalN,
            TdsDataType.Numeric => TdsDataType.NumericN,
            _ => Type,
        };

        var target = (TdsColumn)MemberwiseClone();
        target.Type = type;
        if (LengthKind == TdsLengthKind.Fixed)
        {
            target.LengthKind = TdsLengthKind.ByteLength;
        }

        switch (Type)
        {
            case TdsDataType.Json:
                // As SqlClient does: declared json, sent as varchar(max) holding UTF-8.
                target.Type = TdsDataType.BigVarChar;
                target.Collation = new byte[5];
                target._bulkTypeName = "json";
                break;
            case TdsDataType.Udt:
                // Sent as the type's serialized bytes.
                target.Type = TdsDataType.BigVarBinary;
                target.LengthKind = MaxLength == 0xFFFF ? TdsLengthKind.Plp : TdsLengthKind.UShortLength;
                target._bulkTypeName = MaxLength == 0xFFFF ? "varbinary(max)" : $"varbinary({MaxLength})";
                break;
        }
        return target;
    }

    /// <summary>Writes this column's COLMETADATA entry for a bulk-load stream.</summary>
    public void WriteBulkMetadata(PacketWriter writer)
    {
        writer.WriteUInt32(0); // user type
        writer.WriteUInt16((ushort)(Flags & (0x0001 | 0x000C | 0x0010))); // nullable, updatability, identity
        WriteTypeInfo(writer);
        writer.WriteBVarChar(Name);
    }

    /// <summary>Writes this column's TYPE_INFO, as in bulk-load metadata and RPC parameters.</summary>
    public void WriteTypeInfo(PacketWriter writer)
    {
        writer.WriteByte((byte)Type);
        switch (Type)
        {
            case TdsDataType.IntN:
            case TdsDataType.BitN:
            case TdsDataType.FloatN:
            case TdsDataType.MoneyN:
            case TdsDataType.DateTimeN:
            case TdsDataType.Guid:
                writer.WriteByte((byte)MaxLength);
                break;
            case TdsDataType.DecimalN:
            case TdsDataType.NumericN:
                writer.WriteByte((byte)MaxLength);
                writer.WriteByte(Precision);
                writer.WriteByte(Scale);
                break;
            case TdsDataType.DateN:
                break;
            case TdsDataType.TimeN:
            case TdsDataType.DateTime2N:
            case TdsDataType.DateTimeOffsetN:
                writer.WriteByte(Scale);
                break;
            case TdsDataType.BigVarChar:
            case TdsDataType.BigChar:
            case TdsDataType.NVarChar:
            case TdsDataType.NChar:
                writer.WriteUInt16((ushort)MaxLength);
                writer.WriteBytes(Collation);
                break;
            case TdsDataType.BigVarBinary:
            case TdsDataType.BigBinary:
                writer.WriteUInt16((ushort)MaxLength);
                break;
            case TdsDataType.Vector:
                writer.WriteUInt16((ushort)MaxLength);
                writer.WriteByte(Scale);
                break;
            case TdsDataType.Variant:
                writer.WriteInt32(MaxLength);
                break;
            default:
                throw new AdbcException($"Sending {SqlTypeName} values is not yet supported", AdbcStatusCode.NotImplemented);
        }
    }

    public const ulong PlpNull = 0xFFFFFFFFFFFFFFFF;
    public const ulong PlpUnknownLength = 0xFFFFFFFFFFFFFFFE;

    /// <summary>SQL type name with its parameters, for field metadata and error messages.</summary>
    public string SqlTypeName => Type switch
    {
        TdsDataType.Int1 => "TINYINT",
        TdsDataType.Bit or TdsDataType.BitN => "BIT",
        TdsDataType.Int2 => "SMALLINT",
        TdsDataType.Int4 => "INT",
        TdsDataType.Int8 => "BIGINT",
        TdsDataType.IntN => MaxLength switch { 1 => "TINYINT", 2 => "SMALLINT", 4 => "INT", _ => "BIGINT" },
        TdsDataType.Float4 => "REAL",
        TdsDataType.Float8 => "FLOAT",
        TdsDataType.FloatN => MaxLength == 4 ? "REAL" : "FLOAT",
        TdsDataType.Money => "MONEY",
        TdsDataType.Money4 => "SMALLMONEY",
        TdsDataType.MoneyN => MaxLength == 4 ? "SMALLMONEY" : "MONEY",
        TdsDataType.DateTime => "DATETIME",
        TdsDataType.DateTime4 => "SMALLDATETIME",
        TdsDataType.DateTimeN => MaxLength == 4 ? "SMALLDATETIME" : "DATETIME",
        TdsDataType.Decimal or TdsDataType.DecimalN => $"DECIMAL({Precision},{Scale})",
        TdsDataType.Numeric or TdsDataType.NumericN => $"NUMERIC({Precision},{Scale})",
        TdsDataType.DateN => "DATE",
        TdsDataType.TimeN => $"TIME({Scale})",
        TdsDataType.DateTime2N => $"DATETIME2({Scale})",
        TdsDataType.DateTimeOffsetN => $"DATETIMEOFFSET({Scale})",
        TdsDataType.Guid => "UNIQUEIDENTIFIER",
        TdsDataType.BigVarChar or TdsDataType.VarChar => MaxLength == 0xFFFF ? "VARCHAR(MAX)" : $"VARCHAR({MaxLength})",
        TdsDataType.BigChar or TdsDataType.Char => $"CHAR({MaxLength})",
        TdsDataType.NVarChar => MaxLength == 0xFFFF ? "NVARCHAR(MAX)" : $"NVARCHAR({MaxLength / 2})",
        TdsDataType.NChar => $"NCHAR({MaxLength / 2})",
        TdsDataType.BigVarBinary or TdsDataType.VarBinary => MaxLength == 0xFFFF ? "VARBINARY(MAX)" : $"VARBINARY({MaxLength})",
        TdsDataType.BigBinary or TdsDataType.Binary => $"BINARY({MaxLength})",
        TdsDataType.Xml => "XML",
        TdsDataType.Udt => UdtTypeName?.ToUpperInvariant() ?? "UDT",
        TdsDataType.Json => "JSON",
        TdsDataType.Vector => $"VECTOR({VectorDimensions})",
        TdsDataType.Text => "TEXT",
        TdsDataType.NText => "NTEXT",
        TdsDataType.Image => "IMAGE",
        TdsDataType.Variant => "SQL_VARIANT",
        TdsDataType.Null => "NULL",
        _ => $"0x{(byte)Type:X2}",
    };
}
