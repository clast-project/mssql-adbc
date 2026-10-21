// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Clast.Drivers.SqlServer.Substrait;
using Xunit;

namespace Clast.Drivers.SqlServer.Tests.Substrait;

/// <summary>
/// Translations of hand-built plans, checked as text and with the T-SQL
/// parser; no server needed. SubstraitExecutionTests runs plans like these.
/// </summary>
public class SubstraitTranslationTests
{
    private static readonly (string, byte[])[] s_orders =
    [
        ("id", P.TI64), ("customer_id", P.TI64), ("name", P.TString), ("price", P.TFp64), ("qty", P.TI32),
        ("ts", P.TTimestamp(6)), ("flag", P.TBool), ("amount", P.TDecimal(10, 2)), ("d", P.TDate),
    ];

    private static byte[] Orders => P.Read("orders", s_orders);

    private static string Sql(byte[] plan, string? collation = SqlServerSubstrait.DefaultCollation)
    {
        string sql = SqlServerSubstrait.ToSql(plan, collation).Replace("\r\n", "\n");
        SubstraitTestData.AssertParses(sql);
        return sql;
    }

    private static string Rejection(byte[] plan) => Assert.Throws<SubstraitTranslationException>(() => SqlServerSubstrait.ToSql(plan)).Message;

    private static byte[] Select(byte[] input, int inputWidth, params byte[][] expressions)
        => P.ProjectEmit(input, Enumerable.Range(inputWidth, expressions.Length).ToArray(), expressions);

    [Fact]
    public void FilterAndProjectShareOneSelect()
    {
        var f = new Functions();
        int gt = f.Add("functions_comparison", "gt:any_any");
        int mul = f.Add("functions_arithmetic", "multiply:fp64_fp64");
        byte[] plan = P.Plan(Select(P.Filter(Orders, P.Call(gt, P.Field(4), P.I32(5))), 9, P.Field(2), P.Call(mul, P.Field(3), P.Fp64(2))),
            ["name", "double_price"], f);
        Assert.Equal(
            "SELECT [t0].[name] AS [name], ([t0].[price] * 2E0) AS [double_price]\n" +
            "FROM [orders] AS [t0]\n" +
            "WHERE ([t0].[qty] > 5)",
            Sql(plan));
    }

    [Fact]
    public void FilterOverALimitUsesADerivedTable()
    {
        var f = new Functions();
        int gt = f.Add("functions_comparison", "gt:any_any");
        byte[] limited = P.Fetch(P.Sort(Orders, P.SortField(P.Field(0), P.AscNullsFirst)), null, 5);
        byte[] plan = P.Plan(Select(P.Filter(limited, P.Call(gt, P.Field(4), P.I32(5))), 9, P.Field(0)), ["id"], f);
        Assert.Equal(
            "SELECT [t1].[id] AS [id]\n" +
            "FROM (\n" +
            "SELECT TOP (5) [t0].[id] AS [id], [t0].[customer_id] AS [customer_id], [t0].[name] AS [name], [t0].[price] AS [price], [t0].[qty] AS [qty], [t0].[ts] AS [ts], [t0].[flag] AS [flag], [t0].[amount] AS [amount], [t0].[d] AS [d], [t0].[id] AS [_o0]\n" +
            "FROM [orders] AS [t0]\n" +
            "ORDER BY [t0].[id]\n" +
            ") AS [t1]\n" +
            "WHERE ([t1].[qty] > 5)\n" +
            "ORDER BY [t1].[_o0]",
            Sql(plan));
    }

    [Fact]
    public void StringsGetTheCollationUnlessTurnedOff()
    {
        var f = new Functions();
        int eq = f.Add("functions_comparison", "equal:any_any");
        byte[] plan = P.Plan(Select(P.Filter(Orders, P.Call(eq, P.Field(2), P.Str("a"))), 9, P.Field(2)), ["name"], f);
        Assert.Equal(
            "SELECT [t0].[name] AS [name]\n" +
            "FROM [orders] AS [t0]\n" +
            "WHERE ([t0].[name] COLLATE Latin1_General_100_BIN2_UTF8 = N'a' COLLATE Latin1_General_100_BIN2_UTF8)",
            Sql(plan));
        Assert.Equal(
            "SELECT [t0].[name] AS [name]\n" +
            "FROM [orders] AS [t0]\n" +
            "WHERE ([t0].[name] = N'a')",
            Sql(plan, collation: null));
    }

