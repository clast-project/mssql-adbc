// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Adbc.Extensions;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Xunit;

namespace Clast.Drivers.SqlServer.Tests;

/// <summary>
/// GetInfo, GetTableTypes, GetTableSchema and GetObjects, against tables
/// created in tempdb for the test class.
/// </summary>
[Collection(CatalogCollection.Name)]
public sealed class MetadataTests : IDisposable
{
    private readonly string _prefix = "clast_meta_" + Guid.NewGuid().ToString("N").Substring(0, 8) + "_";
    private readonly AdbcConnection _connection;

    public MetadataTests()
    {
        SqlServerTestConfiguration config = SqlServerTestConfiguration.Load();
        _connection = new SqlServerDriver().Open(config.Options).Connect(null);
        Execute($"""
            CREATE TABLE tempdb.dbo.{_prefix}parent (
                id int IDENTITY NOT NULL CONSTRAINT {_prefix}pk PRIMARY KEY,
                code nvarchar(20) NULL CONSTRAINT {_prefix}uq UNIQUE,
                amount decimal(10, 2) NOT NULL CONSTRAINT {_prefix}df DEFAULT 0,
                CONSTRAINT {_prefix}ck CHECK (amount >= 0),
                CONSTRAINT {_prefix}ck2 CHECK (id > 0 OR amount > 0));
            CREATE TABLE tempdb.dbo.{_prefix}child (
                parent_id int NOT NULL CONSTRAINT {_prefix}fk REFERENCES tempdb.dbo.{_prefix}parent (id),
                note varchar(max) NULL,
                at datetime2(3) NULL,
                doubled AS parent_id * 2);
            EXEC tempdb.sys.sp_executesql N'CREATE VIEW dbo.{_prefix}view AS SELECT id, code FROM dbo.{_prefix}parent';
            """);
    }

    public void Dispose()
    {
        try
        {
            Execute($"""
                EXEC tempdb.sys.sp_executesql N'DROP VIEW IF EXISTS dbo.{_prefix}view';
                DROP TABLE IF EXISTS tempdb.dbo.{_prefix}child;
                DROP TABLE IF EXISTS tempdb.dbo.{_prefix}parent;
                """);
        }
        finally
        {
            _connection.Dispose();
        }
    }

    private void Execute(string sql)
    {
        using AdbcStatement statement = _connection.CreateStatement();
        statement.SqlQuery = sql;
        statement.ExecuteUpdate();
    }

    private static RecordBatch ReadSingle(IArrowArrayStream stream)
    {
        using (stream)
        {
            RecordBatch batch = stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult()!;
            Assert.Null(stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult());
            return batch;
        }
    }

    private static StructArray Item(ListArray list, int index)
    {
        Assert.False(list.IsNull(index));
        return (StructArray)list.GetSlicedValues(index);
    }

    private static string Str(StructArray items, string field, int index) =>
        ((StringArray)items.Fields[((StructType)items.Data.DataType).GetFieldIndex(field)]).GetString(index);

    private static IArrowArray Field(StructArray items, string field) =>
        items.Fields[((StructType)items.Data.DataType).GetFieldIndex(field)];

    private static int IndexOf(StructArray items, string field, string value)
    {
        for (int i = 0; i < items.Length; i++)
        {
            if (Str(items, field, i) == value)
            {
                return i;
            }
        }
        throw new Xunit.Sdk.XunitException($"No {field} '{value}'");
    }

