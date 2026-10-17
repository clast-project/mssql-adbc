// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Data.SqlTypes;
using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Clast.Drivers.SqlServer.Tests;

/// <summary>
/// Reads the same queries through the driver and through SqlClient and compares every cell.
/// </summary>
public class QueryTests
{
    private static AdbcConnection Connect(Action<Dictionary<string, string>>? configure = null)
    {
        SqlServerTestConfiguration config = SqlServerTestConfiguration.Load();
        var options = new Dictionary<string, string>(config.Options);
        configure?.Invoke(options);
        return new SqlServerDriver().Open(options).Connect(null);
    }

    private static List<object?[]> ReadAdbc(AdbcConnection connection, string sql, out Schema schema, out int batches)
    {
        using AdbcStatement statement = connection.CreateStatement();
        statement.SqlQuery = sql;
        QueryResult result = statement.ExecuteQuery();
        using IArrowArrayStream stream = result.Stream!;
        schema = stream.Schema;
        batches = 0;
        var rows = new List<object?[]>();
        while (stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is RecordBatch batch)
        {
            using (batch)
            {
                batches++;
                for (int r = 0; r < batch.Length; r++)
                {
                    var row = new object?[batch.ColumnCount];
                    for (int c = 0; c < batch.ColumnCount; c++)
                    {
                        row[c] = ArrowValue(batch.Column(c), r);
                    }
                    rows.Add(row);
                }
            }
        }
        return rows;
    }

