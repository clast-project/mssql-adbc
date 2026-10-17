// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers.Binary;
using System.Text;
using System.Runtime.InteropServices;

namespace Clast.Drivers.SqlServer.Tds;

/// <summary>
/// Builds PRELOGIN and LOGIN7 payloads (MS-TDS 2.2.6.4 and 2.2.6.5).
/// </summary>
internal static class Login7
{
    private const int FixedLength = 94;
    private const int ClientProgramVersion = 0x00010000;

    /// <summary>Builds the PRELOGIN request payload.</summary>
    /// <param name="federatedAuthentication">Add FEDAUTHREQUIRED, as a client offering Entra authentication must.</param>
    public static byte[] BuildPreLogin(PreLoginEncryption encryption, bool federatedAuthentication = false)
    {
        // Options: VERSION(6), ENCRYPTION(1), INSTOPT(1), THREADID(4), MARS(1), TRACEID(36),
        // [FEDAUTHREQUIRED(1)], terminator. Fabric Warehouse refuses a routed login without TRACEID.
        ReadOnlySpan<(PreLoginOption Option, int Length)> options = federatedAuthentication
            ?
            [
                (PreLoginOption.Version, 6),
                (PreLoginOption.Encryption, 1),
                (PreLoginOption.InstOpt, 1),
                (PreLoginOption.ThreadId, 4),
                (PreLoginOption.Mars, 1),
                (PreLoginOption.TraceId, 36),
                (PreLoginOption.FedAuthRequired, 1),
            ]
            :
            [
                (PreLoginOption.Version, 6),
                (PreLoginOption.Encryption, 1),
                (PreLoginOption.InstOpt, 1),
                (PreLoginOption.ThreadId, 4),
                (PreLoginOption.Mars, 1),
                (PreLoginOption.TraceId, 36),
            ];

        int headerLength = options.Length * 5 + 1;
        int totalLength = headerLength;
        foreach ((PreLoginOption _, int length) in options)
        {
            totalLength += length;
        }

        byte[] payload = new byte[totalLength];
        int header = 0;
        int data = headerLength;
        foreach ((PreLoginOption option, int length) in options)
        {
            payload[header] = (byte)option;
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(header + 1), (ushort)data);
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(header + 3), (ushort)length);
            header += 5;
            data += length;
        }
        payload[header] = (byte)PreLoginOption.Terminator;

        int position = headerLength;
        // VERSION: major, minor, build (big-endian), sub-build.
        payload[position++] = 0;
        payload[position++] = 1;
        position += 4;
        payload[position++] = (byte)encryption;
        payload[position++] = 0; // INSTOPT: default instance
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(position), Environment.CurrentManagedThreadId);
        position += 4;
        payload[position++] = 0; // MARS off
        // TRACEID: a new client connection ID and activity ID, then the activity sequence.
        Guid.NewGuid().TryWriteBytes(payload.AsSpan(position));
        Guid.NewGuid().TryWriteBytes(payload.AsSpan(position + 16));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(position + 32), 0);
        position += 36;
        if (federatedAuthentication)
        {
            payload[position] = 1; // FEDAUTHREQUIRED
        }

        return payload;
    }

    /// <summary>
    /// The server's ENCRYPTION choice from a PRELOGIN response, and whether it
    /// answered FEDAUTHREQUIRED with 1 (echoed back in the FEDAUTH feature).
    /// </summary>
    public static (PreLoginEncryption Encryption, bool FedAuthRequired) ParsePreLogin(ReadOnlySpan<byte> payload)
    {
        PreLoginEncryption? encryption = null;
        bool fedAuthRequired = false;
        int index = 0;
        while (index < payload.Length && payload[index] != (byte)PreLoginOption.Terminator)
        {
            if (index + 5 > payload.Length)
            {
                break;
            }
            byte option = payload[index];
            int offset = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(index + 1));
            int length = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(index + 3));
            if (option == (byte)PreLoginOption.Encryption && length >= 1 && offset < payload.Length)
            {
                encryption = (PreLoginEncryption)payload[offset];
            }
            else if (option == (byte)PreLoginOption.FedAuthRequired && length >= 1 && offset < payload.Length)
            {
                fedAuthRequired = payload[offset] == 1;
            }
            index += 5;
        }
        return encryption is PreLoginEncryption value
            ? (value, fedAuthRequired)
            : throw new TdsProtocolException("The PRELOGIN response did not include an ENCRYPTION option");
    }

    /// <summary>FEDAUTH feature-extension libraries.</summary>
    public const byte FedAuthSecurityToken = 0x01;
    public const byte FedAuthMsal = 0x02;

    /// <summary>The MSAL workflow SqlClient sends for token-based (non-password) methods.</summary>
    public const byte MsalWorkflowDefault = 0x03;

    /// <summary>
    /// The FEDAUTH feature extension: an options byte (library in the upper
    /// seven bits, the echo of the server's FEDAUTHREQUIRED in the lowest),
    /// then for a security token its length and UTF-16 bytes, or for MSAL the
    /// workflow, which makes the server reply with FEDAUTHINFO.
    /// </summary>
    public static byte[] BuildFedAuthFeature(byte library, bool echo, string? token)
    {
        byte options = (byte)((library << 1) | (echo ? 1 : 0));
        byte[] data;
        if (library == FedAuthSecurityToken)
        {
            byte[] tokenBytes = Encoding.Unicode.GetBytes(token!);
            data = new byte[1 + 4 + tokenBytes.Length];
            data[0] = options;
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(1), tokenBytes.Length);
            tokenBytes.CopyTo(data, 5);
        }
        else
        {
            data = [options, MsalWorkflowDefault];
        }

        var feature = new byte[5 + data.Length];
        feature[0] = (byte)FeatureId.FedAuth;
        BinaryPrimitives.WriteInt32LittleEndian(feature.AsSpan(1), data.Length);
        data.CopyTo(feature, 5);
        return feature;
    }

    /// <summary>
    /// The packet size to ask for. Over TLS, .NET Framework's SslStream splits
    /// a packet larger than 16 KB (one TLS record's plaintext) into records
    /// that SQL Server refuses (error 4014, "a fatal error occurred while
    /// reading the input stream"), where .NET's split is accepted, so the
    /// netstandard2.0 build asks for no more than 16384 bytes.
    /// </summary>
    internal static int RequestedPacketSize(int packetSize, bool encryptedAfterLogin) =>
