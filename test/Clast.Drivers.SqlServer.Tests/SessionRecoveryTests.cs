// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Clast.Drivers.SqlServer.Arrow;
using Clast.Drivers.SqlServer.Tds;
using Xunit;

namespace Clast.Drivers.SqlServer.Tests;

/// <summary>
/// Session recovery: a session whose connection the server closes while it
/// is idle (here with KILL, from another connection) is recreated on a new
/// connection before the next request.
/// </summary>
public sealed class SessionRecoveryTests
{
    private static AdbcConnection Connect(Action<Dictionary<string, string>>? configure = null)
    {
        var options = new Dictionary<string, string>(SqlServerTestConfiguration.Load().Options);
        configure?.Invoke(options);
        return new SqlServerDriver().Open(options).Connect(null);
    }

    private static object?[] Row(AdbcConnection connection, string sql)
    {
        using AdbcStatement statement = connection.CreateStatement();
        statement.SqlQuery = sql;
        using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
        object?[]? row = null;
        while (stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is RecordBatch batch)
        {
            using (batch)
            {
                if (row == null && batch.Length > 0)
                {
                    row = Enumerable.Range(0, batch.ColumnCount).Select(c => ResultArrays.Value(batch.Column(c), 0)).ToArray();
                }
            }
        }
        return row ?? throw new InvalidOperationException("No rows");
    }

    private static void Execute(AdbcConnection connection, string sql)
    {
        using AdbcStatement statement = connection.CreateStatement();
        statement.SqlQuery = sql;
        statement.ExecuteUpdate();
    }

    private static int Spid(AdbcConnection connection) => Convert.ToInt32(Row(connection, "SELECT @@SPID")[0]);

    private static object? ConnectionId(AdbcConnection connection) =>
        Row(connection, "SELECT CAST(connection_id AS nvarchar(36)) FROM sys.dm_exec_connections WHERE session_id = @@SPID")[0];

    /// <summary>Kills the connection's session from another connection, and waits until its socket shows it.</summary>
    private static void Kill(AdbcConnection connection)
    {
        int spid = Spid(connection);
        using (AdbcConnection killer = Connect())
        {
            Execute(killer, $"KILL {spid}");
        }
        TdsSession session = ((SqlServerConnection)connection).Session;
        var watch = Stopwatch.StartNew();
        while (session.IsConnectionAlive() && watch.Elapsed < TimeSpan.FromSeconds(5))
        {
            Thread.Sleep(10);
        }
        Assert.False(session.IsConnectionAlive(), "The killed session's socket still looks open");
    }

    [SkippableTheory]
    [InlineData(null)]
    [InlineData("strict")]
    public void RecoversTheStateOfAKilledSession(string? encrypt)
    {
        SqlServerTestConfiguration config = SqlServerTestConfiguration.Load();
        Skip.If(encrypt == "strict" && !config.SupportsStrictEncryption, "The server has no certificate for strict encryption");
        using AdbcConnection connection = Connect(o =>
        {
            if (encrypt != null)
            {
                o[SqlServerParameters.Encrypt] = encrypt;
            }
        });
        Execute(connection, "USE tempdb; SET LANGUAGE Deutsch; SET TEXTSIZE 12345; SET ANSI_NULLS OFF; SET QUOTED_IDENTIFIER OFF");
        connection.IsolationLevel = IsolationLevel.Serializable;
        object? before = ConnectionId(connection);
        TdsSession original = ((SqlServerConnection)connection).Session;

        Kill(connection);

        object?[] row = Row(connection, """
            SELECT @@SPID, DB_NAME(), @@LANGUAGE, @@TEXTSIZE,
                   CAST(SESSIONPROPERTY('ANSI_NULLS') AS int), CAST(SESSIONPROPERTY('QUOTED_IDENTIFIER') AS int),
                   CAST((SELECT transaction_isolation_level FROM sys.dm_exec_sessions WHERE session_id = @@SPID) AS int)
            """);
        // A new connection (its session ID may be reused), with the same state.
        Assert.NotEqual(before, ConnectionId(connection));
        Assert.Equal("tempdb", row[1]);
        Assert.Equal("Deutsch", row[2]);
        Assert.Equal(12345, Convert.ToInt32(row[3]));
        Assert.Equal(0, Convert.ToInt32(row[4]));
        Assert.Equal(0, Convert.ToInt32(row[5]));
        Assert.Equal(4, Convert.ToInt32(row[6])); // SERIALIZABLE
        Assert.NotSame(original, ((SqlServerConnection)connection).Session);
        Assert.Equal("tempdb", connection.CurrentCatalog);
        Assert.Equal(IsolationLevel.Serializable, connection.IsolationLevel);
    }

