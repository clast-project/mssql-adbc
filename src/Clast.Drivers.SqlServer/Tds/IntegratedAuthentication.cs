// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

#if NET
using System.Net.Security;
#else
using System.Runtime.InteropServices;
#endif

namespace Clast.Drivers.SqlServer.Tds;

/// <summary>
/// The client side of an SPNEGO (Negotiate) exchange for integrated
/// authentication: <see cref="NegotiateAuthentication"/> on .NET (SSPI on
/// Windows, GSSAPI elsewhere); on netstandard2.0, which lacks it, Windows SSPI
/// directly.
/// </summary>
internal sealed class IntegratedAuthentication : IDisposable
{
#if NET
    private readonly NegotiateAuthentication _negotiate;

    public IntegratedAuthentication(string targetName)
    {
        _negotiate = new NegotiateAuthentication(new NegotiateAuthenticationClientOptions
        {
            Package = "Negotiate",
            TargetName = targetName,
        });
    }

    /// <summary>The next token to send, given the server's last (empty at first); null with <paramref name="failure"/> set when the exchange failed.</summary>
    public byte[]? Next(ReadOnlySpan<byte> incoming, out string? failure)
    {
        byte[]? outgoing = _negotiate.GetOutgoingBlob(incoming, out NegotiateAuthenticationStatusCode status);
        if (status is NegotiateAuthenticationStatusCode.Completed or NegotiateAuthenticationStatusCode.ContinueNeeded)
        {
            failure = null;
            return outgoing ?? [];
        }
        failure = status.ToString();
        return null;
    }

    public void Dispose() => _negotiate.Dispose();
#else
    private const int SecPkgCredOutbound = 2;
    private const int SecurityNativeDrep = 0x10;
    private const int IscReqAllocateMemory = 0x100;
    private const int IscReqConnection = 0x800;
    private const int SecbufferToken = 2;
    private const int SecEOk = 0;
    private const int SecIContinueNeeded = 0x00090312;
    private const int SecICompleteNeeded = 0x00090313;
    private const int SecICompleteAndContinue = 0x00090314;

    private readonly string _targetName;
    private SecHandle _credentials;
    private SecHandle _context;
    private bool _haveCredentials;
    private bool _haveContext;

    public IntegratedAuthentication(string targetName)
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
        {
            throw new Apache.Arrow.Adbc.AdbcException(
                "Integrated authentication needs Windows in the netstandard2.0 build of this driver",
                Apache.Arrow.Adbc.AdbcStatusCode.NotImplemented);
        }
        _targetName = targetName;
        int status = AcquireCredentialsHandleW(null, "Negotiate", SecPkgCredOutbound, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref _credentials, out _);
        if (status != SecEOk)
        {
            throw new Apache.Arrow.Adbc.AdbcException($"Could not acquire Windows credentials (0x{status:X8})", Apache.Arrow.Adbc.AdbcStatusCode.Unauthenticated);
        }
        _haveCredentials = true;
    }

    /// <summary>The next token to send, given the server's last (empty at first); null with <paramref name="failure"/> set when the exchange failed.</summary>
    public unsafe byte[]? Next(ReadOnlySpan<byte> incoming, out string? failure)
    {
        fixed (byte* input = incoming)
        {
            var inBuffer = new SecBuffer { Count = incoming.Length, Type = SecbufferToken, Buffer = (IntPtr)input };
            var inDesc = new SecBufferDesc { Version = 0, Count = 1, Buffers = (IntPtr)(&inBuffer) };
            var outBuffer = new SecBuffer { Count = 0, Type = SecbufferToken, Buffer = IntPtr.Zero };
            var outDesc = new SecBufferDesc { Version = 0, Count = 1, Buffers = (IntPtr)(&outBuffer) };

            SecHandle previous = _context;
            int status = InitializeSecurityContextW(
                ref _credentials, _haveContext ? &previous : null, _targetName, IscReqAllocateMemory | IscReqConnection, 0, SecurityNativeDrep,
                incoming.IsEmpty ? null : &inDesc, 0, ref _context, &outDesc, out _, out _);
            _haveContext = true;
            try
            {
                if (status is SecICompleteNeeded or SecICompleteAndContinue)
                {
                    int complete = CompleteAuthToken(ref _context, &outDesc);
                    status = complete != SecEOk ? complete : status == SecICompleteNeeded ? SecEOk : SecIContinueNeeded;
                }
                if (status is not (SecEOk or SecIContinueNeeded))
                {
                    failure = $"0x{status:X8}";
                    return null;
                }
                failure = null;
                byte[] token = new byte[outBuffer.Count];
                if (outBuffer.Count > 0)
                {
                    Marshal.Copy(outBuffer.Buffer, token, 0, outBuffer.Count);
                }
                return token;
            }
            finally
            {
                if (outBuffer.Buffer != IntPtr.Zero)
                {
                    FreeContextBuffer(outBuffer.Buffer);
                }
            }
        }
    }

    public void Dispose()
    {
        if (_haveContext)
        {
            DeleteSecurityContext(ref _context);
            _haveContext = false;
        }
        if (_haveCredentials)
        {
            FreeCredentialsHandle(ref _credentials);
            _haveCredentials = false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecHandle
    {
        public IntPtr Lower;
        public IntPtr Upper;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecBuffer
    {
        public int Count;
        public int Type;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecBufferDesc
    {
        public int Version;
        public int Count;
        public IntPtr Buffers;
    }

    [DllImport("secur32.dll", CharSet = CharSet.Unicode)]
    private static extern int AcquireCredentialsHandleW(
        string? principal, string package, int credentialUse, IntPtr logonId, IntPtr authData,
        IntPtr getKeyFunction, IntPtr getKeyArgument, ref SecHandle credential, out long expiry);

    [DllImport("secur32.dll", CharSet = CharSet.Unicode)]
    private static extern unsafe int InitializeSecurityContextW(
        ref SecHandle credential, SecHandle* context, string targetName, int contextRequirements, int reserved1, int targetDataRep,
        SecBufferDesc* input, int reserved2, ref SecHandle newContext, SecBufferDesc* output, out int contextAttributes, out long expiry);

    [DllImport("secur32.dll")]
    private static extern unsafe int CompleteAuthToken(ref SecHandle context, SecBufferDesc* token);

    [DllImport("secur32.dll")]
    private static extern int FreeContextBuffer(IntPtr buffer);

    [DllImport("secur32.dll")]
    private static extern int DeleteSecurityContext(ref SecHandle context);

    [DllImport("secur32.dll")]
    private static extern int FreeCredentialsHandle(ref SecHandle credential);
#endif
}
