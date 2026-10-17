// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow.Adbc;
using Clast.Drivers.SqlServer.Tds;

namespace Clast.Drivers.SqlServer;

internal enum EncryptMode
{
    /// <summary>Encrypt the login only, unless the server requires more.</summary>
    Optional,

    /// <summary>Encrypt the whole session (TDS 7.x, TLS inside PRELOGIN).</summary>
    Mandatory,

    /// <summary>TLS before any TDS traffic (TDS 8).</summary>
    Strict,
}

/// <summary>
/// Parsed connection options.
/// </summary>
internal sealed class SqlServerConnectionSettings
{
    public string Host { get; private set; } = "localhost";
    public int Port { get; private set; } = TdsConstants.DefaultPort;

    /// <summary>Named pipe name (for example <c>sql\query</c>) when connecting over a pipe; otherwise null.</summary>
    public string? PipeName { get; private set; }

    /// <summary>A named instance still to be resolved through the SQL Server Browser; null otherwise.</summary>
    public string? InstanceName { get; private set; }

    /// <summary>A LocalDB instance (<c>(localdb)\name</c>) to start and connect to over its pipe; null otherwise.</summary>
    public string? LocalDbInstance { get; private set; }

    /// <summary>True when the endpoint is only known after asking the SQL Server Browser or LocalDB.</summary>
    public bool ResolvesEndpoint => InstanceName != null || LocalDbInstance != null;

    /// <summary>The server string asked for TCP (<c>tcp:</c>), so a named instance must resolve to a TCP port.</summary>
    private bool _tcpOnly;

    private readonly object _resolveLock = new();
    private bool _resolved;

    public string Database { get; private set; } = "";
    public string? Username { get; private set; }
    public string? Password { get; private set; }
    public bool IntegratedSecurity { get; private set; }
    public EncryptMode Encrypt { get; private set; } = EncryptMode.Mandatory;
    public bool TrustServerCertificate { get; private set; }
    /// <summary>Requested packet size; the server may grant less (16384 at most with TLS on current versions).</summary>
    public int PacketSize { get; private set; } = TdsConstants.MaxPacketSize;
    public string ApplicationName { get; private set; } = "Clast.Drivers.SqlServer";
    public TimeSpan ConnectTimeout { get; private set; } = TimeSpan.FromSeconds(15);
    /// <summary>Retries after a login fails with a transient error (see <see cref="TdsSession.IsTransient"/>).</summary>
    public int ConnectRetryCount { get; private set; }
    public TimeSpan ConnectRetryInterval { get; private set; } = TimeSpan.FromSeconds(10);
    public int BatchRows { get; private set; } = 65536;
    public int ParallelReads { get; private set; } = 1;
    public string? ServerSpn { get; private set; }

    /// <summary>The server name sent in LOGIN7; after routing, the routed <c>host,port</c>, as SqlClient sends it.</summary>
    public string LoginServerName => _loginServerName ?? Host;

    private string? _loginServerName;

    /// <summary>A Microsoft Entra access token to log in with, instead of other credentials.</summary>
    public string? AccessToken { get; private set; }

    private EntraInfo? _entraInfo;

