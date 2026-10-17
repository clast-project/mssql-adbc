// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Security.Authentication;

namespace Clast.Drivers.SqlServer.Tests;

/// <summary>What the tests need on .NET Framework (the driver's netstandard2.0 build) that newer .NET has.</summary>
internal static class TestCompat
{
#if NET
    public const SslProtocols Tls13 = SslProtocols.Tls13;
#else
    public const SslProtocols Tls13 = (SslProtocols)12288;

    extension(Convert)
    {
        public static string ToHexString(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "");

        public static string ToHexString(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes.ToArray());
    }

    extension(OperatingSystem)
    {
        public static bool IsWindowsVersionAtLeast(int major, int minor = 0, int build = 0) =>
            OperatingSystem.IsWindows() && Environment.OSVersion.Version >= new Version(major, minor, build);
    }
#endif
}
