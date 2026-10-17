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
        using var server = new FakeTdsServer(FakeTdsServer.Mode.AcceptToken);
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
        using var server = new FakeTdsServer(FakeTdsServer.Mode.Discovery);
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
        using var server = new FakeTdsServer(FakeTdsServer.Mode.AcceptTokenThenDiscovery);
        using AdbcConnection connection = new SqlServerDriver().Open(Options(server, "token")).Connect(null);
        Assert.Equal(Authority, connection.GetOption(SqlServerParameters.EntraAuthority));
        Assert.Equal(Audience, connection.GetOption(SqlServerParameters.EntraAudience));
    }

    [Fact]
    public void TokenIsNeverSentUnencrypted()
    {
        using var server = new FakeTdsServer(FakeTdsServer.Mode.NoEncryption);
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
        using AdbcDatabase database = new SqlServerDriver().Open(options);
        var discovery = Assert.Throws<AdbcException>(() => database.GetOption(SqlServerParameters.EntraAuthority));
        Assert.Equal(AdbcStatusCode.NotImplemented, discovery.Status);

        options[SqlServerParameters.AccessToken] = "eyJ0eXAiOiJKV1QifQ.e30.x";
        var login = Assert.ThrowsAny<AdbcException>(() => new SqlServerDriver().Open(options).Connect(null));
        Assert.Equal(AdbcStatusCode.Unauthenticated, login.Status);
    }

    /// <summary>
    /// A minimal TDS endpoint: PRELOGIN (with FEDAUTHREQUIRED), TLS inside
    /// PRELOGIN packets, then LOGIN7, answered with LOGINACK or FEDAUTHINFO.
    /// It records what each LOGIN7 carried.
    /// </summary>
    private sealed class FakeTdsServer : IDisposable
    {
        public const byte FedAuthSecurityToken = 0x01;
        public const byte FedAuthMsal = 0x02;

        public enum Mode
        {
            AcceptToken,
            Discovery,
            AcceptTokenThenDiscovery,
            NoEncryption,
        }

        public sealed record Login(bool PreLoginFedAuthRequired, string UserName, bool IntegratedSecurity, byte FedAuthLibrary, bool FedAuthEcho, string? Token, byte Workflow);

        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Mode _mode;
        private readonly X509Certificate2 _certificate = CreateCertificate();
        private readonly Task _loop;

        public FakeTdsServer(Mode mode)
        {
            _mode = mode;
            _listener.Start();
            _loop = Task.Run(Accept);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public System.Collections.Concurrent.ConcurrentQueue<Login> Logins { get; } = new();

        /// <summary>Exceptions on the server side, for diagnosing a failing test.</summary>
        public System.Collections.Concurrent.ConcurrentQueue<Exception> Errors { get; } = new();

        private static X509Certificate2 CreateCertificate()
        {
            using RSA key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using X509Certificate2 ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            // SChannel needs a key it can find, not an ephemeral one.
            return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
        }

        private async Task Accept()
        {
            int connections = 0;
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync();
                }
                catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
                {
                    return;
                }
                bool discovery = _mode == Mode.Discovery || (_mode == Mode.AcceptTokenThenDiscovery && connections++ > 0);
                _ = Task.Run(() =>
                {
                    using (client)
                    {
                        try
                        {
                            Serve(client.GetStream(), discovery);
                        }
                        catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or EndOfStreamException)
                        {
                            // The client went away (as a discovery or refusing client does).
                            Errors.Enqueue(ex);
                        }
                        catch (Exception ex)
                        {
                            Errors.Enqueue(ex);
                        }
                    }
                });
            }
        }

        private void Serve(NetworkStream network, bool discovery)
        {
            (byte _, byte[] preLogin) = ReadMessage(network);
            bool fedAuthRequired = PreLoginOption(preLogin, 0x06) is [1, ..];
            bool encrypt = _mode != Mode.NoEncryption;

            // PRELOGIN response: VERSION, ENCRYPTION (ON, or NOT_SUP), FEDAUTHREQUIRED = 1.
            byte[] response =
            [
                0x00, 0, 16, 0, 6,
                0x01, 0, 22, 0, 1,
                0x06, 0, 23, 0, 1,
                0xFF,
                17, 0, 0x03, 0xE8, 0, 0,
                encrypt ? (byte)0x01 : (byte)0x02,
                1,
            ];
            WritePacket(network, 0x04, response);
            if (!encrypt)
            {
                // A correct client stops here rather than send its token in clear text.
                if (network.Read(new byte[1]) == 0)
                {
                    return;
                }
                throw new InvalidOperationException("The client continued without encryption");
            }

            var tunnel = new PreLoginTlsStream(network);
            using var ssl = new SslStream(tunnel, leaveInnerStreamOpen: true);
            // TLS 1.2, as SQL Server uses inside PRELOGIN (TDS 7.x); TLS 1.3 sends a post-handshake
            // ticket that would still be wrapped in a PRELOGIN packet after the client stopped unwrapping.
            ssl.AuthenticateAsServer(new SslServerAuthenticationOptions
            {
                ServerCertificate = _certificate,
#pragma warning disable SYSLIB0039
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12,
#pragma warning restore SYSLIB0039
            });
            tunnel.Encapsulate = false;

            (byte type, byte[] login7) = ReadMessage(ssl);
            Assert.Equal(0x10, type);
            Logins.Enqueue(ParseLogin(login7, fedAuthRequired));

            if (discovery)
            {
                byte[] info = FedAuthInfoData(Authority, Audience);
                var token = new byte[5 + info.Length];
                token[0] = 0xEE;
                BinaryPrimitives.WriteInt32LittleEndian(token.AsSpan(1), info.Length);
                info.CopyTo(token, 5);
                WritePacket(ssl, 0x04, token);
                // A real server now waits for the token; the discovering client hangs up.
                ssl.ReadExactly(new byte[1]);
                return;
            }

            WritePacket(ssl, 0x04, [.. LoginAck(), .. FeatureExtAck(), .. Done()]);
            // Stay open until the client disconnects.
            while (ssl.Read(new byte[64]) > 0)
            {
            }
        }

        private static Login ParseLogin(byte[] login, bool preLoginFedAuthRequired)
        {
            string String(int at)
            {
                int offset = BinaryPrimitives.ReadUInt16LittleEndian(login.AsSpan(at));
                int length = BinaryPrimitives.ReadUInt16LittleEndian(login.AsSpan(at + 2));
                return Encoding.Unicode.GetString(login, offset, length * 2);
            }

            string userName = String(40);
            bool integrated = (login[25] & 0x80) != 0;
            int extension = BinaryPrimitives.ReadUInt16LittleEndian(login.AsSpan(56));
            int features = BinaryPrimitives.ReadInt32LittleEndian(login.AsSpan(extension));

            byte library = 0, workflow = 0;
            bool echo = false;
            string? token = null;
            for (int i = features; login[i] != 0xFF;)
            {
                byte id = login[i];
                int length = BinaryPrimitives.ReadInt32LittleEndian(login.AsSpan(i + 1));
                ReadOnlySpan<byte> data = login.AsSpan(i + 5, length);
                if (id == 0x02)
                {
                    library = (byte)(data[0] >> 1);
                    echo = (data[0] & 1) != 0;
                    if (library == FedAuthSecurityToken)
                    {
                        int tokenLength = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(1));
                        token = Encoding.Unicode.GetString(data.Slice(5, tokenLength));
                    }
                    else
                    {
                        workflow = data[1];
                    }
                }
                i += 5 + length;
            }
            return new Login(preLoginFedAuthRequired, userName, integrated, library, echo, token, workflow);
        }

        public static byte[] FedAuthInfoData(string stsUrl, string spn)
        {
            byte[] sts = Encoding.Unicode.GetBytes(stsUrl);
            byte[] spnBytes = Encoding.Unicode.GetBytes(spn);
            int dataStart = 4 + 2 * 9;
            var data = new byte[dataStart + sts.Length + spnBytes.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(data, 2);
            data[4] = 0x02; // SPN first, as Azure SQL sends it
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(5), spnBytes.Length);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(9), dataStart + sts.Length);
            data[13] = 0x01;
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(14), sts.Length);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(18), dataStart);
            sts.CopyTo(data, dataStart);
            spnBytes.CopyTo(data, dataStart + sts.Length);
            return data;
        }

        private static byte[] LoginAck()
        {
            byte[] name = Encoding.Unicode.GetBytes("Fake SQL");
            var body = new List<byte> { 0x01, 0x74, 0x00, 0x00, 0x04, (byte)(name.Length / 2) };
            body.AddRange(name);
            body.AddRange([17, 0, 0x03, 0xE8]);
            return [0xAD, (byte)body.Count, (byte)(body.Count >> 8), .. body];
        }

        private static byte[] FeatureExtAck() => [0xAE, 0x02, 0, 0, 0, 0, 0xFF];

        private static byte[] Done() => [0xFD, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

        private static byte[]? PreLoginOption(byte[] payload, byte option)
        {
            for (int i = 0; payload[i] != 0xFF; i += 5)
            {
                if (payload[i] == option)
                {
                    int offset = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(i + 1));
                    int length = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(i + 3));
                    return payload.AsSpan(offset, length).ToArray();
                }
            }
            return null;
        }

        private static (byte Type, byte[] Payload) ReadMessage(Stream stream)
        {
            var payload = new MemoryStream();
            byte type;
            byte status;
            do
            {
                byte[] header = new byte[8];
                stream.ReadExactly(header);
                type = header[0];
                status = header[1];
                int length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
                byte[] data = new byte[length - 8];
                stream.ReadExactly(data);
                payload.Write(data);
            }
            while ((status & 0x01) == 0);
            return (type, payload.ToArray());
        }

        private static void WritePacket(Stream stream, byte type, byte[] payload)
        {
            byte[] packet = new byte[8 + payload.Length];
            packet[0] = type;
            packet[1] = 0x01;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length);
            packet[6] = 1;
            payload.CopyTo(packet, 8);
            stream.Write(packet);
            stream.Flush();
        }

        public void Dispose()
        {
            _listener.Stop();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
            }
            _certificate.Dispose();
        }
    }
}
