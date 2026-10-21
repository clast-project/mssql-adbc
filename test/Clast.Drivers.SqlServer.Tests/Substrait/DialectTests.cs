// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.
//
// Derived from kusto_adbc (test/KustoAdbc.Tests/DialectTests.cs);
// see THIRD-PARTY-NOTICES.md. Changes: namespace, the SQL Server catalog.

using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Clast.Drivers.SqlServer.Substrait;
using Json.Schema;
using Xunit;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Clast.Drivers.SqlServer.Tests.Substrait;

/// <summary>
/// Checks the driver's Substrait dialect file: it must conform to the Substrait
/// dialect schema, and its function lists must match what the translator implements
/// (TSqlFunctionCatalog), so that the published dialect never promises more, or less,
/// than the driver delivers.
/// </summary>
public class DialectTests
{
    private static readonly string s_schemaPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Substrait", "dialect_schema.yaml");

    [Fact]
    public void Dialect_ConformsToSubstraitDialectSchema()
    {
        var schema = JsonSchema.FromText(YamlToJson(File.ReadAllText(s_schemaPath))!.ToJsonString());
        using var dialect = System.Text.Json.JsonDocument.Parse(YamlToJson(SqlServerSubstrait.DialectYaml)!.ToJsonString());

        var result = schema.Evaluate(dialect.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        var errors = (result.Details ?? new List<EvaluationResults>())
            .Where(d => d.Errors != null)
            .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}"))
            .ToList();
        Assert.True(result.IsValid, "Dialect does not conform to the schema:\n" + string.Join("\n", errors));
    }

    [Fact]
    public void Dialect_FunctionsMatchTranslatorCatalog()
    {
        var dialectEntries = DialectFunctions().ToList();
        var catalogEntries = TSqlFunctionCatalog.All
            .SelectMany(f => f.Variants.Select(v => new FunctionEntry(
                f.Kind == TSqlFunctionKind.Scalar ? "scalar" : "aggregate",
                f.Urn,
                f.Name,
                v.Impls.OrderBy(i => i, StringComparer.Ordinal).ToList(),
                v.Options.OrderBy(o => o.Key, StringComparer.Ordinal).Select(o => $"{o.Key}={o.Value}").ToList(),
                v.VariadicMin is >= 1 ? v.VariadicMin : null,
                v.VariadicMax)))
            .ToList();

        var missingFromDialect = catalogEntries.Where(c => !dialectEntries.Contains(c)).ToList();
        var notImplemented = dialectEntries.Where(d => !catalogEntries.Contains(d)).ToList();

        Assert.True(missingFromDialect.Count == 0 && notImplemented.Count == 0,
            "Dialect and translator disagree.\n"
            + "Implemented but not in the dialect:\n  " + string.Join("\n  ", missingFromDialect) + "\n"
            + "In the dialect but not implemented:\n  " + string.Join("\n  ", notImplemented));
    }

    [Fact]
    public void Dialect_EveryDependencyIsUsed()
    {
        var root = LoadDialect();
        var urns = ((YamlMappingNode)root["dependencies"]).Children.Values.Select(v => ((YamlScalarNode)v).Value!).ToList();
        var used = new HashSet<string>(DialectFunctions().Select(f => f.Urn));
        Assert.All(urns, urn => Assert.Contains(urn, used));
    }

    [Fact]
    public void Dialect_NamesTheSubstraitVersion()
    {
        Assert.Contains($"substrait_version: \"{SqlServerSubstrait.SubstraitVersion}\"", SqlServerSubstrait.DialectYaml);
    }

    private sealed record FunctionEntry(string Kind, string Urn, string Name, List<string> Impls, List<string> Options, int? VariadicMin, int? VariadicMax)
    {
        public bool Equals(FunctionEntry? other)
            => other != null && Kind == other.Kind && Urn == other.Urn && Name == other.Name
                && Impls.SequenceEqual(other.Impls) && Options.SequenceEqual(other.Options)
                && VariadicMin == other.VariadicMin && VariadicMax == other.VariadicMax;

        public override int GetHashCode() => (Kind, Urn, Name).GetHashCode();

