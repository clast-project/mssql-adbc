// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using BenchmarkDotNet.Attributes;

namespace Clast.Drivers.SqlServer.Benchmarks;

/// <summary>
/// An ordinary ExecuteQuery on one connection with adbc.mssql.parallel_reads:
/// the driver partitions on <c>id</c> and merges the partitions into one
/// stream. Helper sessions are opened during warmup and reused, as they would
/// be by a long-lived connection.
/// </summary>
[MemoryDiagnoser]
public class ParallelStreamBenchmarks
{
    private AdbcDriver? _driver;
    private AdbcDatabase? _database;
    private AdbcConnection? _connection;
    private string _query = "";

    [Params(1_000_000)]
    public int Rows { get; set; }

    [Params(1, 2, 4, 8, 16)]
    public int ParallelReads { get; set; }

    [Params("full")]
    public string Encryption { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        BenchmarkData.EnsureTable(Rows);
        _query = $"SELECT * FROM {BenchmarkData.TableName(Rows)}";
        _driver = new SqlServerDriver();
        _database = _driver.Open(BenchmarkSettings.ClastOptionsFor(Encryption));
        _connection = _database.Connect(null);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _connection?.Dispose();
        _database?.Dispose();
        _driver?.Dispose();
    }

    [Benchmark]
    public async Task<long> ReadAll()
    {
        using AdbcStatement statement = _connection!.CreateStatement();
        statement.SqlQuery = _query;
        statement.SetOption(SqlServerParameters.PartitionColumn, "id");
        statement.SetOption(SqlServerParameters.ParallelReads, ParallelReads.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;

        long rows = 0;
        while (await stream.ReadNextRecordBatchAsync() is RecordBatch batch)
        {
            rows += batch.Length;
            batch.Dispose();
        }
        return rows;
    }
}