    [SkippableFact]
    public void GetInfoReturnsDriverAndVendor()
    {
        using RecordBatch batch = ReadSingle(_connection.GetInfo([]));
        Assert.Equal(StandardSchemas.GetInfoSchema.FieldsList.Count, batch.Schema.FieldsList.Count);
        var names = (UInt32Array)batch.Column(0);
        var values = (DenseUnionArray)batch.Column(1);

        var info = new Dictionary<AdbcInfoCode, object?>();
        for (int i = 0; i < batch.Length; i++)
        {
            int child = values.TypeIds[i];
            int offset = values.ValueOffsets[i];
            info[(AdbcInfoCode)names.GetValue(i)!.Value] = values.Fields[child] switch
            {
                StringArray s => s.GetString(offset),
                BooleanArray b => b.GetValue(offset),
                Int64Array l => l.GetValue(offset),
                _ => throw new InvalidOperationException(),
            };
        }

        Assert.Contains("SQL Server", (string)info[AdbcInfoCode.VendorName]!);
        Assert.Matches(@"^\d+\.\d+\.\d+$", (string)info[AdbcInfoCode.VendorVersion]!);
        Assert.Equal(true, info[AdbcInfoCode.VendorSql]);
        Assert.Equal(false, info[AdbcInfoCode.VendorSubstrait]);
        Assert.Equal(SqlServerMetadata.DriverName, info[AdbcInfoCode.DriverName]);
        Assert.False(string.IsNullOrEmpty((string)info[AdbcInfoCode.DriverVersion]!));
        Assert.StartsWith("23.", (string)info[AdbcInfoCode.DriverArrowVersion]!);
        Assert.Equal((long)AdbcVersion.Version_1_1_0, info[AdbcInfoCode.DriverAdbcVersion]);
    }

    [SkippableFact]
    public void GetInfoReturnsRequestedCodesAndSkipsUnknownOnes()
    {
        using RecordBatch batch = ReadSingle(_connection.GetInfo([AdbcInfoCode.DriverName, (AdbcInfoCode)9999, AdbcInfoCode.VendorSql]));
        var names = (UInt32Array)batch.Column(0);
        Assert.Equal([(uint)AdbcInfoCode.DriverName, (uint)AdbcInfoCode.VendorSql], names.Values.ToArray());
    }

    [SkippableFact]
    public void GetTableTypes()
    {
        using RecordBatch batch = ReadSingle(_connection.GetTableTypes());
        var types = (StringArray)batch.Column(0);
        Assert.Equal(["TABLE", "VIEW"], Enumerable.Range(0, types.Length).Select(i => types.GetString(i)));
    }

    [SkippableFact]
    public void GetTableSchema()
    {
        Schema schema = _connection.GetTableSchema("tempdb", "dbo", _prefix + "parent");
        Assert.Equal(["id", "code", "amount"], schema.FieldsList.Select(f => f.Name));
        Assert.IsType<Int32Type>(schema.FieldsList[0].DataType);
        Assert.False(schema.FieldsList[0].IsNullable);
        Assert.IsType<StringType>(schema.FieldsList[1].DataType);
        Assert.True(schema.FieldsList[1].IsNullable);
        var amount = Assert.IsType<Decimal128Type>(schema.FieldsList[2].DataType);
        Assert.Equal((10, 2), (amount.Precision, amount.Scale));

        Assert.ThrowsAny<AdbcException>(() => _connection.GetTableSchema("tempdb", "dbo", _prefix + "missing"));
    }

    [SkippableFact]
    public void GetObjectsCatalogs()
    {
        using RecordBatch batch = ReadSingle(_connection.GetObjects(AdbcConnection.GetObjectsDepth.Catalogs, null, null, null, null, null));
        StandardSchemas.GetObjectsSchema.Validate(batch.Arrays.ToList());
        var catalogs = (StringArray)batch.Column(0);
        var names = Enumerable.Range(0, catalogs.Length).Select(i => catalogs.GetString(i)).ToList();
        Assert.Contains("master", names);
        Assert.Contains("tempdb", names);
        Assert.Equal(batch.Length, batch.Column(1).NullCount);

        using RecordBatch none = ReadSingle(_connection.GetObjects(AdbcConnection.GetObjectsDepth.Catalogs, "", null, null, null, null));
        Assert.Equal(0, none.Length);

        using RecordBatch pattern = ReadSingle(_connection.GetObjects(AdbcConnection.GetObjectsDepth.Catalogs, "temp%", null, null, null, null));
        Assert.Equal("tempdb", ((StringArray)pattern.Column(0)).GetString(0));
    }

    [SkippableFact]
    public void GetObjectsSchemasLeavesOutRoleSchemas()
    {
        using RecordBatch batch = ReadSingle(_connection.GetObjects(AdbcConnection.GetObjectsDepth.DbSchemas, "tempdb", null, null, null, null));
        StructArray schemas = Item((ListArray)batch.Column(1), 0);
        var names = Enumerable.Range(0, schemas.Length).Select(i => Str(schemas, "db_schema_name", i)).ToList();
        Assert.Contains("dbo", names);
        Assert.Contains("sys", names);
        Assert.DoesNotContain("db_owner", names);
        Assert.Equal(schemas.Length, Field(schemas, "db_schema_tables").NullCount);
    }

