// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Clast.Drivers.SqlServer.Benchmarks;

/// <summary>
/// Benchmark environment, read from environment variables so the same binaries
/// run against any server.
/// </summary>
internal static class BenchmarkSettings
{
    /// <summary>SqlClient connection string; also used to create the benchmark tables.</summary>
    public static string SqlClientConnectionString =>
        Environment.GetEnvironmentVariable("MSSQL_BENCH_CONNECTION")
        ?? "Server=tcp:localhost;Database=tempdb;Integrated Security=true;TrustServerCertificate=true";

    /// <summary>Path to the Go MSSQL ADBC driver shared library; the Go case is skipped when unset.</summary>
    public static string? GoDriverPath =>
        Environment.GetEnvironmentVariable("MSSQL_BENCH_GO_DRIVER") is { Length: > 0 } path && File.Exists(path) ? path : null;

    /// <summary>The <c>uri</c> option for the Go driver.</summary>
    public static string GoDriverUri =>
        Environment.GetEnvironmentVariable("MSSQL_BENCH_GO_URI")
        ?? "sqlserver://localhost?database=tempdb&trusted_connection=true&TrustServerCertificate=true";

    /// <summary>Options for this repository's driver.</summary>
    public static IReadOnlyDictionary<string, string> ClastOptions => new Dictionary<string, string>
    {
        [SqlServerParameters.Server] = Environment.GetEnvironmentVariable("MSSQL_BENCH_SERVER") ?? "localhost",
        [SqlServerParameters.Database] = "tempdb",
        [SqlServerParameters.IntegratedSecurity] = "true",
        [SqlServerParameters.TrustServerCertificate] = "true",
    };

    /// <summary><c>true</c> once the driver can execute queries (milestone M2).</summary>
    public static bool ClastReadsEnabled =>
        Environment.GetEnvironmentVariable("MSSQL_BENCH_CLAST") == "1";
}
