// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Clast.Drivers.SqlServer.Arrow;
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
    private string? _partitionColumn;
    private int _partitionCount = 1;
    private bool _incremental;
    /// <summary>Incremental execution: the schema of partitions already returned, with the empty set still to return.</summary>
    private Schema? _incrementalPending;
    private int? _parallelReads;

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
            case SqlServerParameters.PartitionColumn:
                _partitionColumn = value;
                break;
            case SqlServerParameters.ParallelReads:
                _parallelReads = SqlServerConnectionSettings.ParseParallelReads(value);
                break;
            case AdbcOptions.Statement.Incremental:
                _incremental = ParseBool(key, value);
                _incrementalPending = null;
                break;
            case SqlServerParameters.PartitionCount:
                if (!int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _partitionCount) || _partitionCount < 1)
                {
                    throw new AdbcException($"Invalid value '{value}' for option '{key}'", AdbcStatusCode.InvalidArgument);
                }
                break;
            default:
                throw AdbcException.NotImplemented($"Unknown statement option '{key}'");
        }
    }

    public override string GetOption(string key) => key switch
    {
        AdbcOptions.Ingest.TargetTable => _targetTable,
        AdbcOptions.Ingest.TargetCatalog => _targetCatalog,
        AdbcOptions.Ingest.TargetDbSchema => _targetDbSchema,
        AdbcOptions.Ingest.Temporary => AdbcOptions.GetEnabled(_temporary),
        AdbcOptions.Ingest.Mode => AdbcOptions.GetIngestMode(_ingestMode),
        SqlServerParameters.IngestTableLock => AdbcOptions.GetEnabled(_tableLock),
        AdbcOptions.Statement.Incremental => AdbcOptions.GetEnabled(_incremental),
        SqlServerParameters.PartitionColumn => _partitionColumn,
        SqlServerParameters.PartitionCount => SqlServerParameters.Format(_partitionCount),
        SqlServerParameters.ParallelReads => SqlServerParameters.Format(_parallelReads ?? _connection.Session.Settings.ParallelReads),
        _ => null,
    } ?? throw SqlServerParameters.OptionNotFound(key);

    internal void ConfigureIngest(string? catalog, string? dbSchema, string table, BulkIngestMode mode, bool temporary)
    {
        _targetCatalog = catalog;
        _targetDbSchema = dbSchema;
        _targetTable = table;
        _ingestMode = mode;
        _temporary = temporary;
    }

    /// <summary>Binds data for ingest or parameters; extension types that crossed the C data interface as metadata are restored.</summary>
    public override void Bind(RecordBatch batch, Schema schema)
    {
        _boundStream?.Dispose();
        _boundStream = BoundExtensionTypes.Resolve(new SingleBatchStream(batch, schema));
    }

    public override void BindStream(IArrowArrayStream stream)
    {
        _boundStream?.Dispose();
        _boundStream = BoundExtensionTypes.Resolve(stream);
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
        _connection.EnsureIdle();
        TdsSession session = _connection.Session;
        if (TakeBoundStream() is IArrowArrayStream parameters)
        {
            return new QueryResult(-1, SqlServerRpc.ExecuteQuery(session, sql, parameters));
        }
        int parallelism = _parallelReads ?? session.Settings.ParallelReads;

        // Helper sessions cannot see this session's uncommitted work, so stay serial inside a transaction.
        if (parallelism > 1 && _partitionColumn != null && session.TransactionDescriptor == 0)
        {
            return new QueryResult(-1, SqlServerParallelQueryStream.Start(_connection, sql, _partitionColumn, parallelism, _partitionCount));
        }
        return new QueryResult(-1, _connection.ExecuteQueryStream(sql));
    }

    /// <summary>
    /// Splits the query into partitions over <see cref="SqlServerParameters.PartitionColumn"/>.
    /// The partitions are range queries, all known at once and executed only
    /// when read. So with incremental execution
    /// (<see cref="AdbcOptions.Statement.Incremental"/>) the first call returns
    /// them all, and the next returns the empty set that ends that execution; a
    /// call after that starts a new one.
    /// </summary>
    public override PartitionedResult ExecutePartitioned()
    {
        if (_incremental && _incrementalPending is Schema schema)
        {
            _incrementalPending = null;
            return new PartitionedResult(schema, -1, []);
        }
        string sql = SqlQuery ?? throw new AdbcException("SqlQuery must be set", AdbcStatusCode.InvalidState);
        _connection.EnsureIdle();
        if (_boundStream != null)
        {
            throw AdbcException.NotImplemented("Partitioned execution with bound parameters is not supported");
        }
        PartitionedResult result = SqlServerPartitions.Execute(_connection.Session, sql, _partitionColumn, _partitionCount);
        if (_incremental)
        {
            _incrementalPending = result.Schema;
        }
        return result;
    }

    public override UpdateResult ExecuteUpdate()
    {
        _connection.EnsureIdle();
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
        if (TakeBoundStream() is IArrowArrayStream parameters)
        {
            return new UpdateResult(SqlServerRpc.ExecuteUpdate(_connection.Session, sql, parameters));
        }
        return new UpdateResult(_connection.Session.ExecuteNonQuery(sql));
    }

    /// <summary>Hands over the bound data; each Bind or BindStream is used by one execution.</summary>
    private IArrowArrayStream? TakeBoundStream()
    {
        IArrowArrayStream? stream = _boundStream;
        _boundStream = null;
        return stream;
    }

    /// <summary>
    /// Nothing to do ahead of execution: sp_executesql already caches the
    /// plan of a parameterized statement. Only checks that there is a query.
    /// </summary>
    public override void Prepare()
    {
        _ = SqlQuery ?? throw new AdbcException("SqlQuery must be set", AdbcStatusCode.InvalidState);
    }

    /// <summary>
    /// The schema of the query's first result set, without running it. Bound
    /// parameters are declared with their bound types; otherwise with the
    /// types the server infers.
    /// </summary>
    public override Schema ExecuteSchema()
    {
        string sql = SqlQuery ?? throw new AdbcException("SqlQuery must be set", AdbcStatusCode.InvalidState);
        _connection.EnsureIdle();
        TdsSession session = _connection.Session;
        string declarations = _boundStream != null
            ? SqlServerRpc.Declarations(_boundStream.Schema, session.DefaultCollation)
            : Declarations(DescribeParameters(session, sql));
        return SqlServerPartitions.DescribeResult(session, sql, declarations);
    }

    /// <summary>
    /// The parameters the query refers to, with the types the server infers
    /// from their use (<c>sp_describe_undeclared_parameters</c>). Field names
    /// omit the leading @.
    /// </summary>
    public override Schema GetParameterSchema()
    {
        string sql = SqlQuery ?? throw new AdbcException("SqlQuery must be set", AdbcStatusCode.InvalidState);
        _connection.EnsureIdle();
        TdsSession session = _connection.Session;
        List<(string Name, string Type)> parameters = DescribeParameters(session, sql);
        if (parameters.Count == 0)
        {
            return new Schema([], null);
        }
        string columns = string.Join(", ", parameters.Select(p => $"CAST(NULL AS {p.Type}) AS {IngestTarget.Quote(p.Name.TrimStart('@'))}"));
        return SqlServerPartitions.DescribeResult(session, "SELECT " + columns);
    }

    private static List<(string Name, string Type)> DescribeParameters(TdsSession session, string sql)
    {
        List<object?[]> rows = SqlServerMetadata.Query(session,
            $"EXEC sys.sp_describe_undeclared_parameters @tsql = {SqlServerMetadata.Literal(sql)}")[0];
        // Columns: parameter_ordinal, name, suggested_system_type_id, suggested_system_type_name, ...
        return rows.OrderBy(r => (int)r[0]!).Select(r => ((string)r[1]!, (string)r[3]!)).ToList();
    }

    private static string Declarations(List<(string Name, string Type)> parameters) =>
        parameters.Count == 0 ? "" : "DECLARE " + string.Join(", ", parameters.Select(p => $"{p.Name} {p.Type}")) + ";\n";

    /// <summary>
    /// Cancels the operation in progress on the statement's connection, which
    /// has one session: executing, ingesting, or reading a result stream.
    /// </summary>
    public override void Cancel() => _connection.Cancel();

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
