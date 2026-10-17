// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Xunit;

namespace Clast.Drivers.SqlServer.Tests;

/// <summary>Autocommit, Commit/Rollback, isolation level and the other standard connection options.</summary>
public sealed class TransactionTests : IDisposable
{
    private readonly string _table = "tempdb.dbo.clast_tx_" + Guid.NewGuid().ToString("N").Substring(0, 8);
    private readonly List<AdbcConnection> _connections = new();

    public TransactionTests()
    {
        using AdbcConnection setup = Connect();
        Execute(setup, $"CREATE TABLE {_table} (v int NOT NULL)");
    }

    public void Dispose()
    {
        foreach (AdbcConnection connection in _connections)
        {
            connection.Dispose();
        }
        using AdbcConnection cleanup = Connect(track: false);
        Execute(cleanup, $"DROP TABLE IF EXISTS {_table}");
    }

    private AdbcConnection Connect(Action<Dictionary<string, string>>? configure = null, bool track = true)
    {
        SqlServerTestConfiguration config = SqlServerTestConfiguration.Load();
        var options = new Dictionary<string, string>(config.Options);
        configure?.Invoke(options);
        AdbcConnection connection = new SqlServerDriver().Open(options).Connect(null);
        if (track)
        {
            _connections.Add(connection);
        }
        return connection;
    }

    private static void Execute(AdbcConnection connection, string sql)
    {
        using AdbcStatement statement = connection.CreateStatement();
        statement.SqlQuery = sql;
        statement.ExecuteUpdate();
    }

    private static object? Scalar(AdbcConnection connection, string sql)
    {
        using AdbcStatement statement = connection.CreateStatement();
        statement.SqlQuery = sql;
        using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
        using RecordBatch batch = stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult()!;
        return batch.Column(0) switch
        {
            Int32Array a => a.GetValue(0),
            StringArray s => s.GetString(0),
            _ => throw new InvalidOperationException(),
        };
    }

    private int Count(AdbcConnection connection) => (int)Scalar(connection, $"SELECT COUNT(*) FROM {_table}")!;

    [SkippableFact]
    public void AutocommitIsOnByDefault()
    {
        AdbcConnection connection = Connect();
        Assert.True(connection.AutoCommit);
        Assert.Equal("true", connection.GetOption(AdbcOptions.Connection.Autocommit));
        Assert.Equal(AdbcStatusCode.InvalidState, Assert.Throws<AdbcException>(() => connection.Commit()).Status);
        Assert.Equal(AdbcStatusCode.InvalidState, Assert.Throws<AdbcException>(() => connection.Rollback()).Status);
    }

    [SkippableFact]
    public void RollbackAndCommit()
    {
        AdbcConnection connection = Connect();
        AdbcConnection observer = Connect();
        connection.SetOption(AdbcOptions.Connection.Autocommit, "false");
        Assert.Equal("false", connection.GetOption(AdbcOptions.Connection.Autocommit));

        Execute(connection, $"INSERT INTO {_table} VALUES (1)");
        Assert.Equal(1, Count(connection));
        connection.Rollback();
        Assert.Equal(0, Count(connection));

        // A new transaction starts with the next statement.
        Execute(connection, $"INSERT INTO {_table} VALUES (2)");
        connection.Commit();
        Assert.Equal(1, Count(observer));

        Execute(connection, $"INSERT INTO {_table} VALUES (3)");
        connection.Rollback();
        Assert.Equal(1, Count(observer));
        Assert.Equal(1, Count(connection));
    }

    [SkippableFact]
    public void EnablingAutocommitCommits()
    {
        AdbcConnection connection = Connect(o => o[AdbcOptions.Connection.Autocommit] = "false");
        Assert.False(connection.AutoCommit);
        Execute(connection, $"INSERT INTO {_table} VALUES (1)");
        connection.AutoCommit = true;
        Assert.Equal(1, Count(Connect()));

        // Now in autocommit mode: each statement commits on its own.
        Execute(connection, $"INSERT INTO {_table} VALUES (2)");
        Assert.Equal(2, Count(Connect()));
    }

    [SkippableFact]
    public void IngestRollsBack()
    {
        AdbcConnection connection = Connect();
        connection.AutoCommit = false;
        string name = _table.Split('.')[2] + "_ingest";
        var schema = new Schema([new Field("v", Int32Type.Default, false)], null);
        using (AdbcStatement ingest = connection.BulkIngest("tempdb", "dbo", name, BulkIngestMode.Create, isTemporary: false))
        {
            var values = new Int32Array.Builder().AppendRange(Enumerable.Range(0, 100)).Build();
            ingest.Bind(new RecordBatch(schema, [values], 100), schema);
            Assert.Equal(100, ingest.ExecuteUpdate().AffectedRows);
        }
        Assert.Equal(100, (int)Scalar(connection, $"SELECT COUNT(*) FROM tempdb.dbo.{name}")!);
        connection.Rollback();

        // The CREATE TABLE was part of the transaction too.
        Assert.Equal("missing", Scalar(connection, $"SELECT CASE WHEN OBJECT_ID(N'tempdb.dbo.{name}') IS NULL THEN N'missing' ELSE N'present' END"));
    }

