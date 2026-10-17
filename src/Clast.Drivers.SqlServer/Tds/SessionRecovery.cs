// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers.Binary;
using System.Text;

namespace Clast.Drivers.SqlServer.Tds;

/// <summary>
/// What a server needs to recreate a session on a new connection (TDS
/// session recovery, feature extension 0x01): the state the session started
/// with, from the login's FEATUREEXTACK, and how it has changed since, from
/// SESSIONSTATE tokens. The states are opaque to the client; the database,
/// language and collation it tracks itself, from ENVCHANGE.
/// </summary>
internal sealed class SessionRecovery
{
    private const int MaxStates = 256;

    private sealed class State(byte[] data, uint version, bool recoverable)
    {
        public byte[] Data { get; set; } = data;
        public uint Version { get; set; } = version;
        public bool Recoverable { get; set; } = recoverable;
    }

    private readonly byte[]?[] _initial = new byte[MaxStates][];
    private readonly State?[] _current = new State[MaxStates];
    private int _unrecoverable;

    public string? InitialDatabase { get; set; }

    public string? InitialLanguage { get; set; }

    public byte[]? InitialCollation { get; set; }

    /// <summary>For a session recovered from <paramref name="original"/>: it keeps the original's initial state.</summary>
    public SessionRecovery(SessionRecovery? original = null)
    {
        if (original != null)
        {
            Array.Copy(original._initial, _initial, MaxStates);
            InitialDatabase = original.InitialDatabase;
            InitialLanguage = original.InitialLanguage;
            InitialCollation = original.InitialCollation;
        }
    }

    /// <summary>Whether the server has reported no state it cannot recover (temporary tables, for example).</summary>
    public bool Recoverable => _unrecoverable == 0;

    /// <summary>
    /// The FEATUREEXTACK data: the session's states, each an ID, a length
    /// (a byte, or 0xFF and a DWORD) and a value. On a first login they are
    /// the initial states; on a recovery login, the restored current ones.
    /// </summary>
    public void Acknowledge(ReadOnlySpan<byte> data, bool recovered)
    {
        int i = 0;
        while (i < data.Length)
        {
            byte id = data[i++];
            int length = ReadLength(data, ref i);
            if (length < 0 || length > data.Length - i)
            {
                throw new TdsProtocolException("Invalid session recovery acknowledgement");
            }
            byte[] value = data.Slice(i, length).ToArray();
            i += length;
            if (recovered)
            {
                _current[id] = new State(value, 0, recoverable: true);
            }
            else
            {
                _initial[id] = value;
            }
        }
    }

    /// <summary>One state from a SESSIONSTATE token: kept unless a later sequence number has already been seen for it.</summary>
    public void Update(byte id, byte[] value, uint sequence, bool recoverable)
    {
        State? state = _current[id];
        if (state == null)
        {
            _current[id] = new State(value, sequence, recoverable);
            if (!recoverable)
            {
                _unrecoverable++;
            }
            return;
        }
        if (state.Version > sequence)
        {
            return;
        }
        if (state.Recoverable != recoverable)
        {
            _unrecoverable += recoverable ? -1 : 1;
            state.Recoverable = recoverable;
        }
        state.Data = value;
        state.Version = sequence;
    }

    /// <summary>
    /// The FEATUREEXT entry for a recovery login: the initial state, then
    /// what differs from it now (the database, collation and language given,
    /// and the changed states).
    /// </summary>
    public byte[] BuildFeature(string? database, byte[]? collation, string? language)
    {
        var initial = new List<byte>();
        WriteIdentifier(initial, InitialDatabase);
        WriteCollation(initial, InitialCollation);
        WriteIdentifier(initial, InitialLanguage);
        for (int id = 0; id < MaxStates; id++)
        {
            if (_initial[id] is byte[] value)
            {
                WriteState(initial, (byte)id, value);
            }
        }

        var current = new List<byte>();
        WriteIdentifier(current, database != InitialDatabase ? database : null);
        WriteCollation(current, collation != null && !collation.AsSpan().SequenceEqual(InitialCollation) ? collation : null);
        WriteIdentifier(current, language != InitialLanguage ? language : null);
        for (int id = 0; id < MaxStates; id++)
        {
            if (_current[id] is State state && !(_initial[id] is byte[] start && start.AsSpan().SequenceEqual(state.Data)))
            {
                WriteState(current, (byte)id, state.Data);
            }
        }

        var feature = new byte[1 + 4 + 4 + initial.Count + 4 + current.Count];
        feature[0] = (byte)FeatureId.SessionRecovery;
        BinaryPrimitives.WriteInt32LittleEndian(feature.AsSpan(1), 8 + initial.Count + current.Count);
        BinaryPrimitives.WriteInt32LittleEndian(feature.AsSpan(5), initial.Count);
        initial.CopyTo(feature, 9);
        BinaryPrimitives.WriteInt32LittleEndian(feature.AsSpan(9 + initial.Count), current.Count);
        current.CopyTo(feature, 13 + initial.Count);
        return feature;
    }

    /// <summary>The FEATUREEXT entry for a first login: the feature with no data.</summary>
    public static ReadOnlySpan<byte> RequestFeature => [(byte)FeatureId.SessionRecovery, 0, 0, 0, 0];

    private static int ReadLength(ReadOnlySpan<byte> data, ref int i)
    {
        byte length = data[i++];
        if (length < 0xFF)
        {
            return length;
        }
        int value = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(i));
        i += 4;
        return value;
    }

    private static void WriteIdentifier(List<byte> output, string? value)
    {
        if (value == null)
        {
            output.Add(0);
            return;
        }
        output.Add((byte)value.Length);
        output.AddRange(Encoding.Unicode.GetBytes(value));
    }

    private static void WriteCollation(List<byte> output, byte[]? collation)
    {
        if (collation is not { Length: 5 })
        {
            output.Add(0);
            return;
        }
        output.Add(5);
        output.AddRange(collation);
    }

    private static void WriteState(List<byte> output, byte id, byte[] value)
    {
        output.Add(id);
        if (value.Length < 0xFF)
        {
            output.Add((byte)value.Length);
        }
        else
        {
            output.Add(0xFF);
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, value.Length);
            output.AddRange(length.ToArray());
        }
        output.AddRange(value);
    }
}
