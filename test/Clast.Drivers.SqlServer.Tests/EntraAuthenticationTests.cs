// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Apache.Arrow.Adbc;
using Clast.Drivers.SqlServer.Tds;
using Xunit;

namespace Clast.Drivers.SqlServer.Tests;

/// <summary>
/// Microsoft Entra access tokens and authority/audience discovery. The test
/// server is not configured for Entra, so a fake TDS server (with real TLS)
/// plays an Azure SQL endpoint; the real server covers the unconfigured case.
/// </summary>
public sealed class EntraAuthenticationTests
{
    private const string Authority = "https://login.microsoftonline.com/00000000-1111-2222-3333-444444444444";
    private const string Audience = "https://database.windows.net/";

    private static Dictionary<string, string> Options(FakeTdsServer server, string? token = null) => new()
    {
        [SqlServerParameters.Server] = $"127.0.0.1,{server.Port}",
        [SqlServerParameters.TrustServerCertificate] = "true",
        [SqlServerParameters.ConnectTimeout] = "5",
        [SqlServerParameters.AccessToken] = token ?? "",
    };

    [Fact]
    public void LogsInWithAnAccessToken()
    {
        using var server = new FakeTdsServer((_, _) => [.. FakeTdsServer.LoginAck(), .. FakeTdsServer.FedAuthAck(), .. FakeTdsServer.Done()]);
        const string Token = "eyJ0eXAiOiJKV1QiLCJhbGciOiJSUzI1NiJ9.eyJhdWQiOiJodHRwczovL2RhdGFiYXNlLndpbmRvd3MubmV0LyJ9.c2lnbmF0dXJl";
        using (AdbcConnection connection = new SqlServerDriver().Open(Options(server, Token)).Connect(null))
        {
            Assert.Equal("Fake SQL", ((SqlServerConnection)connection).Session.ServerProgramName);
        }

        FakeTdsServer.Login login = server.Logins.Single();
        Assert.True(login.PreLoginFedAuthRequired);
        Assert.Equal(FakeTdsServer.FedAuthSecurityToken, login.FedAuthLibrary);
        Assert.True(login.FedAuthEcho);
        Assert.Equal(Token, login.Token);
        Assert.Equal("", login.UserName);
        Assert.False(login.IntegratedSecurity);
    }

    [Fact]
    public void DiscoversAuthorityAndAudienceWithoutCredentials()
    {
        using var server = new FakeTdsServer((_, _) => FakeTdsServer.FedAuthInfo(Authority, Audience));
        using AdbcDatabase database = new SqlServerDriver().Open(Options(server));
        Assert.Equal(Authority, database.GetOption(SqlServerParameters.EntraAuthority));
        Assert.Equal(Audience, database.GetOption(SqlServerParameters.EntraAudience));

        // Asked for once, and with the MSAL workflow.
        FakeTdsServer.Login login = server.Logins.Single();
        Assert.Equal(FakeTdsServer.FedAuthMsal, login.FedAuthLibrary);
        Assert.Equal(0x03, login.Workflow);
        Assert.Null(login.Token);
    }

    [Fact]
    public void ConnectionReportsItsServersAuthority()
    {
        using var server = new FakeTdsServer((_, n) => n == 0 ? [.. FakeTdsServer.LoginAck(), .. FakeTdsServer.FedAuthAck(), .. FakeTdsServer.Done()] : FakeTdsServer.FedAuthInfo(Authority, Audience));
        using AdbcConnection connection = new SqlServerDriver().Open(Options(server, "token")).Connect(null);
        Assert.Equal(Authority, connection.GetOption(SqlServerParameters.EntraAuthority));
        Assert.Equal(Audience, connection.GetOption(SqlServerParameters.EntraAudience));
    }

    [Fact]
    public void TokenIsNeverSentUnencrypted()
    {
        using var server = new FakeTdsServer((_, _) => [], encrypt: false);
        Dictionary<string, string> options = Options(server, "secret-token");
        options[SqlServerParameters.Encrypt] = "optional";
        var ex = Assert.Throws<AdbcException>(() => new SqlServerDriver().Open(options).Connect(null));
        Assert.Contains("must not travel unencrypted", ex.Message);
        Assert.Empty(server.Logins);
    }

    [Theory]
    [InlineData(SqlServerParameters.IntegratedSecurity, "true")]
    [InlineData(SqlServerParameters.Username, "sa")]
    public void TokenExcludesOtherCredentials(string key, string value)
    {
        var options = new Dictionary<string, string> { [SqlServerParameters.AccessToken] = "token", [key] = value };
        var ex = Assert.Throws<AdbcException>(() => SqlServerConnectionSettings.Parse(options));
        Assert.Equal(AdbcStatusCode.InvalidArgument, ex.Status);
    }

    [Fact]
    public void TokenIsNotReturnedByGetOption()
    {
        using AdbcDatabase database = new SqlServerDriver().Open(new Dictionary<string, string> { [SqlServerParameters.AccessToken] = "secret" });
        Assert.Equal(AdbcStatusCode.NotFound, Assert.Throws<AdbcException>(() => database.GetOption(SqlServerParameters.AccessToken)).Status);
    }

    [Fact]
    public void ParsesFedAuthInfo()
    {
        EntraInfo info = TdsSession.ParseFedAuthInfo(FakeTdsServer.FedAuthInfoData(Authority, Audience));
        Assert.Equal(new EntraInfo(Authority, Audience), info);
        Assert.Throws<TdsProtocolException>(() => TdsSession.ParseFedAuthInfo([1, 0, 0, 0]));
    }

    [SkippableFact]
    public void UnconfiguredServerReportsNoEntraSupport()
    {
        SqlServerTestConfiguration config = SqlServerTestConfiguration.Load();
        var options = new Dictionary<string, string>(config.Options);
        options.Remove(SqlServerParameters.IntegratedSecurity);
        options.Remove(SqlServerParameters.Username);
        options.Remove(SqlServerParameters.Password);
        using AdbcDatabase database = new SqlServerDriver().Open(options);
        var discovery = Assert.Throws<AdbcException>(() => database.GetOption(SqlServerParameters.EntraAuthority));
        Assert.Equal(AdbcStatusCode.NotImplemented, discovery.Status);

        options[SqlServerParameters.AccessToken] = "eyJ0eXAiOiJKV1QifQ.e30.x";
        var login = Assert.ThrowsAny<AdbcException>(() => new SqlServerDriver().Open(options).Connect(null));
        Assert.Equal(AdbcStatusCode.Unauthenticated, login.Status);
    }
}
