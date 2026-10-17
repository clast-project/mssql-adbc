// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Apache.Arrow;

namespace Clast.Drivers.SqlServer.Arrow;

/// <summary>
/// A growable, uninitialized byte buffer that is handed to Arrow without
/// copying. After <see cref="Build"/> the memory belongs to the array and the
/// next batch starts with a fresh allocation of the same capacity.
/// </summary>
internal struct ValueBuffer
{
    private byte[] _array;
    private int _length;

    public ValueBuffer(int capacity)
    {
        _array = GC.AllocateUninitializedArray<byte>(Math.Max(capacity, 64));
        _length = 0;
    }

    public readonly int Length => _length;

    /// <summary>Returns writable space for at least <paramref name="size"/> bytes at the end.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<byte> GetSpan(int size)
    {
        if (_array.Length - _length < size)
        {
            Grow(size);
        }
        return _array.AsSpan(_length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Advance(int count) => _length += count;

    /// <summary>Appends one unmanaged value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Append<T>(T value) where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();
        if (_array.Length - _length < size)
        {
            Grow(size);
        }
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_array), (nint)_length), value);
        _length += size;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Append(ReadOnlySpan<byte> bytes)
    {
        if (_array.Length - _length < bytes.Length)
        {
            Grow(bytes.Length);
        }
        bytes.CopyTo(_array.AsSpan(_length));
        _length += bytes.Length;
    }

    /// <summary>Appends <paramref name="size"/> zero bytes (the value slot of a null).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendZeros(int size)
    {
        if (_array.Length - _length < size)
        {
            Grow(size);
        }
        _array.AsSpan(_length, size).Clear();
        _length += size;
    }

    public ArrowBuffer Build()
    {
        var buffer = new ArrowBuffer(_array.AsMemory(0, _length));
        _array = GC.AllocateUninitializedArray<byte>(_array.Length);
        _length = 0;
        return buffer;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow(int size)
    {
        int capacity = Math.Max(_array.Length * 2, _length + size);
        byte[] array = GC.AllocateUninitializedArray<byte>(capacity);
        _array.AsSpan(0, _length).CopyTo(array);
        _array = array;
    }
}

/// <summary>
/// Validity bitmap. Only materialized once the first null is appended, so
/// all-valid columns cost a counter increment per row.
/// </summary>
internal struct ValidityBuffer
{
    private byte[]? _bits;
    private int _length;
    private int _nullCount;

    public readonly int Length => _length;
    public readonly int NullCount => _nullCount;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendValid()
    {
        if (_bits != null)
        {
            SetValid(_length);
        }
        _length++;
    }

    public void AppendNull()
    {
        if (_bits == null)
        {
            Materialize();
        }
        else if (((_length >> 3) + 1) > _bits.Length)
        {
            System.Array.Resize(ref _bits, _bits.Length * 2);
        }
        _nullCount++;
        _length++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetValid(int index)
    {
        int byteIndex = index >> 3;
        if (byteIndex >= _bits!.Length)
        {
            System.Array.Resize(ref _bits, _bits.Length * 2);
        }
        _bits[byteIndex] |= (byte)(1 << (index & 7));
    }

    private void Materialize()
    {
        _bits = new byte[Math.Max(64, (_length >> 3) + 64)];
        // Everything so far was valid.
        int fullBytes = _length >> 3;
        _bits.AsSpan(0, fullBytes).Fill(0xFF);
        for (int i = fullBytes << 3; i < _length; i++)
        {
            _bits[i >> 3] |= (byte)(1 << (i & 7));
        }
    }

    public ArrowBuffer Build()
    {
        ArrowBuffer result = _nullCount == 0 || _bits == null
            ? ArrowBuffer.Empty
            : new ArrowBuffer(_bits.AsMemory(0, (_length + 7) >> 3));
        _bits = null;
        _length = 0;
        _nullCount = 0;
        return result;
    }
}
