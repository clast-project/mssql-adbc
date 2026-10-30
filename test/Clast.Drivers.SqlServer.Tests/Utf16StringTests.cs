// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.InteropServices;
using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.C;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Xunit;

namespace Clast.Drivers.SqlServer.Tests;

/// <summary><c>adbc.mssql.string_encoding=utf16</c>: text as <see cref="Utf16StringType"/>.</summary>
public sealed class Utf16StringTests : IDisposable
{
    private AdbcConnection? _opened;

    // Opened on first use, so tests that need no server run without one.
    private AdbcConnection _connection => _opened ??= new SqlServerDriver().Open(
        new Dictionary<string, string>(SqlServerTestConfiguration.Load().Options) { [SqlServerParameters.StringEncoding] = "utf16" }).Connect(null);

    public void Dispose() => _opened?.Dispose();

    private RecordBatch ReadOne(string sql, out Schema schema)
    {
        using AdbcStatement statement = _connection.CreateStatement();
        statement.SqlQuery = sql;
        using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
        schema = stream.Schema;
        return stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult()!;
    }

    private static Utf16StringArray Utf16(RecordBatch batch, int column) => Assert.IsType<Utf16StringArray>(batch.Column(column));

    /// <summary>A clast.utf16 array of the strings' code units, unpaired surrogates included.</summary>
    internal static Utf16StringArray Utf16Values(params string?[] values)
    {
        var builder = new BinaryArray.Builder();
        foreach (string? value in values)
        {
            if (value == null)
            {
                builder.AppendNull();
            }
            else
            {
                builder.Append(MemoryMarshal.AsBytes(value.AsSpan()));
            }
        }
        return new Utf16StringArray(Utf16StringType.Default, builder.Build());
    }

    private void Execute(string sql)
    {
        using AdbcStatement statement = _connection.CreateStatement();
        statement.SqlQuery = sql;
        statement.ExecuteUpdate();
    }

    private static List<RecordBatch> ReadAll(IArrowArrayStream stream)
    {
        var batches = new List<RecordBatch>();
        while (stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is RecordBatch batch)
        {
            batches.Add(batch);
        }
        return batches;
    }

    [Fact]
    public void RejectsAnUnknownEncoding()
    {
        var options = new Dictionary<string, string> { [SqlServerParameters.Server] = "localhost", [SqlServerParameters.StringEncoding] = "utf32" };
        Assert.Equal(AdbcStatusCode.InvalidArgument, Assert.ThrowsAny<AdbcException>(() => new SqlServerDriver().Open(options).Connect(null)).Status);
    }

    [SkippableFact]
    public void EveryTextTypeIsUtf16()
    {
        const string text = "héllo 😀 日本";
        using RecordBatch batch = ReadOne($"""
            SELECT CAST(N'{text}' AS nvarchar(50)) AS nv, CAST(N'ab' AS nchar(4)) AS nc, CAST(N'{text}' AS ntext) AS nt,
                CAST('héllo' COLLATE Latin1_General_CI_AS AS varchar(20)) AS v1252, CAST('héllo' COLLATE Latin1_General_CI_AS AS text) AS t,
                CAST(N'{text}' COLLATE Latin1_General_100_CI_AS_SC_UTF8 AS varchar(50)) AS vutf8,
                CAST('<a>é</a>' AS xml) AS x, hierarchyid::Parse('/1/2.5/') AS h, CAST(N'[1, "é"]' AS json) AS j,
                CAST(N'' AS nvarchar(10)) AS empty, CAST(NULL AS nvarchar(10)) AS n
            """, out Schema schema);

        Assert.Equal(
            ["clast.utf16", "clast.utf16", "clast.utf16", "clast.utf16", "clast.utf16", "clast.utf16", "clast.utf16", "clast.utf16", "arrow.json", "clast.utf16", "clast.utf16"],
            schema.FieldsList.Select(f => ((ExtensionType)f.DataType).Name));
        Assert.Equal(text, Utf16(batch, 0).GetString(0));
        Assert.Equal("ab  ", Utf16(batch, 1).GetString(0));
        Assert.Equal(text, Utf16(batch, 2).GetString(0));
        Assert.Equal("héllo", Utf16(batch, 3).GetString(0));
        Assert.Equal("héllo", Utf16(batch, 4).GetString(0));
        Assert.Equal(text, Utf16(batch, 5).GetString(0));
        Assert.Equal("<a>é</a>", Utf16(batch, 6).GetString(0));
        Assert.Equal("/1/2.5/", Utf16(batch, 7).GetString(0));
        Assert.Equal("""[1,"é"]""", Assert.IsType<JsonArray>(batch.Column(8)).GetString(0));
        Assert.Equal("", Utf16(batch, 9).GetString(0));
        Assert.True(Utf16(batch, 10).IsNull(0));
        Assert.Null(Utf16(batch, 10).GetString(0));

        // Little-endian code units, as on the wire: offsets count bytes.
        Assert.Equal(new byte[] { (byte)'h', 0, 0xE9, 0 }, Utf16(batch, 0).StorageArray.GetBytes(0).Slice(0, 4).ToArray());
        Assert.Equal(text.Length * 2, Utf16(batch, 0).StorageArray.GetBytes(0).Length);
    }

