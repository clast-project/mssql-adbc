// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Clast.Drivers.SqlServer.Tds;

/// <summary>
/// Reads the payload of a TDS message as one logical byte sequence, stripping
/// packet headers as it goes. Raw bytes are read from the stream in large
/// chunks, so a single receive usually covers several packets.
/// </summary>
/// <remarks>
/// The hot path is a value that lies entirely within the current packet and
/// has already been received; everything else falls through to the slow path.
/// </remarks>
internal sealed class PacketReader
{
    private const int MinBufferSize = 64 * 1024;

    private Stream _stream;
    private byte[] _buffer;
    private byte[] _scratch = new byte[256];

    // Absolute indexes into _buffer.
    private int _position;   // next unread payload byte
    private int _end;        // end of bytes received from the stream
    private int _packetEnd;  // end of the current packet's payload (may be beyond _end)

    private bool _lastPacket = true;

    public PacketReader(Stream stream, int packetSize)
    {
        _stream = stream;
        _buffer = new byte[Math.Max(MinBufferSize, packetSize * 2)];
    }

    public Stream Stream
    {
        get => _stream;
        set => _stream = value;
    }

    /// <summary>Changes the packet size; only valid between messages.</summary>
    public void SetPacketSize(int packetSize)
    {
        int size = Math.Max(MinBufferSize, packetSize * 2);
        if (size > _buffer.Length)
        {
            byte[] buffer = new byte[size];
            int count = _end - _position;
            _buffer.AsSpan(_position, count).CopyTo(buffer);
            _packetEnd -= _position;
            _end = count;
            _position = 0;
            _buffer = buffer;
        }
    }

    /// <summary>True when the current message has been read to the end.</summary>
    public bool IsMessageComplete => _lastPacket && _position == _packetEnd;

    /// <summary>Starts reading the next message from the server.</summary>
    public void BeginMessage()
    {
        if (!IsMessageComplete)
        {
            throw new TdsProtocolException("The previous message was not fully read");
        }
        _lastPacket = false;
    }

    /// <summary>Bytes of the current packet that are buffered and contiguous at the read position.</summary>
    public int Available
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Math.Min(_end, _packetEnd) - _position;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadByte()
    {
        if (Available > 0)
        {
            return _buffer[_position++];
        }
        return ReadByteSlow();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort ReadUInt16()
    {
        if (Available >= 2)
        {
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(_position));
            _position += 2;
            return value;
        }
        Span<byte> bytes = stackalloc byte[2];
        ReadBytes(bytes);
        return BinaryPrimitives.ReadUInt16LittleEndian(bytes);
    }

    public short ReadInt16() => unchecked((short)ReadUInt16());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadInt32()
    {
        if (Available >= 4)
        {
            int value = BinaryPrimitives.ReadInt32LittleEndian(_buffer.AsSpan(_position));
            _position += 4;
            return value;
        }
        Span<byte> bytes = stackalloc byte[4];
        ReadBytes(bytes);
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    public uint ReadUInt32() => unchecked((uint)ReadInt32());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long ReadInt64()
    {
        if (Available >= 8)
        {
            long value = BinaryPrimitives.ReadInt64LittleEndian(_buffer.AsSpan(_position));
            _position += 8;
            return value;
        }
        Span<byte> bytes = stackalloc byte[8];
        ReadBytes(bytes);
        return BinaryPrimitives.ReadInt64LittleEndian(bytes);
    }

    public ulong ReadUInt64() => unchecked((ulong)ReadInt64());

    public void ReadBytes(Span<byte> destination)
    {
        while (destination.Length > 0)
        {
            int available = Available;
            if (available == 0)
            {
                EnsureAvailable();
                available = Available;
            }
            int count = Math.Min(available, destination.Length);
            _buffer.AsSpan(_position, count).CopyTo(destination);
            _position += count;
            destination = destination.Slice(count);
        }
    }

    public byte[] ReadByteArray(int length)
    {
        byte[] result = new byte[length];
        ReadBytes(result);
        return result;
    }

    /// <summary>
    /// Returns the next <paramref name="length"/> bytes. The span refers to
    /// reader-owned memory and is only valid until the next read.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> ReadSpan(int length)
    {
        if (Available >= length)
        {
            ReadOnlySpan<byte> span = _buffer.AsSpan(_position, length);
            _position += length;
            return span;
        }
        return ReadSpanSlow(length);
    }

    public void Skip(int length)
    {
        while (length > 0)
        {
            int available = Available;
            if (available == 0)
            {
                EnsureAvailable();
                available = Available;
            }
            int count = Math.Min(available, length);
            _position += count;
            length -= count;
        }
    }

    /// <summary>Reads <paramref name="chars"/> UTF-16LE characters.</summary>
    public string ReadUtf16String(int chars)
    {
        if (chars == 0)
        {
            return string.Empty;
        }
        return MemoryMarshal.Cast<byte, char>(ReadSpan(chars * 2)).ToString();
    }

    /// <summary>Reads a B_VARCHAR (one-byte character count).</summary>
    public string ReadBVarChar() => ReadUtf16String(ReadByte());

    /// <summary>Reads a US_VARCHAR (two-byte character count).</summary>
    public string ReadUsVarChar() => ReadUtf16String(ReadUInt16());

    private byte ReadByteSlow()
    {
        EnsureAvailable();
        return _buffer[_position++];
    }

    private ReadOnlySpan<byte> ReadSpanSlow(int length)
    {
        if (_scratch.Length < length)
        {
            _scratch = new byte[Math.Max(length, _scratch.Length * 2)];
        }
        Span<byte> span = _scratch.AsSpan(0, length);
        ReadBytes(span);
        return span;
    }

    /// <summary>Ensures at least one payload byte is available, crossing packet boundaries as needed.</summary>
    private void EnsureAvailable()
    {
        while (true)
        {
            if (_position < _packetEnd)
            {
                if (_position < _end)
                {
                    return;
                }
                Fill();
                continue;
            }

            if (_lastPacket)
            {
                throw new TdsProtocolException("Attempted to read past the end of the message");
            }
            ReadPacketHeader();
        }
    }

    private void ReadPacketHeader()
    {
        while (_end - _position < TdsConstants.HeaderLength)
        {
            Fill();
        }

        byte status = _buffer[_position + 1];
        int length = BinaryPrimitives.ReadUInt16BigEndian(_buffer.AsSpan(_position + 2));
        if (length < TdsConstants.HeaderLength)
        {
            throw new TdsProtocolException($"Invalid packet length {length}");
        }

        _position += TdsConstants.HeaderLength;
        _packetEnd = _position + length - TdsConstants.HeaderLength;
        _lastPacket = (status & (byte)PacketStatus.EndOfMessage) != 0;
        if (TdsTrace.Enabled)
        {
            TdsTrace.Packet("<-", _buffer[_position - TdsConstants.HeaderLength], status, length);
        }
    }

    private void Fill()
    {
        if (_end == _buffer.Length)
        {
            if (_position == 0)
            {
                throw new TdsProtocolException("Read buffer exhausted");
            }
            int count = _end - _position;
            Buffer.BlockCopy(_buffer, _position, _buffer, 0, count);
            _packetEnd -= _position;
            _end = count;
            _position = 0;
        }

        int read = _stream.Read(_buffer, _end, _buffer.Length - _end);
        if (read == 0)
        {
            throw new TdsProtocolException("The server closed the connection");
        }
        _end += read;
    }
}
