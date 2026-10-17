// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Clast.Drivers.SqlServer.Tds;

namespace Clast.Drivers.SqlServer;

/// <summary>
/// <c>SERVERPROPERTY('EngineEdition')</c>: which Database Engine product a
/// session is connected to. The values are Microsoft's.
/// </summary>
internal enum EngineEdition
{
    Unknown = 0,
    Standard = 2,
    Enterprise = 3,
    Express = 4,
    SqlDatabase = 5,
    /// <summary>Azure Synapse Analytics dedicated SQL pool.</summary>
    SynapseDedicated = 6,
    ManagedInstance = 8,
    SqlEdge = 9,
    /// <summary>Azure Synapse serverless SQL pool, or a Fabric Warehouse or SQL analytics endpoint.</summary>
    Warehouse = 11,
    FabricSqlDatabase = 12,
}

/// <summary>
/// What the driver does differently on the analytics engines, whose T-SQL
/// is a subset of SQL Server's. The edition is asked for once per session,
/// when first needed.
/// </summary>
internal static class SqlServerEngine
{
    public static EngineEdition Edition(TdsSession session)
    {
        if (session.EngineEdition is not int edition)
        {
            object? value = SqlServerMetadata.Query(session, "SELECT CAST(SERVERPROPERTY('EngineEdition') AS int)")[0][0][0];
            session.EngineEdition = edition = value is int v ? v : 0;
        }
        return (EngineEdition)edition;
    }

    /// <summary>
    /// Whether the engine runs every transaction at one isolation level, so
    /// that SET TRANSACTION ISOLATION LEVEL fails (Synapse dedicated pools,
    /// error 104409) or is ignored (Fabric Warehouse).
    /// </summary>
    public static bool HasFixedIsolation(EngineEdition edition) =>
        edition is EngineEdition.SynapseDedicated or EngineEdition.Warehouse;

    /// <summary>Whether queries can name other databases (three-part names); Synapse dedicated pools cannot.</summary>
    public static bool SupportsCrossDatabase(EngineEdition edition) => edition != EngineEdition.SynapseDedicated;

    /// <summary>Whether the engine has the per-column statistics functions (<c>sys.dm_db_stats_histogram</c>).</summary>
    public static bool HasStatisticsHistograms(EngineEdition edition) =>
        edition is not (EngineEdition.SynapseDedicated or EngineEdition.Warehouse);
}
