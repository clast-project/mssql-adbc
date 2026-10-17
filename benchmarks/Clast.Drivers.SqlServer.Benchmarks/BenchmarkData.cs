// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Data.SqlClient;

namespace Clast.Drivers.SqlServer.Benchmarks;

/// <summary>
/// Creates the benchmark tables server-side, so data generation never goes
/// through any of the drivers being measured.
/// </summary>
internal static class BenchmarkData
{
    /// <summary>
    /// Whether the benchmark server is a Fabric Warehouse (or Synapse serverless
    /// pool), whose tables store a subset of the types: there the table has the
    /// nearest stored type in each column, under the same names.
    /// </summary>
    public static bool Warehouse => s_warehouse.Value;

    private static readonly Lazy<bool> s_warehouse = new(() =>
    {
        using var connection = BenchmarkSettings.NewSqlConnection(BenchmarkSettings.SqlClientConnectionString);
        connection.Open();
        using var command = new SqlCommand("SELECT CAST(SERVERPROPERTY('EngineEdition') AS int)", connection);
        return (int)command.ExecuteScalar() == 11;
    });

    public static string Columns => Warehouse ? WarehouseColumns : SqlServerColumns;

    /// <summary>
    /// The warehouse's equivalents: smallint for tinyint, varchar (UTF-8) for
    /// nvarchar, datetime2(6) for datetime2(7) and for datetimeoffset.
    /// </summary>
    private const string WarehouseColumns = """
        id bigint NOT NULL,
        c_tinyint smallint NULL,
        c_smallint smallint NULL,
        c_int int NULL,
        c_bigint bigint NULL,
        c_bit bit NULL,
        c_real real NULL,
        c_float float NULL,
        c_decimal decimal(18,4) NULL,
        c_decimal38 decimal(38,10) NULL,
        c_varchar varchar(50) NULL,
        c_nvarchar varchar(100) NULL,
        c_varbinary varbinary(16) NULL,
        c_date date NULL,
        c_datetime2 datetime2(6) NULL,
        c_datetimeoffset datetime2(6) NULL,
        c_uniqueidentifier uniqueidentifier NULL
        """;

    /// <summary>One column per prototype type; every column except <c>id</c> is about 10% NULL.</summary>
    private const string SqlServerColumns = """
        id bigint NOT NULL,
        c_tinyint tinyint NULL,
        c_smallint smallint NULL,
        c_int int NULL,
        c_bigint bigint NULL,
        c_bit bit NULL,
        c_real real NULL,
        c_float float NULL,
        c_decimal decimal(18,4) NULL,
        c_decimal38 decimal(38,10) NULL,
        c_varchar varchar(50) NULL,
        c_nvarchar nvarchar(50) NULL,
        c_varbinary varbinary(16) NULL,
        c_date date NULL,
        c_datetime2 datetime2(7) NULL,
        c_datetimeoffset datetimeoffset(7) NULL,
        c_uniqueidentifier uniqueidentifier NULL
        """;

    /// <summary>The prefix of the benchmark's tables: <c>clast_</c> on a shared warehouse, to keep clear of its own tables.</summary>
    public static string Prefix => Warehouse ? "clast_bench_" : "bench_";

    public static string TableName(int rows) => $"dbo.{Prefix}types_{rows}";