    [SkippableFact]
    public void LongValuesAndUnpairedSurrogatesPassThrough()
    {
        string big = string.Concat(Enumerable.Repeat("日本語テキスト", 3000));
        using RecordBatch batch = ReadOne(
            $"SELECT CAST(N'{big}' AS nvarchar(max)) AS big, N'a' + NCHAR(0xD800) + N'b' AS lone, CAST(REPLICATE(CAST('x' AS varchar(max)), 20000) AS varchar(max)) AS v",
            out _);
        Assert.Equal(big, Utf16(batch, 0).GetString(0));
        Assert.Equal(['a', '\uD800', 'b'], Utf16(batch, 1).GetSpan(0).ToArray());
        Assert.Equal(new string('x', 20000), Utf16(batch, 2).GetString(0));
    }

    [SkippableFact]
    public void SchemasMatchTheResults()
    {
        using AdbcStatement statement = _connection.CreateStatement();
        statement.SqlQuery = "SELECT name, object_id FROM sys.objects WHERE object_id = @id";
        Schema described = statement.ExecuteSchema();
        Assert.IsType<Utf16StringType>(described.FieldsList[0].DataType);
        Assert.IsType<Int32Type>(described.FieldsList[1].DataType);

        Schema table = _connection.GetTableSchema("master", "sys", "objects");
        Assert.IsType<Utf16StringType>(table.GetFieldByName("name").DataType);

        // Parameters are described as the types they're bound with.
        statement.Prepare();
        Assert.IsType<Int32Type>(Assert.Single(statement.GetParameterSchema().FieldsList).DataType);
    }

    [SkippableFact]
    public void BoundAndPartitionedReadsAreUtf16()
    {
        using AdbcStatement statement = _connection.CreateStatement();
        statement.SqlQuery = "SELECT @p + N'é' AS s";
        var schema = new Schema([new Field("p", StringType.Default, false)], null);
        statement.Bind(new RecordBatch(schema, [new StringArray.Builder().Append("x").Build()], 1), schema);
        using (IArrowArrayStream stream = statement.ExecuteQuery().Stream!)
        using (RecordBatch batch = stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult()!)
        {
            Assert.Equal("xé", Utf16(batch, 0).GetString(0));
        }

        using AdbcStatement partitioned = _connection.CreateStatement();
        partitioned.SqlQuery = "SELECT object_id, name FROM sys.objects";
        partitioned.SetOption(SqlServerParameters.PartitionColumn, "object_id");
        partitioned.SetOption(SqlServerParameters.PartitionCount, "2");
        PartitionedResult result = partitioned.ExecutePartitioned();
        Assert.IsType<Utf16StringType>(result.Schema.GetFieldByName("name").DataType);
        int rows = 0;
        foreach (PartitionDescriptor descriptor in result.PartitionDescriptors)
        {
            using IArrowArrayStream stream = _connection.ReadPartition(descriptor);
            while (stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is RecordBatch batch)
            {
                using (batch)
                {
                    rows += Utf16(batch, 1).Length;
                }
            }
        }
        Assert.True(rows > 0);

        // Parallel reads: each partition on its own session.
        partitioned.SetOption(SqlServerParameters.ParallelReads, "2");
        using IArrowArrayStream parallel = partitioned.ExecuteQuery().Stream!;
        Assert.IsType<Utf16StringType>(parallel.Schema.GetFieldByName("name").DataType);
        int parallelRows = 0;
        while (parallel.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is RecordBatch batch)
        {
            using (batch)
            {
                parallelRows += Utf16(batch, 1).Length;
            }
        }
        Assert.Equal(rows, parallelRows);
    }

