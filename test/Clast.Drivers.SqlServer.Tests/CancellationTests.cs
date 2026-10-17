// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Xunit;

namespace Clast.Drivers.SqlServer.Tests;

public class CancellationTests
{
    // Billions of rows: never read to the end.
    private const string EndlessQuery =
        "SELECT a.object_id AS a, b.object_id AS b, c.object_id AS c FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c";

    // 64 rows of 20 MB each, keyed by k; the key range is cheap to plan.
    private const string WideRowQuery = """
        SELECT t.k, REPLICATE(CAST(N'x' AS nvarchar(max)), 10000000) AS s
        FROM (SELECT TOP (64) CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS bigint) AS k FROM sys.all_objects) AS t
        """;

    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(10);

    private static AdbcConnection Connect(Action<Dictionary<string, string>>? configure = null)
    {
        SqlServerTestConfiguration config = SqlServerTestConfiguration.Load();
        var options = new Dictionary<string, string>(config.Options) { [SqlServerParameters.BatchRows] = "1000" };
        configure?.Invoke(options);
        return new SqlServerDriver().Open(options).Connect(null);
    }

    private static void CancelAfter(Action cancel, int milliseconds) =>
        Task.Delay(milliseconds).ContinueWith(_ => cancel(), TaskScheduler.Default);

    private static void AssertUsable(AdbcConnection connection)
    {
        using AdbcStatement statement = connection.CreateStatement();
        statement.SqlQuery = "SELECT 42 AS answer";
        using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
        using RecordBatch batch = stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult()!;
        Assert.Equal(42, ((Int32Array)batch.Column(0)).GetValue(0));
        Assert.Null(stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult());
    }

    private static void AssertCancelled(Action action)
    {
        var watch = Stopwatch.StartNew();
        AdbcException ex = Assert.ThrowsAny<AdbcException>(action);
        Assert.Equal(AdbcStatusCode.Cancelled, ex.Status);
        Assert.True(watch.Elapsed < Prompt, $"Cancellation took {watch.Elapsed}");
    }

    [SkippableTheory]
    [InlineData(null)]
    [InlineData(@"np:\\.\pipe\sql\query")]
    public void CancelsRunningUpdate(string? server)
    {
        Skip.If(server != null && !OperatingSystem.IsWindows(), "Named pipes are Windows-only");
        using AdbcConnection connection = Connect(o =>
        {
            if (server != null)
            {
                o[SqlServerParameters.Server] = server;
            }
        });
        using AdbcStatement statement = connection.CreateStatement();
        statement.SqlQuery = "WAITFOR DELAY '00:01:00'";
        CancelAfter(statement.Cancel, 300);
        AssertCancelled(() => statement.ExecuteUpdate());
        AssertUsable(connection);
    }