    [SkippableFact]
    public void RecoversMoreThanOnce()
    {
        using AdbcConnection connection = Connect();
        Execute(connection, "SET TEXTSIZE 4321");
        Kill(connection);
        Assert.Equal(4321, Convert.ToInt32(Row(connection, "SELECT @@TEXTSIZE")[0]));
        Execute(connection, "USE tempdb");
        Kill(connection);
        object?[] row = Row(connection, "SELECT @@TEXTSIZE, DB_NAME()");
        Assert.Equal(4321, Convert.ToInt32(row[0]));
        Assert.Equal("tempdb", row[1]);
    }

    [SkippableFact]
    public void ParallelReadsWorkAfterRecovery()
    {
        using AdbcConnection connection = Connect();
        using (AdbcStatement warm = connection.CreateStatement())
        {
            // Rent and return helper sessions, which the kill below leaves behind.
            warm.SqlQuery = "SELECT TOP (1000) CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS int) AS n FROM sys.all_columns";
            warm.SetOption(SqlServerParameters.PartitionColumn, "n");
            warm.SetOption(SqlServerParameters.ParallelReads, "2");
            using IArrowArrayStream stream = warm.ExecuteQuery().Stream!;
            while (stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is RecordBatch batch)
            {
                batch.Dispose();
            }
        }
        Kill(connection);

        using AdbcStatement statement = connection.CreateStatement();
        statement.SqlQuery = "SELECT TOP (1000) CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS int) AS n FROM sys.all_columns";
        statement.SetOption(SqlServerParameters.PartitionColumn, "n");
        statement.SetOption(SqlServerParameters.ParallelReads, "2");
        using IArrowArrayStream result = statement.ExecuteQuery().Stream!;
        long rows = 0;
        while (result.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is RecordBatch batch)
        {
            rows += batch.Length;
            batch.Dispose();
        }
        Assert.Equal(1000, rows);
    }

    [SkippableFact]
    public void DoesNotRecoverASessionWithATemporaryTable()
    {
        using AdbcConnection connection = Connect();
        Execute(connection, "CREATE TABLE #kept (id int)");
        Kill(connection);
        AdbcException ex = Assert.Throws<AdbcException>(() => Row(connection, "SELECT COUNT(*) FROM #kept"));
        Assert.Equal(AdbcStatusCode.IOError, ex.Status);
        Assert.Contains("state the server cannot recover", ex.Message);
    }

    [SkippableFact]
    public void DoesNotRecoverInATransaction()
    {
        using AdbcConnection connection = Connect();
        connection.AutoCommit = false;
        Row(connection, "SELECT COUNT(*) FROM sys.objects WITH (HOLDLOCK)");
        Kill(connection);
        AdbcException ex = Assert.Throws<AdbcException>(() => Row(connection, "SELECT 1"));
        Assert.Equal(AdbcStatusCode.IOError, ex.Status);
        Assert.Contains("a transaction was open", ex.Message);
    }

    [SkippableFact]
    public void CanBeTurnedOff()
    {
        using AdbcConnection connection = Connect(o => o[SqlServerParameters.SessionRecovery] = "false");
        Kill(connection);
        AdbcException ex = Assert.Throws<AdbcException>(() => Row(connection, "SELECT 1"));
        Assert.Contains(SqlServerParameters.SessionRecovery, ex.Message);
    }

    /// <summary>
    /// The unread rest of a result makes the socket readable: that is a busy
    /// session, not a closed connection, so a second command is refused and
    /// the result can still be read to the end.
    /// </summary>
    [SkippableFact]
    public void AnOpenResultIsNotMistakenForAClosedConnection()
    {
        using AdbcConnection connection = Connect(o =>
        {
            o[SqlServerParameters.PacketSize] = "4096";
            o[SqlServerParameters.BatchRows] = "10";
        });
        TdsSession session = ((SqlServerConnection)connection).Session;
        using AdbcStatement statement = connection.CreateStatement();
        statement.SqlQuery = "SELECT TOP (5000) a.name, b.object_id FROM sys.all_columns a CROSS JOIN sys.objects b";
        using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
        using (RecordBatch first = stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult()!)
        {
            Assert.Equal(10, first.Length);
        }

        using AdbcStatement other = connection.CreateStatement();
        other.SqlQuery = "SELECT 1";
        Assert.Equal(AdbcStatusCode.InvalidState, Assert.ThrowsAny<AdbcException>(() => other.ExecuteQuery()).Status);
        Assert.Same(session, ((SqlServerConnection)connection).Session);

        long rows = 10;
        while (stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is RecordBatch batch)
        {
            rows += batch.Length;
            batch.Dispose();
        }
        Assert.Equal(5000, rows);
    }
}
