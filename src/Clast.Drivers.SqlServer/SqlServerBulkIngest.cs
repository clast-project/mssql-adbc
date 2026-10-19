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
/// per row, encoded directly from the Arrow columns. With a batch size, the
/// rows are split into that many per <c>INSERT BULK</c> and message.
/// </summary>
internal static class SqlServerBulkIngest
{
    public static long Execute(TdsSession session, IngestTarget target, BulkIngestMode mode, IArrowArrayStream data, bool tableLock, int batchSize)
    {
        Schema schema = data.Schema;

        if (mode != BulkIngestMode.Append)
        {
            string create = CreateTableSql(target, schema, SqlServerEngine.Edition(session));
            session.ExecuteNonQuery(mode switch
            {
                // Not DROP TABLE IF EXISTS, which Synapse dedicated pools lack.
                BulkIngestMode.Replace => $"IF OBJECT_ID({Literal(target.ObjectIdName)}) IS NOT NULL DROP TABLE {target.QuotedName}; {create}",
                BulkIngestMode.CreateAppend => $"IF OBJECT_ID({Literal(target.ObjectIdName)}) IS NULL {create}",
                _ => create,
            });
        }

        TdsColumn[] tableColumns = GetTableColumns(session, target);
        ColumnEncoder[] encoders = MapColumns(schema, tableColumns, session.DefaultCollation);
        Dictionary<string, string> collations = encoders.Any(e => e.Target.Type is TdsDataType.BigVarChar or TdsDataType.BigChar)
            ? GetCollationNames(session, target)
            : new Dictionary<string, string>();

        return SendRows(session, encoders, data, InsertBulkSql(target, encoders, collations, tableLock), batchSize);
    }

