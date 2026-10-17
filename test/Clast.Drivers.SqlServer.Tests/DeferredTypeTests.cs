// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Apache.Arrow.Scalars.Variant;
using Apache.Arrow.Types;
using Clast.Drivers.SqlServer.Arrow;
using Xunit;

namespace Clast.Drivers.SqlServer.Tests;

/// <summary>sql_variant, json, vector, text/ntext/image and CLR types.</summary>
public sealed class DeferredTypeTests : IDisposable
{
    private readonly AdbcConnection _connection;

    public DeferredTypeTests()
    {
        SqlServerTestConfiguration config = SqlServerTestConfiguration.Load();
        _connection = new SqlServerDriver().Open(config.Options).Connect(null);
    }

    public void Dispose() => _connection.Dispose();

    private (Schema Schema, RecordBatch Batch) ReadOne(string sql)
    {
        using AdbcStatement statement = _connection.CreateStatement();
        statement.SqlQuery = sql;
        using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
        RecordBatch batch = stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult()!;
        Assert.Null(stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult());
        return (stream.Schema, batch);
    }

    private void Execute(string sql)
    {
        using AdbcStatement statement = _connection.CreateStatement();
        statement.SqlQuery = sql;
        statement.ExecuteUpdate();
    }

    [SkippableFact]
    public void SqlVariantValuesBecomeVariants()
    {
        var (schema, batch) = ReadOne("""
            SELECT n, v FROM (VALUES
                (1, CAST(CAST(200 AS tinyint) AS sql_variant)),
                (2, CAST(CAST(-5 AS smallint) AS sql_variant)),
                (3, CAST(123456789 AS sql_variant)),
                (4, CAST(CAST(-9000000000 AS bigint) AS sql_variant)),
                (5, CAST(CAST(1 AS bit) AS sql_variant)),
                (6, CAST(CAST(1.5 AS real) AS sql_variant)),
                (7, CAST(CAST(PI() AS float) AS sql_variant)),
                (8, CAST(CAST(-123.45 AS decimal(5, 2)) AS sql_variant)),
                (9, CAST(CAST(12345678901234567890.1234567890 AS decimal(30, 10)) AS sql_variant)),
                (10, CAST(CAST(922337203685477.5807 AS money) AS sql_variant)),
                (11, CAST(CAST(-214748.3648 AS smallmoney) AS sql_variant)),
                (12, CAST(CAST('2024-02-29' AS date) AS sql_variant)),
                (13, CAST(CAST('13:14:15.1234567' AS time(7)) AS sql_variant)),
                (14, CAST(CAST('2024-02-29 13:14:15.123' AS datetime) AS sql_variant)),
                (15, CAST(CAST('2024-02-29 13:14:00' AS smalldatetime) AS sql_variant)),
                (16, CAST(CAST('2024-02-29 13:14:15.123456' AS datetime2(6)) AS sql_variant)),
                (17, CAST(CAST('2024-02-29 13:14:15.5 +05:30' AS datetimeoffset(1)) AS sql_variant)),
                (18, CAST(CAST('00112233-4455-6677-8899-aabbccddeeff' AS uniqueidentifier) AS sql_variant)),
                (19, CAST(CAST('h' + CHAR(233) + 'llo' AS varchar(10)) COLLATE Latin1_General_CI_AS AS sql_variant)),
                (20, CAST(REPLICATE(N'日本', 40) AS sql_variant)),
                (21, CAST(0x010203 AS sql_variant)),
                (22, CAST(NULL AS sql_variant))
            ) AS t(n, v) ORDER BY n
            """);
        using (batch)
        {
            Assert.IsType<VariantType>(schema.FieldsList[1].DataType);
            Assert.Equal("SQL_VARIANT", schema.FieldsList[1].Metadata[DecoderFactory.SqlTypeMetadataKey]);
            var values = (VariantArray)batch.Column(1);
            Assert.Equal(22, values.Length);

            VariantReader Row(int n) => values.GetVariantReader(n - 1);
            Assert.Equal((short)200, Row(1).GetInt16());
            Assert.Equal((short)-5, Row(2).GetInt16());
            Assert.Equal(123456789, Row(3).GetInt32());
            Assert.Equal(-9000000000L, Row(4).GetInt64());
            Assert.True(Row(5).GetBoolean());
            Assert.Equal(1.5f, Row(6).GetFloat());
            Assert.Equal(Math.PI, Row(7).GetDouble());
            Assert.Equal(VariantPrimitiveType.Decimal4, Row(8).PrimitiveType);
            Assert.Equal(-123.45m, Row(8).GetDecimal4());
            Assert.Equal(VariantPrimitiveType.Decimal16, Row(9).PrimitiveType);
            // 30 digits: beyond System.Decimal.
            Assert.Equal("12345678901234567890.1234567890", Row(9).GetSqlDecimal().ToString());
            Assert.Equal(922337203685477.5807m, Row(10).GetDecimal16());
            Assert.Equal(-214748.3648m, Row(11).GetDecimal8());
            Assert.Equal(new DateTime(2024, 2, 29), Row(12).GetDate());
            Assert.Equal((13 * 3600 + 14 * 60 + 15) * 1_000_000L + 123456, Row(13).GetTimeNtzMicros());
            Assert.Equal(new DateTime(2024, 2, 29, 13, 14, 15, 123), Row(14).GetTimestampNtz());
            Assert.Equal(new DateTime(2024, 2, 29, 13, 14, 0), Row(15).GetTimestampNtz());
            Assert.Equal(new DateTime(2024, 2, 29, 13, 14, 15).AddTicks(1234560), Row(16).GetTimestampNtz());
            Assert.Equal(new DateTimeOffset(2024, 2, 29, 13, 14, 15, 500, TimeSpan.FromMinutes(330)).ToUniversalTime(), Row(17).GetTimestamp());
            Assert.Equal(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), Row(18).GetUuid());
            Assert.Equal("héllo", Row(19).GetString());
            Assert.Equal(string.Concat(Enumerable.Repeat("日本", 40)), Row(20).GetString());
            Assert.Equal([1, 2, 3], Row(21).GetBinary().ToArray());
            Assert.True(values.IsNull(21));
            Assert.Equal(1, values.NullCount);
        }
    }

    [SkippableFact]
    public void JsonUsesNativeTransport()
    {
        Assert.Equal(1, ((SqlServerConnection)_connection).Session.JsonVersion);
        var (schema, batch) = ReadOne("""
            SELECT j FROM (VALUES (1, CAST(N'{"name":"é","list":[1,2,3]}' AS json)), (2, CAST(NULL AS json))) AS t(n, j) ORDER BY n
            """);
        using (batch)
        {
            Assert.IsType<JsonType>(schema.FieldsList[0].DataType);
            Assert.Equal("JSON", schema.FieldsList[0].Metadata[DecoderFactory.SqlTypeMetadataKey]);
            var json = (JsonArray)batch.Column(0);
            Assert.Equal("""{"name":"é","list":[1,2,3]}""", json.GetString(0));
            Assert.True(json.IsNull(1));
        }
    }

    [SkippableFact]
    public void VectorBecomesFixedSizeListOfFloats()
    {
        Assert.Equal(1, ((SqlServerConnection)_connection).Session.VectorVersion);
        var (schema, batch) = ReadOne("""
            SELECT v FROM (VALUES (1, CAST('[1.5, -2, 3.25]' AS vector(3))), (2, CAST(NULL AS vector(3))), (3, CAST('[0, 0, 7]' AS vector(3)))) AS t(n, v) ORDER BY n
            """);
        using (batch)
        {
            var type = Assert.IsType<FixedSizeListType>(schema.FieldsList[0].DataType);
            Assert.Equal(3, type.ListSize);
            Assert.IsType<FloatType>(type.ValueDataType);
            Assert.Equal("VECTOR(3)", schema.FieldsList[0].Metadata[DecoderFactory.SqlTypeMetadataKey]);
            var vectors = (FixedSizeListArray)batch.Column(0);
            var floats = (FloatArray)vectors.Values;
            Assert.Equal([1.5f, -2f, 3.25f], floats.Values.Slice(0, 3).ToArray());
            Assert.True(vectors.IsNull(1));
            Assert.Equal([0f, 0f, 7f], floats.Values.Slice(6, 3).ToArray());
        }
    }

    [SkippableFact]
    public void LegacyLobTypes()
    {
        Execute("CREATE TABLE #lob (n int, t text COLLATE Latin1_General_CI_AS, nt ntext, i image)");
        Execute("INSERT INTO #lob VALUES (1, 'caf' + CHAR(233), N'日本', 0x0102), (2, NULL, NULL, NULL), (3, REPLICATE(CAST('x' AS varchar(max)), 100000), REPLICATE(CAST(N'y' AS nvarchar(max)), 50000), CAST(REPLICATE(CAST(0xAB AS varbinary(max)), 70000) AS varbinary(max)))");
        var (schema, batch) = ReadOne("SELECT t, nt, i FROM #lob ORDER BY n");
        using (batch)
        {
            Assert.IsType<StringType>(schema.FieldsList[0].DataType);
            Assert.IsType<StringType>(schema.FieldsList[1].DataType);
            Assert.IsType<BinaryType>(schema.FieldsList[2].DataType);
            var t = (StringArray)batch.Column(0);
            var nt = (StringArray)batch.Column(1);
            var i = (BinaryArray)batch.Column(2);
            Assert.Equal("café", t.GetString(0));
            Assert.Equal("日本", nt.GetString(0));
            Assert.Equal([1, 2], i.GetBytes(0).ToArray());
            Assert.True(t.IsNull(1) && nt.IsNull(1) && i.IsNull(1));
            Assert.Equal(100000, t.GetString(2).Length);
            Assert.Equal(50000, nt.GetString(2).Length);
            Assert.Equal(70000, i.GetBytes(2).Length);
        }
    }

    [SkippableFact]
    public void ClrTypesAreTheirSerializedBytes()
    {
        var (schema, batch) = ReadOne("""
            SELECT h, CAST(h AS varbinary(892)) AS hb, g, CAST(g AS varbinary(max)) AS gb, n
            FROM (VALUES (1, hierarchyid::Parse('/1/2/'), geometry::Point(1, 2, 0)), (2, NULL, NULL)) AS t(n, h, g) ORDER BY n
            """);
        using (batch)
        {
            Assert.IsType<BinaryType>(schema.FieldsList[0].DataType);
            Assert.Equal("HIERARCHYID", schema.FieldsList[0].Metadata[DecoderFactory.SqlTypeMetadataKey]);
            Assert.Equal("GEOMETRY", schema.FieldsList[2].Metadata[DecoderFactory.SqlTypeMetadataKey]);
            Assert.Equal(((BinaryArray)batch.Column(1)).GetBytes(0).ToArray(), ((BinaryArray)batch.Column(0)).GetBytes(0).ToArray());
            Assert.Equal(((BinaryArray)batch.Column(3)).GetBytes(0).ToArray(), ((BinaryArray)batch.Column(2)).GetBytes(0).ToArray());
            Assert.True(batch.Column(0).IsNull(1) && batch.Column(2).IsNull(1));
        }
    }
}
