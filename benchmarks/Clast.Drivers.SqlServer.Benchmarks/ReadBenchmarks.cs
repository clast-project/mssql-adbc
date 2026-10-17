// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Data;
using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Adbc.C;
using Apache.Arrow.Ipc;
using BenchmarkDotNet.Attributes;
using Microsoft.Data.SqlClient;

namespace Clast.Drivers.SqlServer.Benchmarks;

/// <summary>
/// Full-table reads of every prototype type.
/// </summary>
[MemoryDiagnoser]
public class ReadBenchmarks
{
    private AdbcDriver? _adbcDriver;
    private AdbcDatabase? _adbcDatabase;
    private AdbcConnection? _adbcConnection;
    private SqlConnection? _sqlConnection;
    private string _query = "";

    [Params(1_000_000)]
    public int Rows { get; set; }

    [ParamsSource(nameof(Drivers))]
    public string Driver { get; set; } = "";

    /// <summary>"full" (whole-session TLS, the default) or "login" (login-only TLS).</summary>
    [Params("full", "login")]
    public string Encryption { get; set; } = "";

    public static IEnumerable<string> Drivers()
    {
        yield return "SqlClient";
        if (BenchmarkSettings.GoDriverPath != null)
        {
            yield return "Go";
        }
        if (BenchmarkSettings.ClastReadsEnabled)
        {
            yield return "Clast";
        }
    }

    [GlobalSetup]
    public void Setup()
    {
        BenchmarkData.EnsureTable(Rows);
        _query = $"SELECT * FROM {BenchmarkData.TableName(Rows)}";

        switch (Driver)
        {
            case "SqlClient":
                _sqlConnection = ServerPlacement.OpenSqlClient(BenchmarkSettings.SqlClientConnectionStringFor(Encryption));
                break;
            case "Go":
                _adbcDriver = CAdbcDriverImporter.Load(BenchmarkSettings.GoDriverPath!);
                _adbcDatabase = _adbcDriver.Open(new Dictionary<string, string> { ["uri"] = BenchmarkSettings.GoDriverUriFor(Encryption) });
                _adbcConnection = ServerPlacement.OpenAdbc(_adbcDatabase);
                break;
            case "Clast":
                _adbcDriver = new SqlServerDriver();
                _adbcDatabase = _adbcDriver.Open(BenchmarkSettings.ClastOptionsFor(Encryption));
                _adbcConnection = ServerPlacement.OpenAdbc(_adbcDatabase);
                break;
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _sqlConnection?.Dispose();
        _adbcConnection?.Dispose();
        _adbcDatabase?.Dispose();
        _adbcDriver?.Dispose();
    }

    [Benchmark]
    public async Task<long> ReadAll()
    {
        return Driver == "SqlClient" ? ReadSqlClient() : await ReadAdbc();
    }

    private async Task<long> ReadAdbc()
    {
        using AdbcStatement statement = _adbcConnection!.CreateStatement();
        statement.SqlQuery = _query;
        QueryResult result = statement.ExecuteQuery();
        using IArrowArrayStream stream = result.Stream!;

        long rows = 0;
        while (await stream.ReadNextRecordBatchAsync() is RecordBatch batch)
        {
            rows += batch.Length;
            batch.Dispose();
        }
        return rows;
    }

    /// <summary>
    /// Reads every value with its typed getter, which avoids boxing; the values
    /// are discarded apart from a cheap fold that keeps the reads from being elided.
    /// </summary>
    private long ReadSqlClient()
    {
        using SqlCommand command = new SqlCommand(_query, _sqlConnection) { CommandTimeout = 0 };
        using SqlDataReader reader = command.ExecuteReader(CommandBehavior.SequentialAccess);

        long rows = 0;
        long sink = 0;
        byte[] binary = new byte[16];
        while (reader.Read())
        {
            rows++;
            sink += reader.GetInt64(0);
            if (!reader.IsDBNull(1)) sink += reader.GetByte(1);
            if (!reader.IsDBNull(2)) sink += reader.GetInt16(2);
            if (!reader.IsDBNull(3)) sink += reader.GetInt32(3);
            if (!reader.IsDBNull(4)) sink += reader.GetInt64(4);
            if (!reader.IsDBNull(5)) sink += reader.GetBoolean(5) ? 1 : 0;
            if (!reader.IsDBNull(6)) sink += (long)reader.GetFloat(6);
            if (!reader.IsDBNull(7)) sink += (long)reader.GetDouble(7);
            if (!reader.IsDBNull(8)) sink += reader.GetSqlDecimal(8).Scale;
            if (!reader.IsDBNull(9)) sink += reader.GetSqlDecimal(9).Scale;
            if (!reader.IsDBNull(10)) sink += reader.GetString(10).Length;
            if (!reader.IsDBNull(11)) sink += reader.GetString(11).Length;
            if (!reader.IsDBNull(12)) sink += reader.GetBytes(12, 0, binary, 0, binary.Length);
            if (!reader.IsDBNull(13)) sink += reader.GetDateTime(13).Ticks;
            if (!reader.IsDBNull(14)) sink += reader.GetDateTime(14).Ticks;
            if (!reader.IsDBNull(15)) sink += reader.GetDateTimeOffset(15).Ticks;
            if (!reader.IsDBNull(16)) sink += reader.GetGuid(16).GetHashCode();
        }
        return rows + (sink & 1);
    }
}
