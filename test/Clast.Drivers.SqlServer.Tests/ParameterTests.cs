// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Arrays;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Xunit;

namespace Clast.Drivers.SqlServer.Tests;

/// <summary>Bound parameters, ExecuteSchema and GetParameterSchema.</summary>
public sealed class ParameterTests : IDisposable
{
    private readonly AdbcConnection _connection;

    public ParameterTests()
    {
        SqlServerTestConfiguration config = SqlServerTestConfiguration.Load();
        _connection = new SqlServerDriver().Open(config.Options).Connect(null);
    }

    public void Dispose() => _connection.Dispose();

    private void Execute(string sql)
    {
        using AdbcStatement statement = _connection.CreateStatement();
        statement.SqlQuery = sql;
        statement.ExecuteUpdate();
    }

    private static RecordBatch Batch(params (string Name, IArrowArray Array)[] columns)
    {
        var schema = new Schema(columns.Select(c => new Field(c.Name, c.Array.Data.DataType, true)), null);
        return new RecordBatch(schema, columns.Select(c => c.Array), columns[0].Array.Length);
    }

    private static Int32Array Ints(params int?[] values)
    {
        var builder = new Int32Array.Builder();
        foreach (int? value in values)
        {
            builder.Append(value);
        }
        return builder.Build();
    }

    private static StringArray Strings(params string?[] values)
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

    private List<RecordBatch> Query(AdbcStatement statement)
    {
        var batches = new List<RecordBatch>();
        using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
        while (stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is RecordBatch batch)
        {
            batches.Add(batch);
        }
        return batches;
    }

    private List<int?> IntColumn(string sql, int column = 0)
    {
        using AdbcStatement statement = _connection.CreateStatement();
        statement.SqlQuery = sql;
        var values = new List<int?>();
        foreach (RecordBatch batch in Query(statement))
        {
            using (batch)
            {
                var array = (Int32Array)batch.Column(column);
                for (int i = 0; i < batch.Length; i++)
                {
                    values.Add(array.GetValue(i));
                }
            }
        }
        return values;
    }

    [SkippableFact]
    public void InsertsEachBoundRow()
    {
        Execute("CREATE TABLE #p (id int PRIMARY KEY, name nvarchar(50) NULL)");
        using (AdbcStatement insert = _connection.CreateStatement())
        {
            insert.SqlQuery = "INSERT INTO #p (id, name) VALUES (@id, @name)";
            RecordBatch batch = Batch(("id", Ints(1, 2, 3)), ("name", Strings("one", null, "thrée")));
            insert.Bind(batch, batch.Schema);
            Assert.Equal(3, insert.ExecuteUpdate().AffectedRows);
        }
        Assert.Equal([1, 2, 3], IntColumn("SELECT id FROM #p ORDER BY id"));
        Assert.Equal([1], IntColumn("SELECT COUNT(*) FROM #p WHERE name IS NULL"));
        Assert.Equal([3], IntColumn("SELECT id FROM #p WHERE name = N'thrée'"));
    }

    [SkippableFact]
    public void UnnamedFieldsBindPositionally()
    {
        using AdbcStatement statement = _connection.CreateStatement();
        statement.SqlQuery = "SELECT @p1 + @p2 AS total";
        RecordBatch batch = Batch(("", Ints(40)), ("", Ints(2)));
        statement.Bind(batch, batch.Schema);
        List<RecordBatch> result = Query(statement);
        Assert.Equal(42, ((Int32Array)result.Single().Column(0)).GetValue(0));
        result.ForEach(b => b.Dispose());
    }

    [SkippableFact]
    public void ManyRowsAreSentInSeveralMessages()
    {
        Execute("CREATE TABLE #many (id int NOT NULL, label nvarchar(max) NULL)");
        const int Count = 5000;
        var ids = Ints(Enumerable.Range(0, Count).Select(i => (int?)i).ToArray());
        var labels = Strings(Enumerable.Range(0, Count).Select(i => i % 1000 == 0 ? new string('x', 5000) : "label " + i).ToArray());
        RecordBatch batch = Batch(("id", ids), ("label", labels));
        using (AdbcStatement insert = _connection.CreateStatement())
        {
            insert.SqlQuery = "INSERT INTO #many VALUES (@id, @label)";
            insert.Bind(batch, batch.Schema);
            Assert.Equal(Count, insert.ExecuteUpdate().AffectedRows);
        }
        Assert.Equal([Count], IntColumn("SELECT COUNT(*) FROM #many"));
        Assert.Equal([5], IntColumn("SELECT COUNT(*) FROM #many WHERE LEN(label) = 5000"));
        Assert.Equal([Count * (Count - 1) / 2], IntColumn("SELECT SUM(id) FROM #many"));
    }

