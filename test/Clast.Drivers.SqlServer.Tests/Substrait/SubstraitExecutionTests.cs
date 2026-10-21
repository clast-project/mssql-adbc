// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Clast.Drivers.SqlServer.Substrait;
using Xunit;

namespace Clast.Drivers.SqlServer.Tests.Substrait;

/// <summary>
/// Runs hand-built Substrait plans through a statement's SubstraitPlan
/// against the fixture data (#orders, #customers; see data.json) and checks
/// the results against values worked out by hand from Substrait's definitions.
/// </summary>
public class SubstraitExecutionTests
{
    // #orders: id, customer_id, name, price, qty, ts, flag, amount, d
    private static readonly (string, byte[])[] s_orders =
    [
        ("id", P.TI64), ("customer_id", P.TI64), ("name", P.TString), ("price", P.TFp64), ("qty", P.TI32),
        ("ts", P.TTimestamp(6)), ("flag", P.TBool), ("amount", P.TDecimal(10, 2)), ("d", P.TDate),
    ];

    // #customers: id, cname, region
    private static readonly (string, byte[])[] s_customers = [("id", P.TI64), ("cname", P.TString), ("region", P.TString)];

    private static byte[] Orders => P.Read("#orders", s_orders);
    private static byte[] Customers => P.Read("#customers", s_customers);

    private static List<object?[]> Run(byte[] plan, Action<AdbcStatement>? configure = null)
    {
        SqlServerTestConfiguration config = SqlServerTestConfiguration.Load();
        using AdbcConnection connection = new SqlServerDriver().Open(config.Options).Connect(null);
        SubstraitTestData.CreateTables(connection);
        using AdbcStatement statement = connection.CreateStatement();
        configure?.Invoke(statement);
        statement.SubstraitPlan = plan;
        return SubstraitTestData.Read(statement);
    }

    private static void AssertRows(List<object?[]> actual, bool ordered, params object?[][] expected)
    {
        string? difference = SubstraitTestData.Difference(expected.ToList(), actual, ordered);
        Assert.True(difference == null, difference);
    }

    private static object?[] Row(params object?[] values) => values;

    /// <summary>Projects only the given expressions (the plan's output is just them).</summary>
    private static byte[] Select(byte[] input, int inputWidth, params byte[][] expressions)
        => P.ProjectEmit(input, Enumerable.Range(inputWidth, expressions.Length).ToArray(), expressions);

    #region Strings and collation

    [SkippableFact]
    public void StringsCompareExactlyByDefault()
    {
        var f = new Functions();
        int eq = f.Add("functions_comparison", "equal:any_any");
        byte[] filter = P.Filter(Orders, P.Call(eq, P.Field(2), P.Str("apple")));
        byte[] plan = P.Plan(Select(filter, 9, P.Field(0)), ["id"], f);
        AssertRows(Run(plan), false, Row(1L));
    }

    [SkippableFact]
    public void DatabaseCollationIgnoresCase()
    {
        var f = new Functions();
        int eq = f.Add("functions_comparison", "equal:any_any");
        byte[] filter = P.Filter(Orders, P.Call(eq, P.Field(2), P.Str("apple")));
        byte[] plan = P.Plan(Select(filter, 9, P.Field(0)), ["id"], f);
        // The server's default collation (SQL_Latin1_General_CP1_CI_AS unless set otherwise) ignores case.
        AssertRows(Run(plan, s => s.SetOption(SqlServerParameters.SubstraitCollation, "")), false, Row(1L), Row(9L));
    }

    [SkippableFact]
    public void StringsSortByCodePoint()
    {
        byte[] sort = P.Sort(Orders, P.SortField(P.Field(2), P.AscNullsLast));
        byte[] plan = P.Plan(Select(sort, 9, P.Field(0)), ["id"]);
        AssertRows(Run(plan), true, Row(9L), Row(3L), Row(7L), Row(1L), Row(5L), Row(2L), Row(4L), Row(6L), Row(8L));
    }

