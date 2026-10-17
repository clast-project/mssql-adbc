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
    public int BatchRows { get; private set; } = 65536;

    public static SqlServerConnectionSettings Parse(IReadOnlyDictionary<string, string> options)
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
        if (options.TryGetValue(SqlServerParameters.BatchRows, out string? batchRows))
        {
            int rows = ParseInt(SqlServerParameters.BatchRows, batchRows);
            if (rows <= 0)
            {
                throw InvalidOption(SqlServerParameters.BatchRows, batchRows);
            }
            settings.BatchRows = rows;
        }

        if (!settings.IntegratedSecurity && settings.Username == null)
        {
            throw new AdbcException(
                $"Either '{SqlServerParameters.Username}' or '{SqlServerParameters.IntegratedSecurity}' must be set",
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
            return;
        }

        if (value.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
        {
            value = value.Substring(4);
        }

        int comma = value.IndexOf(',');
        if (comma >= 0)
        {
            Port = ParseInt(SqlServerParameters.Server, value.Substring(comma + 1).Trim());
            value = value.Substring(0, comma).Trim();
        }

        if (value.Contains('\\'))
        {
            // Resolving a named instance needs the SQL Browser (SSRP); not yet supported.
            throw new AdbcException(
                $"Named instances are not yet supported; specify the port instead ('{server}')",
                AdbcStatusCode.NotImplemented);
        }

        Host = NormalizeHost(value);
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
