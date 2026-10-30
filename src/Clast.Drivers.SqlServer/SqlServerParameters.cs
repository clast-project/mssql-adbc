// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow.Adbc;

namespace Clast.Drivers.SqlServer;

/// <summary>
/// Option keys understood by the SQL Server driver.
/// </summary>
public static class SqlServerParameters
{
    /// <summary>Server to connect to: <c>host</c>, <c>host,port</c>, <c>host\instance</c> or <c>np:\\host\pipe\name</c>.</summary>
    public const string Server = "adbc.mssql.server";

    /// <summary>Initial database.</summary>
    public const string Database = "adbc.mssql.database";

    /// <summary>SQL authentication user name (standard ADBC key).</summary>
    public const string Username = "username";

    /// <summary>SQL authentication password (standard ADBC key).</summary>
    public const string Password = "password";

    /// <summary><c>true</c> to use Windows integrated authentication (SSPI).</summary>
    public const string IntegratedSecurity = "adbc.mssql.integrated_security";

    /// <summary>Encryption mode: <c>optional</c>, <c>mandatory</c> (default) or <c>strict</c> (TDS 8).</summary>
    public const string Encrypt = "adbc.mssql.encrypt";

    /// <summary><c>true</c> to skip validation of the server certificate.</summary>
    public const string TrustServerCertificate = "adbc.mssql.trust_server_certificate";

    /// <summary>Requested TDS packet size in bytes.</summary>
    public const string PacketSize = "adbc.mssql.packet_size";

    /// <summary>Application name reported to the server.</summary>
    public const string ApplicationName = "adbc.mssql.application_name";

    /// <summary>Seconds allowed for connecting and logging in.</summary>
    public const string ConnectTimeout = "adbc.mssql.connect_timeout";

    /// <summary>
    /// How many times to retry a login that fails with a transient Azure error
    /// (such as 40613, "database not currently available"), within the connect
    /// timeout. Defaults to 2 for Azure SQL hosts, 5 for Synapse serverless and
    /// Fabric warehouses, 1 for Synapse dedicated pools and 0 elsewhere.
    /// </summary>
    public const string ConnectRetryCount = "adbc.mssql.connect_retry_count";

    /// <summary>Seconds to wait before retrying a login (1 to 60; default 10).</summary>
    public const string ConnectRetryInterval = "adbc.mssql.connect_retry_interval";

    /// <summary>
    /// <c>true</c> (the default) to reconnect, before the next request, a
    /// session whose connection broke while it was idle (a failover, a
    /// gateway reset, a killed session): the server recreates its database,
    /// language and SET options. Not when a transaction was open, or the
    /// session had state the server cannot recover, such as temporary tables.
    /// </summary>
    public const string SessionRecovery = "adbc.mssql.session_recovery";

    /// <summary><c>true</c> to take a table lock during bulk ingestion (<c>INSERT BULK ... WITH (TABLOCK)</c>).</summary>
    public const string IngestTableLock = "adbc.mssql.ingest.table_lock";

    /// <summary>
    /// Statement option: rows per <c>INSERT BULK</c> during bulk ingestion, like
    /// <c>SqlBulkCopy.BatchSize</c>. 0 (the default) sends all rows as one batch.
    /// Each batch is all or nothing; under autocommit each commits on its own,
    /// so a failure leaves the earlier batches in the table. Inside a
    /// transaction they commit or roll back with it.
    /// </summary>
    public const string IngestBatchSize = "adbc.mssql.ingest.batch_size";

    /// <summary>Statement option: integer result column whose range is split for <c>ExecutePartitioned</c>.</summary>
    public const string PartitionColumn = "adbc.mssql.partition.column";

    /// <summary>Statement option: number of partitions <c>ExecutePartitioned</c> produces (default 1).</summary>
    public const string PartitionCount = "adbc.mssql.partition.count";

    /// <summary>
    /// Number of sessions <c>ExecuteQuery</c> reads with in parallel (default 1).
    /// Needs <see cref="PartitionColumn"/>; batches arrive in no particular order.
    /// Settable on the database, connection or statement.
    /// </summary>
    public const string ParallelReads = "adbc.mssql.parallel_reads";

