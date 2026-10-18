// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Clast.Drivers.SqlServer.Arrow;

/// <summary>
/// Converts between hierarchyid's serialized form and its text form
/// (<c>/1/2.5/-3/</c>, as <c>ToString()</c> and <c>hierarchyid::Parse</c> use).
/// <para>The serialized form is a bit stream, most significant bit first,
/// padded with zero bits to a whole byte; the root is empty. Each label is
/// one of the patterns below: fixed bits that identify the pattern and
/// separate its value bits (x), which hold the label less the pattern's
/// minimum, and a final bit (T) that is 1 when the label ends its level
/// (<c>/</c> follows) and 0 when a <c>.</c> and another label follow; such
/// a label is stored one higher, so that the bytes sort in tree order. The
/// patterns are those of SQL Server's implementation, as documented by the
/// open-source Microsoft.SqlServer.Types port.</para>
/// </summary>
internal static class HierarchyId
{
    /// <summary>The longest serialized hierarchyid SQL Server accepts.</summary>
    public const int MaxBytes = 892;

    private sealed class Pattern
    {
        public Pattern(long min, long max, string bits)
        {
            Min = min;
            Max = max;
            Bits = bits;
            PrefixLength = bits.IndexOf('x');
            for (int i = 0; i < PrefixLength; i++)
            {
                Prefix = (Prefix << 1) | (uint)(bits[i] - '0');
            }
            foreach (char c in bits)
            {
                ValueBits += c == 'x' ? 1 : 0;
            }
        }

        public long Min { get; }
        public long Max { get; }

        /// <summary>The pattern: 0 and 1 are fixed bits, x value bits, T the final bit.</summary>
        public string Bits { get; }

        /// <summary>The fixed bits before the first value bit, which identify the pattern.</summary>
        public int PrefixLength { get; }
        public ulong Prefix { get; }

        public int ValueBits { get; }
    }

    private static readonly Pattern[] Patterns =
    [
        new(0, 3, "01xxT"),
        new(4, 7, "100xxT"),
        new(8, 15, "101xxxT"),
        new(16, 79, "110xx0x1xxxT"),
        new(80, 1103, "1110xxx0xxx0x1xxxT"),
        new(1104, 5199, "11110xxxxx0xxx0x1xxxT"),
        new(5200, 4294972495, "111110xxxxxxxxxxxxxxxxxxx0xxxxxx0xxx0x1xxxT"),
        new(4294972496, 281479271683151, "111111xxxxxxxxxxxxxx0xxxxxxxxxxxxxxxxxxxxx0xxxxxx0xxx0x1xxxT"),
        new(-8, -1, "00111xxxT"),
        new(-72, -9, "0010xx0x1xxxT"),
        new(-4168, -73, "000110xxxxx0xxx0x1xxxT"),
        new(-4294971464, -4169, "000101xxxxxxxxxxxxxxxxxxx0xxxxxx0xxx0x1xxxT"),
        new(-281479271682120, -4294971465, "000100xxxxxxxxxxxxxx0xxxxxxxxxxxxxxxxxxxxx0xxxxxx0xxx0x1xxxT"),
    ];

    /// <summary>An upper bound on the text length of a serialized value of <paramref name="byteCount"/> bytes.</summary>
    /// <remarks>Every pattern spends at least 2.5 bits per character it formats to (5 bits for <c>/3</c>).</remarks>
    public static int MaxTextLength(int byteCount) => byteCount * 8 * 2 / 5 + 1;

    /// <summary>
    /// Writes the text form of a serialized hierarchyid as ASCII (so also
    /// UTF-8), returning the number of bytes written; false if the bytes are
    /// not a valid hierarchyid. <paramref name="destination"/> holds at least
    /// <see cref="MaxTextLength"/> bytes.
    /// </summary>
    public static bool TryFormat(ReadOnlySpan<byte> value, Span<byte> destination, out int written)
    {
        int bitCount = value.Length * 8;
        int position = 0;
        written = 0;
        destination[written++] = (byte)'/';
        while (!RestIsZero(value, position))
        {
            Pattern? pattern = Match(value, position);
            if (pattern == null || position + pattern.Bits.Length > bitCount)
            {
                return false;
            }
            ulong offset = 0;
            bool final = false;
            foreach (char c in pattern.Bits)
            {
                int bit = Bit(value, position++);
                switch (c)
                {
                    case 'x':
                        offset = (offset << 1) | (uint)bit;
                        break;
                    case 'T':
                        final = bit == 1;
                        break;
                    default:
                        if (bit != c - '0')
                        {
                            return false;
                        }
                        break;
                }
            }
            long label = pattern.Min + (long)offset - (final ? 0 : 1);
            written += FormatInt64(label, destination.Slice(written));
            destination[written++] = final ? (byte)'/' : (byte)'.';
        }
        // A level cannot end on a '.'.
        return destination[written - 1] == '/';
    }

