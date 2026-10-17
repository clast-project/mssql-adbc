// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Clast.Drivers.SqlServer.Tds;

namespace Clast.Drivers.SqlServer;

/// <summary>
/// Executes SQL batches and bulk ingestion on a <see cref="SqlServerConnection"/>.
/// </summary>
public sealed class SqlServerStatement : AdbcStatement
{
    private readonly SqlServerConnection _connection;
    private string? _targetCatalog;
    private string? _targetDbSchema;
    private string? _targetTable;
    private bool _temporary;
    private BulkIngestMode _ingestMode = BulkIngestMode.Create;
    private bool _tableLock;
    private IArrowArrayStream? _boundStream;

    internal SqlServerStatement(SqlServerConnection connection)
    {
        _connection = connection;
    }

    public override void SetOption(string key, string value)
    {
        switch (key)
        {
            case AdbcOptions.Ingest.TargetTable:
                _targetTable = value;
                break;
            case AdbcOptions.Ingest.TargetCatalog:
                _targetCatalog = value;
                break;
            case AdbcOptions.Ingest.TargetDbSchema:
                _targetDbSchema = value;
                break;
            case AdbcOptions.Ingest.Temporary:
                _temporary = ParseBool(key, value);
                break;
            case AdbcOptions.Ingest.Mode:
                _ingestMode = AdbcOptions.GetIngestMode(value);
                break;
            case SqlServerParameters.IngestTableLock:
                _tableLock = ParseBool(key, value);
                break;
            default:
                throw AdbcException.NotImplemented($"Unknown statement option '{key}'");
        }
    }

    internal void ConfigureIngest(string? catalog, string? dbSchema, string table, BulkIngestMode mode, bool temporary)
    {
        _targetCatalog = catalog;
        _targetDbSchema = dbSchema;
        _targetTable = table;
        _ingestMode = mode;
        _temporary = temporary;
    }

    public override void Bind(RecordBatch batch, Schema schema)
    {
        _boundStream?.Dispose();
        _boundStream = new SingleBatchStream(batch, schema);
    }

    public override void BindStream(IArrowArrayStream stream)
    {
        _boundStream?.Dispose();
        _boundStream = stream;
    }

    private static bool ParseBool(string key, string value) => value.ToLowerInvariant() switch
    {
        "true" or "1" => true,
        "false" or "0" => false,
        _ => throw new AdbcException($"Invalid value '{value}' for option '{key}'", AdbcStatusCode.InvalidArgument),
    };

    public override QueryResult ExecuteQuery()
    {
        string sql = SqlQuery ?? throw new AdbcException("SqlQuery must be set", AdbcStatusCode.InvalidState);
        TdsSession session = _connection.Session;
        session.SendSqlBatch(sql);
        session.Reader.BeginMessage();

        TdsColumn[]? columns = session.ReadUntilResultSet();
        if (columns == null)
        {
            return new QueryResult(-1, SqlServerQueryStream.Empty());
        }

        try
        {
            return new QueryResult(-1, new SqlServerQueryStream(session, columns));
        }
        catch
        {
            // Unsupported column type: leave the session usable.
            session.ActiveStream = null;
            session.DrainResponse(columns);
            throw;
        }
    }

    public override UpdateResult ExecuteUpdate()
    {
        if (_targetTable != null)
        {
            if (_boundStream == null)
            {
                throw new AdbcException("Bulk ingestion requires data to be bound with Bind or BindStream", AdbcStatusCode.InvalidState);
            }
            if (_temporary && (_targetCatalog != null || _targetDbSchema != null))
            {
                throw new AdbcException("A temporary table cannot have a catalog or schema", AdbcStatusCode.InvalidArgument);
            }

            IArrowArrayStream data = _boundStream;
            _boundStream = null;
            using (data)
            {
                var target = new IngestTarget(_targetCatalog, _targetDbSchema, _targetTable, _temporary);
                return new UpdateResult(SqlServerBulkIngest.Execute(_connection.Session, target, _ingestMode, data, _tableLock));
            }
        }

        string sql = SqlQuery ?? throw new AdbcException("SqlQuery must be set", AdbcStatusCode.InvalidState);
        return new UpdateResult(_connection.Session.ExecuteNonQuery(sql));
    }

    public override void Dispose()
    {
        _boundStream?.Dispose();
        _boundStream = null;
        base.Dispose();
    }

    private sealed class SingleBatchStream : IArrowArrayStream
    {
        private RecordBatch? _batch;

        public SingleBatchStream(RecordBatch batch, Schema schema)
        {
            _batch = batch;
            Schema = schema;
        }

        public Schema Schema { get; }

        public ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
        {
            // The caller keeps ownership of a bound batch, so hand out a non-owning slice.
            RecordBatch? batch = _batch?.SliceShared(0, _batch.Length);
            _batch = null;
            return new ValueTask<RecordBatch?>(batch);
        }

        public void Dispose()
        {
        }
    }
}
