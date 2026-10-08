using System.Collections;
using PiSharp.CodingAgent.Resources.Skills;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NodeDeserializers;

namespace PiSharp.PromptTemplates.Yaml;

/// <summary>Skills metadata projection through the already admitted standard YAML dependency and alias guard.</summary>
public static class SkillYamlDecoder
{
    public static SkillMetadata Decode(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml); PromptTemplateAliasGuard.Validate(yaml);
        var decoder = new DeserializerBuilder().WithDuplicateKeyChecking().WithAttemptingUnquotedStringTypeDeserialization()
            .WithNodeDeserializer(new PreservedStringNode(), location => location.Before<NullNodeDeserializer>()).Build();
        if (decoder.Deserialize<object>(yaml) is not IDictionary mapping) return new();
        return new(Field("name"), Field("description"), mapping.Contains("disable-model-invocation") && mapping["disable-model-invocation"] is true);
        string? Field(string name) => mapping.Contains(name) ? mapping[name] as string : null;
    }
    // Match the existing qualified prompt decoder's literal/explicit-string preservation policy.
    private sealed class PreservedStringNode : INodeDeserializer
    {
        public bool Deserialize(IParser parser, Type expectedType, Func<IParser, Type, object?> nestedObjectDeserializer,
            out object? value, ObjectDeserializer rootDeserializer)
        {
            if (parser.Accept<Scalar>(out var scalar) && (scalar.Tag == "tag:yaml.org,2002:str" || scalar.Style == ScalarStyle.Literal &&
                (scalar.Tag.IsEmpty || scalar.Tag.IsNonSpecific))) { parser.MoveNext(); value = scalar.Value; return true; }
            value = null; return false;
        }
    }
}
