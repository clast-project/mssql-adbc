// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;

namespace Clast.Drivers.SqlServer.Arrow;

/// <summary>
/// Restores the Arrow extension types of data bound for ingest or as
/// parameters. Data that crosses the C data interface into the driver (from
/// Python, Go, R or C++ through the NativeAOT library) arrives with only the
/// storage type and the <c>ARROW:extension:*</c> field metadata, so a
/// datetimeoffset column is a plain struct and a uuid a fixed-size binary.
/// This rebuilds the extension types the encoders know, from their
/// definitions.
/// </summary>
internal static class BoundExtensionTypes
{
    private const string NameKey = "ARROW:extension:name";
    private const string MetadataKey = "ARROW:extension:metadata";

    private static readonly Dictionary<string, ExtensionDefinition> s_definitions = new ExtensionDefinition[]
    {
        GuidExtensionDefinition.Instance,
        TimestampWithOffsetExtensionDefinition.Instance,
        VariantExtensionDefinition.Instance,
        JsonExtensionDefinition.Instance,
        Utf16StringExtensionDefinition.Instance,
    }.ToDictionary(d => d.ExtensionName, StringComparer.Ordinal);

    /// <summary>The stream, or one with its extension types restored if it has any to restore.</summary>
    public static IArrowArrayStream Resolve(IArrowArrayStream stream)
    {
        Schema schema = stream.Schema;
        var types = new ExtensionType?[schema.FieldsList.Count];
        bool any = false;
        for (int i = 0; i < types.Length; i++)
        {
            types[i] = Restore(schema.FieldsList[i]);
            any |= types[i] != null;
        }
        return any ? new ResolvedStream(stream, types) : stream;
    }

    private static ExtensionType? Restore(Field field)
    {
        if (field.DataType is ExtensionType
            || field.Metadata == null
            || !field.Metadata.TryGetValue(NameKey, out string? name)
            || !s_definitions.TryGetValue(name, out ExtensionDefinition? definition))
        {
            return null;
        }
        field.Metadata.TryGetValue(MetadataKey, out string? metadata);
        return definition.TryCreateType(field.DataType, metadata ?? "", out ExtensionType? type) ? type : null;
    }

    private sealed class ResolvedStream : IArrowArrayStream
    {
        private readonly IArrowArrayStream _inner;
        private readonly ExtensionType?[] _types;

        public ResolvedStream(IArrowArrayStream inner, ExtensionType?[] types)
        {
            _inner = inner;
            _types = types;
            var fields = new List<Field>(types.Length);
            for (int i = 0; i < types.Length; i++)
            {
                Field field = inner.Schema.FieldsList[i];
                fields.Add(types[i] is ExtensionType type
                    ? new Field(field.Name, type, field.IsNullable,
                        field.Metadata?.Where(p => p.Key is not (NameKey or MetadataKey)))
                    : field);
            }
            Schema = new Schema(fields, inner.Schema.Metadata);
        }

        public Schema Schema { get; }

        public async ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
        {
            if (await _inner.ReadNextRecordBatchAsync(cancellationToken).ConfigureAwait(false) is not RecordBatch batch)
            {
                return null;
            }
            // The new batch takes over the columns: disposing it releases them.
            var columns = new IArrowArray[_types.Length];
            for (int i = 0; i < columns.Length; i++)
            {
                IArrowArray column = batch.Column(i);
                columns[i] = _types[i] is ExtensionType type ? type.CreateArray(column) : column;
            }
            return new RecordBatch(Schema, columns, batch.Length);
        }

        public void Dispose() => _inner.Dispose();
    }

    /// <summary><c>arrow.json</c> over utf8, the driver's own <see cref="JsonType"/>.</summary>
    private sealed class JsonExtensionDefinition : ExtensionDefinition
    {
        public static readonly JsonExtensionDefinition Instance = new();

        public override string ExtensionName => "arrow.json";

        public override bool TryCreateType(IArrowType storageType, string metadata, out ExtensionType type)
        {
            type = JsonType.Default;
            return storageType is StringType;
        }
    }

    /// <summary><c>clast.utf16</c> over binary, the driver's own <see cref="Utf16StringType"/>.</summary>
    private sealed class Utf16StringExtensionDefinition : ExtensionDefinition
    {
        public static readonly Utf16StringExtensionDefinition Instance = new();

        public override string ExtensionName => "clast.utf16";

        public override bool TryCreateType(IArrowType storageType, string metadata, out ExtensionType type)
        {
            type = Utf16StringType.Default;
            return storageType is BinaryType;
        }
    }
}