    [SkippableFact]
    public void LikeTreatsOnlyPercentAndUnderscoreAsWildcards()
    {
        var f = new Functions();
        int like = f.Add("functions_string", "like:str_str");
        int starts = f.Add("functions_string", "starts_with:str_str");
        int contains = f.Add("functions_string", "contains:str_str");
        byte[] values = P.Values([("s", P.TString)], [P.Str("[ab]c")], [P.Str("ac")], [P.Str("a%b")], [P.Str("axb")], [P.Str("a.b")]);
        byte[] plan = P.Plan(Select(values, 1,
            P.Field(0),
            P.Call(like, P.Field(0), P.Str("[ab]%")),
            P.Call(starts, P.Field(0), P.Str("a%")),
            P.Call(contains, P.Field(0), P.Str("."))), ["s", "like", "starts", "contains"], f);
        AssertRows(Run(plan), false,
            Row("[ab]c", true, false, false),
            Row("ac", false, false, false),
            Row("a%b", false, true, false),
            Row("axb", false, false, false),
            Row("a.b", false, false, true));
    }

    [SkippableFact]
    public void StringFunctions()
    {
        var f = new Functions();
        int length = f.Add("functions_string", "char_length:str");
        int octets = f.Add("functions_string", "octet_length:str");
        int strpos = f.Add("functions_string", "strpos:str_str");
        int substring = f.Add("functions_string", "substring:str_i32_i32");
        int concat = f.Add("functions_string", "concat:str");
        int replace = f.Add("functions_string", "replace:str_str_str");
        int trim = f.Add("functions_string", "trim:str_str");
        int regexp = f.Add("functions_string", "regexp_replace:str_str_str");
        byte[] values = P.Values([("s", P.TString)], [P.Str("ab é ")]);
        byte[] plan = P.Plan(Select(values, 1,
            P.Call(length, P.Field(0)),
            P.Call(octets, P.Field(0)),
            P.Call(strpos, P.Field(0), P.Str("")),
            P.Call(substring, P.Field(0), P.I32(-1), P.I32(4)),
            P.CallWith(concat, [("null_handling", ["ACCEPT_NULLS"])], P.Field(0), P.Null(P.TString)),
            P.CallWith(concat, [("null_handling", ["IGNORE_NULLS"])], P.Field(0), P.Null(P.TString), P.Str("!")),
            P.CallWith(replace, [("case_sensitivity", ["CASE_INSENSITIVE"])], P.Field(0), P.Str("AB"), P.Str("x")),
            P.Call(trim, P.Field(0), P.Str(" a")),
            P.Call(regexp, P.Field(0), P.Str("(a)(b)"), P.Str("$2$1"))),
            ["length", "octets", "strpos", "substring", "accept", "ignore", "replace", "trim", "regexp"], f);
        // Trailing spaces count; é is two bytes in UTF-8; an empty string is found at 1;
        // a start of -1 begins two characters before the string.
        AssertRows(Run(plan), false, Row(5L, 6L, 1L, "ab", null, "ab é !", "x é ", "b é", "ba é "));
    }

    #endregion

    #region Aggregates and set operations

    [SkippableFact]
    public void GroupingSetsAppendTheSetIndex()
    {
        var f = new Functions();
        int count = f.Add("functions_aggregate_generic", "count:");
        // Grouping sets (flag) and (): output is flag, count, then the set's index.
        byte[] plan = P.Plan(P.AggregateSets(Orders, [P.Field(6)], [[0], []], P.Measure(count, [])), ["flag", "n", "set"], f);
        AssertRows(Run(plan), false,
            Row(true, 4L, 0L), Row(false, 3L, 0L), Row(null, 2L, 0L),
            Row(null, 9L, 1L));
    }

