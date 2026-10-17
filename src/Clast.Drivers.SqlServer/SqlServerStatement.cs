// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow.Adbc;
using Clast.Drivers.SqlServer.Tds;

namespace Clast.Drivers.SqlServer;

/// <summary>
/// Executes SQL batches and bulk ingestion on a <see cref="SqlServerConnection"/>.
/// </summary>
public sealed class SqlServerStatement : AdbcStatement
{
    private readonly SqlServerConnection _connection;

    internal SqlServerStatement(SqlServerConnection connection)
    {
        _connection = connection;
    }

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
        // TODO(M3): bulk ingestion.
        string sql = SqlQuery ?? throw new AdbcException("SqlQuery must be set", AdbcStatusCode.InvalidState);
        return new UpdateResult(_connection.Session.ExecuteNonQuery(sql));
    }
}
