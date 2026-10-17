// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Adbc.C;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Clast.Drivers.SqlServer.Tests;
using Xunit;

namespace Clast.Drivers.SqlServer.Native.Tests;

/// <summary>
/// The NativeAOT-published driver, loaded through <see cref="CAdbcDriverImporter"/>
/// and driven over the C ABI: the same driver code as the managed tests, but
/// as a native library. A canary for what only AOT breaks (trimmed-away code,
/// reflection, missing exports, Arrow C data interface marshaling).
/// <para>The library's path comes from <c>ADBC_TEST_AOT_FIXTURE_PATH</c>
/// (see ci/scripts/csharp_aot.sh); without it the tests skip. The fake TDS
/// server needs nothing else; the real-server tests also need
/// <c>MSSQL_TEST_CONFIG_FILE</c>.</para>
/// </summary>
public sealed class AotDriverTests
{
    private const string FixturePathEnvVar = "ADBC_TEST_AOT_FIXTURE_PATH";

    private static string RequireFixture()
    {
        string? path = Environment.GetEnvironmentVariable(FixturePathEnvVar);
        Skip.IfNot(!string.IsNullOrEmpty(path) && File.Exists(path),
            $"Set {FixturePathEnvVar} to the NativeAOT-published adbc_driver_sqlserver library to run this test");
        return path!;
    }

    private static Dictionary<string, string> FakeServerOptions(FakeTdsServer server) => new()
    {
        [SqlServerParameters.Server] = $"127.0.0.1,{server.Port}",
        [SqlServerParameters.Username] = "user",
        [SqlServerParameters.Password] = "password",
        [SqlServerParameters.TrustServerCertificate] = "true",
        [SqlServerParameters.ConnectTimeout] = "5",
    };

    private static async Task<List<RecordBatch>> ReadAll(IArrowArrayStream stream)
    {
        var batches = new List<RecordBatch>();
        while (await stream.ReadNextRecordBatchAsync() is RecordBatch batch)
        {
            batches.Add(batch);
        }
        return batches;
    }

    /// <summary>The extension type a field carries: as a registered type, or only as metadata.</summary>
    private static string? ExtensionName(Field field) =>
        field.DataType is ExtensionType extension ? extension.Name
        : field.Metadata?.TryGetValue("ARROW:extension:name", out string? name) == true ? name : null;

    [SkippableFact]
    public void LoadsTheDriver()
    {
        using AdbcDriver driver = CAdbcDriverImporter.Load(RequireFixture());
        Assert.NotNull(driver);
    }

    /// <summary>TLS, the login and a result set, over the C ABI against the fake TDS server.</summary>
    [SkippableFact]
    public async Task QueriesTheFakeServer()
    {
        string fixture = RequireFixture();
        using var server = new FakeTdsServer((_, _) => [.. FakeTdsServer.LoginAck(), .. FakeTdsServer.Done()])
        {
            Batch = _ => FakeTdsServer.IntResult(42),
        };

        using AdbcDriver driver = CAdbcDriverImporter.Load(fixture);
        using AdbcDatabase database = driver.Open(FakeServerOptions(server));
        using AdbcConnection connection = database.Connect(null);
        using AdbcStatement statement = connection.CreateStatement();
        statement.SqlQuery = "SELECT 42";
        using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
        List<RecordBatch> batches = await ReadAll(stream);

        RecordBatch batch = Assert.Single(batches);
        Assert.Equal(42, Assert.IsType<Int32Array>(batch.Column(0)).GetValue(0));
        Assert.Equal("SELECT 42", Assert.Single(server.Batches));
        batches.ForEach(b => b.Dispose());
    }

