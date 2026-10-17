// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Clast.Drivers.SqlServer.Tds;

namespace Clast.Drivers.SqlServer;

/// <summary>
/// A TDS session, plus a pool of helper sessions used for parallel reads.
/// </summary>
public sealed class SqlServerConnection : AdbcConnection
{
    private readonly TdsSession _session;
    private readonly Stack<TdsSession> _helpers = new();
    private bool _disposed;
    private SqlServerParallelQueryStream? _parallelStream;
    private bool _autoCommit = true;

    internal SqlServerConnection(IReadOnlyDictionary<string, string> options)
    {
        _session = TdsSession.Connect(SqlServerConnectionSettings.Parse(options));
        try
        {
            // Standard connection options may also be given when connecting.
            foreach (string key in (string[])[AdbcOptions.Connection.Autocommit, AdbcOptions.Connection.IsolationLevel, AdbcOptions.Connection.ReadOnly])
            {
                if (options.TryGetValue(key, out string? value))
                {
                    SetOption(key, value);
                }
            }
        }
        catch
        {
            _session.Dispose();
            throw;
        }
    }

    internal TdsSession Session => _session;

    /// <summary>
    /// Throws if a parallel result stream is reading on this connection's
    /// session; its workers move between partitions, so the session can look
    /// idle while it is still in use.
    /// </summary>
    internal void EnsureIdle()
    {
        if (Volatile.Read(ref _parallelStream) != null)
        {
            throw new AdbcException("A parallel result stream is still open; read it to the end or dispose it first", AdbcStatusCode.InvalidState);
        }
    }

    internal void SetParallelStream(SqlServerParallelQueryStream? stream) => Volatile.Write(ref _parallelStream, stream);

    public override AdbcStatement CreateStatement()
    {
        return new SqlServerStatement(this);
    }

    public override AdbcStatement BulkIngest(string targetTableName, BulkIngestMode mode)
    {
        return BulkIngest(null, null, targetTableName, mode, isTemporary: false);
    }

    public override AdbcStatement BulkIngest(string? targetCatalog, string? targetDbSchema, string targetTableName, BulkIngestMode mode, bool isTemporary)
    {
        var statement = new SqlServerStatement(this);
        statement.ConfigureIngest(targetCatalog, targetDbSchema, targetTableName, mode, isTemporary);
        return statement;
    }

    public override IArrowArrayStream ReadPartition(PartitionDescriptor partition)
    {
        EnsureIdle();
        return ExecuteQueryStream(SqlServerPartitions.Decode(partition));
    }

    /// <summary>Sends a batch and returns a stream over its first result set.</summary>
    internal IArrowArrayStream ExecuteQueryStream(string sql) => SqlServerQueryStream.Execute(_session, sql);

    /// <summary>
    /// Returns <paramref name="count"/> idle helper sessions logged in with
    /// this connection's settings and using its current database, opening new
    /// ones as needed.
    /// </summary>
    internal TdsSession[] RentHelperSessions(int count)
    {
        var sessions = new List<TdsSession>(count);
        lock (_helpers)
        {
            while (sessions.Count < count && _helpers.TryPop(out TdsSession? helper))
            {
                sessions.Add(helper);
            }
        }

        try
        {
            int missing = count - sessions.Count;
            if (missing > 0)
            {
                Task<TdsSession>[] opening = Enumerable.Range(0, missing)
                    .Select(_ => Task.Run(() => TdsSession.Connect(_session.ConnectSettings)))
                    .ToArray();
                try
                {
                    Task.WaitAll(opening);
                }
                catch (AggregateException ex)
                {
                    foreach (Task<TdsSession> task in opening.Where(t => t.IsCompletedSuccessfully))
                    {
                        task.Result.Dispose();
                    }
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
                }
                sessions.AddRange(opening.Select(t => t.Result));
            }

            // Helpers read as this session would: in its current database and at its isolation level.
            foreach (TdsSession helper in sessions)
            {
                string sql = "";
                if (!string.Equals(helper.Database, _session.Database, StringComparison.Ordinal))
                {
                    sql = "USE " + IngestTarget.Quote(_session.Database) + "; ";
                }
                if (helper.IsolationLevel != _session.IsolationLevel)
                {
                    sql += "SET TRANSACTION ISOLATION LEVEL " + (_session.IsolationLevel ?? "READ COMMITTED");
                }
                if (sql.Length > 0)
                {
                    helper.ExecuteNonQuery(sql);
                    helper.IsolationLevel = _session.IsolationLevel;
                }
            }
        }
        catch
        {
            foreach (TdsSession helper in sessions)
            {
                helper.Dispose();
            }
            throw;
        }
        return sessions.ToArray();
    }

    /// <summary>Puts helper sessions back in the pool; broken or busy ones are closed.</summary>
    internal void ReturnHelperSessions(ReadOnlySpan<TdsSession> sessions)
    {
        lock (_helpers)
        {
            foreach (TdsSession helper in sessions)
            {
                if (helper.IsBroken || helper.ActiveStream != null || _disposed)
                {
                    helper.Dispose();
                }
                else
                {
                    _helpers.Push(helper);
                }
            }
        }
    }

