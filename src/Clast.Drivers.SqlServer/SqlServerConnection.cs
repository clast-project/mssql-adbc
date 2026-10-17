// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Clast.Drivers.SqlServer.Tds;

namespace Clast.Drivers.SqlServer;

/// <summary>
/// A single TDS session.
/// </summary>
public sealed class SqlServerConnection : AdbcConnection
{
    private readonly TdsSession _session;

    internal SqlServerConnection(IReadOnlyDictionary<string, string> options)
    {
        _session = TdsSession.Connect(SqlServerConnectionSettings.Parse(options));
    }

    internal TdsSession Session => _session;

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
        return ExecuteQueryStream(SqlServerPartitions.Decode(partition));
    }

    /// <summary>Sends a batch and returns a stream over its first result set.</summary>
    internal IArrowArrayStream ExecuteQueryStream(string sql)
    {
        _session.SendSqlBatch(sql);
        _session.Reader.BeginMessage();

        TdsColumn[]? columns = _session.ReadUntilResultSet();
        if (columns == null)
        {
            return SqlServerQueryStream.Empty();
        }

        try
        {
            return new SqlServerQueryStream(_session, columns);
        }
        catch
        {
            // Unsupported column type: leave the session usable.
            _session.ActiveStream = null;
            _session.DrainResponse(columns);
            throw;
        }
    }

    public override IArrowArrayStream GetObjects(
        GetObjectsDepth depth,
        string? catalogPattern,
        string? dbSchemaPattern,
        string? tableNamePattern,
        IReadOnlyList<string>? tableTypes,
        string? columnNamePattern)
    {
        throw AdbcException.NotImplemented("GetObjects is not yet supported");
    }

    public override Schema GetTableSchema(string? catalog, string? dbSchema, string tableName)
    {
        throw AdbcException.NotImplemented("GetTableSchema is not yet supported");
    }

    public override IArrowArrayStream GetTableTypes()
    {
        throw AdbcException.NotImplemented("GetTableTypes is not yet supported");
    }

    public override void Dispose()
    {
        _session.Dispose();
        base.Dispose();
    }
}
