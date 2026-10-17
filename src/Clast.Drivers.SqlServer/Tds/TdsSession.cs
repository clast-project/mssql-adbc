// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using Apache.Arrow.Adbc;

namespace Clast.Drivers.SqlServer.Tds;

/// <summary>
/// One TDS session: the transport, TLS, login, and the token stream that every
/// request and response shares.
/// </summary>
internal sealed class TdsSession : IDisposable
{
    private readonly SqlServerConnectionSettings _settings;
    private readonly List<SqlServerError> _errors = new();
    private Stream _transport = Stream.Null;
    private SslStream? _ssl;
    private NegotiateAuthentication? _sspi;
    private readonly string? _spnOverride;
    private string? _spn;
    private bool _sspiFailed;
    private bool _broken;

    // Request state. Cancel can be called on any thread, so changes to the
    // state and the sending of an attention are serialized by _requestLock.
    private readonly object _requestLock = new();
    private RequestState _requestState;
    private volatile bool _cancelPending;
    private volatile bool _attentionSent;
    private bool _attentionAcknowledged;

    private enum RequestState
    {
        Idle,
        Sending,
        Receiving,
    }

    /// <summary>An ATTENTION message: a single packet with only a header.</summary>
    private static readonly byte[] s_attentionPacket =
        [(byte)PacketType.Attention, (byte)PacketStatus.EndOfMessage, 0, TdsConstants.HeaderLength, 0, 0, 1, 0];

    /// <summary>Discovery only: ask the server for its Entra authority and audience (FEDAUTHINFO), then stop.</summary>
    private readonly bool _discovery;

    /// <summary>What a discovery session learned.</summary>
    private EntraInfo? _entraInfo;

    private TdsSession(SqlServerConnectionSettings settings, string? spn = null, bool discovery = false)
    {
        _discovery = discovery;
        _settings = settings;
        _spnOverride = spn ?? settings.ServerSpn;
        Reader = new PacketReader(Stream.Null, TdsConstants.InitialPacketSize);
        Writer = new PacketWriter(Stream.Null, TdsConstants.InitialPacketSize);
    }

    public PacketReader Reader { get; }

    public PacketWriter Writer { get; }

    public SqlServerConnectionSettings Settings => _settings;

    /// <summary>Current database, as last reported by the server.</summary>
    public string Database { get; private set; } = "";

    /// <summary>Default collation of the current database (5 bytes), used for non-Unicode data without its own collation.</summary>
    public byte[] DefaultCollation { get; private set; } = [];

    /// <summary>Negotiated TDS version, as reported by LOGINACK.</summary>
    public uint TdsVersion { get; private set; }

    public string ServerProgramName { get; private set; } = "";

    /// <summary>Server version as major.minor.build.</summary>
    public Version ServerVersion { get; private set; } = new();

    public bool Utf8Supported { get; private set; }

    /// <summary>Version of native json transport the server acknowledged, or 0 (json then arrives as nvarchar(max)).</summary>
    public byte JsonVersion { get; private set; }

    /// <summary>Version of native vector transport the server acknowledged, or 0 (vectors then arrive as JSON text).</summary>
    public byte VectorVersion { get; private set; }

    /// <summary>The isolation level this driver last set on the session (for example <c>SERIALIZABLE</c>), or null for the default.</summary>
    public string? IsolationLevel { get; set; }

    /// <summary>Descriptor of the open transaction, or 0 when in autocommit mode.</summary>
    public long TransactionDescriptor { get; private set; }

    /// <summary>INFO messages received since the last call to <see cref="ClearMessages"/>.</summary>
    public List<SqlServerError> Messages { get; } = new();

    public bool IsBroken => _broken;

    /// <summary>The TLS version protecting the session, or null when it is unencrypted (or only the login was).</summary>
    public System.Security.Authentication.SslProtocols? TlsProtocol => _ssl?.SslProtocol;

    /// <summary>True when the current request has been cancelled and the response is being cut short.</summary>
    public bool IsCancelRequested => _cancelPending || _attentionSent;