#if NET
        packetSize;
#else
        encryptedAfterLogin ? Math.Min(packetSize, MaxTlsRecordPlaintext) : packetSize;

    private const int MaxTlsRecordPlaintext = 16384;
#endif

    /// <summary>Builds the LOGIN7 record, including feature extensions.</summary>
    /// <param name="fedAuth">A FEDAUTH feature extension (Entra authentication), which replaces the other credentials.</param>
    public static byte[] Build(
        SqlServerConnectionSettings settings,
        uint tdsVersion,
        ReadOnlySpan<byte> sspi,
        ReadOnlySpan<byte> fedAuth = default,
        bool encryptedAfterLogin = false,
        ReadOnlySpan<byte> sessionRecovery = default)
    {
        bool useSspi = settings.IntegratedSecurity && fedAuth.IsEmpty;
        bool credentials = !useSspi && fedAuth.IsEmpty;
        string hostName = Environment.MachineName;
        string userName = credentials ? settings.Username ?? "" : "";
        string password = credentials ? settings.Password ?? "" : "";
        string appName = settings.ApplicationName;
        string serverName = settings.LoginServerName;
        string clientInterface = "Clast.Drivers.SqlServer";
        string language = "";
        string database = settings.Database;

        // Feature extensions: UTF-8 support; native json and vector types (version 1 each,
        // ignored by servers that do not know them); enhanced routing (no data); then the terminator.
        ReadOnlySpan<byte> standardFeatures =
        [
            (byte)FeatureId.Utf8Support, 0, 0, 0, 0,
            (byte)FeatureId.JsonSupport, 1, 0, 0, 0, 1,
            (byte)FeatureId.VectorSupport, 1, 0, 0, 0, 1,
            (byte)FeatureId.EnhancedRoutingSupport, 0, 0, 0, 0,
        ];
        byte[] featureExt = [.. fedAuth, .. sessionRecovery, .. standardFeatures, (byte)FeatureId.Terminator];

        int variableLength =
            2 * (hostName.Length + userName.Length + password.Length + appName.Length + serverName.Length
                 + clientInterface.Length + language.Length + database.Length)
            + 4 // feature extension offset
            + sspi.Length;
        int featureExtOffset = FixedLength + variableLength;
        int totalLength = featureExtOffset + featureExt.Length;

        byte[] record = new byte[totalLength];
        Span<byte> span = record;

        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(0), totalLength);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4), tdsVersion);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(8), RequestedPacketSize(settings.PacketSize, encryptedAfterLogin));
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(12), ClientProgramVersion);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(16), Environment.ProcessId);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(20), 0); // connection ID

        // OptionFlags1: USE_DB_ON | INIT_DB_FATAL | SET_LANG_ON.
        span[24] = 0xE0;
        // OptionFlags2: INIT_LANG_FATAL | ODBC_ON | INTEGRATED_SECURITY.
        span[25] = (byte)(0x01 | 0x02 | (useSspi ? 0x80 : 0));
        // TypeFlags: SQL_DFLT, and fReadOnlyIntent (ApplicationIntent=ReadOnly).
        span[26] = settings.ReadOnlyIntent ? (byte)0x20 : (byte)0;
        // OptionFlags3: fExtension.
        span[27] = 0x10;
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(28), 0); // client time zone (unused)
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(32), 0); // client LCID (unused)

        int fixedPosition = 36;
        int dataPosition = FixedLength;

        void WriteString(Span<byte> target, string value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(fixedPosition), (ushort)dataPosition);
            BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(fixedPosition + 2), (ushort)value.Length);
            fixedPosition += 4;
            MemoryMarshal.AsBytes(value.AsSpan()).CopyTo(target.Slice(dataPosition));
            dataPosition += value.Length * 2;
        }

        WriteString(span, hostName);
        WriteString(span, userName);

        // Password, obfuscated.
        int passwordStart = dataPosition;
        WriteString(span, password);
        ObfuscatePassword(span.Slice(passwordStart, password.Length * 2));

        WriteString(span, appName);
        WriteString(span, serverName);

        // Extension: points at a DWORD holding the feature extension offset.
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(fixedPosition), (ushort)dataPosition);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(fixedPosition + 2), 4);
        fixedPosition += 4;
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(dataPosition), featureExtOffset);
        dataPosition += 4;

        WriteString(span, clientInterface);
        WriteString(span, language);
        WriteString(span, database);

        // ClientID (6 bytes): left as zeros.
        fixedPosition += 6;

        // SSPI.
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(fixedPosition), (ushort)dataPosition);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(fixedPosition + 2), (ushort)Math.Min(sspi.Length, ushort.MaxValue));
        fixedPosition += 4;
        sspi.CopyTo(span.Slice(dataPosition));
        dataPosition += sspi.Length;

        // AtchDBFile and ChangePassword: empty, pointing at the end of the data.
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(fixedPosition), (ushort)dataPosition);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(fixedPosition + 2), 0);
        fixedPosition += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(fixedPosition), (ushort)dataPosition);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(fixedPosition + 2), 0);
        fixedPosition += 4;

        // cbSSPILong: used when the SSPI blob does not fit in 16 bits.
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(fixedPosition), sspi.Length >= ushort.MaxValue ? sspi.Length : 0);

        featureExt.CopyTo(span.Slice(featureExtOffset));
        return record;
    }

    /// <summary>Swaps the nibbles of each byte and XORs it with 0xA5.</summary>
    private static void ObfuscatePassword(Span<byte> bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            bytes[i] = (byte)(((b << 4) | (b >> 4)) ^ 0xA5);
        }
    }
}
