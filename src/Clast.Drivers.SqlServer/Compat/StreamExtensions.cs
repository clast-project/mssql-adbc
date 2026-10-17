// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

#if !NET
using System.Buffers;
using System.Runtime.InteropServices;

namespace System.IO;

/// <summary>
/// The span and memory overloads of <see cref="Stream"/> that netstandard2.0
/// lacks, over its array methods: arrays pass straight through, other memory
/// goes through a pooled buffer.
/// </summary>
internal static class StreamExtensions
{
    extension(Stream stream)
    {
        public int Read(Span<byte> buffer)
        {
            byte[] rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
            try
            {
                int read = stream.Read(rented, 0, buffer.Length);
                rented.AsSpan(0, read).CopyTo(buffer);
                return read;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        public void Write(ReadOnlySpan<byte> buffer)
        {
            byte[] rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
            try
            {
                buffer.CopyTo(rented);
                stream.Write(rented, 0, buffer.Length);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        public void ReadExactly(Span<byte> buffer)
        {
            while (buffer.Length > 0)
            {
                int read = stream.Read(buffer);
                if (read == 0)
                {
                    throw new EndOfStreamException();
                }
                buffer = buffer.Slice(read);
            }
        }

        public void ReadExactly(byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int read = stream.Read(buffer, offset, count);
                if (read == 0)
                {
                    throw new EndOfStreamException();
                }
                offset += read;
                count -= read;
            }
        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (MemoryMarshal.TryGetArray(buffer, out ArraySegment<byte> segment))
            {
                return await stream.ReadAsync(segment.Array!, segment.Offset, segment.Count, cancellationToken).ConfigureAwait(false);
            }
            byte[] rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
            try
            {
                int read = await stream.ReadAsync(rented, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                rented.AsMemory(0, read).CopyTo(buffer);
                return read;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        public async ValueTask ReadExactlyAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (buffer.Length > 0)
            {
                int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException();
                }
                buffer = buffer.Slice(read);
            }
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (MemoryMarshal.TryGetArray(buffer, out ArraySegment<byte> segment))
            {
                await stream.WriteAsync(segment.Array!, segment.Offset, segment.Count, cancellationToken).ConfigureAwait(false);
                return;
            }
            byte[] rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
            try
            {
                buffer.CopyTo(rented);
                await stream.WriteAsync(rented, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }
}
#endif
