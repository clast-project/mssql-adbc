// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

#if !NET
namespace System.Runtime.InteropServices;

/// <summary>The part of .NET Core's <c>NativeLibrary</c> the driver uses (LocalDB, Windows only), for netstandard2.0.</summary>
internal static class NativeLibrary
{
    public static bool TryLoad(string libraryPath, out IntPtr handle)
    {
        handle = LoadLibraryW(libraryPath);
        return handle != IntPtr.Zero;
    }

    public static bool TryGetExport(IntPtr handle, string name, out IntPtr address)
    {
        address = GetProcAddress(handle, name);
        return address != IntPtr.Zero;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string fileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true, BestFitMapping = false)]
    private static extern IntPtr GetProcAddress(IntPtr module, string procName);
}
#endif