    [SkippableFact]
    public void LargeOutputFromBatchedRowsDoesNotStall()
    {
        // Each call sends 8 KB and returns 16 KB, so request messages reach their size limit and the
        // responses are far larger than the transport buffers.
        Execute("CREATE TABLE #out (id int NOT NULL, label nvarchar(max) NULL)");
        const int Count = 1000;
        RecordBatch batch = Batch(
            ("id", Ints(Enumerable.Range(0, Count).Select(i => (int?)i).ToArray())),
            ("pad", Strings(Enumerable.Range(0, Count).Select(_ => new string('p', 4000)).ToArray())));
        using AdbcStatement insert = _connection.CreateStatement();
        insert.SqlQuery = "INSERT INTO #out OUTPUT inserted.label VALUES (@id, @pad + REPLICATE(CAST(N'y' AS nvarchar(max)), 4000))";
        insert.Bind(batch, batch.Schema);
        Task<UpdateResult> run = Task.Run(() => insert.ExecuteUpdate());
        Assert.True(run.Wait(TimeSpan.FromSeconds(60)), "ExecuteUpdate stalled");
        Assert.Equal([Count], IntColumn("SELECT COUNT(*) FROM #out"));
    }

    [SkippableFact]
    public void QueryResultsOfEachRowAreConcatenated()
    {
        using AdbcStatement statement = _connection.CreateStatement();
        statement.SqlQuery = "SELECT v.n * @factor AS n FROM (VALUES (1), (2)) AS v(n) WHERE @factor IS NOT NULL OR v.n = 99";
        RecordBatch batch = Batch(("factor", Ints(1, null, 10, 100)));
        statement.BindStream(new BatchStream(batch));
        List<RecordBatch> result = Query(statement);
        Assert.Equal([1, 2, 10, 20, 100, 200], result.SelectMany(b => ((Int32Array)b.Column(0)).Values.ToArray()));
        result.ForEach(b => b.Dispose());
    }

