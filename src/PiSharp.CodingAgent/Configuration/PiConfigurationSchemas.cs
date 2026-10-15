// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/coding-agent/schemas/{models,settings,keybindings,theme}.schema.json
// (embedded byte-for-byte under Schemas/) and packages/coding-agent/scripts/generate-schemas.ts.
using System.Text;
using PiSharp.Agent;

namespace PiSharp.CodingAgent.Configuration;

/// <summary>
/// Pi's published configuration schemas (<c>https://pi.dev/schemas/*.schema.json</c>), generated from the TypeBox schemas that validate
/// models.json, settings.json, keybindings.json and theme files. PiSharp embeds them and validates with them, so the checks and their
/// messages are the TypeBox ones.
/// </summary>
public static class PiConfigurationSchemas
{
    public const string BaseUrl = "https://pi.dev/schemas";
    public const string ModelsFileName = "models.schema.json";
    public const string SettingsFileName = "settings.schema.json";
    public const string KeybindingsFileName = "keybindings.schema.json";
    public const string ThemeFileName = "theme.schema.json";
    public static IReadOnlyList<string> FileNames { get; } = [ModelsFileName, SettingsFileName, KeybindingsFileName, ThemeFileName];

    /// <summary>The published URL of a schema file (its <c>$id</c>).</summary>
    public static string Url(string fileName) => BaseUrl + "/" + fileName;

    /// <summary>The embedded published schema file, exactly as upstream ships it.</summary>
    public static byte[] ReadBytes(string fileName)
    {
        if (!FileNames.Contains(fileName, StringComparer.Ordinal)) throw new ArgumentException("Unknown configuration schema " + fileName, nameof(fileName));
        using var stream = typeof(PiConfigurationSchemas).Assembly.GetManifestResourceStream("PiSharp.CodingAgent.Schemas." + fileName)
            ?? throw new InvalidOperationException("Missing embedded configuration schema " + fileName + ".");
        using var buffer = new MemoryStream(); stream.CopyTo(buffer); return buffer.ToArray();
    }

    public static string Read(string fileName) => new UTF8Encoding(false, true).GetString(ReadBytes(fileName));

    private static readonly Lazy<TypeBoxJsonSchema> models = new(() => TypeBoxJsonSchema.FromPublished(Read(ModelsFileName)));
    private static readonly Lazy<TypeBoxJsonSchema> settings = new(() => TypeBoxJsonSchema.FromPublished(Read(SettingsFileName)));
    private static readonly Lazy<TypeBoxJsonSchema> keybindings = new(() => TypeBoxJsonSchema.FromPublished(Read(KeybindingsFileName)));
    private static readonly Lazy<TypeBoxJsonSchema> theme = new(() => TypeBoxJsonSchema.FromPublished(Read(ThemeFileName)));
    private static readonly Lazy<TypeBoxJsonSchema> keybindingValue = new(() => Definition(KeybindingsFileName, "KeybindingValue"));

    /// <summary>model-config.ts ModelsConfigSchema.</summary>
    public static TypeBoxJsonSchema Models => models.Value;
    /// <summary>settings-schema.ts SettingsSchema.</summary>
    public static TypeBoxJsonSchema Settings => settings.Value;
    /// <summary>keybindings-schema.ts KeybindingsSchema.</summary>
    public static TypeBoxJsonSchema Keybindings => keybindings.Value;
    /// <summary>theme-schema.ts ThemeJsonSchema.</summary>
    public static TypeBoxJsonSchema Theme => theme.Value;
    /// <summary>key-id-schema.ts KeybindingValueSchema: a key id or an array of key ids.</summary>
    public static TypeBoxJsonSchema KeybindingValue => keybindingValue.Value;

    /// <summary>One <c>$defs</c> entry of a published schema, as its own schema (references inside it resolved against the same file).</summary>
    private static TypeBoxJsonSchema Definition(string fileName, string name)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(Read(fileName))!.AsObject();
        var definitions = root["$defs"]!.AsObject();
        var definition = definitions[name]!.DeepClone().AsObject();
        definition["$defs"] = definitions.DeepClone();
        return TypeBoxJsonSchema.FromPublished(definition.ToJsonString());
    }
}
