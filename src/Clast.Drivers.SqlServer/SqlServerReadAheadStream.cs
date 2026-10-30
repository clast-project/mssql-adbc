// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Clast.Drivers.SqlServer.Tds;

namespace Clast.Drivers.SqlServer;

/// <summary>
/// Decodes a serial result stream on a background thread, up to a number of
/// batches ahead of the caller, so that decoding the next batch overlaps the
/// caller's work on the last one. Batches arrive in order, and an error comes
/// after the batches decoded before it, as from the serial stream; a cancel
/// takes effect at once.
/// </summary>
internal sealed class SqlServerReadAheadStream : IArrowArrayStream, IBackgroundStream
{
    private readonly SqlServerConnection _connection;
    private readonly TdsSession _session;
    private readonly IArrowArrayStream _inner;
    private readonly Channel<RecordBatch> _channel;
    private readonly CancellationTokenSource _cancel = new();
    private readonly object _sync = new();
    private readonly Task _worker;
    private ExceptionDispatchInfo? _error;
    private volatile bool _cancelled;
    private bool _disposed;
    private bool _shutdown;
    private bool _ended;

    private SqlServerReadAheadStream(SqlServerConnection connection, TdsSession session, IArrowArrayStream inner, int depth)
    {
        _connection = connection;
        _session = session;
        _inner = inner;
        Schema = inner.Schema;
        _channel = Channel.CreateBounded<RecordBatch>(new BoundedChannelOptions(depth)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        connection.SetBackgroundStream(this);
        _worker = Task.Factory.StartNew(Work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    /// <summary>
    /// <paramref name="inner"/>, read <paramref name="depth"/> batches ahead
    /// on <paramref name="session"/>; as it is for a depth of 0, or a response
    /// with no result set.
    /// </summary>
    public static IArrowArrayStream Start(SqlServerConnection connection, TdsSession session, IArrowArrayStream inner, int depth) =>
        depth > 0 && inner is SqlServerQueryStream ? new SqlServerReadAheadStream(connection, session, inner, depth) : inner;

    public Schema Schema { get; }

    private void Work()
    {
        try
        {
            while (!_cancel.IsCancellationRequested &&
                   _inner.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is RecordBatch batch)
            {
                try
                {
                    if (!_channel.Writer.TryWrite(batch))
                    {
                        _channel.Writer.WriteAsync(batch, _cancel.Token).AsTask().GetAwaiter().GetResult();
                    }
                }
                catch
                {
                    batch.Dispose();
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _error = ExceptionDispatchInfo.Capture(ex);
        }
        finally
        {
            _channel.Writer.TryComplete();
        }
    }

    public async ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // As with a serial stream, a cancelled token cancels the query.
        using CancellationTokenRegistration registration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(static s => ((SqlServerReadAheadStream)s!).Cancel(), this)
            : default;

        ChannelReader<RecordBatch> reader = _channel.Reader;
        while (!_cancelled && await reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
        {
            if (!_cancelled && reader.TryRead(out RecordBatch? batch))
            {
                return batch;
            }
        }

        // Finished, failed or cancelled: release the session now rather than
        // waiting for Dispose. The error is reported once, then the stream is
        // at its end, as a serial one is.
        Shutdown();
        if (_ended)
        {
            return null;
        }
        _ended = true;
        if (_cancelled)
        {
            var cancelled = TdsSession.CancelledException();
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancelled.Message, cancelled, cancellationToken);
            }
            throw cancelled;
        }
        _error?.Throw();
        return null;
    }

    /// <summary>Cancels the query; the reader then throws <see cref="Apache.Arrow.Adbc.AdbcStatusCode.Cancelled"/>.</summary>
    public void Cancel()
    {
        lock (_sync)
        {
            if (_shutdown)
            {
                return;
            }
            _cancelled = true;
            _cancel.Cancel();
            _session.Cancel();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Shutdown();
    }

    private void Shutdown()
    {
        lock (_sync)
        {
            if (_shutdown)
            {
                return;
            }
            _shutdown = true;
            if (!_worker.IsCompleted)
            {
                // Stop the server sending rows that will not be read.
                _cancel.Cancel();
                _session.Cancel();
            }
        }

        while (_channel.Reader.TryRead(out RecordBatch? batch))
        {
            batch.Dispose();
        }
        _worker.Wait();
        while (_channel.Reader.TryRead(out RecordBatch? batch))
        {
            batch.Dispose();
        }
        _cancel.Dispose();

        // Drains whatever the worker left unread, and leaves the session idle.
        _inner.Dispose();
        _connection.SetBackgroundStream(null);
    }
}

/// <summary>A result stream that reads on the connection's session from a background thread.</summary>
internal interface IBackgroundStream
{
    /// <summary>Cancels the query; safe to call on any thread.</summary>
    void Cancel();
}
