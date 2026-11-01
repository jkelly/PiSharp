// Decision 0011 (run W4 fix 2, bench): Strict (constrainedSampling) in the Tool Bench. A tool switched to Strict goes to the run engine as
// RunTool.Strict, which RunModelOptions sends as strict: true on the OpenAI APIs (Chat Completions in the function definition; Responses,
// Azure Responses and the Codex backend on the function tool) and accepts offline on the faux models without applying it. A model whose API
// takes no strict tools (Anthropic Messages, Gemini, the APIs PiSharp does not patch) is never sent the turn with Strict silently dropped:
// the bench refuses it before anything is sent, naming the strict tools and the reason, and the tool list says so beforehand. OpenAI's
// strict mode also asks more of the schema ("additionalProperties": false on every object, every property required); the editor shows
// what a schema misses as a warning, not a refusal, since the provider has the last word.
using System.Text.Json.Nodes;
using PiSharp.WebAgent.Runs;

namespace PiSharp.WebAgent.Bench;

/// <summary>What the Tool Bench does with strict tools on a model, and what OpenAI's strict mode asks of a schema.</summary>
public static class BenchStrict
{
    /// <summary>The most schema problems listed.</summary>
    public const int MaxProblems = 20;

    /// <summary>Where Strict is sent, for a model that takes no strict tools.</summary>
    public const string Where = "the OpenAI APIs (Chat Completions, Responses and the Codex backend)";

    /// <summary>The model's support of strict tools, or null when PiSharp cannot run the model.</summary>
    public static RunOptionSupport? Support(RunCapabilities? capabilities) => capabilities?.Find(RunModelOptions.StrictTools);

    /// <summary>Whether the model takes strict tools (true when PiSharp cannot run it: Run says that first).</summary>
    public static bool Taken(RunCapabilities? capabilities) => Support(capabilities) is not { Supported: false };

    /// <summary>Why the next turn of <paramref name="setup"/> on <paramref name="capabilities"/>' model may not be sent, or null: a tool
    /// that is on is strict, and the model's API takes no strict tools.</summary>
    public static string? Refusal(BenchSetup setup, RunCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(capabilities);
        var strict = setup.EnabledStrict;
        if (strict.Count == 0 || Support(capabilities) is not { Supported: false } support) return null;
        return $"Strict is on for {Names(strict)}, and {capabilities.Provider}/{capabilities.Model} cannot take it: {support.Reason ?? "its API has no strict tools"}. " +
            $"Switch Strict off for {(strict.Count == 1 ? "it" : "them")} in the tool's editor, or choose a model on {Where}; nothing was sent.";
    }

    /// <summary>The line under the tool list's schema preview: its text and tone (<c>faint</c> or <c>danger</c>).</summary>
    public static (string Text, string Tone) ListNote(BenchSetup setup, RunCapabilities? capabilities)
    {
        ArgumentNullException.ThrowIfNull(setup);
        var strict = setup.EnabledStrict;
        if (strict.Count == 0) return ("Strict (constrainedSampling) is off, so arguments can break the schema. The bench validates every call.", "faint");
        var names = Names(strict);
        if (capabilities is null) return ($"Strict is on for {names}: it is sent as strict: true on {Where}.", "faint");
        if (Support(capabilities) is { Supported: false } support)
            return ($"Strict is on for {names}, but this model cannot take it: {support.Reason ?? "its API has no strict tools"}. Run refuses until you " +
                $"switch Strict off for {(strict.Count == 1 ? "it" : "them")} or choose a model on {Where}.", "danger");
        if (capabilities.Offline)
            return ($"Strict is on for {names}: the faux model accepts it offline and does not apply it. The bench validates every call.", "faint");
        return ($"Strict is on for {names}: sent as strict: true on {Api(capabilities.Api)}, so the provider keeps the arguments to the schema. " +
            "The bench still validates every call.", "faint");
    }

    /// <summary>What the tool editor says under Strict for the bench's model (<paramref name="name"/>: its display name).</summary>
    public static string EditorNote(RunCapabilities? capabilities, string? name)
    {
        const string Leave = " Leave it off to see how often the model breaks the schema.";
        if (capabilities is null) return $"Asks the provider to keep the arguments to this schema: strict: true on {Where}; Anthropic and Gemini refuse it." + Leave;
        var model = string.IsNullOrWhiteSpace(name) ? capabilities.Model : name;
        if (Support(capabilities) is { Supported: false } support)
            return $"{model} cannot take Strict: {support.Reason ?? "its API has no strict tools"}. Run refuses while a tool that is on is strict; Strict is sent " +
                $"as strict: true on {Where}.";
        if (capabilities.Offline)
            return $"The faux model accepts Strict offline and does not apply it; on {Where} it is sent as strict: true." + Leave;
        return $"Sent to {model} as strict: true on {Api(capabilities.Api)}: the provider keeps the arguments to this schema. OpenAI's strict mode needs " +
            "\"additionalProperties\": false on every object and every property in \"required\" (allow null for an optional one)." + Leave;
    }

