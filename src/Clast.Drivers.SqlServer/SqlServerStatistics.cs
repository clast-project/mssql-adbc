// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text;
using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Clast.Drivers.SqlServer.Arrow;
using Clast.Drivers.SqlServer.Tds;
using static Clast.Drivers.SqlServer.Arrow.ResultArrays;
using static Clast.Drivers.SqlServer.SqlServerMetadata;

namespace Clast.Drivers.SqlServer;

/// <summary>
/// GetStatistics and GetStatisticsNames. Approximate statistics are the
/// server's own: table row counts from <c>sys.partitions</c>, and column
/// distinct and null counts from the histogram of each column's statistics
/// object. Exact statistics are row counts with <c>COUNT_BIG(*)</c>.
/// </summary>
internal static class SqlServerStatistics
{
    private static readonly UnionType s_valueType = new(
        [
            new Field("int64", Int64Type.Default, true),
            new Field("uint64", UInt64Type.Default, true),
            new Field("float64", DoubleType.Default, true),
            new Field("binary", BinaryType.Default, true),
        ],
        [0, 1, 2, 3],
        UnionMode.Dense);

    private static readonly StructType s_statisticType = new(
    [
        new Field("table_name", StringType.Default, false),
        new Field("column_name", StringType.Default, true),
        new Field("statistic_key", Int16Type.Default, false),
        new Field("statistic_value", s_valueType, false),
        new Field("statistic_is_approximate", BooleanType.Default, false),
    ]);

    private static readonly ListType s_statisticsListType = new(new Field("item", s_statisticType, true));

    private static readonly StructType s_dbSchemaType = new(
    [
        new Field("db_schema_name", StringType.Default, true),
        new Field("db_schema_statistics", s_statisticsListType, false),
    ]);

    private static readonly ListType s_dbSchemasListType = new(new Field("item", s_dbSchemaType, true));

    public static readonly Schema StatisticsSchema = new(
    [
        new Field("catalog_name", StringType.Default, true),
        new Field("catalog_db_schemas", s_dbSchemasListType, false),
    ], null);

    public static readonly Schema StatisticsNamesSchema = new(
    [
        new Field("statistic_name", StringType.Default, false),
        new Field("statistic_key", Int16Type.Default, false),
    ], null);

    /// <summary>The driver defines no statistics of its own.</summary>
    public static IArrowArrayStream GetStatisticsNames() =>
        new InMemoryStream(StatisticsNamesSchema, new RecordBatch(StatisticsNamesSchema, [Strings([]), Int16s([])], 0));

    private sealed record Statistic(string Table, string? Column, short Key, long? Exact, double? Approximate);

    public static IArrowArrayStream GetStatistics(TdsSession session, string? catalogPattern, string? schemaPattern, string? tablePattern, bool approximate)
    {
        var catalogs = new List<(string Name, SortedDictionary<string, List<Statistic>> Schemas)>();
        if (catalogPattern != "")
        {
            List<object?[]> databases = SqlServerMetadata.Query(session,
                $"SELECT name FROM sys.databases WHERE HAS_DBACCESS(name) = 1{Like("name", catalogPattern)} ORDER BY name")[0];
            foreach (object?[] row in databases)
            {
                string catalog = (string)row[0]!;
                try
                {
                    catalogs.Add((catalog, approximate
                        ? ReadApproximate(session, catalog, schemaPattern, tablePattern)
                        : ReadExact(session, catalog, schemaPattern, tablePattern)));
                }
                catch (SqlServerException ex) when (ex.Number is 916 or 942 or 943 or 952 or 4060)
                {
                    // The database cannot be read now.
                }
            }
        }
        return Build(catalogs);
    }

    private static string TableFilter(string? schemaPattern, string? tablePattern) =>
        $"o.type = 'U'{Like("s.name", schemaPattern)}{Like("o.name", tablePattern)}";

    /// <summary>Runs a query in the context of <paramref name="catalog"/>, as the statistics functions require.</summary>
    private static List<object?[]> QueryIn(TdsSession session, string catalog, string sql) =>
        SqlServerMetadata.Query(session, $"EXEC {IngestTarget.Quote(catalog)}.sys.sp_executesql {SqlServerMetadata.Literal(sql)}")[0];

