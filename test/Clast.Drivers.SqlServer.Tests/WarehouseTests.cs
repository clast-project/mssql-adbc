// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Serialization;
using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Clast.Drivers.SqlServer.Arrow;
using Xunit;

namespace Clast.Drivers.SqlServer.Tests;

/// <summary>
/// Against a Fabric Warehouse, named by the JSON file in
/// <c>MSSQL_WAREHOUSE_CONFIG_FILE</c>: driver options, and the path of a file
/// holding an Entra access token (kept out of the configuration). Every table
/// these tests create is named <c>clast_wh_*</c> and dropped afterwards.
/// </summary>
public sealed class WarehouseTests
{
    private sealed class Configuration
    {
        [JsonPropertyName("options")]
        public Dictionary<string, string> Options { get; set; } = new();

        [JsonPropertyName("accessTokenFile")]
        public string? AccessTokenFile { get; set; }
    }

    private static Dictionary<string, string> Options()
    {
        string? path = Environment.GetEnvironmentVariable("MSSQL_WAREHOUSE_CONFIG_FILE");
        Skip.IfNot(path is { Length: > 0 } && File.Exists(path), "MSSQL_WAREHOUSE_CONFIG_FILE is not set to an existing file");
        Configuration config = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(path!))!;
        var options = new Dictionary<string, string>(config.Options);
        if (config.AccessTokenFile is string tokenFile)
        {
            options[SqlServerParameters.AccessToken] = File.ReadAllText(Environment.ExpandEnvironmentVariables(tokenFile)).Trim();
        }
        return options;
    }

    private static AdbcConnection Connect() => new SqlServerDriver().Open(Options()).Connect(null);

    private static string NewTable() => "clast_wh_" + Guid.NewGuid().ToString("N").Substring(0, 12);

    private static List<object?[]> Query(AdbcConnection connection, string sql)
    {
        using AdbcStatement statement = connection.CreateStatement();
        statement.SqlQuery = sql;
        using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
        var rows = new List<object?[]>();
        while (stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is RecordBatch batch)
        {
            using (batch)
            {
                for (int i = 0; i < batch.Length; i++)
                {
                    rows.Add(Enumerable.Range(0, batch.ColumnCount).Select(c => ResultArrays.Value(batch.Column(c), i)).ToArray());
                }
            }
        }
        return rows;
    }

    private static void Execute(AdbcConnection connection, string sql)
    {
        using AdbcStatement statement = connection.CreateStatement();
        statement.SqlQuery = sql;
        statement.ExecuteUpdate();
    }

    /// <summary>Drops the tables a test created, and only those.</summary>
    private static void Drop(AdbcConnection connection, params string[] tables)
    {
        foreach (string table in tables)
        {
            Assert.StartsWith("clast_wh_", table);
            Execute(connection, $"DROP TABLE IF EXISTS dbo.[{table}]");
        }
    }

    private static long Ingest(AdbcConnection connection, string table, BulkIngestMode mode, RecordBatch batch)
    {
        using AdbcStatement statement = connection.BulkIngest(null, "dbo", table, mode, false);
        statement.Bind(batch, batch.Schema);
        return statement.ExecuteUpdate().AffectedRows;
    }

    private static readonly DateTimeOffset Instant = new(2024, 2, 29, 12, 34, 56, TimeSpan.Zero);

    /// <summary>Three rows (the last all nulls) of the types the warehouse maps.</summary>
    private static RecordBatch WarehouseBatch()
    {
        var micros = new TimestampType(TimeUnit.Microsecond, (string?)null);
        var nanos = new TimestampType(TimeUnit.Nanosecond, (string?)null);
        var utc = new TimestampType(TimeUnit.Microsecond, "UTC");
        var timeNanos = new Time64Type(TimeUnit.Nanosecond);
        var dec = new Decimal128Type(18, 4);
        var strings = new StringArray.Builder().Append("plain").Append("é😀 ünïcode").AppendNull().Build();
        var json = new JsonArray(new StringArray.Builder().Append("{\"a\":1}").Append("[1,2,3]").AppendNull().Build());
        IArrowArray[] columns =
        [
            new Int32Array.Builder().Append(1).Append(2).Append(3).Build(),
            new UInt8Array.Builder().Append(0).Append(255).AppendNull().Build(),
            strings,
            json,
            new TimestampArray.Builder(nanos).Append(Instant.AddTicks(1234567)).Append(Instant).AppendNull().Build(),
            new TimestampArray.Builder(utc).Append(Instant).Append(Instant.AddDays(-1)).AppendNull().Build(),
            new Time64Array.Builder(timeNanos).Append(3_723_123_456_789L).Append(0).AppendNull().Build(),
            new Decimal128Array.Builder(dec).Append(123.4567m).Append(-1m).AppendNull().Build(),
            new BooleanArray.Builder().Append(true).Append(false).AppendNull().Build(),
            new DoubleArray.Builder().Append(1.5).Append(-2.25).AppendNull().Build(),
            new Date32Array.Builder().Append(new DateTime(2024, 2, 29)).Append(new DateTime(1, 1, 1)).AppendNull().Build(),
            new BinaryArray.Builder().Append([1, 2, 3]).Append([]).AppendNull().Build(),
            new TimestampArray.Builder(micros).Append(Instant).Append(Instant).AppendNull().Build(),
        ];
        string[] names = ["id", "c_u8", "c_str", "c_json", "c_ts_ns", "c_ts_utc", "c_time_ns", "c_dec", "c_bool", "c_double", "c_date", "c_bin", "c_ts_us"];
        var schema = new Schema(names.Select((n, i) => new Field(n, columns[i].Data.DataType, n != "id")).ToList(), null);
        return new RecordBatch(schema, columns, 3);
    }

    [SkippableFact]
    public void IsAWarehouseRunningAtSnapshot()
    {
        using AdbcConnection connection = Connect();
        Assert.Equal("11", connection.GetOption(SqlServerParameters.EngineEdition));
        Assert.Equal(IsolationLevel.Snapshot, connection.IsolationLevel);
        connection.IsolationLevel = IsolationLevel.Default;
        connection.IsolationLevel = IsolationLevel.Snapshot;
        Assert.Throws<AdbcException>(() => connection.IsolationLevel = IsolationLevel.Serializable);
        Assert.Equal(1, Convert.ToInt32(Query(connection, "SELECT 1")[0][0]));
    }

    [SkippableFact]
    public void IngestsWithTheWarehouseTypes()
    {
        using AdbcConnection connection = Connect();
        string table = NewTable();
        try
        {
            Assert.Equal(3, Ingest(connection, table, BulkIngestMode.Create, WarehouseBatch()));

            List<object?[]> types = Query(connection, $"""
                SELECT c.name, t.name, c.max_length, c.scale, c.collation_name FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
                WHERE c.object_id = OBJECT_ID('dbo.{table}') ORDER BY c.column_id
                """);
            string Type(string column) => (string)types.Single(r => (string)r[0]! == column)[1]!;
            Assert.Equal("smallint", Type("c_u8"));
            Assert.Equal("varchar", Type("c_str"));
            Assert.Equal("varchar", Type("c_json"));
            Assert.Equal("datetime2", Type("c_ts_ns"));
            Assert.Equal("datetime2", Type("c_ts_utc"));
            Assert.Equal("time", Type("c_time_ns"));
            Assert.Equal("varbinary", Type("c_bin"));

            List<object?[]> rows = Query(connection,
                $"SELECT id, c_u8, c_str, c_json, CAST(c_dec AS varchar(20)), c_bool, CAST(c_double AS varchar(20)), CONVERT(varchar(20), c_bin, 2) FROM dbo.[{table}] ORDER BY id");
            Assert.Equal(3, rows.Count);
            Assert.Equal((short)255, rows[1][1]);
            Assert.Equal("é😀 ünïcode", rows[1][2]);
            Assert.Equal("[1,2,3]", rows[1][3]);
            Assert.Equal("123.4567", rows[0][4]);
            Assert.Equal(true, rows[0][5]);
            Assert.Equal("1.5", rows[0][6]);
            Assert.Equal("010203", rows[0][7]);
            Assert.All(rows[2].Skip(1), Assert.Null);

            // Fractional seconds stop at 6 digits; the UTC timestamp keeps its instant.
            Assert.Equal("2024-02-29 12:34:56.123456", Query(connection, $"SELECT CONVERT(varchar(30), c_ts_ns, 121) FROM dbo.[{table}] WHERE id = 1")[0][0]);
            Assert.Equal("2024-02-29 12:34:56.000000", Query(connection, $"SELECT CONVERT(varchar(30), c_ts_utc, 121) FROM dbo.[{table}] WHERE id = 1")[0][0]);
            Assert.Equal("01:02:03.123456", Query(connection, $"SELECT CONVERT(varchar(20), c_time_ns, 114) + '' FROM dbo.[{table}] WHERE id = 1")[0][0]);

            // Append, then replace.
            Assert.Equal(3, Ingest(connection, table, BulkIngestMode.Append, WarehouseBatch()));
            Assert.Equal(6L, Convert.ToInt64(Query(connection, $"SELECT COUNT_BIG(*) FROM dbo.[{table}]")[0][0]));
            Assert.Equal(3, Ingest(connection, table, BulkIngestMode.Replace, WarehouseBatch()));
            Assert.Equal(3, Ingest(connection, table, BulkIngestMode.CreateAppend, WarehouseBatch()));
            Assert.Equal(6L, Convert.ToInt64(Query(connection, $"SELECT COUNT_BIG(*) FROM dbo.[{table}]")[0][0]));
        }
        finally
        {
            Drop(connection, table);
        }
    }

    [SkippableFact]
    public void IngestsInBatches()
    {
        using AdbcConnection connection = Connect();
        string table = NewTable();
        try
        {
            long BatchedIngest(BulkIngestMode mode, IEnumerable<int> values)
            {
                var batch = new RecordBatch.Builder()
                    .Append("v", false, c => c.Int32(b => b.AppendRange(values)))
                    .Build();
                using AdbcStatement statement = connection.BulkIngest(null, "dbo", table, mode, false);
                statement.SetOption(SqlServerParameters.IngestBatchSize, "10000");
                statement.Bind(batch, batch.Schema);
                return statement.ExecuteUpdate().AffectedRows;
            }
            long Count() => Convert.ToInt64(Query(connection, $"SELECT COUNT_BIG(*) FROM dbo.[{table}]")[0][0]);

            // Three INSERT BULKs on one session, the last partial.
            Execute(connection, $"CREATE TABLE dbo.[{table}] (v smallint NOT NULL)");
            Assert.Equal(25000, BatchedIngest(BulkIngestMode.Append, Enumerable.Range(0, 25000).Select(i => i % 30000)));
            Assert.Equal(25000L, Count());
            Assert.Equal(25000L * 24999 / 2, Convert.ToInt64(Query(connection, $"SELECT SUM(CAST(v AS bigint)) FROM dbo.[{table}]")[0][0]));

            // In a transaction, the batches roll back together.
            connection.AutoCommit = false;
            Assert.Equal(25000, BatchedIngest(BulkIngestMode.Append, Enumerable.Range(0, 25000)));
            connection.Rollback();
            connection.AutoCommit = true;
            Assert.Equal(25000L, Count());

            // A value too big for smallint aborts the second batch; the first stays, and the session still works.
            var ex = Assert.ThrowsAny<AdbcException>(() => BatchedIngest(BulkIngestMode.Append, Enumerable.Range(0, 20000).Select(i => i == 15000 ? 40000 : i)));
            Assert.Equal(AdbcStatusCode.InvalidData, ex.Status);
            Assert.Equal(35000L, Count());
        }
        finally
        {
            Drop(connection, table);
        }
    }

    [SkippableFact]
    public void CatalogFunctionsWork()
    {
        using AdbcConnection connection = Connect();
        string table = NewTable();
        try
        {
            Ingest(connection, table, BulkIngestMode.Create, WarehouseBatch());

            Schema schema = connection.GetTableSchema(null, "dbo", table);
            Assert.Equal(13, schema.FieldsList.Count);
            Assert.IsType<Int16Type>(schema.GetFieldByName("c_u8").DataType);

            using (IArrowArrayStream objects = connection.GetObjects(AdbcConnection.GetObjectsDepth.All, connection.CurrentCatalog, "dbo", table, null, null))
            {
                RecordBatch batch = objects.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult()!;
                Assert.Equal(1, batch.Length);
                batch.Dispose();
            }

            using (IArrowArrayStream stats = connection.GetStatistics(connection.CurrentCatalog, "dbo", table, approximate: false))
            {
                RecordBatch? batch = stats.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult();
                Assert.NotNull(batch);
                Assert.True(batch!.Length > 0);
                batch.Dispose();
            }
            using (IArrowArrayStream stats = connection.GetStatistics(null, null, "%", approximate: true))
            {
                RecordBatch? batch = stats.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult();
                Assert.True(batch == null || batch.Length == 0);
                batch?.Dispose();
            }
        }
        finally
        {
            Drop(connection, table);
        }
    }

    [SkippableFact]
    public void TransactionsAndParameters()
    {
        using AdbcConnection connection = Connect();
        string table = NewTable();
        try
        {
            Ingest(connection, table, BulkIngestMode.Create, WarehouseBatch());

            connection.AutoCommit = false;
            Execute(connection, $"DELETE FROM dbo.[{table}]");
            connection.Rollback();
            connection.AutoCommit = true;
            Assert.Equal(3L, Convert.ToInt64(Query(connection, $"SELECT COUNT_BIG(*) FROM dbo.[{table}]")[0][0]));

            using AdbcStatement statement = connection.CreateStatement();
            statement.SqlQuery = $"SELECT c_str FROM dbo.[{table}] WHERE id = @p";
            var parameter = new Int32Array.Builder().Append(2).Build();
            statement.Bind(new RecordBatch(new Schema([new Field("p", Int32Type.Default, false)], null), [parameter], 1), new Schema([new Field("p", Int32Type.Default, false)], null));
            using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
            using RecordBatch batch = stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult()!;
            Assert.Equal("é😀 ünïcode", ((StringArray)batch.Column(0)).GetString(0));

            using AdbcStatement describe = connection.CreateStatement();
            describe.SqlQuery = $"SELECT c_str FROM dbo.[{table}] WHERE id = @p";
            describe.Prepare();
            Schema parameters = describe.GetParameterSchema();
            Assert.Single(parameters.FieldsList);
        }
        finally
        {
            Drop(connection, table);
        }
    }

    [SkippableFact]
    public void PartitionedAndParallelReads()
    {
        using AdbcConnection connection = Connect();
        string table = NewTable();
        try
        {
            Ingest(connection, table, BulkIngestMode.Create, WarehouseBatch());
            using AdbcStatement statement = connection.CreateStatement();
            statement.SqlQuery = $"SELECT id, c_str FROM dbo.[{table}]";
            statement.SetOption(SqlServerParameters.PartitionColumn, "id");
            statement.SetOption(SqlServerParameters.ParallelReads, "2");
            using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
            long rows = 0;
            while (stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is RecordBatch batch)
            {
                rows += batch.Length;
                batch.Dispose();
            }
            Assert.Equal(3, rows);
        }
        finally
        {
            Drop(connection, table);
        }
    }
}
