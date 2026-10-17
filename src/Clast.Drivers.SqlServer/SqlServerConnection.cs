// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;

namespace Clast.Drivers.SqlServer;

/// <summary>
/// A single TDS session.
/// </summary>
public sealed class SqlServerConnection : AdbcConnection
{
    private readonly IReadOnlyDictionary<string, string> _options;

    internal SqlServerConnection(IReadOnlyDictionary<string, string> options)
    {
        _options = options;
        // TODO(M1): open transport, prelogin, TLS and login.
    }

    internal IReadOnlyDictionary<string, string> Options => _options;

    public override AdbcStatement CreateStatement()
    {
        return new SqlServerStatement(this);
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
}