    [Fact]
    public void BooleansAreConditionsOrBits()
    {
        var f = new Functions();
        int and = f.Add("functions_boolean", "and:bool");
        int gt = f.Add("functions_comparison", "gt:any_any");
        byte[] condition = P.Call(and, P.Field(6), P.Call(gt, P.Field(4), P.I32(2)));
        byte[] plan = P.Plan(Select(P.Filter(Orders, condition), 9, condition), ["b"], f);
        Assert.Equal(
            "SELECT CASE WHEN (([t0].[flag] = 1) AND ([t0].[qty] > 2)) THEN CAST(1 AS bit) WHEN NOT (([t0].[flag] = 1) AND ([t0].[qty] > 2)) THEN CAST(0 AS bit) END AS [b]\n" +
            "FROM [orders] AS [t0]\n" +
            "WHERE (([t0].[flag] = 1) AND ([t0].[qty] > 2))",
            Sql(plan));
    }

    [Fact]
    public void NullsLastNeedsAnExtraSortKey()
    {
        byte[] plan = P.Plan(Select(P.Sort(Orders, P.SortField(P.Field(3), P.AscNullsLast), P.SortField(P.Field(3), P.DescNullsLast)), 9, P.Field(0)), ["id"]);
        Assert.EndsWith("ORDER BY CASE WHEN [t0].[price] IS NULL THEN 1 ELSE 0 END, [t0].[price]", Sql(plan));
    }

    [Fact]
    public void SemiJoinIsExists()
    {
        var f = new Functions();
        int eq = f.Add("functions_comparison", "equal:any_any");
        byte[] customers = P.Read("customers", ("id", P.TI64), ("cname", P.TString));
        byte[] plan = P.Plan(Select(P.Join(Orders, customers, P.Call(eq, P.Field(1), P.Field(9)), P.LeftAnti), 9, P.Field(0)), ["id"], f);
        Assert.Equal(
            "SELECT [t0].[id] AS [id]\n" +
            "FROM [orders] AS [t0]\n" +
            "WHERE (NOT EXISTS (SELECT 1 FROM [customers] AS [t1] WHERE ([t0].[customer_id] = [t1].[id])))",
            Sql(plan));
    }

    [Fact]
    public void TableNamesHaveUpToFourParts()
    {
        byte[] plan = P.Plan(P.Read(["server", "db", "dbo", "t]x"], [("a", P.TI32)]), ["a"]);
        Assert.Contains("FROM [server].[db].[dbo].[t]]x] AS [t0]", Sql(plan));
        Assert.Contains("1 to 4 name parts", Rejection(P.Plan(P.Read(["a", "b", "c", "d", "e"], [("a", P.TI32)]))));
    }

    [Fact]
    public void LiteralsKeepTheirTypes()
    {
        byte[] plan = P.Plan(Select(Orders, 9,
            P.I16(-2), P.I32(-2), P.I64(5), P.Fp32(1.5f), P.Fp64(-0.25), P.Str("it's"), P.Bool(true),
            P.Date(19723), P.Timestamp(6, 1_704_067_200_123_456), P.TimestampTz(3, 1_704_067_200_123), P.Time(6, 3_723_000_001),
            P.Decimal(-12345, 7, 3), P.Uuid(new Guid("0F1E2D3C-4B5A-6978-8796-A5B4C3D2E1F0")), P.Binary(0xAB, 0x01), P.Null(P.TDecimal(5, 1))),
            null);
        string sql = Sql(plan, collation: null);
        Assert.Contains(
            "CAST(-2 AS smallint) AS [_e0], (-2) AS [_e1], CAST(5 AS bigint) AS [_e2], CAST(1.5E0 AS real) AS [_e3], (-0.25E0) AS [_e4], N'it''s' AS [_e5], CAST(1 AS bit) AS [_e6], " +
            "CAST('2024-01-01' AS date) AS [_e7], CAST('2024-01-01T00:00:00.123456' AS datetime2(6)) AS [_e8], CAST('2024-01-01T00:00:00.123+00:00' AS datetimeoffset(3)) AS [_e9], " +
            "CAST('01:02:03.000001' AS time(6)) AS [_e10], CAST(-12.345 AS decimal(7,3)) AS [_e11], CAST('0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0' AS uniqueidentifier) AS [_e12], " +
            "0xAB01 AS [_e13], CAST(NULL AS decimal(5,1)) AS [_e14]",
            sql);
    }

