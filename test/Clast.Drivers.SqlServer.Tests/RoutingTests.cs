// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Clast.Drivers.SqlServer.Tds;
using Xunit;

namespace Clast.Drivers.SqlServer.Tests;

/// <summary>
/// Routing (ENVCHANGE 20), as Azure SQL's redirect connection policy,
/// read-only routing and failover groups use it. Fake gateways route to the
/// real test server, or to another fake.
/// </summary>
public sealed class RoutingTests
{
    /// <summary>A gateway that acknowledges the login and routes it to <paramref name="port"/> on this machine.</summary>
    private static FakeTdsServer Gateway(int port, string host = "127.0.0.1") =>
        new((_, _) => [.. FakeTdsServer.LoginAck(), .. FakeTdsServer.Routing(host, port), .. FakeTdsServer.Done()]);

    /// <summary>A gateway routing to the configured real server.</summary>
    private static FakeTdsServer GatewayToRealServer()
    {
        (string host, int port) = SqlServerTestConfiguration.Load().Endpoint;
        return Gateway(port, host);
    }

    private static Dictionary<string, string> Options(int port, Action<Dictionary<string, string>>? configure = null)
    {
        SqlServerTestConfiguration config = SqlServerTestConfiguration.Load();
        var options = new Dictionary<string, string>(config.Options)
        {
            [SqlServerParameters.Server] = $"127.0.0.1,{port}",
            [SqlServerParameters.TrustServerCertificate] = "true",
        };
        configure?.Invoke(options);
        return options;
    }

    private static int Scalar(AdbcConnection connection, string sql)
    {
        using AdbcStatement statement = connection.CreateStatement();
        statement.SqlQuery = sql;
        using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
        using RecordBatch batch = stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult()!;
        return ((Int32Array)batch.Column(0)).GetValue(0)!.Value;
    }

    [SkippableFact]
    public void FollowsTheRouteToTheRealServer()
    {
        using FakeTdsServer gateway = GatewayToRealServer();
        using AdbcConnection connection = new SqlServerDriver().Open(Options(gateway.Port)).Connect(null);
        TdsSession session = ((SqlServerConnection)connection).Session;

        Assert.Equal(SqlServerTestConfiguration.Load().Endpoint, (session.Settings.Host, session.Settings.Port));
        Assert.Equal(gateway.Port, session.ConnectSettings.Port);
        Assert.NotEqual("Fake SQL", session.ServerProgramName);
        Assert.Equal(1, Scalar(connection, "SELECT 1"));
        Assert.Single(gateway.Logins);
    }

    [SkippableFact]
    public void RoutedLoginNamesTheRouteAndKeepsTheDatabase()
    {
        using var target = new FakeTdsServer((_, _) => [.. FakeTdsServer.LoginAck(), .. FakeTdsServer.Done()]);
        using FakeTdsServer gateway = Gateway(target.Port, "localhost");
        using AdbcConnection connection = new SqlServerDriver().Open(Options(gateway.Port, o => o[SqlServerParameters.Database] = "sales")).Connect(null);

        FakeTdsServer.Login first = gateway.Logins.Single();
        FakeTdsServer.Login routed = target.Logins.Single();
        Assert.Equal("127.0.0.1", first.ServerName);
        Assert.Equal($"localhost,{target.Port}", routed.ServerName);
        Assert.Equal("sales", routed.Database);
        Assert.Equal(SqlServerTestConfiguration.Load().UsesIntegratedSecurity, routed.IntegratedSecurity);
        Assert.Equal("localhost", ((SqlServerConnection)connection).Session.Settings.Host);
    }

    [SkippableFact]
    public void FollowsChainedRoutes()
    {
        using FakeTdsServer second = GatewayToRealServer();
        using FakeTdsServer first = Gateway(second.Port);
        using AdbcConnection connection = new SqlServerDriver().Open(Options(first.Port)).Connect(null);
        Assert.Equal(1, Scalar(connection, "SELECT 1"));
        Assert.Single(first.Logins);
        Assert.Single(second.Logins);
    }

    [SkippableFact]
    public void StopsARoutingLoop()
    {
        FakeTdsServer? loop = null;
        loop = new FakeTdsServer((_, _) => [.. FakeTdsServer.LoginAck(), .. FakeTdsServer.Routing("127.0.0.1", loop!.Port), .. FakeTdsServer.Done()]);
        using (loop)
        {
            var ex = Assert.Throws<AdbcException>(() => new SqlServerDriver().Open(Options(loop.Port)).Connect(null));
            Assert.Contains($"more than {TdsSession.MaxRoutes} times", ex.Message);
            Assert.Equal(TdsSession.MaxRoutes + 1, loop.Logins.Count);
        }
    }

