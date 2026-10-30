// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.C;
using Apache.Arrow.Types;
using Xunit;

namespace Clast.Drivers.SqlServer.Tests;

/// <summary>
/// Data bound after crossing the Arrow C data interface, as it arrives from
/// Python, Go or C++ through the NativeAOT library: extension types come as
/// their storage types with <c>ARROW:extension:*</c> field metadata, and the
/// driver restores them for ingest and parameters.
/// </summary>
public sealed class CDataInterfaceTests
{
    private static readonly DateTimeOffset Instant = new(2024, 2, 29, 12, 34, 56, 123, TimeSpan.FromMinutes(330));
    private static readonly Guid Id = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");

    private static RecordBatch Batch()
    {
        var dto = new TimestampWithOffsetArray.Builder(TimeUnit.Microsecond).Append(Instant).AppendNull().Build();
        var guid = new GuidArray.Builder().Append(Id).AppendNull().Build();
        var json = new JsonArray(new StringArray.Builder().Append("{\"a\":1}").AppendNull().Build());
        Utf16StringArray utf16 = Utf16StringTests.Utf16Values("日本", null);
        var ids = new Int32Array.Builder().Append(1).Append(2).Build();
        var schema = new Schema(
        [
            new Field("id", Int32Type.Default, false),
            new Field("dto", dto.Data.DataType, true),
            new Field("g", guid.Data.DataType, true),
            new Field("j", json.Data.DataType, true),
            new Field("u", utf16.Data.DataType, true),
        ], null);
        return new RecordBatch(schema, [ids, dto, guid, json, utf16], 2);
    }

    /// <summary>Exports and imports through the C data interface without an extension registry, as CAdbcDriverExporter does.</summary>
    private static unsafe RecordBatch ThroughCDataInterface(RecordBatch batch)
    {
        CArrowSchema* schema = CArrowSchema.Create();
        CArrowArray* array = CArrowArray.Create();
        try
        {
            CArrowSchemaExporter.ExportSchema(batch.Schema, schema);
            CArrowArrayExporter.ExportRecordBatch(batch, array);
            Schema imported = CArrowSchemaImporter.ImportSchema(schema);
            return CArrowArrayImporter.ImportRecordBatch(array, imported);
        }
        finally
        {
            CArrowSchema.Free(schema);
            CArrowArray.Free(array);
        }
    }

    private static AdbcConnection Connect() => new SqlServerDriver().Open(SqlServerTestConfiguration.Load().Options).Connect(null);

    [SkippableFact]
    public void IngestsExtensionTypesThatArriveAsMetadata()
    {
        using AdbcConnection connection = Connect();
        using RecordBatch original = Batch();
        using RecordBatch imported = ThroughCDataInterface(original);
        Assert.IsType<StructType>(imported.Schema.GetFieldByName("dto").DataType);
        Assert.Equal("arrow.uuid", imported.Schema.GetFieldByName("g").Metadata["ARROW:extension:name"]);

        using (AdbcStatement ingest = connection.BulkIngest(null, null, "clast_cdata", BulkIngestMode.Create, isTemporary: true))
        {
            ingest.Bind(imported, imported.Schema);
            Assert.Equal(2, ingest.ExecuteUpdate().AffectedRows);
        }

        List<object?[]> types = QueryTests.ReadAdbc(connection,
            "SELECT c.name, t.name FROM tempdb.sys.columns c JOIN tempdb.sys.types t ON t.user_type_id = c.user_type_id WHERE c.object_id = OBJECT_ID('tempdb..#clast_cdata') ORDER BY c.column_id",
            out _, out _);
        Assert.Equal(["id", "dto", "g", "j", "u"], types.Select(r => (string)r[0]!));
        Assert.Equal(["int", "datetimeoffset", "uniqueidentifier", "json", "nvarchar"], types.Select(r => (string)r[1]!));

        object?[] row = Assert.Single(QueryTests.ReadAdbc(connection, """
            SELECT COUNT(*) FROM #clast_cdata
            WHERE dto = '2024-02-29T12:34:56.123+05:30' AND DATEPART(TZOFFSET, dto) = 330
              AND g = '00112233-4455-6677-8899-AABBCCDDEEFF' AND CAST(j AS nvarchar(max)) = N'{"a":1}' AND u = N'日本'
            """, out _, out _));
        Assert.Equal(1, row[0]);
    }

    [SkippableFact]
    public void BindsExtensionTypesThatArriveAsMetadata()
    {
        using AdbcConnection connection = Connect();
        using RecordBatch original = Batch();
        using RecordBatch imported = ThroughCDataInterface(original);
        using AdbcStatement statement = connection.CreateStatement();
        statement.SqlQuery = "SELECT DATEPART(TZOFFSET, @dto) AS offset, CAST(@g AS nvarchar(36)) AS g, JSON_VALUE(@j, '$.a') AS a, @u + N'!' AS u";
        statement.Bind(imported.Slice(0, 1), imported.Schema);
        using Apache.Arrow.Ipc.IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
        using RecordBatch result = stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult()!;
        Assert.Equal(330, ((Int32Array)result.Column("offset")).GetValue(0));
        Assert.Equal("00112233-4455-6677-8899-AABBCCDDEEFF", ((StringArray)result.Column("g")).GetString(0));
        Assert.Equal("1", ((StringArray)result.Column("a")).GetString(0));
        Assert.Equal("日本!", ((StringArray)result.Column("u")).GetString(0));
    }
}
