// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Clast.Drivers.SqlServer.Tds;
using Xunit;

namespace Clast.Drivers.SqlServer.Tests;

/// <summary>
/// A minimal TDS endpoint for behaviour the test server cannot show (Entra,
/// routing, other engines): PRELOGIN (answering FEDAUTHREQUIRED = 1), TLS 1.2
/// inside PRELOGIN packets as SQL Server does, then one LOGIN7, answered with
/// whatever tokens <c>respond</c> returns, then SQL batches, answered by
/// <see cref="Batch"/>. It records each login and batch.
/// </summary>
internal sealed class FakeTdsServer : IDisposable
{
    public const byte FedAuthSecurityToken = 0x01;
    public const byte FedAuthMsal = 0x02;

    /// <summary>What a client's LOGIN7 carried.</summary>
    public sealed record Login(
        bool PreLoginFedAuthRequired, string UserName, bool IntegratedSecurity, string ServerName, string Database,
        byte FedAuthLibrary, bool FedAuthEcho, string? Token, byte Workflow, IReadOnlyList<byte> Features, byte TypeFlags);

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Func<Login, int, byte[]> _respond;
    private readonly bool _encrypt;
    private readonly X509Certificate2 _certificate = CreateCertificate();
    private readonly Task _loop;
    private int _connections;