    [SkippableFact]
    public void ParallelReadSessionsAreRoutedToo()
    {
        using FakeTdsServer gateway = GatewayToRealServer();
        using AdbcConnection connection = new SqlServerDriver().Open(Options(gateway.Port, o => o[SqlServerParameters.ParallelReads] = "3")).Connect(null);
        using AdbcStatement statement = connection.CreateStatement();
        statement.SqlQuery = "SELECT CAST(n AS bigint) AS k FROM (SELECT TOP (3000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects a CROSS JOIN sys.all_objects b) AS t";
        statement.SetOption(SqlServerParameters.PartitionColumn, "k");
        using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
        Assert.IsType<SqlServerParallelQueryStream>(stream);
        long rows = 0;
        while (stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is RecordBatch batch)
        {
            rows += batch.Length;
            batch.Dispose();
        }
        Assert.Equal(3000, rows);
        // The connection's own session and two helpers, each logging in through the gateway.
        Assert.Equal(3, gateway.Logins.Count);
    }

    [SkippableFact]
    public void RejectsARouteThatIsNotTcp()
    {
        using var gateway = new FakeTdsServer((_, _) => [.. FakeTdsServer.LoginAck(), .. FakeTdsServer.Routing("127.0.0.1", 1433, protocol: 1), .. FakeTdsServer.Done()]);
        Dictionary<string, string> options = Options(gateway.Port);
        var ex = Assert.ThrowsAny<Exception>(() => new SqlServerDriver().Open(options).Connect(null));
        Assert.Contains("Invalid routing information", ex.ToString());
    }

    /// <summary>Token login options for a fake server, needing no test configuration.</summary>
    private static Dictionary<string, string> TokenOptions(int port) => new()
    {
        [SqlServerParameters.Server] = $"127.0.0.1,{port}",
        [SqlServerParameters.TrustServerCertificate] = "true",
        [SqlServerParameters.ConnectTimeout] = "5",
        [SqlServerParameters.AccessToken] = "token",
    };

    /// <summary>
    /// Fabric Warehouse routes to <c>host\instance</c>: the port wins, the
    /// connection goes to the host, and LOGIN7 names the whole route, as in SqlClient.
    /// </summary>
    [Fact]
    public void RouteNamingAnInstanceGoesToItsHost()
    {
        using var target = new FakeTdsServer((_, _) => [.. FakeTdsServer.LoginAck(), .. FakeTdsServer.FedAuthAck(), .. FakeTdsServer.Done()]);
        using var gateway = new FakeTdsServer((_, _) =>
            [.. FakeTdsServer.LoginAck(), .. FakeTdsServer.FedAuthAck(), .. FakeTdsServer.Routing(@"127.0.0.1\WAREHOUSE-dw", target.Port), .. FakeTdsServer.Done()]);
        using AdbcConnection connection = new SqlServerDriver().Open(TokenOptions(gateway.Port)).Connect(null);

        Assert.Equal($@"127.0.0.1\WAREHOUSE-dw,{target.Port}", target.Logins.Single().ServerName);
        Assert.Equal("127.0.0.1", ((SqlServerConnection)connection).Session.Settings.Host);
    }

    /// <summary>Every PRELOGIN carries TRACEID: Fabric Warehouse refuses a routed login without it.</summary>
    [Fact]
    public void PreLoginCarriesATraceId()
    {
        using var target = new FakeTdsServer((_, _) => [.. FakeTdsServer.LoginAck(), .. FakeTdsServer.FedAuthAck(), .. FakeTdsServer.Done()]);
        using var gateway = new FakeTdsServer((_, _) =>
            [.. FakeTdsServer.LoginAck(), .. FakeTdsServer.FedAuthAck(), .. FakeTdsServer.Routing("127.0.0.1", target.Port), .. FakeTdsServer.Done()]);
        using (new SqlServerDriver().Open(TokenOptions(gateway.Port)).Connect(null))
        {
        }

        byte[][] preLogins = [gateway.PreLogins.Single(), target.PreLogins.Single()];
        Guid[] ids = preLogins.Select(p =>
        {
            for (int i = 0; p[i] != 0xFF; i += 5)
            {
                if (p[i] == 0x05)
                {
                    Assert.Equal(36, (p[i + 3] << 8) | p[i + 4]);
                    return new Guid(p.AsSpan((p[i + 1] << 8) | p[i + 2], 16));
                }
            }
            throw new Xunit.Sdk.XunitException("No TRACEID option");
        }).ToArray();
        Assert.All(ids, id => Assert.NotEqual(Guid.Empty, id));
    }
}
