// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Xunit;

namespace Clast.Drivers.SqlServer.Tests;

/// <summary>
/// What the driver does differently on Synapse dedicated pools (EngineEdition
/// 6) and Fabric Warehouse or Synapse serverless (11). Fake TDS servers report
/// the edition; the real server covers SQL Server's own behavior.
/// </summary>
public sealed class EngineEditionTests
{
    /// <summary>A server reporting <paramref name="edition"/>, with READ_COMMITTED_SNAPSHOT as given.</summary>
    private static FakeTdsServer Engine(int edition, bool readCommittedSnapshot = false) =>
        new((_, _) => [.. FakeTdsServer.LoginAck(), .. FakeTdsServer.Done()])
        {
            Batch = sql =>
                sql.Contains("EngineEdition", StringComparison.Ordinal) ? FakeTdsServer.IntResult(edition)
                : sql.Contains("is_read_committed_snapshot_on", StringComparison.Ordinal) ? FakeTdsServer.IntResult(readCommittedSnapshot ? 1 : 0)
                : sql.StartsWith("SELECT", StringComparison.Ordinal) ? FakeTdsServer.EmptyResult()
                : FakeTdsServer.Done(),
        };

    private static AdbcConnection Connect(FakeTdsServer server) => new SqlServerDriver().Open(new Dictionary<string, string>
    {
        [SqlServerParameters.Server] = $"127.0.0.1,{server.Port}",
        [SqlServerParameters.Username] = "user",
        [SqlServerParameters.Password] = "password",
        [SqlServerParameters.TrustServerCertificate] = "true",
        [SqlServerParameters.ConnectTimeout] = "5",
    }).Connect(null);

    private static bool SentIsolation(FakeTdsServer server) =>
        server.Batches.Any(b => b.Contains("SET TRANSACTION ISOLATION LEVEL", StringComparison.Ordinal));

