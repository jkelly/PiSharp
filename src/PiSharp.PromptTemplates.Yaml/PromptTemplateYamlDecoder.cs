using System.Collections;
using PiSharp.CodingAgent.Resources;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NodeDeserializers;

namespace PiSharp.PromptTemplates.Yaml;

/// <summary>Standard YAML parsing followed by Pi's two string-only metadata projections.
/// Does not parse frontmatter delimiters or implement a YAML grammar.</summary>
public static class PromptTemplateYamlDecoder
{
    // Internal deterministic work observation; no global counters, timers or alias expansion.
    internal static (int SyntaxNodes, long WeightVisits, long ReferenceVisits) InspectAliasAccounting(string yaml) =>
        PromptTemplateAliasGuard.Validate(yaml);
    public static PromptTemplateMetadata Decode(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        PromptTemplateAliasGuard.Validate(yaml);
        // A fresh deserializer has no shared mutable parser or alias state across files/callers.
        var decoder = new DeserializerBuilder().WithDuplicateKeyChecking()
            .WithAttemptingUnquotedStringTypeDeserialization()
            .WithNodeDeserializer(new PreservedStringNode(), location => location.Before<NullNodeDeserializer>())
            .Build();
        var root = decoder.Deserialize<object>(yaml);
        if (root is not IDictionary mapping) return new();
        return new(StringField("description"), StringField("argument-hint"));
        string? StringField(string name) => mapping.Contains(name) ? mapping[name] as string : null;
    }

    // YamlDotNet 16.3.0's untyped scalar inference excludes folded/quoted styles but not literal.
    // Preserve the library-parsed literal text and explicit string tags (including !!str null)
    // before its null/scalar inference. Other explicit typed tags use standard resolution.
    private sealed class PreservedStringNode : INodeDeserializer
    {
        public bool Deserialize(IParser parser, Type expectedType,
            Func<IParser, Type, object?> nestedObjectDeserializer, out object? value, ObjectDeserializer rootDeserializer)
        {
            if (parser.Accept<Scalar>(out var scalar) &&
                (scalar.Tag == "tag:yaml.org,2002:str" || scalar.Style == ScalarStyle.Literal &&
                    (scalar.Tag.IsEmpty || scalar.Tag.IsNonSpecific)))
            { parser.MoveNext(); value = scalar.Value; return true; }
            value = null; return false;
        }
    }
}