    /// <summary>A server error keeps its status and number across the C ABI.</summary>
    [SkippableFact]
    public void LoginErrorsCrossTheCAbi()
    {
        string fixture = RequireFixture();
        using var server = new FakeTdsServer((_, _) => FakeTdsServer.LoginError(18456, "Login failed for user 'user'."));

        using AdbcDriver driver = CAdbcDriverImporter.Load(fixture);
        using AdbcDatabase database = driver.Open(FakeServerOptions(server));
        AdbcException ex = Assert.ThrowsAny<AdbcException>(() => database.Connect(null));
        Assert.Equal(AdbcStatusCode.Unauthenticated, ex.Status);
        Assert.Equal(18456, ex.NativeError);
        Assert.Contains("Login failed for user 'user'.", ex.Message);
    }

    private static AdbcConnection ConnectToRealServer(AdbcDriver driver, out AdbcDatabase database)
    {
        database = driver.Open(SqlServerTestConfiguration.Load().Options);
        return database.Connect(null);
    }

    /// <summary>Every family of decoder, including Arrow extension types, through the C data interface.</summary>
    [SkippableFact]
    public async Task TypesRoundTripFromARealServer()
    {
        string fixture = RequireFixture();
        using AdbcDriver driver = CAdbcDriverImporter.Load(fixture);
        using AdbcConnection connection = ConnectToRealServer(driver, out AdbcDatabase database);
        using (database)
        using (AdbcStatement statement = connection.CreateStatement())
        {
            statement.SqlQuery = """
                SELECT CAST(42 AS int) AS i, N'héllo 😀' AS s, CAST('caf' + CHAR(233) AS varchar(10)) AS v,
                       CAST(12.34 AS decimal(10,2)) AS d, CAST(3.5 AS float) AS f, CAST(1 AS bit) AS bit,
                       CAST('2024-02-29T12:34:56.1234567' AS datetime2(7)) AS ts, CAST('2024-02-29' AS date) AS dt,
                       CAST('12:34:56.789' AS time(3)) AS t,
                       CAST('2024-02-29T12:34:56.123+05:30' AS datetimeoffset(3)) AS dto,
                       CAST('00112233-4455-6677-8899-AABBCCDDEEFF' AS uniqueidentifier) AS g,
                       CAST(0x0102FF AS varbinary(10)) AS b, CAST(NULL AS bigint) AS n,
                       CAST(5 AS sql_variant) AS sv
                """;
            using IArrowArrayStream stream = statement.ExecuteQuery().Stream!;
            List<RecordBatch> batches = await ReadAll(stream);
            RecordBatch batch = Assert.Single(batches);

            Assert.Equal(42, Assert.IsType<Int32Array>(batch.Column("i")).GetValue(0));
            Assert.Equal("héllo 😀", Assert.IsType<StringArray>(batch.Column("s")).GetString(0));
            Assert.Equal("café", Assert.IsType<StringArray>(batch.Column("v")).GetString(0));
            Assert.Equal(12.34m, Assert.IsType<Decimal128Array>(batch.Column("d")).GetValue(0));
            Assert.Equal(3.5, Assert.IsType<DoubleArray>(batch.Column("f")).GetValue(0));
            Assert.True(Assert.IsType<BooleanArray>(batch.Column("bit")).GetValue(0));
            Assert.Equal(new DateTimeOffset(2024, 2, 29, 12, 34, 56, TimeSpan.Zero).AddTicks(1234560),
                Assert.IsType<TimestampArray>(batch.Column("ts")).GetTimestamp(0));
            Assert.Equal(new DateTime(2024, 2, 29), Assert.IsType<Date32Array>(batch.Column("dt")).GetDateTime(0));
            Assert.Equal(45_296_789_000_000L, Assert.IsType<Time64Array>(batch.Column("t")).GetValue(0));
            Assert.Equal(new byte[] { 1, 2, 0xFF }, Assert.IsType<BinaryArray>(batch.Column("b")).GetBytes(0).ToArray());
            Assert.True(batch.Column("n").IsNull(0));

            Assert.Equal("arrow.timestamp_with_offset", ExtensionName(batch.Schema.GetFieldByName("dto")));
            Assert.Equal("arrow.uuid", ExtensionName(batch.Schema.GetFieldByName("g")));
            Assert.Equal("arrow.parquet.variant", ExtensionName(batch.Schema.GetFieldByName("sv")));
            Assert.Equal("INT", batch.Schema.GetFieldByName("i").Metadata["MSSQL:type"]);
            batches.ForEach(b => b.Dispose());
        }
    }

