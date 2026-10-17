// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Clast.Drivers.SqlServer.Tds;

/// <summary>
/// Packet-level tracing to stderr, enabled by setting <c>CLAST_TDS_TRACE=1</c>.
/// <see cref="Enabled"/> is a static readonly field, so disabled tracing costs nothing once jitted.
/// </summary>
internal static class TdsTrace
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("CLAST_TDS_TRACE") == "1";

    public static void Packet(string direction, byte type, byte status, int length)
    {
        Console.Error.WriteLine($"TDS {direction} type=0x{type:X2} status=0x{status:X2} length={length}");
    }
}
