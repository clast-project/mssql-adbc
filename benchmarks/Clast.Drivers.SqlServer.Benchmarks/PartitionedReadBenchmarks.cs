// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using BenchmarkDotNet.Attributes;

namespace Clast.Drivers.SqlServer.Benchmarks;

/// <summary>
/// Reads the benchmark table with ExecutePartitioned split on <c>id</c>, each
/// partition read concurrently on its own connection (opened beforehand, as a
/// pool would). Partitions = 1 is the single-stream baseline including the
/// partitioning round trips.
/// </summary>
[MemoryDiagnoser]
public class PartitionedReadBenchmarks
{
    private AdbcDriver? _driver;
    private AdbcDatabase? _database;
    private AdbcConnection[] _connections = [];
    private string _query = "";

    [Params(1_000_000)]
    public int Rows { get; set; }

    [Params(1, 2, 4, 8, 16)]
    public int Partitions { get; set; }

    [Params("full")]
    public string Encryption { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        BenchmarkData.EnsureTable(Rows);
        _query = $"SELECT * FROM {BenchmarkData.TableName(Rows)}";
        _driver = new SqlServerDriver();
        _database = _driver.Open(BenchmarkSettings.ClastOptionsFor(Encryption));
        _connections = Enumerable.Range(0, Partitions).Select(_ => _database.Connect(null)).ToArray();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (AdbcConnection connection in _connections)
        {
            connection.Dispose();
        }
        _database?.Dispose();
        _driver?.Dispose();
    }

    [Benchmark]
    public long ReadPartitioned()
    {
        PartitionedResult result;
        using (AdbcStatement statement = _connections[0].CreateStatement())
        {
            statement.SqlQuery = _query;
            statement.SetOption(SqlServerParameters.PartitionColumn, "id");
            statement.SetOption(SqlServerParameters.PartitionCount, Partitions.ToString(System.Globalization.CultureInfo.InvariantCulture));
            result = statement.ExecutePartitioned();
        }

        var tasks = new Task<long>[result.PartitionDescriptors.Count];
        for (int i = 0; i < tasks.Length; i++)
        {
            AdbcConnection connection = _connections[i];
            PartitionDescriptor descriptor = result.PartitionDescriptors[i];
            tasks[i] = Task.Factory.StartNew(() => Read(connection, descriptor), TaskCreationOptions.LongRunning);
        }
        Task.WaitAll(tasks);
        return tasks.Sum(t => t.Result);
    }

    private static long Read(AdbcConnection connection, PartitionDescriptor descriptor)
    {
        using IArrowArrayStream stream = connection.ReadPartition(descriptor);
        long rows = 0;
        while (stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is RecordBatch batch)
        {
            rows += batch.Length;
            batch.Dispose();
        }
        return rows;
    }
}
