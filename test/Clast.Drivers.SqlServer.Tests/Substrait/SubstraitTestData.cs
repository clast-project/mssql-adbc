// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Xunit;

namespace Clast.Drivers.SqlServer.Tests.Substrait;

/// <summary>
/// The rows the DataFusion fixtures were run over (Fixtures/DataFusion/data.json),
/// as temporary tables (#orders, #customers), and helpers to compare results.
/// </summary>
internal static class SubstraitTestData
{
    public static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "DataFusion");

    /// <summary>Plans name the tables orders and customers; the tests read temporary tables of those names.</summary>
    public static string TempTableName(IReadOnlyList<string> names) => "[#" + names[names.Count - 1].Replace("]", "]]") + "]";

    /// <summary>Creates the test tables as temporary tables of the connection's session.</summary>
    public static void CreateTables(AdbcConnection connection)
    {
        using JsonDocument data = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureDirectory, "data.json")));
        var sql = new StringBuilder();
        foreach (JsonProperty table in data.RootElement.EnumerateObject())
        {
            var columns = table.Value.GetProperty("columns").EnumerateArray()
                .Select(c => (Name: c.GetProperty("name").GetString()!, Type: c.GetProperty("sql").GetString()!)).ToList();
            sql.Append($"CREATE TABLE [#{table.Name}] (")
                .Append(string.Join(", ", columns.Select(c => $"[{c.Name}] {c.Type}")))
                .Append(");\n");
            foreach (JsonElement row in table.Value.GetProperty("rows").EnumerateArray())
            {
                var values = row.EnumerateArray().Select((v, i) => Literal(v, columns[i].Type));
                sql.Append($"INSERT INTO [#{table.Name}] VALUES ({string.Join(", ", values)});\n");
            }
        }
        using AdbcStatement statement = connection.CreateStatement();
        statement.SqlQuery = sql.ToString();
        statement.ExecuteUpdate();
    }

    private static string Literal(JsonElement value, string sqlType)
    {
        string type = sqlType.Split(' ')[0];
        return value.ValueKind switch
        {
            JsonValueKind.Null => "NULL",
            JsonValueKind.True => "1",
            JsonValueKind.False => "0",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String when type.StartsWith("nvarchar", StringComparison.Ordinal) => "N'" + value.GetString()!.Replace("'", "''") + "'",
            JsonValueKind.String => $"CAST('{value.GetString()}' AS {type})",
            _ => throw new NotSupportedException(value.ValueKind.ToString()),
        };
    }

    /// <summary>Runs a query and returns its rows, each value normalized as the fixture generator normalizes DataFusion's.</summary>
    public static List<object?[]> Read(AdbcStatement statement)
    {
        QueryResult result = statement.ExecuteQuery();
        using IArrowArrayStream stream = result.Stream!;
        var rows = new List<object?[]>();
        while (stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is RecordBatch batch)
        {
            using (batch)
            {
                for (int r = 0; r < batch.Length; r++)
                {
                    rows.Add(Enumerable.Range(0, batch.ColumnCount).Select(c => Normalize(batch.Column(c), r)).ToArray());
                }
            }
        }
        return rows;
    }

    public static object? Normalize(IArrowArray array, int index)
    {
        if (array.IsNull(index))
        {
            return null;
        }
        return array switch
        {
            BooleanArray a => a.GetValue(index),
            Int16Array a => (long)a.GetValue(index)!.Value,
            Int32Array a => (long)a.GetValue(index)!.Value,
            Int64Array a => a.GetValue(index),
            UInt8Array a => (long)a.GetValue(index)!.Value,
            FloatArray a => (double)a.GetValue(index)!.Value,
            DoubleArray a => a.GetValue(index),
            Decimal128Array a => NormalizeDecimal(a.GetSqlDecimal(index)!.Value.ToString()),
            StringArray a => a.GetString(index),
            Date32Array a => a.GetDateTime(index)!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimestampArray a => a.GetTimestamp(index)!.Value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff", CultureInfo.InvariantCulture),
            GuidArray a => a.GetGuid(index)!.Value.ToString(),
            _ => QueryTests.ArrowValue(array, index),
        };
    }

    public static object? Normalize(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when value.TryGetInt64(out long l) && !value.GetRawText().Contains(".") && !value.GetRawText().Contains("e") && !value.GetRawText().Contains("E") => l,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.String => value.GetString(),
        _ => throw new NotSupportedException(value.ValueKind.ToString()),
    };

    public static string NormalizeDecimal(string text)
    {
        decimal d = decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture);
        return d.ToString("0.############################", CultureInfo.InvariantCulture);
    }

    /// <summary>Compares rows, as a multiset unless <paramref name="ordered"/>; numbers compare by value.</summary>
    public static string? Difference(List<object?[]> expected, List<object?[]> actual, bool ordered)
    {
        if (!ordered)
        {
            expected = expected.OrderBy(Key, StringComparer.Ordinal).ToList();
            actual = actual.OrderBy(Key, StringComparer.Ordinal).ToList();
        }
        if (expected.Count != actual.Count)
        {
            return $"expected {expected.Count} rows, got {actual.Count}:\n{Show(expected)}\n---\n{Show(actual)}";
        }
        for (int r = 0; r < expected.Count; r++)
        {
            if (expected[r].Length != actual[r].Length)
            {
                return $"row {r}: expected {expected[r].Length} columns, got {actual[r].Length}";
            }
            for (int c = 0; c < expected[r].Length; c++)
            {
                if (!SameValue(expected[r][c], actual[r][c]))
                {
                    return $"row {r} column {c}: expected <{expected[r][c]}>, got <{actual[r][c]}>\n{Show(expected)}\n---\n{Show(actual)}";
                }
            }
        }
        return null;
    }

    private static bool SameValue(object? e, object? a)
    {
        if (e == null || a == null)
        {
            return e == null && a == null;
        }
        if (IsNumber(e) && IsNumber(a))
        {
            double x = Convert.ToDouble(e, CultureInfo.InvariantCulture), y = Convert.ToDouble(a, CultureInfo.InvariantCulture);
            return x == y || Math.Abs(x - y) <= 1e-9 * Math.Max(Math.Abs(x), Math.Abs(y));
        }
        if (e is string es && a is string asx && IsNumeric(es) && IsNumeric(asx))
        {
            return NormalizeDecimal(es) == NormalizeDecimal(asx);
        }
        return Equals(e, a);
    }

    private static bool IsNumber(object o) => o is long or int or short or double or float;

    private static bool IsNumeric(string s) => decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out _);

    private static string Key(object?[] row) => string.Join("|", row.Select(v => v switch
    {
        null => "\u0000",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        long l => ((double)l).ToString("R", CultureInfo.InvariantCulture),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture),
    }));

    private static string Show(List<object?[]> rows) => string.Join("\n", rows.Select(r => string.Join(" | ", r.Select(v => v?.ToString() ?? "NULL"))));

    /// <summary>Asserts that the T-SQL parser accepts a query.</summary>
    public static void AssertParses(string sql)
    {
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        parser.Parse(new StringReader(sql), out IList<ParseError> errors);
        Assert.True(errors.Count == 0, "T-SQL does not parse:\n" + string.Join("\n", errors.Select(e => $"({e.Line},{e.Column}) {e.Message}")) + "\n" + sql);
    }
}
