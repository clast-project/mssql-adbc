// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow.Adbc;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Clast.Drivers.SqlServer.Tests;

public class EnvironmentTests
{
    [Fact]
    public void DriverReportsAdbc11()
    {
        using var driver = new SqlServerDriver();
        Assert.Equal(AdbcVersion.Version_1_1_0, driver.DriverVersion);
    }

    /// <summary>
    /// Confirms the configured server is reachable over TCP, so failures in the
    /// driver's own tests can be told apart from environment problems.
    /// </summary>
    [SkippableFact]
    public void SqlClientCanConnectOverTcp()
    {
        SqlServerTestConfiguration config = SqlServerTestConfiguration.Load();

        using var connection = new SqlConnection(config.SqlClientConnectionString);
        connection.Open();
        using SqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT net_transport FROM sys.dm_exec_connections WHERE session_id = @@SPID";
        Assert.Equal("TCP", (string)command.ExecuteScalar());
    }
}