    private static long SendRows(TdsSession session, ColumnEncoder[] encoders, IArrowArrayStream data, string insertBulk, int batchSize)
    {
        PacketWriter writer = session.Writer;
        long rows = 0;
        // Rows in the current BulkLoad message, and whether one is being sent.
        int loadRows = 0;
        bool loading = false;

        void BeginLoad()
        {
            session.ExecuteNonQuery(insertBulk);
            session.BeginRequest(PacketType.BulkLoad);
            loading = true;
            loadRows = 0;
            writer.WriteByte((byte)TokenType.ColMetadata);
            writer.WriteUInt16((ushort)encoders.Length);
            foreach (ColumnEncoder encoder in encoders)
            {
                encoder.Target.WriteBulkMetadata(writer);
            }
        }

        void EndLoad()
        {
            writer.WriteByte((byte)TokenType.Done);
            writer.WriteUInt16(0); // status
            writer.WriteUInt16(0); // current command
            writer.WriteInt64(0);  // row count
            loading = false;
            session.EndRequest();

            // The server's row counts are ignored: they vary (Fabric Warehouse's add up to
            // twice the rows), and a load is all or nothing.
            session.DrainResponse();
        }

        try
        {
            BeginLoad();

            while (data.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is RecordBatch batch)
            {
                using (batch)
                {
                    ThrowIfCancelled(session);
                    try
                    {
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
                            if (loadRows == batchSize && batchSize > 0)
                            {
                                EndLoad();
                                BeginLoad();
                            }
                            writer.WriteByte((byte)TokenType.Row);
                            for (int c = 0; c < encoders.Length; c++)
                            {
                                encoders[c].Write(writer, row);
                            }
                            loadRows++;
                        }
                        rows += length;
                    }
                    finally
                    {
                        // The encoders pin the batch's buffers; release them before it is disposed.
                        foreach (ColumnEncoder encoder in encoders)
                        {
                            encoder.Release();
                        }
                    }
                }
            }

            EndLoad();
        }
        catch (Exception) when (loading)
        {
            // Discard what has been sent of the message; the server ignores it.
            loading = false;
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

        // The rows sent, as SqlBulkCopy counts them.
        return rows;
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

    private static ColumnEncoder[] MapColumns(Schema schema, TdsColumn[] tableColumns, byte[] defaultCollation)
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
            encoders[i] = EncoderFactory.Create(column.ToBulkTarget(), field, i, defaultCollation);
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

    /// <summary>
    /// The CREATE TABLE statement for the Arrow schema. On a Synapse dedicated
    /// pool a table with a (max) column is a heap: the default, a clustered
    /// columnstore index, cannot hold one.
    /// </summary>
    internal static string CreateTableSql(IngestTarget target, Schema schema, EngineEdition edition)
    {
        var sql = new StringBuilder("CREATE TABLE ").Append(target.QuotedName).Append(" (");
        bool max = false;
        for (int i = 0; i < schema.FieldsList.Count; i++)
        {
            Field field = schema.FieldsList[i];
            if (i > 0)
            {
                sql.Append(", ");
            }
            string type = SqlType(field, edition);
            max |= type.EndsWith("(max)", StringComparison.Ordinal);
            sql.Append(IngestTarget.Quote(field.Name)).Append(' ').Append(type).Append(field.IsNullable ? " NULL" : " NOT NULL");
        }
        sql.Append(')');
        if (max && edition == EngineEdition.SynapseDedicated)
        {
            sql.Append(" WITH (HEAP)");
        }
        return sql.ToString();
    }

    /// <summary>
    /// The column type for an Arrow field, used when the driver creates the
    /// table: SQL Server's, or on Fabric Warehouse (whose tables are Delta
    /// Parquet) the nearest it stores. There strings are varchar in the
    /// warehouse's UTF-8 collation, tinyint widens to smallint, fractional
    /// seconds stop at 6 digits, and time-zone-aware timestamps are stored as
    /// UTC datetime2. Synapse dedicated pools have no json type.
    /// </summary>
    internal static string SqlType(Field field, EngineEdition edition) => edition switch
    {
        EngineEdition.Warehouse => WarehouseType(field),
        EngineEdition.SynapseDedicated when field.DataType is JsonType => "nvarchar(max)",
        EngineEdition.SynapseDedicated => SqlType(field),
        _ => SqlServerType(field) ?? SqlType(field),
    };

    /// <summary>
    /// xml and hierarchyid, which Arrow has no types for, from the field's
    /// <c>MSSQL:type</c> metadata, as the driver reads them: xml from a string
    /// field, hierarchyid from a string (its text form) or binary field. The
    /// analytics engines have neither type.
    /// </summary>
    private static string? SqlServerType(Field field) =>
        field.Metadata != null && field.Metadata.TryGetValue(DecoderFactory.SqlTypeMetadataKey, out string? name)
            ? (name.ToUpperInvariant(), field.DataType) switch
            {
                ("XML", StringType) => "xml",
                ("HIERARCHYID", StringType or BinaryType) => "hierarchyid",
                _ => null,
            }
            : null;

    private static string WarehouseType(Field field) => field.DataType switch
    {
        UInt8Type => "smallint",
        StringType or JsonType => "varchar(max)",
        // Exactly fixed-size binary: decimals and GUIDs derive from it.
        FixedSizeBinaryType f when f.GetType() == typeof(FixedSizeBinaryType) && f.ByteWidth <= 8000 => $"varbinary({f.ByteWidth})",
        TimestampType t => $"datetime2({Math.Min(Scale(t.Unit), 6)})",
        Time32Type t => $"time({Math.Min(Scale(t.Unit), 6)})",
        Time64Type t => $"time({Math.Min(Scale(t.Unit), 6)})",
        TimestampWithOffsetType or VariantType or FixedSizeListType => throw new AdbcException(
            $"Cannot create a column for field '{field.Name}' of Arrow type {Describe(field.DataType)}: Fabric Warehouse has no such column type",
            AdbcStatusCode.NotImplemented),
        _ => SqlType(field),
    };

    private static string Describe(IArrowType type) => type is ExtensionType e ? e.Name : type.Name;

    /// <summary>SQL Server column type for an Arrow field.</summary>
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
        JsonType => "json",
        VariantType => "sql_variant",
        FixedSizeListType { ValueDataType: FloatType } v => $"vector({v.ListSize})",
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
            $"Cannot create a column for field '{field.Name}' of Arrow type {Describe(field.DataType)}",
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