    [SkippableFact]
    public void ValuesOfEachTypeRoundTrip()
    {
        var timestamp = new TimestampArray.Builder(new TimestampType(TimeUnit.Microsecond, (string?)null))
            .Append(new DateTimeOffset(2024, 2, 29, 13, 14, 15, TimeSpan.Zero).AddTicks(1234560)).AppendNull().Build();
        var zoned = new TimestampArray.Builder(new TimestampType(TimeUnit.Microsecond, "UTC"))
            .Append(new DateTimeOffset(1999, 12, 31, 23, 59, 59, TimeSpan.Zero)).AppendNull().Build();
        var date = new Date32Array.Builder().Append(new DateTime(1900, 1, 1)).AppendNull().Build();
        var time = new Time64Array.Builder(new Time64Type(TimeUnit.Nanosecond)).Append(45_296_789_000_000L).AppendNull().Build();
        var guid = new GuidArray.Builder().Append(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff")).AppendNull().Build();
        var dec = new Decimal128Array.Builder(new Decimal128Type(10, 2)).Append(-12345678.91m).AppendNull().Build();
        var binary = new BinaryArray.Builder().Append([1, 2, 3]).AppendNull().Build();
        string longText = string.Concat(Enumerable.Repeat("long ", 2000));

        RecordBatch batch = Batch(
            ("b", new BooleanArray.Builder().Append(true).AppendNull().Build()),
            ("u8", new UInt8Array.Builder().Append(255).AppendNull().Build()),
            ("i8", new Int8Array.Builder().Append(-128).AppendNull().Build()),
            ("i16", new Int16Array.Builder().Append(short.MinValue).AppendNull().Build()),
            ("i32", Ints(int.MaxValue, null)),
            ("i64", new Int64Array.Builder().Append(long.MinValue).AppendNull().Build()),
            ("f32", new FloatArray.Builder().Append(1.5f).AppendNull().Build()),
            ("f64", new DoubleArray.Builder().Append(Math.PI).AppendNull().Build()),
            ("dec", dec),
            ("s", Strings("héllo", null)),
            ("long", Strings(longText, null)),
            ("bin", binary),
            ("g", guid),
            ("d", date),
            ("ts", timestamp),
            ("tz", zoned),
            ("t", time));

        using AdbcStatement statement = _connection.CreateStatement();
        statement.SqlQuery = "SELECT @b AS b, @u8 AS u8, @i8 AS i8, @i16 AS i16, @i32 AS i32, @i64 AS i64, @f32 AS f32, @f64 AS f64, " +
            "@dec AS dec, @s AS s, @long AS long, @bin AS bin, @g AS g, @d AS d, @ts AS ts, @tz AS tz, @t AS t";
        statement.Bind(batch, batch.Schema);
        List<RecordBatch> result = Query(statement);
        Assert.Equal(2, result.Sum(b => b.Length));
        RecordBatch first = result[0];

        Assert.True(((BooleanArray)first.Column("b")).GetValue(0));
        Assert.Equal((byte)255, ((UInt8Array)first.Column("u8")).GetValue(0));
        Assert.Equal((short)-128, ((Int16Array)first.Column("i8")).GetValue(0));
        Assert.Equal(short.MinValue, ((Int16Array)first.Column("i16")).GetValue(0));
        Assert.Equal(int.MaxValue, ((Int32Array)first.Column("i32")).GetValue(0));
        Assert.Equal(long.MinValue, ((Int64Array)first.Column("i64")).GetValue(0));
        Assert.Equal(1.5f, ((FloatArray)first.Column("f32")).GetValue(0));
        Assert.Equal(Math.PI, ((DoubleArray)first.Column("f64")).GetValue(0));
        Assert.Equal(-12345678.91m, ((Decimal128Array)first.Column("dec")).GetValue(0));
        Assert.Equal("héllo", ((StringArray)first.Column("s")).GetString(0));
        Assert.Equal(longText, ((StringArray)first.Column("long")).GetString(0));
        Assert.Equal([1, 2, 3], ((BinaryArray)first.Column("bin")).GetBytes(0).ToArray());
        Assert.Equal(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), ((GuidArray)first.Column("g")).GetGuid(0));
        Assert.Equal(new DateTime(1900, 1, 1), ((Date32Array)first.Column("d")).GetDateTime(0));
        Assert.Equal(timestamp.GetTimestamp(0), ((TimestampArray)first.Column("ts")).GetTimestamp(0));
        Assert.Equal(45_296_789_000_000L, ((Time64Array)first.Column("t")).GetValue(0));

        // Every column of the second row is NULL.
        Assert.All(Enumerable.Range(0, first.ColumnCount), c => Assert.True(result.Last().Column(c).IsNull(result.Last().Length - 1)));
        result.ForEach(b => b.Dispose());
    }

    [SkippableFact]
    public void ExecuteSchemaWithAndWithoutParameters()
    {
        using AdbcStatement statement = _connection.CreateStatement();
        statement.SqlQuery = "SELECT CAST(1 AS bigint) AS a, N'x' AS b";
        Schema plain = statement.ExecuteSchema();
        Assert.Equal(["a", "b"], plain.FieldsList.Select(f => f.Name));
        Assert.IsType<Int64Type>(plain.FieldsList[0].DataType);

        // Parameter types inferred by the server.
        statement.SqlQuery = "SELECT name, object_id FROM sys.objects WHERE object_id = @id";
        Schema inferred = statement.ExecuteSchema();
        Assert.Equal(["name", "object_id"], inferred.FieldsList.Select(f => f.Name));

        // Parameter types from the bound data.
        statement.SqlQuery = "SELECT @v AS v";
        RecordBatch batch = Batch(("v", new DoubleArray.Builder().Append(1).Build()));
        statement.Bind(batch, batch.Schema);
        Assert.IsType<DoubleType>(statement.ExecuteSchema().FieldsList[0].DataType);
    }

    [SkippableFact]
    public void ExecuteSchemaReportsNullabilityAndNativeTypes()
    {
        using AdbcStatement statement = _connection.CreateStatement();
        statement.SqlQuery = "SELECT 1, CAST(NULL AS int) AS n, CAST(N'{}' AS json) AS j, CAST('[1, 2, 3]' AS vector(3)) AS v, CAST(1.5 AS decimal(9, 2)) AS d";
        Schema schema = statement.ExecuteSchema();
        Assert.Equal(["", "n", "j", "v", "d"], schema.FieldsList.Select(f => f.Name));
        Assert.False(schema.FieldsList[0].IsNullable);
        Assert.True(schema.FieldsList[1].IsNullable);
        Assert.IsType<JsonType>(schema.FieldsList[2].DataType);
        Assert.Equal(3, Assert.IsType<FixedSizeListType>(schema.FieldsList[3].DataType).ListSize);
        var decimalType = Assert.IsType<Decimal128Type>(schema.FieldsList[4].DataType);
        Assert.Equal((9, 2), (decimalType.Precision, decimalType.Scale));
    }

