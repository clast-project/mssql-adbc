// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Clast.Drivers.SqlServer.Substrait;

/// <summary>
/// Translates Substrait plans to T-SQL, and publishes the driver's Substrait
/// dialect: a YAML document in the Substrait dialect format
/// (<c>text/dialect_schema.yaml</c> in the Substrait repository) listing the
/// types, relations, expressions and functions, with their implementations
/// and option behaviors, that the translation supports.
/// </summary>
/// <remarks>
/// A statement given a plan through <c>SubstraitPlan</c> runs its translation.
/// Constructs that can't be translated with the same semantics are rejected
/// with <see cref="SubstraitTranslationException"/> rather than approximated.
/// </remarks>
public static class SqlServerSubstrait
{
    private const string DialectResource = "Clast.Drivers.SqlServer.sqlserver_dialect.yaml";

    /// <summary>
    /// The collation string values get by default
    /// (<see cref="SqlServerParameters.SubstraitCollation"/>): binary, so that
    /// comparisons, sorting and grouping are exact and case-sensitive.
    /// </summary>
    public const string DefaultCollation = TSqlEmitter.BinaryCollation;

    /// <summary>The Substrait release whose protobuf messages and extensions the translation follows.</summary>
    public const string SubstraitVersion = "0.107.0";

    private static readonly Lazy<string> s_dialect = new(() =>
    {
        using Stream stream = typeof(SqlServerSubstrait).Assembly.GetManifestResourceStream(DialectResource)
            ?? throw new InvalidOperationException($"Embedded resource '{DialectResource}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    /// <summary>The driver's Substrait dialect, as YAML.</summary>
    public static string DialectYaml => s_dialect.Value;

    /// <summary>
    /// Translates a Substrait plan (a serialized <c>substrait.Plan</c>) to a
    /// T-SQL query.
    /// </summary>
    /// <param name="plan">The plan.</param>
    /// <param name="collation">
    /// The collation for string values; null or empty to use the database's
    /// collations instead (see <see cref="SqlServerParameters.SubstraitCollation"/>).
    /// </param>
    public static string ToSql(byte[] plan, string? collation = DefaultCollation) => Translate(plan, collation, nested: false);

    /// <param name="nested">
    /// The query will be used as a derived table (partitioned reads), where
    /// T-SQL allows no final ORDER BY.
    /// </param>
    /// <param name="tableName">Writes a named table's name in place of the default (tests read temporary tables with it).</param>
    internal static string Translate(byte[] plan, string? collation, bool nested, Func<IReadOnlyList<string>, string>? tableName = null)
    {
        if (plan == null || plan.Length == 0)
        {
            throw new SubstraitTranslationException("The Substrait plan is empty.");
        }
        var decoder = new SubstraitPlanDecoder(plan);
        RootRel root = decoder.DecodePlan();
        var emitter = new TSqlEmitter(decoder.PerRecordEvaluation, string.IsNullOrEmpty(collation) ? null : CheckCollation(collation!), tableName);
        return emitter.EmitRoot(root, nested, out _);
    }

    /// <summary>Checks that a collation name is a plain identifier, since it becomes part of the query.</summary>
    internal static string CheckCollation(string collation)
    {
        if (collation.Length > 128 || !collation.All(ch => ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
        {
            throw new Apache.Arrow.Adbc.AdbcException($"'{collation}' is not a collation name.", Apache.Arrow.Adbc.AdbcStatusCode.InvalidArgument);
        }
        return collation;
    }
}
