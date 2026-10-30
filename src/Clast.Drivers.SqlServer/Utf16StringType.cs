// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.InteropServices;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace Clast.Drivers.SqlServer;

/// <summary>
/// The <c>clast.utf16</c> extension type: text as little-endian UTF-16 code
/// units in a binary array, whose offsets count bytes. Arrow's string type is
/// UTF-8; a consumer whose strings are UTF-16 can read nvarchar data as the
/// server sends it, without transcoding it to UTF-8 and back. Results have it
/// in place of utf8 when <see cref="SqlServerParameters.StringEncoding"/> is
/// <c>utf16</c>.
/// </summary>
/// <remarks>
/// nvarchar values are the server's code units as they are, so, like .NET and
/// Windows strings, they may hold unpaired surrogates.
/// </remarks>
public sealed class Utf16StringType : ExtensionType
{
    public static readonly Utf16StringType Default = new();

    public Utf16StringType()
        : base(BinaryType.Default)
    {
    }

    public override string Name => "clast.utf16";

    public override string ExtensionMetadata => "";

    public override ExtensionArray CreateArray(IArrowArray storageArray) => new Utf16StringArray(this, storageArray);
}

/// <summary>An array of <see cref="Utf16StringType"/> values.</summary>
public sealed class Utf16StringArray : ExtensionArray
{
    public Utf16StringArray(Utf16StringType type, IArrowArray storage)
        : base(type, storage)
    {
    }

    public BinaryArray StorageArray => (BinaryArray)Storage;

    /// <summary>The value's UTF-16 code units, without copying them; empty for null.</summary>
    public ReadOnlySpan<char> GetSpan(int index) => MemoryMarshal.Cast<byte, char>(StorageArray.GetBytes(index));

    public string? GetString(int index) => IsNull(index) ? null : GetSpan(index).ToString();
}
