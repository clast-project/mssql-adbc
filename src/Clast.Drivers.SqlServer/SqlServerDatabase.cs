// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow.Adbc;

namespace Clast.Drivers.SqlServer;

/// <summary>
/// Holds database-level options; each <see cref="Connect"/> opens a new TDS session.
/// </summary>
public sealed class SqlServerDatabase : AdbcDatabase
{
    private readonly Dictionary<string, string> _options;

    internal SqlServerDatabase(IReadOnlyDictionary<string, string> options)
    {
        _options = new Dictionary<string, string>(options);
    }

    private SqlServerConnectionSettings? _discoverySettings;

    public override void SetOption(string key, string value)
    {
        _options[key] = value;
        _discoverySettings = null;
    }

    /// <summary>
    /// Returns an option as it was set (never the password or access token),
    /// or the server's Entra authority or audience, which are found with a
    /// probe handshake that needs no credentials.
    /// </summary>
    public override string GetOption(string key)
    {
        if (key is SqlServerParameters.EntraAuthority or SqlServerParameters.EntraAudience)
        {
            _discoverySettings ??= SqlServerConnectionSettings.Parse(_options, requireCredentials: false);
            EntraInfo info = _discoverySettings.DiscoverEntra();
            return key == SqlServerParameters.EntraAuthority ? info.Authority : info.Audience;
        }
        if (key is not (SqlServerParameters.Password or SqlServerParameters.AccessToken) && _options.TryGetValue(key, out string? value))
        {
            return value;
        }
        throw SqlServerParameters.OptionNotFound(key);
    }

    public override AdbcConnection Connect(IReadOnlyDictionary<string, string>? options)
    {
        var merged = new Dictionary<string, string>(_options);
        if (options != null)
        {
            foreach (KeyValuePair<string, string> pair in options)
            {
                merged[pair.Key] = pair.Value;
            }
        }

        return new SqlServerConnection(merged);
    }
}