    [SkippableFact]
    public void AggregatesFollowSubstraitResultRules()
    {
        var f = new Functions();
        int avg = f.Add("functions_arithmetic", "avg:i32");
        int sum0 = f.Add("functions_arithmetic", "sum0:i32");
        int product = f.Add("functions_arithmetic", "product:i32");
        int boolAnd = f.Add("functions_boolean", "bool_and:bool");
        int stringAgg = f.Add("functions_string", "string_agg:str_str");
        int gt = f.Add("functions_comparison", "gt:any_any");
        byte[] none = P.Call(gt, P.Field(4), P.I32(100));
        byte[] plan = P.Plan(P.Aggregate(Orders, [],
            P.Measure(avg, [P.Field(4)]),
            P.Measure(sum0, [P.Field(4)], filter: none),
            P.Measure(product, [P.Field(4)], filter: none),
            P.Measure(boolAnd, [P.Field(6)]),
            P.MeasureSorted(stringAgg, [P.Field(2), P.Str(",")], P.SortField(P.Field(0), P.DescNullsLast))),
            ["avg", "sum0", "product", "all", "names"], f);
        // avg truncates (26 / 8); sum0 of nothing is 0 and product of nothing is 1.
        AssertRows(Run(plan), false, Row(3L, 0L, 1L, false, "Apple,a.b,date,apricot,cherry,Avocado,banana,apple"));
    }

    [SkippableFact]
    public void AllSetOperationsCountDuplicates()
    {
        byte[] orderCustomers = Select(Orders, 9, P.Field(1));
        byte[] customerIds = Select(Customers, 3, P.Field(0));
        // orders: 10, 10, 20, NULL, 30, NULL, 40, 20, 30; customers: 10, 20, NULL, 50, 30.
        AssertRows(Run(P.Plan(P.Set(P.MinusPrimaryAll, orderCustomers, customerIds), ["c"])), false,
            Row(10L), Row(20L), Row(30L), Row((object?)null), Row(40L));
        AssertRows(Run(P.Plan(P.Set(P.IntersectionMultisetAll, orderCustomers, customerIds), ["c"])), false,
            Row(10L), Row(20L), Row(30L), Row((object?)null));
        AssertRows(Run(P.Plan(P.Set(P.MinusPrimary, orderCustomers, customerIds), ["c"])), false, Row(40L));
        AssertRows(Run(P.Plan(P.Set(P.IntersectionPrimary, orderCustomers, Select(Customers, 3, P.I64(40)), customerIds), ["c"])), false,
            Row(10L), Row(20L), Row(30L), Row((object?)null), Row(40L));
    }

    #endregion

    #region Joins and subqueries

    [SkippableFact]
    public void MarkJoinIsThreeValued()
    {
        var f = new Functions();
        int eq = f.Add("functions_comparison", "equal:any_any");
        byte[] join = P.Join(Orders, Customers, P.Call(eq, P.Field(1), P.Field(9)), P.LeftMark);
        byte[] plan = P.Plan(Select(join, 10, P.Field(0), P.Field(9)), ["id", "mark"], f);
        // customers has a NULL id, so an order without a match compares NULL with it: unknown.
        AssertRows(Run(plan), false,
            Row(1L, true), Row(2L, true), Row(3L, true), Row(4L, null), Row(5L, true),
            Row(6L, null), Row(7L, null), Row(8L, true), Row(9L, true));
    }

    [SkippableFact]
    public void OuterJoinComputedColumnsAreNullWithoutAMatch()
    {
        var f = new Functions();
        int eq = f.Add("functions_comparison", "equal:any_any");
        byte[] right = Select(Customers, 3, P.Field(0), P.Str("matched"));
        byte[] join = P.Join(Orders, right, P.Call(eq, P.Field(1), P.Field(9)), P.Left);
        byte[] plan = P.Plan(Select(join, 11, P.Field(0), P.Field(10)), ["id", "m"], f);
        AssertRows(Run(plan), false,
            Row(1L, "matched"), Row(2L, "matched"), Row(3L, "matched"), Row(4L, null), Row(5L, "matched"),
            Row(6L, null), Row(7L, null), Row(8L, "matched"), Row(9L, "matched"));
    }