    /// <param name="requireCredentials">False to parse without credentials, as for Entra discovery before a token is available.</param>
    public static SqlServerConnectionSettings Parse(IReadOnlyDictionary<string, string> options, bool requireCredentials = true)
    {
        var settings = new SqlServerConnectionSettings();

        if (options.TryGetValue(SqlServerParameters.Server, out string? server))
        {
            settings.ParseServer(server);
        }
        if (options.TryGetValue(SqlServerParameters.Database, out string? database))
        {
            settings.Database = database;
        }
        if (options.TryGetValue(SqlServerParameters.Username, out string? username))
        {
            settings.Username = username;
        }
        if (options.TryGetValue(SqlServerParameters.Password, out string? password))
        {
            settings.Password = password;
        }
        if (options.TryGetValue(SqlServerParameters.IntegratedSecurity, out string? integrated))
        {
            settings.IntegratedSecurity = ParseBool(SqlServerParameters.IntegratedSecurity, integrated);
        }
        if (options.TryGetValue(SqlServerParameters.Encrypt, out string? encrypt))
        {
            settings.Encrypt = encrypt.ToLowerInvariant() switch
            {
                "optional" or "false" or "no" => EncryptMode.Optional,
                "mandatory" or "true" or "yes" => EncryptMode.Mandatory,
                "strict" => EncryptMode.Strict,
                _ => throw InvalidOption(SqlServerParameters.Encrypt, encrypt),
            };
        }
        if (options.TryGetValue(SqlServerParameters.TrustServerCertificate, out string? trust))
        {
            settings.TrustServerCertificate = ParseBool(SqlServerParameters.TrustServerCertificate, trust);
        }
        if (options.TryGetValue(SqlServerParameters.PacketSize, out string? packetSize))
        {
            int size = ParseInt(SqlServerParameters.PacketSize, packetSize);
            if (size < TdsConstants.MinPacketSize || size > TdsConstants.MaxPacketSize)
            {
                throw InvalidOption(SqlServerParameters.PacketSize, packetSize);
            }
            settings.PacketSize = size;
        }
        if (options.TryGetValue(SqlServerParameters.ApplicationName, out string? appName))
        {
            settings.ApplicationName = appName;
        }
        if (options.TryGetValue(SqlServerParameters.ConnectTimeout, out string? timeout))
        {
            settings.ConnectTimeout = TimeSpan.FromSeconds(ParseInt(SqlServerParameters.ConnectTimeout, timeout));
        }
        if (options.TryGetValue(SqlServerParameters.ConnectRetryCount, out string? retryCount))
        {
            int count = ParseInt(SqlServerParameters.ConnectRetryCount, retryCount);
            settings.ConnectRetryCount = count is >= 0 and <= 255 ? count : throw InvalidOption(SqlServerParameters.ConnectRetryCount, retryCount);
        }
        else
        {
            settings.ConnectRetryCount = DefaultConnectRetryCount(settings.Host);
        }
        if (options.TryGetValue(SqlServerParameters.ConnectRetryInterval, out string? retryInterval))
        {
            int seconds = ParseInt(SqlServerParameters.ConnectRetryInterval, retryInterval);
            settings.ConnectRetryInterval = seconds is >= 1 and <= 60
                ? TimeSpan.FromSeconds(seconds)
                : throw InvalidOption(SqlServerParameters.ConnectRetryInterval, retryInterval);
        }
        if (options.TryGetValue(SqlServerParameters.BatchRows, out string? batchRows))
        {
            int rows = ParseInt(SqlServerParameters.BatchRows, batchRows);
            if (rows <= 0)
            {
                throw InvalidOption(SqlServerParameters.BatchRows, batchRows);
            }
            settings.BatchRows = rows;
        }

        if (options.TryGetValue(SqlServerParameters.ServerSpn, out string? spn) && spn.Length > 0)
        {
            settings.ServerSpn = spn;
        }
        if (options.TryGetValue(SqlServerParameters.ParallelReads, out string? parallel))
        {
            settings.ParallelReads = ParseParallelReads(parallel);
        }

        // LocalDB has no TLS certificate and answers PRELOGIN with "encryption not
        // supported"; its traffic stays on a local pipe to the user's own instance.
        // Unless encryption was asked for explicitly, use login-only encryption there.
        if (settings.LocalDbInstance != null && !options.ContainsKey(SqlServerParameters.Encrypt))
        {
            settings.Encrypt = EncryptMode.Optional;
        }

        if (options.TryGetValue(SqlServerParameters.AccessToken, out string? token) && token.Length > 0)
        {
            if (settings.IntegratedSecurity || settings.Username != null || settings.Password != null)
            {
                throw new AdbcException(
                    $"'{SqlServerParameters.AccessToken}' cannot be combined with a username, password or integrated security",
                    AdbcStatusCode.InvalidArgument);
            }
            settings.AccessToken = token;
        }

        if (requireCredentials && !settings.IntegratedSecurity && settings.Username == null && settings.AccessToken == null)
        {
            throw new AdbcException(
                $"One of '{SqlServerParameters.Username}', '{SqlServerParameters.IntegratedSecurity}' or '{SqlServerParameters.AccessToken}' must be set",
                AdbcStatusCode.InvalidArgument);
        }

        return settings;
    }

