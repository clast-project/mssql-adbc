// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Xunit;

namespace Clast.Drivers.SqlServer.Tests;

/// <summary>
/// Tests that scan catalog views and statistics across databases run on their
/// own: alongside other tests creating and dropping tables in tempdb, SQL
/// Server can pick their catalog queries as deadlock victims.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CatalogCollection
{
    public const string Name = "Catalog scans";
}
