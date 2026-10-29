// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Globalization;
using System.Text;
using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Clast.Drivers.SqlServer.Arrow;
using Clast.Drivers.SqlServer.Tds;

namespace Clast.Drivers.SqlServer;

/// <summary>
/// Partitioned execution: a query is split into ranges of an integer column
/// so that the partitions can be read in parallel over separate connections.
/// </summary>
/// <remarks>
/// A partition descriptor is a version byte followed by the UTF-8 text of the
/// partition's query, so it can be read on any connection to the same server.
/// The query is used as a derived table, so it must be a single SELECT that
/// is valid in a FROM clause (no ORDER BY without TOP, no trailing statements).
/// </remarks>
internal static class SqlServerPartitions
{
    private const byte DescriptorVersion = 1;

    public static PartitionedResult Execute(TdsSession session, string sql, string? column, int count)
    {
        Schema schema = DescribeResult(session, sql);
        if (column == null || count <= 1)
        {
            return new PartitionedResult(schema, -1, [Encode(sql)]);
        }

        Field? field = schema.FieldsList.FirstOrDefault(f => string.Equals(f.Name, column, StringComparison.OrdinalIgnoreCase));
        if (field == null)
        {
            throw new AdbcException($"The query has no column '{column}' to partition by", AdbcStatusCode.InvalidArgument);
        }
        if (field.DataType.TypeId is not (Apache.Arrow.Types.ArrowTypeId.Int64 or Apache.Arrow.Types.ArrowTypeId.Int32
            or Apache.Arrow.Types.ArrowTypeId.Int16 or Apache.Arrow.Types.ArrowTypeId.UInt8))
        {
            throw new AdbcException($"Partitioning requires an integer column; '{field.Name}' is {field.DataType.Name}", AdbcStatusCode.NotImplemented);
        }

        string quoted = IngestTarget.Quote(field.Name);
        (long? min, long? max) = GetRange(session, $"SELECT MIN(q.{quoted}), MAX(q.{quoted}) FROM ({sql}) AS q");
        if (min == null || max == null)
        {
            return new PartitionedResult(schema, -1, [Encode(sql)]);
        }

        Int128 span = (Int128)max.Value - min.Value + 1;
        Int128 width = (span + count - 1) / count;
        var partitions = new List<PartitionDescriptor>(count);
        for (int i = 0; i < count; i++)
        {
            Int128 low = min.Value + width * i;
            if (low > max.Value)
            {
                break;
            }
            Int128 high = low + width; // exclusive
            var where = new StringBuilder();
            where.Append(FormattableString.Invariant($"q.{quoted} >= {low}"));
            if (high <= max.Value)
            {
                where.Append(FormattableString.Invariant($" AND q.{quoted} < {high}"));
            }
            string partition = $"SELECT * FROM ({sql}) AS q WHERE {where}";
            if (i == 0 && field.IsNullable)
            {
                // A separate arm rather than "OR ... IS NULL", which would stop the range from using an index seek.
                partition += $" UNION ALL SELECT * FROM ({sql}) AS q WHERE q.{quoted} IS NULL";
            }
            partitions.Add(Encode(partition));
        }
        return new PartitionedResult(schema, -1, partitions);
    }

    public static string Decode(PartitionDescriptor descriptor)
    {
        ReadOnlySpan<byte> bytes = descriptor.Descriptor;
        if (bytes.Length < 1 || bytes[0] != DescriptorVersion)
        {
            throw new AdbcException("Not a partition descriptor produced by this driver", AdbcStatusCode.InvalidArgument);
        }
        return Encoding.UTF8.GetString(bytes.Slice(1));
    }

    private static PartitionDescriptor Encode(string sql)
    {
        byte[] bytes = new byte[1 + Encoding.UTF8.GetByteCount(sql)];
        bytes[0] = DescriptorVersion;
        Encoding.UTF8.GetBytes(sql, bytes.AsSpan(1));
        return new PartitionDescriptor(bytes);
    }

