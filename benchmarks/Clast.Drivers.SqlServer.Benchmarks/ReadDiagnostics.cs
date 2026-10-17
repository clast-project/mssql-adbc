// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using BenchmarkDotNet.Attributes;
using Clast.Drivers.SqlServer.Tds;

namespace Clast.Drivers.SqlServer.Benchmarks;

/// <summary>
/// Breaks down this driver's read cost: draining the token stream without
/// decoding (transport, TLS and framing only) versus full Arrow decoding,
/// across encryption modes and packet sizes. Not a comparison with other drivers.
/// </summary>
[MemoryDiagnoser]
public class ReadDiagnostics
{
    private AdbcDriver? _driver;
    private AdbcDatabase? _database;
    private AdbcConnection? _connection;
    private string _query = "";

    [Params(1_000_000)]
    public int Rows { get; set; }

    [Params("optional", "mandatory")]
    public string Encrypt { get; set; } = "";

    [Params(8000, 32767)]
    public int PacketSize { get; set; }

    [Params("Drain", "Decode")]
    public string Mode { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        BenchmarkData.EnsureTable(Rows);
        _query = $"SELECT * FROM {BenchmarkData.TableName(Rows)}";
        var options = new Dictionary<string, string>(BenchmarkSettings.ClastOptions)
        {
            [SqlServerParameters.Encrypt] = Encrypt,
            [SqlServerParameters.PacketSize] = PacketSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        _driver = new SqlServerDriver();
        _database = _driver.Open(options);
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
    public long Read()
    {
        if (Mode == "Drain")
        {
            TdsSession session = ((SqlServerConnection)_connection!).Session;
            session.SendSqlBatch(_query);
            session.Reader.BeginMessage();
            TdsColumn[] columns = session.ReadUntilResultSet()!;
            return session.DrainResponse(columns);
        }

        using AdbcStatement statement = _connection!.CreateStatement();
        statement.SqlQuery = _query;
        using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
        long rows = 0;
        while (stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is RecordBatch batch)
        {
            rows += batch.Length;
            batch.Dispose();
        }
        return rows;
    }
}