    [SkippableFact]
    public void IsolationLevel()
    {
        AdbcConnection connection = Connect();
        Assert.Equal(Apache.Arrow.Adbc.IsolationLevel.ReadCommitted, connection.IsolationLevel);

        connection.IsolationLevel = Apache.Arrow.Adbc.IsolationLevel.Serializable;
        Assert.Equal(Apache.Arrow.Adbc.IsolationLevel.Serializable, connection.IsolationLevel);
        Assert.Equal(AdbcOptions.IsolationLevels.Serializable, connection.GetOption(AdbcOptions.Connection.IsolationLevel));

        connection.SetOption(AdbcOptions.Connection.IsolationLevel, AdbcOptions.IsolationLevels.Snapshot);
        Assert.Equal(Apache.Arrow.Adbc.IsolationLevel.Snapshot, connection.IsolationLevel);

        connection.SetOption(AdbcOptions.Connection.IsolationLevel, AdbcOptions.IsolationLevels.Default);
        Assert.Equal(Apache.Arrow.Adbc.IsolationLevel.ReadCommitted, connection.IsolationLevel);

        Assert.Equal(AdbcStatusCode.NotImplemented, Assert.Throws<AdbcException>(() => connection.IsolationLevel = Apache.Arrow.Adbc.IsolationLevel.Linearizable).Status);
        Assert.Equal(AdbcStatusCode.InvalidArgument, Assert.Throws<AdbcException>(() => connection.SetOption(AdbcOptions.Connection.IsolationLevel, "bogus")).Status);
    }

    [SkippableFact]
    public void ParallelHelpersUseTheIsolationLevel()
    {
        AdbcConnection connection = Connect();
        connection.IsolationLevel = Apache.Arrow.Adbc.IsolationLevel.Serializable;
        using AdbcStatement statement = connection.CreateStatement();
        statement.SqlQuery = """
            SELECT CAST(n AS bigint) AS k, (SELECT CAST(transaction_isolation_level AS int) FROM sys.dm_exec_sessions WHERE session_id = @@SPID) AS level
            FROM (SELECT TOP (4000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects a CROSS JOIN sys.all_objects b) AS t
            """;
        statement.SetOption(SqlServerParameters.PartitionColumn, "k");
        statement.SetOption(SqlServerParameters.ParallelReads, "4");
        using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
        Assert.IsType<SqlServerParallelQueryStream>(stream);
        var levels = new HashSet<int>();
        while (stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is RecordBatch batch)
        {
            using (batch)
            {
                var column = (Int32Array)batch.Column(1);
                for (int i = 0; i < batch.Length; i++)
                {
                    levels.Add(column.GetValue(i)!.Value);
                }
            }
        }
        Assert.Equal([4], levels);
    }

    [SkippableFact]
    public void CurrentCatalogAndSchema()
    {
        AdbcConnection connection = Connect();
        Assert.Equal("master", connection.CurrentCatalog);
        Assert.Equal("dbo", connection.CurrentDbSchema);
        Assert.Equal("dbo", connection.GetOption(AdbcOptions.Connection.CurrentDbSchema));

        connection.SetOption(AdbcOptions.Connection.CurrentCatalog, "tempdb");
        Assert.Equal("tempdb", connection.GetOption(AdbcOptions.Connection.CurrentCatalog));
        Assert.Equal("tempdb", Scalar(connection, "SELECT DB_NAME()"));

        Assert.Equal(AdbcStatusCode.NotImplemented, Assert.Throws<AdbcException>(() => connection.CurrentDbSchema = "sys").Status);
        Assert.ThrowsAny<AdbcException>(() => connection.CurrentCatalog = "no_such_database_" + Guid.NewGuid().ToString("N"));
        Assert.Equal("tempdb", connection.CurrentCatalog);
    }

    [SkippableFact]
    public void DatabaseAndStatementOptionsCanBeRead()
    {
        SqlServerTestConfiguration config = SqlServerTestConfiguration.Load();
        using AdbcDatabase database = new SqlServerDriver().Open(new Dictionary<string, string>(config.Options) { [SqlServerParameters.Password] = "secret" });
        Assert.Equal(config.Options[SqlServerParameters.Server], database.GetOption(SqlServerParameters.Server));
        Assert.Equal(AdbcStatusCode.NotFound, Assert.Throws<AdbcException>(() => database.GetOption(SqlServerParameters.Password)).Status);
        Assert.Equal(AdbcStatusCode.NotFound, Assert.Throws<AdbcException>(() => database.GetOption("adbc.mssql.no_such_option")).Status);

        using AdbcStatement statement = Connect().BulkIngest("t", BulkIngestMode.Append);
        Assert.Equal("t", statement.GetOption(AdbcOptions.Ingest.TargetTable));
        Assert.Equal(AdbcOptions.IngestMode.Append, statement.GetOption(AdbcOptions.Ingest.Mode));
        Assert.Equal("false", statement.GetOption(AdbcOptions.Ingest.Temporary));
        statement.SetOption(SqlServerParameters.ParallelReads, "3");
        Assert.Equal(3, statement.GetOptionInt64(SqlServerParameters.ParallelReads));
        Assert.Equal(AdbcStatusCode.NotFound, Assert.Throws<AdbcException>(() => statement.GetOption(SqlServerParameters.PartitionColumn)).Status);
    }

    [SkippableFact]
    public void ReadOnlyIsNotSupported()
    {
        AdbcConnection connection = Connect();
        Assert.Equal("false", connection.GetOption(AdbcOptions.Connection.ReadOnly));
        connection.SetOption(AdbcOptions.Connection.ReadOnly, "false");
        Assert.Equal(AdbcStatusCode.NotImplemented, Assert.Throws<AdbcException>(() => connection.SetOption(AdbcOptions.Connection.ReadOnly, "true")).Status);
        Assert.Equal(AdbcStatusCode.InvalidArgument, Assert.Throws<AdbcException>(() => connection.SetOption(AdbcOptions.Connection.Autocommit, "maybe")).Status);
    }
}
