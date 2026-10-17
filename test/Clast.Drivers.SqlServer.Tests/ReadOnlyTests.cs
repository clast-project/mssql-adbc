// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Xunit;

namespace Clast.Drivers.SqlServer.Tests;

/// <summary>
/// adbc.connection.readonly: read-only application intent, sent with the
/// login (LOGIN7 TypeFlags fReadOnlyIntent), as SqlClient's ApplicationIntent=ReadOnly.
/// </summary>
public sealed class ReadOnlyTests
{
    private const byte ReadOnlyIntent = 0x20;

    private static FakeTdsServer Server() => new((_, _) => [.. FakeTdsServer.LoginAck(), .. FakeTdsServer.Done()]);

    private static Dictionary<string, string> Options(FakeTdsServer server) => new()
    {
        [SqlServerParameters.Server] = $"127.0.0.1,{server.Port}",
        [SqlServerParameters.Username] = "user",
        [SqlServerParameters.Password] = "password",
        [SqlServerParameters.TrustServerCertificate] = "true",
        [SqlServerParameters.ConnectTimeout] = "5",
    };

    [Fact]
    public void IsOffByDefault()
    {
        using FakeTdsServer server = Server();
        using AdbcConnection connection = new SqlServerDriver().Open(Options(server)).Connect(null);
        Assert.Equal(0, server.Logins.Single().TypeFlags & ReadOnlyIntent);
        Assert.False(connection.ReadOnly);
        Assert.Equal("false", connection.GetOption(AdbcOptions.Connection.ReadOnly));
    }

    [Fact]
    public void SetOnTheDatabaseItIsSentWithTheLogin()
    {
        using FakeTdsServer server = Server();
        using AdbcDatabase database = new SqlServerDriver().Open(Options(server));
        database.SetOption(AdbcOptions.Connection.ReadOnly, AdbcOptions.Enabled);
        using AdbcConnection connection = database.Connect(null);
        Assert.Equal(ReadOnlyIntent, server.Logins.Single().TypeFlags & ReadOnlyIntent);
        Assert.True(connection.ReadOnly);
        Assert.Equal("true", connection.GetOption(AdbcOptions.Connection.ReadOnly));
    }

    [Fact]
    public void SetInConnectOptionsItIsSentWithTheLogin()
    {
        using FakeTdsServer server = Server();
        using AdbcDatabase database = new SqlServerDriver().Open(Options(server));
        using AdbcConnection connection = database.Connect(new Dictionary<string, string> { [AdbcOptions.Connection.ReadOnly] = "true" });
        Assert.Equal(ReadOnlyIntent, server.Logins.Single().TypeFlags & ReadOnlyIntent);
        Assert.True(connection.ReadOnly);
    }

    [Fact]
    public void CannotChangeOnAnOpenConnection()
    {
        using FakeTdsServer server = Server();
        using AdbcConnection connection = new SqlServerDriver().Open(Options(server)).Connect(null);
        connection.SetOption(AdbcOptions.Connection.ReadOnly, "false"); // unchanged: fine
        AdbcException ex = Assert.Throws<AdbcException>(() => connection.SetOption(AdbcOptions.Connection.ReadOnly, "true"));
        Assert.Equal(AdbcStatusCode.InvalidState, ex.Status);
        Assert.Contains("before connecting", ex.Message);
        Assert.False(connection.ReadOnly);
    }

    [Fact]
    public void RefusesAnInvalidValue()
    {
        using FakeTdsServer server = Server();
        var options = Options(server);
        options[AdbcOptions.Connection.ReadOnly] = "yes";
        AdbcException ex = Assert.Throws<AdbcException>(() => new SqlServerDriver().Open(options).Connect(null));
        Assert.Equal(AdbcStatusCode.InvalidArgument, ex.Status);
    }

    /// <summary>A server without replicas accepts the intent and, having no read-only mode, still allows writes.</summary>
    [SkippableFact]
    public async Task AStandaloneServerAcceptsTheIntent()
    {
        var options = new Dictionary<string, string>(SqlServerTestConfiguration.Load().Options)
        {
            [AdbcOptions.Connection.ReadOnly] = "true",
        };
        using AdbcConnection connection = new SqlServerDriver().Open(options).Connect(null);
        Assert.True(connection.ReadOnly);
        using AdbcStatement statement = connection.CreateStatement();
        statement.SqlQuery = "CREATE TABLE #written (id int); INSERT #written VALUES (1); SELECT COUNT(*) AS n FROM #written";
        using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
        using RecordBatch batch = (await stream.ReadNextRecordBatchAsync())!;
        Assert.Equal(1, ((Int32Array)batch.Column(0)).GetValue(0));
    }
}
