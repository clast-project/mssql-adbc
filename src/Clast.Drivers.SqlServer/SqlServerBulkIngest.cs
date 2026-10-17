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

/// <summary>Where bulk ingestion writes to.</summary>
internal sealed record IngestTarget(string? Catalog, string? DbSchema, string Table, bool Temporary)
{
    /// <summary>The table name, bracket-quoted and qualified.</summary>
    public string QuotedName
    {
        get
        {
            if (Temporary)
            {
                return Quote(Table.StartsWith('#') ? Table : "#" + Table);
            }
            var parts = new List<string>(3);
            if (Catalog != null)
            {
                parts.Add(Quote(Catalog));
            }
            if (DbSchema != null || Catalog != null)
            {
                parts.Add(DbSchema != null ? Quote(DbSchema) : "");
            }
            parts.Add(Quote(Table));
            return string.Join(".", parts);
        }
    }

    /// <summary>The name to pass to OBJECT_ID and system procedures.</summary>
    public string ObjectIdName => Temporary ? "tempdb.." + QuotedName : QuotedName;

    public static string Quote(string identifier) => "[" + identifier.Replace("]", "]]") + "]";
}

/// <summary>
/// Bulk ingestion with the TDS bulk-load protocol: an <c>INSERT BULK</c>
/// statement, then one BulkLoad message holding COLMETADATA and a ROW token
/// per row, encoded directly from the Arrow columns.
/// </summary>
internal static class SqlServerBulkIngest
{
    public static long Execute(TdsSession session, IngestTarget target, BulkIngestMode mode, IArrowArrayStream data, bool tableLock)
    {
        Schema schema = data.Schema;

        switch (mode)
        {
            case BulkIngestMode.Create:
                session.ExecuteNonQuery(CreateTableSql(target, schema));
                break;
            case BulkIngestMode.Replace:
                session.ExecuteNonQuery($"DROP TABLE IF EXISTS {target.QuotedName}; {CreateTableSql(target, schema)}");
                break;
            case BulkIngestMode.CreateAppend:
                session.ExecuteNonQuery($"IF OBJECT_ID({Literal(target.ObjectIdName)}) IS NULL {CreateTableSql(target, schema)}");
                break;
        }

        TdsColumn[] tableColumns = GetTableColumns(session, target);
        ColumnEncoder[] encoders = MapColumns(schema, tableColumns);
        Dictionary<string, string> collations = encoders.Any(e => e.Target.Type is TdsDataType.BigVarChar or TdsDataType.BigChar)
            ? GetCollationNames(session, target)
            : new Dictionary<string, string>();

        session.ExecuteNonQuery(InsertBulkSql(target, encoders, collations, tableLock));
        return SendRows(session, encoders, data);
    }

    private static long SendRows(TdsSession session, ColumnEncoder[] encoders, IArrowArrayStream data)
    {
        PacketWriter writer = session.Writer;
        long rows = 0;

        session.BeginRequest(PacketType.BulkLoad);
        try
        {
            writer.WriteByte((byte)TokenType.ColMetadata);
            writer.WriteUInt16((ushort)encoders.Length);
            foreach (ColumnEncoder encoder in encoders)
            {
                encoder.Target.WriteBulkMetadata(writer);
            }

            while (data.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is RecordBatch batch)
            {
                using (batch)
                {
                    ThrowIfCancelled(session);
                    foreach (ColumnEncoder encoder in encoders)
                    {
                        encoder.Bind(batch.Column(encoder.SourceIndex));
                    }

                    int length = batch.Length;
                    for (int row = 0; row < length; row++)
                    {
                        if ((row & CancelCheckRows) == CancelCheckRows)
                        {
                            ThrowIfCancelled(session);
                        }
                        writer.WriteByte((byte)TokenType.Row);
                        for (int c = 0; c < encoders.Length; c++)
                        {
                            encoders[c].Write(writer, row);
                        }
                    }
                    rows += length;
                }
            }

            writer.WriteByte((byte)TokenType.Done);
            writer.WriteUInt16(0); // status
            writer.WriteUInt16(0); // current command
            writer.WriteInt64(0);  // row count
        }
        catch (Exception)
        {
            // Discard what has been sent of the message; the server ignores it.
            session.EndRequest(abort: true);
            try
            {
                session.DrainResponse();
            }
            catch (AdbcException ex) when (ex.Status == AdbcStatusCode.Cancelled)
            {
                // Cancelled while aborting; report the original failure.
            }
            throw;
        }
        session.EndRequest();

        long reported = session.DrainResponse();
        return reported >= 0 ? reported : rows;
    }

    // A bulk-load message cannot be interrupted by an attention, so the sender
    // polls for cancellation and abandons the message itself.
    private const int CancelCheckRows = 4095;

    private static void ThrowIfCancelled(TdsSession session)
    {
        if (session.IsCancelRequested)
        {
            throw TdsSession.CancelledException();
        }
    }

    private static TdsColumn[] GetTableColumns(TdsSession session, IngestTarget target)
    {
        session.SendSqlBatch($"SELECT TOP 0 * FROM {target.QuotedName}");
        TdsColumn[]? columns = session.ReadUntilResultSet();
        if (columns == null)
        {
            throw new AdbcException($"Could not read the columns of {target.QuotedName}", AdbcStatusCode.InternalError);
        }
        session.DrainResponse(columns);
        return columns;
    }