    /// <summary>The driver's own queries read utf8 whatever the option says.</summary>
    [SkippableFact]
    public void CatalogFunctionsAndOptionsAreUnchanged()
    {
        Assert.Equal("dbo", _connection.GetOption(AdbcOptions.Connection.CurrentDbSchema));
        using IArrowArrayStream objects = _connection.GetObjects(AdbcConnection.GetObjectsDepth.Tables, "master", "sys", "objects", null, null);
        Assert.IsType<StringType>(objects.Schema.GetFieldByName("catalog_name").DataType);

        using AdbcStatement ingest = _connection.BulkIngest(null, null, "clast_utf16", BulkIngestMode.Create, isTemporary: true);
        var schema = new Schema([new Field("s", StringType.Default, true)], null);
        ingest.Bind(new RecordBatch(schema, [new StringArray.Builder().Append("é").Build()], 1), schema);
        Assert.Equal(1, ingest.ExecuteUpdate().AffectedRows);
        using RecordBatch batch = ReadOne("SELECT s FROM #clast_utf16", out _);
        Assert.Equal("é", Utf16(batch, 0).GetString(0));
    }

    [SkippableFact]
    public void IngestTakesUtf16IntoEveryTextType()
    {
        const string text = "héllo 😀 日本";
        string big = string.Concat(Enumerable.Repeat("日本語テキスト", 3000));
        const string lone = "a\uD800b";
        Execute("""
            CREATE TABLE #clast_utf16_in (nv nvarchar(50), nc nchar(4), nmax nvarchar(max), v1252 varchar(20) COLLATE Latin1_General_CI_AS,
                vutf8 varchar(50) COLLATE Latin1_General_100_CI_AS_SC_UTF8, vmax varchar(max) COLLATE Latin1_General_100_CI_AS_SC_UTF8,
                x xml, h hierarchyid)
            """);
        string[] names = ["nv", "nc", "nmax", "v1252", "vutf8", "vmax", "x", "h"];
        var schema = new Schema(names.Select(n => new Field(n, Utf16StringType.Default, true)), null);
        IArrowArray[] columns =
        [
            Utf16Values(text, lone, null),
            Utf16Values("ab", "", null),
            Utf16Values(big, lone, null),
            Utf16Values("héllo", "日", null),
            Utf16Values(text, lone, null),
            Utf16Values(big, "", null),
            Utf16Values("<a>é 😀</a>", "<b/>", null),
            Utf16Values("/1/2.5/", "/", null),
        ];
        using (AdbcStatement ingest = _connection.BulkIngest(null, null, "clast_utf16_in", BulkIngestMode.Append, isTemporary: true))
        {
            ingest.Bind(new RecordBatch(schema, columns, 3), schema);
            Assert.Equal(3, ingest.ExecuteUpdate().AffectedRows);
        }

        using RecordBatch batch = ReadOne("SELECT nv, nc, nmax, v1252, vutf8, vmax, x, CAST(h AS nvarchar(100)) AS h FROM #clast_utf16_in ORDER BY h DESC", out _);
        Assert.Equal(
            new string?[] { text, "ab  ", big, "héllo", text, big, "<a>é 😀</a>", "/1/2.5/" },
            Enumerable.Range(0, names.Length).Select(c => Utf16(batch, c).GetString(0)));

        // Unpaired surrogates pass through into nvarchar; varchar can't hold them.
        Assert.Equal(lone, Utf16(batch, 0).GetSpan(1).ToString());
        Assert.Equal("    ", Utf16(batch, 1).GetString(1));
        Assert.Equal(lone, Utf16(batch, 2).GetSpan(1).ToString());
        Assert.Equal("?", Utf16(batch, 3).GetString(1));
        Assert.Equal("a�b", Utf16(batch, 4).GetString(1));
        Assert.Equal("", Utf16(batch, 5).GetString(1));
        Assert.Equal("<b/>", Utf16(batch, 6).GetString(1));
        Assert.Equal("/", Utf16(batch, 7).GetString(1));
        Assert.All(Enumerable.Range(0, names.Length), c => Assert.True(batch.Column(c).IsNull(2)));
    }

