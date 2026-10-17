// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Data.SqlClient;

namespace Clast.Drivers.SqlServer.Benchmarks;

/// <summary>
/// Benchmark environment, read from environment variables so the same binaries
/// run against any server.
/// </summary>
internal static class BenchmarkSettings
{
    private static string Server => Environment.GetEnvironmentVariable("MSSQL_BENCH_SERVER") ?? "localhost";

    private static string Database => Environment.GetEnvironmentVariable("MSSQL_BENCH_DATABASE") ?? "tempdb";

    /// <summary>
    /// A Microsoft Entra access token, read from the file named by
    /// <c>MSSQL_BENCH_ACCESS_TOKEN_FILE</c>, for every driver to log in with
    /// (Fabric, Azure SQL); null for integrated authentication.
    /// </summary>
    public static string? AccessToken { get; } =
        Environment.GetEnvironmentVariable("MSSQL_BENCH_ACCESS_TOKEN_FILE") is { Length: > 0 } path
            ? File.ReadAllText(Environment.ExpandEnvironmentVariables(path)).Trim()
            : null;

    /// <summary>SqlClient connection string; also used to create the benchmark tables.</summary>
    public static string SqlClientConnectionString =>
        Environment.GetEnvironmentVariable("MSSQL_BENCH_CONNECTION")
        ?? (AccessToken != null
            ? $"Server=tcp:{Server};Database={Database}"
            : $"Server=tcp:{Server};Database={Database};Integrated Security=true;TrustServerCertificate=true");

    /// <summary>A SqlClient connection, with <see cref="AccessToken"/> when there is one.</summary>
    public static SqlConnection NewSqlConnection(string connectionString) =>
        new(connectionString) { AccessToken = AccessToken };

    /// <summary>
    /// The encryption settings to measure: both locally; with an access token
    /// only "full", as Entra servers require encryption.
    /// </summary>
    public static IEnumerable<string> Encryptions() => AccessToken != null ? ["full"] : ["full", "login"];

    /// <summary>
    /// Benchmark encryption settings: "full" encrypts the whole session (the
    /// default for this driver and SqlClient); "login" encrypts only the login.
    /// </summary>
    public static string SqlClientConnectionStringFor(string encryption) =>
        SqlClientConnectionString + (encryption == "login" ? ";Encrypt=false" : ";Encrypt=true");

    public static string GoDriverUriFor(string encryption) =>
        GoDriverUri + (encryption == "login" ? "&encrypt=false" : "&encrypt=true");

    public static IReadOnlyDictionary<string, string> ClastOptionsFor(string encryption) =>
        new Dictionary<string, string>(ClastOptions)
        {
            [SqlServerParameters.Encrypt] = encryption == "login" ? "optional" : "mandatory",
        };

    /// <summary>Path to the Go MSSQL ADBC driver shared library; the Go case is skipped when unset.</summary>
    public static string? GoDriverPath =>
        Environment.GetEnvironmentVariable("MSSQL_BENCH_GO_DRIVER") is { Length: > 0 } path && File.Exists(path) ? path : null;

    /// <summary>The <c>uri</c> option for the Go driver.</summary>
    public static string GoDriverUri =>
        Environment.GetEnvironmentVariable("MSSQL_BENCH_GO_URI")
        ?? (AccessToken != null
            // go-mssqldb takes a ready token as the password of this method.
            ? $"sqlserver://{Server}?database={Database}&fedauth=ActiveDirectoryServicePrincipalAccessToken&password={Uri.EscapeDataString(AccessToken)}"
            : $"sqlserver://{Server}?database={Database}&trusted_connection=true&TrustServerCertificate=true");

    /// <summary>Options for this repository's driver.</summary>
    public static IReadOnlyDictionary<string, string> ClastOptions => AccessToken != null
        ? new Dictionary<string, string>
        {
            [SqlServerParameters.Server] = Server,
            [SqlServerParameters.Database] = Database,
            [SqlServerParameters.AccessToken] = AccessToken,
        }
        : new Dictionary<string, string>
        {
            [SqlServerParameters.Server] = Server,
            [SqlServerParameters.Database] = Database,
            [SqlServerParameters.IntegratedSecurity] = "true",
            [SqlServerParameters.TrustServerCertificate] = "true",
        };

    /// <summary><c>true</c> once the driver can execute queries (milestone M2).</summary>
    public static bool ClastReadsEnabled =>
        Environment.GetEnvironmentVariable("MSSQL_BENCH_CLAST") == "1";
}
