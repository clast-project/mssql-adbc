// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Clast.Drivers.SqlServer.Tds;

/// <summary>TDS packet types (MS-TDS 2.2.3.1.1).</summary>
internal enum PacketType : byte
{
    SqlBatch = 0x01,
    Rpc = 0x03,
    TabularResult = 0x04,
    Attention = 0x06,
    BulkLoad = 0x07,
    Login7 = 0x10,
    Sspi = 0x11,
    PreLogin = 0x12,
}

/// <summary>Packet header status bits.</summary>
[Flags]
internal enum PacketStatus : byte
{
    Normal = 0x00,
    EndOfMessage = 0x01,
    Ignore = 0x02,
    ResetConnection = 0x08,
}

/// <summary>Data stream token types (MS-TDS 2.2.7).</summary>
internal enum TokenType : byte
{
    ReturnStatus = 0x79,
    ColMetadata = 0x81,
    AltMetadata = 0x88,
    DataClassification = 0xA3,
    TabName = 0xA4,
    ColInfo = 0xA5,
    Order = 0xA9,
    Error = 0xAA,
    Info = 0xAB,
    ReturnValue = 0xAC,
    LoginAck = 0xAD,
    FeatureExtAck = 0xAE,
    Row = 0xD1,
    NbcRow = 0xD2,
    AltRow = 0xD3,
    EnvChange = 0xE3,
    SessionState = 0xE4,
    Sspi = 0xED,
    FedAuthInfo = 0xEE,
    Done = 0xFD,
    DoneProc = 0xFE,
    DoneInProc = 0xFF,
}

/// <summary>DONE token status bits.</summary>
[Flags]
internal enum DoneStatus : ushort
{
    Final = 0x0000,
    More = 0x0001,
    Error = 0x0002,
    InTransaction = 0x0004,
    Count = 0x0010,
    Attention = 0x0020,
    ServerError = 0x0100,
}

/// <summary>ENVCHANGE types.</summary>
internal enum EnvChangeType : byte
{
    Database = 1,
    Language = 2,
    CharacterSet = 3,
    PacketSize = 4,
    UnicodeSortLocale = 5,
    UnicodeComparisonFlags = 6,
    SqlCollation = 7,
    BeginTransaction = 8,
    CommitTransaction = 9,
    RollbackTransaction = 10,
    EnlistDtcTransaction = 11,
    DefectTransaction = 12,
    DatabaseMirroringPartner = 13,
    PromoteTransaction = 15,
    TransactionManagerAddress = 16,
    TransactionEnded = 17,
    ResetConnectionAck = 18,
    UserInstanceName = 19,
    Routing = 20,
}

/// <summary>PRELOGIN option tokens.</summary>
internal enum PreLoginOption : byte
{
    Version = 0x00,
    Encryption = 0x01,
    InstOpt = 0x02,
    ThreadId = 0x03,
    Mars = 0x04,
    TraceId = 0x05,
    FedAuthRequired = 0x06,
    NonceOpt = 0x07,
    Terminator = 0xFF,
}

/// <summary>PRELOGIN ENCRYPTION values.</summary>
internal enum PreLoginEncryption : byte
{
    Off = 0x00,
    On = 0x01,
    NotSupported = 0x02,
    Required = 0x03,
}

/// <summary>LOGIN7 feature extension identifiers.</summary>
internal enum FeatureId : byte
{
    SessionRecovery = 0x01,
    FedAuth = 0x02,
    ColumnEncryption = 0x04,
    GlobalTransactions = 0x05,
    AzureSqlSupport = 0x08,
    DataClassification = 0x09,
    Utf8Support = 0x0A,
    SqlDnsCaching = 0x0B,
    JsonSupport = 0x0D,
    VectorSupport = 0x0E,
    UserAgent = 0x10,
    Terminator = 0xFF,
}

internal static class TdsConstants
{
    public const int HeaderLength = 8;

    /// <summary>Packet size used until the server acknowledges the negotiated size.</summary>
    public const int InitialPacketSize = 4096;

    public const int MinPacketSize = 512;
    public const int MaxPacketSize = 32767;

    /// <summary>TDS 7.4, written little-endian in LOGIN7.</summary>
    public const uint Tds74Version = 0x74000004;

    /// <summary>TDS 8.0, used with strict encryption.</summary>
    public const uint Tds80Version = 0x08000000;

    public const string Tds8AlpnProtocol = "tds/8.0";

    public const int DefaultPort = 1433;
}
