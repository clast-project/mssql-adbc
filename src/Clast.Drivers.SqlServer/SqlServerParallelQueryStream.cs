// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Clast.Drivers.SqlServer.Tds;

namespace Clast.Drivers.SqlServer;

/// <summary>
/// One result stream fed by several sessions: the query is split into
/// partitions, worker threads read them concurrently on separate sessions,
/// and their record batches are merged in arrival order (not query order).
/// </summary>
internal sealed class SqlServerParallelQueryStream : IArrowArrayStream
{
    private readonly SqlServerConnection _connection;
    private readonly Channel<RecordBatch> _channel;
    private readonly CancellationTokenSource _cancel = new();
    private readonly ConcurrentQueue<PartitionDescriptor> _work;
    private readonly Task[] _workers;
    private readonly TdsSession[] _sessions;
    private ExceptionDispatchInfo? _error;
    private int _running;
    private bool _disposed;
    private bool _shutdown;

    private SqlServerParallelQueryStream(SqlServerConnection connection, PartitionedResult partitions, TdsSession[] sessions)
    {
        _connection = connection;
        Schema = partitions.Schema;
        _work = new ConcurrentQueue<PartitionDescriptor>(partitions.PartitionDescriptors);
        _sessions = sessions;
        // Bounded so that fast partitions cannot buffer unboundedly ahead of the reader.
        _channel = Channel.CreateBounded<RecordBatch>(new BoundedChannelOptions(sessions.Length * 2)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _running = sessions.Length;
        connection.SetParallelStream(this);
        _workers = new Task[sessions.Length];
        for (int i = 0; i < sessions.Length; i++)
        {
            TdsSession session = sessions[i];
            _workers[i] = Task.Factory.StartNew(() => Work(session), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
    }

    private const int PartitionsPerWorker = 4;

    public Schema Schema { get; }

    /// <summary>
    /// Plans the partitions on the connection's own session and starts
    /// <paramref name="parallelism"/> workers, the first on that session and
    /// the rest on helper sessions from the connection's pool.
    /// </summary>
    public static IArrowArrayStream Start(SqlServerConnection connection, string sql, string column, int parallelism, int partitionCount)
    {
        // Several partitions per worker, so a worker on a slow session takes fewer of them.
        PartitionedResult partitions = SqlServerPartitions.Execute(connection.Session, sql, column, Math.Max(partitionCount, parallelism * PartitionsPerWorker));
        int workers = Math.Min(parallelism, partitions.PartitionDescriptors.Count);
        if (workers <= 1)
        {
            return connection.ExecuteQueryStream(SqlServerPartitions.Decode(partitions.PartitionDescriptors[0]));
        }

        var sessions = new TdsSession[workers];
        sessions[0] = connection.Session;
        try
        {
            TdsSession[] helpers = connection.RentHelperSessions(workers - 1);
            helpers.CopyTo(sessions, 1);
        }
        catch
        {
            sessions[0] = null!;
            throw;
        }
        return new SqlServerParallelQueryStream(connection, partitions, sessions);
    }

    private void Work(TdsSession session)
    {
        try
        {
            while (!_cancel.IsCancellationRequested && _work.TryDequeue(out PartitionDescriptor descriptor))
            {
                using IArrowArrayStream stream = SqlServerQueryStream.Execute(session, SqlServerPartitions.Decode(descriptor));
                while (!_cancel.IsCancellationRequested &&
                       stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is RecordBatch batch)
                {
                    // Re-home the arrays under the merged schema; the arrays move, nothing is copied.
                    var merged = new RecordBatch(Schema, Enumerable.Range(0, batch.ColumnCount).Select(batch.Column), batch.Length);
                    try
                    {
                        if (!_channel.Writer.TryWrite(merged))
                        {
                            _channel.Writer.WriteAsync(merged, _cancel.Token).AsTask().GetAwaiter().GetResult();
                        }
                    }
                    catch
                    {
                        merged.Dispose();
                        throw;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (Interlocked.CompareExchange(ref _error, ExceptionDispatchInfo.Capture(ex), null) == null)
            {
                _cancel.Cancel();
            }
        }
        finally
        {
            if (Interlocked.Decrement(ref _running) == 0)
            {
                _channel.Writer.TryComplete();
            }
        }
    }

    public async ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ChannelReader<RecordBatch> reader = _channel.Reader;
        while (_error == null && await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_error == null && reader.TryRead(out RecordBatch? batch))
            {
                return batch;
            }
        }

        // Finished or failed: release the sessions now rather than waiting for Dispose.
        Shutdown();
        _error?.Throw();
        return null;
    }

    public void Dispose()
    {
        _disposed = true;
        Shutdown();
    }

    private void Shutdown()
    {
        if (_shutdown)
        {
            return;
        }
        _shutdown = true;

        // Stop the workers; a worker's partition stream drains whatever it has not read.
        _cancel.Cancel();
        while (_channel.Reader.TryRead(out RecordBatch? batch))
        {
            batch.Dispose();
        }
        try
        {
            Task.WaitAll(_workers);
        }
        catch (AggregateException)
        {
            // Worker failures are recorded in _error.
        }
        while (_channel.Reader.TryRead(out RecordBatch? batch))
        {
            batch.Dispose();
        }
        _cancel.Dispose();

        _connection.ReturnHelperSessions(_sessions.AsSpan(1));
        _connection.SetParallelStream(null);
    }
}
