// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.InteropServices;
using Apache.Arrow.Adbc;
using Apache.Arrow.Adbc.C;

namespace Clast.Drivers.SqlServer.Native;

/// <summary>
/// The native entry point of the NativeAOT shared library. The ADBC driver
/// manager looks up <c>AdbcDriverInit</c> (unless told another entrypoint);
/// it hands a new <see cref="SqlServerDriver"/>, the same driver the managed
/// tests use, to <see cref="CAdbcDriverExporter"/>, which owns it through a
/// GCHandle on the CAdbcDriver and disposes it when the driver is released.
/// </summary>
public static unsafe class DriverInit
{
    [UnmanagedCallersOnly(EntryPoint = "AdbcDriverInit")]
    public static AdbcStatusCode AdbcDriverInit(int version, CAdbcDriver* driver, CAdbcError* error) =>
        CAdbcDriverExporter.AdbcDriverInit(version, driver, error, new SqlServerDriver());
}