    /// <summary>
    /// Serializes the text form of a hierarchyid (UTF-8), returning the
    /// number of bytes written; false, with the reason, if the text is not a
    /// valid hierarchyid. <paramref name="destination"/> holds at least
    /// <see cref="MaxBytes"/> bytes.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> text, Span<byte> destination, out int written, out string? error)
    {
        written = 0;
        error = null;
        if (text.Length == 0 || text[0] != '/' || text[text.Length - 1] != '/')
        {
            error = "it must start and end with '/'";
            return false;
        }
        destination.Slice(0, Math.Min(destination.Length, MaxBytes)).Clear();
        int position = 0;
        int i = 1;
        while (i < text.Length)
        {
            if (!TryParseInt64(text, ref i, out long label))
            {
                error = $"expected a number at position {i}";
                return false;
            }
            if (text[i] is not ((byte)'/' or (byte)'.'))
            {
                error = $"expected '/' or '.' at position {i}";
                return false;
            }
            bool final = text[i++] == '/';
            if (!final && label == long.MaxValue)
            {
                error = $"label {label} is out of range";
                return false;
            }
            long stored = final ? label : label + 1;
            Pattern? pattern = Find(stored);
            if (pattern == null)
            {
                error = $"label {label} is out of range";
                return false;
            }
            if (position + pattern.Bits.Length > MaxBytes * 8)
            {
                error = $"it is longer than {MaxBytes} bytes";
                return false;
            }
            ulong offset = (ulong)(stored - pattern.Min);
            int remaining = pattern.ValueBits;
            foreach (char c in pattern.Bits)
            {
                int bit = c switch
                {
                    'x' => (int)((offset >> --remaining) & 1),
                    'T' => final ? 1 : 0,
                    _ => c - '0',
                };
                if (bit != 0)
                {
                    destination[position >> 3] |= (byte)(0x80 >> (position & 7));
                }
                position++;
            }
        }
        written = (position + 7) / 8;
        return true;
    }

    private static Pattern? Find(long label)
    {
        foreach (Pattern pattern in Patterns)
        {
            if (label >= pattern.Min && label <= pattern.Max)
            {
                return pattern;
            }
        }
        return null;
    }

    private static Pattern? Match(ReadOnlySpan<byte> value, int position)
    {
        foreach (Pattern pattern in Patterns)
        {
            ulong prefix = 0;
            for (int i = 0; i < pattern.PrefixLength; i++)
            {
                prefix = (prefix << 1) | (uint)Bit(value, position + i);
            }
            if (prefix == pattern.Prefix)
            {
                return pattern;
            }
        }
        return null;
    }

    /// <summary>The bit at <paramref name="position"/>, most significant first; 0 past the end.</summary>
    private static int Bit(ReadOnlySpan<byte> value, int position) =>
        position >> 3 < value.Length ? (value[position >> 3] >> (7 - (position & 7))) & 1 : 0;

    /// <summary>Whether only padding (zero bits) remains from <paramref name="position"/> on.</summary>
    private static bool RestIsZero(ReadOnlySpan<byte> value, int position)
    {
        for (int i = position; i < value.Length * 8; i++)
        {
            if (Bit(value, i) != 0)
            {
                return false;
            }
        }
        return true;
    }

    private static int FormatInt64(long value, Span<byte> destination)
    {
        Span<byte> digits = stackalloc byte[20];
        ulong magnitude = value < 0 ? (ulong)(-(value + 1)) + 1 : (ulong)value;
        int count = 0;
        do
        {
            digits[count++] = (byte)('0' + (int)(magnitude % 10));
            magnitude /= 10;
        }
        while (magnitude != 0);
        int written = 0;
        if (value < 0)
        {
            destination[written++] = (byte)'-';
        }
        while (count > 0)
        {
            destination[written++] = digits[--count];
        }
        return written;
    }

    /// <summary>Parses an optionally negative decimal integer at <paramref name="i"/>, which ends past it.</summary>
    private static bool TryParseInt64(ReadOnlySpan<byte> text, ref int i, out long value)
    {
        value = 0;
        bool negative = i < text.Length && text[i] == '-';
        int start = negative ? i + 1 : i;
        int end = start;
        ulong magnitude = 0;
        while (end < text.Length && text[end] is >= (byte)'0' and <= (byte)'9')
        {
            if (magnitude > (ulong.MaxValue - 9) / 10)
            {
                return false;
            }
            magnitude = magnitude * 10 + (uint)(text[end] - '0');
            end++;
        }
        if (end == start || magnitude > (negative ? (ulong)long.MaxValue + 1 : long.MaxValue))
        {
            return false;
        }
        value = negative ? (long)(0 - magnitude) : (long)magnitude;
        i = end;
        return true;
    }
}
