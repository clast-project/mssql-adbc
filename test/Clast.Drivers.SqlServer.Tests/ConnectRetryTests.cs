// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using Apache.Arrow.Adbc;
using Xunit;

namespace Clast.Drivers.SqlServer.Tests;

/// <summary>
/// Connect retry after transient (Azure) login errors. Fake TDS servers fail
/// the first logins with the error under test and accept later ones.
/// </summary>
public sealed class ConnectRetryTests
{
    private static Dictionary<string, string> Options(FakeTdsServer server, Action<Dictionary<string, string>>? configure = null)
    {
        var options = new Dictionary<string, string>
        {
            [SqlServerParameters.Server] = $"127.0.0.1,{server.Port}",
            [SqlServerParameters.Username] = "user",
            [SqlServerParameters.Password] = "password",
            [SqlServerParameters.TrustServerCertificate] = "true",
            [SqlServerParameters.ConnectTimeout] = "10",
            [SqlServerParameters.ConnectRetryCount] = "2",
            [SqlServerParameters.ConnectRetryInterval] = "1",
        };
        configure?.Invoke(options);
        return options;
    }

    /// <summary>A server failing the first <paramref name="failures"/> logins with <paramref name="number"/>.</summary>
    private static FakeTdsServer Failing(int failures, int number = 40613) =>
        new((_, n) => n < failures
            ? FakeTdsServer.LoginError(number, "Database 'db' on server 'fake' is not currently available.")
            : [.. FakeTdsServer.LoginAck(), .. FakeTdsServer.Done()]);

    private static SqlServerException ConnectFails(Dictionary<string, string> options)
    {
        using AdbcDatabase database = new SqlServerDriver().Open(options);
        return Assert.Throws<SqlServerException>(() => database.Connect(null));
    }

    [Fact]
    public void RetriesATransientLoginError()
    {
        using FakeTdsServer server = Failing(2);
        var watch = Stopwatch.StartNew();
        using (AdbcConnection connection = new SqlServerDriver().Open(Options(server)).Connect(null))
        {
            Assert.Equal("Fake SQL", ((SqlServerConnection)connection).Session.ServerProgramName);
        }
        Assert.Equal(3, server.Logins.Count);
        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(1.9), $"Retried after {watch.Elapsed}");
    }

    [Fact]
    public void GivesUpAfterTheRetryCount()
    {
        using FakeTdsServer server = Failing(int.MaxValue);
        SqlServerException ex = ConnectFails(Options(server, o => o[SqlServerParameters.ConnectRetryCount] = "1"));
        Assert.Equal(40613, ex.Number);
        Assert.Equal(2, server.Logins.Count);
    }

    [Fact]
    public void DoesNotRetryOtherErrors()
    {
        using FakeTdsServer server = Failing(1, 18456);
        SqlServerException ex = ConnectFails(Options(server));
        Assert.Equal(18456, ex.Number);
        Assert.Equal(AdbcStatusCode.Unauthenticated, ex.Status);
        Assert.Single(server.Logins);
    }

    [Fact]
    public void DoesNotRetryPastTheConnectTimeout()
    {
        using FakeTdsServer server = Failing(1);
        var watch = Stopwatch.StartNew();
        ConnectFails(Options(server, o =>
        {
            o[SqlServerParameters.ConnectTimeout] = "5";
            o[SqlServerParameters.ConnectRetryInterval] = "10";
        }));
        Assert.Single(server.Logins);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), $"Failed after {watch.Elapsed}");
    }

    [Fact]
    public void DoesNotRetryByDefaultOutsideAzure()
    {
        using FakeTdsServer server = Failing(1);
        ConnectFails(Options(server, o => o.Remove(SqlServerParameters.ConnectRetryCount)));
        Assert.Single(server.Logins);
    }

    [Fact]
    public void RetriesFromTheStartAfterRouting()
    {
        using FakeTdsServer target = Failing(1);
        using var gateway = new FakeTdsServer((_, _) =>
            [.. FakeTdsServer.LoginAck(), .. FakeTdsServer.Routing("127.0.0.1", target.Port), .. FakeTdsServer.Done()]);
        using (AdbcConnection connection = new SqlServerDriver().Open(Options(gateway)).Connect(null))
        {
            Assert.Equal(target.Port, ((SqlServerConnection)connection).Session.Settings.Port);
            Assert.Equal(gateway.Port, ((SqlServerConnection)connection).Session.ConnectSettings.Port);
        }
        Assert.Equal(2, gateway.Logins.Count);
        Assert.Equal(2, target.Logins.Count);
    }

    /// <summary>4060 ("cannot open database") is on the list, and the one a real server here can produce.</summary>
    [SkippableFact]
    public void RetriesAMissingDatabaseOnARealServer()
    {
        var options = new Dictionary<string, string>(SqlServerTestConfiguration.Load().Options)
        {
            [SqlServerParameters.Database] = "clast_no_such_database",
            [SqlServerParameters.ConnectRetryCount] = "1",
            [SqlServerParameters.ConnectRetryInterval] = "1",
        };
        var watch = Stopwatch.StartNew();
        SqlServerException ex = ConnectFails(options);
        Assert.Equal(4060, ex.Number);
        Assert.Equal(AdbcStatusCode.NotFound, ex.Status);
        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(0.9), $"Failed after {watch.Elapsed}");
    }

    [Theory]
    [InlineData("myserver.database.windows.net", 2)]
    [InlineData("MyServer.Database.Windows.Net", 2)]
    [InlineData("myserver.database.usgovcloudapi.net", 2)]
    [InlineData("abc.database.fabric.microsoft.com", 2)]
    [InlineData("ws-ondemand.sql.azuresynapse.net", 5)]
    [InlineData("myserver-ondemand.database.windows.net", 5)]
    [InlineData("abc.datawarehouse.fabric.microsoft.com", 5)]
    [InlineData("abc.pbidedicated.windows.net", 5)]
    [InlineData("ws.sql.azuresynapse.net", 1)]
    [InlineData("localhost", 0)]
    [InlineData("db.example.com", 0)]
    public void DefaultRetryCountFollowsTheHost(string host, int expected)
    {
        SqlServerConnectionSettings settings = SqlServerConnectionSettings.Parse(new Dictionary<string, string>
        {
            [SqlServerParameters.Server] = $"tcp:{host},1433",
            [SqlServerParameters.Username] = "user",
        });
        Assert.Equal(expected, settings.ConnectRetryCount);
    }

    [Theory]
    [InlineData(SqlServerParameters.ConnectRetryCount, "-1")]
    [InlineData(SqlServerParameters.ConnectRetryCount, "256")]
    [InlineData(SqlServerParameters.ConnectRetryInterval, "0")]
    [InlineData(SqlServerParameters.ConnectRetryInterval, "61")]
    [InlineData(SqlServerParameters.ConnectRetryInterval, "x")]
    public void RejectsInvalidRetryOptions(string key, string value)
    {
        AdbcException ex = Assert.Throws<AdbcException>(() => SqlServerConnectionSettings.Parse(new Dictionary<string, string>
        {
            [SqlServerParameters.Username] = "user",
            [key] = value,
        }));
        Assert.Equal(AdbcStatusCode.InvalidArgument, ex.Status);
    }
}
