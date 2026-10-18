// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text;
using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Clast.Drivers.SqlServer.Arrow;
using Xunit;

namespace Clast.Drivers.SqlServer.Tests;

/// <summary>hierarchyid as text (and, by option, bytes) and xml: reads, ingest and Create mode.</summary>
public sealed class HierarchyIdXmlTests
{
    private static AdbcConnection Connect(string? hierarchyId = null)
    {
        var options = new Dictionary<string, string>(SqlServerTestConfiguration.Load().Options);
        if (hierarchyId != null)
        {
            options[SqlServerParameters.HierarchyId] = hierarchyId;
        }
        return new SqlServerDriver().Open(options).Connect(null);
    }

    private static RecordBatch ReadAll(AdbcConnection connection, string sql, out Schema schema)
    {
        using AdbcStatement statement = connection.CreateStatement();
        statement.SqlQuery = sql;
        using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
        schema = stream.Schema;
        var batches = new List<RecordBatch>();
        while (stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is RecordBatch batch)
        {
            batches.Add(batch);
        }
        RecordBatch result = Assert.Single(batches);
        return result;
    }

    private static void Execute(AdbcConnection connection, string sql)
    {
        using AdbcStatement statement = connection.CreateStatement();
        statement.SqlQuery = sql;
        statement.ExecuteUpdate();
    }

    private static void Ingest(AdbcConnection connection, RecordBatch batch, string table, BulkIngestMode mode)
    {
        using AdbcStatement ingest = connection.BulkIngest(null, null, table, mode, isTemporary: true);
        ingest.Bind(batch, batch.Schema);
        Assert.Equal(batch.Length, ingest.ExecuteUpdate().AffectedRows);
    }

    private static string[] ColumnTypes(AdbcConnection connection, string table)
    {
        using RecordBatch types = ReadAll(connection, $"""
            SELECT t.name FROM tempdb.sys.columns c JOIN tempdb.sys.types t ON t.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID('tempdb..#{table}') ORDER BY c.column_id
            """, out _);
        var names = (StringArray)types.Column(0);
        return Enumerable.Range(0, types.Length).Select(i => names.GetString(i)).ToArray();
    }

    private static string? Format(byte[] bytes)
    {
        var text = new byte[HierarchyId.MaxTextLength(bytes.Length)];
        return HierarchyId.TryFormat(bytes, text, out int written) ? Encoding.ASCII.GetString(text, 0, written) : null;
    }

    private static byte[]? Parse(string text)
    {
        var bytes = new byte[HierarchyId.MaxBytes];
        return HierarchyId.TryParse(Encoding.UTF8.GetBytes(text), bytes, out int written, out _) ? bytes.AsSpan(0, written).ToArray() : null;
    }

    // Every label pattern's bounds, and a few values inside each.
    private static readonly long[] Labels =
    [
        0, 1, 3, 4, 7, 8, 15, 16, 79, 80, 1103, 1104, 5199, 5200, 4294972495, 4294972496, 281479271683150,
        -1, -8, -9, -72, -73, -4168, -4169, -4294971464, -4294971465, -281479271682120,
        2, 5, 42, 500, 3000, 123456789, 9876543210987, -5, -40, -1000, -1234567, -98765432109876,
    ];

    /// <summary>Random paths: up to 6 levels of 1 to 3 labels; the labels before a '.' stay below the top label.</summary>
    private static IEnumerable<string> RandomPaths(int count)
    {
        var random = new Random(20261018);
        for (int n = 0; n < count; n++)
        {
            var path = new StringBuilder("/");
            int levels = random.Next(7);
            for (int level = 0; level < levels; level++)
            {
                int labels = random.Next(1, 4);
                for (int l = 0; l < labels; l++)
                {
                    long label = random.Next(3) == 0 ? random.Next(-100, 100) : Labels[random.Next(Labels.Length)];
                    path.Append(label).Append(l == labels - 1 ? '/' : '.');
                }
            }
            yield return path.ToString();
        }
    }

