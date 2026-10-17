// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Microsoft.Data.SqlClient;

namespace Clast.Drivers.SqlServer.Benchmarks;

/// <summary>
/// Controls which server CPU a measured session runs on. SQL Server keeps a
/// connection's requests on one scheduler, and on a hybrid CPU with soft-NUMA
/// that choice (P-core or E-core, and whether it shares a core with a log
/// writer or with the client) changes timings by up to 2x, which swamps any
/// difference between drivers. When <c>MSSQL_BENCH_SERVER_CPU</c> is set,
/// connections are reopened until one lands on that CPU.
/// </summary>
/// <remarks>
/// An idle server gives new connections the first scheduler of each soft-NUMA
/// node in turn, so only those CPUs are reachable (on the reference machine,
/// CPU 16, an E-core, and CPU 7, a P-core that hosts a log writer).
/// </remarks>
internal static class ServerPlacement
{
    public static readonly int? ServerCpu =
        int.TryParse(Environment.GetEnvironmentVariable("MSSQL_BENCH_SERVER_CPU"), out int cpu) ? cpu : null;

    private const string CpuQuery =
        "SELECT s.cpu_id FROM sys.dm_exec_requests r JOIN sys.dm_os_schedulers s ON s.scheduler_id = r.scheduler_id WHERE r.session_id = @@SPID";

    public static AdbcConnection OpenAdbc(AdbcDatabase database) =>
        OpenPlaced(() => database.Connect(null), connection => Convert.ToInt32(AdbcScalar(connection, CpuQuery)));

    public static SqlConnection OpenSqlClient(string connectionString) => OpenPlaced(() =>
    {
        var connection = new SqlConnection(connectionString);
        connection.Open();
        return connection;
    }, connection => (int)new SqlCommand(CpuQuery, connection).ExecuteScalar());

    private static T OpenPlaced<T>(Func<T> open, Func<T, int> cpuOf) where T : IDisposable
    {
        var rejected = new List<T>();
        try
        {
            for (int attempt = 0; attempt < 200; attempt++)
            {
                T connection = open();
                if (ServerCpu == null || cpuOf(connection) == ServerCpu)
                {
                    return connection;
                }
                rejected.Add(connection);
            }
            throw new InvalidOperationException($"No connection landed on server CPU {ServerCpu}");
        }
        finally
        {
            rejected.ForEach(r => r.Dispose());
        }
    }

    private static object? AdbcScalar(AdbcConnection connection, string sql)
    {
        using AdbcStatement statement = connection.CreateStatement();
        statement.SqlQuery = sql;
        using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
        object? value = null;
        while (stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is RecordBatch batch)
        {
            using (batch)
            {
                if (value == null && batch.Length > 0)
                {
                    value = batch.Column(0) switch
                    {
                        Int32Array a => a.GetValue(0),
                        Int64Array a => a.GetValue(0),
                        Int16Array a => a.GetValue(0),
                        _ => throw new InvalidOperationException($"Unexpected type {batch.Column(0).Data.DataType}"),
                    };
                }
            }
        }
        return value;
    }
}
