// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Reflection;
using System.Text;
using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Clast.Drivers.SqlServer.Arrow;
using Clast.Drivers.SqlServer.Tds;
using static Clast.Drivers.SqlServer.Arrow.ResultArrays;

namespace Clast.Drivers.SqlServer;

/// <summary>
/// GetInfo, GetTableTypes, GetTableSchema and GetObjects. Catalogs are
/// databases; objects are read from each database's catalog views.
/// </summary>
internal static class SqlServerMetadata
{
    public const string DriverName = "Clast ADBC Driver for SQL Server";

    private const string TableType = "TABLE";
    private const string ViewType = "VIEW";

    private static readonly AdbcInfoCode[] s_infoCodes =
    [
        AdbcInfoCode.VendorName,
        AdbcInfoCode.VendorVersion,
        AdbcInfoCode.VendorSql,
        AdbcInfoCode.VendorSubstrait,
        AdbcInfoCode.DriverName,
        AdbcInfoCode.DriverVersion,
        AdbcInfoCode.DriverArrowVersion,
        AdbcInfoCode.DriverAdbcVersion,
    ];

    // ---- GetInfo ----

    public static IArrowArrayStream GetInfo(TdsSession session, IReadOnlyList<AdbcInfoCode> codes)
    {
        if (codes.Count == 0)
        {
            codes = s_infoCodes;
        }

        var names = new UInt32Array.Builder();
        var typeIds = new ArrowBuffer.Builder<byte>();
        var offsets = new ArrowBuffer.Builder<int>();
        var strings = new List<string?>();
        var bools = new List<bool?>();
        var int64s = new Int64Array.Builder();

        void AddString(AdbcInfoCode code, string value)
        {
            names.Append((uint)code);
            typeIds.Append(0);
            offsets.Append(strings.Count);
            strings.Add(value);
        }

        void AddBool(AdbcInfoCode code, bool value)
        {
            names.Append((uint)code);
            typeIds.Append(1);
            offsets.Append(bools.Count);
            bools.Add(value);
        }

        foreach (AdbcInfoCode code in codes)
        {
            switch (code)
            {
                case AdbcInfoCode.VendorName:
                    AddString(code, session.ServerProgramName.TrimEnd('\0', ' '));
                    break;
                case AdbcInfoCode.VendorVersion:
                    AddString(code, session.ServerVersion.ToString());
                    break;
                case AdbcInfoCode.VendorSql:
                    AddBool(code, true);
                    break;
                case AdbcInfoCode.VendorSubstrait:
                    AddBool(code, false);
                    break;
                case AdbcInfoCode.DriverName:
                    AddString(code, DriverName);
                    break;
                case AdbcInfoCode.DriverVersion:
                    AddString(code, InformationalVersion(typeof(SqlServerMetadata).Assembly));
                    break;
                case AdbcInfoCode.DriverArrowVersion:
                    AddString(code, InformationalVersion(typeof(RecordBatch).Assembly));
                    break;
                case AdbcInfoCode.DriverAdbcVersion:
                    names.Append((uint)code);
                    typeIds.Append(2);
                    offsets.Append(int64s.Length);
                    int64s.Append(AdbcVersion.Version_1_1_0);
                    break;
                default:
                    // Codes the driver does not know are left out of the result.
                    break;
            }
        }

        var unionType = (UnionType)StandardSchemas.GetInfoSchema.FieldsList[1].DataType;
        var stringListType = (ListType)unionType.Fields[4].DataType;
        var mapType = (ListType)unionType.Fields[5].DataType;
        IArrowArray[] children =
        [
            Strings(strings),
            Booleans(bools),
            int64s.Build(),
            new Int32Array.Builder().Build(),
            List<string>(stringListType, [], Strings),
            List<int>(mapType, [], _ => Struct(ItemType(mapType), 0, new Int32Array.Builder().Build(), new Int32Array.Builder().Build())),
        ];
        int length = names.Length;
        var values = new DenseUnionArray(unionType, length, children, typeIds.Build(), offsets.Build());
        return new InMemoryStream(StandardSchemas.GetInfoSchema, new RecordBatch(StandardSchemas.GetInfoSchema, [names.Build(), values], length));
    }