    [SkippableFact]
    public void RightSemiAndAntiJoinsKeepTheRightInput()
    {
        var f = new Functions();
        int eq = f.Add("functions_comparison", "equal:any_any");
        byte[] condition = P.Call(eq, P.Field(1), P.Field(9));
        AssertRows(Run(P.Plan(Select(P.Join(Orders, Customers, condition, P.RightSemi), 3, P.Field(1)), ["cname"], f)), false,
            Row("x"), Row("y"), Row("X"));
        AssertRows(Run(P.Plan(Select(P.Join(Orders, Customers, condition, P.RightAnti), 3, P.Field(1)), ["cname"], f)), false,
            Row("z"), Row("w"));
    }

    [SkippableFact]
    public void CorrelatedSubqueries()
    {
        var f = new Functions();
        int eq = f.Add("functions_comparison", "equal:any_any");
        int gt = f.Add("functions_comparison", "gt:any_any");
        // (SELECT cname FROM customers WHERE id = orders.customer_id)
        byte[] cname = P.ScalarSubquery(Select(P.Filter(Customers, P.Call(eq, P.Field(0), P.OuterField(1))), 3, P.Field(1)));
        // customer_id IN (SELECT id FROM customers)
        byte[] isCustomer = P.InSubquery(P.Field(1), Select(Customers, 3, P.Field(0)));
        // price > ALL (SELECT price FROM orders WHERE qty > 5)
        byte[] dearest = P.SetComparison(P.Field(3), P.Gt, P.All, Select(P.Filter(Orders, P.Call(gt, P.Field(4), P.I32(5))), 9, P.Field(3)));
        byte[] plan = P.Plan(Select(Orders, 9, P.Field(0), cname, isCustomer, dearest), ["id", "cname", "in", "dearest"], f);
        AssertRows(Run(plan), false,
            Row(1L, "x", true, false), Row(2L, "x", true, null), Row(3L, "y", true, false), Row(4L, null, null, false),
            Row(5L, "X", true, false), Row(6L, null, null, false), Row(7L, null, null, false), Row(8L, "y", true, false),
            Row(9L, "X", true, false));
    }

    [SkippableFact]
    public void AggregatesOfOuterColumnsInSubqueries()
    {
        var f = new Functions();
        int sum = f.Add("functions_arithmetic", "sum:i64");
        int count = f.Add("functions_aggregate_generic", "count:");
        // (SELECT sum(orders.id) FROM customers): T-SQL would take SUM(outer column) as the outer query's aggregate.
        byte[] total = P.ScalarSubquery(P.Aggregate(Customers, [], P.Measure(sum, [P.OuterField(0)])));
        // (SELECT count(*) FROM customers GROUP BY orders.id): grouping by an outer column.
        byte[] groups = P.ScalarSubquery(Select(P.Aggregate(Customers, [P.OuterField(0)], P.Measure(count, [])), 2, P.Field(1)));
        byte[] plan = P.Plan(Select(P.Filter(Orders, P.Call(f.Add("functions_comparison", "lt:any_any"), P.Field(0), P.I64(3))), 9,
            P.Field(0), total, groups), ["id", "total", "n"], f);
        // Five customers each time.
        AssertRows(Run(plan), false, Row(1L, 5L, 5L), Row(2L, 10L, 5L));
    }

    [SkippableFact]
    public void MeasureOverASubqueryColumn()
    {
        var f = new Functions();
        int sum = f.Add("functions_arithmetic", "sum:i64");
        int eq = f.Add("functions_comparison", "equal:any_any");
        // count of customers with the order's customer id, summed over orders: 6 orders match one customer each.
        byte[] matches = P.ScalarSubquery(P.Aggregate(P.Filter(Customers, P.Call(eq, P.Field(0), P.OuterField(1))), [],
            P.Measure(f.Add("functions_aggregate_generic", "count:"), [])));
        byte[] plan = P.Plan(P.Aggregate(Select(Orders, 9, matches), [], P.Measure(sum, [P.Field(0)])), ["n"], f);
        AssertRows(Run(plan), false, Row(6L));
    }

