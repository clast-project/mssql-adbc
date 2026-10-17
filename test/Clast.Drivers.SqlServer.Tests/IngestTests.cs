// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Data.SqlTypes;
using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Xunit;

namespace Clast.Drivers.SqlServer.Tests;

public class IngestTests
{
    private static AdbcConnection Connect()
    {
        SqlServerTestConfiguration config = SqlServerTestConfiguration.Load();
        return new SqlServerDriver().Open(config.Options).Connect(null);
    }

    private static long Execute(AdbcConnection connection, string sql)
    {
        using AdbcStatement statement = connection.CreateStatement();
        statement.SqlQuery = sql;
        return statement.ExecuteUpdate().AffectedRows;
    }

    private static long Ingest(AdbcConnection connection, string table, BulkIngestMode mode, RecordBatch batch, bool temporary = true)
    {
        using AdbcStatement statement = connection.BulkIngest(null, null, table, mode, temporary);
        statement.Bind(batch, batch.Schema);
        return statement.ExecuteUpdate().AffectedRows;
    }

    private static readonly TimestampType Micros = new TimestampType(TimeUnit.Microsecond, (string?)null);

    /// <summary>Four rows covering the prototype types, including a row of nulls.</summary>
    private static RecordBatch PrototypeBatch()
    {
        DateTimeOffset[] timestamps =
        [
            new DateTimeOffset(1, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(9999, 12, 31, 23, 59, 59, TimeSpan.Zero).AddTicks(9999990),
            default,
            new DateTimeOffset(1969, 12, 31, 23, 59, 59, 999, TimeSpan.Zero),
        ];
        DateTimeOffset[] withOffsets =
        [
            new DateTimeOffset(1, 1, 2, 0, 0, 0, TimeSpan.FromHours(-14)),
            new DateTimeOffset(9999, 12, 30, 23, 59, 59, TimeSpan.FromHours(14)),
            default,
            new DateTimeOffset(2020, 2, 29, 12, 34, 56, 123, TimeSpan.FromMinutes(330)).AddTicks(4560),
        ];
        bool[] valid = [true, true, false, true];

        var builder = new RecordBatch.Builder()
            .Append("c_bit", true, c => c.Boolean(b => b.Append(false).Append(true).AppendNull().Append(true)))
            .Append("c_tinyint", true, c => c.UInt8(b => b.Append(0).Append(255).AppendNull().Append(7)))
            .Append("c_smallint", true, c => c.Int16(b => b.Append(short.MinValue).Append(short.MaxValue).AppendNull().Append(-1)))
            .Append("c_int", true, c => c.Int32(b => b.Append(int.MinValue).Append(int.MaxValue).AppendNull().Append(42)))
            .Append("c_bigint", true, c => c.Int64(b => b.Append(long.MinValue).Append(long.MaxValue).AppendNull().Append(-42)))
            .Append("c_real", true, c => c.Float(b => b.Append(-3.4e38f).Append(3.4e38f).AppendNull().Append(1.5f)))
            .Append("c_float", true, c => c.Double(b => b.Append(-1.79e308).Append(1.79e308).AppendNull().Append(-2.25)))
            .Append("c_varchar", true, c => c.String(b => b.Append("").Append("hello").AppendNull().Append("x")))
            .Append("c_nvarchar", true, c => c.String(b => b.Append("").Append("h€llo 😀 世界").AppendNull().Append(new string('é', 5000))))
            .Append("c_varbinary", true, c => c.Binary(b => b.Append(System.Array.Empty<byte>()).Append(new byte[] { 0, 255, 16 }).AppendNull().Append(new byte[9000])))
            .Append("c_date", true, c => c.Date32(b => b.Append(new DateTime(1, 1, 1)).Append(new DateTime(9999, 12, 31)).AppendNull().Append(new DateTime(1970, 1, 1))));

        RecordBatch partial = builder.Build();

        var decimals = new Decimal128Array.Builder(new Decimal128Type(18, 4));
        decimals.Append(SqlDecimal.Parse("-99999999999999.9999")).Append(SqlDecimal.Parse("99999999999999.9999")).AppendNull().Append(SqlDecimal.Parse("0.0001"));
        var decimals38 = new Decimal128Array.Builder(new Decimal128Type(38, 10));
        decimals38.Append(SqlDecimal.Parse("-9999999999999999999999999999.9999999999")).Append(SqlDecimal.Parse("9999999999999999999999999999.9999999999")).AppendNull().Append(SqlDecimal.Parse("-0.0000000001"));
        var datetime2 = new TimestampArray.Builder(Micros);
        var datetimeoffset = new TimestampWithOffsetArray.Builder(TimeUnit.Microsecond);
        var guids = new GuidArray.Builder();
        Guid[] guidValues = [Guid.Empty, Guid.Parse("6F9619FF-8B86-D011-B42D-00C04FC964FF"), default, Guid.NewGuid()];
        for (int i = 0; i < 4; i++)
        {
            if (valid[i])
            {
                datetime2.Append(timestamps[i]);
                datetimeoffset.Append(withOffsets[i]);
                guids.Append(guidValues[i]);
            }
            else
            {
                datetime2.AppendNull();
                datetimeoffset.AppendNull();
                guids.AppendNull();
            }
        }

        var fields = partial.Schema.FieldsList.ToList();
        var arrays = Enumerable.Range(0, partial.ColumnCount).Select(partial.Column).ToList();
        void Add(string name, IArrowArray array)
        {
            fields.Add(new Field(name, array.Data.DataType, true));
            arrays.Add(array);
        }
        Add("c_decimal", decimals.Build());
        Add("c_decimal38", decimals38.Build());
        Add("c_datetime2", datetime2.Build());
        Add("c_datetimeoffset", datetimeoffset.Build());
        Add("c_uniqueidentifier", guids.Build());
        return new RecordBatch(new Schema(fields, null), arrays, 4);
    }

    [SkippableFact]
    public void CreateRoundTripsPrototypeTypes()
    {
        using AdbcConnection connection = Connect();
        RecordBatch batch = PrototypeBatch();
        Assert.Equal(4, Ingest(connection, "clast_roundtrip", BulkIngestMode.Create, batch));

        List<object?[]> rows = QueryTests.ReadAdbc(connection, "SELECT * FROM #clast_roundtrip", out Schema schema, out _);
        Assert.Equal(4, rows.Count);
        for (int c = 0; c < batch.ColumnCount; c++)
        {
            Assert.Equal(batch.Schema.FieldsList[c].Name, schema.FieldsList[c].Name);
            for (int r = 0; r < 4; r++)
            {
                object? expected = QueryTests.ArrowValue(batch.Column(c), r);
                object? actual = rows[r][c];
                Assert.True(Equals(expected, actual), $"{schema.FieldsList[c].Name} row {r}: expected <{expected}>, actual <{actual}>");
            }
        }

        // The created column types follow the Arrow types.
        Assert.Equal("NVARCHAR(MAX)", schema.GetFieldByName("c_nvarchar").Metadata[Arrow.DecoderFactory.SqlTypeMetadataKey]);
        Assert.Equal("DATETIME2(6)", schema.GetFieldByName("c_datetime2").Metadata[Arrow.DecoderFactory.SqlTypeMetadataKey]);
        Assert.Equal("DATETIMEOFFSET(6)", schema.GetFieldByName("c_datetimeoffset").Metadata[Arrow.DecoderFactory.SqlTypeMetadataKey]);
    }

    [SkippableFact]
    public void AppendConvertsToExistingColumnTypes()
    {
        using AdbcConnection connection = Connect();
        Execute(connection, """
            CREATE TABLE #clast_append (
                id bigint NOT NULL,
                small tinyint NULL,
                amount decimal(20,6) NULL,
                cp varchar(50) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
                u8 varchar(50) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
                fixed nchar(5) NULL,
                legacy datetime NULL,
                dt date NULL,
                t time(3) NULL,
                untouched int NULL DEFAULT 99)
            """);

        var batch = new RecordBatch.Builder()
            .Append("id", false, c => c.Int32(b => b.Append(1).Append(2)))
            .Append("small", true, c => c.Int64(b => b.Append(200).AppendNull()))
            .Append("AMOUNT", true, c => c.Decimal128(new Decimal128Type(10, 2), b => b.Append(SqlDecimal.Parse("-12.34")).Append(SqlDecimal.Parse("99999999.99"))))
            .Append("cp", true, c => c.String(b => b.Append("café €").Append("plain")))
            .Append("u8", true, c => c.String(b => b.Append("café € 😀").AppendNull()))
            .Append("fixed", true, c => c.String(b => b.Append("ab").Append("abcde")))
            .Append("legacy", true, c => c.Timestamp(Micros, b => b.Append(new DateTimeOffset(2020, 1, 2, 3, 4, 5, 123, TimeSpan.Zero)).AppendNull()))
            .Append("dt", true, c => c.Date32(b => b.Append(new DateTime(2024, 2, 29)).AppendNull()))
            .Append("t", true, c => c.Time64(new Time64Type(TimeUnit.Nanosecond), b => b.Append(45_296_789_000_000L).AppendNull()))
            .Build();

        Assert.Equal(2, Ingest(connection, "clast_append", BulkIngestMode.Append, batch));

        List<object?[]> rows = QueryTests.ReadAdbc(connection,
            "SELECT id, small, CAST(amount AS varchar(30)), cp, u8, fixed, legacy, dt, t, untouched FROM #clast_append ORDER BY id", out _, out _);
        Assert.Equal(new object?[] { 1L, (byte)200, "-12.340000", "café €", "café € 😀", "ab   ", new DateTime(2020, 1, 2, 3, 4, 5, 123), new DateTime(2024, 2, 29), new TimeSpan(0, 12, 34, 56, 789), 99 }, rows[0]);
        Assert.Equal(new object?[] { 2L, null, "99999999.990000", "plain", null, "abcde", null, null, null, 99 }, rows[1]);
    }

    [SkippableFact]
    public void ModesBehaveAsSpecified()
    {
        using AdbcConnection connection = Connect();
        RecordBatch Rows(int start, int count) => new RecordBatch.Builder()
            .Append("n", false, c => c.Int32(b => b.AppendRange(Enumerable.Range(start, count))))
            .Build();

        Assert.Equal(3, Ingest(connection, "clast_modes", BulkIngestMode.Create, Rows(0, 3)));
        var ex = Assert.ThrowsAny<AdbcException>(() => Ingest(connection, "clast_modes", BulkIngestMode.Create, Rows(0, 3)));
        Assert.Equal(AdbcStatusCode.AlreadyExists, ex.Status);

        Assert.Equal(2, Ingest(connection, "clast_modes", BulkIngestMode.Append, Rows(10, 2)));
        Assert.Equal(4, Ingest(connection, "clast_modes", BulkIngestMode.CreateAppend, Rows(20, 4)));
        Assert.Equal(9, QueryTests.ReadAdbc(connection, "SELECT * FROM #clast_modes", out _, out _).Count);

        Assert.Equal(1, Ingest(connection, "clast_modes", BulkIngestMode.Replace, Rows(100, 1)));
        Assert.Equal(100, Assert.Single(QueryTests.ReadAdbc(connection, "SELECT n FROM #clast_modes", out _, out _))[0]);

        Assert.Equal(5, Ingest(connection, "clast_modes_new", BulkIngestMode.CreateAppend, Rows(0, 5)));
        var missing = Assert.ThrowsAny<AdbcException>(() => Ingest(connection, "clast_no_such_table", BulkIngestMode.Append, Rows(0, 1)));
        Assert.Equal(AdbcStatusCode.NotFound, missing.Status);
    }

    [SkippableFact]
    public void IngestsStreamOfManyBatches()
    {
        using AdbcConnection connection = Connect();
        const int batches = 20;
        const int rowsPerBatch = 5000;
        var list = new List<RecordBatch>();
        for (int b = 0; b < batches; b++)
        {
            int start = b * rowsPerBatch;
            list.Add(new RecordBatch.Builder()
                .Append("id", false, c => c.Int64(x => x.AppendRange(Enumerable.Range(start, rowsPerBatch).Select(i => (long)i))))
                .Append("label", true, c => c.String(x => x.AppendRange(Enumerable.Range(start, rowsPerBatch).Select(i => i % 7 == 0 ? null : $"label {i}"))))
                .Build());
        }

        using (AdbcStatement statement = connection.BulkIngest(null, null, "clast_stream", BulkIngestMode.Create, true))
        {
            statement.BindStream(new ListStream(list));
            Assert.Equal(batches * rowsPerBatch, statement.ExecuteUpdate().AffectedRows);
        }

        List<object?[]> summary = QueryTests.ReadAdbc(connection,
            "SELECT COUNT(*), SUM(id), COUNT(label), MAX(label) FROM #clast_stream", out _, out _);
        long n = batches * rowsPerBatch;
        Assert.Equal(new object?[] { (int)n, n * (n - 1) / 2, (int)(n - (n + 6) / 7), "label 99999" }, summary[0]);
    }

    [SkippableFact]
    public void FailureMidStreamLeavesSessionUsable()
    {
        using AdbcConnection connection = Connect();
        Execute(connection, "CREATE TABLE #clast_fail (v tinyint NULL)");
        var batch = new RecordBatch.Builder()
            .Append("v", true, c => c.Int32(b => b.AppendRange(Enumerable.Range(0, 20000).Select(i => i == 15000 ? 300 : i % 200))))
            .Build();

        var ex = Assert.ThrowsAny<AdbcException>(() => Ingest(connection, "clast_fail", BulkIngestMode.Append, batch));
        Assert.Equal(AdbcStatusCode.InvalidData, ex.Status);

        // Nothing was inserted, and the session still works.
        Assert.Equal(0, Assert.Single(QueryTests.ReadAdbc(connection, "SELECT COUNT(*) FROM #clast_fail", out _, out _))[0]);
    }

    [SkippableFact]
    public void UnknownColumnIsRejected()
    {
        using AdbcConnection connection = Connect();
        Execute(connection, "CREATE TABLE #clast_cols (a int)");
        var batch = new RecordBatch.Builder().Append("b", true, c => c.Int32(x => x.Append(1))).Build();
        var ex = Assert.ThrowsAny<AdbcException>(() => Ingest(connection, "clast_cols", BulkIngestMode.Append, batch));
        Assert.Equal(AdbcStatusCode.InvalidArgument, ex.Status);
    }

    [SkippableFact]
    public void IngestsIntoPermanentTableViaOptions()
    {
        using AdbcConnection connection = Connect();
        string table = "clast_ingest_" + Guid.NewGuid().ToString("N");
        try
        {
            using AdbcStatement statement = connection.CreateStatement();
            statement.SetOption(AdbcOptions.Ingest.TargetTable, table);
            statement.SetOption(AdbcOptions.Ingest.TargetDbSchema, "dbo");
            statement.SetOption(AdbcOptions.Ingest.Mode, AdbcOptions.IngestMode.Create);
            statement.SetOption(SqlServerParameters.IngestTableLock, "true");
            RecordBatch batch = new RecordBatch.Builder().Append("x", true, c => c.Int32(b => b.Append(1).Append(2))).Build();
            statement.Bind(batch, batch.Schema);
            Assert.Equal(2, statement.ExecuteUpdate().AffectedRows);
            Assert.Equal(2, QueryTests.ReadSqlClient($"SELECT * FROM dbo.[{table}]").Count);
        }
        finally
        {
            Execute(connection, $"DROP TABLE IF EXISTS dbo.[{table}]");
        }
    }

    private sealed class ListStream : IArrowArrayStream
    {
        private readonly Queue<RecordBatch> _batches;

        public ListStream(List<RecordBatch> batches)
        {
            _batches = new Queue<RecordBatch>(batches);
            Schema = batches[0].Schema;
        }

        public Schema Schema { get; }

        public ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default) =>
            new ValueTask<RecordBatch?>(_batches.Count > 0 ? _batches.Dequeue() : null);

        public void Dispose()
        {
        }
    }
}