    [SkippableFact]
    public void GetObjectsTablesFiltersByType()
    {
        using RecordBatch batch = ReadSingle(_connection.GetObjects(AdbcConnection.GetObjectsDepth.Tables, "tempdb", "dbo", _prefix + "%", ["VIEW"], null));
        StructArray schemas = Item((ListArray)batch.Column(1), 0);
        StructArray tables = Item((ListArray)Field(schemas, "db_schema_tables"), IndexOf(schemas, "db_schema_name", "dbo"));
        Assert.Equal(1, tables.Length);
        Assert.Equal(_prefix + "view", Str(tables, "table_name", 0));
        Assert.Equal("VIEW", Str(tables, "table_type", 0));
        Assert.True(Field(tables, "table_columns").IsNull(0));
    }

    [SkippableFact]
    public void GetObjectsAll()
    {
        using RecordBatch batch = ReadSingle(_connection.GetObjects(AdbcConnection.GetObjectsDepth.All, "tempdb", "dbo", _prefix + "%", null, null));
        StandardSchemas.GetObjectsSchema.Validate(batch.Arrays.ToList());
        Assert.Equal(1, batch.Length);
        StructArray schemas = Item((ListArray)batch.Column(1), 0);
        StructArray tables = Item((ListArray)Field(schemas, "db_schema_tables"), IndexOf(schemas, "db_schema_name", "dbo"));
        Assert.Equal(3, tables.Length);
        int parent = IndexOf(tables, "table_name", _prefix + "parent");
        int child = IndexOf(tables, "table_name", _prefix + "child");
        Assert.Equal("TABLE", Str(tables, "table_type", parent));

        // Columns of the parent table.
        StructArray columns = Item((ListArray)Field(tables, "table_columns"), parent);
        Assert.Equal(3, columns.Length);
        Assert.Equal(["id", "code", "amount"], Enumerable.Range(0, 3).Select(i => Str(columns, "column_name", i)));
        Assert.Equal([1, 2, 3], ((Int32Array)Field(columns, "ordinal_position")).Values.ToArray());
        Assert.Equal([4, -9, 3], ((Int16Array)Field(columns, "xdbc_data_type")).Values.ToArray());
        Assert.Equal(["int", "nvarchar", "decimal"], Enumerable.Range(0, 3).Select(i => Str(columns, "xdbc_type_name", i)));
        Assert.Equal([10, 20, 10], ((Int32Array)Field(columns, "xdbc_column_size")).Values.ToArray());
        Assert.Equal((short)2, ((Int16Array)Field(columns, "xdbc_decimal_digits")).GetValue(2));
        Assert.Equal([0, 1, 0], ((Int16Array)Field(columns, "xdbc_nullable")).Values.ToArray());
        Assert.Equal(["NO", "YES", "NO"], Enumerable.Range(0, 3).Select(i => Str(columns, "xdbc_is_nullable", i)));
        Assert.Equal("((0))", Str(columns, "xdbc_column_def", 2));
        Assert.Equal([true, false, false], Enumerable.Range(0, 3).Select(i => ((BooleanArray)Field(columns, "xdbc_is_autoincrement")).GetValue(i)!.Value));
        Assert.Equal(40, ((Int32Array)Field(columns, "xdbc_char_octet_length")).GetValue(1));

        // Columns of the child table: varchar(max), datetime2(3) and a computed column.
        StructArray childColumns = Item((ListArray)Field(tables, "table_columns"), child);
        Assert.Equal((short)-1, ((Int16Array)Field(childColumns, "xdbc_data_type")).GetValue(1));
        Assert.Equal(int.MaxValue, ((Int32Array)Field(childColumns, "xdbc_column_size")).GetValue(1));
        Assert.Equal((short)93, ((Int16Array)Field(childColumns, "xdbc_data_type")).GetValue(2));
        Assert.Equal((short)9, ((Int16Array)Field(childColumns, "xdbc_sql_data_type")).GetValue(2));
        Assert.Equal((short)3, ((Int16Array)Field(childColumns, "xdbc_datetime_sub")).GetValue(2));
        Assert.Equal((short)3, ((Int16Array)Field(childColumns, "xdbc_decimal_digits")).GetValue(2));
        Assert.True(((BooleanArray)Field(childColumns, "xdbc_is_generatedcolumn")).GetValue(3));

        // Constraints.
        StructArray constraints = Item((ListArray)Field(tables, "table_constraints"), parent);
        var byName = Enumerable.Range(0, constraints.Length).ToDictionary(i => Str(constraints, "constraint_name", i));
        Assert.Equal(["PRIMARY KEY", "UNIQUE", "CHECK"], new[] { "pk", "uq", "ck" }.Select(n => Str(constraints, "constraint_type", byName[_prefix + n])));
        var keyColumns = (ListArray)Field(constraints, "constraint_column_names");
        Assert.Equal("id", ((StringArray)keyColumns.GetSlicedValues(byName[_prefix + "pk"])).GetString(0));
        // A CHECK on one column is reported against it; one on several columns has none.
        Assert.Equal("amount", ((StringArray)keyColumns.GetSlicedValues(byName[_prefix + "ck"])).GetString(0));
        Assert.Equal(0, keyColumns.GetSlicedValues(byName[_prefix + "ck2"]).Length);

        StructArray childConstraints = Item((ListArray)Field(tables, "table_constraints"), child);
        Assert.Equal(1, childConstraints.Length);
        Assert.Equal("FOREIGN KEY", Str(childConstraints, "constraint_type", 0));
        Assert.Equal("parent_id", ((StringArray)((ListArray)Field(childConstraints, "constraint_column_names")).GetSlicedValues(0)).GetString(0));
        StructArray usage = Item((ListArray)Field(childConstraints, "constraint_column_usage"), 0);
        Assert.Equal(("tempdb", "dbo", _prefix + "parent", "id"),
            (Str(usage, "fk_catalog", 0), Str(usage, "fk_db_schema", 0), Str(usage, "fk_table", 0), Str(usage, "fk_column_name", 0)));
    }