    public override IArrowArrayStream GetInfo(IReadOnlyList<AdbcInfoCode> codes)
    {
        ArgumentNullException.ThrowIfNull(codes);
        return SqlServerMetadata.GetInfo(_session, codes);
    }

    public override IArrowArrayStream GetObjects(
        GetObjectsDepth depth,
        string? catalogPattern,
        string? dbSchemaPattern,
        string? tableNamePattern,
        IReadOnlyList<string>? tableTypes,
        string? columnNamePattern)
    {
        EnsureIdle();
        return SqlServerMetadata.GetObjects(_session, depth, catalogPattern, dbSchemaPattern, tableNamePattern, tableTypes, columnNamePattern);
    }

    public override Schema GetTableSchema(string? catalog, string? dbSchema, string tableName)
    {
        EnsureIdle();
        return SqlServerMetadata.GetTableSchema(_session, catalog, dbSchema, tableName);
    }

    public override IArrowArrayStream GetTableTypes() => SqlServerMetadata.GetTableTypes();

    public override IArrowArrayStream GetStatisticsNames() => SqlServerStatistics.GetStatisticsNames();

    /// <summary>
    /// Approximate: table row counts and per-column distinct and null counts
    /// from the server's statistics. Exact: table row counts only, by counting.
    /// </summary>
    public override IArrowArrayStream GetStatistics(string? catalogPattern, string? schemaPattern, string tableNamePattern, bool approximate)
    {
        EnsureIdle();
        return SqlServerStatistics.GetStatistics(_session, catalogPattern, schemaPattern, tableNamePattern, approximate);
    }

    // ---- Options and transactions ----

    /// <summary>
    /// Adds the server's Entra authority and audience (asked for once, with a
    /// probe handshake) and its engine edition to the standard connection options.
    /// </summary>
    public override string GetOption(string key)
    {
        if (key is SqlServerParameters.EntraAuthority or SqlServerParameters.EntraAudience)
        {
            EntraInfo info = _session.ConnectSettings.DiscoverEntra();
            return key == SqlServerParameters.EntraAuthority ? info.Authority : info.Audience;
        }
        if (key == SqlServerParameters.EngineEdition)
        {
            EnsureIdle();
            return SqlServerParameters.Format((int)SqlServerEngine.Edition(_session));
        }
        return base.GetOption(key);
    }

    public override void SetOption(string key, string value)
    {
        switch (key)
        {
            case AdbcOptions.Connection.Autocommit:
                AutoCommit = ParseEnabled(key, value);
                break;
            case AdbcOptions.Connection.ReadOnly:
                ReadOnly = ParseEnabled(key, value);
                break;
            case AdbcOptions.Connection.IsolationLevel:
                IsolationLevel level;
                try
                {
                    level = AdbcOptions.GetIsolationLevel(value);
                }
                catch (NotSupportedException)
                {
                    throw new AdbcException($"Invalid value '{value}' for option '{key}'", AdbcStatusCode.InvalidArgument);
                }
                IsolationLevel = level;
                break;
            case AdbcOptions.Connection.CurrentCatalog:
                EnsureIdle();
                _session.ExecuteNonQuery("USE " + IngestTarget.Quote(value));
                break;
            case AdbcOptions.Connection.CurrentDbSchema:
                throw AdbcException.NotImplemented("SQL Server has no session-level default schema; it is a property of the database user");
            default:
                throw AdbcException.NotImplemented($"Unknown connection option '{key}'");
        }
    }

    private static bool ParseEnabled(string key, string value) => value switch
    {
        AdbcOptions.Enabled => true,
        AdbcOptions.Disabled => false,
        _ => throw new AdbcException($"Invalid value '{value}' for option '{key}'", AdbcStatusCode.InvalidArgument),
    };

    /// <summary>
    /// With autocommit off, the session runs with IMPLICIT_TRANSACTIONS ON:
    /// the server opens a transaction at the first statement that needs one,
    /// and <see cref="Commit"/> or <see cref="Rollback"/> ends it. Turning
    /// autocommit back on commits any open transaction.
    /// </summary>
    public override bool AutoCommit
    {
        get => _autoCommit;
        set
        {
            if (value == _autoCommit)
            {
                return;
            }
            EnsureIdle();
            _session.ExecuteNonQuery(value
                ? "WHILE @@TRANCOUNT > 0 COMMIT TRANSACTION; SET IMPLICIT_TRANSACTIONS OFF"
                : "SET IMPLICIT_TRANSACTIONS ON");
            _autoCommit = value;
        }
    }

    public override void Commit() => EndTransaction("WHILE @@TRANCOUNT > 0 COMMIT TRANSACTION");

    public override void Rollback() => EndTransaction("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION");

    private void EndTransaction(string sql)
    {
        if (_autoCommit)
        {
            throw new AdbcException("There is no transaction to end while autocommit is enabled", AdbcStatusCode.InvalidState);
        }
        EnsureIdle();
        _session.ExecuteNonQuery(sql);
    }

