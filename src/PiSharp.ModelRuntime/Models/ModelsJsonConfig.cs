// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/coding-agent/src/core/model-config.ts (ModelsConfigSchema with
// packages/ai/src/providers/{compat-schema,model-schema}.ts, as published in packages/coding-agent/schemas/models.schema.json),
// packages/coding-agent/src/utils/json.ts (stripJsonComments) and utils/text.ts (stripBom).
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PiSharp.Cli.Models;

/// <summary>One immutable, credential-blind load of <c>models.json</c>. A missing file is an empty config; a read, parse or schema
/// failure is an empty config with the upstream error text.</summary>
internal sealed partial class ModelsJsonConfig
{
    private readonly ImmutableDictionary<string, JsonObject> providers;
    private readonly ImmutableArray<string> order;
    internal string? Error { get; }

    private ModelsJsonConfig(IEnumerable<(string Id, JsonObject Provider)> providers, string? error)
    {
        var list = providers.ToList();
        this.providers = list.ToImmutableDictionary(pair => pair.Id, pair => pair.Provider, StringComparer.Ordinal);
        order = [.. list.Select(pair => pair.Id)];
        Error = error;
    }

    internal static ModelsJsonConfig Empty { get; } = new([], null);

    internal JsonObject? GetProvider(string id) => providers.TryGetValue(id, out var provider) ? provider : null;
    internal ImmutableArray<string> ProviderIds => order;

    /// <summary>Source ModelConfig.load: null path or a missing file is empty.</summary>
    internal static ModelsJsonConfig Load(string? path)
    {
        if (path is null) return Empty;
        string content;
        try { content = File.ReadAllText(path, new System.Text.UTF8Encoding(false, false)); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return Empty; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new([], $"Failed to load models.json: {error.Message}\n\nFile: {path}"); }
        return Parse(content, path);
    }

    internal static ModelsJsonConfig Parse(string content, string path)
    {
        JsonNode? parsed;
        var stripped = StripJsonComments(StripBom(content));
        try { parsed = JsonTree.Parse(stripped); }
        catch (JsonException error)
        { return new([], $"Failed to parse models.json: {PiSharp.Contracts.Compatibility.JsJsonSyntax.Describe(stripped, error.Message)}\n\nFile: {path}"); }
        // validateModelsConfig: the published models schema (model-config.ts ModelsConfigSchema with pi-ai's compat and model schemas),
        // checked and reported by the TypeBox engine; a top-level $schema string is accepted.
        var schema = PiSharp.CodingAgent.Configuration.PiConfigurationSchemas.Models;
        if (!schema.CheckJson(stripped))
        {
            var errors = string.Join("\n", schema.ErrorsJson(stripped).Select(error => $"  - {FormatValidationPath(error)}: {error.Message}"));
            return new([], $"Invalid models.json schema:\n{(errors.Length > 0 ? errors : "Unknown schema error")}\n\nFile: {path}");
        }
        var root = (JsonObject)parsed!;
        return new(((JsonObject)root["providers"]!).Select(pair => (pair.Key, JsonTree.CloneObject((JsonObject)pair.Value!))), null);
    }

    /// <summary>Source formatValidationPath: dotted instance path ("root" for the document); a required error names its first property.</summary>
    private static string FormatValidationPath(PiSharp.Agent.TypeBoxSchemaError error)
    {
        var path = error.InstancePath.StartsWith('/') ? error.InstancePath[1..] : error.InstancePath;
        path = path.Replace('/', '.');
        if (error.Keyword == "required" && error.RequiredProperties.Count > 0 && error.RequiredProperties[0].Length > 0)
            return path.Length > 0 ? path + "." + error.RequiredProperties[0] : error.RequiredProperties[0];
        return path.Length > 0 ? path : "root";
    }

    internal static string StripBom(string content) => content.Length > 0 && content[0] == '﻿' ? content[1..] : content;

    /// <summary>Source stripJsonComments: <c>//</c> line comments and trailing commas outside string literals.</summary>
    internal static string StripJsonComments(string input)
    {
        var withoutComments = LineComments().Replace(input, match => match.Value[0] == '"' ? match.Value : "");
        return TrailingCommas().Replace(withoutComments, match => match.Groups[1].Success ? match.Groups[1].Value : match.Value[0] == '"' ? match.Value : "");
    }

    [GeneratedRegex("\"(?:\\\\.|[^\"\\\\])*\"|//[^\\n]*")]
    private static partial Regex LineComments();
    [GeneratedRegex("\"(?:\\\\.|[^\"\\\\])*\"|,(\\s*[}\\]])")]
    private static partial Regex TrailingCommas();
}