    [SkippableFact]
    public void TokenCancelsQueryBeforeFirstResult()
    {
        using AdbcConnection connection = Connect();
        using AdbcStatement statement = connection.CreateStatement();
        statement.SqlQuery = "WAITFOR DELAY '00:01:00'; SELECT 1 AS x";
        using var cts = new CancellationTokenSource(300);
        var watch = Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() => statement.ExecuteQueryAsync(cts.Token).AsTask().GetAwaiter().GetResult());
        Assert.True(watch.Elapsed < Prompt);
        AssertUsable(connection);
    }

    [SkippableFact]
    public void CancelStopsStreamBeingRead()
    {
        using AdbcConnection connection = Connect();
        using AdbcStatement statement = connection.CreateStatement();
        statement.SqlQuery = EndlessQuery;
        using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
        CancelAfter(connection.Cancel, 300);
        AssertCancelled(() =>
        {
            while (stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is RecordBatch batch)
            {
                batch.Dispose();
            }
        });
        Assert.Null(stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult());
        AssertUsable(connection);
    }

    [SkippableFact]
    public void CancelBetweenReadsFailsNextRead()
    {
        using AdbcConnection connection = Connect();
        using AdbcStatement statement = connection.CreateStatement();
        statement.SqlQuery = EndlessQuery;
        using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
        stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult()!.Dispose();
        statement.Cancel();
        AssertCancelled(() => stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult());
        AssertUsable(connection);
    }

    [SkippableFact]
    public void TokenCancelsRead()
    {
        using AdbcConnection connection = Connect();
        using AdbcStatement statement = connection.CreateStatement();
        statement.SqlQuery = EndlessQuery;
        using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
        using var cts = new CancellationTokenSource(300);
        var watch = Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() =>
        {
            while (stream.ReadNextRecordBatchAsync(cts.Token).AsTask().GetAwaiter().GetResult() is RecordBatch batch)
            {
                batch.Dispose();
            }
        });
        Assert.True(watch.Elapsed < Prompt);
        AssertUsable(connection);
    }

    [SkippableFact]
    public void DisposingUnfinishedStreamIsPrompt()
    {
        using AdbcConnection connection = Connect();
        var watch = Stopwatch.StartNew();
        using (AdbcStatement statement = connection.CreateStatement())
        {
            statement.SqlQuery = EndlessQuery;
            using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
            stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult()!.Dispose();
        }
        Assert.True(watch.Elapsed < Prompt, $"Dispose took {watch.Elapsed}");
        AssertUsable(connection);
    }

    [SkippableFact]
    public void CancelWhenIdleDoesNothing()
    {
        using AdbcConnection connection = Connect();
        connection.Cancel();
        AssertUsable(connection);
        connection.Cancel();
        AssertUsable(connection);
    }

    [SkippableTheory]
    [InlineData(null, null)]
    [InlineData(null, "optional")]
    [InlineData(@"np:\\.\pipe\sql\query", null)]
    public void CancelRacingShortQueriesKeepsSessionInStep(string? server, string? encrypt)
    {
        Skip.If(server != null && !OperatingSystem.IsWindows(), "Named pipes are Windows-only");
        using AdbcConnection connection = Connect(o =>
        {
            if (server != null)
            {
                o[SqlServerParameters.Server] = server;
            }
            if (encrypt != null)
            {
                o[SqlServerParameters.Encrypt] = encrypt;
            }
        });
        using var stop = new CancellationTokenSource();
        Task canceller = Task.Run(() =>
        {
            var random = new Random(1);
            while (!stop.IsCancellationRequested)
            {
                connection.Cancel();
                Thread.SpinWait(random.Next(20_000));
            }
        });

        int completed = 0, cancelled = 0;
        try
        {
            for (int i = 0; i < 300; i++)
            {
                try
                {
                    using AdbcStatement statement = connection.CreateStatement();
                    statement.SqlQuery = $"SELECT {i} AS i, CAST(REPLICATE('a', {i}) AS varchar(max)) AS s";
                    using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
                    int rows = 0;
                    while (stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is RecordBatch batch)
                    {
                        using (batch)
                        {
                            // Never a stale response from an earlier query.
                            Assert.Equal(i, ((Int32Array)batch.Column(0)).GetValue(0));
                            rows += batch.Length;
                        }
                    }
                    Assert.Equal(1, rows);
                    completed++;
                }
                catch (AdbcException ex) when (ex.Status == AdbcStatusCode.Cancelled)
                {
                    cancelled++;
                }
            }
        }
        finally
        {
            stop.Cancel();
            canceller.GetAwaiter().GetResult();
        }

        Assert.Equal(300, completed + cancelled);
        Assert.True(cancelled > 0, "The canceller never hit a query");
        AssertUsable(connection);
    }

    [SkippableFact]
    public void CancelsBulkIngest()
    {
        using AdbcConnection connection = Connect();
        var data = new EndlessStream(onBatch: n =>
        {
            if (n == 5)
            {
                connection.Cancel();
            }
        });
        using (AdbcStatement ingest = connection.BulkIngest(null, null, "cancel_ingest", BulkIngestMode.Replace, isTemporary: true))
        {
            ingest.BindStream(data);
            AssertCancelled(() => ingest.ExecuteUpdate());
        }

        // The bulk-load message was discarded, so no rows were inserted.
        using AdbcStatement count = connection.CreateStatement();
        count.SqlQuery = "SELECT COUNT(*) FROM #cancel_ingest";
        using IArrowArrayStream stream = count.ExecuteQuery().Stream!;
        using RecordBatch batch = stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult()!;
        Assert.Equal(0, ((Int32Array)batch.Column(0)).GetValue(0));
    }

    [SkippableFact]
    public void CancelsParallelStream()
    {
        using AdbcConnection connection = Connect(o => o[SqlServerParameters.BatchRows] = "1");
        for (int round = 0; round < 2; round++)
        {
            using AdbcStatement statement = connection.CreateStatement();
            statement.SqlQuery = WideRowQuery;
            statement.SetOption(SqlServerParameters.PartitionColumn, "k");
            statement.SetOption(SqlServerParameters.ParallelReads, "4");
            using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
            Assert.IsType<SqlServerParallelQueryStream>(stream);
            stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult()!.Dispose();

            if (round == 0)
            {
                CancelAfter(statement.Cancel, 100);
                AssertCancelled(() =>
                {
                    while (stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is RecordBatch batch)
                    {
                        batch.Dispose();
                    }
                });
            }
            else
            {
                using var cts = new CancellationTokenSource(100);
                Assert.ThrowsAny<OperationCanceledException>(() =>
                {
                    while (stream.ReadNextRecordBatchAsync(cts.Token).AsTask().GetAwaiter().GetResult() is RecordBatch batch)
                    {
                        batch.Dispose();
                    }
                });
            }
            AssertUsable(connection);
        }
    }

    [SkippableFact]
    public void DisposingUnfinishedParallelStreamIsPrompt()
    {
        using AdbcConnection connection = Connect(o => o[SqlServerParameters.BatchRows] = "1");
        var watch = Stopwatch.StartNew();
        using (AdbcStatement statement = connection.CreateStatement())
        {
            statement.SqlQuery = WideRowQuery;
            statement.SetOption(SqlServerParameters.PartitionColumn, "k");
            statement.SetOption(SqlServerParameters.ParallelReads, "4");
            using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
            stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult()!.Dispose();
        }
        Assert.True(watch.Elapsed < Prompt, $"Dispose took {watch.Elapsed}");
        AssertUsable(connection);
    }

    /// <summary>Batches of 1000 int64 values without end; calls <c>onBatch</c> with each batch's number.</summary>
    private sealed class EndlessStream(Action<int> onBatch) : IArrowArrayStream
    {
        private int _batches;

        public Schema Schema { get; } = new Schema([new Field("v", Int64Type.Default, nullable: false)], null);

        public ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
        {
            onBatch(++_batches);
            var values = new Int64Array.Builder();
            for (int i = 0; i < 1000; i++)
            {
                values.Append(i);
            }
            return new ValueTask<RecordBatch?>(new RecordBatch(Schema, [values.Build()], 1000));
        }

        public void Dispose()
        {
        }
    }
}