    [SkippableFact]
    public void RepeatedGroupingKeysAreNullOutsideTheirSets()
    {
        var f = new Functions();
        int count = f.Add("functions_aggregate_generic", "count:");
        // Keys flag, flag (the same expression twice) and customer_id; sets (keys 0 and 2) and (key 1).
        byte[] plan = P.Plan(P.AggregateSets(Orders, [P.Field(6), P.Field(6), P.Field(1)], [[0, 2], [1]], P.Measure(count, [])),
            ["a", "b", "c", "n", "set"], f);
        var rows = Run(plan);
        // Key 1 is null in the first set's rows, key 0 in the second's; both sets cover all 9 rows.
        Assert.All(rows.Where(r => Equals(r[4], 0L)), r => Assert.Null(r[1]));
        Assert.All(rows.Where(r => Equals(r[4], 1L)), r => Assert.Null(r[0]));
        Assert.Equal(new object?[] { true, false, null }, rows.Where(r => Equals(r[4], 1L)).Select(r => r[1]).OrderBy(v => v == null ? 2 : (bool)v ? 0 : 1));
        Assert.Equal(9L, rows.Where(r => Equals(r[4], 0L)).Sum(r => (long)r[3]!));
    }

    [SkippableFact]
    public void EmitAfterDistinctKeepsDuplicates()
    {
        // Distinct (customer_id, flag) rows, then only customer_id: duplicates of customer_id remain.
        byte[] distinct = Select(Orders, 9, P.Field(1), P.Field(6));
        byte[] set = P.LenDel(8, P.Cat(P.Emit(0), P.LenDel(2, distinct), P.VarField(3, P.UnionDistinct)));
        var rows = Run(P.Plan(set, ["c"]));
        Assert.Equal(8, rows.Count);
    }

    #endregion

    #region Rows and order

    [SkippableFact]
    public void NullsLastForACaseThatCanBeNull()
    {
        var f = new Functions();
        int gt = f.Add("functions_comparison", "gt:any_any");
        // if qty > 5 then qty (else null), ascending with nulls last.
        byte[] key = P.IfThen([(P.Call(gt, P.Field(4), P.I32(5)), P.Field(4))], null);
        byte[] plan = P.Plan(Select(P.Sort(Orders, P.SortField(key, P.AscNullsLast), P.SortField(P.Field(0), P.AscNullsFirst)), 9, P.Field(0)), ["id"], f);
        var rows = Run(plan);
        Assert.Equal(new object?[] { 1L, 7L, 4L }, rows.Take(3).Select(r => r[0]));
    }

    [SkippableFact]
    public void FetchOfNoRowsAfterAnOffset()
    {
        byte[] sorted = P.Sort(Orders, P.SortField(P.Field(0), P.AscNullsFirst));
        Assert.Empty(Run(P.Plan(Select(P.Fetch(sorted, 1, 0), 9, P.Field(0)), ["id"])));
    }

    [SkippableFact]
    public void TrimCharactersFromAColumn()
    {
        var f = new Functions();
        int trim = f.Add("functions_string", "trim:str_str");
        byte[] values = P.Values([("s", P.TString), ("c", P.TString)], [P.Str("xxaxx"), P.Str("x")]);
        AssertRows(Run(P.Plan(Select(values, 2, P.Call(trim, P.Field(0), P.Field(1))), ["t"], f)), false, Row("a"));
    }

    [SkippableFact]
    public void EmptyTableWithoutColumns()
    {
        byte[] none = P.Values([]);
        AssertRows(Run(P.Plan(Select(P.Cross(Customers, none), 3, P.Field(0)), ["id"])), false);
    }


