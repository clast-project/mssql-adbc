// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;

namespace Clast.Drivers.SqlServer.Arrow;

/// <summary>Small builders for the nested results of the metadata calls.</summary>
internal static class ResultArrays
{
    public static StringArray Strings(IEnumerable<string?> values)
    {
        var builder = new StringArray.Builder();
        foreach (string? value in values)
        {
            if (value == null)
            {
                builder.AppendNull();
            }
            else
            {
                builder.Append(value);
            }
        }
        return builder.Build();
    }

    public static Int16Array Int16s(IEnumerable<short?> values)
    {
        var builder = new Int16Array.Builder();
        foreach (short? value in values)
        {
            builder.Append(value);
        }
        return builder.Build();
    }

    public static Int32Array Int32s(IEnumerable<int?> values)
    {
        var builder = new Int32Array.Builder();
        foreach (int? value in values)
        {
            builder.Append(value);
        }
        return builder.Build();
    }

    public static BooleanArray Booleans(IEnumerable<bool?> values)
    {
        var builder = new BooleanArray.Builder();
        foreach (bool? value in values)
        {
            if (value is bool b)
            {
                builder.Append(b);
            }
            else
            {
                builder.AppendNull();
            }
        }
        return builder.Build();
    }

    /// <summary>
    /// A list array with one list per entry of <paramref name="lists"/> (null
    /// for a null list); <paramref name="buildValues"/> builds the child array
    /// from all of the lists' items, flattened.
    /// </summary>
    public static ListArray List<T>(ListType type, IReadOnlyList<IReadOnlyList<T>?> lists, Func<List<T>, IArrowArray> buildValues)
    {
        var offsets = new ArrowBuffer.Builder<int>(lists.Count + 1);
        var validity = new ArrowBuffer.BitmapBuilder(lists.Count);
        var items = new List<T>();
        int nulls = 0;

        offsets.Append(0);
        foreach (IReadOnlyList<T>? list in lists)
        {
            if (list == null)
            {
                validity.Append(false);
                nulls++;
            }
            else
            {
                validity.Append(true);
                items.AddRange(list);
            }
            offsets.Append(items.Count);
        }
        return new ListArray(type, lists.Count, offsets.Build(), buildValues(items), nulls > 0 ? validity.Build() : ArrowBuffer.Empty, nulls);
    }

    /// <summary>A struct array without nulls.</summary>
    public static StructArray Struct(StructType type, int length, params IArrowArray[] children) =>
        new StructArray(type, length, children, ArrowBuffer.Empty, 0);

    /// <summary>The value type of a list field in a standard schema.</summary>
    public static StructType ItemType(ListType list) => (StructType)list.ValueDataType;

    /// <summary>Reads a value from a result column as a CLR object.</summary>
    public static object? Value(IArrowArray array, int index)
    {
        if (array.IsNull(index))
        {
            return null;
        }
        return array switch
        {
            StringArray a => a.GetString(index),
            Int32Array a => a.GetValue(index),
            Int16Array a => a.GetValue(index),
            Int64Array a => a.GetValue(index),
            UInt8Array a => a.GetValue(index),
            BooleanArray a => a.GetValue(index),
            DoubleArray a => a.GetValue(index),
            FloatArray a => a.GetValue(index),
            _ => throw new NotSupportedException($"Unexpected metadata column type {array.Data.DataType}"),
        };
    }
}

/// <summary>A stream over record batches that are already in memory; owns them.</summary>
internal sealed class InMemoryStream : IArrowArrayStream
{
    private readonly Queue<RecordBatch> _batches;

    public InMemoryStream(Schema schema, params RecordBatch[] batches)
    {
        Schema = schema;
        _batches = new Queue<RecordBatch>(batches);
    }

    public Schema Schema { get; }

    public ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default) =>
        new ValueTask<RecordBatch?>(_batches.TryDequeue(out RecordBatch? batch) ? batch : null);

    public void Dispose()
    {
        while (_batches.TryDequeue(out RecordBatch? batch))
        {
            batch.Dispose();
        }
    }
}