    [Fact]
    public void ExpressionsSupportedOnlyByWorkarounds()
    {
        var f = new Functions();
        int least = f.Add("functions_comparison", "least:any");
        int strpos = f.Add("functions_string", "strpos:str_str");
        int length = f.Add("functions_string", "char_length:str");
        byte[] plan = P.Plan(Select(Orders, 9,
            P.Call(least, P.Field(3), P.Fp64(1)),
            P.Call(strpos, P.Field(2), P.Str("b")),
            P.Call(length, P.Field(2))), null, f);
        string sql = Sql(plan, collation: null);
        // LEAST skips nulls, Substrait's least doesn't; LEN ignores trailing spaces.
        Assert.Contains("CASE WHEN [t0].[price] IS NULL THEN NULL ELSE LEAST([t0].[price], 1E0) END", sql);
        Assert.Contains("CAST(CHARINDEX(N'b', [t0].[name]) AS bigint)", sql);
        Assert.Contains("CAST(LEN(REPLACE([t0].[name], N' ', N'.')) AS bigint)", sql);
    }

    [Fact]
    public void RejectsWhatCannotBeTranslatedFaithfully()
    {
        var f = new Functions();
        int add = f.Add("functions_arithmetic", "add:i32_i32");
        int lt = f.Add("functions_comparison", "lt:any_any");
        int regexp = f.Add("functions_string", "regexp_replace:str_str_str");
        int addDay = f.Add("functions_datetime", "add:pts_iday");
        int unknown = f.Add("functions_string", "soundex:str");
        byte[] uuids = P.Values([("u", P.TUuid)], [P.Uuid(Guid.Empty)]);

        Assert.Contains("SILENT", Rejection(P.Plan(Select(Orders, 9, P.CallWith(add, [("overflow", ["SILENT"])], P.Field(4), P.I32(1))), null, f)));
        Assert.Contains("uuid", Rejection(P.Plan(Select(uuids, 1, P.Call(lt, P.Field(0), P.Field(0))), null, f)));
        Assert.Contains("RE2", Rejection(P.Plan(Select(Orders, 9, P.Call(regexp, P.Field(2), P.Str("a(?=b)"), P.Str("x"))), null, f)));
        Assert.Contains("must be a literal", Rejection(P.Plan(Select(Orders, 9, P.Call(addDay, P.Field(5), P.Null(P.TIntervalDay()))), null, f)));
        Assert.Contains("soundex", Rejection(P.Plan(Select(Orders, 9, P.Call(unknown, P.Field(2))), null, f)));
        Assert.Contains("i8", Rejection(P.Plan(Select(Orders, 9, P.I8(1)))));
        Assert.Contains("NaN", Rejection(P.Plan(Select(Orders, 9, P.Fp64(double.NaN)))));
        Assert.Contains("Single joins", Rejection(P.Plan(P.Join(Orders, Orders, null, P.LeftSingle))));
        Assert.Contains("string is not supported", Rejection(P.Plan(Select(Orders, 9, P.Cast(P.TString, P.Field(3))))));
    }

    [Fact]
    public void LegacyEncodingsAreAccepted()
    {
        // Older producers: extension URIs, int64 fetch fields, inline grouping expressions.
        var f = new Functions().UseLegacyUris();
        int count = f.Add("functions_aggregate_generic", "count:any");
        byte[] plan = P.Plan(P.FetchLegacy(P.AggregateLegacy(Orders, [P.Field(1)], P.Measure(count, [P.Field(0)])), 0, 3), ["c", "n"], f);
        Assert.Equal(
            "SELECT TOP (3) [t0].[customer_id] AS [c], COUNT_BIG([t0].[id]) AS [n]\n" +
            "FROM [orders] AS [t0]\n" +
            "GROUP BY [t0].[customer_id]",
            Sql(plan));
    }
}
