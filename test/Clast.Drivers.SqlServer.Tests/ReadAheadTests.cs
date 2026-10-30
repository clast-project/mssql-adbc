// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Xunit;

namespace Clast.Drivers.SqlServer.Tests;

/// <summary><c>adbc.mssql.read_ahead</c>: serial results decoded on a background thread.</summary>
public sealed class ReadAheadTests
{
    // Billions of rows: never read to the end.
    private const string EndlessQuery =
        "SELECT a.object_id AS a, b.object_id AS b, c.object_id AS c FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c";

    private const string Numbers = "SELECT TOP (10000) CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS int) AS n FROM sys.all_objects a CROSS JOIN sys.all_objects b ORDER BY n";

    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(10);

    private static AdbcConnection Connect(string readAhead = "2") =>
        new SqlServerDriver().Open(new Dictionary<string, string>(SqlServerTestConfiguration.Load().Options)
        {
            [SqlServerParameters.BatchRows] = "1000",
            [SqlServerParameters.ReadAhead] = readAhead,
        }).Connect(null);

    private static IArrowArrayStream Execute(AdbcConnection connection, string sql)
    {
        using AdbcStatement statement = connection.CreateStatement();
        statement.SqlQuery = sql;
        return statement.ExecuteQuery().Stream!;
    }

    private static RecordBatch? Next(IArrowArrayStream stream, CancellationToken cancellationToken = default) =>
        stream.ReadNextRecordBatchAsync(cancellationToken).AsTask().GetAwaiter().GetResult();

    private static List<int> ReadNumbers(IArrowArrayStream stream)
    {
        var values = new List<int>();
        while (Next(stream) is RecordBatch batch)
        {
            using (batch)
            {
                Int32Array n = (Int32Array)batch.Column(0);
                for (int i = 0; i < n.Length; i++)
                {
                    values.Add(n.GetValue(i)!.Value);
                }
            }
        }
        return values;
    }

    private static void AssertUsable(AdbcConnection connection)
    {
        using IArrowArrayStream stream = Execute(connection, "SELECT 42 AS answer");
        using RecordBatch batch = Next(stream)!;
        Assert.Equal(42, ((Int32Array)batch.Column(0)).GetValue(0));
        Assert.Null(Next(stream));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("17")]
    [InlineData("two")]
    public void RejectsAnInvalidDepth(string value)
    {
        var options = new Dictionary<string, string> { [SqlServerParameters.Server] = "localhost", [SqlServerParameters.ReadAhead] = value };
        Assert.Equal(AdbcStatusCode.InvalidArgument, Assert.ThrowsAny<AdbcException>(() => new SqlServerDriver().Open(options).Connect(null)).Status);
    }

    [SkippableFact]
    public void ReadsEveryRowInOrder()
    {
        using AdbcConnection connection = Connect();
        using (IArrowArrayStream stream = Execute(connection, Numbers))
        {
            Assert.IsType<SqlServerReadAheadStream>(stream);
            Assert.Equal(Enumerable.Range(1, 10000), ReadNumbers(stream));
            Assert.Null(Next(stream));
        }
        AssertUsable(connection);

        // A statement can turn it off, and the connection's depth is its default.
        using AdbcStatement statement = connection.CreateStatement();
        Assert.Equal(2, statement.GetOptionInt64(SqlServerParameters.ReadAhead));
        statement.SetOption(SqlServerParameters.ReadAhead, "0");
        statement.SqlQuery = Numbers;
        using IArrowArrayStream serial = statement.ExecuteQuery().Stream!;
        Assert.IsType<SqlServerQueryStream>(serial);
        Assert.Equal(Enumerable.Range(1, 10000), ReadNumbers(serial));
    }

    [SkippableFact]
    public void BoundParametersReadAhead()
    {
        using AdbcConnection connection = Connect("1");
        using AdbcStatement statement = connection.CreateStatement();
        statement.SqlQuery = "SELECT TOP (@count) CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS int) AS n FROM sys.all_objects a CROSS JOIN sys.all_objects b ORDER BY n";
        var schema = new Schema([new Field("count", Int32Type.Default, false)], null);
        statement.Bind(new RecordBatch(schema, [new Int32Array.Builder().Append(2500).Append(0).Append(1500).Build()], 3), schema);
        using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
        Assert.IsType<SqlServerReadAheadStream>(stream);
        Assert.Equal(Enumerable.Range(1, 2500).Concat(Enumerable.Range(1, 1500)), ReadNumbers(stream));
    }