    [SkippableFact]
    public void IngestCreatesTablesForUtf16()
    {
        var schema = new Schema(
        [
            new Field("s", Utf16StringType.Default, true),
            new Field("x", Utf16StringType.Default, true, [new KeyValuePair<string, string>("MSSQL:type", "xml")]),
            new Field("h", Utf16StringType.Default, true, [new KeyValuePair<string, string>("MSSQL:type", "hierarchyid")]),
        ], null);
        using (AdbcStatement ingest = _connection.BulkIngest(null, null, "clast_utf16_new", BulkIngestMode.Create, isTemporary: true))
        {
            ingest.Bind(new RecordBatch(schema, [Utf16Values("日本"), Utf16Values("<a/>"), Utf16Values("/3/")], 1), schema);
            Assert.Equal(1, ingest.ExecuteUpdate().AffectedRows);
        }
        using RecordBatch batch = ReadOne("""
            SELECT STRING_AGG(t.name, ',') WITHIN GROUP (ORDER BY c.column_id) FROM tempdb.sys.columns c
                JOIN tempdb.sys.types t ON t.user_type_id = c.user_type_id WHERE c.object_id = OBJECT_ID('tempdb..#clast_utf16_new')
            """, out _);
        Assert.Equal("nvarchar,xml,hierarchyid", Utf16(batch, 0).GetString(0));
        using RecordBatch values = ReadOne("SELECT s, x, CAST(h AS nvarchar(10)) FROM #clast_utf16_new", out _);
        Assert.Equal(new string?[] { "日本", "<a/>", "/3/" }, Enumerable.Range(0, 3).Select(c => Utf16(values, c).GetString(0)));
    }

    [SkippableFact]
    public void IngestRejectsAnOddNumberOfBytes()
    {
        var schema = new Schema([new Field("s", Utf16StringType.Default, true)], null);
        var odd = new Utf16StringArray(Utf16StringType.Default, new BinaryArray.Builder().Append(new byte[] { 0x41 }.AsSpan()).Build());
        using AdbcStatement ingest = _connection.BulkIngest(null, null, "clast_utf16_odd", BulkIngestMode.Create, isTemporary: true);
        ingest.Bind(new RecordBatch(schema, [odd], 1), schema);
        Assert.Equal(AdbcStatusCode.InvalidData, Assert.ThrowsAny<AdbcException>(() => ingest.ExecuteUpdate()).Status);
    }

    /// <summary>Parameters take clast.utf16 whatever the string encoding option, so this reads with utf8.</summary>
    [SkippableFact]
    public void ParametersTakeUtf16()
    {
        using AdbcConnection utf8 = new SqlServerDriver().Open(SqlServerTestConfiguration.Load().Options).Connect(null);
        using AdbcStatement statement = utf8.CreateStatement();
        statement.SqlQuery = "SELECT @p AS s, CAST(DATALENGTH(@p) AS int) AS d, UNICODE(SUBSTRING(@p, 2, 1)) AS u";
        var schema = new Schema([new Field("p", Utf16StringType.Default, true)], null);

        // Short values: nvarchar(4000).
        statement.Bind(new RecordBatch(schema, [Utf16Values("héllo 😀", "a\uD800b", null)], 3), schema);
        List<RecordBatch> batches = ReadAll(statement.ExecuteQuery().Stream!);
        try
        {
            string?[] s = batches.SelectMany(b => Enumerable.Range(0, b.Length).Select(i => ((StringArray)b.Column("s")).GetString(i))).ToArray();
            int?[] d = batches.SelectMany(b => Enumerable.Range(0, b.Length).Select(i => ((Int32Array)b.Column("d")).GetValue(i))).ToArray();
            int?[] u = batches.SelectMany(b => Enumerable.Range(0, b.Length).Select(i => ((Int32Array)b.Column("u")).GetValue(i))).ToArray();
            Assert.Equal(new string?[] { "héllo 😀", "a�b", null }, s);
            Assert.Equal([16, 6, null], d);
            Assert.Equal([0xE9, 0xD800, null], u);        }
        finally
        {
            batches.ForEach(b => b.Dispose());
        }

        // A value over 4000 characters: nvarchar(max).
        string big = new string('日', 4001);
        statement.Bind(new RecordBatch(schema, [Utf16Values(big)], 1), schema);
        using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
        using RecordBatch batch = stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult()!;
        Assert.Equal(big, ((StringArray)batch.Column("s")).GetString(0));
        Assert.Equal(8002, ((Int32Array)batch.Column("d")).GetValue(0));
    }

    [SkippableFact]
    public unsafe void TheExtensionNameCrossesTheCDataInterface()
    {
        using RecordBatch batch = ReadOne("SELECT N'é' AS s", out Schema schema);
        CArrowSchema* exported = CArrowSchema.Create();
        try
        {
            CArrowSchemaExporter.ExportSchema(schema, exported);
            Field field = CArrowSchemaImporter.ImportSchema(exported).FieldsList[0];
            Assert.IsType<BinaryType>(field.DataType);
            Assert.Equal("clast.utf16", field.Metadata["ARROW:extension:name"]);
        }
        finally
        {
            CArrowSchema.Free(exported);
        }
    }
}
