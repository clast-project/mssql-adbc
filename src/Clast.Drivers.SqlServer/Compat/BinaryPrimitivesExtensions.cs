// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

#if !NET
namespace System.Buffers.Binary;

/// <summary>.NET 7's 128-bit <see cref="BinaryPrimitives"/> methods, for netstandard2.0.</summary>
internal static class BinaryPrimitivesExtensions
{
    extension(BinaryPrimitives)
    {
        public static Int128 ReadInt128LittleEndian(ReadOnlySpan<byte> source) =>
            new(BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(8)), BinaryPrimitives.ReadUInt64LittleEndian(source));

        public static UInt128 ReadUInt128LittleEndian(ReadOnlySpan<byte> source) =>
            new(BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(8)), BinaryPrimitives.ReadUInt64LittleEndian(source));

        public static void WriteInt128LittleEndian(Span<byte> destination, Int128 value)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(destination, value.Lower);
            BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(8), value.Upper);
        }

        public static void WriteUInt128LittleEndian(Span<byte> destination, UInt128 value)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(destination, value.Lower);
            BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(8), value.Upper);
        }
    }
}
#endif
