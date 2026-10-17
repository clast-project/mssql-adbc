// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Xunit;

namespace Clast.Drivers.SqlServer.Tests;

public class PartitionTests
{
    // Keys -5..994 plus three NULL keys; the value column identifies each row.
    private const string Query = """
        SELECT CASE WHEN n > 1000 THEN NULL ELSE CAST(n - 6 AS bigint) END AS k, CAST(n AS int) AS v, CONCAT(N'row ', n) AS s
        FROM (SELECT TOP (1003) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects a CROSS JOIN sys.all_objects b) AS t
        """;

    private static AdbcDatabase OpenDatabase()
    {
        SqlServerTestConfiguration config = SqlServerTestConfiguration.Load();
        return new SqlServerDriver().Open(config.Options);
    }

    private static PartitionedResult Partition(AdbcConnection connection, string sql, string? column, int count)
    {
        using AdbcStatement statement = connection.CreateStatement();
        statement.SqlQuery = sql;
        if (column != null)
        {
            statement.SetOption(SqlServerParameters.PartitionColumn, column);
        }
        statement.SetOption(SqlServerParameters.PartitionCount, count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return statement.ExecutePartitioned();
    }

    private static List<int> ReadValues(AdbcConnection connection, PartitionDescriptor descriptor)
    {
        using IArrowArrayStream stream = connection.ReadPartition(descriptor);
        var values = new List<int>();
        while (stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is RecordBatch batch)
        {
            using (batch)
            {
                var column = (Int32Array)batch.Column(batch.Schema.GetFieldIndex("v"));
                for (int i = 0; i < batch.Length; i++)
                {
                    values.Add(column.GetValue(i)!.Value);
                }
            }
        }
        return values;
    }

    [SkippableTheory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(8)]
    public void PartitionsCoverEveryRowOnce(int count)
    {
        using AdbcDatabase database = OpenDatabase();
        using AdbcConnection connection = database.Connect(null);
        PartitionedResult result = Partition(connection, Query, "k", count);

        Assert.Equal(count, result.PartitionDescriptors.Count);
        Assert.Equal(["k", "v", "s"], result.Schema.FieldsList.Select(f => f.Name));

        List<int> all = result.PartitionDescriptors.SelectMany(d => ReadValues(connection, d)).ToList();
        Assert.Equal(Enumerable.Range(1, 1003), all.OrderBy(v => v));
    }

    /// <summary>
    /// Incremental execution: every partition on the first call (they are all
    /// known at once), then the empty set that ends that execution; the next
    /// call starts another.
    /// </summary>
    [SkippableFact]
    public void IncrementalExecutionReturnsThePartitionsThenAnEmptySet()
    {
        using AdbcDatabase database = OpenDatabase();
        using AdbcConnection connection = database.Connect(null);
        using AdbcStatement statement = connection.CreateStatement();
        statement.SqlQuery = Query;
        statement.SetOption(SqlServerParameters.PartitionColumn, "k");
        statement.SetOption(SqlServerParameters.PartitionCount, "4");
        Assert.Equal("false", statement.GetOption(AdbcOptions.Statement.Incremental));
        statement.SetOption(AdbcOptions.Statement.Incremental, AdbcOptions.Enabled);
        Assert.Equal("true", statement.GetOption(AdbcOptions.Statement.Incremental));

        for (int execution = 0; execution < 2; execution++)
        {
            PartitionedResult first = statement.ExecutePartitioned();
            Assert.Equal(4, first.PartitionDescriptors.Count);
            Assert.Equal(Enumerable.Range(1, 1003), first.PartitionDescriptors.SelectMany(d => ReadValues(connection, d)).OrderBy(v => v));

            PartitionedResult done = statement.ExecutePartitioned();
            Assert.Empty(done.PartitionDescriptors);
            Assert.Equal(first.Schema.FieldsList.Select(f => f.Name), done.Schema.FieldsList.Select(f => f.Name));
        }

        // Without it, every call executes and returns the partitions.
        statement.SetOption(AdbcOptions.Statement.Incremental, AdbcOptions.Disabled);
        Assert.Equal(4, statement.ExecutePartitioned().PartitionDescriptors.Count);
        Assert.Equal(4, statement.ExecutePartitioned().PartitionDescriptors.Count);
    }

    /// <summary>Execution progress is not reported: the options are unknown.</summary>
    [SkippableFact]
    public void ProgressIsNotReported()
    {
        using AdbcDatabase database = OpenDatabase();
        using AdbcConnection connection = database.Connect(null);
        using AdbcStatement statement = connection.CreateStatement();
        Assert.Equal(AdbcStatusCode.NotFound, Assert.Throws<AdbcException>(() => statement.GetOption(AdbcOptions.Statement.Progress)).Status);
        Assert.Equal(AdbcStatusCode.NotFound, Assert.Throws<AdbcException>(() => statement.GetOptionDouble(AdbcOptions.Statement.MaxProgress)).Status);
    }

    [SkippableFact]
    public void PartitionsReadInParallelOnSeparateConnections()
    {
        using AdbcDatabase database = OpenDatabase();
        using AdbcConnection coordinator = database.Connect(null);
        PartitionedResult result = Partition(coordinator, Query, "K", 4);

        List<int>[] parts = result.PartitionDescriptors
            .AsParallel()
            .Select(descriptor =>
            {
                using AdbcConnection connection = database.Connect(null);
                return ReadValues(connection, descriptor);
            })
            .ToArray();

        Assert.All(parts, p => Assert.NotEmpty(p));
        Assert.Equal(Enumerable.Range(1, 1003), parts.SelectMany(p => p).OrderBy(v => v));
    }

    [SkippableFact]
    public void FewerPartitionsWhenRangeIsSmall()
    {
        using AdbcDatabase database = OpenDatabase();
        using AdbcConnection connection = database.Connect(null);
        PartitionedResult result = Partition(connection,
            "SELECT k, CAST(k AS int) AS v FROM (VALUES (CAST(1 AS tinyint)), (2), (3)) AS t(k)", "k", 10);
        Assert.Equal(3, result.PartitionDescriptors.Count);
        Assert.Equal([1, 2, 3], result.PartitionDescriptors.SelectMany(d => ReadValues(connection, d)).OrderBy(v => v));
    }

    [SkippableFact]
    public void WithoutColumnGivesSinglePartition()
    {
        using AdbcDatabase database = OpenDatabase();
        using AdbcConnection connection = database.Connect(null);
        PartitionedResult result = Partition(connection, Query, null, 4);
        Assert.Equal(1003, ReadValues(connection, Assert.Single(result.PartitionDescriptors)).Count);
    }

    [SkippableFact]
    public void EmptyResultGivesSinglePartition()
    {
        using AdbcDatabase database = OpenDatabase();
        using AdbcConnection connection = database.Connect(null);
        PartitionedResult result = Partition(connection, "SELECT k, v FROM (" + Query + ") AS x WHERE v < 0", "k", 4);
        Assert.Empty(ReadValues(connection, Assert.Single(result.PartitionDescriptors)));
        Assert.Equal(2, result.Schema.FieldsList.Count);
    }

    [SkippableFact]
    public void NonIntegerColumnIsNotImplemented()
    {
        using AdbcDatabase database = OpenDatabase();
        using AdbcConnection connection = database.Connect(null);
        var ex = Assert.ThrowsAny<AdbcException>(() => Partition(connection, Query, "s", 4));
        Assert.Equal(AdbcStatusCode.NotImplemented, ex.Status);
        var missing = Assert.ThrowsAny<AdbcException>(() => Partition(connection, Query, "nope", 4));
        Assert.Equal(AdbcStatusCode.InvalidArgument, missing.Status);
    }

    [SkippableFact]
    public void ForeignDescriptorIsRejected()
    {
        using AdbcDatabase database = OpenDatabase();
        using AdbcConnection connection = database.Connect(null);
        var ex = Assert.ThrowsAny<AdbcException>(() => connection.ReadPartition(new PartitionDescriptor([9, 1, 2])));
        Assert.Equal(AdbcStatusCode.InvalidArgument, ex.Status);
    }
}
