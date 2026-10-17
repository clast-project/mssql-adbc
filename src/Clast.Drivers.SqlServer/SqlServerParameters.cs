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

    /// <summary><c>true</c> to take a table lock during bulk ingestion (<c>INSERT BULK ... WITH (TABLOCK)</c>).</summary>
    public const string IngestTableLock = "adbc.mssql.ingest.table_lock";

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

    /// <summary>Maximum number of rows per Arrow record batch.</summary>
    public const string BatchRows = "adbc.mssql.batch_rows";

    internal static AdbcException OptionNotFound(string key) =>
        new($"Option '{key}' is not recognized or has no value", AdbcStatusCode.NotFound);

    internal static string Format(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
