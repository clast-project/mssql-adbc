// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow.Adbc;

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
        // TODO(M2): send SQL batch and stream rows into Arrow batches.
        throw AdbcException.NotImplemented("ExecuteQuery is not yet supported");
    }

    public override UpdateResult ExecuteUpdate()
    {
        // TODO(M2/M3): SQL batch row counts and bulk ingestion.
        throw AdbcException.NotImplemented("ExecuteUpdate is not yet supported");
    }
}