    [SkippableFact]
    public void GetObjectsAllWithoutFilters()
    {
        using RecordBatch batch = ReadSingle(_connection.GetObjects(AdbcConnection.GetObjectsDepth.All, null, null, null, null, null));
        StandardSchemas.GetObjectsSchema.Validate(batch.Arrays.ToList());
        var catalogs = (StringArray)batch.Column(0);
        int tempdb = Enumerable.Range(0, batch.Length).Single(i => catalogs.GetString(i) == "tempdb");
        StructArray schemas = Item((ListArray)batch.Column(1), tempdb);
        StructArray tables = Item((ListArray)Field(schemas, "db_schema_tables"), IndexOf(schemas, "db_schema_name", "dbo"));
        IndexOf(tables, "table_name", _prefix + "child");
    }

    [SkippableFact]
    public void FailedMetadataQueryLeavesSessionUsable()
    {
        // The second result set has a type the metadata reader does not convert, which fails between result sets.
        var session = ((SqlServerConnection)_connection).Session;
        Assert.Throws<NotSupportedException>(() => SqlServerMetadata.Query(session, "SELECT 1; SELECT CAST(1 AS decimal(5, 1)); SELECT 2"));
        Assert.Equal(2, (int)SqlServerMetadata.Query(session, "SELECT 2")[0][0][0]!);
    }

    [SkippableFact]
    public void GetObjectsFiltersColumns()
    {
        using RecordBatch batch = ReadSingle(_connection.GetObjects(AdbcConnection.GetObjectsDepth.All, "tempdb", "dbo", _prefix + "parent", null, "co%"));
        StructArray schemas = Item((ListArray)batch.Column(1), 0);
        StructArray tables = Item((ListArray)Field(schemas, "db_schema_tables"), IndexOf(schemas, "db_schema_name", "dbo"));
        StructArray columns = Item((ListArray)Field(tables, "table_columns"), 0);
        Assert.Equal(1, columns.Length);
        Assert.Equal("code", Str(columns, "column_name", 0));
        Assert.Equal(2, ((Int32Array)Field(columns, "ordinal_position")).GetValue(0));
    }
}
