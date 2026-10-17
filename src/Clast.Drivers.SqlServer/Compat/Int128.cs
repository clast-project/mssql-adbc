// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

#if !NET
using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;

namespace System;

// The parts of .NET 7's 128-bit integers that the driver uses (decimal
// encoding and decoding, partition ranges), for netstandard2.0. The layout
// matches .NET's: the low 64 bits first, as Arrow's decimal128 stores them.

/// <summary>A signed 128-bit integer (two's complement).</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct Int128 : IComparable<Int128>, IEquatable<Int128>
{
    private readonly ulong _lower;
    private readonly ulong _upper;

    public Int128(ulong upper, ulong lower)
    {
        _upper = upper;
        _lower = lower;
    }

    internal ulong Lower => _lower;

    internal ulong Upper => _upper;

    public static implicit operator Int128(int value) => new((ulong)(value >> 31), (ulong)(long)value);

    public static implicit operator Int128(long value) => new((ulong)(value >> 63), (ulong)value);

    public static implicit operator Int128(ulong value) => new(0, value);

    public static explicit operator Int128(UInt128 value) => new(value.Upper, value.Lower);

    public static explicit operator long(Int128 value) => (long)value._lower;

    public static Int128 operator -(Int128 value) => ~value + 1;

    public static Int128 operator ~(Int128 value) => new(~value._upper, ~value._lower);

    public static Int128 operator +(Int128 left, Int128 right)
    {
        ulong lower = left._lower + right._lower;
        ulong carry = lower < left._lower ? 1UL : 0UL;
        return new(left._upper + right._upper + carry, lower);
    }

    public static Int128 operator -(Int128 left, Int128 right) => left + -right;

    /// <summary>The low 128 bits of the product, as .NET's (unchecked) multiplication gives.</summary>
    public static Int128 operator *(Int128 left, Int128 right)
    {
        UInt128 product = (UInt128)left * (UInt128)right;
        return new(product.Upper, product.Lower);
    }

    public static Int128 operator /(Int128 left, Int128 right) => FromBigInteger(BigInteger.Divide(left.ToBigInteger(), right.ToBigInteger()));

    public static bool operator <(Int128 left, Int128 right) => left.CompareTo(right) < 0;

    public static bool operator >(Int128 left, Int128 right) => left.CompareTo(right) > 0;

    public static bool operator <=(Int128 left, Int128 right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Int128 left, Int128 right) => left.CompareTo(right) >= 0;

    public static bool operator ==(Int128 left, Int128 right) => left.Equals(right);

    public static bool operator !=(Int128 left, Int128 right) => !left.Equals(right);

    public int CompareTo(Int128 other)
    {
        int upper = ((long)_upper).CompareTo((long)other._upper);
        return upper != 0 ? upper : _lower.CompareTo(other._lower);
    }

    public bool Equals(Int128 other) => _lower == other._lower && _upper == other._upper;

    public override bool Equals(object? obj) => obj is Int128 other && Equals(other);

    public override int GetHashCode() => (_lower ^ _upper).GetHashCode();

    public override string ToString() => ToBigInteger().ToString(CultureInfo.InvariantCulture);

    public static Int128 Parse(string s) => FromBigInteger(BigInteger.Parse(s, CultureInfo.InvariantCulture));

    public static Int128 Parse(string s, IFormatProvider? provider) => FromBigInteger(BigInteger.Parse(s, provider));

    internal BigInteger ToBigInteger()
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, _lower);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.Slice(8), _upper);
        return new BigInteger(bytes.ToArray());
    }

    internal static Int128 FromBigInteger(BigInteger value)
    {
        byte[] bytes = value.ToByteArray();
        Span<byte> padded = stackalloc byte[16];
        padded.Fill(value.Sign < 0 ? (byte)0xFF : (byte)0);
        bytes.AsSpan(0, Math.Min(bytes.Length, 16)).CopyTo(padded);
        return new(BinaryPrimitives.ReadUInt64LittleEndian(padded.Slice(8)), BinaryPrimitives.ReadUInt64LittleEndian(padded));
    }
}

/// <summary>An unsigned 128-bit integer.</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct UInt128 : IEquatable<UInt128>
{
    private readonly ulong _lower;
    private readonly ulong _upper;

    public UInt128(ulong upper, ulong lower)
    {
        _upper = upper;
        _lower = lower;
    }

    internal ulong Lower => _lower;

    internal ulong Upper => _upper;

    public static explicit operator UInt128(Int128 value) => new(value.Upper, value.Lower);

    public static UInt128 operator *(UInt128 left, UInt128 right)
    {
        ulong high = MultiplyHigh(left._lower, right._lower, out ulong low);
        high += left._upper * right._lower + left._lower * right._upper;
        return new(high, low);
    }

    /// <summary>The 128-bit product of two 64-bit values: returns the high half.</summary>
    private static ulong MultiplyHigh(ulong a, ulong b, out ulong low)
    {
        ulong aLow = (uint)a, aHigh = a >> 32, bLow = (uint)b, bHigh = b >> 32;
        ulong lowLow = aLow * bLow;
        ulong highLow = aHigh * bLow;
        ulong lowHigh = aLow * bHigh;
        ulong highHigh = aHigh * bHigh;
        ulong middle = (lowLow >> 32) + (uint)highLow + (uint)lowHigh;
        low = (middle << 32) | (uint)lowLow;
        return highHigh + (highLow >> 32) + (lowHigh >> 32) + (middle >> 32);
    }

    public bool Equals(UInt128 other) => _lower == other._lower && _upper == other._upper;

    public override bool Equals(object? obj) => obj is UInt128 other && Equals(other);

    public override int GetHashCode() => (_lower ^ _upper).GetHashCode();
}
#endif
