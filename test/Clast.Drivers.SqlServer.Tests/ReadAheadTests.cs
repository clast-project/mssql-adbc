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

    private static AdbcConnection Connect(string readAhead = "2", string batchRows = "1000") =>
        new SqlServerDriver().Open(new Dictionary<string, string>(SqlServerTestConfiguration.Load().Options)
        {
            [SqlServerParameters.BatchRows] = batchRows,
            [SqlServerParameters.ReadAhead] = readAhead,
        }).Connect(null);

    private static readonly Schema DSchema = new([new Field("d", Int32Type.Default, false)], null);

    private static RecordBatch D(int value) => new(DSchema, [new Int32Array.Builder().Append(value).Build()], 1);

    /// <summary>Bound parameters, one row per batch, made on demand; counts the reads.</summary>
    private sealed class ParameterStream(Func<int, CancellationToken, RecordBatch?> next) : IArrowArrayStream
    {
        private int _reads;

        public ParameterStream(Func<int, RecordBatch?> next)
            : this((n, _) => next(n))
        {
        }

        public int Reads => Volatile.Read(ref _reads);

        public Schema Schema => DSchema;

        public ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default) =>
            new(next(Interlocked.Increment(ref _reads), cancellationToken));

        public void Dispose()
        {
        }
    }

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

    /// <summary>
    /// No more than the depth is decoded ahead: with batches of one row, one
    /// execution per batch, the worker asks for the next parameter row only
    /// once the caller has taken a batch.
    /// </summary>
    [SkippableFact]
    public void DecodesNoMoreThanTheDepthAhead()
    {
        using AdbcConnection connection = Connect("1", batchRows: "1");
        using AdbcStatement statement = connection.CreateStatement();
        statement.SqlQuery = "SELECT @d AS x";
        var parameters = new ParameterStream(n => n <= 5 ? D(n) : null);
        statement.BindStream(parameters);
        using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;

        // Wait for the worker to get as far as it should, then check it goes no further.
        Thread.Sleep(300);
        Assert.Equal(1, parameters.Reads);
        Next(stream)!.Dispose();
        Assert.True(SpinWait.SpinUntil(() => parameters.Reads >= 2, Prompt));
        Thread.Sleep(300);
        Assert.Equal(2, parameters.Reads);
        Assert.Equal([2, 3, 4, 5], ReadNumbers(stream));
    }

    /// <summary>
    /// A cancel while the worker waits for the next parameter row, with no
    /// request in flight, still cancels the execution it then sends.
    /// </summary>
    [SkippableFact]
    public void CancelBetweenParameterRowsCancelsTheNextExecution()
    {
        using AdbcConnection connection = Connect(batchRows: "1");
        using var release = new ManualResetEventSlim();
        var parameters = new ParameterStream(n =>
        {
            if (n == 2)
            {
                release.Wait();
            }
            return n <= 2 ? D(n - 1) : null;
        });
        using AdbcStatement statement = connection.CreateStatement();
        statement.SqlQuery = "IF @d = 0 SELECT 1 AS x ELSE BEGIN WAITFOR DELAY '00:01:00'; SELECT 2 AS x END";
        statement.BindStream(parameters);
        using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
        Next(stream)!.Dispose();
        SpinWait.SpinUntil(() => parameters.Reads == 2, Prompt);

        statement.Cancel();
        _ = Task.Delay(300).ContinueWith(_ => release.Set(), TaskScheduler.Default);
        var watch = Stopwatch.StartNew();
        Assert.Equal(AdbcStatusCode.Cancelled, Assert.ThrowsAny<AdbcException>(() => Next(stream)).Status);
        Assert.True(watch.Elapsed < Prompt, $"Cancellation took {watch.Elapsed}");
        Assert.Null(Next(stream));
        AssertUsable(connection);
    }

    /// <summary>
    /// A worker waiting for the next parameter row, in a read that honours
    /// its token, is stopped by Dispose rather than waited for.
    /// </summary>
    [SkippableFact]
    public void DisposeStopsAReadOfTheParameterRows()
    {
        using AdbcConnection connection = Connect(batchRows: "1");
        var parameters = new ParameterStream((n, token) =>
        {
            if (n == 2)
            {
                token.WaitHandle.WaitOne();
                token.ThrowIfCancellationRequested();
            }
            return n <= 2 ? D(n) : null;
        });
        var watch = Stopwatch.StartNew();
        using (AdbcStatement statement = connection.CreateStatement())
        {
            statement.SqlQuery = "SELECT @d AS x";
            statement.BindStream(parameters);
            using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
            Next(stream)!.Dispose();
            Assert.True(SpinWait.SpinUntil(() => parameters.Reads == 2, Prompt));
        }
        Assert.True(watch.Elapsed < Prompt, $"Dispose took {watch.Elapsed}");
        AssertUsable(connection);
    }

    /// <summary>
    /// Before ExecuteQuery returns, executions for rows without a result set
    /// run one after another; a cancel between them still stops the next.
    /// </summary>
    [SkippableTheory]
    [InlineData("0")]
    [InlineData("2")]
    public void CancelBeforeTheFirstResultSetCancelsTheNextExecution(string readAhead)
    {
        using AdbcConnection connection = Connect(readAhead);
        using var release = new ManualResetEventSlim();
        var parameters = new ParameterStream(n =>
        {
            if (n == 2)
            {
                release.Wait();
            }
            return n <= 2 ? D(n - 1) : null;
        });
        using AdbcStatement statement = connection.CreateStatement();
        statement.SqlQuery = "IF @d = 0 SET NOCOUNT ON ELSE BEGIN WAITFOR DELAY '00:01:00'; SELECT 2 AS x END";
        statement.BindStream(parameters);
        _ = Task.Run(() =>
        {
            SpinWait.SpinUntil(() => parameters.Reads == 2, Prompt);
            statement.Cancel();
            release.Set();
        });
        var watch = Stopwatch.StartNew();
        Assert.Equal(AdbcStatusCode.Cancelled, Assert.ThrowsAny<AdbcException>(() => statement.ExecuteQuery()).Status);
        Assert.True(watch.Elapsed < Prompt, $"Cancellation took {watch.Elapsed}");
        AssertUsable(connection);
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