    /// <summary>Bulk ingest and bound parameters, both handing Arrow data to the driver across the C ABI.</summary>
    [SkippableFact]
    public async Task IngestsAndBindsParametersWithARealServer()
    {
        string fixture = RequireFixture();
        using AdbcDriver driver = CAdbcDriverImporter.Load(fixture);
        using AdbcConnection connection = ConnectToRealServer(driver, out AdbcDatabase database);
        using (database)
        {
            var schema = new Schema(
            [
                new Field("id", Int32Type.Default, false),
                new Field("name", StringType.Default, true),
            ], null);
            const int Rows = 10_000;
            var ids = new Int32Array.Builder();
            var names = new StringArray.Builder();
            for (int i = 0; i < Rows; i++)
            {
                ids.Append(i);
                if (i % 10 == 0)
                {
                    names.AppendNull();
                }
                else
                {
                    names.Append($"row {i}");
                }
            }
            using (var data = new RecordBatch(schema, [ids.Build(), names.Build()], Rows))
            using (AdbcStatement ingest = connection.BulkIngest(null, null, "aot_ingest", BulkIngestMode.Create, isTemporary: true))
            {
                ingest.Bind(data, schema);
                Assert.Equal(Rows, ingest.ExecuteUpdate().AffectedRows);
            }

            var parameterSchema = new Schema([new Field("low", Int32Type.Default, false)], null);
            using var parameters = new RecordBatch(parameterSchema, [new Int32Array.Builder().Append(9_000).Build()], 1);
            using AdbcStatement query = connection.CreateStatement();
            query.SqlQuery = "SELECT COUNT(*) AS n, COUNT(name) AS named FROM #aot_ingest WHERE id >= @low";
            query.Bind(parameters, parameterSchema);
            using IArrowArrayStream stream = query.ExecuteQuery().Stream!;
            List<RecordBatch> batches = await ReadAll(stream);
            RecordBatch batch = Assert.Single(batches);
            Assert.Equal(1_000, Assert.IsType<Int32Array>(batch.Column("n")).GetValue(0));
            Assert.Equal(900, Assert.IsType<Int32Array>(batch.Column("named")).GetValue(0));
            batches.ForEach(b => b.Dispose());
        }
    }

    /// <summary>Catalog functions and GetInfo, and a SQL error's status and number.</summary>
    [SkippableFact]
    public async Task CatalogFunctionsAndErrorsWithARealServer()
    {
        string fixture = RequireFixture();
        using AdbcDriver driver = CAdbcDriverImporter.Load(fixture);
        using AdbcConnection connection = ConnectToRealServer(driver, out AdbcDatabase database);
        using (database)
        {
            using (IArrowArrayStream objects = connection.GetObjects(AdbcConnection.GetObjectsDepth.Catalogs, null, null, null, null, null))
            {
                List<RecordBatch> batches = await ReadAll(objects);
                Assert.Contains("master", batches.SelectMany(b => Enumerable.Range(0, b.Length).Select(i => ((StringArray)b.Column(0)).GetString(i))));
                batches.ForEach(b => b.Dispose());
            }

            using (IArrowArrayStream info = connection.GetInfo([AdbcInfoCode.VendorName, AdbcInfoCode.DriverName]))
            {
                List<RecordBatch> batches = await ReadAll(info);
                Assert.Equal(2, batches.Sum(b => b.Length));
                batches.ForEach(b => b.Dispose());
            }

            using AdbcStatement statement = connection.CreateStatement();
            statement.SqlQuery = "SELECT * FROM dbo.clast_no_such_table";
            AdbcException ex = Assert.ThrowsAny<AdbcException>(() => statement.ExecuteQuery());
            Assert.Equal(AdbcStatusCode.NotFound, ex.Status);
            Assert.Equal(208, ex.NativeError);
        }
    }
}