    private static string InformationalVersion(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? assembly.GetName().Version?.ToString()
        ?? "unknown";

    // ---- GetTableTypes ----

    public static IArrowArrayStream GetTableTypes()
    {
        Schema schema = StandardSchemas.TableTypesSchema;
        return new InMemoryStream(schema, new RecordBatch(schema, [Strings([TableType, ViewType])], 2));
    }

    // ---- GetTableSchema ----

    public static Schema GetTableSchema(TdsSession session, string? catalog, string? dbSchema, string tableName)
    {
        string name = new IngestTarget(catalog, dbSchema, tableName, Temporary: false).QuotedName;
        session.SendSqlBatch($"SELECT TOP 0 * FROM {name}");
        TdsColumn[]? columns = session.ReadUntilResultSet();
        if (columns == null)
        {
            throw new AdbcException($"Could not read the columns of {name}", AdbcStatusCode.InternalError);
        }
        session.DrainResponse(columns);
        return new Schema(columns.Select(c =>
        {
            using ColumnDecoder decoder = DecoderFactory.Create(c, 1, session.DefaultCollation);
            return DecoderFactory.CreateField(decoder);
        }), null);
    }

    // ---- GetObjects ----

    private sealed record ColumnInfo(
        string Name, int Ordinal, string TypeName, string BaseType, short MaxLength, byte Precision, byte Scale,
        bool IsNullable, string? Default, bool IsIdentity, bool IsComputed);

    private sealed record ConstraintColumn(string Name, string Type, string? Column, string? RefSchema, string? RefTable, string? RefColumn);

    private sealed class TableInfo(string name, string type)
    {
        public string Name { get; } = name;
        public string Type { get; } = type;
        public List<ColumnInfo> Columns { get; } = new();
        public List<ConstraintColumn> Constraints { get; } = new();
    }

    private sealed class SchemaInfo(string name)
    {
        public string Name { get; } = name;
        public List<TableInfo> Tables { get; } = new();
    }

    private sealed class CatalogInfo(string name)
    {
        public string Name { get; } = name;
        public List<SchemaInfo> Schemas { get; } = new();
    }

    // Errors that mean a listed database cannot be read now (no access, offline, restoring, ...).
    private static readonly HashSet<int> s_inaccessibleDatabaseErrors = [916, 942, 943, 952, 4060, 40892];

    public static IArrowArrayStream GetObjects(
        TdsSession session,
        AdbcConnection.GetObjectsDepth depth,
        string? catalogPattern,
        string? dbSchemaPattern,
        string? tableNamePattern,
        IReadOnlyList<string>? tableTypes,
        string? columnNamePattern)
    {
        var catalogs = new List<CatalogInfo>();
        // An empty pattern matches only objects without a catalog, and every object here has one.
        if (catalogPattern != "")
        {
            foreach (object?[] row in Query(session, $"SELECT name FROM sys.databases WHERE {AccessibleDatabases(session)}{Like("name", catalogPattern)} ORDER BY name")[0])
            {
                catalogs.Add(new CatalogInfo((string)row[0]!));
            }
        }

        if (depth != AdbcConnection.GetObjectsDepth.Catalogs)
        {
            string types = ObjectTypes(tableTypes);
            catalogs.RemoveAll(catalog =>
            {
                try
                {
                    ReadCatalog(session, catalog, depth, dbSchemaPattern, tableNamePattern, types, columnNamePattern);
                    return false;
                }
                catch (SqlServerException ex) when (s_inaccessibleDatabaseErrors.Contains(ex.Number))
                {
                    return true;
                }
            });
        }

        return BuildObjects(catalogs, depth);
    }

    /// <summary>
    /// The <c>sys.databases</c> filter for the databases the catalog functions
    /// read: those the user can open, or on a Synapse dedicated pool (which
    /// cannot query another database) the current one.
    /// </summary>
    internal static string AccessibleDatabases(TdsSession session) =>
        SqlServerEngine.SupportsCrossDatabase(SqlServerEngine.Edition(session)) ? "HAS_DBACCESS(name) = 1" : "name = DB_NAME()";

    /// <summary>
    /// <c>sys</c> for the current database, else <c>[db].sys</c>: a two-part
    /// name where one will do, as engines without cross-database queries need.
    /// </summary>
    internal static string SystemSchema(TdsSession session, string catalog) =>
        string.Equals(catalog, session.Database, StringComparison.Ordinal) ? "sys" : IngestTarget.Quote(catalog) + ".sys";

    /// <summary>The <c>sys.objects</c> type codes for the requested table types.</summary>
    private static string ObjectTypes(IReadOnlyList<string>? tableTypes)
    {
        if (tableTypes == null)
        {
            return "'U', 'V'";
        }
        var codes = new List<string>();
        if (tableTypes.Contains(TableType, StringComparer.OrdinalIgnoreCase))
        {
            codes.Add("'U'");
        }
        if (tableTypes.Contains(ViewType, StringComparer.OrdinalIgnoreCase))
        {
            codes.Add("'V'");
        }
        return codes.Count > 0 ? string.Join(", ", codes) : "NULL";
    }

    private static void ReadCatalog(
        TdsSession session, CatalogInfo catalog, AdbcConnection.GetObjectsDepth depth,
        string? schemaPattern, string? tablePattern, string types, string? columnPattern)
    {
        string sys = SystemSchema(session, catalog.Name);
        string objectFilter = $"o.type IN ({types}){Like("s.name", schemaPattern)}{Like("o.name", tablePattern)}";
        bool tables = depth is AdbcConnection.GetObjectsDepth.Tables or AdbcConnection.GetObjectsDepth.All;
        bool columns = depth == AdbcConnection.GetObjectsDepth.All;

        var sql = new StringBuilder();
        // Fixed database roles own schemas of their own; those are not where objects live.
        sql.Append($"SELECT s.name FROM {sys}.schemas s WHERE s.schema_id NOT BETWEEN 16384 AND 16399{Like("s.name", schemaPattern)} ORDER BY s.name;\n");
        if (tables)
        {
            sql.Append($"""
                SELECT s.name, o.name, o.type FROM {sys}.objects o JOIN {sys}.schemas s ON s.schema_id = o.schema_id
                WHERE {objectFilter} ORDER BY s.name, o.name;

                """);
        }
        if (columns)
        {
            sql.Append($"""
                SELECT s.name, o.name, c.name, c.ordinal, t.name, COALESCE(bt.name, t.name), c.max_length, c.precision, c.scale,
                       c.is_nullable, d.definition, c.is_identity, c.is_computed
                FROM (SELECT *, CAST(ROW_NUMBER() OVER (PARTITION BY object_id ORDER BY column_id) AS int) AS ordinal FROM {sys}.columns) c
                JOIN {sys}.objects o ON o.object_id = c.object_id
                JOIN {sys}.schemas s ON s.schema_id = o.schema_id
                JOIN {sys}.types t ON t.user_type_id = c.user_type_id
                LEFT JOIN {sys}.types bt ON bt.user_type_id = t.system_type_id
                LEFT JOIN {sys}.default_constraints d ON d.object_id = c.default_object_id
                WHERE {objectFilter}{Like("c.name", columnPattern)}
                ORDER BY s.name, o.name, c.ordinal;
                SELECT s.name, o.name, k.name, CASE k.type WHEN 'PK' THEN N'PRIMARY KEY' ELSE N'UNIQUE' END, c.name,
                       CAST(ic.key_ordinal AS int) AS ordinal, CAST(NULL AS sysname), CAST(NULL AS sysname), CAST(NULL AS sysname)
                FROM {sys}.key_constraints k
                JOIN {sys}.objects o ON o.object_id = k.parent_object_id
                JOIN {sys}.schemas s ON s.schema_id = o.schema_id
                JOIN {sys}.index_columns ic ON ic.object_id = k.parent_object_id AND ic.index_id = k.unique_index_id AND ic.key_ordinal > 0
                JOIN {sys}.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE {objectFilter}
                UNION ALL
                SELECT s.name, o.name, f.name, N'FOREIGN KEY', c.name, CAST(fc.constraint_column_id AS int), rs.name, ro.name, rc.name
                FROM {sys}.foreign_keys f
                JOIN {sys}.objects o ON o.object_id = f.parent_object_id
                JOIN {sys}.schemas s ON s.schema_id = o.schema_id
                JOIN {sys}.foreign_key_columns fc ON fc.constraint_object_id = f.object_id
                JOIN {sys}.columns c ON c.object_id = fc.parent_object_id AND c.column_id = fc.parent_column_id
                JOIN {sys}.objects ro ON ro.object_id = fc.referenced_object_id
                JOIN {sys}.schemas rs ON rs.schema_id = ro.schema_id
                JOIN {sys}.columns rc ON rc.object_id = fc.referenced_object_id AND rc.column_id = fc.referenced_column_id
                WHERE {objectFilter}
                UNION ALL
                SELECT s.name, o.name, k.name, N'CHECK', c.name, 1, NULL, NULL, NULL
                FROM {sys}.check_constraints k
                JOIN {sys}.objects o ON o.object_id = k.parent_object_id
                JOIN {sys}.schemas s ON s.schema_id = o.schema_id
                LEFT JOIN {sys}.columns c ON c.object_id = k.parent_object_id AND c.column_id = k.parent_column_id
                WHERE {objectFilter}
                ORDER BY 1, 2, 3, 6;

                """);
        }

        List<List<object?[]>> results = Query(session, sql.ToString());

        var schemas = new Dictionary<string, SchemaInfo>(StringComparer.Ordinal);
        foreach (object?[] row in results[0])
        {
            var schema = new SchemaInfo((string)row[0]!);
            schemas[schema.Name] = schema;
            catalog.Schemas.Add(schema);
        }
        if (!tables)
        {
            return;
        }

        var tablesByName = new Dictionary<(string, string), TableInfo>();
        foreach (object?[] row in results[1])
        {
            if (schemas.TryGetValue((string)row[0]!, out SchemaInfo? schema))
            {
                var table = new TableInfo((string)row[1]!, ((string)row[2]!).Trim() == "U" ? TableType : ViewType);
                schema.Tables.Add(table);
                tablesByName[(schema.Name, table.Name)] = table;
            }
        }
        if (!columns)
        {
            return;
        }

        foreach (object?[] row in results[2])
        {
            if (tablesByName.TryGetValue(((string)row[0]!, (string)row[1]!), out TableInfo? table))
            {
                table.Columns.Add(new ColumnInfo(
                    (string)row[2]!, (int)row[3]!, (string)row[4]!, (string)row[5]!, (short)row[6]!, (byte)row[7]!, (byte)row[8]!,
                    (bool)row[9]!, (string?)row[10], (bool)row[11]!, (bool)row[12]!));
            }
        }
        foreach (object?[] row in results[3])
        {
            if (tablesByName.TryGetValue(((string)row[0]!, (string)row[1]!), out TableInfo? table))
            {
                table.Constraints.Add(new ConstraintColumn((string)row[2]!, (string)row[3]!, (string?)row[4], (string?)row[6], (string?)row[7], (string?)row[8]));
            }
        }
    }

    /// <summary>
    /// <c> AND column LIKE pattern</c>, or nothing for a null pattern. ADBC
    /// patterns use <c>%</c> and <c>_</c> as LIKE does; T-SQL also gives
    /// <c>[</c> a meaning, so it is escaped.
    /// </summary>
    internal static string Like(string column, string? pattern) =>
        pattern == null ? "" : $" AND {column} LIKE {Literal(pattern.Replace("[", "[[]"))}";

    internal static string Literal(string value) => "N'" + value.Replace("'", "''") + "'";

    /// <summary>Runs a batch and returns the rows of each of its result sets.</summary>
    internal static List<List<object?[]>> Query(TdsSession session, string sql)
    {
        var results = new List<List<object?[]>>();
        session.SendSqlBatch(sql);
        try
        {
            ReadResults(session, results);
        }
        catch
        {
            // Between result sets nothing else reads the rest of the response; leave the session usable.
            if (!session.Reader.IsMessageComplete && !session.IsBroken)
            {
                try
                {
                    session.DrainResponse();
                }
                catch (AdbcException)
                {
                }
            }
            throw;
        }
        return results;
    }

    private static void ReadResults(TdsSession session, List<List<object?[]>> results)
    {
        while (session.ReadUntilResultSet() is TdsColumn[] columns)
        {
            var rows = new List<object?[]>();
            using (var stream = new SqlServerQueryStream(session, columns, stopAtResultEnd: true))
            {
                while (stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is RecordBatch batch)
                {
                    using (batch)
                    {
                        for (int r = 0; r < batch.Length; r++)
                        {
                            var row = new object?[batch.ColumnCount];
                            for (int c = 0; c < row.Length; c++)
                            {
                                row[c] = Value(batch.Column(c), r);
                            }
                            rows.Add(row);
                        }
                    }
                }
            }
            results.Add(rows);
        }
    }

    private static IArrowArrayStream BuildObjects(List<CatalogInfo> catalogs, AdbcConnection.GetObjectsDepth depth)
    {
        Schema schema = StandardSchemas.GetObjectsSchema;
        var schemasType = (ListType)schema.FieldsList[1].DataType;
        var tablesType = (ListType)ItemType(schemasType).Fields[1].DataType;
        var columnsType = (ListType)ItemType(tablesType).Fields[2].DataType;
        var constraintsType = (ListType)ItemType(tablesType).Fields[3].DataType;
        var constraintColumnsType = (ListType)ItemType(constraintsType).Fields[2].DataType;
        var usageType = (ListType)ItemType(constraintsType).Fields[3].DataType;

        bool withSchemas = depth != AdbcConnection.GetObjectsDepth.Catalogs;
        bool withTables = depth is AdbcConnection.GetObjectsDepth.Tables or AdbcConnection.GetObjectsDepth.All;
        bool withColumns = depth == AdbcConnection.GetObjectsDepth.All;

        IArrowArray BuildColumns(List<ColumnInfo> columns) => Struct(ItemType(columnsType), columns.Count,
            Strings(columns.Select(c => c.Name)),
            Int32s(columns.Select(c => (int?)c.Ordinal)),
            Strings(columns.Select(_ => (string?)null)),
            Int16s(columns.Select(c => XdbcType(c.BaseType, c.MaxLength))),
            Strings(columns.Select(c => c.TypeName)),
            Int32s(columns.Select(ColumnSize)),
            Int16s(columns.Select(DecimalDigits)),
            Int16s(columns.Select(Radix)),
            Int16s(columns.Select(c => (short?)(c.IsNullable ? 1 : 0))),
            Strings(columns.Select(c => c.Default)),
            Int16s(columns.Select(c => XdbcSqlType(c.BaseType, c.MaxLength))),
            Int16s(columns.Select(c => DatetimeSub(c.BaseType))),
            Int32s(columns.Select(CharOctetLength)),
            Strings(columns.Select(c => c.IsNullable ? "YES" : "NO")),
            Strings(columns.Select(_ => (string?)null)),
            Strings(columns.Select(_ => (string?)null)),
            Strings(columns.Select(_ => (string?)null)),
            Booleans(columns.Select(c => (bool?)c.IsIdentity)),
            Booleans(columns.Select(c => (bool?)c.IsComputed)));

        IArrowArray BuildConstraints(List<(CatalogInfo Catalog, List<ConstraintColumn> Columns)> constraints) => Struct(ItemType(constraintsType), constraints.Count,
            Strings(constraints.Select(c => c.Columns[0].Name)),
            Strings(constraints.Select(c => c.Columns[0].Type)),
            List<string>(constraintColumnsType, constraints.Select(c => (IReadOnlyList<string>?)c.Columns.Where(k => k.Column != null).Select(k => k.Column!).ToList()).ToList(), Strings),
            List<(string Catalog, ConstraintColumn Column)>(usageType,
                constraints.Select(c => (IReadOnlyList<(string, ConstraintColumn)>?)c.Columns.Where(k => k.RefTable != null).Select(k => (c.Catalog.Name, k)).ToList()).ToList(),
                usage => Struct(ItemType(usageType), usage.Count,
                    Strings(usage.Select(u => (string?)u.Catalog)),
                    Strings(usage.Select(u => u.Column.RefSchema)),
                    Strings(usage.Select(u => u.Column.RefTable)),
                    Strings(usage.Select(u => u.Column.RefColumn)))));

        IArrowArray BuildTables(List<(CatalogInfo Catalog, TableInfo Table)> tables) => Struct(ItemType(tablesType), tables.Count,
            Strings(tables.Select(t => t.Table.Name)),
            Strings(tables.Select(t => t.Table.Type)),
            List(columnsType, tables.Select(t => withColumns ? t.Table.Columns : null).ToList<IReadOnlyList<ColumnInfo>?>(), BuildColumns),
            List(constraintsType,
                tables.Select(t => withColumns
                    ? t.Table.Constraints.GroupBy(c => c.Name).Select(g => (t.Catalog, g.ToList())).ToList()
                    : null).ToList<IReadOnlyList<(CatalogInfo, List<ConstraintColumn>)>?>(),
                BuildConstraints));

        IArrowArray BuildSchemas(List<(CatalogInfo Catalog, SchemaInfo Schema)> schemas) => Struct(ItemType(schemasType), schemas.Count,
            Strings(schemas.Select(s => s.Schema.Name)),
            List(tablesType, schemas.Select(s => withTables ? s.Schema.Tables.Select(t => (s.Catalog, t)).ToList() : null).ToList<IReadOnlyList<(CatalogInfo, TableInfo)>?>(), BuildTables));

        IArrowArray[] arrays =
        [
            Strings(catalogs.Select(c => c.Name)),
            List(schemasType, catalogs.Select(c => withSchemas ? c.Schemas.Select(s => (c, s)).ToList() : null).ToList<IReadOnlyList<(CatalogInfo, SchemaInfo)>?>(), BuildSchemas),
        ];
        return new InMemoryStream(schema, new RecordBatch(schema, arrays, catalogs.Count));
    }

    // ---- XDBC (ODBC) column attributes ----

    private static bool IsMax(ColumnInfo c) => c.MaxLength == -1;

    private static short? XdbcType(string type, short maxLength) => type switch
    {
        "bit" => -7,
        "tinyint" => -6,
        "smallint" => 5,
        "int" => 4,
        "bigint" => -5,
        "real" => 7,
        "float" => 6,
        "decimal" or "money" or "smallmoney" => 3,
        "numeric" => 2,
        "char" => 1,
        "varchar" => (short)(maxLength == -1 ? -1 : 12),
        "text" => -1,
        "nchar" => -8,
        "nvarchar" => (short)(maxLength == -1 ? -10 : -9),
        "ntext" => -10,
        "binary" or "timestamp" => -2,
        "varbinary" => (short)(maxLength == -1 ? -4 : -3),
        "image" => -4,
        "date" => 91,
        "time" => -154,
        "datetime" or "datetime2" or "smalldatetime" => 93,
        "datetimeoffset" => -155,
        "uniqueidentifier" => -11,
        "xml" => -152,
        "sql_variant" => -150,
        "hierarchyid" or "geometry" or "geography" => -151,
        _ => null,
    };

    /// <summary>The verbose type: SQL_DATETIME for the date and time types, otherwise the concise type.</summary>
    private static short? XdbcSqlType(string type, short maxLength) =>
        type is "date" or "datetime" or "datetime2" or "smalldatetime" ? (short)9 : XdbcType(type, maxLength);

    private static short? DatetimeSub(string type) => type switch
    {
        "date" => 1,
        "datetime" or "datetime2" or "smalldatetime" => 3,
        _ => null,
    };

    private static bool IsCharacter(string type) => type is "char" or "varchar" or "text" or "nchar" or "nvarchar" or "ntext" or "xml";

    private static bool IsBinary(string type) => type is "binary" or "varbinary" or "image" or "timestamp";

    private static bool IsUnicode(string type) => type is "nchar" or "nvarchar" or "ntext" or "xml";

    private static int? ColumnSize(ColumnInfo c)
    {
        if (IsCharacter(c.BaseType) || IsBinary(c.BaseType))
        {
            if (IsMax(c) || c.BaseType is "text" or "ntext" or "image" or "xml")
            {
                return IsUnicode(c.BaseType) ? 1073741823 : int.MaxValue;
            }
            return IsUnicode(c.BaseType) ? c.MaxLength / 2 : c.MaxLength;
        }
        if (c.BaseType == "uniqueidentifier")
        {
            return 36;
        }
        return c.Precision > 0 ? c.Precision : null;
    }

    private static int? CharOctetLength(ColumnInfo c)
    {
        if (!IsCharacter(c.BaseType) && !IsBinary(c.BaseType))
        {
            return null;
        }
        return IsMax(c) || c.BaseType is "text" or "ntext" or "image" or "xml" ? int.MaxValue : c.MaxLength;
    }

    private static short? DecimalDigits(ColumnInfo c) => c.BaseType switch
    {
        "decimal" or "numeric" or "money" or "smallmoney" or "datetime" or "datetime2" or "smalldatetime" or "time" or "datetimeoffset" => c.Scale,
        "bit" or "tinyint" or "smallint" or "int" or "bigint" or "date" => 0,
        _ => null,
    };

    private static short? Radix(ColumnInfo c) => c.BaseType switch
    {
        "real" or "float" => 2,
        "tinyint" or "smallint" or "int" or "bigint" or "decimal" or "numeric" or "money" or "smallmoney" => 10,
        _ => null,
    };
}
