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

    // ---- Ingest ----

    private const string SourceQuery = """
        SELECT n, v, j, vec, h, g FROM (VALUES
            (1, CAST(CAST(200 AS tinyint) AS sql_variant), CAST(N'{"a":[1,2]}' AS json), CAST('[1, 2, 3]' AS vector(3)), hierarchyid::Parse('/1/'), geometry::Point(1, 2, 0)),
            (2, CAST(CAST(-123.45 AS decimal(5, 2)) AS sql_variant), CAST(N'{"s":"é"}' AS json), CAST('[0.5, -1, 1e10]' AS vector(3)), hierarchyid::Parse('/1/2/'), geometry::Point(3, 4, 0)),
            (3, CAST(CAST(922337203685477.5807 AS money) AS sql_variant), NULL, NULL, NULL, NULL),
            (4, CAST(CAST('2024-02-29 13:14:15.123' AS datetime) AS sql_variant), CAST(N'[]' AS json), CAST('[7, 8, 9]' AS vector(3)), hierarchyid::GetRoot(), NULL),
            (5, CAST(CAST('13:14:15.123456' AS time(6)) AS sql_variant), NULL, NULL, NULL, NULL),
            (6, CAST(CAST('2024-02-29 13:14:15.5 +05:30' AS datetimeoffset(6)) AS sql_variant), NULL, NULL, NULL, NULL),
            (7, CAST(NEWID() AS sql_variant), NULL, NULL, NULL, NULL),
            (8, CAST(N'日本語' AS sql_variant), NULL, NULL, NULL, NULL),
            (9, CAST(CAST('2024-02-29' AS date) AS sql_variant), NULL, NULL, NULL, NULL),
            (10, CAST(0xDEADBEEF AS sql_variant), NULL, NULL, NULL, NULL),
            (11, CAST(CAST(1 AS bit) AS sql_variant), NULL, NULL, NULL, NULL),
            (12, CAST(CAST(2.5 AS float) AS sql_variant), NULL, NULL, NULL, NULL),
            (13, CAST(NULL AS sql_variant), NULL, NULL, NULL, NULL)
        ) AS t(n, v, j, vec, h, g)
        """;

    private void Ingest(RecordBatch batch, string table, BulkIngestMode mode)
    {
        using AdbcStatement ingest = _connection.BulkIngest(null, null, table, mode, isTemporary: true);
        ingest.Bind(batch, batch.Schema);
        Assert.Equal(batch.Length, ingest.ExecuteUpdate().AffectedRows);
    }

    private static void AssertSameRows(RecordBatch expected, RecordBatch actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        var ev = (VariantArray)expected.Column("v");
        var av = (VariantArray)actual.Column("v");
        var evec = (FixedSizeListArray)expected.Column("vec");
        var avec = (FixedSizeListArray)actual.Column("vec");
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(ev.IsNull(i), av.IsNull(i));
            if (!ev.IsNull(i))
            {
                Assert.Equal(ev.GetValueBytes(i).ToArray(), av.GetValueBytes(i).ToArray());
            }
            Assert.Equal(((JsonArray)expected.Column("j")).GetString(i), ((JsonArray)actual.Column("j")).GetString(i));
            Assert.Equal(evec.IsNull(i), avec.IsNull(i));
            Assert.Equal(((FloatArray)evec.Values).Values.Slice(i * 3, 3).ToArray(), ((FloatArray)avec.Values).Values.Slice(i * 3, 3).ToArray());
        }
    }

    [SkippableFact]
    public void IngestIntoNativeColumns()
    {
        var (_, source) = ReadOne(SourceQuery + " ORDER BY n");
        using (source)
        {
            Execute("CREATE TABLE #native (n int, v sql_variant, j json, vec vector(3), h hierarchyid, g geometry)");
            Ingest(source, "native", BulkIngestMode.Append);

            var (schema, copy) = ReadOne("SELECT n, v, j, vec, h, g FROM #native ORDER BY n");
            using (copy)
            {
                Assert.Equal("HIERARCHYID", schema.FieldsList[4].Metadata[DecoderFactory.SqlTypeMetadataKey]);
                AssertSameRows(source, copy);
                Assert.Equal(((BinaryArray)source.Column("h")).GetBytes(1).ToArray(), ((BinaryArray)copy.Column("h")).GetBytes(1).ToArray());
                Assert.Equal(((BinaryArray)source.Column("g")).GetBytes(1).ToArray(), ((BinaryArray)copy.Column("g")).GetBytes(1).ToArray());
            }

            // The server sees the values as the original types.
            var (_, check) = ReadOne("SELECT CAST(h AS nvarchar(100)) AS h, g.STX AS x, JSON_VALUE(j, '$.a[1]') AS a FROM #native WHERE n IN (1, 2) ORDER BY n");
            using (check)
            {
                Assert.Equal("/1/", ((StringArray)check.Column(0)).GetString(0));
                Assert.Equal("/1/2/", ((StringArray)check.Column(0)).GetString(1));
                Assert.Equal(3.0, ((DoubleArray)check.Column(1)).GetValue(1));
                Assert.Equal("2", ((StringArray)check.Column(2)).GetString(0));
            }
        }
    }

    [SkippableFact]
    public void CreateModeMakesNativeColumns()
    {
        var (_, source) = ReadOne("SELECT n, v, j, vec FROM (" + SourceQuery + ") AS s ORDER BY n");
        using (source)
        {
            Ingest(source, "created", BulkIngestMode.Replace);
            var (_, types) = ReadOne("""
                SELECT c.name, t.name AS type_name FROM tempdb.sys.columns c JOIN tempdb.sys.types t ON t.user_type_id = c.user_type_id
                WHERE c.object_id = OBJECT_ID('tempdb..#created') ORDER BY c.column_id
                """);
            using (types)
            {
                var names = (StringArray)types.Column(1);
                Assert.Equal(["int", "sql_variant", "json", "vector"], Enumerable.Range(0, types.Length).Select(i => names.GetString(i)));
            }
            var (_, copy) = ReadOne("SELECT n, v, j, vec FROM #created ORDER BY n");
            using (copy)
            {
                AssertSameRows(source, copy);
            }
        }
    }

    [SkippableFact]
    public void ParametersOfTheNewTypes()
    {
        var (_, source) = ReadOne("SELECT v, j, vec FROM (" + SourceQuery + ") AS s WHERE n = 2");
        using (source)
        {
            using AdbcStatement statement = _connection.CreateStatement();
            statement.SqlQuery = "SELECT @v AS v, CAST(@j AS json) AS j, @vec AS vec, CAST(SQL_VARIANT_PROPERTY(@v, 'BaseType') AS nvarchar(20)) AS base_type";
            statement.Bind(source, source.Schema);
            using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
            using RecordBatch result = stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult()!;
            Assert.Equal(-123.45m, ((VariantArray)result.Column("v")).GetVariantReader(0).GetDecimal4());
            Assert.Equal("decimal", ((StringArray)result.Column("base_type")).GetString(0));
            Assert.Equal("{\"s\":\"é\"}", ((JsonArray)result.Column("j")).GetString(0));
            Assert.Equal([0.5f, -1f, 1e10f], ((FloatArray)((FixedSizeListArray)result.Column("vec")).Values).Values.ToArray());
        }
    }

    [SkippableFact]
    public void VariantsThatSqlVariantCannotHoldAreRejected()
    {
        Execute("CREATE TABLE #bad (v sql_variant)");
        // An empty variant array: basic type 3 with one-byte offsets, no elements.
        var metadata = new BinaryArray.Builder().Append([0x01, 0x00, 0x00]).Build();
        var values = new BinaryArray.Builder().Append([0x03, 0x00, 0x00]).Build();
        var storage = new StructArray((StructType)VariantType.Default.StorageType, 1, [metadata, values], ArrowBuffer.Empty);
        var schema = new Schema([new Field("v", VariantType.Default, true)], null);
        using var batch = new RecordBatch(schema, [new VariantArray(VariantType.Default, storage)], 1);
        using (AdbcStatement ingest = _connection.BulkIngest(null, null, "bad", BulkIngestMode.Append, isTemporary: true))
        {
            ingest.Bind(batch, schema);
            Assert.Equal(AdbcStatusCode.InvalidData, Assert.ThrowsAny<AdbcException>(() => ingest.ExecuteUpdate()).Status);
        }

        // Nothing was inserted, and the session is still usable.
        var (_, count) = ReadOne("SELECT COUNT(*) FROM #bad");
        using (count)
        {
            Assert.Equal(0, ((Int32Array)count.Column(0)).GetValue(0));
        }
    }
}
