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

    public override void SetOption(string key, string value)
    {
        _options[key] = value;
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