    [SkippableFact]
    public void TopNWithTies()
    {
        // Prices: 100, 7.5, 4, then 3 three times.
        byte[] top = P.TopN(Orders, 0, 4, P.WithTies, P.SortField(P.Field(3), P.DescNullsLast));
        AssertRows(Run(P.Plan(Select(top, 9, P.Field(0)), ["id"])), false, Row(7L), Row(3L), Row(8L), Row(1L), Row(5L), Row(9L));
    }

    [SkippableFact]
    public void FetchAfterFetchKeepsTheOrder()
    {
        byte[] sorted = P.Sort(Orders, P.SortField(P.Field(0), P.DescNullsLast));
        byte[] plan = P.Plan(Select(P.Fetch(P.Fetch(sorted, 0, 6), 2, 3), 9, P.Field(0)), ["id"]);
        AssertRows(Run(plan), true, Row(7L), Row(6L), Row(5L));
    }

    [SkippableFact]
    public void VirtualTables()
    {
        byte[] values = P.Values([("a", P.TI32), ("b", P.TString)], [P.I32(1), P.Str("x")], [P.Null(P.TI32), P.Null(P.TString)]);
        AssertRows(Run(P.Plan(values, ["a", "b"])), false, Row(1L, "x"), Row(null, null));
    }

    #endregion

    #region Arithmetic, casts and dates

    [SkippableFact]
    public void DivisionByZeroErrorsOrIsNullAsRequested()
    {
        var f = new Functions();
        int divide = f.Add("functions_arithmetic", "divide:i32_i32");
        int modulus = f.Add("functions_arithmetic", "modulus:i32_i32");
        byte[] values = P.Values([("a", P.TI32), ("b", P.TI32)], [P.I32(-3), P.I32(0)], [P.I32(-3), P.I32(4)]);
        byte[] plan = P.Plan(Select(values, 2,
            P.CallWith(divide, [("on_division_by_zero", ["NULL"])], P.Field(0), P.Field(1)),
            P.CallWith(modulus, [("division_type", ["FLOOR"]), ("on_domain_error", ["NULL"])], P.Field(0), P.Field(1)),
            P.CallWith(modulus, [("division_type", ["TRUNCATE"]), ("on_domain_error", ["NULL"])], P.Field(0), P.Field(1))),
            ["q", "floor", "truncate"], f);
        AssertRows(Run(plan), false, Row(null, null, null), Row(0L, 1L, -3L));

        byte[] failing = P.Plan(Select(values, 2, P.CallWith(divide, [("on_division_by_zero", ["ERROR"])], P.Field(0), P.Field(1))), ["q"], f);
        Assert.ThrowsAny<AdbcException>(() => Run(failing));
    }

    [SkippableFact]
    public void Casts()
    {
        byte[] values = P.Values([("s", P.TString), ("u", P.TUuid), ("b", P.TBool)],
            [P.Str("12"), P.Uuid(new Guid("0F1E2D3C-4B5A-6978-8796-A5B4C3D2E1F0")), P.Bool(true)],
            [P.Str("x"), P.Null(P.TUuid), P.Null(P.TBool)]);
        byte[] plan = P.Plan(Select(values, 3,
            P.Cast(P.TI32, P.Field(0), P.FailureReturnNull),
            P.Cast(P.TString, P.Field(1)),
            P.Cast(P.TString, P.Field(2))), ["i", "u", "b"]);
        AssertRows(Run(plan), false, Row(12L, "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0", "true"), Row(null, null, null));

        byte[] failing = P.Plan(Select(values, 3, P.Cast(P.TI32, P.Field(0), P.FailureThrow)), ["i"]);
        Assert.ThrowsAny<AdbcException>(() => Run(failing));
    }

