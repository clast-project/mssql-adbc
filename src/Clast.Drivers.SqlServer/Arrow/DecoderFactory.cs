// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Types;
using Clast.Drivers.SqlServer.Tds;

namespace Clast.Drivers.SqlServer.Arrow;

/// <summary>
/// Maps TDS column metadata to Arrow fields and decoders.
/// </summary>
internal static class DecoderFactory
{
    /// <summary>Field metadata key holding the SQL Server type name (same key as the Go driver).</summary>
    public const string SqlTypeMetadataKey = "MSSQL:type";

    public static readonly TimestampType Timestamp = new TimestampType(TimeUnit.Microsecond, (string?)null);
    public static readonly Time64Type Time = new Time64Type(TimeUnit.Nanosecond);
    public static readonly TimestampWithOffsetType TimestampWithOffset = new TimestampWithOffsetType(TimeUnit.Microsecond);

    public static ColumnDecoder Create(TdsColumn column, int capacity, byte[] defaultCollation)
    {
        switch (column.Type)
        {
            case TdsDataType.Int1:
                return new PrimitiveDecoder<byte>(column, UInt8Type.Default, capacity);
            case TdsDataType.Int2:
                return new PrimitiveDecoder<short>(column, Int16Type.Default, capacity);
            case TdsDataType.Int4:
                return new PrimitiveDecoder<int>(column, Int32Type.Default, capacity);
            case TdsDataType.Int8:
                return new PrimitiveDecoder<long>(column, Int64Type.Default, capacity);
            case TdsDataType.IntN:
                return column.MaxLength switch
                {
                    1 => new PrimitiveDecoder<byte>(column, UInt8Type.Default, capacity),
                    2 => new PrimitiveDecoder<short>(column, Int16Type.Default, capacity),
                    4 => new PrimitiveDecoder<int>(column, Int32Type.Default, capacity),
                    8 => new PrimitiveDecoder<long>(column, Int64Type.Default, capacity),
                    _ => throw Unsupported(column),
                };

            case TdsDataType.Float4:
                return new PrimitiveDecoder<float>(column, FloatType.Default, capacity);
            case TdsDataType.Float8:
                return new PrimitiveDecoder<double>(column, DoubleType.Default, capacity);
            case TdsDataType.FloatN:
                return column.MaxLength == 4
                    ? new PrimitiveDecoder<float>(column, FloatType.Default, capacity)
                    : new PrimitiveDecoder<double>(column, DoubleType.Default, capacity);

            case TdsDataType.Bit:
            case TdsDataType.BitN:
                return new BooleanDecoder(column, capacity);

            case TdsDataType.Decimal:
            case TdsDataType.Numeric:
            case TdsDataType.DecimalN:
            case TdsDataType.NumericN:
                return new DecimalDecoder(column, capacity);

            case TdsDataType.Money:
            case TdsDataType.Money4:
            case TdsDataType.MoneyN:
                return new MoneyDecoder(column, capacity);

            case TdsDataType.BigVarChar:
            case TdsDataType.BigChar:
            case TdsDataType.NVarChar:
            case TdsDataType.NChar:
            case TdsDataType.Xml:
                return new VariableDecoder(column, StringType.Default, capacity, defaultCollation);

            case TdsDataType.BigVarBinary:
            case TdsDataType.BigBinary:
                return new VariableDecoder(column, BinaryType.Default, capacity, defaultCollation);

            case TdsDataType.Guid:
                return new GuidDecoder(column, capacity);

            case TdsDataType.DateN:
                return new TemporalDecoder(column, Date32Type.Default, capacity);
            case TdsDataType.TimeN:
                return new TemporalDecoder(column, Time, capacity);
            case TdsDataType.DateTime2N:
            case TdsDataType.DateTime:
            case TdsDataType.DateTime4:
            case TdsDataType.DateTimeN:
                return new TemporalDecoder(column, Timestamp, capacity);
            case TdsDataType.DateTimeOffsetN:
                return new TemporalDecoder(column, TimestampWithOffset, capacity);

            default:
                throw Unsupported(column);
        }
    }

    public static Field CreateField(ColumnDecoder decoder)
    {
        TdsColumn column = decoder.Column;
        return new Field(
            column.Name,
            decoder.ArrowType,
            column.IsNullable,
            [new KeyValuePair<string, string>(SqlTypeMetadataKey, column.SqlTypeName)]);
    }

    private static AdbcException Unsupported(TdsColumn column) =>
        new AdbcException(
            $"Column '{column.Name}' has type {column.SqlTypeName}, which is not yet supported",
            AdbcStatusCode.NotImplemented);
}
