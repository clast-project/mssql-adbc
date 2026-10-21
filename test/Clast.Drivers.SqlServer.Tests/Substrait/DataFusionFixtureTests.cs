// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using Apache.Arrow.Adbc;
using Clast.Drivers.SqlServer.Substrait;
using Xunit;

namespace Clast.Drivers.SqlServer.Tests.Substrait;

/// <summary>
/// Translates Substrait plans produced by a real producer (DataFusion) and
/// compares the T-SQL with reviewed snapshots in Fixtures/DataFusion/*.tsql:
/// either the expected query, which must parse, or "ERROR: &lt;message
/// fragment&gt;" for plans the driver must reject. Given a server, each
/// translation also runs over the fixture data and must return what DataFusion
/// returned for the SQL the plan came from.
///
/// Set MSSQL_UPDATE_SNAPSHOTS=1 to rewrite the snapshots, then review the diff.
/// </summary>
public class DataFusionFixtureTests
{
    private const string ErrorPrefix = "ERROR: ";

    /// <summary>
    /// Fixtures whose results are known to differ from DataFusion's, and why.
    /// The test checks that they still differ, so that a fix is noticed.
    /// </summary>
    private static readonly Dictionary<string, string> s_knownDifferences = new()
    {
        // DataFusion plans NOT IN as a LEFT_ANTI join, which Substrait defines as NOT EXISTS
        // (substrait-io/substrait#325). With a null in the subquery, NOT IN returns no rows.
        ["not_in_anti_join"] = "null-aware anti join",
        // With several grouping sets, Substrait puts the grouping set index after the measures;
        // DataFusion's plans expect it before them, so its projections pick the wrong columns.
        ["rollup"] = "grouping set index position",
        ["grouping_sets"] = "grouping set index position",
        ["cube"] = "grouping set index position",
    };

    public static IEnumerable<object[]> Fixtures()
        => Directory.GetFiles(SubstraitTestData.FixtureDirectory, "*.pb")
            .Select(p => Path.GetFileNameWithoutExtension(p))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n => new object[] { n });

    private static string Translate(string name, Func<IReadOnlyList<string>, string>? tableName = null)
    {
        byte[] plan = File.ReadAllBytes(Path.Combine(SubstraitTestData.FixtureDirectory, name + ".pb"));
        try
        {
            return SqlServerSubstrait.Translate(plan, SqlServerSubstrait.DefaultCollation, nested: false, tableName).Replace("\r\n", "\n");
        }
        catch (SubstraitTranslationException ex)
        {
            return ErrorPrefix + ex.Message;
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void TranslatesAsReviewed(string name)
    {
        string actual = Translate(name);
        if (Environment.GetEnvironmentVariable("MSSQL_UPDATE_SNAPSHOTS") == "1")
        {
            // Write to the source tree, not the test output directory.
            File.WriteAllText(Path.Combine(FindSourceFixtureDirectory(), name + ".tsql"), actual + "\n");
            return;
        }

        string snapshotPath = Path.Combine(SubstraitTestData.FixtureDirectory, name + ".tsql");
        Assert.True(File.Exists(snapshotPath), $"Missing snapshot {name}.tsql. Actual translation:\n{actual}");
        string expected = File.ReadAllText(snapshotPath).Replace("\r\n", "\n").TrimEnd('\n');
        if (expected.StartsWith(ErrorPrefix, StringComparison.Ordinal))
        {
            Assert.True(actual.StartsWith(ErrorPrefix, StringComparison.Ordinal), $"Expected rejection, got:\n{actual}");
            Assert.Contains(expected.Substring(ErrorPrefix.Length), actual);
            return;
        }
        Assert.False(actual.StartsWith(ErrorPrefix, StringComparison.Ordinal), actual);
        Assert.Equal(expected, actual);
        SubstraitTestData.AssertParses(actual);
    }

    [SkippableTheory]
    [MemberData(nameof(Fixtures))]
    public void ReturnsWhatDataFusionReturns(string name)
    {
        string sql = Translate(name, SubstraitTestData.TempTableName);
        Skip.If(sql.StartsWith(ErrorPrefix, StringComparison.Ordinal), "The driver rejects this plan.");

        SqlServerTestConfiguration config = SqlServerTestConfiguration.Load();
        using AdbcConnection connection = new SqlServerDriver().Open(config.Options).Connect(null);
        SubstraitTestData.CreateTables(connection);
        using AdbcStatement statement = connection.CreateStatement();
        statement.SqlQuery = sql;
        List<object?[]> actual = SubstraitTestData.Read(statement);

        using JsonDocument expectedJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(SubstraitTestData.FixtureDirectory, name + ".expected.json")));
        var expected = expectedJson.RootElement.GetProperty("rows").EnumerateArray()
            .Select(row => row.EnumerateArray().Select(SubstraitTestData.Normalize).ToArray())
            .ToList();
        bool ordered = expectedJson.RootElement.GetProperty("ordered").GetBoolean();

        string? difference = SubstraitTestData.Difference(expected, actual, ordered);
        if (s_knownDifferences.TryGetValue(name, out string? reason))
        {
            Assert.True(difference != null, $"{name} was expected to differ from DataFusion ({reason}), but now matches.");
            return;
        }
        Assert.True(difference == null, $"{name}: {difference}\n{sql}");
    }

    private static string FindSourceFixtureDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Clast.Drivers.SqlServer.Tests.csproj")))
        {
            dir = dir.Parent;
        }
        if (dir == null)
        {
            throw new InvalidOperationException("Cannot locate the test project directory.");
        }
        return Path.Combine(dir.FullName, "Fixtures", "DataFusion");
    }
}