    /// <summary>Creates <see cref="TableName"/> with <paramref name="rows"/> rows unless it already exists.</summary>
    public static void EnsureTable(int rows)
    {
        string table = TableName(rows);
        string sql = Warehouse ? WarehouseTableSql(table, rows) : $"""
            IF OBJECT_ID('{table}') IS NULL
            BEGIN
                CREATE TABLE {table} ({Columns});

                WITH n AS (
                    SELECT TOP ({rows}) CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS bigint) AS i
                    FROM sys.all_columns a CROSS JOIN sys.all_columns b CROSS JOIN sys.all_columns c
                )
                INSERT INTO {table} WITH (TABLOCK)
                SELECT
                    i,
                    CASE WHEN i % 10 = 1 THEN NULL ELSE CAST(i % 256 AS tinyint) END,
                    CASE WHEN i % 10 = 2 THEN NULL ELSE CAST(i % 32000 AS smallint) END,
                    CASE WHEN i % 10 = 3 THEN NULL ELSE CAST(i * 7 % 2000000000 AS int) END,
                    CASE WHEN i % 10 = 4 THEN NULL ELSE i * 1000003 END,
                    CASE WHEN i % 10 = 5 THEN NULL ELSE CAST(i % 2 AS bit) END,
                    CASE WHEN i % 10 = 6 THEN NULL ELSE CAST(i / 3.0 AS real) END,
                    CASE WHEN i % 10 = 7 THEN NULL ELSE i / 7.0E0 END,
                    CASE WHEN i % 10 = 8 THEN NULL ELSE CAST(i / 9.0 AS decimal(18,4)) END,
                    CASE WHEN i % 10 = 9 THEN NULL ELSE CAST(i * 1234567.0123456789 AS decimal(38,10)) END,
                    CASE WHEN i % 10 = 0 THEN NULL ELSE CONCAT('row ', i, ' varchar') END,
                    CASE WHEN i % 10 = 1 THEN NULL ELSE CONCAT(N'row ', i, N' nvarchar ', NCHAR(0x00E9)) END,
                    CASE WHEN i % 10 = 2 THEN NULL ELSE CAST(HASHBYTES('MD5', CAST(i AS varchar(20))) AS varbinary(16)) END,
                    CASE WHEN i % 10 = 3 THEN NULL ELSE DATEADD(day, CAST(i % 36500 AS int), CAST('1950-01-01' AS date)) END,
                    CASE WHEN i % 10 = 4 THEN NULL ELSE DATEADD(second, CAST(i % 1000000000 AS int), CAST('2000-01-01' AS datetime2(7))) END,
                    CASE WHEN i % 10 = 5 THEN NULL ELSE TODATETIMEOFFSET(DATEADD(second, CAST(i % 1000000000 AS int), CAST('2000-01-01' AS datetime2(7))), CAST(i % 1500 AS int) - 720) END,
                    CASE WHEN i % 10 = 6 THEN NULL ELSE NEWID() END
                FROM n;
            END;
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('{table}') AND index_id = 1)
                CREATE CLUSTERED INDEX cx_id ON {table} (id);
            """;

        using var connection = BenchmarkSettings.NewSqlConnection(BenchmarkSettings.SqlClientConnectionString);
        connection.Open();
        using SqlCommand command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// The same values for a warehouse, which has no system tables to cross
    /// join for row numbers, no clustered indexes and no table hints: row
    /// numbers come from a cross join of digits.
    /// </summary>
    private static string WarehouseTableSql(string table, int rows) => $"""
        IF OBJECT_ID('{table}') IS NULL
        BEGIN
            CREATE TABLE {table} ({WarehouseColumns});

            WITH d AS (SELECT v FROM (VALUES (0), (1), (2), (3), (4), (5), (6), (7), (8), (9)) AS t (v)),
            n AS (
                SELECT CAST(1 + a.v + 10 * b.v + 100 * c.v + 1000 * e.v + 10000 * f.v + 100000 * g.v + 1000000 * h.v AS bigint) AS i
                FROM d a CROSS JOIN d b CROSS JOIN d c CROSS JOIN d e CROSS JOIN d f CROSS JOIN d g CROSS JOIN d h
            )
            INSERT INTO {table}
            SELECT
                i,
                CASE WHEN i % 10 = 1 THEN NULL ELSE CAST(i % 256 AS smallint) END,
                CASE WHEN i % 10 = 2 THEN NULL ELSE CAST(i % 32000 AS smallint) END,
                CASE WHEN i % 10 = 3 THEN NULL ELSE CAST(i * 7 % 2000000000 AS int) END,
                CASE WHEN i % 10 = 4 THEN NULL ELSE i * 1000003 END,
                CASE WHEN i % 10 = 5 THEN NULL ELSE CAST(i % 2 AS bit) END,
                CASE WHEN i % 10 = 6 THEN NULL ELSE CAST(i / 3.0 AS real) END,
                CASE WHEN i % 10 = 7 THEN NULL ELSE i / 7.0E0 END,
                CASE WHEN i % 10 = 8 THEN NULL ELSE CAST(i / 9.0 AS decimal(18,4)) END,
                CASE WHEN i % 10 = 9 THEN NULL ELSE CAST(i * 1234567.0123456789 AS decimal(38,10)) END,
                CASE WHEN i % 10 = 0 THEN NULL ELSE CONCAT('row ', i, ' varchar') END,
                CASE WHEN i % 10 = 1 THEN NULL ELSE CONCAT('row ', i, ' nvarchar ', NCHAR(0x00E9)) END,
                CASE WHEN i % 10 = 2 THEN NULL ELSE CAST(HASHBYTES('MD5', CAST(i AS varchar(20))) AS varbinary(16)) END,
                CASE WHEN i % 10 = 3 THEN NULL ELSE DATEADD(day, CAST(i % 36500 AS int), CAST('1950-01-01' AS date)) END,
                CASE WHEN i % 10 = 4 THEN NULL ELSE DATEADD(second, CAST(i % 1000000000 AS int), CAST('2000-01-01' AS datetime2(6))) END,
                CASE WHEN i % 10 = 5 THEN NULL ELSE DATEADD(minute, CAST(i % 1500 AS int) - 720, DATEADD(second, CAST(i % 1000000000 AS int), CAST('2000-01-01' AS datetime2(6)))) END,
                CASE WHEN i % 10 = 6 THEN NULL ELSE NEWID() END
            FROM n
            WHERE i <= {rows};
        END;
        """;
}