    private static SortedDictionary<string, List<Statistic>> ReadApproximate(TdsSession session, string catalog, string? schemaPattern, string? tablePattern)
    {
        string filter = TableFilter(schemaPattern, tablePattern);
        string sql = $"""
            WITH h AS (
                SELECT s.object_id, sc.column_id, s.stats_id,
                       CAST(COUNT(*) - MAX(CASE WHEN x.range_high_key IS NULL THEN 1 ELSE 0 END) + SUM(x.distinct_range_rows) AS float) AS distinct_count,
                       CAST(SUM(CASE WHEN x.range_high_key IS NULL THEN x.equal_rows ELSE 0 END) AS float) AS null_count
                FROM sys.stats s
                JOIN sys.stats_columns sc ON sc.object_id = s.object_id AND sc.stats_id = s.stats_id AND sc.stats_column_id = 1
                CROSS APPLY sys.dm_db_stats_histogram(s.object_id, s.stats_id) x
                GROUP BY s.object_id, sc.column_id, s.stats_id),
            best AS (
                SELECT h.*, ROW_NUMBER() OVER (PARTITION BY h.object_id, h.column_id ORDER BY p.rows_sampled DESC, p.last_updated DESC) AS rn
                FROM h CROSS APPLY sys.dm_db_stats_properties(h.object_id, h.stats_id) p)
            SELECT s.name, o.name, CAST(NULL AS sysname), CAST({AdbcStatistic.RowCountKey} AS smallint), CAST(SUM(p.rows) AS float)
            FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            JOIN sys.partitions p ON p.object_id = o.object_id AND p.index_id IN (0, 1)
            WHERE {filter}
            GROUP BY s.name, o.name
            UNION ALL
            SELECT s.name, o.name, c.name, k.statistic_key, k.value
            FROM best b
            JOIN sys.objects o ON o.object_id = b.object_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            JOIN sys.columns c ON c.object_id = b.object_id AND c.column_id = b.column_id
            CROSS APPLY (VALUES (CAST({AdbcStatistic.DistinctCountKey} AS smallint), b.distinct_count),
                                (CAST({AdbcStatistic.NullCountKey} AS smallint), b.null_count)) k(statistic_key, value)
            WHERE b.rn = 1 AND {filter}
            ORDER BY 1, 2, 3, 4
            """;

        var schemas = new SortedDictionary<string, List<Statistic>>(StringComparer.Ordinal);
        foreach (object?[] row in QueryIn(session, catalog, sql))
        {
            Add(schemas, (string)row[0]!, new Statistic((string)row[1]!, (string?)row[2], (short)row[3]!, null, (double)row[4]!));
        }
        return schemas;
    }

    private static SortedDictionary<string, List<Statistic>> ReadExact(TdsSession session, string catalog, string? schemaPattern, string? tablePattern)
    {
        var schemas = new SortedDictionary<string, List<Statistic>>(StringComparer.Ordinal);
        List<object?[]> tables = QueryIn(session, catalog,
            $"SELECT s.name, o.name FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id WHERE {TableFilter(schemaPattern, tablePattern)} ORDER BY s.name, o.name");

        const int TablesPerQuery = 100;
        for (int start = 0; start < tables.Count; start += TablesPerQuery)
        {
            var sql = new StringBuilder();
            foreach (object?[] table in tables.Skip(start).Take(TablesPerQuery))
            {
                string schema = (string)table[0]!, name = (string)table[1]!;
                if (sql.Length > 0)
                {
                    sql.Append("\nUNION ALL ");
                }
                sql.Append($"SELECT {SqlServerMetadata.Literal(schema)}, {SqlServerMetadata.Literal(name)}, COUNT_BIG(*) FROM {IngestTarget.Quote(catalog)}.{IngestTarget.Quote(schema)}.{IngestTarget.Quote(name)}");
            }
            foreach (object?[] row in SqlServerMetadata.Query(session, sql.ToString())[0])
            {
                Add(schemas, (string)row[0]!, new Statistic((string)row[1]!, null, AdbcStatistic.RowCountKey, (long)row[2]!, null));
            }
        }
        return schemas;
    }

    private static void Add(SortedDictionary<string, List<Statistic>> schemas, string schema, Statistic statistic)
    {
        if (!schemas.TryGetValue(schema, out List<Statistic>? list))
        {
            schemas[schema] = list = new List<Statistic>();
        }
        list.Add(statistic);
    }

    private static IArrowArrayStream Build(List<(string Name, SortedDictionary<string, List<Statistic>> Schemas)> catalogs)
    {
        IArrowArray BuildStatistics(List<Statistic> statistics)
        {
            var typeIds = new ArrowBuffer.Builder<byte>();
            var offsets = new ArrowBuffer.Builder<int>();
            var exact = new Int64Array.Builder();
            var approximate = new DoubleArray.Builder();
            foreach (Statistic statistic in statistics)
            {
                if (statistic.Exact is long value)
                {
                    typeIds.Append(0);
                    offsets.Append(exact.Length);
                    exact.Append(value);
                }
                else
                {
                    typeIds.Append(2);
                    offsets.Append(approximate.Length);
                    approximate.Append(statistic.Approximate!.Value);
                }
            }
            var values = new DenseUnionArray(s_valueType, statistics.Count,
                [exact.Build(), new UInt64Array.Builder().Build(), approximate.Build(), new BinaryArray.Builder().Build()],
                typeIds.Build(), offsets.Build());

            return Struct(s_statisticType, statistics.Count,
                Strings(statistics.Select(s => s.Table)),
                Strings(statistics.Select(s => s.Column)),
                Int16s(statistics.Select(s => (short?)s.Key)),
                values,
                Booleans(statistics.Select(s => (bool?)(s.Exact == null))));
        }

        IArrowArray BuildSchemas(List<KeyValuePair<string, List<Statistic>>> schemas) => Struct(s_dbSchemaType, schemas.Count,
            Strings(schemas.Select(s => s.Key)),
            List(s_statisticsListType, schemas.Select(s => (IReadOnlyList<Statistic>?)s.Value).ToList(), BuildStatistics));

        IArrowArray[] arrays =
        [
            Strings(catalogs.Select(c => c.Name)),
            List(s_dbSchemasListType, catalogs.Select(c => (IReadOnlyList<KeyValuePair<string, List<Statistic>>>?)c.Schemas.ToList()).ToList(), BuildSchemas),
        ];
        return new InMemoryStream(StatisticsSchema, new RecordBatch(StatisticsSchema, arrays, catalogs.Count));
    }
}
