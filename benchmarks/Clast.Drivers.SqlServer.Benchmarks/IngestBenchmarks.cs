// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Data;
using System.Globalization;
using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Adbc.C;
using Apache.Arrow.Ipc;
using BenchmarkDotNet.Attributes;
using Microsoft.Data.SqlClient;

namespace Clast.Drivers.SqlServer.Benchmarks;

/// <summary>
/// Appends the benchmark table's rows into an empty table of the same shape.
/// The source data is materialized before measurement: Arrow record batches
/// for the ADBC drivers, a DataTable for SqlBulkCopy.
/// </summary>
[MemoryDiagnoser]
public class IngestBenchmarks
{
    private static string TargetName => BenchmarkData.Prefix + "ingest_target";

    private static string TargetTable => "dbo." + TargetName;

    private List<RecordBatch> _batches = new();
    private DataTable? _table;
    private AdbcDriver? _adbcDriver;
    private AdbcDatabase? _adbcDatabase;
    private AdbcConnection? _adbcConnection;
    private SqlConnection? _sqlConnection;

    [Params(1_000_000)]
    public int Rows { get; set; }

    [Params(false, true)]
    public bool TableLock { get; set; }

    [ParamsSource(nameof(Drivers))]
    public string Driver { get; set; } = "";

    /// <summary>Rows per batch for this driver and SqlClient; 0 for one batch. The Go driver has no such setting.</summary>
    [Params(0)]
    public int BatchSize { get; set; }

    public static IEnumerable<string> Drivers() => ReadBenchmarks.Drivers();

    [GlobalSetup]
    public void Setup()
    {
        BenchmarkData.EnsureTable(Rows);
        string source = BenchmarkData.TableName(Rows);

        _sqlConnection = ServerPlacement.OpenSqlClient(BenchmarkSettings.SqlClientConnectionString);
        using (var command = new SqlCommand($"DROP TABLE IF EXISTS {TargetTable}; CREATE TABLE {TargetTable} ({BenchmarkData.Columns})", _sqlConnection))
        {
            command.ExecuteNonQuery();
        }

        switch (Driver)
        {
            case "SqlClient":
                _table = new DataTable();
                using (var adapter = new SqlDataAdapter($"SELECT * FROM {source}", _sqlConnection))
                {
                    adapter.Fill(_table);
                }
                break;
            case "Go":
                _adbcDriver = CAdbcDriverImporter.Load(BenchmarkSettings.GoDriverPath!);
                _adbcDatabase = _adbcDriver.Open(new Dictionary<string, string> { ["uri"] = BenchmarkSettings.GoDriverUri });
                _adbcConnection = ServerPlacement.OpenAdbc(_adbcDatabase);
                LoadBatches(source);
                // The Go driver cannot ingest uint8 (its own mapping for tinyint), so widen it.
                _batches = _batches.Select(WidenUInt8).ToList();
                break;
            case "Clast":
                _adbcDriver = new SqlServerDriver();
                _adbcDatabase = _adbcDriver.Open(BenchmarkSettings.ClastOptions);
                _adbcConnection = ServerPlacement.OpenAdbc(_adbcDatabase);
                LoadBatches(source);
                break;
        }
    }

    /// <summary>Reads the source table into memory with this repository's driver.</summary>
    private void LoadBatches(string source)
    {
        using var driver = new SqlServerDriver();
        using AdbcDatabase database = driver.Open(BenchmarkSettings.ClastOptions);
        using AdbcConnection connection = database.Connect(null);
        using AdbcStatement statement = connection.CreateStatement();
        statement.SqlQuery = $"SELECT * FROM {source}";
        using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
        while (stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is RecordBatch batch)
        {
            _batches.Add(batch);
        }
    }

    private static RecordBatch WidenUInt8(RecordBatch batch)
    {
        var fields = new List<Field>();
        var arrays = new List<IArrowArray>();
        for (int i = 0; i < batch.ColumnCount; i++)
        {
            Field field = batch.Schema.FieldsList[i];
            IArrowArray array = batch.Column(i);
            if (array is UInt8Array bytes)
            {
                var builder = new Int16Array.Builder();
                for (int r = 0; r < bytes.Length; r++)
                {
                    byte? value = bytes.GetValue(r);
                    if (value.HasValue)
                    {
                        builder.Append(value.Value);
                    }
                    else
                    {
                        builder.AppendNull();
                    }
                }
                array = builder.Build();
                field = new Field(field.Name, array.Data.DataType, field.IsNullable);
            }
            fields.Add(field);
            arrays.Add(array);
        }
        return new RecordBatch(new Schema(fields, null), arrays, batch.Length);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (RecordBatch batch in _batches)
        {
            batch.Dispose();
        }
        _batches.Clear();
        _table?.Dispose();
        _adbcConnection?.Dispose();
        _adbcDatabase?.Dispose();
        _adbcDriver?.Dispose();
        _sqlConnection?.Dispose();
    }

    [Benchmark]
    public long Ingest()
    {
        using (var truncate = new SqlCommand($"TRUNCATE TABLE {TargetTable}", _sqlConnection))
        {
            truncate.ExecuteNonQuery();
        }

        if (Driver == "SqlClient")
        {
            var options = TableLock ? SqlBulkCopyOptions.TableLock : SqlBulkCopyOptions.Default;
            using var bulkCopy = new SqlBulkCopy(_sqlConnection!, options, null)
            {
                DestinationTableName = TargetTable,
                BulkCopyTimeout = 0,
                BatchSize = BatchSize,
            };
            bulkCopy.WriteToServer(_table!);
            return _table!.Rows.Count;
        }

        using AdbcStatement statement = _adbcConnection!.CreateStatement();
        statement.SetOption(AdbcOptions.Ingest.TargetTable, TargetName);
        statement.SetOption(AdbcOptions.Ingest.TargetDbSchema, "dbo");
        statement.SetOption(AdbcOptions.Ingest.Mode, AdbcOptions.IngestMode.Append);
        if (TableLock && Driver == "Clast")
        {
            statement.SetOption(SqlServerParameters.IngestTableLock, "true");
        }
        if (BatchSize > 0 && Driver == "Clast")
        {
            statement.SetOption(SqlServerParameters.IngestBatchSize, BatchSize.ToString(CultureInfo.InvariantCulture));
        }
        statement.BindStream(new BatchListStream(_batches));
        return statement.ExecuteUpdate().AffectedRows;
    }

    /// <summary>Hands out non-owning slices so the cached batches survive each iteration.</summary>
    private sealed class BatchListStream : IArrowArrayStream
    {
        private readonly List<RecordBatch> _batches;
        private int _next;

        public BatchListStream(List<RecordBatch> batches)
        {
            _batches = batches;
        }

        public Schema Schema => _batches[0].Schema;

        public ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
        {
            RecordBatch? batch = _next < _batches.Count ? _batches[_next].SliceShared(0, _batches[_next].Length) : null;
            _next++;
            return new ValueTask<RecordBatch?>(batch);
        }

        public void Dispose()
        {
        }
    }
}
