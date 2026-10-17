// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Xunit;

namespace Clast.Drivers.SqlServer.Tests;

[Collection(CatalogCollection.Name)]
public sealed class StatisticsTests : IDisposable
{
    private readonly string _table = "clast_stats_" + Guid.NewGuid().ToString("N").Substring(0, 8);
    private readonly AdbcConnection _connection;

    public StatisticsTests()
    {
        SqlServerTestConfiguration config = SqlServerTestConfiguration.Load();
        _connection = new SqlServerDriver().Open(config.Options).Connect(null);
        // 1000 rows; a has 37 distinct values and 100 NULLs.
        Execute($"""
            CREATE TABLE tempdb.dbo.{_table} (a int NULL, b nvarchar(10) NULL);
            INSERT INTO tempdb.dbo.{_table}
            SELECT TOP (1000) CASE WHEN n % 10 = 0 THEN NULL ELSE n % 37 END, N'x'
            FROM (SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects a CROSS JOIN sys.all_objects b) AS t;
            EXEC tempdb.sys.sp_executesql N'CREATE STATISTICS st_a ON dbo.{_table} (a) WITH FULLSCAN';
            """);
    }

    public void Dispose()
    {
        try
        {
            Execute($"DROP TABLE IF EXISTS tempdb.dbo.{_table}");
        }
        finally
        {
            _connection.Dispose();
        }
    }

    private void Execute(string sql)
    {
        using AdbcStatement statement = _connection.CreateStatement();
        statement.SqlQuery = sql;
        statement.ExecuteUpdate();
    }

    private sealed record Row(string? Column, short Key, object Value, bool Approximate);

    private List<Row> Read(bool approximate)
    {
        using IArrowArrayStream stream = _connection.GetStatistics("tempdb", "dbo", _table, approximate);
        using RecordBatch batch = stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult()!;
        Assert.Equal(1, batch.Length);
        Assert.Equal("tempdb", ((StringArray)batch.Column(0)).GetString(0));
        var schemas = (StructArray)((ListArray)batch.Column(1)).GetSlicedValues(0);
        Assert.Equal(1, schemas.Length);
        Assert.Equal("dbo", ((StringArray)schemas.Fields[0]).GetString(0));
        var statistics = (StructArray)((ListArray)schemas.Fields[1]).GetSlicedValues(0);

        var rows = new List<Row>();
        var values = (DenseUnionArray)statistics.Fields[3];
        for (int i = 0; i < statistics.Length; i++)
        {
            Assert.Equal(_table, ((StringArray)statistics.Fields[0]).GetString(i));
            int offset = values.ValueOffsets[i];
            object value = values.Fields[values.TypeIds[i]] switch
            {
                // As object, so the long is not widened to the double arm's type.
                Int64Array l => (object)l.GetValue(offset)!.Value,
                DoubleArray d => d.GetValue(offset)!.Value,
                _ => throw new InvalidOperationException(),
            };
            rows.Add(new Row(((StringArray)statistics.Fields[1]).GetString(i), ((Int16Array)statistics.Fields[2]).GetValue(i)!.Value,
                value, ((BooleanArray)statistics.Fields[4]).GetValue(i)!.Value));
        }
        return rows;
    }

    [SkippableFact]
    public void ApproximateStatisticsComeFromTheServer()
    {
        List<Row> rows = Read(approximate: true);
        Assert.Contains(new Row(null, AdbcStatistic.RowCountKey, 1000.0, true), rows);
        Assert.Contains(new Row("a", AdbcStatistic.DistinctCountKey, 37.0, true), rows);
        Assert.Contains(new Row("a", AdbcStatistic.NullCountKey, 100.0, true), rows);
    }

    [SkippableFact]
    public void ExactStatisticsAreCounted()
    {
        Row row = Assert.Single(Read(approximate: false));
        Assert.Null(row.Column);
        Assert.Equal(AdbcStatistic.RowCountKey, row.Key);
        Assert.Equal(1000L, Assert.IsType<long>(row.Value));
        Assert.False(row.Approximate);
    }

    [SkippableFact]
    public void NoDriverSpecificStatistics()
    {
        using IArrowArrayStream stream = _connection.GetStatisticsNames();
        Assert.Equal(["statistic_name", "statistic_key"], stream.Schema.FieldsList.Select(f => f.Name));
        using RecordBatch batch = stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult()!;
        Assert.Equal(0, batch.Length);

        using IArrowArrayStream none = _connection.GetStatistics("", null, _table, approximate: true);
        using RecordBatch empty = none.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult()!;
        Assert.Equal(0, empty.Length);
    }

    [SkippableFact]
    public void WholeServer()
    {
        using IArrowArrayStream stream = _connection.GetStatistics(null, null, null!, approximate: true);
        using RecordBatch batch = stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult()!;
        Assert.True(batch.Length >= 4);
        Assert.IsType<UnionType>(((StructType)((ListType)((StructType)((ListType)batch.Schema.FieldsList[1].DataType).ValueDataType).Fields[1].DataType).ValueDataType).Fields[3].DataType);
    }
}
