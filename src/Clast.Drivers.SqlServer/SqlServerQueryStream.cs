// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Clast.Drivers.SqlServer.Arrow;
using Clast.Drivers.SqlServer.Tds;

namespace Clast.Drivers.SqlServer;

/// <summary>
/// Streams the first result set of a response as Arrow record batches. Rows
/// are decoded straight from the packet buffer into per-column Arrow buffers.
/// Later result sets in the same response are skipped.
/// </summary>
internal sealed class SqlServerQueryStream : IArrowArrayStream
{
    private readonly TdsSession _session;
    private readonly TdsColumn[] _columns;
    private readonly ColumnDecoder[] _decoders;
    private readonly int _batchRows;
    private readonly byte[] _nullBitmap;
    private bool _resultComplete;
    private bool _finished;

    public SqlServerQueryStream(TdsSession session, TdsColumn[] columns)
    {
        _session = session;
        _columns = columns;
        _batchRows = session.Settings.BatchRows;
        _decoders = new ColumnDecoder[columns.Length];
        var fields = new List<Field>(columns.Length);
        for (int i = 0; i < columns.Length; i++)
        {
            _decoders[i] = DecoderFactory.Create(columns[i], _batchRows, session.DefaultCollation);
            fields.Add(DecoderFactory.CreateField(_decoders[i]));
        }
        Schema = new Schema(fields, null);
        _nullBitmap = new byte[(columns.Length + 7) >> 3];
        session.ActiveStream = this;
    }

    /// <summary>Sends a batch on <paramref name="session"/> and returns a stream over its first result set.</summary>
    public static IArrowArrayStream Execute(TdsSession session, string sql)
    {
        session.SendSqlBatch(sql);

        TdsColumn[]? columns = session.ReadUntilResultSet();
        if (columns == null)
        {
            return Empty();
        }

        try
        {
            return new SqlServerQueryStream(session, columns);
        }
        catch
        {
            // Unsupported column type: leave the session usable.
            session.ActiveStream = null;
            session.DrainResponse(columns);
            throw;
        }
    }

    /// <summary>A stream for a response that produced no result set.</summary>
    public static IArrowArrayStream Empty() => new EmptyStream();

    public Schema Schema { get; }

    public ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            return new ValueTask<RecordBatch?>(ReadNextRecordBatch());
        }

        // The read blocks in the transport, so a cancelled token (even one
        // cancelled already) cancels the query rather than just this call.
        try
        {
            using (cancellationToken.Register(static s => ((TdsSession)s!).Cancel(), _session))
            {
                return new ValueTask<RecordBatch?>(ReadNextRecordBatch());
            }
        }
        catch (AdbcException ex) when (ex.Status == AdbcStatusCode.Cancelled && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(ex.Message, ex, cancellationToken);
        }
    }

    private RecordBatch? ReadNextRecordBatch()
    {
        if (_finished)
        {
            return null;
        }
        if (_resultComplete || _session.IsCancelRequested)
        {
            // A cancelled response is skipped to the attention acknowledgment, which throws.
            Finish();
            return null;
        }

        PacketReader reader = _session.Reader;
        ColumnDecoder[] decoders = _decoders;
        int rows = 0;
        try
        {
            while (rows < _batchRows)
            {
                TokenType token = (TokenType)reader.ReadByte();
                if (token == TokenType.Row)
                {
                    for (int i = 0; i < decoders.Length; i++)
                    {
                        decoders[i].ReadValue(reader);
                    }
                    rows++;
                }
                else if (token == TokenType.NbcRow)
                {
                    reader.ReadBytes(_nullBitmap);
                    for (int i = 0; i < decoders.Length; i++)
                    {
                        if ((_nullBitmap[i >> 3] & (1 << (i & 7))) != 0)
                        {
                            decoders[i].AppendNull();
                        }
                        else
                        {
                            decoders[i].ReadValue(reader);
                        }
                    }
                    rows++;
                }
                else if (token is TokenType.Done or TokenType.DoneInProc or TokenType.DoneProc)
                {
                    _session.ReadDone(out _, out _, out _);
                    _resultComplete = true;
                    break;
                }
                else if (!_session.TryReadCommonToken(token))
                {
                    throw new TdsProtocolException($"Unexpected token 0x{(byte)token:X2} in a result set");
                }
            }
        }
        catch (TdsProtocolException)
        {
            _session.MarkBroken();
            _finished = true;
            _session.ActiveStream = null;
            throw;
        }

        if (_resultComplete)
        {
            // Read the rest of the response now so that errors from the
            // result set (or later statements) surface before the last batch.
            Finish();
            if (rows == 0)
            {
                return null;
            }
        }

        var arrays = new IArrowArray[decoders.Length];
        for (int i = 0; i < decoders.Length; i++)
        {
            arrays[i] = decoders[i].Build();
        }
        return new RecordBatch(Schema, arrays, rows);
    }

    private void Finish()
    {
        if (_finished)
        {
            return;
        }
        _finished = true;
        _session.ActiveStream = null;
        _session.DrainResponse(_resultComplete ? null : _columns);
    }

    public void Dispose()
    {
        foreach (ColumnDecoder decoder in _decoders)
        {
            decoder.Dispose();
        }

        if (!_finished && !_session.IsBroken)
        {
            _finished = true;
            _session.ActiveStream = null;
            if (!_resultComplete)
            {
                // Stop the server sending rows that will not be read.
                _session.Cancel();
            }
            try
            {
                _session.DrainResponse(_resultComplete ? null : _columns);
            }
            catch (AdbcException ex) when (ex is SqlServerException || ex.Status == AdbcStatusCode.Cancelled)
            {
                // The caller abandoned the stream; errors in the unread part are not reported.
            }
        }
    }

    private sealed class EmptyStream : IArrowArrayStream
    {
        public Schema Schema { get; } = new Schema([], null);

        public ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default) =>
            new ValueTask<RecordBatch?>((RecordBatch?)null);

        public void Dispose()
        {
        }
    }
}
