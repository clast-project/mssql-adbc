// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Types;
using Clast.Drivers.SqlServer.Tds;

namespace Clast.Drivers.SqlServer.Arrow;

/// <summary>
/// Decodes one column's values from TDS rows directly into Arrow buffers.
/// One decoder is created per column when COLMETADATA arrives.
/// </summary>
internal abstract class ColumnDecoder
{
    protected ColumnDecoder(TdsColumn column, IArrowType arrowType)
    {
        Column = column;
        ArrowType = arrowType;
    }

    public TdsColumn Column { get; }

    public IArrowType ArrowType { get; }

    /// <summary>Reads one value, including its length prefix, and appends it (or a null).</summary>
    public abstract void ReadValue(PacketReader reader);

    /// <summary>Appends a null without reading anything (NBCROW null bitmap).</summary>
    public abstract void AppendNull();

    /// <summary>Returns the values appended since the last call and starts a new batch.</summary>
    public abstract IArrowArray Build();

    protected static IArrowArray BuildArray(IArrowType type, int length, int nullCount, ArrowBuffer validity, params ArrowBuffer[] buffers)
    {
        var all = new ArrowBuffer[buffers.Length + 1];
        all[0] = validity;
        buffers.CopyTo(all, 1);
        return ArrowArrayFactory.BuildArray(new ArrayData(type, length, nullCount, 0, all));
    }
}