    [SkippableFact]
    public void ExtractDateParts()
    {
        var f = new Functions();
        int extract = f.Add("functions_datetime", "extract:req_req_date");
        int extractYear = f.Add("functions_datetime", "extract:req_date");
        byte[] values = P.Values([("d", P.TDate)],
            [P.Date(Days(2021, 1, 3))],    // Sunday: ISO 2020-W53, US (MMWR) 2021 week 1
            [P.Date(Days(2022, 1, 1))],    // Saturday: ISO 2021-W52, US 2021 week 52
            [P.Date(Days(2024, 12, 30))],  // Monday: ISO 2025-W01, US 2025 week 1
            [P.Date(Days(2020, 12, 31))]); // Thursday: ISO 2020-W53, US 2020 week 53
        byte[] plan = P.Plan(Select(values, 1,
            P.Call(extractYear, P.Enum("ISO_YEAR"), P.Field(0)),
            P.Call(extract, P.Enum("ISO_WEEK"), P.Enum("ONE"), P.Field(0)),
            P.Call(extractYear, P.Enum("US_YEAR"), P.Field(0)),
            P.Call(extract, P.Enum("US_WEEK"), P.Enum("ONE"), P.Field(0)),
            P.Call(extract, P.Enum("MONDAY_DAY_OF_WEEK"), P.Enum("ONE"), P.Field(0)),
            P.Call(extract, P.Enum("SUNDAY_DAY_OF_WEEK"), P.Enum("ZERO"), P.Field(0)),
            P.Call(extractYear, P.Enum("UNIX_TIME"), P.Field(0))),
            ["iso_year", "iso_week", "us_year", "us_week", "monday_dow", "sunday_dow", "unix"], f);
        AssertRows(Run(plan), false,
            Row(2020L, 53L, 2021L, 1L, 7L, 0L, 1609632000L),
            Row(2021L, 52L, 2021L, 52L, 6L, 6L, 1640995200L),
            Row(2025L, 1L, 2025L, 1L, 1L, 1L, 1735516800L),
            Row(2020L, 53L, 2020L, 53L, 4L, 4L, 1609372800L));
    }

    [SkippableFact]
    public void DatetimeArithmeticAndTimeZones()
    {
        var f = new Functions();
        int addDay = f.Add("functions_datetime", "add:pts_iday");
        int addMonth = f.Add("functions_datetime", "add:date_iyear");
        int subtractDay = f.Add("functions_datetime", "subtract:pts_iday");
        int assume = f.Add("functions_datetime", "assume_timezone:pts_str");
        int extract = f.Add("functions_datetime", "extract:req_ptstz_str");
        byte[] values = P.Values([("ts", P.TTimestamp(6)), ("d", P.TDate)],
            [P.Timestamp(6, Micros(2024, 2, 28, 23, 0, 0)), P.Date(Days(2024, 1, 31))]);
        byte[] plan = P.Plan(Select(values, 2,
            P.Call(addDay, P.Field(0), P.IntervalDay(1, 3600, 500_000)),
            P.Call(subtractDay, P.Field(0), P.IntervalDay(0, 1)),
            P.Call(addMonth, P.Field(1), P.IntervalYear(0, 1)),
            P.Call(extract, P.Enum("HOUR"), P.Call(assume, P.Field(0), P.Str("Etc/GMT+5")), P.Str("+02:00"))),
            ["plus", "minus", "month", "hour"], f);
        // Leap year: Feb 28 23:00 + 1 day 1 hour = Mar 1 00:00:00.5; Jan 31 + 1 month = Feb 29.
        // 23:00 at UTC-5 is 04:00 UTC the next day, which is 06:00 at UTC+2.
        AssertRows(Run(plan), false, Row("2024-03-01T00:00:00.500000", "2024-02-28T22:59:59.000000", "2024-02-29", 6L));
    }

