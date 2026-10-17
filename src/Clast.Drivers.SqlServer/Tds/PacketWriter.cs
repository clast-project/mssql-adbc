// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Clast.Drivers.SqlServer.Tds;

/// <summary>
/// Writes a TDS message as a sequence of packets of the negotiated size.
/// Values are written into the current packet's buffer; a full packet is sent
/// as soon as it fills.
/// </summary>
internal sealed class PacketWriter
{
    private Stream _stream;
    private byte[] _buffer;
    private int _position;
    private PacketType _type;
    private byte _packetId;
    private long _sentBytes;
    private byte[] _scratch = new byte[64];
    private bool _inScratch;

    public PacketWriter(Stream stream, int packetSize)
    {
        _stream = stream;
        _buffer = new byte[packetSize];
        _position = TdsConstants.HeaderLength;
    }

    public Stream Stream
    {
        get => _stream;
        set => _stream = value;
    }

    public int PacketSize => _buffer.Length;

    /// <summary>Payload bytes written in the current message so far.</summary>
    public long BytesWritten => _sentBytes + _position - TdsConstants.HeaderLength;

    /// <summary>Changes the packet size; only valid between messages.</summary>
    public void SetPacketSize(int packetSize)
    {
        if (packetSize != _buffer.Length)
        {
            _buffer = new byte[packetSize];
        }
    }

    public void BeginMessage(PacketType type)
    {
        _type = type;
        _packetId = 1;
        _sentBytes = 0;
        _position = TdsConstants.HeaderLength;
    }

    public void EndMessage()
    {
        SendPacket(PacketStatus.EndOfMessage);
        _stream.Flush();
    }

    /// <summary>
    /// Ends the current message with the IGNORE bit set, so the server
    /// discards everything sent for it.
    /// </summary>
    public void AbortMessage()
    {
        SendPacket(PacketStatus.EndOfMessage | PacketStatus.Ignore);
        _stream.Flush();
    }

    /// <summary>
    /// Space for up to <paramref name="size"/> bytes, to fill and then commit
    /// with <see cref="Advance"/>: in the packet when it fits there (one bounds
    /// check for a whole value), otherwise in a scratch area that
    /// <see cref="Advance"/> writes on across packets.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<byte> GetSpan(int size)
    {
        if (_buffer.Length - _position >= size)
        {
            return _buffer.AsSpan(_position, size);
        }
        return GetScratch(size);
    }

    private Span<byte> GetScratch(int size)
    {
        if (_scratch.Length < size)
        {
            _scratch = new byte[Math.Max(size, _scratch.Length * 2)];
        }
        _inScratch = true;
        return _scratch.AsSpan(0, size);
    }

    /// <summary>Commits <paramref name="count"/> bytes written into the span from the last <see cref="GetSpan"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Advance(int count)
    {
        if (!_inScratch)
        {
            _position += count;
            return;
        }
        _inScratch = false;
        WriteBytesSlow(_scratch.AsSpan(0, count));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteByte(byte value)
    {
        if (_position == _buffer.Length)
        {
            SendPacket(PacketStatus.Normal);
        }
        _buffer[_position++] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUInt16(ushort value)
    {
        if (_buffer.Length - _position >= 2)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(_position), value);
            _position += 2;
        }
        else
        {
            Span<byte> bytes = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
            WriteBytesSlow(bytes);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteInt32(int value)
    {
        if (_buffer.Length - _position >= 4)
        {
            BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_position), value);
            _position += 4;
        }
        else
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            WriteBytesSlow(bytes);
        }
    }

    public void WriteUInt32(uint value) => WriteInt32(unchecked((int)value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteInt64(long value)
    {
        if (_buffer.Length - _position >= 8)
        {
            BinaryPrimitives.WriteInt64LittleEndian(_buffer.AsSpan(_position), value);
            _position += 8;
        }
        else
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
            WriteBytesSlow(bytes);
        }
    }

    public void WriteUInt64(ulong value) => WriteInt64(unchecked((long)value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        if (_buffer.Length - _position >= bytes.Length)
        {
            bytes.CopyTo(_buffer.AsSpan(_position));
            _position += bytes.Length;
        }
        else
        {
            WriteBytesSlow(bytes);
        }
    }

    /// <summary>Writes a string as UTF-16LE without a length prefix.</summary>
    public void WriteUtf16(ReadOnlySpan<char> value)
    {
        WriteBytes(MemoryMarshal.AsBytes(value));
    }

    /// <summary>Writes a B_VARCHAR: one-byte character count then UTF-16LE.</summary>
    public void WriteBVarChar(string value)
    {
        WriteByte(checked((byte)value.Length));
        WriteUtf16(value);
    }

    /// <summary>Writes a US_VARCHAR: two-byte character count then UTF-16LE.</summary>
    public void WriteUsVarChar(string value)
    {
        WriteUInt16(checked((ushort)value.Length));
        WriteUtf16(value);
    }

    private void WriteBytesSlow(ReadOnlySpan<byte> bytes)
    {
        while (bytes.Length > 0)
        {
            if (_position == _buffer.Length)
            {
                SendPacket(PacketStatus.Normal);
            }
            int count = Math.Min(bytes.Length, _buffer.Length - _position);
            bytes.Slice(0, count).CopyTo(_buffer.AsSpan(_position));
            _position += count;
            bytes = bytes.Slice(count);
        }
    }

    private void SendPacket(PacketStatus status)
    {
        byte[] buffer = _buffer;
        buffer[0] = (byte)_type;
        buffer[1] = (byte)status;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2), (ushort)_position);
        buffer[4] = 0; // SPID
        buffer[5] = 0;
        buffer[6] = _packetId++;
        buffer[7] = 0; // window
        if (TdsTrace.Enabled)
        {
            TdsTrace.Packet("->", buffer[0], buffer[1], _position);
        }
        _stream.Write(buffer, 0, _position);
        _sentBytes += _position - TdsConstants.HeaderLength;
        _position = TdsConstants.HeaderLength;
    }
}