    [SkippableFact]
    public void AResponseWithoutRowsNeedsNoThread()
    {
        using AdbcConnection connection = Connect();
        using IArrowArrayStream stream = Execute(connection, "DECLARE @x int = 1");
        Assert.IsNotType<SqlServerReadAheadStream>(stream);
        Assert.Null(Next(stream));
    }

    /// <summary>Batches decoded before an error arrive first, as from a serial stream; then the error, once.</summary>
    [SkippableFact]
    public void AnErrorFollowsTheBatchesBeforeIt()
    {
        using AdbcConnection connection = Connect();
        using IArrowArrayStream stream = Execute(connection,
            "SELECT TOP (5000) 1 / (5000 - CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS int)) AS n FROM sys.all_objects a CROSS JOIN sys.all_objects b");
        int rows = 0;
        SqlServerException ex = Assert.Throws<SqlServerException>(() =>
        {
            while (Next(stream) is RecordBatch batch)
            {
                rows += batch.Length;
                batch.Dispose();
            }
        });
        Assert.Equal(8134, ex.Number); // divide by zero
        Assert.True(rows >= 1000, $"Only {rows} rows arrived before the error");
        Assert.Null(Next(stream));
        AssertUsable(connection);
    }

    [SkippableFact]
    public void TheConnectionIsBusyUntilTheStreamEnds()
    {
        using AdbcConnection connection = Connect();
        using (IArrowArrayStream stream = Execute(connection, "SELECT 1 AS x"))
        {
            // The worker may already have read the whole response, but the stream is still open.
            Assert.Equal(AdbcStatusCode.InvalidState, Assert.ThrowsAny<AdbcException>(() => Execute(connection, "SELECT 2 AS y")).Status);
            using (RecordBatch batch = Next(stream)!)
            {
                Assert.Equal(1, batch.Length);
            }
            Assert.Null(Next(stream));
            AssertUsable(connection);
        }
        AssertUsable(connection);
    }

    [SkippableFact]
    public void DisposingAnUnfinishedStreamIsPrompt()
    {
        using AdbcConnection connection = Connect();
        var watch = Stopwatch.StartNew();
        using (IArrowArrayStream stream = Execute(connection, EndlessQuery))
        {
            Next(stream)!.Dispose();
        }
        Assert.True(watch.Elapsed < Prompt, $"Dispose took {watch.Elapsed}");
        AssertUsable(connection);
    }

    [SkippableFact]
    public void CancelStopsTheStream()
    {
        using AdbcConnection connection = Connect();
        for (int round = 0; round < 3; round++)
        {
            using AdbcStatement statement = connection.CreateStatement();
            statement.SqlQuery = EndlessQuery;
            using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
            Next(stream)!.Dispose();
            var watch = Stopwatch.StartNew();
            switch (round)
            {
                case 0:
                    // Between reads: the next read fails, whatever is queued.
                    statement.Cancel();
                    Assert.Equal(AdbcStatusCode.Cancelled, Assert.ThrowsAny<AdbcException>(() => Next(stream)).Status);
                    break;
                case 1:
                    _ = Task.Delay(200).ContinueWith(_ => connection.Cancel(), TaskScheduler.Default);
                    Assert.Equal(AdbcStatusCode.Cancelled, Assert.ThrowsAny<AdbcException>(() =>
                    {
                        while (Next(stream) is RecordBatch batch)
                        {
                            batch.Dispose();
                        }
                    }).Status);
                    break;
                default:
                    using (var cts = new CancellationTokenSource(200))
                    {
                        Assert.ThrowsAny<OperationCanceledException>(() =>
                        {
                            while (Next(stream, cts.Token) is RecordBatch batch)
                            {
                                batch.Dispose();
                            }
                        });
                    }
                    break;
            }
            Assert.True(watch.Elapsed < Prompt, $"Cancellation took {watch.Elapsed}");
            Assert.Null(Next(stream));
            AssertUsable(connection);
        }
    }
}
