// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text;
using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Clast.Drivers.SqlServer.Arrow;
using Clast.Drivers.SqlServer.Tds;

namespace Clast.Drivers.SqlServer;

/// <summary>
/// Parameterized execution: each row of the bound data is one call to
/// <c>sp_executesql</c>, with the Arrow columns as typed parameters. Field
/// <c>name</c> binds to <c>@name</c>; unnamed fields bind to <c>@p1</c>,
/// <c>@p2</c>, and so on.
/// </summary>
internal static class SqlServerRpc
{
    private const ushort SpExecuteSql = 10;
    private const byte BatchSeparator = 0xFF;

    // Rows per request message in ExecuteUpdate, and a size at which to end a message early.
    private const int RowsPerMessage = 1000;
    private const int BytesPerMessage = 1 << 20;

    /// <summary>Parameter name for a field: its name without a leading @, or p1, p2, ... when unnamed.</summary>
    public static string ParameterName(Field field, int index)
    {
        string name = field.Name.StartsWith('@') ? field.Name.Substring(1) : field.Name;
        return name.Length > 0 ? name : "p" + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The SQL Server type a parameter is sent as. Strings and binary values
    /// use the bounded types unless a value in <paramref name="column"/>
    /// needs the <c>max</c> type.
    /// </summary>
    public static TdsColumn ParameterType(string name, Field field, IArrowArray? column, byte[] collation)
    {
        IArrowType type = field.DataType;
        return type switch
        {
            BooleanType => TdsColumn.Create(name, TdsDataType.BitN, 1),
            UInt8Type => TdsColumn.Create(name, TdsDataType.IntN, 1),
            Int8Type or Int16Type => TdsColumn.Create(name, TdsDataType.IntN, 2),
            UInt16Type or Int32Type => TdsColumn.Create(name, TdsDataType.IntN, 4),
            UInt32Type or Int64Type or UInt64Type => TdsColumn.Create(name, TdsDataType.IntN, 8),
            FloatType => TdsColumn.Create(name, TdsDataType.FloatN, 4),
            DoubleType => TdsColumn.Create(name, TdsDataType.FloatN, 8),
            Decimal128Type d when d.Precision <= 38 =>
                TdsColumn.Create(name, TdsDataType.DecimalN, DecimalLength(d.Precision), (byte)d.Precision, (byte)d.Scale),
            // json parameters are sent as nvarchar; the server converts them where a json value is needed.
            StringType or JsonType => TdsColumn.Create(name, TdsDataType.NVarChar, MaxBytes(column) <= 4000 ? 8000 : 0xFFFF, collation: collation),
            VariantType => TdsColumn.Create(name, TdsDataType.Variant, 8016),
            FixedSizeListType { ValueDataType: FloatType } v =>
                TdsColumn.Create(name, TdsDataType.Vector, TdsColumn.VectorHeaderLength + v.ListSize * 4),
            BinaryType => TdsColumn.Create(name, TdsDataType.BigVarBinary, MaxBytes(column) <= 8000 ? 8000 : 0xFFFF),
            GuidType or FixedSizeBinaryType { ByteWidth: 16 } => TdsColumn.Create(name, TdsDataType.Guid, 16),
            Date32Type or Date64Type => TdsColumn.Create(name, TdsDataType.DateN, 3),
            TimestampType { IsTimeZoneAware: true } t => TdsColumn.Create(name, TdsDataType.DateTimeOffsetN, 0, scale: Scale(t.Unit)),
            TimestampType t => TdsColumn.Create(name, TdsDataType.DateTime2N, 0, scale: Scale(t.Unit)),
            TimestampWithOffsetType t => TdsColumn.Create(name, TdsDataType.DateTimeOffsetN, 0,
                scale: Scale(((TimestampType)((StructType)t.StorageType).Fields[0].DataType).Unit)),
            Time32Type t => TdsColumn.Create(name, TdsDataType.TimeN, 0, scale: Scale(t.Unit)),
            Time64Type t => TdsColumn.Create(name, TdsDataType.TimeN, 0, scale: Scale(t.Unit)),
            _ => throw new AdbcException(
                $"Cannot bind parameter '{name}' of Arrow type {(type is ExtensionType e ? e.Name : type.Name)}",
                AdbcStatusCode.NotImplemented),
        };
    }

    private static int DecimalLength(int precision) => precision <= 9 ? 5 : precision <= 19 ? 9 : precision <= 28 ? 13 : 17;

    private static byte Scale(TimeUnit unit) => unit switch
    {
        TimeUnit.Second => 0,
        TimeUnit.Millisecond => 3,
        TimeUnit.Microsecond => 6,
        _ => 7,
    };

    /// <summary>Longest value in a string or binary column, in UTF-8 or raw bytes (an upper bound on UTF-16 characters).</summary>
    private static int MaxBytes(IArrowArray? column)
    {
        if (column is ExtensionArray extension)
        {
            column = extension.Storage;
        }
        if (column is not BinaryArray binary)
        {
            return 0;
        }
        int max = 0;
        for (int i = 0; i < binary.Length; i++)
        {
            max = Math.Max(max, binary.GetValueLength(i));
        }
        return max;
    }

    /// <summary>The parameters for one bound record batch: their types, encoders and declaration.</summary>
    private sealed class BatchParameters : IDisposable
    {
        public BatchParameters(RecordBatch batch, byte[] collation)
        {
            Batch = batch;
            var declaration = new StringBuilder();
            Encoders = new ColumnEncoder[batch.ColumnCount];
            for (int i = 0; i < Encoders.Length; i++)
            {
                Field field = batch.Schema.FieldsList[i];
                string name = ParameterName(field, i);
                TdsColumn type = ParameterType("@" + name, field, batch.Column(i), collation);
                Encoders[i] = EncoderFactory.Create(type, field, i, collation);
                Encoders[i].Bind(batch.Column(i));
                if (i > 0)
                {
                    declaration.Append(", ");
                }
                declaration.Append('@').Append(name).Append(' ').Append(type.SqlTypeName);
            }
            Declaration = declaration.ToString();
        }

        public RecordBatch Batch { get; }

        public ColumnEncoder[] Encoders { get; }

        /// <summary>The <c>@params</c> argument of sp_executesql, for example <c>@a INT, @b NVARCHAR(4000)</c>.</summary>
        public string Declaration { get; }

        /// <summary>Releases the encoders' pins on the batch's buffers, then the batch.</summary>
        public void Dispose()
        {
            foreach (ColumnEncoder? encoder in Encoders)
            {
                encoder?.Dispose();
            }
            Batch.Dispose();
        }
    }

    /// <summary>The rows of the bound data, one batch at a time.</summary>
    private sealed class ParameterRows : IDisposable
    {
        private readonly IArrowArrayStream _data;
        private readonly byte[] _collation;
        private BatchParameters? _current;
        private int _row = -1;

        public ParameterRows(IArrowArrayStream data, byte[] collation)
        {
            _data = data;
            _collation = collation;
        }

        public BatchParameters Current => _current!;

        public int Row => _row;

        public bool MoveNext()
        {
            while (_current == null || ++_row >= _current.Batch.Length)
            {
                _current?.Dispose();
                _current = null;
                RecordBatch? batch = _data.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult();
                if (batch == null)
                {
                    return false;
                }
                _current = new BatchParameters(batch, _collation);
                _row = -1;
            }
            return true;
        }

        public void Dispose()
        {
            _current?.Dispose();
            _current = null;
            _data.Dispose();
        }
    }

    /// <summary>Writes one sp_executesql call for the current row; the request message is already begun.</summary>
    private static void WriteCall(TdsSession session, string sql, BatchParameters parameters, int row)
    {
        PacketWriter writer = session.Writer;
        writer.WriteUInt16(0xFFFF); // procedure given by ID
        writer.WriteUInt16(SpExecuteSql);
        writer.WriteUInt16(0);      // option flags

        WriteString(writer, session, sql);
        WriteString(writer, session, parameters.Declaration);
        foreach (ColumnEncoder encoder in parameters.Encoders)
        {
            writer.WriteBVarChar(encoder.Target.Name);
            writer.WriteByte(0);    // status: input parameter
            encoder.Target.WriteTypeInfo(writer);
            encoder.Write(writer, row);
        }
    }

    /// <summary>Writes an unnamed nvarchar parameter (the statement or the declaration).</summary>
    private static void WriteString(PacketWriter writer, TdsSession session, string value)
    {
        bool max = value.Length > 4000;
        writer.WriteBVarChar("");
        writer.WriteByte(0);
        TdsColumn.Create("", TdsDataType.NVarChar, max ? 0xFFFF : 8000, collation: session.DefaultCollation).WriteTypeInfo(writer);
        StringEncoder.WriteValue(writer, System.Runtime.InteropServices.MemoryMarshal.AsBytes(value.AsSpan()), max);
    }

    /// <summary>
    /// Executes the statement once per bound row and returns the total row
    /// count. Rows are sent as batched RPC calls, many to a message.
    /// </summary>
    public static long ExecuteUpdate(TdsSession session, string sql, IArrowArrayStream data)
    {
        using var rows = new ParameterRows(data, session.DefaultCollation);
        long total = -1;
        bool more = rows.MoveNext();
        while (more)
        {
            session.BeginRequest(PacketType.Rpc);
            session.WriteAllHeaders();
            int count = 0;
            long start = session.Writer.BytesWritten;
            do
            {
                if (count > 0)
                {
                    session.Writer.WriteByte(BatchSeparator);
                }
                WriteCall(session, sql, rows.Current, rows.Row);
                count++;
                more = rows.MoveNext();
            }
            while (more && count < RowsPerMessage && session.Writer.BytesWritten - start < BytesPerMessage && !session.IsCancelRequested);
            session.EndRequest();

            long reported = session.DrainResponse();
            if (reported >= 0)
            {
                total = Math.Max(total, 0) + reported;
            }
        }
        return total;
    }

    /// <summary>
    /// Executes the query once per bound row, each after the previous one's
    /// results are read, and streams their first result sets as one.
    /// </summary>
    public static IArrowArrayStream ExecuteQuery(TdsSession session, string sql, IArrowArrayStream data)
    {
        var continuation = new Continuation(session, sql, new ParameterRows(data, session.DefaultCollation));
        try
        {
            while (continuation.SendNext())
            {
                TdsColumn[]? columns = session.ReadUntilResultSet();
                if (columns == null)
                {
                    continue;
                }
                try
                {
                    return new SqlServerQueryStream(session, columns, continuation: continuation, utf16Strings: session.Settings.Utf16Strings);
                }
                catch
                {
                    session.ActiveStream = null;
                    session.DrainResponse(columns);
                    throw;
                }
            }
        }
        catch
        {
            continuation.Dispose();
            throw;
        }
        continuation.Dispose();
        return SqlServerQueryStream.Empty();
    }

    private sealed class Continuation(TdsSession session, string sql, ParameterRows rows) : IQueryContinuation
    {
        public bool SendNext()
        {
            if (!rows.MoveNext())
            {
                return false;
            }
            session.BeginRequest(PacketType.Rpc);
            session.WriteAllHeaders();
            WriteCall(session, sql, rows.Current, rows.Row);
            session.EndRequest();
            return true;
        }

        public void Dispose() => rows.Dispose();
    }

    /// <summary>
    /// The parameters of a bound schema with their types (<c>@a int, @b
    /// nvarchar(10)</c>), so a batch can be described without values.
    /// </summary>
    public static string Declarations(Schema schema, byte[] collation)
    {
        if (schema.FieldsList.Count == 0)
        {
            return "";
        }
        var sql = new StringBuilder();
        for (int i = 0; i < schema.FieldsList.Count; i++)
        {
            Field field = schema.FieldsList[i];
            string name = ParameterName(field, i);
            if (i > 0)
            {
                sql.Append(", ");
            }
            sql.Append('@').Append(name).Append(' ').Append(ParameterType("@" + name, field, null, collation).SqlTypeName);
        }
        return sql.ToString();
    }
}