    /// <param name="respond">The tokens answering a login (given its number, from 0).</param>
    /// <param name="encrypt">False to answer PRELOGIN with "encryption not supported".</param>
    public FakeTdsServer(Func<Login, int, byte[]> respond, bool encrypt = true)
    {
        _respond = respond;
        _encrypt = encrypt;
        _listener.Start();
        _loop = Task.Run(Accept);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public ConcurrentQueue<Login> Logins { get; } = new();

    /// <summary>Each PRELOGIN payload as received.</summary>
    public ConcurrentQueue<byte[]> PreLogins { get; } = new();

    /// <summary>The tokens answering a SQL batch, given its text; by default DONE.</summary>
    public Func<string, byte[]>? Batch { get; init; }

    /// <summary>The text of each SQL batch received, after login.</summary>
    public ConcurrentQueue<string> Batches { get; } = new();

    /// <summary>Exceptions on the server side, for diagnosing a failing test.</summary>
    public ConcurrentQueue<Exception> Errors { get; } = new();

    // ---- Response tokens ----

    public static byte[] LoginAck()
    {
        byte[] name = Encoding.Unicode.GetBytes("Fake SQL");
        var body = new List<byte> { 0x01, 0x74, 0x00, 0x00, 0x04, (byte)(name.Length / 2) };
        body.AddRange(name);
        body.AddRange([17, 0, 0x03, 0xE8]);
        return [0xAD, (byte)body.Count, (byte)(body.Count >> 8), .. body];
    }

    public static byte[] FedAuthAck() => [0xAE, 0x02, 0, 0, 0, 0, 0xFF];

    public static byte[] Done() => [0xFD, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    /// <summary>A result set of one nullable nvarchar column and no rows, then DONE.</summary>
    public static byte[] EmptyResult() =>
        [0x81, 1, 0, 0, 0, 0, 0, 0x01, 0x00, 0xE7, 0x40, 0x1F, 0x09, 0x04, 0xD0, 0x00, 0x34, 0, 0xFD, 0x10, 0, 0xC1, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    /// <summary>A result set of one nullable int column and one row, then DONE.</summary>
    public static byte[] IntResult(int? value)
    {
        var token = new List<byte> { 0x81, 1, 0, 0, 0, 0, 0, 0x01, 0x00, 0x26, 4, 0 };
        token.AddRange([0xD1]);
        if (value is int v)
        {
            token.Add(4);
            token.AddRange(BitConverter.GetBytes(v));
        }
        else
        {
            token.Add(0);
        }
        token.AddRange([0xFD, 0x10, 0, 0xC1, 0, 1, 0, 0, 0, 0, 0, 0, 0]);
        return [.. token];
    }

    /// <summary>A failed login: an ERROR token with <paramref name="number"/>, then DONE with the error bit.</summary>
    public static byte[] LoginError(int number, string message = "Login failed")
    {
        byte[] text = Encoding.Unicode.GetBytes(message);
        byte[] server = Encoding.Unicode.GetBytes("fake");
        int length = 4 + 1 + 1 + 2 + text.Length + 1 + server.Length + 1 + 4;
        var token = new byte[3 + length];
        token[0] = 0xAA;
        BinaryPrimitives.WriteUInt16LittleEndian(token.AsSpan(1), (ushort)length);
        BinaryPrimitives.WriteInt32LittleEndian(token.AsSpan(3), number);
        token[7] = 1;
        token[8] = 14;
        BinaryPrimitives.WriteUInt16LittleEndian(token.AsSpan(9), (ushort)(text.Length / 2));
        text.CopyTo(token, 11);
        int at = 11 + text.Length;
        token[at++] = (byte)(server.Length / 2);
        server.CopyTo(token, at);
        at += server.Length;
        token[at++] = 0; // procedure name
        BinaryPrimitives.WriteInt32LittleEndian(token.AsSpan(at), 1);
        return [.. token, 0xFD, 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
    }

    public static byte[] FedAuthInfo(string stsUrl, string spn)
    {
        byte[] info = FedAuthInfoData(stsUrl, spn);
        var token = new byte[5 + info.Length];
        token[0] = 0xEE;
        BinaryPrimitives.WriteInt32LittleEndian(token.AsSpan(1), info.Length);
        info.CopyTo(token, 5);
        return token;
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

    /// <summary>ENVCHANGE type 20: route the client to <paramref name="host"/>:<paramref name="port"/>.</summary>
    public static byte[] Routing(string host, int port, byte protocol = 0)
    {
        byte[] name = Encoding.Unicode.GetBytes(host);
        int valueLength = 1 + 2 + 2 + name.Length;
        int length = 1 + 2 + valueLength + 2;
        var token = new byte[3 + length];
        token[0] = 0xE3;
        BinaryPrimitives.WriteUInt16LittleEndian(token.AsSpan(1), (ushort)length);
        token[3] = 20;
        BinaryPrimitives.WriteUInt16LittleEndian(token.AsSpan(4), (ushort)valueLength);
        token[6] = protocol;
        BinaryPrimitives.WriteUInt16LittleEndian(token.AsSpan(7), (ushort)port);
        BinaryPrimitives.WriteUInt16LittleEndian(token.AsSpan(9), (ushort)host.Length);
        name.CopyTo(token, 11);
        // Old value: an empty (zero-length) value.
        return token;
    }

    /// <summary>ENVCHANGE type 21: route the client to <paramref name="host"/>:<paramref name="port"/> and <paramref name="database"/>.</summary>
    public static byte[] EnhancedRouting(string host, int port, string database)
    {
        byte[] name = Encoding.Unicode.GetBytes(host);
        byte[] db = Encoding.Unicode.GetBytes(database);
        int valueLength = 1 + 2 + 2 + name.Length + 2 + db.Length;
        int length = 1 + 2 + valueLength + 2;
        var token = new byte[3 + length];
        token[0] = 0xE3;
        BinaryPrimitives.WriteUInt16LittleEndian(token.AsSpan(1), (ushort)length);
        token[3] = 21;
        BinaryPrimitives.WriteUInt16LittleEndian(token.AsSpan(4), (ushort)valueLength);
        token[6] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(token.AsSpan(7), (ushort)port);
        BinaryPrimitives.WriteUInt16LittleEndian(token.AsSpan(9), (ushort)host.Length);
        name.CopyTo(token, 11);
        BinaryPrimitives.WriteUInt16LittleEndian(token.AsSpan(11 + name.Length), (ushort)database.Length);
        db.CopyTo(token, 13 + name.Length);
        // Old value: an empty (zero-length) value.
        return token;
    }

    /// <summary>FEATUREEXTACK acknowledging enhanced routing (feature 0x0F) with <paramref name="value"/>.</summary>
    public static byte[] EnhancedRoutingAck(byte value = 1) => [0xAE, 0x0F, 1, 0, 0, 0, value, 0xFF];

    // ---- Server ----

    private static X509Certificate2 CreateCertificate()
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // SChannel needs a key it can find, not an ephemeral one.
#if NET
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
#else
        return new X509Certificate2(ephemeral.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
#endif
    }

    private async Task Accept()
    {
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
            int index = Interlocked.Increment(ref _connections) - 1;
            _ = Task.Run(() =>
            {
                using (client)
                {
                    try
                    {
                        Serve(client.GetStream(), index);
                    }
                    catch (Exception ex)
                    {
                        // Usually the client hanging up (as a discovering, routed or refusing client does).
                        Errors.Enqueue(ex);
                    }
                }
            });
        }
    }

    private void Serve(NetworkStream network, int index)
    {
        (byte _, byte[] preLogin) = ReadMessage(network);
        PreLogins.Enqueue(preLogin);
        bool fedAuthRequired = PreLoginOption(preLogin, 0x06) is { Length: > 0 } fedAuth && fedAuth[0] == 1;

        // PRELOGIN response: VERSION, ENCRYPTION (ON, or NOT_SUP), FEDAUTHREQUIRED = 1.
        byte[] response =
        [
            0x00, 0, 16, 0, 6,
            0x01, 0, 22, 0, 1,
            0x06, 0, 23, 0, 1,
            0xFF,
            17, 0, 0x03, 0xE8, 0, 0,
            _encrypt ? (byte)0x01 : (byte)0x02,
            1,
        ];
        WritePacket(network, 0x04, response);
        if (!_encrypt)
        {
            // A client sending a token stops here rather than send it in clear text.
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
#pragma warning disable SYSLIB0039
#if NET
        ssl.AuthenticateAsServer(new SslServerAuthenticationOptions
        {
            ServerCertificate = _certificate,
            EnabledSslProtocols = SslProtocols.Tls12,
        });
#else
        ssl.AuthenticateAsServer(_certificate, clientCertificateRequired: false, SslProtocols.Tls12, checkCertificateRevocation: false);
#endif
#pragma warning restore SYSLIB0039
        tunnel.Encapsulate = false;

        (byte type, byte[] login7) = ReadMessage(ssl);
        Assert.Equal(0x10, type);
        Login login = ParseLogin(login7, fedAuthRequired);
        Logins.Enqueue(login);

        WritePacket(ssl, 0x04, _respond(login, index));
        // Answer batches until the client disconnects.
        while (true)
        {
            byte requestType;
            byte[] request;
            try
            {
                (requestType, request) = ReadMessage(ssl);
            }
            catch (Exception ex) when (ex is EndOfStreamException or IOException)
            {
                return;
            }
            if (requestType == 0x01)
            {
                // ALL_HEADERS (its total length first), then the text.
                int headers = BinaryPrimitives.ReadInt32LittleEndian(request);
                string sql = Encoding.Unicode.GetString(request, headers, request.Length - headers);
                Batches.Enqueue(sql);
                WritePacket(ssl, 0x04, Batch?.Invoke(sql) ?? Done());
            }
            else
            {
                WritePacket(ssl, 0x04, Done());
            }
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
        string serverName = String(52);
        string database = String(68);
        bool integrated = (login[25] & 0x80) != 0;
        int extension = BinaryPrimitives.ReadUInt16LittleEndian(login.AsSpan(56));
        int features = BinaryPrimitives.ReadInt32LittleEndian(login.AsSpan(extension));

        byte library = 0, workflow = 0;
        bool echo = false;
        string? token = null;
        var ids = new List<byte>();
        for (int i = features; login[i] != 0xFF;)
        {
            byte id = login[i];
            ids.Add(id);
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
        return new Login(preLoginFedAuthRequired, userName, integrated, serverName, database, library, echo, token, workflow, ids, login[26]);
    }

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