    [Fact]
    public void ReportsTheEngineEdition()
    {
        using FakeTdsServer server = Engine(6);
        using AdbcConnection connection = Connect(server);
        Assert.Equal("6", connection.GetOption(SqlServerParameters.EngineEdition));
        Assert.Equal("6", connection.GetOption(SqlServerParameters.EngineEdition));
        Assert.Single(server.Batches, b => b.Contains("EngineEdition", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, IsolationLevel.ReadUncommitted)]
    [InlineData(true, IsolationLevel.ReadCommitted)]
    public void SynapseDedicatedReportsItsOneIsolationLevel(bool readCommittedSnapshot, IsolationLevel expected)
    {
        using FakeTdsServer server = Engine(6, readCommittedSnapshot);
        using AdbcConnection connection = Connect(server);
        Assert.Equal(expected, connection.IsolationLevel);

        connection.IsolationLevel = expected;
        connection.IsolationLevel = IsolationLevel.Default;
        AdbcException ex = Assert.Throws<AdbcException>(() => connection.IsolationLevel = IsolationLevel.Serializable);
        Assert.Equal(AdbcStatusCode.NotImplemented, ex.Status);
        Assert.Contains("Synapse", ex.Message);
        Assert.False(SentIsolation(server));
    }

    [Fact]
    public void WarehouseRunsAtSnapshot()
    {
        using FakeTdsServer server = Engine(11);
        using AdbcConnection connection = Connect(server);
        Assert.Equal(IsolationLevel.Snapshot, connection.IsolationLevel);

        connection.IsolationLevel = IsolationLevel.Snapshot;
        connection.SetOption(AdbcOptions.Connection.IsolationLevel, AdbcOptions.IsolationLevels.Default);
        Assert.Throws<AdbcException>(() => connection.IsolationLevel = IsolationLevel.ReadCommitted);
        Assert.False(SentIsolation(server));
    }

    [Fact]
    public async Task SynapseDedicatedListsOnlyTheCurrentDatabase()
    {
        using FakeTdsServer server = Engine(6);
        using AdbcConnection connection = Connect(server);
        using (IArrowArrayStream stream = connection.GetObjects(AdbcConnection.GetObjectsDepth.Catalogs, null, null, null, null, null))
        {
            while (await stream.ReadNextRecordBatchAsync() is RecordBatch batch)
            {
                batch.Dispose();
            }
        }
        string databases = Assert.Single(server.Batches, b => b.Contains("sys.databases", StringComparison.Ordinal));
        Assert.Contains("name = DB_NAME()", databases);
        Assert.DoesNotContain("HAS_DBACCESS", databases);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(11)]
    public async Task ApproximateStatisticsAreNoneWithoutHistograms(int edition)
    {
        using FakeTdsServer server = Engine(edition);
        using AdbcConnection connection = Connect(server);
        using IArrowArrayStream stream = connection.GetStatistics(null, null, "%", approximate: true);
        RecordBatch? batch = await stream.ReadNextRecordBatchAsync();
        Assert.True(batch == null || batch.Length == 0);
        batch?.Dispose();
        Assert.Single(server.Batches);
    }

    private static readonly IngestTarget Target = new(null, "dbo", "t", Temporary: false);

    private static Schema Fields(params IArrowType[] types) =>
        new(types.Select((t, i) => new Field($"c{i}", t, true)).ToList(), null);

    [Fact]
    public void WarehouseTablesUseTypesItStores()
    {
        Schema schema = Fields(
            UInt8Type.Default, StringType.Default, JsonType.Default, new FixedSizeBinaryType(4),
            new TimestampType(TimeUnit.Nanosecond, (string?)null), new TimestampType(TimeUnit.Microsecond, "UTC"),
            new Time64Type(TimeUnit.Nanosecond), Int32Type.Default, BinaryType.Default, new Decimal128Type(18, 4));
        Assert.Equal(
            "CREATE TABLE [dbo].[t] ([c0] smallint NULL, [c1] varchar(max) NULL, [c2] varchar(max) NULL, [c3] varbinary(4) NULL, " +
            "[c4] datetime2(6) NULL, [c5] datetime2(6) NULL, [c6] time(6) NULL, [c7] int NULL, [c8] varbinary(max) NULL, [c9] decimal(18,4) NULL)",
            SqlServerBulkIngest.CreateTableSql(Target, schema, EngineEdition.Warehouse));
    }

    [Fact]
    public void WarehouseRejectsTypesItCannotStore()
    {
        AdbcException ex = Assert.Throws<AdbcException>(() =>
            SqlServerBulkIngest.CreateTableSql(Target, Fields(new TimestampWithOffsetType(TimeUnit.Microsecond)), EngineEdition.Warehouse));
        Assert.Equal(AdbcStatusCode.NotImplemented, ex.Status);
        Assert.Contains("Fabric Warehouse", ex.Message);
    }

    [Fact]
    public void SynapseDedicatedTablesWithMaxColumnsAreHeaps()
    {
        Assert.Equal(
            "CREATE TABLE [dbo].[t] ([c0] int NULL, [c1] nvarchar(max) NULL) WITH (HEAP)",
            SqlServerBulkIngest.CreateTableSql(Target, Fields(Int32Type.Default, JsonType.Default), EngineEdition.SynapseDedicated));
        Assert.Equal(
            "CREATE TABLE [dbo].[t] ([c0] int NULL, [c1] tinyint NULL)",
            SqlServerBulkIngest.CreateTableSql(Target, Fields(Int32Type.Default, UInt8Type.Default), EngineEdition.SynapseDedicated));
    }

    [Fact]
    public void SqlServerTablesAreUnchanged()
    {
        Assert.Equal(
            "CREATE TABLE [dbo].[t] ([c0] tinyint NULL, [c1] nvarchar(max) NULL, [c2] json NULL, [c3] datetime2(7) NULL)",
            SqlServerBulkIngest.CreateTableSql(Target,
                Fields(UInt8Type.Default, StringType.Default, JsonType.Default, new TimestampType(TimeUnit.Nanosecond, (string?)null)),
                EngineEdition.Enterprise));
    }

    private static FakeTdsServer DescribeFails(params (int Number, string Message)[] errors) =>
        new((_, _) => [.. FakeTdsServer.LoginAck(), .. FakeTdsServer.Done()])
        {
            Batch = sql =>
                sql.Contains("sp_describe_first_result_set", StringComparison.Ordinal) ? FakeTdsServer.ErrorResponse(errors)
                : sql.Contains("sp_describe_undeclared_parameters", StringComparison.Ordinal) ? FakeTdsServer.EmptyResult()
                : sql.Contains("SET FMTONLY ON; SELECT 1;", StringComparison.Ordinal) ? FakeTdsServer.IntResult(1)
                : FakeTdsServer.Done(),
        };

    [Theory]
    [InlineData(2812, "Could not find stored procedure 'sys.sp_describe_first_result_set'.")]
    [InlineData(15871, "PROCEDURE 'sp_describe_first_result_set' is not supported.")]
    public void ExecuteSchemaFallsBackToFormatOnlyWithoutDescribeFirstResultSet(int number, string message)
    {
        using FakeTdsServer server = DescribeFails((number, message));
        using AdbcConnection connection = Connect(server);
        using AdbcStatement statement = connection.CreateStatement();
        statement.SqlQuery = "SELECT 1";
        Assert.IsType<Int32Type>(Assert.Single(statement.ExecuteSchema().FieldsList).DataType);
        Assert.IsType<Int32Type>(Assert.Single(statement.ExecuteSchema().FieldsList).DataType);
        Assert.Single(server.Batches, b => b.Contains("sp_describe_first_result_set", StringComparison.Ordinal));
        Assert.False(((SqlServerConnection)connection).Session.DescribesFirstResultSet);
    }

    /// <summary>
    /// The same errors from inside the described batch come with 11529 after
    /// them: they are the query's, so the query must not be run under FMTONLY.
    /// </summary>
    [Theory]
    [InlineData(2812, "Could not find stored procedure 'nope'.")]
    [InlineData(15871, "PROCEDURE 'sp_who' is not supported.")]
    public void ExecuteSchemaDoesNotFallBackOnAnErrorInTheBatch(int number, string message)
    {
        using FakeTdsServer server = DescribeFails((number, message),
            (11529, "The metadata could not be determined because every code path results in an error; see previous errors for some of these."));
        using AdbcConnection connection = Connect(server);
        using AdbcStatement statement = connection.CreateStatement();
        statement.SqlQuery = "SELECT 1";
        SqlServerException ex = Assert.Throws<SqlServerException>(() => statement.ExecuteSchema());
        Assert.Equal([number, 11529], ex.Errors.Select(e => e.Number));
        Assert.DoesNotContain(server.Batches, b => b.Contains("FMTONLY", StringComparison.Ordinal));
        Assert.Null(((SqlServerConnection)connection).Session.DescribesFirstResultSet);
    }

    [SkippableFact]
    public void TheRealServerIsSqlServer()
    {
        using AdbcConnection connection = new SqlServerDriver().Open(SqlServerTestConfiguration.Load().Options).Connect(null);
        int edition = int.Parse(connection.GetOption(SqlServerParameters.EngineEdition), System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains(edition, (int[])[2, 3, 4]);
    }
}
