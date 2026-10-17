// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers.Binary;

namespace Clast.Drivers.SqlServer.Tds;

/// <summary>
/// Carries the TLS handshake inside PRELOGIN packets, as TDS 7.x requires, and
/// passes bytes straight through once <see cref="Encapsulate"/> is cleared.
/// An <see cref="System.Net.Security.SslStream"/> sits on top of this stream.
/// </summary>
internal sealed class PreLoginTlsStream : Stream
{
    private readonly Stream _inner;
    private int _packetRemaining;

    public PreLoginTlsStream(Stream inner)
    {
        _inner = inner;
    }

    /// <summary>True while the handshake is in progress.</summary>
    public bool Encapsulate { get; set; } = true;

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (!Encapsulate)
        {
            return _inner.Read(buffer);
        }

        if (_packetRemaining == 0)
        {
            Span<byte> header = stackalloc byte[TdsConstants.HeaderLength];
            _inner.ReadExactly(header);
            _packetRemaining = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(2)) - TdsConstants.HeaderLength;
            if (_packetRemaining <= 0)
            {
                throw new TdsProtocolException("Invalid packet during TLS handshake");
            }
        }

        int read = _inner.Read(buffer.Slice(0, Math.Min(buffer.Length, _packetRemaining)));
        _packetRemaining -= read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!Encapsulate)
        {
            return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        if (_packetRemaining == 0)
        {
            byte[] header = new byte[TdsConstants.HeaderLength];
            await _inner.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            _packetRemaining = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2)) - TdsConstants.HeaderLength;
            if (_packetRemaining <= 0)
            {
                throw new TdsProtocolException("Invalid packet during TLS handshake");
            }
        }

        int read = await _inner.ReadAsync(buffer.Slice(0, Math.Min(buffer.Length, _packetRemaining)), cancellationToken).ConfigureAwait(false);
        _packetRemaining -= read;
        return read;
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (!Encapsulate)
        {
            _inner.Write(buffer);
            return;
        }

        const int maxPayload = TdsConstants.InitialPacketSize - TdsConstants.HeaderLength;
        byte[] packet = new byte[TdsConstants.InitialPacketSize];
        while (buffer.Length > 0)
        {
            int count = Math.Min(maxPayload, buffer.Length);
            WriteHeader(packet, count, last: count == buffer.Length);
            buffer.Slice(0, count).CopyTo(packet.AsSpan(TdsConstants.HeaderLength));
            _inner.Write(packet, 0, TdsConstants.HeaderLength + count);
            buffer = buffer.Slice(count);
        }
        _inner.Flush();
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!Encapsulate)
        {
            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            return;
        }

        const int maxPayload = TdsConstants.InitialPacketSize - TdsConstants.HeaderLength;
        byte[] packet = new byte[TdsConstants.InitialPacketSize];
        while (buffer.Length > 0)
        {
            int count = Math.Min(maxPayload, buffer.Length);
            WriteHeader(packet, count, last: count == buffer.Length);
            buffer.Span.Slice(0, count).CopyTo(packet.AsSpan(TdsConstants.HeaderLength));
            await _inner.WriteAsync(packet.AsMemory(0, TdsConstants.HeaderLength + count), cancellationToken).ConfigureAwait(false);
            buffer = buffer.Slice(count);
        }
        await _inner.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    private static void WriteHeader(byte[] packet, int payloadLength, bool last)
    {
        packet[0] = (byte)PacketType.PreLogin;
        packet[1] = (byte)(last ? PacketStatus.EndOfMessage : PacketStatus.Normal);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)(TdsConstants.HeaderLength + payloadLength));
        packet[4] = 0;
        packet[5] = 0;
        packet[6] = 0;
        packet[7] = 0;
    }
}
