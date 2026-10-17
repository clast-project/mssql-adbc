// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow.Adbc;
using Xunit;

namespace Clast.Drivers.SqlServer.Tests;

public class ConnectionTests
{
    private static AdbcConnection Connect(Action<Dictionary<string, string>>? configure = null)
    {
        SqlServerTestConfiguration config = SqlServerTestConfiguration.Load();
        var options = new Dictionary<string, string>(config.Options);
        configure?.Invoke(options);
        var driver = new SqlServerDriver();
        AdbcDatabase database = driver.Open(options);
        return database.Connect(null);
    }

    [SkippableTheory]
    [InlineData("optional")]
    [InlineData("mandatory")]
    [InlineData("strict")]
    public void ConnectsWithEachEncryptionMode(string mode)
    {
        Skip.If(mode == "strict" && !SqlServerTestConfiguration.Load().SupportsStrictEncryption,
            "The server has no certificate configured for TDS 8 strict encryption");
        using AdbcConnection connection = Connect(o => o[SqlServerParameters.Encrypt] = mode);
        var session = ((SqlServerConnection)connection).Session;
        Assert.True(session.ServerVersion.Major >= 11);
        Assert.False(string.IsNullOrEmpty(session.Database));
        Assert.Equal(5, session.DefaultCollation.Length);
    }

    [SkippableFact]
    public void ConnectsOverNamedPipe()
    {
        using AdbcConnection connection = Connect(o => o[SqlServerParameters.Server] = @"np:\\.\pipe\sql\query");
        Assert.True(((SqlServerConnection)connection).Session.ServerVersion.Major >= 11);
    }

    [SkippableTheory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("[::1]")]
    [InlineData("tcp:127.0.0.1,1433")]
    public void ConnectsByAddress(string server)
    {
        using AdbcConnection connection = Connect(o => o[SqlServerParameters.Server] = server);
        Assert.True(((SqlServerConnection)connection).Session.ServerVersion.Major >= 11);
    }

    /// <summary>
    /// The machine's own names and LAN addresses. Without a domain, an
    /// address that resolves to another name (host.localdomain) fails NTLM's
    /// loopback check, so the driver retries with the machine name.
    /// </summary>
    [SkippableFact]
    public void ConnectsByLocalNamesAndAddresses()
    {
        string machine = System.Net.Dns.GetHostName();
        var servers = new List<string> { machine };
        foreach (var address in System.Net.Dns.GetHostAddresses(machine).Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
        {
            servers.Add(address.ToString());
            servers.Add(System.Net.Dns.GetHostEntry(address).HostName);
        }
        foreach (string server in servers.Distinct())
        {
            using AdbcConnection connection = Connect(o => o[SqlServerParameters.Server] = server);
            Assert.True(((SqlServerConnection)connection).Session.ServerVersion.Major >= 11, server);
        }
    }

    [SkippableFact]
    public void ExplicitServerSpnIsUsedAsGiven()
    {
        string lan = System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName())
            .First(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).ToString();
        var ex = Assert.Throws<AdbcException>(() => Connect(o =>
        {
            o[SqlServerParameters.Server] = lan;
            o[SqlServerParameters.ServerSpn] = "MSSQLSvc/no-such-host.localdomain:1433";
        }));
        Assert.Equal(AdbcStatusCode.Unauthenticated, ex.Status);
        Assert.Contains("MSSQLSvc/no-such-host.localdomain:1433", ex.Message);
    }

    [SkippableFact]
    public void NegotiatesPacketSize()
    {
        // The server decides; current versions cap TLS sessions at 16384.
        using AdbcConnection connection = Connect(o => o[SqlServerParameters.PacketSize] = "32767");
        Assert.InRange(((SqlServerConnection)connection).Session.Writer.PacketSize, 8192, 32767);
    }

    [SkippableFact]
    public void ExecuteUpdateReturnsRowCount()
    {
        using AdbcConnection connection = Connect();
        using AdbcStatement statement = connection.CreateStatement();

        statement.SqlQuery = "CREATE TABLE #t (i int); INSERT INTO #t VALUES (1), (2), (3);";
        Assert.Equal(3, statement.ExecuteUpdate().AffectedRows);

        statement.SqlQuery = "UPDATE #t SET i = i + 1 WHERE i > 1";
        Assert.Equal(2, statement.ExecuteUpdate().AffectedRows);

        statement.SqlQuery = "DECLARE @x int = 1";
        Assert.Equal(-1, statement.ExecuteUpdate().AffectedRows);
    }

    [SkippableFact]
    public void LargeBatchSpansPackets()
    {
        using AdbcConnection connection = Connect(o => o[SqlServerParameters.PacketSize] = "4096");
        using AdbcStatement statement = connection.CreateStatement();

        // ~200 KB of UTF-16 text, so the request spans many packets.
        statement.SqlQuery = "CREATE TABLE #t (i int); INSERT INTO #t VALUES " + string.Join("; INSERT INTO #t VALUES ",
            Enumerable.Range(0, 20).Select(_ => string.Join(",", Enumerable.Range(0, 1000).Select(i => $"({i})"))));
        Assert.Equal(20000, statement.ExecuteUpdate().AffectedRows);
    }

    [SkippableFact]
    public void ServerErrorsSurfaceAndSessionSurvives()
    {
        using AdbcConnection connection = Connect();
        using AdbcStatement statement = connection.CreateStatement();

        statement.SqlQuery = "DROP TABLE dbo.this_table_does_not_exist_8f3a";
        SqlServerException ex = Assert.Throws<SqlServerException>(() => statement.ExecuteUpdate());
        Assert.Equal(3701, ex.Number);

        statement.SqlQuery = "DECLARE @x int = 1";
        Assert.Equal(-1, statement.ExecuteUpdate().AffectedRows);
    }

    [SkippableFact]
    public void TracksTransactionDescriptor()
    {
        using AdbcConnection connection = Connect();
        using AdbcStatement statement = connection.CreateStatement();
        var session = ((SqlServerConnection)connection).Session;

        statement.SqlQuery = "BEGIN TRANSACTION";
        statement.ExecuteUpdate();
        Assert.NotEqual(0, session.TransactionDescriptor);

        statement.SqlQuery = "CREATE TABLE #t (i int); INSERT INTO #t VALUES (1)";
        Assert.Equal(1, statement.ExecuteUpdate().AffectedRows);

        statement.SqlQuery = "ROLLBACK TRANSACTION";
        statement.ExecuteUpdate();
        Assert.Equal(0, session.TransactionDescriptor);
    }

    [SkippableFact]
    public void BadPasswordIsUnauthenticated()
    {
        SqlServerTestConfiguration.Load();
        var ex = Assert.ThrowsAny<AdbcException>(() => Connect(o =>
        {
            o.Remove(SqlServerParameters.IntegratedSecurity);
            o[SqlServerParameters.Username] = "clast_no_such_login";
            o[SqlServerParameters.Password] = "wrong";
        }));
        Assert.Equal(AdbcStatusCode.Unauthenticated, ex.Status);
    }
}