    private static List<object?[]> ReadSqlClient(string sql)
    {
        SqlServerTestConfiguration config = SqlServerTestConfiguration.Load();
        using var connection = new SqlConnection(config.SqlClientConnectionString);
        connection.Open();
        using var command = new SqlCommand(sql, connection);
        using SqlDataReader reader = command.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (int c = 0; c < reader.FieldCount; c++)
            {
                row[c] = SqlClientValue(reader, c);
            }
            rows.Add(row);
        }
        return rows;
    }

    private static object? ArrowValue(IArrowArray array, int index)
    {
        if (array.IsNull(index))
        {
            return null;
        }
        return array switch
        {
            BooleanArray a => a.GetValue(index),
            UInt8Array a => a.GetValue(index),
            Int16Array a => a.GetValue(index),
            Int32Array a => a.GetValue(index),
            Int64Array a => a.GetValue(index),
            FloatArray a => a.GetValue(index),
            DoubleArray a => a.GetValue(index),
            Decimal128Array a => a.GetSqlDecimal(index)!.Value.ToString(),
            StringArray a => a.GetString(index),
            BinaryArray a => Convert.ToHexString(a.GetBytes(index)),
            Date32Array a => a.GetDateTime(index),
            TimestampArray a => a.GetTimestamp(index)!.Value.UtcDateTime,
            Time64Array a => TimeSpan.FromTicks(a.GetNanoSeconds(index)!.Value / 100),
            TimestampWithOffsetArray a => a.GetValue(index),
            GuidArray a => a.GetGuid(index),
            _ => throw new NotSupportedException(array.GetType().Name),
        };
    }

    private static object? SqlClientValue(SqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }
        object value = reader.GetSqlValue(ordinal);
        string typeName = reader.GetDataTypeName(ordinal);
        return value switch
        {
            SqlDecimal d => d.ToString(),
            SqlMoney m => SqlDecimal.ConvertToPrecScale(m.ToSqlDecimal(), typeName == "smallmoney" ? 10 : 19, 4).ToString(),
            SqlBinary b => Convert.ToHexString(b.Value),
            SqlBytes b => Convert.ToHexString(b.Value),
            SqlGuid g => g.Value,
            SqlXml x => x.Value,
            SqlByte b => b.Value,
            SqlInt16 i => i.Value,
            SqlInt32 i => i.Value,
            SqlInt64 i => i.Value,
            SqlBoolean b => b.Value,
            SqlSingle f => f.Value,
            SqlDouble f => f.Value,
            SqlString s => s.Value,
            SqlChars s => new string(s.Value),
            SqlDateTime d => d.Value,
            // datetime2 and datetimeoffset are read at microsecond precision.
            DateTime d => new DateTime(d.Ticks - d.Ticks % 10, DateTimeKind.Utc),
            DateTimeOffset d => new DateTimeOffset(d.Ticks - d.Ticks % 10, d.Offset),
            TimeSpan t => t,
            _ => throw new NotSupportedException(value.GetType().Name),
        };
    }

    private static void AssertSameAsSqlClient(string sql, Action<Dictionary<string, string>>? configure = null)
    {
        using AdbcConnection connection = Connect(configure);
        List<object?[]> actual = ReadAdbc(connection, sql, out _, out _);
        List<object?[]> expected = ReadSqlClient(sql);
        AssertRowsEqual(expected, actual);
    }

    private static void AssertRowsEqual(List<object?[]> expected, List<object?[]> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int r = 0; r < expected.Count; r++)
        {
            for (int c = 0; c < expected[r].Length; c++)
            {
                object? e = expected[r][c];
                object? a = actual[r][c];
                if (e is DateTime ed && a is DateTime ad)
                {
                    Assert.True(ed.Ticks == ad.Ticks, $"row {r} column {c}: expected {ed:o}, actual {ad:o}");
                }
                else
                {
                    Assert.True(Equals(e, a), $"row {r} column {c}: expected <{e}> ({e?.GetType().Name}), actual <{a}> ({a?.GetType().Name})");
                }
            }
        }
    }

    /// <summary>Edge values for each prototype type, plus a NULL row.</summary>
    private const string EdgeValues = """
        SELECT * FROM (VALUES
            (CAST(0 AS bit), CAST(0 AS tinyint), CAST(-32768 AS smallint), CAST(-2147483648 AS int), CAST(-9223372036854775808 AS bigint),
             CAST(-3.4e38 AS real), CAST(-1.79e308 AS float), CAST(-99999999999999.9999 AS decimal(18,4)), CAST('-9999999999999999999999999999.9999999999' AS decimal(38,10)),
             CAST(0.5 AS decimal(5,2)), CAST('' AS varchar(50)), CAST(N'' AS nvarchar(50)), CAST(0x AS varbinary(16)),
             CAST('0001-01-01' AS date), CAST('0001-01-01 00:00:00' AS datetime2(7)), CAST('0001-01-01 00:00:00 -14:00' AS datetimeoffset(7)),
             CAST('00000000-0000-0000-0000-000000000000' AS uniqueidentifier)),
            (CAST(1 AS bit), CAST(255 AS tinyint), CAST(32767 AS smallint), CAST(2147483647 AS int), CAST(9223372036854775807 AS bigint),
             CAST(3.4e38 AS real), CAST(1.79e308 AS float), CAST(99999999999999.9999 AS decimal(18,4)), CAST('9999999999999999999999999999.9999999999' AS decimal(38,10)),
             CAST(-0.01 AS decimal(5,2)), CAST('hello' AS varchar(50)), CAST(N'h€llo 😀 世界' AS nvarchar(50)), CAST(0x00FF10 AS varbinary(16)),
             CAST('9999-12-31' AS date), CAST('9999-12-31 23:59:59.9999999' AS datetime2(7)), CAST('9999-12-31 23:59:59.9999999 +14:00' AS datetimeoffset(7)),
             CAST('6F9619FF-8B86-D011-B42D-00C04FC964FF' AS uniqueidentifier)),
            (CAST(NULL AS bit), CAST(NULL AS tinyint), CAST(NULL AS smallint), CAST(NULL AS int), CAST(NULL AS bigint),
             CAST(NULL AS real), CAST(NULL AS float), CAST(NULL AS decimal(18,4)), CAST(NULL AS decimal(38,10)),
             CAST(NULL AS decimal(5,2)), CAST(NULL AS varchar(50)), CAST(NULL AS nvarchar(50)), CAST(NULL AS varbinary(16)),
             CAST(NULL AS date), CAST(NULL AS datetime2(7)), CAST(NULL AS datetimeoffset(7)),
             CAST(NULL AS uniqueidentifier)),
            (CAST(1 AS bit), CAST(7 AS tinyint), CAST(-1 AS smallint), CAST(42 AS int), CAST(-42 AS bigint),
             CAST(1.5 AS real), CAST(-2.25 AS float), CAST(0.0001 AS decimal(18,4)), CAST(-0.0000000001 AS decimal(38,10)),
             CAST(0 AS decimal(5,2)), CAST('x' AS varchar(50)), CAST(N'y' AS nvarchar(50)), CAST(0x01 AS varbinary(16)),
             CAST('1970-01-01' AS date), CAST('1969-12-31 23:59:59.9999999' AS datetime2(7)), CAST('2020-02-29 12:34:56.1234567 +05:30' AS datetimeoffset(7)),
             NEWID())
        ) AS v(c_bit, c_tinyint, c_smallint, c_int, c_bigint, c_real, c_float, c_decimal, c_decimal38, c_decimal5,
               c_varchar, c_nvarchar, c_varbinary, c_date, c_datetime2, c_datetimeoffset, c_uniqueidentifier)
        """;

    [SkippableFact]
    public void EdgeValuesMatchSqlClient()
    {
        using AdbcConnection connection = Connect();
        // NEWID() differs per execution, so pin the values in a temp table first.
        using (AdbcStatement statement = connection.CreateStatement())
        {
            statement.SqlQuery = "SELECT * INTO ##clast_edge FROM (" + EdgeValues + ") AS x";
            statement.ExecuteUpdate();
        }
        try
        {
            List<object?[]> actual = ReadAdbc(connection, "SELECT * FROM ##clast_edge", out Schema schema, out _);
            List<object?[]> expected = ReadSqlClient("SELECT * FROM ##clast_edge");
            AssertRowsEqual(expected, actual);
            Assert.Equal(4, actual.Count);
            Assert.All(actual[2], Assert.Null);
        }
        finally
        {
            using AdbcStatement statement = connection.CreateStatement();
            statement.SqlQuery = "DROP TABLE ##clast_edge";
            statement.ExecuteUpdate();
        }
    }

    [SkippableFact]
    public void SchemaMapsTypes()
    {
        using AdbcConnection connection = Connect();
        ReadAdbc(connection, EdgeValues, out Schema schema, out _);

        Assert.IsType<BooleanType>(schema.GetFieldByName("c_bit").DataType);
        Assert.IsType<UInt8Type>(schema.GetFieldByName("c_tinyint").DataType);
        Assert.IsType<Int16Type>(schema.GetFieldByName("c_smallint").DataType);
        Assert.IsType<Int32Type>(schema.GetFieldByName("c_int").DataType);
        Assert.IsType<Int64Type>(schema.GetFieldByName("c_bigint").DataType);
        Assert.IsType<FloatType>(schema.GetFieldByName("c_real").DataType);
        Assert.IsType<DoubleType>(schema.GetFieldByName("c_float").DataType);
        var dec = Assert.IsType<Decimal128Type>(schema.GetFieldByName("c_decimal38").DataType);
        Assert.Equal((38, 10), (dec.Precision, dec.Scale));
        Assert.IsType<StringType>(schema.GetFieldByName("c_varchar").DataType);
        Assert.IsType<StringType>(schema.GetFieldByName("c_nvarchar").DataType);
        Assert.IsType<BinaryType>(schema.GetFieldByName("c_varbinary").DataType);
        Assert.IsType<Date32Type>(schema.GetFieldByName("c_date").DataType);
        var ts = Assert.IsType<TimestampType>(schema.GetFieldByName("c_datetime2").DataType);
        Assert.Equal(TimeUnit.Microsecond, ts.Unit);
        Assert.Null(ts.Timezone);
        Assert.IsType<TimestampWithOffsetType>(schema.GetFieldByName("c_datetimeoffset").DataType);
        Assert.IsType<GuidType>(schema.GetFieldByName("c_uniqueidentifier").DataType);
        Assert.Equal("DATETIME2(7)", schema.GetFieldByName("c_datetime2").Metadata[Arrow.DecoderFactory.SqlTypeMetadataKey]);
    }

    [SkippableFact]
    public void OtherTypesMatchSqlClient()
    {
        AssertSameAsSqlClient("""
            SELECT * FROM (VALUES
                (CAST(922337203685477.5807 AS money), CAST(-214748.3648 AS smallmoney), CAST('1753-01-01 00:00:00.003' AS datetime),
                 CAST('2079-06-06 23:59:00' AS smalldatetime), CAST('23:59:59.9999999' AS time(7)), CAST('12:00:00.5' AS time(1)),
                 CAST('2020-01-02 03:04:05.123' AS datetime2(3)), CAST('2020-01-02 03:04:05' AS datetime2(0)),
                 CAST('abc' AS char(5)), CAST(N'abc' AS nchar(5)), CAST(0x0102 AS binary(4)),
                 CAST('<a b="1">x</a>' AS xml), CAST(12345.678 AS numeric(10,3))),
                (CAST(-922337203685477.5808 AS money), CAST(214748.3647 AS smallmoney), CAST('9999-12-31 23:59:59.997' AS datetime),
                 CAST('1900-01-01 00:00:00' AS smalldatetime), CAST('00:00:00' AS time(7)), CAST('00:00:00' AS time(1)),
                 CAST('1900-01-01 00:00:00.000' AS datetime2(3)), CAST('1900-01-01' AS datetime2(0)),
                 NULL, NULL, NULL, NULL, NULL)
            ) AS v(m, sm, dt, sdt, t7, t1, dt23, dt20, ch, nch, bin, x, num)
            """);
    }

    [SkippableFact]
    public void CodePageAndUtf8VarcharMatchSqlClient()
    {
        AssertSameAsSqlClient("""
            SELECT
                CAST('caf' + CHAR(233) + ' ' + CHAR(128) AS varchar(20)) COLLATE SQL_Latin1_General_CP1_CI_AS AS cp1252,
                CAST(N'caf' + NCHAR(233) + N' ' + NCHAR(0x20AC) AS varchar(20)) COLLATE Latin1_General_100_CI_AS_SC_UTF8 AS utf8,
                CAST(N'Ελληνικά' AS varchar(20)) COLLATE Greek_CI_AS AS cp1253,
                CAST(N'日本語' AS varchar(20)) COLLATE Japanese_CI_AS AS cp932
            """);
    }

    [SkippableFact]
    public void LargeValuesSpanPackets()
    {
        // PLP values of several packets, plus long non-PLP values that straddle packet boundaries.
        AssertSameAsSqlClient("""
            SELECT TOP (50)
                n,
                REPLICATE(CAST(N'ab😀' AS nvarchar(max)), 20000 + n) AS big_nvarchar,
                REPLICATE(CAST('xyz' AS varchar(max)), 30000 + n) AS big_varchar,
                CAST(REPLICATE(CAST('Q' AS varchar(max)), 70000) AS varbinary(max)) AS big_binary,
                REPLICATE(N'é', 4000) AS full_nvarchar,
                CAST(NULL AS nvarchar(max)) AS null_max
            FROM (SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects) AS t
            ORDER BY n
            """,
            o => o[SqlServerParameters.PacketSize] = "4096");
    }

    [SkippableFact]
    public void SparseNullsUseNbcRows()
    {
        // Mostly-null wide rows make the server use NBCROW tokens.
        string columns = string.Join(", ", Enumerable.Range(0, 40).Select(i =>
            $"CASE WHEN n % 40 = {i} THEN CAST(n * {i} AS int) END AS c{i}"));
        AssertSameAsSqlClient($"""
            SELECT TOP (500) n, {columns}
            FROM (SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects a CROSS JOIN sys.all_objects b) AS t
            ORDER BY n
            """);
    }

    [SkippableFact]
    public void SplitsIntoBatches()
    {
        using AdbcConnection connection = Connect(o => o[SqlServerParameters.BatchRows] = "7");
        List<object?[]> rows = ReadAdbc(connection,
            "SELECT TOP (100) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n, CAST(NULL AS int) AS z FROM sys.all_objects",
            out _, out int batches);
        Assert.Equal(100, rows.Count);
        Assert.Equal(15, batches);
        Assert.Equal(Enumerable.Range(1, 100).Select(i => (long)i), rows.Select(r => (long)r[0]!));
    }

    [SkippableFact]
    public void ReturnsFirstResultSetAndSkipsTheRest()
    {
        using AdbcConnection connection = Connect();
        List<object?[]> rows = ReadAdbc(connection,
            "SET NOCOUNT ON; DECLARE @x int = 1; SELECT 1 AS a UNION ALL SELECT 2; SELECT 'other' AS b; SELECT 3",
            out Schema schema, out _);
        Assert.Equal("a", Assert.Single(schema.FieldsList).Name);
        Assert.Equal(new object?[] { 1, 2 }, rows.Select(r => r[0]));

        // The session is usable afterwards.
        Assert.Single(ReadAdbc(connection, "SELECT 1", out _, out _));
    }

    [SkippableFact]
    public void NoResultSetGivesEmptyStream()
    {
        using AdbcConnection connection = Connect();
        List<object?[]> rows = ReadAdbc(connection, "DECLARE @x int = 1", out Schema schema, out int batches);
        Assert.Empty(rows);
        Assert.Empty(schema.FieldsList);
        Assert.Equal(0, batches);
    }

    [SkippableFact]
    public void ErrorDuringResultSetIsThrown()
    {
        using AdbcConnection connection = Connect();
        var ex = Assert.Throws<SqlServerException>(() => ReadAdbc(connection,
            "SELECT TOP (100) 1 / (ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 50) AS x FROM sys.all_objects",
            out _, out _));
        Assert.Equal(8134, ex.Number); // divide by zero
        Assert.Single(ReadAdbc(connection, "SELECT 1", out _, out _));
    }

    [SkippableFact]
    public void ErrorBeforeResultSetIsThrown()
    {
        using AdbcConnection connection = Connect();
        var ex = Assert.Throws<SqlServerException>(() => ReadAdbc(connection, "SELECT * FROM dbo.no_such_table_clast", out _, out _));
        Assert.Equal(208, ex.Number);
        Assert.Equal(AdbcStatusCode.NotFound, ex.Status);
        Assert.Single(ReadAdbc(connection, "SELECT 1", out _, out _));
    }

    [SkippableFact]
    public void AbandonedStreamLeavesSessionUsable()
    {
        using AdbcConnection connection = Connect(o => o[SqlServerParameters.BatchRows] = "10");
        using (AdbcStatement statement = connection.CreateStatement())
        {
            statement.SqlQuery = "SELECT TOP (1000) * FROM sys.all_objects";
            using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
            using RecordBatch? first = stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult();
            Assert.Equal(10, first!.Length);

            // A second command while the stream is open is rejected.
            using AdbcStatement other = connection.CreateStatement();
            other.SqlQuery = "SELECT 1";
            Assert.Equal(AdbcStatusCode.InvalidState, Assert.ThrowsAny<AdbcException>(() => other.ExecuteQuery()).Status);
        }
        Assert.Single(ReadAdbc(connection, "SELECT 1", out _, out _));
    }

    [SkippableFact]
    public void UnsupportedTypeIsNotImplemented()
    {
        using AdbcConnection connection = Connect();
        var ex = Assert.ThrowsAny<AdbcException>(() => ReadAdbc(connection, "SELECT CAST(1 AS sql_variant) AS v", out _, out _));
        Assert.Equal(AdbcStatusCode.NotImplemented, ex.Status);
        Assert.Single(ReadAdbc(connection, "SELECT 1", out _, out _));
    }

    [SkippableFact]
    public void BenchmarkShapedTableMatchesSqlClient()
    {
        AssertSameAsSqlClient("""
            WITH n AS (SELECT TOP (20000) CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS bigint) AS i FROM sys.all_columns a CROSS JOIN sys.all_columns b)
            SELECT i,
                CASE WHEN i % 10 = 3 THEN NULL ELSE CAST(i * 7 % 2000000000 AS int) END AS c_int,
                CASE WHEN i % 10 = 8 THEN NULL ELSE CAST(i / 9.0 AS decimal(18,4)) END AS c_decimal,
                CASE WHEN i % 10 = 1 THEN NULL ELSE CONCAT(N'row ', i, N' nvarchar ', NCHAR(0x00E9)) END AS c_nvarchar,
                CASE WHEN i % 10 = 4 THEN NULL ELSE DATEADD(second, CAST(i AS int), CAST('2000-01-01' AS datetime2(7))) END AS c_datetime2
            FROM n ORDER BY i
            """);
    }
}