    /// <summary>
    /// Gets the schema of the query's first result set without running it.
    /// <paramref name="parameters"/> declares any parameters it refers to, as
    /// <c>@a int, @b nvarchar(10)</c>.
    /// </summary>
    /// <remarks>
    /// <c>sp_describe_first_result_set</c> analyzes the batch, and fails where
    /// it can't tell the shape (a temp table in a multi-statement batch,
    /// dynamic SQL, branches that return different shapes). SET FMTONLY would
    /// instead return a shape that may be wrong, and runs a procedure that
    /// turns it off for real. The Arrow types come from the TDS column
    /// metadata, as for query results, so they are read from a SELECT of a
    /// typed NULL per column under FMTONLY. A server without the procedure
    /// gets FMTONLY on the query.
    /// </remarks>
    internal static Schema DescribeResult(TdsSession session, string sql, string parameters = "")
    {
        if (session.DescribesFirstResultSet != false)
        {
            List<object?[]> rows;
            try
            {
                rows = SqlServerMetadata.Query(session,
                    $"EXEC sys.sp_describe_first_result_set @tsql = {SqlServerMetadata.Literal(sql)}, @params = {(parameters == "" ? "NULL" : SqlServerMetadata.Literal(parameters))}")[0];
            }
            // Errors in the described batch come with error 11529 after them, so a lone one is about the procedure itself.
            catch (SqlServerException ex) when (session.DescribesFirstResultSet == null && ex.Errors.Count == 1 && ex.Number is 2812 or 15871)
            {
                session.DescribesFirstResultSet = false;
                return FormatOnly(session, sql, parameters == "" ? "" : $"DECLARE {parameters};\n");
            }
            session.DescribesFirstResultSet = true;
            return FromDescription(session, rows);
        }
        return FormatOnly(session, sql, parameters == "" ? "" : $"DECLARE {parameters};\n");
    }

    /// <summary>
    /// The schema described by the rows of <c>sp_describe_first_result_set</c>:
    /// names and nullability from the rows, Arrow types from FMTONLY on a
    /// SELECT of NULLs of each column's type.
    /// </summary>
    private static Schema FromDescription(TdsSession session, List<object?[]> rows)
    {
        // Columns: is_hidden, column_ordinal, name, is_nullable, system_type_id, system_type_name, ...
        if (rows.Count == 0)
        {
            return new Schema([], null);
        }
        Schema types = FormatOnly(session, "SELECT " + string.Join(", ", rows.Select(r => $"CAST(NULL AS {(string)r[5]!})")));
        return new Schema(rows.Select((r, i) =>
        {
            Field field = types.FieldsList[i];
            return new Field((string?)r[2] ?? "", field.DataType, (bool)r[3]!, field.Metadata);
        }), null);
    }

    /// <summary>
    /// The schema of the batch's first result set under SET FMTONLY, which
    /// doesn't run it. <paramref name="declarations"/> declares any parameters
    /// it refers to.
    /// </summary>
    internal static Schema FormatOnly(TdsSession session, string sql, string declarations = "")
    {
        session.SendSqlBatch($"{declarations}SET FMTONLY ON; {sql}; SET FMTONLY OFF;");
        TdsColumn[]? columns = session.ReadUntilResultSet();
        if (columns == null)
        {
            return new Schema([], null);
        }
        session.DrainResponse(columns);
        return new Schema(columns.Select(c =>
        {
            using ColumnDecoder decoder = DecoderFactory.Create(c, 1, session.DefaultCollation, session.Settings.HierarchyIdText);
            return DecoderFactory.CreateField(decoder);
        }), null);
    }

    private static (long? Min, long? Max) GetRange(TdsSession session, string sql)
    {
        session.SendSqlBatch(sql);
        TdsColumn[] columns = session.ReadUntilResultSet()!;
        using var stream = new SqlServerQueryStream(session, columns);
        using RecordBatch batch = stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult()!;
        long? min = AsInt64(batch.Column(0));
        long? max = AsInt64(batch.Column(1));
        while (stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is RecordBatch extra)
        {
            extra.Dispose();
        }
        return (min, max);
    }

    private static long? AsInt64(IArrowArray array) => array switch
    {
        Int64Array a => a.GetValue(0),
        Int32Array a => a.GetValue(0),
        Int16Array a => a.GetValue(0),
        UInt8Array a => a.GetValue(0),
        _ => throw new InvalidOperationException(),
    };
}
