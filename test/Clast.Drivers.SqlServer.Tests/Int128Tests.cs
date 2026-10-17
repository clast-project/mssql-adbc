// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using Xunit;

namespace Clast.Drivers.SqlServer.Tests;

/// <summary>
/// The 128-bit integer operations the driver uses, checked against
/// BigInteger: on .NET Framework they exercise the driver's netstandard2.0
/// polyfill, on .NET the runtime's own types.
/// </summary>
public sealed class Int128Tests
{
    private static readonly BigInteger Modulus = BigInteger.One << 128;

    public static IEnumerable<object[]> Values() =>
        new[]
        {
            "0", "1", "-1", "10", "-10", "4294967295", "4294967296", "-4294967296",
            "9223372036854775807", "-9223372036854775808", "18446744073709551615", "18446744073709551616",
            "99999999999999999999999999999999999999", "-99999999999999999999999999999999999999",
            "170141183460469231731687303715884105727", "-170141183460469231731687303715884105728",
            "123456789012345678901234567", "-98765432109876543210",
        }.Select(v => new object[] { v });

    private static Int128 Of(BigInteger value) => Int128.Parse(Wrap(value).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    /// <summary>The two's-complement value of the low 128 bits, as Int128 arithmetic wraps.</summary>
    private static BigInteger Wrap(BigInteger value)
    {
        BigInteger low = ((value % Modulus) + Modulus) % Modulus;
        return low >= Modulus / 2 ? low - Modulus : low;
    }

    private static BigInteger Big(Int128 value) => BigInteger.Parse(value.ToString(), CultureInfo.InvariantCulture);

    [Theory]
    [MemberData(nameof(Values))]
    public void ParsesFormatsAndRoundTripsBytes(string text)
    {
        Int128 value = Int128.Parse(text, CultureInfo.InvariantCulture);
        Assert.Equal(text, value.ToString());

        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteInt128LittleEndian(bytes, value);
        Assert.Equal(value, BinaryPrimitives.ReadInt128LittleEndian(bytes));
        Assert.Equal(Wrap(BigInteger.Parse(text, CultureInfo.InvariantCulture)), new BigInteger(bytes.ToArray()));
    }

    [Fact]
    public void ArithmeticMatchesBigInteger()
    {
        BigInteger[] values = Values().Select(v => BigInteger.Parse((string)v[0], CultureInfo.InvariantCulture)).ToArray();
        foreach (BigInteger a in values)
        {
            Assert.Equal(Wrap(-a), Big(-Of(a)));
            foreach (BigInteger b in values)
            {
                Int128 x = Of(a), y = Of(b);
                Assert.Equal(Wrap(a + b), Big(x + y));
                Assert.Equal(Wrap(a - b), Big(x - y));
                Assert.Equal(Wrap(a * b), Big(x * y));
                Assert.Equal(Wrap(a).CompareTo(Wrap(b)) < 0, x < y);
                Assert.Equal(Wrap(a).CompareTo(Wrap(b)) > 0, x > y);
                Assert.Equal(Wrap(a) == Wrap(b), x == y);
                if (b != 0 && !(Wrap(a) == -(Modulus / 2) && b == -1))
                {
                    Assert.Equal(BigInteger.Divide(Wrap(a), Wrap(b)), Big(x / y));
                }
            }
        }
    }

    [Fact]
    public void ConvertsFromSmallerIntegers()
    {
        Assert.Equal("-1", ((Int128)(-1)).ToString());
        Assert.Equal("-9223372036854775808", ((Int128)long.MinValue).ToString());
        Assert.Equal("18446744073709551615", ((Int128)ulong.MaxValue).ToString());
        Assert.Equal(long.MinValue, (long)(Int128)long.MinValue);

        UInt128 unsigned = (UInt128)Int128.Parse("99999999999999999999999999999999999999", CultureInfo.InvariantCulture);
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt128LittleEndian(bytes, unsigned);
        Assert.Equal(unsigned, BinaryPrimitives.ReadUInt128LittleEndian(bytes));
    }
}