    /// <summary>
    /// Service principal name for integrated authentication, such as
    /// <c>MSSQLSvc/host.example.com:1433</c>. Defaults to one built from the
    /// server's DNS name.
    /// </summary>
    public const string ServerSpn = "adbc.mssql.server_spn";

    /// <summary>
    /// A Microsoft Entra access token (for example for the
    /// <c>https://database.windows.net/.default</c> scope) to log in with, in
    /// place of a username, password or integrated security. Never sent
    /// without encryption.
    /// </summary>
    public const string AccessToken = "adbc.mssql.access_token";

    /// <summary>
    /// Read-only, from GetOption on the database or a connection: the Entra
    /// authority (token endpoint) the server names, such as
    /// <c>https://login.microsoftonline.com/{tenant}</c>. Found with a probe
    /// handshake that needs no credentials.
    /// </summary>
    public const string EntraAuthority = "adbc.mssql.entra.authority";

    /// <summary>
    /// Read-only, from GetOption on the database or a connection: the audience
    /// (resource) the server wants tokens for, such as <c>https://database.windows.net/</c>.
    /// </summary>
    public const string EntraAudience = "adbc.mssql.entra.audience";

    /// <summary>
    /// Read-only, from GetOption on a connection: the server's
    /// <c>SERVERPROPERTY('EngineEdition')</c>, such as 3 (SQL Server
    /// Enterprise or Developer), 5 (Azure SQL Database), 6 (Synapse dedicated
    /// SQL pool) or 11 (Synapse serverless SQL pool, Fabric Warehouse).
    /// </summary>
    public const string EngineEdition = "adbc.mssql.engine_edition";

    /// <summary>
    /// How hierarchyid columns are read: <c>text</c> (the default), as
    /// <c>ToString()</c> formats them (<c>/1/2.5/</c>), or <c>binary</c>, their
    /// serialized bytes, which are shorter and sort in tree (depth-first)
    /// order where the text does not. Ingest takes either form.
    /// </summary>
    public const string HierarchyId = "adbc.mssql.hierarchyid";

    /// <summary>
    /// How result columns that would be Arrow strings are returned: <c>utf8</c>
    /// (the default), as Arrow's string type, or <c>utf16</c>, as
    /// <see cref="Utf16StringType"/> (<c>clast.utf16</c>, little-endian UTF-16
    /// in binary), which nvarchar data reaches without transcoding. Covers
    /// char, varchar, nchar, nvarchar, text, ntext, xml and hierarchyid as
    /// text; json stays <c>arrow.json</c>, whose text is UTF-8. Catalog
    /// functions' standard schemas keep utf8.
    /// </summary>
    public const string StringEncoding = "adbc.mssql.string_encoding";

    /// <summary>Maximum number of rows per Arrow record batch.</summary>
    public const string BatchRows = "adbc.mssql.batch_rows";

    /// <summary>
    /// Statement option: the collation a Substrait plan's string values get,
    /// which decides how they compare, sort and group. The default,
    /// <c>Latin1_General_100_BIN2_UTF8</c>, is binary and case-sensitive, the
    /// closest SQL Server comes to Substrait's semantics, but not the same:
    /// it works on UTF-16 code units, so a character outside the Basic
    /// Multilingual Plane counts as two and sorts below U+E000, and, as under
    /// every collation, trailing spaces are ignored in comparisons
    /// (<c>'a' = 'a '</c>). An empty value uses the columns' own collations
    /// instead (often case-insensitive), which lets the server use indexes on
    /// them.
    /// </summary>
    public const string SubstraitCollation = "adbc.mssql.substrait.collation";

    /// <summary>Read-only, from GetOption on a statement: the T-SQL its Substrait plan translates to.</summary>
    public const string SubstraitSql = "adbc.mssql.substrait.sql";

    /// <summary>
    /// Read-only, from GetOption on the database or a connection: the
    /// driver's Substrait dialect (YAML in the Substrait dialect format), which
    /// lists the relations, expressions, types and functions it translates.
    /// </summary>
    public const string SubstraitDialect = "adbc.mssql.substrait.dialect";

    internal static AdbcException OptionNotFound(string key) =>
        new($"Option '{key}' is not recognized or has no value", AdbcStatusCode.NotFound);

    internal static string Format(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