    private static ColumnEncoder[] MapColumns(Schema schema, TdsColumn[] tableColumns)
    {
        var encoders = new ColumnEncoder[schema.FieldsList.Count];
        for (int i = 0; i < encoders.Length; i++)
        {
            Field field = schema.FieldsList[i];
            TdsColumn? column = tableColumns.FirstOrDefault(c => string.Equals(c.Name, field.Name, StringComparison.OrdinalIgnoreCase));
            if (column == null)
            {
                throw new AdbcException($"The target table has no column '{field.Name}'", AdbcStatusCode.InvalidArgument);
            }
            encoders[i] = EncoderFactory.Create(column.ToBulkTarget(), field, i);
        }
        return encoders;
    }

    private static Dictionary<string, string> GetCollationNames(TdsSession session, IngestTarget target)
    {
        string procedure = target.Temporary ? "tempdb..sp_tablecollations_100" : "sp_tablecollations_100";
        string name = target.Temporary ? "." + target.QuotedName : target.QuotedName;

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        session.SendSqlBatch($"EXEC {procedure} {Literal(name)}");
        TdsColumn[]? columns = session.ReadUntilResultSet();
        if (columns == null)
        {
            return result;
        }

        using var stream = new SqlServerQueryStream(session, columns);
        int nameIndex = stream.Schema.GetFieldIndex("name");
        int collationIndex = stream.Schema.GetFieldIndex("collation");
        while (stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is RecordBatch batch)
        {
            using (batch)
            {
                var names = (StringArray)batch.Column(nameIndex);
                var collationNames = (StringArray)batch.Column(collationIndex);
                for (int i = 0; i < batch.Length; i++)
                {
                    if (!collationNames.IsNull(i))
                    {
                        result[names.GetString(i)] = collationNames.GetString(i);
                    }
                }
            }
        }
        return result;
    }

    private static string InsertBulkSql(IngestTarget target, ColumnEncoder[] encoders, Dictionary<string, string> collations, bool tableLock)
    {
        var sql = new StringBuilder("INSERT BULK ").Append(target.QuotedName).Append(" (");
        for (int i = 0; i < encoders.Length; i++)
        {
            TdsColumn column = encoders[i].Target;
            if (i > 0)
            {
                sql.Append(", ");
            }
            sql.Append(IngestTarget.Quote(column.Name)).Append(' ').Append(column.BulkTypeName);
            if (collations.TryGetValue(column.Name, out string? collation))
            {
                sql.Append(" COLLATE ").Append(collation);
            }
        }
        sql.Append(')');
        if (tableLock)
        {
            sql.Append(" WITH (TABLOCK)");
        }
        return sql.ToString();
    }

    private static string CreateTableSql(IngestTarget target, Schema schema)
    {
        var sql = new StringBuilder("CREATE TABLE ").Append(target.QuotedName).Append(" (");
        for (int i = 0; i < schema.FieldsList.Count; i++)
        {
            Field field = schema.FieldsList[i];
            if (i > 0)
            {
                sql.Append(", ");
            }
            sql.Append(IngestTarget.Quote(field.Name)).Append(' ').Append(SqlType(field)).Append(field.IsNullable ? " NULL" : " NOT NULL");
        }
        return sql.Append(')').ToString();
    }

    /// <summary>SQL Server column type for an Arrow field, used when the driver creates the table.</summary>
    private static string SqlType(Field field) => field.DataType switch
    {
        BooleanType => "bit",
        UInt8Type => "tinyint",
        Int8Type or Int16Type => "smallint",
        UInt16Type or Int32Type => "int",
        UInt32Type or Int64Type => "bigint",
        UInt64Type => "decimal(20,0)",
        FloatType => "real",
        DoubleType => "float",
        Decimal128Type d when d.Precision <= 38 => $"decimal({d.Precision},{d.Scale})",
        StringType => "nvarchar(max)",
        BinaryType => "varbinary(max)",
        GuidType => "uniqueidentifier",
        FixedSizeBinaryType f when f.ByteWidth <= 8000 => $"binary({f.ByteWidth})",
        Date32Type or Date64Type => "date",
        TimestampType { IsTimeZoneAware: true } t => $"datetimeoffset({Scale(t.Unit)})",
        TimestampType t => $"datetime2({Scale(t.Unit)})",
        TimestampWithOffsetType t => $"datetimeoffset({Scale(((TimestampType)((StructType)t.StorageType).Fields[0].DataType).Unit)})",
        Time32Type t => $"time({Scale(t.Unit)})",
        Time64Type t => $"time({Scale(t.Unit)})",
        _ => throw new AdbcException(
            $"Cannot create a column for field '{field.Name}' of Arrow type {(field.DataType is ExtensionType e ? e.Name : field.DataType.Name)}",
            AdbcStatusCode.NotImplemented),
    };

    private static int Scale(TimeUnit unit) => unit switch
    {
        TimeUnit.Second => 0,
        TimeUnit.Millisecond => 3,
        TimeUnit.Microsecond => 6,
        _ => 7,
    };

    private static string Literal(string value) => "N'" + value.Replace("'", "''") + "'";
}
