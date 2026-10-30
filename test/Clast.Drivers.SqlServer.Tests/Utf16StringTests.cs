// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

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