    /// <summary>The result stream currently reading from this session, if any.</summary>
    public object? ActiveStream { get; set; }

    public void ClearMessages() => Messages.Clear();

    /// <summary>
    /// Asks the server which Entra authority issues its tokens and for which
    /// audience: the handshake of the MSAL workflow, stopped at the server's
    /// FEDAUTHINFO reply instead of answering with a token.
    /// </summary>
    public static EntraInfo DiscoverEntra(SqlServerConnectionSettings settings)
    {
        using var session = new TdsSession(settings, discovery: true);
        try
        {
            session.Open();
        }
        catch (Exception ex) when (ex is not AdbcException)
        {
            throw new AdbcException($"Could not connect to '{settings.Host}': {ex.Message}", AdbcStatusCode.IOError, ex);
        }
        return session._entraInfo!;
    }

    public static TdsSession Connect(SqlServerConnectionSettings settings)
    {
        string? spn = null;
        while (true)
        {
            var session = new TdsSession(settings, spn);
            try
            {
                session.Open();
                return session;
            }
            catch (Exception ex)
            {
                session.Dispose();
                if (spn == null && session.LocalFallbackSpn() is string fallback)
                {
                    spn = fallback;
                    continue;
                }
                if (ex is AdbcException)
                {
                    throw;
                }
                throw new AdbcException($"Could not connect to '{settings.Host}': {ex.Message}", AdbcStatusCode.IOError, ex);
            }
        }
    }

    /// <summary>
    /// The SPN to retry an integrated login with after the SSPI exchange
    /// failed: the machine's own name, when the server is on this machine
    /// under another name. Without a domain, Negotiate falls back to NTLM,
    /// and NTLM rejects a local login whose target is not the machine name
    /// (for example its LAN address, which resolves to <c>host.localdomain</c>).
    /// Microsoft's own drivers fail in that case ("Cannot generate SSPI context").
    /// </summary>
    private string? LocalFallbackSpn()
    {
        if (!_sspiFailed || _settings.ServerSpn != null || !IsLocalHost(_settings.Host))
        {
            return null;
        }
        string spn = FormatSpn(Environment.MachineName);
        return string.Equals(spn, _spn, StringComparison.OrdinalIgnoreCase) ? null : spn;
    }

    private static bool IsLocalHost(string host)
    {
        try
        {
            var local = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(u => u.Address)
                .ToHashSet();
            return Dns.GetHostAddresses(host).Any(a => IPAddress.IsLoopback(a) || local.Contains(a));
        }
        catch (Exception ex) when (ex is SocketException or System.Net.NetworkInformation.NetworkInformationException)
        {
            return false;
        }
    }

