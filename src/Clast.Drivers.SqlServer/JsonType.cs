// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Types;

namespace Clast.Drivers.SqlServer;

/// <summary>
/// The canonical <c>arrow.json</c> extension type: UTF-8 JSON text in a utf8
/// array. Apache.Arrow for .NET does not define it, so the driver does; any
/// consumer that knows the extension name sees it through the C data
/// interface or IPC.
/// </summary>
public sealed class JsonType : ExtensionType
{
    public static readonly JsonType Default = new();

    public JsonType()
        : base(StringType.Default)
    {
    }

    public override string Name => "arrow.json";

    public override string ExtensionMetadata => "";

    public override ExtensionArray CreateArray(IArrowArray storageArray) => new JsonArray(this, storageArray);
}

/// <summary>An array of <see cref="JsonType"/> values.</summary>
public sealed class JsonArray : ExtensionArray
{
    public JsonArray(JsonType type, IArrowArray storage)
        : base(type, storage)
    {
    }

    public JsonArray(StringArray storage)
        : base(JsonType.Default, storage)
    {
    }

    public StringArray StorageArray => (StringArray)Storage;

    public string? GetString(int index) => StorageArray.GetString(index);
}