    /// <summary>What OpenAI's strict mode would refuse in <paramref name="parameters"/> (an object schema; null is the empty one): an
    /// object without <c>"additionalProperties": false</c>, a property missing from <c>required</c>. Empty when it asks nothing more.</summary>
    public static IReadOnlyList<string> Problems(JsonObject? parameters)
    {
        var problems = new List<string>();
        Walk(parameters ?? BenchSchema.EmptyParameters(), "", problems, 0);
        return problems;
    }

    /// <summary>The API as a sentence names it.</summary>
    public static string Api(string api) => api switch
    {
        RunModelOptions.CompletionsApi => "the Chat Completions API",
        RunModelOptions.ResponsesApi => "the Responses API",
        RunModelOptions.AzureResponsesApi => "the Azure OpenAI Responses API",
        RunModelOptions.CodexApi => "the Codex backend",
        RunModelOptions.AnthropicApi => "the Anthropic Messages API",
        RunModelOptions.GoogleApi => "the Gemini API",
        _ => $"the {api} API",
    };

    /// <summary>"a", "a and b", "a, b and c", "a, b, c and 3 more".</summary>
    private static string Names(IReadOnlyList<BenchTool> tools)
    {
        var names = tools.Select(tool => tool.Name).ToList();
        if (names.Count > 4) return string.Join(", ", names.Take(3)) + $" and {names.Count - 3} more";
        return names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
    }

    private static void Walk(JsonNode? node, string path, List<string> problems, int depth)
    {
        if (problems.Count >= MaxProblems || depth > BenchSchema.MaxDepth || node is not JsonObject schema) return;
        if (path.Length == 0 || IsObject(schema))
        {
            if (!(schema["additionalProperties"] is JsonValue extra && extra.TryGetValue<bool>(out var allowed) && !allowed))
                problems.Add($"{Place(path)}: set \"additionalProperties\": false");
            var names = schema["properties"] is JsonObject declared ? declared.Select(pair => pair.Key).ToList() : [];
            var required = schema["required"] is JsonArray list
                ? list.Select(item => item is JsonValue value && value.TryGetValue<string>(out var text) ? text : null).OfType<string>().ToHashSet(StringComparer.Ordinal)
                : [];
            var missing = names.Where(name => !required.Contains(name)).ToList();
            if (missing.Count > 0)
                problems.Add($"{Place(path)}: list every property in \"required\" ({string.Join(", ", missing.Take(6))}{(missing.Count > 6 ? ", …" : "")} " +
                    $"{(missing.Count == 1 ? "is" : "are")} not); allow null for an optional one");
        }
        if (schema["properties"] is JsonObject properties)
            foreach (var (key, child) in properties) Walk(child, path + "/properties/" + key, problems, depth + 1);
        switch (schema["items"])
        {
            case JsonObject items:
                Walk(items, path + "/items", problems, depth + 1);
                break;
            case JsonArray tuple:
                for (var index = 0; index < tuple.Count; index++) Walk(tuple[index], $"{path}/items/{index}", problems, depth + 1);
                break;
        }
        foreach (var keyword in new[] { "anyOf", "oneOf", "allOf", "prefixItems" })
            if (schema[keyword] is JsonArray options)
                for (var index = 0; index < options.Count; index++) Walk(options[index], $"{path}/{keyword}/{index}", problems, depth + 1);
        foreach (var keyword in new[] { "$defs", "definitions" })
            if (schema[keyword] is JsonObject definitions)
                foreach (var (key, child) in definitions) Walk(child, $"{path}/{keyword}/{key}", problems, depth + 1);
    }

    private static bool IsObject(JsonObject schema) => schema["type"] switch
    {
        JsonValue value when value.TryGetValue<string>(out var type) => type == "object",
        JsonArray types => types.Any(item => item is JsonValue value && value.TryGetValue<string>(out var type) && type == "object"),
        _ => schema["properties"] is JsonObject,
    };

    private static string Place(string path) => "parameters" + path;
}