        public override string ToString()
            => $"{Kind} {Urn}#{Name} impls=[{string.Join(",", Impls)}] options=[{string.Join(",", Options)}]"
                + (VariadicMin.HasValue || VariadicMax.HasValue ? $" variadic={VariadicMin}..{VariadicMax}" : "");
    }

    private static YamlMappingNode LoadDialect()
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(SqlServerSubstrait.DialectYaml));
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static IEnumerable<FunctionEntry> DialectFunctions()
    {
        var root = LoadDialect();
        var dependencies = ((YamlMappingNode)root["dependencies"]).Children
            .ToDictionary(kv => ((YamlScalarNode)kv.Key).Value!, kv => ((YamlScalarNode)kv.Value).Value!);

        foreach (var (section, kind) in new[] { ("supported_scalar_functions", "scalar"), ("supported_aggregate_functions", "aggregate") })
        {
            foreach (YamlMappingNode entry in (YamlSequenceNode)root[section])
            {
                string Scalar(string key) => ((YamlScalarNode)entry[key]).Value!;
                var impls = ((YamlSequenceNode)entry["supported_impls"]).Children.Select(n => ((YamlScalarNode)n).Value ?? "")
                    .OrderBy(i => i, StringComparer.Ordinal).ToList();
                var options = entry.Children.TryGetValue(new YamlScalarNode("required_options"), out var opts)
                    ? ((YamlMappingNode)opts).Children.Select(kv => $"{((YamlScalarNode)kv.Key).Value}={((YamlScalarNode)kv.Value).Value}")
                        .OrderBy(o => o, StringComparer.Ordinal).ToList()
                    : new List<string>();
                int? min = null, max = null;
                if (entry.Children.TryGetValue(new YamlScalarNode("variadic"), out var variadic))
                {
                    var v = (YamlMappingNode)variadic;
                    if (v.Children.TryGetValue(new YamlScalarNode("min"), out var m))
                    {
                        min = int.Parse(((YamlScalarNode)m).Value!, CultureInfo.InvariantCulture);
                    }
                    if (v.Children.TryGetValue(new YamlScalarNode("max"), out var x))
                    {
                        max = int.Parse(((YamlScalarNode)x).Value!, CultureInfo.InvariantCulture);
                    }
                }
                yield return new FunctionEntry(kind, dependencies[Scalar("source")], Scalar("name"), impls, options, min, max);
            }
        }
    }

    #region YAML to JSON

    /// <summary>Converts YAML to JSON, typing plain scalars by the YAML 1.2 core schema.</summary>
    private static JsonNode? YamlToJson(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        return Convert(stream.Documents[0].RootNode);
    }

    private static readonly Regex s_int = new(@"^[-+]?[0-9]+$");
    private static readonly Regex s_float = new(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$");

    private static JsonNode? Convert(YamlNode node)
    {
        switch (node)
        {
            case YamlMappingNode map:
                var obj = new JsonObject();
                foreach (var kv in map.Children)
                {
                    obj[((YamlScalarNode)kv.Key).Value!] = Convert(kv.Value);
                }
                return obj;
            case YamlSequenceNode seq:
                var arr = new System.Text.Json.Nodes.JsonArray();
                foreach (var item in seq.Children)
                {
                    arr.Add(Convert(item));
                }
                return arr;
            case YamlScalarNode scalar:
                string value = scalar.Value ?? "";
                if (scalar.Style != ScalarStyle.Plain)
                {
                    return JsonValue.Create(value);
                }
                if (value is "" or "~" or "null" or "Null" or "NULL")
                {
                    return null;
                }
                if (value is "true" or "True" or "TRUE")
                {
                    return JsonValue.Create(true);
                }
                if (value is "false" or "False" or "FALSE")
                {
                    return JsonValue.Create(false);
                }
                if (s_int.IsMatch(value))
                {
                    return JsonValue.Create(long.Parse(value, CultureInfo.InvariantCulture));
                }
                if (s_float.IsMatch(value))
                {
                    return JsonValue.Create(double.Parse(value, CultureInfo.InvariantCulture));
                }
                return JsonValue.Create(value);
            default:
                throw new NotSupportedException(node.GetType().Name);
        }
    }

    #endregion
}
