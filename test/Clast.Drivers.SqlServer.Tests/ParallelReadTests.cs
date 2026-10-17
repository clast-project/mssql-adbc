// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Xunit;

namespace Clast.Drivers.SqlServer.Tests;

public class ParallelReadTests
{
    // Keys -5..19994 plus three NULL keys; v identifies each row.
    private const string Query = """
        SELECT CASE WHEN n > 20000 THEN NULL ELSE CAST(n - 6 AS bigint) END AS k, CAST(n AS int) AS v, CONCAT(N'row ', n) AS s
        FROM (SELECT TOP (20003) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects a CROSS JOIN sys.all_objects b) AS t
        """;

    private static AdbcConnection Connect(Action<Dictionary<string, string>>? configure = null)
    {
        SqlServerTestConfiguration config = SqlServerTestConfiguration.Load();
        var options = new Dictionary<string, string>(config.Options) { [SqlServerParameters.BatchRows] = "1000" };
        configure?.Invoke(options);
        return new SqlServerDriver().Open(options).Connect(null);
    }

    private static AdbcStatement Parallel(AdbcConnection connection, string sql, int parallelism, int? partitions = null)
    {
        AdbcStatement statement = connection.CreateStatement();
        statement.SqlQuery = sql;
        statement.SetOption(SqlServerParameters.PartitionColumn, "k");
        statement.SetOption(SqlServerParameters.ParallelReads, parallelism.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (partitions != null)
        {
            statement.SetOption(SqlServerParameters.PartitionCount, partitions.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return statement;
    }

    private static List<int> ReadValues(IArrowArrayStream stream)
    {
        var values = new List<int>();
        while (stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is RecordBatch batch)
        {
            using (batch)
            {
                Assert.Same(stream.Schema, batch.Schema);
                var column = (Int32Array)batch.Column(1);
                for (int i = 0; i < batch.Length; i++)
                {
                    values.Add(column.GetValue(i)!.Value);
                }
            }
        }
        return values;
    }

    private static List<int> ReadParallel(AdbcConnection connection, string sql, int parallelism, int? partitions = null, bool expectParallel = true)
    {
        using AdbcStatement statement = Parallel(connection, sql, parallelism, partitions);
        using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
        Assert.Equal(expectParallel, stream is SqlServerParallelQueryStream);
        return ReadValues(stream);
    }

    [SkippableTheory]
    [InlineData(2, null)]
    [InlineData(4, null)]
    [InlineData(4, 13)]
    public void ReturnsEveryRowOnce(int parallelism, int? partitions)
    {
        using AdbcConnection connection = Connect();
        List<int> values = ReadParallel(connection, Query, parallelism, partitions);
        Assert.Equal(Enumerable.Range(1, 20003), values.OrderBy(v => v));
    }

    [SkippableFact]
    public void ReusesHelperSessionsAndHonorsDatabaseDefault()
    {
        using AdbcConnection connection = Connect(o => o[SqlServerParameters.ParallelReads] = "3");
        for (int i = 0; i < 3; i++)
        {
            using AdbcStatement statement = connection.CreateStatement();
            statement.SqlQuery = Query;
            statement.SetOption(SqlServerParameters.PartitionColumn, "k");
            using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
            Assert.Equal(20003, ReadValues(stream).Count);
        }
    }

    [SkippableFact]
    public void ErrorInOnePartitionIsThrown()
    {
        using AdbcConnection connection = Connect();
        string failing = Query.Replace("CAST(n AS int) AS v", "CAST(1 / (n - 15000) AS int) AS v");
        var ex = Assert.Throws<SqlServerException>(() => ReadParallel(connection, failing, 4));
        Assert.Equal(8134, ex.Number);

        Assert.Equal(20003, ReadParallel(connection, Query, 4).Count);
    }

    [SkippableFact]
    public void EarlyDisposeLeavesConnectionUsable()
    {
        using AdbcConnection connection = Connect();
        using (AdbcStatement statement = Parallel(connection, Query, 4))
        {
            using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
            using RecordBatch first = stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult()!;
            Assert.True(first.Length > 0);

            using AdbcStatement other = connection.CreateStatement();
            other.SqlQuery = "SELECT 1";
            Assert.Equal(AdbcStatusCode.InvalidState, Assert.ThrowsAny<AdbcException>(() => other.ExecuteQuery()).Status);
        }

        Assert.Equal(20003, ReadParallel(connection, Query, 4).Count);
    }

    [SkippableFact]
    public void FallsBackToSerialInsideTransaction()
    {
        using AdbcConnection connection = Connect();
        using (AdbcStatement setup = connection.CreateStatement())
        {
            // A local temp table is invisible to helper sessions, so this only works serially.
            setup.SqlQuery = "BEGIN TRANSACTION; SELECT k, v, s INTO #parallel_tx FROM (" + Query + ") AS q;";
            setup.ExecuteUpdate();
        }
        try
        {
            Assert.Equal(20003, ReadParallel(connection, "SELECT k, v, s FROM #parallel_tx", 4, expectParallel: false).Count);
        }
        finally
        {
            using AdbcStatement rollback = connection.CreateStatement();
            rollback.SqlQuery = "ROLLBACK TRANSACTION";
            rollback.ExecuteUpdate();
        }
    }

    [SkippableFact]
    public void HelpersFollowCurrentDatabase()
    {
        using AdbcConnection connection = Connect();
        using (AdbcStatement use = connection.CreateStatement())
        {
            use.SqlQuery = "USE tempdb";
            use.ExecuteUpdate();
        }
        List<int> values = ReadParallel(connection,
            "SELECT CAST(n AS bigint) AS k, CAST(n AS int) AS v FROM (SELECT TOP (5000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects a CROSS JOIN sys.all_objects b) AS t WHERE DB_NAME() = 'tempdb'",
            4);
        Assert.Equal(5000, values.Count);
    }
}
