// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using BenchmarkDotNet.Attributes;

namespace Clast.Drivers.SqlServer.Benchmarks;

/// <summary>
/// A serial read with adbc.mssql.read_ahead, against a consumer that spends a
/// fixed time per row on each batch (as an application encoding or
/// compressing it would), so decoding can overlap the consumer's work.
/// </summary>
[MemoryDiagnoser]
public class ReadAheadBenchmarks
{
    private AdbcDriver? _driver;
    private AdbcDatabase? _database;
    private AdbcConnection? _connection;
    private string _query = "";

    [Params(1_000_000)]
    public int Rows { get; set; }

    [Params(0, 1, 2)]
    public int ReadAhead { get; set; }

    /// <summary>The consumer's time per row, spent spinning after each batch arrives.</summary>
    [Params(0, 500, 2000)]
    public int ConsumerNanosPerRow { get; set; }

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
        statement.SetOption(SqlServerParameters.ReadAhead, ReadAhead.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;

        long rows = 0;
        while (await stream.ReadNextRecordBatchAsync() is RecordBatch batch)
        {
            rows += batch.Length;
            Consume(batch.Length);
            batch.Dispose();
        }
        return rows;
    }

    private void Consume(int rows)
    {
        if (ConsumerNanosPerRow == 0)
        {
            return;
        }
        long until = Stopwatch.GetTimestamp() + (long)rows * ConsumerNanosPerRow * Stopwatch.Frequency / 1_000_000_000;
        while (Stopwatch.GetTimestamp() < until)
        {
            Thread.SpinWait(20);
        }
    }
}
