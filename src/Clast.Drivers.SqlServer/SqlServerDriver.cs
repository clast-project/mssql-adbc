// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow.Adbc;

namespace Clast.Drivers.SqlServer;

/// <summary>
/// ADBC driver for Microsoft SQL Server.
/// </summary>
public sealed class SqlServerDriver : AdbcDriver
{
    public override int DriverVersion => AdbcVersion.Version_1_1_0;

    public override AdbcDatabase Open(IReadOnlyDictionary<string, string> parameters)
    {
        return new SqlServerDatabase(parameters);
    }
}
