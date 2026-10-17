// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Apache.Arrow.Adbc;
using Microsoft.Win32;

namespace Clast.Drivers.SqlServer.Tds;

/// <summary>
/// SQL Server Express LocalDB: the instance API in SqlUserInstance.dll starts
/// an instance on demand and reports the named pipe it listens on. The DLL of
/// the newest installed LocalDB version is found through the registry.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class LocalDb
{
    private const string InstalledVersionsKey = @"SOFTWARE\Microsoft\Microsoft SQL Server Local DB\Installed Versions";
    private const int MaxConnectionLength = 260; // LOCALDB_MAX_SQLCONNECTION_BUFFER_SIZE
    private const int TruncateMessage = 1;       // LOCALDB_TRUNCATE_ERR_MESSAGE

    private static readonly object s_lock = new();
    private static bool s_loaded;

    // HRESULT LocalDBStartInstance(PCWSTR pInstanceName, DWORD dwFlags, LPWSTR wszSqlConnection, LPDWORD lpcchSqlConnection)
    private static delegate* unmanaged[Stdcall]<char*, int, char*, int*, int> s_startInstance;

    // HRESULT LocalDBFormatMessage(HRESULT hrLocalDB, DWORD dwFlags, DWORD dwLanguageId, LPWSTR wszMessage, LPDWORD lpcchMessage)
    private static delegate* unmanaged[Stdcall]<int, int, int, char*, int*, int> s_formatMessage;

    /// <summary>
    /// Starts <paramref name="instance"/> if it is not running (which can take
    /// a few seconds) and returns its connection string, for example
    /// <c>np:\\.\pipe\LOCALDB#2F9E3A1B\tsql\query</c>.
    /// </summary>
    public static string StartInstance(string instance)
    {
        Load();
        char* connection = stackalloc char[MaxConnectionLength + 1];
        int length = MaxConnectionLength + 1;
        int result;
        fixed (char* name = instance)
        {
            result = s_startInstance(name, 0, connection, &length);
        }
        if (result < 0)
        {
            throw new AdbcException($"Could not start LocalDB instance '{instance}': {FormatMessage(result)}", AdbcStatusCode.IOError);
        }
        return new string(connection);
    }

    private static string FormatMessage(int hresult)
    {
        if (s_formatMessage != null)
        {
            char* message = stackalloc char[1024];
            int length = 1024;
            if (s_formatMessage(hresult, TruncateMessage, 0, message, &length) >= 0)
            {
                return new string(message).Trim();
            }
        }
        return $"HRESULT 0x{hresult:X8}";
    }

    private static void Load()
    {
        if (s_loaded)
        {
            return;
        }
        lock (s_lock)
        {
            if (s_loaded)
            {
                return;
            }
            string path = FindInstanceApi();
            if (!NativeLibrary.TryLoad(path, out IntPtr library))
            {
                throw new AdbcException($"Could not load the LocalDB instance API '{path}'", AdbcStatusCode.IOError);
            }
            if (!NativeLibrary.TryGetExport(library, "LocalDBStartInstance", out IntPtr start))
            {
                throw new AdbcException($"'{path}' does not export LocalDBStartInstance", AdbcStatusCode.IOError);
            }
            s_startInstance = (delegate* unmanaged[Stdcall]<char*, int, char*, int*, int>)start;
            if (NativeLibrary.TryGetExport(library, "LocalDBFormatMessage", out IntPtr format))
            {
                s_formatMessage = (delegate* unmanaged[Stdcall]<int, int, int, char*, int*, int>)format;
            }
            s_loaded = true;
        }
    }

    /// <summary>The InstanceAPIPath of the newest installed LocalDB version.</summary>
    private static string FindInstanceApi()
    {
        using RegistryKey? versions = Registry.LocalMachine.OpenSubKey(InstalledVersionsKey);
        if (versions == null)
        {
            throw new AdbcException("LocalDB is not installed", AdbcStatusCode.NotFound);
        }
        string? newest = versions.GetSubKeyNames()
            .Select(name => (Name: name, Version: Version.TryParse(name, out Version? v) ? v : null))
            .Where(v => v.Version != null)
            .OrderByDescending(v => v.Version)
            .Select(v => v.Name)
            .FirstOrDefault();
        using RegistryKey? key = newest != null ? versions.OpenSubKey(newest) : null;
        if (key?.GetValue("InstanceAPIPath") is not string path || path.Trim().Length == 0)
        {
            throw new AdbcException("The LocalDB installation has no InstanceAPIPath in the registry", AdbcStatusCode.NotFound);
        }
        return path.Trim();
    }
}