    [SkippableFact]
    public void DecimalArithmeticHasSubstraitResultType()
    {
        var f = new Functions();
        int divide = f.Add("functions_arithmetic_decimal", "divide:dec_dec");
        byte[] values = P.Values([("a", P.TDecimal(5, 2)), ("b", P.TDecimal(7, 3))],
            [P.Decimal(150, 5, 2), P.Decimal(2250, 7, 3)]);
        byte[] plan = P.Plan(Select(values, 2, P.Call(divide, P.Field(0), P.Field(1))), ["q"], f);

        SqlServerTestConfiguration config = SqlServerTestConfiguration.Load();
        using AdbcConnection connection = new SqlServerDriver().Open(config.Options).Connect(null);
        using AdbcStatement statement = connection.CreateStatement();
        statement.SubstraitPlan = plan;
        using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
        // Substrait: scale max(6, 2 + 7 + 1) = 10, precision 5 - 2 + 7 + 10 = 20 (SQL Server's own is 16).
        var type = Assert.IsType<Decimal128Type>(stream.Schema.FieldsList[0].DataType);
        Assert.Equal((20, 10), (type.Precision, type.Scale));
    }

    private static int Days(int year, int month, int day) => (int)(new DateTime(year, month, day) - new DateTime(1970, 1, 1)).TotalDays;

    private static long Micros(int year, int month, int day, int hour, int minute, int second)
        => (new DateTime(year, month, day, hour, minute, second) - new DateTime(1970, 1, 1)).Ticks / 10;

    #endregion

    #region The statement API

    [SkippableFact]
    public void StatementTranslatesAndDescribesPlans()
    {
        var f = new Functions();
        int gt = f.Add("functions_comparison", "gt:any_any");
        byte[] plan = P.Plan(Select(P.Filter(Orders, P.Call(gt, P.Field(4), P.I32(5))), 9, P.Field(0), P.Field(2)), ["id", "name"], f);

        SqlServerTestConfiguration config = SqlServerTestConfiguration.Load();
        using AdbcDatabase database = new SqlServerDriver().Open(config.Options);
        using AdbcConnection connection = database.Connect(null);
        SubstraitTestData.CreateTables(connection);
        using AdbcStatement statement = connection.CreateStatement();
        statement.SqlQuery = "SELECT 1";
        statement.SubstraitPlan = plan;
        Assert.Null(statement.SqlQuery);
        Assert.Contains("WHERE ([t0].[qty] > 5)", statement.GetOption(SqlServerParameters.SubstraitSql));

        Schema schema = statement.ExecuteSchema();
        Assert.Equal(new[] { "id", "name" }, schema.FieldsList.Select(field => field.Name));
        statement.Prepare();
        Assert.Empty(statement.GetParameterSchema().FieldsList);

        Assert.Contains("name: SQL Server", database.GetOption(SqlServerParameters.SubstraitDialect));
        Assert.Contains("supported_scalar_functions", connection.GetOption(SqlServerParameters.SubstraitDialect));
        Assert.Throws<AdbcException>(() => statement.SetOption(SqlServerParameters.SubstraitCollation, "Latin1_General_CI_AS; DROP TABLE x"));

        statement.SqlQuery = "SELECT 2";
        Assert.Null(statement.SubstraitPlan);
    }

    [SkippableFact]
    public void PartitionedReadsOfASortedPlan()
    {
        // A sorted plan can't be used as a derived table as it stands (T-SQL allows no ORDER BY there).
        byte[] plan = P.Plan(Select(P.Sort(Orders, P.SortField(P.Field(0), P.AscNullsFirst)), 9, P.Field(0)), ["id"]);
        SqlServerTestConfiguration config = SqlServerTestConfiguration.Load();
        using AdbcConnection connection = new SqlServerDriver().Open(config.Options).Connect(null);
        SubstraitTestData.CreateTables(connection);
        using AdbcStatement statement = connection.CreateStatement();
        statement.SetOption(SqlServerParameters.PartitionColumn, "id");
        statement.SetOption(SqlServerParameters.PartitionCount, "3");
        statement.SubstraitPlan = plan;
        PartitionedResult result = statement.ExecutePartitioned();
        long rows = 0;
        foreach (PartitionDescriptor partition in result.PartitionDescriptors)
        {
            using IArrowArrayStream stream = connection.ReadPartition(partition);
            while (stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is RecordBatch batch)
            {
                using (batch)
                {
                    rows += batch.Length;
                }
            }
        }
        Assert.Equal(9, rows);
    }

    #endregion
}
