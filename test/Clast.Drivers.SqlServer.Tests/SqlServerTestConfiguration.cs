// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace Clast.Drivers.SqlServer.Tests;

/// <summary>
/// Test settings, loaded from the JSON file named by <see cref="EnvironmentVariable"/>.
/// </summary>
public sealed class SqlServerTestConfiguration
{
    public const string EnvironmentVariable = "MSSQL_TEST_CONFIG_FILE";

    /// <summary>Driver options passed to <see cref="SqlServerDriver.Open"/>.</summary>
    [JsonPropertyName("options")]
    public Dictionary<string, string> Options { get; set; } = new();

    /// <summary>SqlClient connection string for the same server; used as the correctness oracle.</summary>
    [JsonPropertyName("sqlClientConnectionString")]
    public string SqlClientConnectionString { get; set; } = "";

    /// <summary>Whether the server has a certificate that allows TDS 8 strict encryption.</summary>
    [JsonPropertyName("supportsStrictEncryption")]
    public bool SupportsStrictEncryption { get; set; }

    /// <summary>
    /// The server's certificate is trusted by this machine and names the
    /// configured server, so connections can validate it (no trust_server_certificate).
    /// </summary>
    [JsonPropertyName("serverCertificateTrusted")]
    public bool ServerCertificateTrusted { get; set; }

    public static bool IsAvailable =>
        Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } path && File.Exists(path);

    public static SqlServerTestConfiguration Load()
    {
        Skip.IfNot(IsAvailable, $"{EnvironmentVariable} is not set to an existing file");
        string path = Environment.GetEnvironmentVariable(EnvironmentVariable)!;
        return JsonSerializer.Deserialize<SqlServerTestConfiguration>(File.ReadAllText(path))
            ?? throw new InvalidOperationException($"Could not parse {path}");
    }
}