    private void Open()
    {
        using var timeout = new CancellationTokenSource(_settings.ConnectTimeout);
        _transport = OpenTransport(timeout.Token);
        int timeoutMs = (int)_settings.ConnectTimeout.TotalMilliseconds;
        if (_transport.CanTimeout)
        {
            _transport.ReadTimeout = timeoutMs;
            _transport.WriteTimeout = timeoutMs;
        }

        Stream stream = _transport;
        bool loginOnlyEncryption = false;

        if (_settings.Encrypt == EncryptMode.Strict)
        {
            _ssl = new SslStream(_transport, leaveInnerStreamOpen: true);
            _ssl.AuthenticateAsClient(CreateTlsOptions(strict: true));
            stream = _ssl;
        }

        SetStream(stream);

        // PRELOGIN
        bool fedAuth = _discovery || _settings.AccessToken != null;
        // Under strict encryption TLS is already in place, so (as SqlClient does) the
        // ENCRYPTION option says "not supported" rather than asking for TLS again.
        PreLoginEncryption requested = _settings.Encrypt switch
        {
            EncryptMode.Optional => PreLoginEncryption.Off,
            EncryptMode.Strict => PreLoginEncryption.NotSupported,
            _ => PreLoginEncryption.On,
        };
        Writer.BeginMessage(PacketType.PreLogin);
        Writer.WriteBytes(Login7.BuildPreLogin(requested, fedAuth));
        Writer.EndMessage();

        Reader.BeginMessage();
        byte[] response = ReadMessageBytes();
        (PreLoginEncryption serverEncryption, bool fedAuthRequired) = Login7.ParsePreLogin(response);

        if (_settings.Encrypt != EncryptMode.Strict)
        {
            switch (serverEncryption)
            {
                case PreLoginEncryption.NotSupported:
                    if (_settings.Encrypt == EncryptMode.Mandatory)
                    {
                        throw new AdbcException(
                            $"The server does not support encryption; to connect without it, set {SqlServerParameters.Encrypt}=optional",
                            AdbcStatusCode.IOError);
                    }
                    break;
                case PreLoginEncryption.Off:
                    loginOnlyEncryption = true;
                    goto case PreLoginEncryption.On;
                case PreLoginEncryption.On:
                case PreLoginEncryption.Required:
                    var tunnel = new PreLoginTlsStream(_transport);
                    _ssl = new SslStream(tunnel, leaveInnerStreamOpen: true);
                    _ssl.AuthenticateAsClient(CreateTlsOptions(strict: false));
                    tunnel.Encapsulate = false;
                    SetStream(_ssl);
                    break;
                default:
                    throw new TdsProtocolException($"Unexpected PRELOGIN encryption value {serverEncryption}");
            }
        }

        // LOGIN7
        if (fedAuth && _ssl == null)
        {
            // An access token is a bearer credential: never send it in clear text.
            throw new AdbcException(
                "The server does not support encryption, and Entra authentication sends a token that must not travel unencrypted",
                AdbcStatusCode.IOError);
        }
        byte[] fedAuthFeature = fedAuth
            ? Login7.BuildFedAuthFeature(_discovery ? Login7.FedAuthMsal : Login7.FedAuthSecurityToken, fedAuthRequired, _settings.AccessToken)
            : [];

        ReadOnlySpan<byte> sspiToken = default;
        if (_settings.IntegratedSecurity && !fedAuth)
        {
            _sspi = new NegotiateAuthentication(new NegotiateAuthenticationClientOptions
            {
                Package = "Negotiate",
                TargetName = GetServicePrincipalName(),
            });
            sspiToken = NextSspiToken(default);
        }

        uint version = _settings.Encrypt == EncryptMode.Strict ? TdsConstants.Tds80Version : TdsConstants.Tds74Version;
        Writer.BeginMessage(PacketType.Login7);
        Writer.WriteBytes(Login7.Build(_settings, version, sspiToken, fedAuthFeature));
        Writer.EndMessage();

        if (loginOnlyEncryption)
        {
            SetStream(_transport);
            _ssl!.Dispose();
            _ssl = null;
        }

        if (_discovery)
        {
            ReadFedAuthInfo();
            return;
        }

        ReadLoginResponse();

        if (_transport.CanTimeout)
        {
            _transport.ReadTimeout = Timeout.Infinite;
            _transport.WriteTimeout = Timeout.Infinite;
        }
    }

    private Stream OpenTransport(CancellationToken cancellationToken)
    {
        _settings.ResolveInstance();
        try
        {
            return OpenResolvedTransport(cancellationToken);
        }
        catch (Exception ex) when (_settings.ResolvesEndpoint && ex is SocketException or IOException or TimeoutException or OperationCanceledException)
        {
            // The instance may have restarted on another port or pipe: resolve it again next time.
            _settings.InvalidateInstance();
            throw;
        }
    }