    /// <summary>
    /// Accepts <c>host</c>, <c>host,port</c>, <c>tcp:host[,port]</c>, <c>np:\\host\pipe\name</c>,
    /// and <c>.</c> or <c>(local)</c> for the local machine.
    /// </summary>
    private void ParseServer(string server)
    {
        string value = server.Trim();

        if (value.StartsWith("np:", StringComparison.OrdinalIgnoreCase) && !value.StartsWith(@"np:\\", StringComparison.Ordinal))
        {
            // np:host or np:host\instance: the instance's standard pipe; no browser needed.
            string target = value.Substring(3);
            int slash = target.IndexOf('\\');
            string? instance = slash >= 0 ? target.Substring(slash + 1) : null;
            if (instance is "")
            {
                throw InvalidOption(SqlServerParameters.Server, server);
            }
            Host = NormalizeHost(slash >= 0 ? target.Substring(0, slash) : target);
            PipeName = instance == null || IsDefaultInstance(instance) ? @"sql\query" : $@"MSSQL${instance}\sql\query";
            RequireNamedPipes();
            return;
        }

        if (value.StartsWith("np:", StringComparison.OrdinalIgnoreCase))
        {
            // np:\\host\pipe\name
            string path = value.Substring(3);
            const string pipeMarker = @"\pipe\";
            int pipeIndex = path.IndexOf(pipeMarker, StringComparison.OrdinalIgnoreCase);
            if (!path.StartsWith(@"\\", StringComparison.Ordinal) || pipeIndex < 0)
            {
                throw InvalidOption(SqlServerParameters.Server, server);
            }
            Host = NormalizeHost(path.Substring(2, pipeIndex - 2));
            PipeName = path.Substring(pipeIndex + pipeMarker.Length);
            RequireNamedPipes();
            return;
        }

        if (value.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
        {
            value = value.Substring(4);
            _tcpOnly = true;
        }

        bool explicitPort = false;
        int comma = value.IndexOf(',');
        if (comma >= 0)
        {
            Port = ParseInt(SqlServerParameters.Server, value.Substring(comma + 1).Trim());
            value = value.Substring(0, comma).Trim();
            explicitPort = true;
        }

        if (value.StartsWith(@"(localdb)\", StringComparison.OrdinalIgnoreCase))
        {
            // (localdb)\name, or (localdb)\.\name for a shared instance.
            string localDb = value.Substring(@"(localdb)\".Length).Trim();
            if (localDb.Length == 0 || _tcpOnly || explicitPort)
            {
                throw InvalidOption(SqlServerParameters.Server, server);
            }
            if (!OperatingSystem.IsWindows())
            {
                throw new AdbcException("LocalDB is only available on Windows", AdbcStatusCode.NotImplemented);
            }
            LocalDbInstance = localDb;
            Host = "localhost";
            return;
        }

        int backslash = value.IndexOf('\\');
        if (backslash >= 0)
        {
            // host\instance: resolved through the SQL Server Browser when connecting, unless a
            // port was given or it is the default instance.
            string instance = value.Substring(backslash + 1).Trim();
            if (instance.Length == 0 || instance.Contains('\\'))
            {
                throw InvalidOption(SqlServerParameters.Server, server);
            }
            value = value.Substring(0, backslash).Trim();
            if (!explicitPort && !IsDefaultInstance(instance))
            {
                InstanceName = instance;
            }
        }

        Host = NormalizeHost(value);
    }

    /// <summary>
    /// SQL Server named pipes are Windows pipes (local, or remote over SMB).
    /// .NET's named pipes elsewhere are Unix domain sockets, and SQL Server on
    /// Linux does not offer pipes, so other systems connect over TCP only.
    /// </summary>
    private static void RequireNamedPipes()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new AdbcException("Named pipes are only supported on Windows; connect over TCP instead (host or host,port)", AdbcStatusCode.NotImplemented);
        }
    }

    /// <summary>MSSQLSERVER names the default instance, as with Microsoft's native client.</summary>
    private static bool IsDefaultInstance(string instance) => string.Equals(instance, "MSSQLSERVER", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves a named instance to its TCP port (or, unless TCP was asked for,
    /// its named pipe) through the SQL Server Browser. Done once per settings,
    /// so the helper sessions of parallel reads reuse it.
    /// </summary>
    public void ResolveInstance()
    {
        if (!ResolvesEndpoint)
        {
            return;
        }
        lock (_resolveLock)
        {
            if (_resolved)
            {
                return;
            }
            if (LocalDbInstance != null)
            {
                ResolveLocalDb();
                _resolved = true;
                return;
            }
            SqlBrowser.Instance instance = SqlBrowser.Resolve(Host, InstanceName!, ConnectTimeout);
            if (instance.TcpPort is int port)
            {
                Port = port;
                PipeName = null;
            }
            else if (!_tcpOnly && instance.PipeName != null && OperatingSystem.IsWindows())
            {
                PipeName = instance.PipeName;
            }
            else
            {
                // Named pipes are a Windows (SMB) transport; elsewhere only TCP is possible.
                bool pipes = !_tcpOnly && OperatingSystem.IsWindows();
                throw new AdbcException(
                    $"Instance '{InstanceName}' on '{Host}' does not accept TCP connections{(pipes ? " or named pipes" : "")}",
                    AdbcStatusCode.IOError);
            }
            _resolved = true;
        }
    }

    /// <summary>Starts the LocalDB instance and takes the pipe it reports (<c>np:\\.\pipe\LOCALDB#...\tsql\query</c>).</summary>
    private void ResolveLocalDb()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new AdbcException("LocalDB is only available on Windows", AdbcStatusCode.NotImplemented);
        }
        string endpoint = LocalDb.StartInstance(LocalDbInstance!);
        const string marker = @"\pipe\";
        int pipe = endpoint.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (!endpoint.StartsWith(@"np:\\", StringComparison.OrdinalIgnoreCase) || pipe < 0)
        {
            throw new AdbcException($"LocalDB instance '{LocalDbInstance}' reported an unexpected endpoint '{endpoint}'", AdbcStatusCode.IOError);
        }
        Host = NormalizeHost(endpoint.Substring(5, pipe - 5));
        PipeName = endpoint.Substring(pipe + marker.Length);
    }

    /// <summary>The server's Entra authority and audience, asked for once (see <see cref="TdsSession.DiscoverEntra"/>).</summary>
    public EntraInfo DiscoverEntra()
    {
        lock (_resolveLock)
        {
            return _entraInfo ??= TdsSession.DiscoverEntra(this);
        }
    }

    /// <summary>
    /// The settings for logging in again where a server routed the login: TCP
    /// to <paramref name="host"/> and <paramref name="port"/> (whose name TLS
    /// then validates and the default SPN uses), with the same credentials,
    /// database and options, and what is left of the connect timeout.
    /// </summary>
    public SqlServerConnectionSettings RoutedTo(string host, int port, TimeSpan timeout)
    {
        var routed = (SqlServerConnectionSettings)MemberwiseClone();
        routed.Host = host;
        routed.Port = port;
        routed.PipeName = null;
        routed.InstanceName = null;
        routed.LocalDbInstance = null;
        routed._loginServerName = $"{host},{port}";
        routed.ConnectTimeout = timeout;
        routed._entraInfo = null;
        return routed;
    }

    /// <summary>These settings with what is left of the connect timeout, for retrying a login.</summary>
    public SqlServerConnectionSettings WithConnectTimeout(TimeSpan timeout)
    {
        var copy = (SqlServerConnectionSettings)MemberwiseClone();
        copy.ConnectTimeout = timeout;
        return copy;
    }

    private static readonly string[] s_azureSqlSuffixes =
    [
        ".database.windows.net",
        ".database.cloudapi.de",
        ".database.usgovcloudapi.net",
        ".database.chinacloudapi.cn",
        ".database.fabric.microsoft.com",
    ];

    private static readonly string[] s_warehouseSuffixes =
    [
        "datawarehouse.fabric.microsoft.com",
        ".pbidedicated.microsoft.com",
        ".pbidedicated.windows.net",
    ];

    /// <summary>
    /// The connect retry count for a host, by its name, as SqlClient picks it:
    /// Synapse serverless (<c>-ondemand</c>) and Fabric/Power BI warehouses 5,
    /// Azure SQL 2. Unlike SqlClient, other servers get none: the transient
    /// errors are Azure's, and on other servers the one they share (4060,
    /// "cannot open database") is nearly always a wrong name, which a retry
    /// only reports 10 seconds later.
    /// </summary>
    internal static int DefaultConnectRetryCount(string host)
    {
        bool onDemand = host.Contains("-ondemand.", StringComparison.OrdinalIgnoreCase);
        if (s_warehouseSuffixes.Any(s => host.EndsWith(s, StringComparison.OrdinalIgnoreCase))
            || (onDemand && (host.Contains(".sql.azuresynapse.", StringComparison.OrdinalIgnoreCase)
                || s_azureSqlSuffixes.Any(s => host.EndsWith(s, StringComparison.OrdinalIgnoreCase)))))
        {
            return 5;
        }
        if (s_azureSqlSuffixes.Any(s => host.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
        {
            return 2;
        }
        return host.Contains(".sql.azuresynapse.", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

    /// <summary>Forgets a resolved endpoint (after connecting to it failed), so the next connection asks the browser again.</summary>
    public void InvalidateInstance()
    {
        lock (_resolveLock)
        {
            _resolved = false;
        }
    }

    public static int ParseParallelReads(string value)
    {
        int count = ParseInt(SqlServerParameters.ParallelReads, value);
        return count is >= 1 and <= 256 ? count : throw InvalidOption(SqlServerParameters.ParallelReads, value);
    }

    private static string NormalizeHost(string host) =>
        host is "." or "(local)" or "" ? "localhost" : host;

    private static bool ParseBool(string key, string value) => value.ToLowerInvariant() switch
    {
        "true" or "yes" or "1" => true,
        "false" or "no" or "0" => false,
        _ => throw InvalidOption(key, value),
    };

    private static int ParseInt(string key, string value) =>
        int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int result)
            ? result
            : throw InvalidOption(key, value);

    private static AdbcException InvalidOption(string key, string value) =>
        new AdbcException($"Invalid value '{value}' for option '{key}'", AdbcStatusCode.InvalidArgument);
}

/// <summary>
/// What a server reports for Entra authentication: the authority (STS URL)
/// to request a token from, and the audience (SPN) the token is for.
/// </summary>
internal sealed record EntraInfo(string Authority, string Audience);
