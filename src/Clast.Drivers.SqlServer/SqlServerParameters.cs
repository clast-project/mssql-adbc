// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

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

    /// <summary>Maximum number of rows per Arrow record batch.</summary>
    public const string BatchRows = "adbc.mssql.batch_rows";
}