    /// <summary>SQL Server has no read-only session mode; only false is accepted.</summary>
    public override bool ReadOnly
    {
        get => false;
        set
        {
            if (value)
            {
                throw AdbcException.NotImplemented("SQL Server does not support read-only connections");
            }
        }
    }

    /// <summary>
    /// The session's isolation level, as the server reports it. Synapse
    /// dedicated pools and Fabric Warehouse run every transaction at one level
    /// (see <see cref="FixedIsolationLevel"/>); there only that level, or the
    /// default, can be set.
    /// </summary>
    public override IsolationLevel IsolationLevel
    {
        get
        {
            EnsureIdle();
            if (FixedIsolationLevel() is IsolationLevel fixedLevel)
            {
                return fixedLevel;
            }
            object? level = QueryScalar("SELECT CAST(transaction_isolation_level AS int) FROM sys.dm_exec_sessions WHERE session_id = @@SPID");
            return level switch
            {
                1 => IsolationLevel.ReadUncommitted,
                3 => IsolationLevel.RepeatableRead,
                4 => IsolationLevel.Serializable,
                5 => IsolationLevel.Snapshot,
                _ => IsolationLevel.ReadCommitted,
            };
        }
        set
        {
            string level = value switch
            {
                IsolationLevel.Default or IsolationLevel.ReadCommitted => "READ COMMITTED",
                IsolationLevel.ReadUncommitted => "READ UNCOMMITTED",
                IsolationLevel.RepeatableRead => "REPEATABLE READ",
                IsolationLevel.Snapshot => "SNAPSHOT",
                IsolationLevel.Serializable => "SERIALIZABLE",
                _ => throw AdbcException.NotImplemented($"SQL Server does not support isolation level {value}"),
            };
            EnsureIdle();
            if (FixedIsolationLevel() is IsolationLevel fixedLevel)
            {
                if (value != IsolationLevel.Default && value != fixedLevel)
                {
                    throw AdbcException.NotImplemented(
                        $"{EngineName(SqlServerEngine.Edition(_session))} runs every transaction at isolation level {fixedLevel}; it cannot be changed");
                }
                return;
            }
            _session.ExecuteNonQuery("SET TRANSACTION ISOLATION LEVEL " + level);
            _session.IsolationLevel = level == "READ COMMITTED" ? null : level;
        }
    }

    /// <summary>
    /// The one isolation level of an engine that does not let a session choose:
    /// read uncommitted on a Synapse dedicated pool, or read committed (as
    /// snapshot) where its database has READ_COMMITTED_SNAPSHOT on; snapshot on
    /// Fabric Warehouse and Synapse serverless. Null elsewhere.
    /// </summary>
    private IsolationLevel? FixedIsolationLevel()
    {
        EngineEdition edition = SqlServerEngine.Edition(_session);
        if (!SqlServerEngine.HasFixedIsolation(edition))
        {
            return null;
        }
        if (edition == EngineEdition.Warehouse)
        {
            return IsolationLevel.Snapshot;
        }
        object? snapshot = QueryScalar("SELECT CAST(is_read_committed_snapshot_on AS int) FROM sys.databases WHERE name = DB_NAME()");
        return snapshot is 1 ? IsolationLevel.ReadCommitted : IsolationLevel.ReadUncommitted;
    }

    private static string EngineName(EngineEdition edition) => edition switch
    {
        EngineEdition.SynapseDedicated => "An Azure Synapse dedicated SQL pool",
        EngineEdition.Warehouse => "A Fabric Warehouse or Synapse serverless SQL pool",
        _ => "This server",
    };

    public override string? CurrentCatalog
    {
        get => _session.Database;
        set => SetOption(AdbcOptions.Connection.CurrentCatalog, value ?? throw new ArgumentNullException(nameof(value)));
    }

    /// <summary>The default schema of the database user.</summary>
    public override string? CurrentDbSchema
    {
        get
        {
            EnsureIdle();
            return (string?)QueryScalar("SELECT SCHEMA_NAME()");
        }
        set => SetOption(AdbcOptions.Connection.CurrentDbSchema, value ?? throw new ArgumentNullException(nameof(value)));
    }

    /// <summary>Runs a query and returns the first column of its first row.</summary>
    private object? QueryScalar(string sql)
    {
        using IArrowArrayStream stream = ExecuteQueryStream(sql);
        object? value = null;
        while (stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is RecordBatch batch)
        {
            using (batch)
            {
                if (value == null && batch.Length > 0)
                {
                    value = Arrow.ResultArrays.Value(batch.Column(0), 0);
                }
            }
        }
        return value;
    }

    /// <summary>
    /// Cancels the operation in progress on this connection, including the
    /// reading of a result stream; that operation throws
    /// <see cref="AdbcStatusCode.Cancelled"/>. Does nothing when the
    /// connection is idle.
    /// </summary>
    public override void Cancel()
    {
        _session.Cancel();
        Volatile.Read(ref _parallelStream)?.Cancel();
    }

    public override void Dispose()
    {
        lock (_helpers)
        {
            _disposed = true;
            while (_helpers.TryPop(out TdsSession? helper))
            {
                helper.Dispose();
            }
        }
        _session.Dispose();
        base.Dispose();
    }
}
