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

    internal SqlServerConnection(IReadOnlyDictionary<string, string> options)
    {
        _session = TdsSession.Connect(SqlServerConnectionSettings.Parse(options));
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
                    .Select(_ => Task.Run(() => TdsSession.Connect(_session.Settings)))
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

            foreach (TdsSession helper in sessions)
            {
                if (!string.Equals(helper.Database, _session.Database, StringComparison.Ordinal))
                {
                    helper.ExecuteNonQuery("USE " + IngestTarget.Quote(_session.Database));
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