    [SkippableTheory]
    [InlineData("IF 1 = 1 SELECT 1 AS a ELSE SELECT 'x' AS b, 2 AS c", 11509)]
    [InlineData("DECLARE @s nvarchar(100) = N'SELECT 1 AS a'; EXEC(@s)", 11513)]
    [InlineData("SELECT * FROM clast_no_such_table", 208)]
    public void ExecuteSchemaFailsWhenTheShapeIsUnknown(string sql, int error)
    {
        using AdbcStatement statement = _connection.CreateStatement();
        statement.SqlQuery = sql;
        Assert.Equal(error, Assert.Throws<SqlServerException>(() => statement.ExecuteSchema()).Number);

        // The session is still usable.
        statement.SqlQuery = "DECLARE @s nvarchar(100) = N'SELECT 1 AS a'; EXEC(@s) WITH RESULT SETS ((a bigint NOT NULL))";
        Field field = Assert.Single(statement.ExecuteSchema().FieldsList);
        Assert.IsType<Int64Type>(field.DataType);
        Assert.False(field.IsNullable);
    }

    [SkippableFact]
    public void ExecuteSchemaDoesNotRunAProcedureThatTurnsFormatOnlyOff()
    {
        Execute("CREATE PROCEDURE #p AS BEGIN SET FMTONLY OFF; SET CONTEXT_INFO 0x01; SELECT 1 AS n; END");
        using AdbcStatement statement = _connection.CreateStatement();
        statement.SqlQuery = "EXEC #p";
        Assert.Equal("n", Assert.Single(statement.ExecuteSchema().FieldsList).Name);

        statement.SqlQuery = "SELECT CAST(CASE WHEN CONTEXT_INFO() IS NULL THEN 0 ELSE 1 END AS int)";
        using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
        using RecordBatch batch = stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult()!;
        Assert.Equal(0, ((Int32Array)batch.Column(0)).GetValue(0));
    }

    [SkippableFact]
    public void GetParameterSchemaDescribesParameters()
    {
        using AdbcStatement statement = _connection.CreateStatement();
        statement.SqlQuery = "SELECT * FROM sys.objects WHERE object_id = @id AND name = @name";
        statement.Prepare();
        Schema schema = statement.GetParameterSchema();
        Assert.Equal(["id", "name"], schema.FieldsList.Select(f => f.Name));
        Assert.IsType<Int32Type>(schema.FieldsList[0].DataType);
        Assert.IsType<StringType>(schema.FieldsList[1].DataType);

        statement.SqlQuery = "SELECT 1";
        Assert.Empty(statement.GetParameterSchema().FieldsList);
    }

    [SkippableFact]
    public void ErrorInABoundRowIsThrown()
    {
        Execute("CREATE TABLE #pk (id int PRIMARY KEY)");
        using (AdbcStatement insert = _connection.CreateStatement())
        {
            insert.SqlQuery = "INSERT INTO #pk VALUES (@id)";
            RecordBatch batch = Batch(("id", Ints(1, 2, 2, 3)));
            insert.Bind(batch, batch.Schema);
            var ex = Assert.Throws<SqlServerException>(() => insert.ExecuteUpdate());
            Assert.Equal(2627, ex.Number);
        }
        // Rows are independent calls: the others were inserted.
        Assert.Equal([1, 2, 3], IntColumn("SELECT id FROM #pk ORDER BY id"));
    }

    [SkippableFact]
    public void ParameterizedStatementCanBeCancelled()
    {
        using AdbcStatement statement = _connection.CreateStatement();
        statement.SqlQuery = "WAITFOR DELAY @delay";
        RecordBatch batch = Batch(("delay", Strings("00:01:00")));
        statement.Bind(batch, batch.Schema);
        Task.Delay(300).ContinueWith(_ => statement.Cancel(), TaskScheduler.Default);
        Assert.Equal(AdbcStatusCode.Cancelled, Assert.ThrowsAny<AdbcException>(() => statement.ExecuteUpdate()).Status);
        Assert.Equal([1], IntColumn("SELECT 1"));
    }

    private sealed class BatchStream(RecordBatch batch) : IArrowArrayStream
    {
        private RecordBatch? _batch = batch;

        public Schema Schema { get; } = batch.Schema;

        public ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
        {
            RecordBatch? next = _batch;
            _batch = null;
            return new ValueTask<RecordBatch?>(next);
        }

        public void Dispose() => _batch?.Dispose();
    }
}
