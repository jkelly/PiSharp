// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/extensions/codemode/tool.ts (CodemodeModelRuntime,
// CodemodeToolOptions, CodemodeNestedCall, CodemodeToolDetails), packages/coding-agent/src/extensions/codemode/execute.ts
// (readCodemodeStore) and packages/coding-agent/src/utils/output-files.ts (writeOutputFile).
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Contracts.ModelOperations;

namespace PiSharp.Codemode;

/// <summary>The part of the model registry scripts reach through <c>models</c>. Models are JSON catalog entries without
/// <c>headers</c>. Classification and image generation never throw for provider failures; they return error results.</summary>
public interface ICodemodeModelRuntime
{
    ImmutableArray<JsonData> GetModelsOfType(ModelType type, string? provider);
    Task<ImmutableArray<JsonData>> GetAvailableOfTypeAsync(ModelType type, string? provider, CancellationToken cancellationToken);
    JsonData? GetModelOfType(ModelType type, string provider, string id);
    /// <summary><paramref name="model"/> is the registry's own entry, resolved by provider and id.</summary>
    Task<ClassifierResult> ClassifyAsync(JsonData model, ClassifierContext context, CancellationToken cancellationToken);
    Task<AssistantImages> GenerateImagesAsync(JsonData model, ImagesContext context, CancellationToken cancellationToken);
}

/// <summary>The outcome of one nested call through the session's tool pipeline. <paramref name="Result"/> is the tool result
/// (<c>{ content, details?, structuredContent?, ... }</c>); <see cref="ScriptValue"/> overrides its <c>structuredContent</c>
/// as the value a tool with an output schema resolves to (MCP tools resolve to their whole <c>CallToolResult</c>).</summary>
public sealed record CodemodeNestedOutcome(string? ToolCallId, JsonData Result, bool IsError)
{
    public JsonData? ScriptValue { get; init; }
}

/// <summary>The session as the codemode tool sees it (ExtensionToolContext in the original).</summary>
public interface ICodemodeHost
{
    /// <summary>The agent loop's nested tools: active direct tools and every codemode or deferred tool.</summary>
    ImmutableArray<CodemodeNestedTool> Tools { get; }
    /// <summary>Runs a nested call through the session's tool pipeline (validation, hooks, permissions), with the codemode
    /// call as its parent.</summary>
    ValueTask<CodemodeNestedOutcome> ExecuteToolAsync(string name, JsonData? arguments, CancellationToken cancellationToken);
    /// <summary>Values of <c>load()</c>: the <c>codemode-store</c> entries on the current branch, applied from the root.</summary>
    IReadOnlyList<KeyValuePair<string, JsonData>> ReadStore();
    /// <summary>Persists one successful script's <c>store()</c> writes as a <c>codemode-store</c> custom entry.</summary>
    ValueTask AppendStoreEntryAsync(JsonData data, CancellationToken cancellationToken);
    /// <summary>The model registry, or null when scripts get no <c>models</c> namespace.</summary>
    ICodemodeModelRuntime? Models { get; }
}

/// <summary>Store entries and output files shared by hosts.</summary>
public static class CodemodeStore
{
    /// <summary>execute.ts readCodemodeStore over branch custom entries (<c>customType</c>, <c>data</c>), root first.</summary>
    public static ImmutableArray<KeyValuePair<string, JsonData>> Read(IEnumerable<(string? CustomType, JsonElement Data)> branch)
    {
        var keys = new List<string>(); var values = new Dictionary<string, JsonData>(StringComparer.Ordinal);
        foreach (var (customType, data) in branch)
        {
            if (customType != CodemodeToolDefinition.StoreEntryType || data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("set", out var set) || set.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("delete", out var deleted) || deleted.ValueKind != JsonValueKind.Array ||
                deleted.EnumerateArray().Any(key => key.ValueKind != JsonValueKind.String)) continue;
            foreach (var key in deleted.EnumerateArray()) { values.Remove(key.GetString()!); keys.Remove(key.GetString()!); }
            foreach (var (key, value) in ModelOperationJson.ObjectEntries(set))
            {
                if (!values.ContainsKey(key)) keys.Add(key);
                values[key] = JsonData.FromElement(value.Clone());
            }
        }
        return [.. ModelOperationJson.Ordered(keys.Select(key => KeyValuePair.Create(key, values[key])))];
    }

    /// <summary>The custom entry data of one script's writes: <c>{ set, delete }</c>.</summary>
    public static JsonData EntryData(CodemodeStoreWrites writes)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject(); writer.WriteStartObject("set");
            foreach (var (key, value) in writes.Set) { writer.WritePropertyName(key); value.Value.WriteTo(writer); }
            writer.WriteEndObject(); writer.WriteStartArray("delete");
            foreach (var key in writes.Delete) writer.WriteStringValue(key);
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        return JsonData.Parse(System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
    }

    /// <summary>output-files.ts writeOutputFile: a new <c>&lt;temp&gt;/&lt;prefix&gt;-&lt;16 hex&gt;&lt;extension&gt;</c> only the user
    /// can read, never following a link someone else placed at the path.</summary>
    public static async Task<string> WriteOutputFileAsync(string prefix, string extension, byte[] data, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}{extension}");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var stream = new FileStream(path, options);
        await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        return path;
    }
}