    /// <summary>The codec agrees with the server, byte for byte, on every pattern's bounds and on random paths.</summary>
    [SkippableFact]
    public void CodecMatchesTheServer()
    {
        using AdbcConnection connection = Connect();
        string[] paths = ["/", .. Labels.Select(l => $"/{l}/"), .. Labels.Select(l => $"/{l}.{l}/7/"), .. RandomPaths(1500)];
        for (int start = 0; start < paths.Length; start += 500)
        {
            IEnumerable<string> chunk = paths.Skip(start).Take(500);
            string values = string.Join(", ", chunk.Select((p, i) => $"({i}, N'{p}')"));
            using RecordBatch batch = ReadAll(connection, $"""
                SELECT t, CAST(hierarchyid::Parse(t) AS varbinary(892)) AS b, hierarchyid::Parse(t) AS h, hierarchyid::Parse(t).ToString() AS s
                FROM (VALUES {values}) AS v(i, t) ORDER BY i
                """, out _);
            var texts = (StringArray)batch.Column(0);
            var bytes = (BinaryArray)batch.Column(1);
            var decoded = (StringArray)batch.Column(2);
            var server = (StringArray)batch.Column(3);
            for (int i = 0; i < batch.Length; i++)
            {
                string text = texts.GetString(i);
                byte[] expected = bytes.GetBytes(i).ToArray();
                Assert.Equal(text, server.GetString(i));
                Assert.True(expected.SequenceEqual(Parse(text) ?? []), $"Parse({text})");
                Assert.Equal(text, Format(expected));
                Assert.Equal(text, decoded.GetString(i));
            }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("1/")]
    [InlineData("/1")]
    [InlineData("//")]
    [InlineData("/a/")]
    [InlineData("/1./")]
    [InlineData("/1..2/")]
    [InlineData("/.1/")]
    [InlineData("/1/ /")]
    [InlineData("/281479271683152/")]
    [InlineData("/-281479271682121/")]
    [InlineData("/281479271683151.1/")]
    [InlineData("/99999999999999999999/")]
    public void RejectsInvalidText(string text) => Assert.Null(Parse(text));

    [Theory]
    [InlineData(new byte[] { 0x00 }, "/")]
    [InlineData(new byte[] { 0x58 }, "/1/")]
    [InlineData(new byte[] { 0x50 }, null)] // /0. with no level end
    [InlineData(new byte[] { 0x08 }, null)] // 000010: no such pattern
    [InlineData(new byte[] { 0xFC }, null)] // a 60-bit pattern cut short
    public void FormatsOrRejectsBytes(byte[] bytes, string? expected) => Assert.Equal(expected, Format(bytes));

    [SkippableFact]
    public void HierarchyIdIsTextByDefault()
    {
        using AdbcConnection connection = Connect();
        using RecordBatch batch = ReadAll(connection, """
            SELECT h, g FROM (VALUES (1, hierarchyid::Parse('/1/2.5/-3/'), geometry::Point(1, 2, 0)), (2, hierarchyid::GetRoot(), NULL), (3, NULL, NULL)) AS t(n, h, g) ORDER BY n
            """, out Schema schema);
        Assert.IsType<StringType>(schema.FieldsList[0].DataType);
        Assert.Equal("HIERARCHYID", schema.FieldsList[0].Metadata[DecoderFactory.SqlTypeMetadataKey]);
        Assert.IsType<BinaryType>(schema.FieldsList[1].DataType);
        var h = (StringArray)batch.Column(0);
        Assert.Equal("/1/2.5/-3/", h.GetString(0));
        Assert.Equal("/", h.GetString(1));
        Assert.True(h.IsNull(2));
    }

    [SkippableFact]
    public void HierarchyIdAsBinaryByOption()
    {
        using AdbcConnection connection = Connect("binary");
        using RecordBatch batch = ReadAll(connection, "SELECT h, CAST(h AS varbinary(892)) AS b FROM (VALUES (hierarchyid::Parse('/1/2/'))) AS t(h)", out Schema schema);
        Assert.IsType<BinaryType>(schema.FieldsList[0].DataType);
        Assert.Equal("HIERARCHYID", schema.FieldsList[0].Metadata[DecoderFactory.SqlTypeMetadataKey]);
        Assert.Equal(((BinaryArray)batch.Column(1)).GetBytes(0).ToArray(), ((BinaryArray)batch.Column(0)).GetBytes(0).ToArray());
    }

    [Fact]
    public void RejectsAnUnknownHierarchyIdOption()
    {
        var options = new Dictionary<string, string> { [SqlServerParameters.HierarchyId] = "bytes" };
        AdbcException ex = Assert.ThrowsAny<AdbcException>(() => new SqlServerDriver().Open(options).Connect(null));
        Assert.Equal(AdbcStatusCode.InvalidArgument, ex.Status);
        Assert.Contains(SqlServerParameters.HierarchyId, ex.Message);
    }

    /// <summary>Text or bytes go into a hierarchyid column; Create mode makes one from either, as the driver reads them.</summary>
    [SkippableFact]
    public void IngestsHierarchyIdAsTextOrBytes()
    {
        const string Source = "SELECT n, h FROM (VALUES (1, hierarchyid::Parse('/1/')), (2, hierarchyid::Parse('/1/2.5/-3/')), (3, hierarchyid::GetRoot()), (4, NULL)) AS t(n, h) ORDER BY n";
        string[] expected = ["/1/", "/1/2.5/-3/", "/", null!];
        foreach (string mode in new[] { "text", "binary" })
        {
            using AdbcConnection connection = Connect(mode);
            using RecordBatch source = ReadAll(connection, Source, out _);

            Execute(connection, "CREATE TABLE #h_append (n int, h hierarchyid)");
            Ingest(connection, source, "h_append", BulkIngestMode.Append);
            Ingest(connection, source, "h_created", BulkIngestMode.Create);
            Assert.Equal(["int", "hierarchyid"], ColumnTypes(connection, "h_created"));

            foreach (string table in new[] { "#h_append", "#h_created" })
            {
                using RecordBatch copy = ReadAll(connection, $"SELECT h.ToString() AS h FROM {table} ORDER BY n", out _);
                var h = (StringArray)copy.Column(0);
                Assert.Equal(expected, Enumerable.Range(0, copy.Length).Select(i => h.GetString(i)));
            }
        }
    }

    [SkippableFact]
    public void IngestRejectsInvalidHierarchyIdText()
    {
        using AdbcConnection connection = Connect();
        Execute(connection, "CREATE TABLE #h_bad (h hierarchyid)");
        var batch = new RecordBatch(new Schema([new Field("h", StringType.Default, true)], null),
            [new StringArray.Builder().Append("/1/").Append("/1.x/").Build()], 2);
        AdbcException ex = Assert.ThrowsAny<AdbcException>(() => Ingest(connection, batch, "h_bad", BulkIngestMode.Append));
        Assert.Equal(AdbcStatusCode.InvalidData, ex.Status);
        Assert.Contains("row 1", ex.Message);
        // The session is still usable.
        using RecordBatch one = ReadAll(connection, "SELECT 1 AS one", out _);
    }

    private const string XmlSource = """
        SELECT n, x FROM (VALUES
            (1, CAST('<a b="1">x &amp; y</a>' AS xml)),
            (2, CAST(N'<日本語 attr="é">ü</日本語>' AS xml)),
            (3, CAST(N'<r/><r/>' AS xml)),
            (4, NULL)
        ) AS t(n, x) ORDER BY n
        """;

    [SkippableFact]
    public void XmlIsTextWithItsSqlType()
    {
        using AdbcConnection connection = Connect();
        using RecordBatch batch = ReadAll(connection, XmlSource, out Schema schema);
        Assert.IsType<StringType>(schema.FieldsList[1].DataType);
        Assert.Equal("XML", schema.FieldsList[1].Metadata[DecoderFactory.SqlTypeMetadataKey]);
        Assert.Equal("<日本語 attr=\"é\">ü</日本語>", ((StringArray)batch.Column(1)).GetString(1));
    }

    /// <summary>Strings go into an xml column, with or without an XML declaration; Create mode makes one for a field read from xml.</summary>
    [SkippableFact]
    public void IngestsXml()
    {
        using AdbcConnection connection = Connect();
        using RecordBatch source = ReadAll(connection, XmlSource, out _);
        string?[] expected = ["<a b=\"1\">x &amp; y</a>", "<日本語 attr=\"é\">ü</日本語>", "<r/><r/>", null];

        Execute(connection, "CREATE TABLE #x_append (n int, x xml)");
        Ingest(connection, source, "x_append", BulkIngestMode.Append);
        Ingest(connection, source, "x_created", BulkIngestMode.Create);
        Assert.Equal(["int", "xml"], ColumnTypes(connection, "x_created"));

        // Strings without the metadata: one with a UTF-8 declaration, which the server drops, and an empty one.
        var plain = new RecordBatch(new Schema([new Field("n", Int32Type.Default, false), new Field("x", StringType.Default, true)], null),
        [
            new Int32Array.Builder().Append(5).Append(6).Build(),
            new StringArray.Builder().Append("<?xml version=\"1.0\" encoding=\"utf-8\"?><d>é</d>").Append("").Build(),
        ], 2);
        Ingest(connection, plain, "x_append", BulkIngestMode.Append);

        foreach (string table in new[] { "#x_append", "#x_created" })
        {
            using RecordBatch copy = ReadAll(connection, $"SELECT x FROM {table} ORDER BY n", out _);
            var x = (StringArray)copy.Column(0);
            string?[] rows = Enumerable.Range(0, copy.Length).Select(i => x.GetString(i)).ToArray();
            Assert.Equal(table == "#x_append" ? [.. expected, "<d>é</d>", ""] : expected, rows);
        }

        // Without the metadata a string field is still nvarchar(max).
        Ingest(connection, plain, "x_plain", BulkIngestMode.Create);
        Assert.Equal(["int", "nvarchar"], ColumnTypes(connection, "x_plain"));
    }

    [SkippableFact]
    public void IngestRejectsMalformedXml()
    {
        using AdbcConnection connection = Connect();
        Execute(connection, "CREATE TABLE #x_bad (x xml)");
        var batch = new RecordBatch(new Schema([new Field("x", StringType.Default, true)], null),
            [new StringArray.Builder().Append("<a>").Build()], 1);
        Assert.ThrowsAny<AdbcException>(() => Ingest(connection, batch, "x_bad", BulkIngestMode.Append));
        using RecordBatch one = ReadAll(connection, "SELECT 1 AS one", out _);
    }
}