    private Stream OpenResolvedTransport(CancellationToken cancellationToken)
    {
        if (_settings.PipeName != null)
        {
            string server = _settings.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? "." : _settings.Host;
            var pipe = new NamedPipeClientStream(server, _settings.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                pipe.ConnectAsync(cancellationToken).GetAwaiter().GetResult();
                pipe.ReadMode = PipeTransmissionMode.Byte;
            }
            catch
            {
                pipe.Dispose();
                throw;
            }
            return pipe;
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.NoDelay = true;
            socket.ConnectAsync(_settings.Host, _settings.Port, cancellationToken).AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        return new NetworkStream(socket, ownsSocket: true);
    }

    private SslClientAuthenticationOptions CreateTlsOptions(bool strict)
    {
        var options = new SslClientAuthenticationOptions
        {
            TargetHost = _settings.Host,
        };
        if (_settings.TrustServerCertificate)
        {
            options.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
        }
        if (strict)
        {
            options.ApplicationProtocols = [new SslApplicationProtocol(TdsConstants.Tds8AlpnProtocol)];
            // Windows caches TLS sessions per target host, whatever the mode: a strict connection
            // could resume the TLS 1.2 session of an earlier TDS 7.x connection to the same server
            // instead of negotiating TLS 1.3. Strict connections always do a full handshake.
            options.AllowTlsResume = false;
        }
        return options;
    }

    /// <summary><c>MSSQLSvc/fqdn:port</c> (<c>MSSQLSvc/fqdn</c> over a named pipe), as SQL Server registers it.</summary>
    private string GetServicePrincipalName()
    {
        if (_spnOverride != null)
        {
            return _spn = _spnOverride;
        }
        string host = _settings.Host;
        try
        {
            host = Dns.GetHostEntry(host).HostName;
        }
        catch (SocketException)
        {
            // Fall back to the name as given.
        }
        return _spn = FormatSpn(host);
    }

    private string FormatSpn(string host) => _settings.PipeName != null ? $"MSSQLSvc/{host}" : $"MSSQLSvc/{host}:{_settings.Port}";

    private byte[] NextSspiToken(ReadOnlySpan<byte> incoming)
    {
        byte[]? outgoing = _sspi!.GetOutgoingBlob(incoming, out NegotiateAuthenticationStatusCode status);
        if (status != NegotiateAuthenticationStatusCode.Completed && status != NegotiateAuthenticationStatusCode.ContinueNeeded)
        {
            _sspiFailed = true;
            throw new AdbcException(
                $"Integrated authentication failed ({status}) for service principal name '{_spn}'. If the server is registered under another name, set {SqlServerParameters.ServerSpn}",
                AdbcStatusCode.Unauthenticated);
        }
        return outgoing ?? [];
    }

    private void SetStream(Stream stream)
    {
        Reader.Stream = stream;
        Writer.Stream = stream;
    }

    private byte[] ReadMessageBytes()
    {
        using var buffer = new MemoryStream();
        while (!Reader.IsMessageComplete)
        {
            buffer.WriteByte(Reader.ReadByte());
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// Reads the server's reply to an MSAL-workflow LOGIN7: a FEDAUTHINFO token
    /// naming the token endpoint (STS URL) and the SPN the token is for. The
    /// server then waits for a token, which a discovery session never sends.
    /// </summary>
    private void ReadFedAuthInfo()
    {
        Reader.BeginMessage();
        while (!Reader.IsMessageComplete)
        {
            TokenType token = (TokenType)Reader.ReadByte();
            switch (token)
            {
                case TokenType.FedAuthInfo:
                    _entraInfo = ParseFedAuthInfo(Reader.ReadByteArray(Reader.ReadInt32()));
                    break;
                case TokenType.Done:
                    ReadDone(out _, out _, out _);
                    break;
                case TokenType.LoginAck:
                    throw new AdbcException("The server accepted the login without asking for an Entra token", AdbcStatusCode.InternalError);
                default:
                    if (!TryReadCommonToken(token))
                    {
                        throw new TdsProtocolException($"Unexpected token 0x{(byte)token:X2} during login");
                    }
                    break;
            }
        }
        if (_entraInfo == null)
        {
            try
            {
                ThrowIfErrors();
            }
            catch (SqlServerException ex)
            {
                throw new AdbcException($"The server does not offer Entra authentication: {ex.Message}", AdbcStatusCode.NotImplemented, ex);
            }
            throw new AdbcException("The server does not offer Entra authentication", AdbcStatusCode.NotImplemented);
        }
    }

    /// <summary>
    /// FEDAUTHINFO data: a count, then 9-byte entries (ID, length, offset from
    /// the count) locating UTF-16 strings: 0x01 the STS URL, 0x02 the SPN.
    /// </summary>
    internal static EntraInfo ParseFedAuthInfo(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            throw new TdsProtocolException("FEDAUTHINFO token too short");
        }
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(data);
        string? stsUrl = null, spn = null;
        for (int i = 0; i < count; i++)
        {
            int entry = 4 + i * 9;
            if (entry + 9 > data.Length)
            {
                throw new TdsProtocolException("FEDAUTHINFO entry beyond the token");
            }
            byte id = data[entry];
            int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(entry + 1)));
            int offset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(entry + 5)));
            if (offset < 4 || offset + length > data.Length)
            {
                throw new TdsProtocolException("FEDAUTHINFO data beyond the token");
            }
            string value = System.Text.Encoding.Unicode.GetString(data.Slice(offset, length));
            switch (id)
            {
                case 0x01:
                    stsUrl = value;
                    break;
                case 0x02:
                    spn = value;
                    break;
            }
        }
        if (stsUrl == null || spn == null)
        {
            throw new TdsProtocolException("FEDAUTHINFO lacks the STS URL or SPN");
        }
        return new EntraInfo(stsUrl, spn);
    }

    private void ReadLoginResponse()
    {
        bool loggedIn = false;
        while (true)
        {
            Reader.BeginMessage();
            byte[]? sspiChallenge = null;
            while (!Reader.IsMessageComplete)
            {
                TokenType token = (TokenType)Reader.ReadByte();
                switch (token)
                {
                    case TokenType.LoginAck:
                        ReadLoginAck();
                        loggedIn = true;
                        break;
                    case TokenType.Sspi:
                        sspiChallenge = Reader.ReadByteArray(Reader.ReadUInt16());
                        break;
                    case TokenType.Done:
                        ReadDone(out _, out _, out _);
                        break;
                    default:
                        if (!TryReadCommonToken(token))
                        {
                            throw new TdsProtocolException($"Unexpected token 0x{(byte)token:X2} during login");
                        }
                        break;
                }
            }

            ThrowIfErrors();

            if (sspiChallenge != null && _sspi != null)
            {
                // When the client side completes without a token to send (the
                // final SPNEGO leg), the server sends LOGINACK unprompted.
                byte[] next = NextSspiToken(sspiChallenge);
                if (next.Length > 0)
                {
                    Writer.BeginMessage(PacketType.Sspi);
                    Writer.WriteBytes(next);
                    Writer.EndMessage();
                }
                continue;
            }

            if (!loggedIn)
            {
                throw new AdbcException("The server did not acknowledge the login", AdbcStatusCode.Unauthenticated);
            }
            return;
        }
    }

    private void ReadLoginAck()
    {
        int length = Reader.ReadUInt16();
        Reader.ReadByte(); // interface
        Span<byte> version = stackalloc byte[4];
        Reader.ReadBytes(version);
        TdsVersion = BinaryPrimitives.ReadUInt32BigEndian(version);
        ServerProgramName = Reader.ReadBVarChar();
        byte major = Reader.ReadByte();
        byte minor = Reader.ReadByte();
        int build = (Reader.ReadByte() << 8) | Reader.ReadByte();
        ServerVersion = new Version(major, minor, build);
        _ = length;
    }

    /// <summary>
    /// Reads a token that can appear in any response (ENVCHANGE, INFO, ERROR,
    /// FEATUREEXTACK and other tokens this driver ignores). Returns false for
    /// tokens the caller must handle itself.
    /// </summary>
    public bool TryReadCommonToken(TokenType token)
    {
        switch (token)
        {
            case TokenType.EnvChange:
                ReadEnvChange();
                return true;
            case TokenType.Error:
                _errors.Add(ReadMessage());
                return true;
            case TokenType.Info:
                Messages.Add(ReadMessage());
                return true;
            case TokenType.FeatureExtAck:
                ReadFeatureExtAck();
                return true;
            case TokenType.Order:
            case TokenType.TabName:
            case TokenType.ColInfo:
                Reader.Skip(Reader.ReadUInt16());
                return true;
            case TokenType.ReturnStatus:
                Reader.Skip(4);
                return true;
            case TokenType.SessionState:
            case TokenType.FedAuthInfo:
                Reader.Skip(Reader.ReadInt32());
                return true;
            default:
                return false;
        }
    }

    /// <summary>Reads the body of a DONE, DONEPROC or DONEINPROC token.</summary>
    public void ReadDone(out DoneStatus status, out ushort command, out long rowCount)
    {
        status = (DoneStatus)Reader.ReadUInt16();
        command = Reader.ReadUInt16();
        rowCount = Reader.ReadInt64();
        if ((status & DoneStatus.Attention) != 0)
        {
            _attentionAcknowledged = true;
        }
    }

    /// <summary>DONE current-command value for SELECT, including variable assignment; its count is not an affected-row count.</summary>
    public const ushort SelectCommand = 0xC1;

    /// <summary>Throws the errors accumulated while reading the last response, if any.</summary>
    public void ThrowIfErrors()
    {
        if (_errors.Count > 0)
        {
            SqlServerError[] errors = _errors.ToArray();
            _errors.Clear();
            throw new SqlServerException(errors);
        }
    }

    /// <summary>Marks the session unusable after a protocol failure.</summary>
    public void MarkBroken() => _broken = true;

    /// <summary>Starts a request message; the caller writes its payload with <see cref="Writer"/>.</summary>
    public void BeginRequest(PacketType type)
    {
        if (_broken)
        {
            throw new AdbcException("The connection is broken and must be closed", AdbcStatusCode.InvalidState);
        }
        if (ActiveStream != null)
        {
            throw new AdbcException("A previous result stream is still open; read it to the end or dispose it first", AdbcStatusCode.InvalidState);
        }

        lock (_requestLock)
        {
            _requestState = RequestState.Sending;
            _cancelPending = false;
            _attentionSent = false;
            _attentionAcknowledged = false;
        }
        Writer.BeginMessage(type);
    }

    /// <summary>
    /// Sends the rest of the request message and starts reading the response.
    /// With <paramref name="abort"/>, the message is ended with the IGNORE bit
    /// instead, so the server discards it.
    /// </summary>
    public void EndRequest(bool abort = false)
    {
        if (abort)
        {
            Writer.AbortMessage();
        }
        else
        {
            Writer.EndMessage();
        }

        lock (_requestLock)
        {
            _requestState = RequestState.Receiving;
            if (_cancelPending)
            {
                // Cancel arrived while the request was being sent.
                SendAttention();
            }
        }
        Reader.BeginMessage();
    }

    /// <summary>
    /// Cancels the current request, if any; safe to call on any thread. While
    /// the request is being sent, the attention follows it; while the response
    /// is being read, it is sent at once. The reader then stops at the
    /// server's acknowledgment and throws <see cref="AdbcStatusCode.Cancelled"/>.
    /// </summary>
    public void Cancel()
    {
        lock (_requestLock)
        {
            switch (_requestState)
            {
                case RequestState.Sending:
                    _cancelPending = true;
                    break;
                case RequestState.Receiving when !_attentionSent:
                    SendAttention();
                    break;
            }
        }
    }

    /// <summary>Sends an attention; the caller holds _requestLock and the writer is between messages.</summary>
    private void SendAttention()
    {
        if (TdsTrace.Enabled)
        {
            TdsTrace.Packet("->", s_attentionPacket[0], s_attentionPacket[1], s_attentionPacket.Length);
        }
        // Written beside the reader, which may be blocked in a receive; the transport and TLS allow one of each at a time.
        Writer.Stream.Write(s_attentionPacket);
        Writer.Stream.Flush();
        _attentionSent = true;
    }

    public static AdbcException CancelledException() => new("The operation was cancelled", AdbcStatusCode.Cancelled);

    /// <summary>
    /// Completes a response whose last token has been read. If an attention is
    /// outstanding, reads on to its acknowledgment, which may come in a later
    /// message. Then throws for cancellation, or for the response's errors.
    /// </summary>
    private void CompleteResponse()
    {
        while (true)
        {
            lock (_requestLock)
            {
                if (!_attentionSent || _attentionAcknowledged)
                {
                    _requestState = RequestState.Idle;
                    break;
                }
            }
            Reader.BeginMessage();
            SkipTokens(null);
        }

        if (_attentionSent)
        {
            // Errors caused by the cancellation itself, or that the caller no longer wants.
            _errors.Clear();
            throw CancelledException();
        }
        ThrowIfErrors();
    }

    /// <summary>Sends a SQL batch request and starts reading the response.</summary>
    public void SendSqlBatch(string sql)
    {
        BeginRequest(PacketType.SqlBatch);
        WriteAllHeaders();
        Writer.WriteUtf16(sql);
        EndRequest();
    }

    /// <summary>
    /// Executes a batch and returns the total row count reported by its
    /// statements, or -1 if none reported one. Any result sets are skipped.
    /// </summary>
    public long ExecuteNonQuery(string sql)
    {
        SendSqlBatch(sql);
        return DrainResponse();
    }

    /// <summary>
    /// Reads the response up to the first result set and returns its columns,
    /// or null if the response ends without one. On null, the response has
    /// been fully read and any server errors thrown.
    /// </summary>
    public TdsColumn[]? ReadUntilResultSet()
    {
        try
        {
            while (!Reader.IsMessageComplete)
            {
                TokenType token = (TokenType)Reader.ReadByte();
                switch (token)
                {
                    case TokenType.ColMetadata:
                        TdsColumn[]? columns = TdsColumn.ReadMetadata(Reader);
                        if (columns == null)
                        {
                            continue;
                        }
                        if (_errors.Count > 0)
                        {
                            // An earlier statement failed: report that rather than a later result.
                            DrainResponse(columns);
                            return null;
                        }
                        return columns;
                    case TokenType.Done:
                    case TokenType.DoneProc:
                    case TokenType.DoneInProc:
                        ReadDone(out _, out _, out _);
                        break;
                    default:
                        ReadCommonTokenOrThrow(token);
                        break;
                }
            }
        }
        catch (TdsProtocolException)
        {
            MarkBroken();
            throw;
        }

        CompleteResponse();
        return null;
    }

    /// <summary>
    /// Reads the rest of the current response, skipping any rows, and throws
    /// any server errors it contained. <paramref name="columns"/> describes
    /// the result set the reader is positioned in, if any.
    /// </summary>
    public long DrainResponse(TdsColumn[]? columns = null)
    {
        long total = SkipTokens(columns);
        CompleteResponse();
        return total;
    }

    /// <summary>Reads to the end of the current message and returns its total row count, or -1.</summary>
    private long SkipTokens(TdsColumn[]? columns)
    {
        long total = -1;
        byte[] nullBitmap = [];
        try
        {
            while (!Reader.IsMessageComplete)
            {
                TokenType token = (TokenType)Reader.ReadByte();
                switch (token)
                {
                    case TokenType.ColMetadata:
                        columns = TdsColumn.ReadMetadata(Reader) ?? columns;
                        break;
                    case TokenType.Row:
                        foreach (TdsColumn column in columns!)
                        {
                            column.SkipValue(Reader);
                        }
                        break;
                    case TokenType.NbcRow:
                        int bitmapLength = (columns!.Length + 7) >> 3;
                        if (nullBitmap.Length < bitmapLength)
                        {
                            nullBitmap = new byte[bitmapLength];
                        }
                        Reader.ReadBytes(nullBitmap.AsSpan(0, bitmapLength));
                        for (int i = 0; i < columns.Length; i++)
                        {
                            if ((nullBitmap[i >> 3] & (1 << (i & 7))) == 0)
                            {
                                columns[i].SkipValue(Reader);
                            }
                        }
                        break;
                    case TokenType.Done:
                    case TokenType.DoneProc:
                    case TokenType.DoneInProc:
                        ReadDone(out DoneStatus status, out ushort command, out long rowCount);
                        if ((status & DoneStatus.Count) != 0 && command != SelectCommand)
                        {
                            total = Math.Max(total, 0) + rowCount;
                        }
                        break;
                    default:
                        ReadCommonTokenOrThrow(token);
                        break;
                }
            }
        }
        catch (TdsProtocolException)
        {
            MarkBroken();
            throw;
        }
        return total;
    }

    private void ReadCommonTokenOrThrow(TokenType token)
    {
        if (!TryReadCommonToken(token))
        {
            throw new TdsProtocolException($"Unexpected token 0x{(byte)token:X2}");
        }
    }

    /// <summary>Writes ALL_HEADERS with the transaction descriptor header.</summary>
    public void WriteAllHeaders()
    {
        Writer.WriteInt32(22);           // total length
        Writer.WriteInt32(18);           // header length
        Writer.WriteUInt16(2);           // transaction descriptor header
        Writer.WriteInt64(TransactionDescriptor);
        Writer.WriteInt32(1);            // outstanding request count
    }

    private SqlServerError ReadMessage()
    {
        Reader.ReadUInt16(); // length
        int number = Reader.ReadInt32();
        byte state = Reader.ReadByte();
        byte errorClass = Reader.ReadByte();
        string message = Reader.ReadUsVarChar();
        string server = Reader.ReadBVarChar();
        string procedure = Reader.ReadBVarChar();
        int line = Reader.ReadInt32();
        return new SqlServerError(number, state, errorClass, message, server, procedure, line);
    }

    private void ReadEnvChange()
    {
        int remaining = Reader.ReadUInt16();
        var type = (EnvChangeType)Reader.ReadByte();
        remaining -= 1;

        switch (type)
        {
            case EnvChangeType.Database:
            case EnvChangeType.PacketSize:
            {
                int length = Reader.ReadByte();
                string value = Reader.ReadUtf16String(length);
                remaining -= 1 + length * 2;
                if (type == EnvChangeType.Database)
                {
                    Database = value;
                }
                else
                {
                    int packetSize = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                    Reader.SetPacketSize(packetSize);
                    Writer.SetPacketSize(packetSize);
                }
                break;
            }
            case EnvChangeType.SqlCollation:
            {
                int length = Reader.ReadByte();
                DefaultCollation = Reader.ReadByteArray(length);
                remaining -= 1 + length;
                break;
            }
            case EnvChangeType.BeginTransaction:
            {
                int length = Reader.ReadByte();
                TransactionDescriptor = length == 8 ? Reader.ReadInt64() : 0;
                if (length != 8)
                {
                    Reader.Skip(length);
                }
                remaining -= 1 + length;
                break;
            }
            case EnvChangeType.CommitTransaction:
            case EnvChangeType.RollbackTransaction:
            case EnvChangeType.DefectTransaction:
            case EnvChangeType.TransactionEnded:
                TransactionDescriptor = 0;
                break;
            case EnvChangeType.Routing:
                throw new AdbcException("Server redirection (routing) is not yet supported", AdbcStatusCode.NotImplemented);
        }

        Reader.Skip(remaining);
    }

    private void ReadFeatureExtAck()
    {
        while (true)
        {
            var feature = (FeatureId)Reader.ReadByte();
            if (feature == FeatureId.Terminator)
            {
                return;
            }
            int length = Reader.ReadInt32();
            if (feature is FeatureId.Utf8Support or FeatureId.JsonSupport or FeatureId.VectorSupport && length >= 1)
            {
                byte value = Reader.ReadByte();
                length -= 1;
                switch (feature)
                {
                    case FeatureId.Utf8Support:
                        Utf8Supported = (value & 0x01) != 0;
                        break;
                    case FeatureId.JsonSupport:
                        JsonVersion = value;
                        break;
                    case FeatureId.VectorSupport:
                        VectorVersion = value;
                        break;
                }
            }
            Reader.Skip(length);
        }
    }

    public void Dispose()
    {
        _sspi?.Dispose();
        _ssl?.Dispose();
        _transport.Dispose();
    }
}
