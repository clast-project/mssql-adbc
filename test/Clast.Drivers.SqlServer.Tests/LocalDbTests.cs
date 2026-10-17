// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Xunit;

namespace Clast.Drivers.SqlServer.Tests;

/// <summary>SQL Server Express LocalDB; skipped where LocalDB is not installed.</summary>
public sealed class LocalDbTests
{
    private static bool Installed =>
        OperatingSystem.IsWindows() &&
        Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Microsoft SQL Server Local DB\Installed Versions") is { SubKeyCount: > 0 };

    private static AdbcConnection Connect(string server, Action<Dictionary<string, string>>? configure = null)
    {
        Skip.IfNot(Installed, "LocalDB is not installed");
        var options = new Dictionary<string, string>
        {
            [SqlServerParameters.Server] = server,
            [SqlServerParameters.IntegratedSecurity] = "true",
            [SqlServerParameters.TrustServerCertificate] = "true",
        };
        configure?.Invoke(options);
        return new SqlServerDriver().Open(options).Connect(null);
    }

    private static int Scalar(AdbcConnection connection, string sql)
    {
        using AdbcStatement statement = connection.CreateStatement();
        statement.SqlQuery = sql;
        using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
        using RecordBatch batch = stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult()!;
        return ((Int32Array)batch.Column(0)).GetValue(0)!.Value;
    }

    [SkippableTheory]
    [InlineData(@"(localdb)\MSSQLLocalDB")]
    [InlineData(@"(LocalDB)\mssqllocaldb")]
    public void ConnectsToTheAutomaticInstance(string server)
    {
        using AdbcConnection connection = Connect(server);
        Assert.Equal(1, Scalar(connection, "SELECT CAST(SERVERPROPERTY('IsLocalDB') AS int)"));
        Assert.StartsWith("LOCALDB#", ((SqlServerConnection)connection).Session.Settings.PipeName);
    }

    [SkippableFact]
    public void ParallelReadsShareTheInstance()
    {
        using AdbcConnection connection = Connect(@"(localdb)\MSSQLLocalDB", o => o[SqlServerParameters.ParallelReads] = "3");
        using AdbcStatement statement = connection.CreateStatement();
        statement.SqlQuery = "SELECT CAST(n AS bigint) AS k FROM (SELECT TOP (3000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects a CROSS JOIN sys.all_objects b) AS t";
        statement.SetOption(SqlServerParameters.PartitionColumn, "k");
        using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
        Assert.IsType<SqlServerParallelQueryStream>(stream);
        long rows = 0;
        while (stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is RecordBatch batch)
        {
            rows += batch.Length;
            batch.Dispose();
        }
        Assert.Equal(3000, rows);
    }

    [SkippableFact]
    public void StartsAStoppedInstance()
    {
        Skip.IfNot(Installed, "LocalDB is not installed");
        const string Instance = "ClastTest";
        SqlLocalDb($"create {Instance}");
        try
        {
            SqlLocalDb($"stop {Instance}");
            using AdbcConnection connection = Connect($@"(localdb)\{Instance}", o => o[SqlServerParameters.ConnectTimeout] = "60");
            Assert.Equal(1, Scalar(connection, "SELECT CAST(SERVERPROPERTY('IsLocalDB') AS int)"));
        }
        finally
        {
            SqlLocalDb($"stop {Instance} -k");
            SqlLocalDb($"delete {Instance}");
        }
    }

    [SkippableFact]
    public void ExplicitMandatoryEncryptionIsRefused()
    {
        // LocalDB does not support TLS; without an explicit setting the driver uses login-only encryption.
        var ex = Assert.Throws<AdbcException>(() => Connect(@"(localdb)\MSSQLLocalDB", o => o[SqlServerParameters.Encrypt] = "mandatory"));
        Assert.Contains("encrypt=optional", ex.Message);
    }

    [SkippableFact]
    public void UnknownInstanceReportsLocalDbsMessage()
    {
        var ex = Assert.Throws<AdbcException>(() => Connect(@"(localdb)\NoSuchInstance" + Guid.NewGuid().ToString("N")[..8]));
        Assert.Equal(AdbcStatusCode.IOError, ex.Status);
        Assert.Contains("Could not start LocalDB instance", ex.Message);
        Assert.DoesNotContain("HRESULT", ex.Message); // LocalDB's own text, not just a code
    }

    [Theory]
    [InlineData(@"(localdb)\")]
    [InlineData(@"(localdb)\MSSQLLocalDB,1433")]
    [InlineData(@"tcp:(localdb)\MSSQLLocalDB")]
    public void MalformedLocalDbIsInvalid(string server)
    {
        var ex = Assert.Throws<AdbcException>(() => SqlServerConnectionSettings.Parse(new Dictionary<string, string> { [SqlServerParameters.Server] = server }));
        Assert.Equal(AdbcStatusCode.InvalidArgument, ex.Status);
    }

    private static void SqlLocalDb(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("SqlLocalDB.exe", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
    }
}
