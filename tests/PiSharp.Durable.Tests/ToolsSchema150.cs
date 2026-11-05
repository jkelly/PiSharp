// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/schema/src/builders.ts (obj), packages/schema/src/explain.ts, packages/durable/src/tools/read.ts,
// write.ts, edit.ts and bash.ts, packages/durable/src/tool-search/tool-search.ts, packages/durable/src/codemode/codemode.ts.
// Authored Pi v1.5.0 expectations over the Pi v1.2.0 captures (decision 0013: fixtures are not recaptured; area tools-schema). Nothing here is
// a capture, and a byte comparison with records an upstream v1.5.0 run wrote needs a recapture. Durable's tools moving to pi-schema changes
// two things v1.5.0 writes where the v1.2.0 captures hold TypeBox's:
// - The built-in tools (read, write, edit, bash, powershell, tool_search, codemode) build their parameters, and bash's and powershell's
//   structuredOutputSchema, with pi-schema's obj(), which puts "properties" before "required"; TypeBox's Type.Object printed "required"
//   first. The values are the same. PiSchemaOrder moves "required" after "properties" in each object schema; Authored applies it to every
//   declaration of a built-in tool (an object with that name and a parameters object). The tools a test or scenario defines keep the
//   schemas it gives them, which v1.5.0 writes as given.
// - Tool argument and structured output validation print pi-schema's messages (explain.ts: "expected <types>, got <type>"), where the
//   captures hold TypeBox's ("must be <type>"). Messages pairs each captured message with the one v1.5.0 prints for the same value, and
//   Usages the faux provider's usage of the request that carries it (and the pi.usage totals), which its length changes.
using PiSharp.Durable.Chord;

/// <summary>The authored v1.5.0 forms of v1.2.0 captures (see the file header).</summary>
internal static class ToolsSchema150
{
    private static readonly HashSet<string> BuiltIns = new(["read", "write", "edit", "bash", "powershell", "tool_search", "codemode"], StringComparer.Ordinal);

    private static readonly (string Captured, string Authored)[] Messages =
    [
        // compat structured-output: "wrong" returns { count: "x" } for obj({ count: num() }).
        ("count: must be number", "count: expected number, got string"),
        // tool-rounds rejections: echo's { text: { not: "a string" } } for { text: string }.
        ("  - text: must be string", "  - text: expected string, got object"),
    ];

    // pi-ai's faux provider estimates usage from the prompt's length (ceil(characters / 4); withUsageEstimate), so the request that carries a
    // longer pi-schema message, 13 characters longer in both cases, reports more input and cache-write tokens, and the conversation's pi.usage
    // totals grow by as much. Each row is the captured usage (its token counts) and the one the faux estimate gives for v1.5.0's message.
    private static readonly (string Captured, string Authored)[] Usages =
    [
        // compat structured-output: the request after "wrong"'s result, and pi.usage.
        ("""{"input":133,"output":1,"cacheRead":166,"cacheWrite":133,"totalTokens":433""", """{"input":136,"output":1,"cacheRead":166,"cacheWrite":136,"totalTokens":439"""),
        ("""{"input":299,"output":8,"cacheRead":313,"cacheWrite":300,"totalTokens":920""", """{"input":302,"output":8,"cacheRead":313,"cacheWrite":303,"totalTokens":926"""),
        // tool-rounds rejections: the request after the rejected call's result, and pi.usage.
        ("""{"input":106,"output":1,"cacheRead":64,"cacheWrite":106,"totalTokens":277""", """{"input":109,"output":1,"cacheRead":64,"cacheWrite":110,"totalTokens":284"""),
        ("""{"input":170,"output":21,"cacheRead":64,"cacheWrite":170,"totalTokens":425""", """{"input":173,"output":21,"cacheRead":64,"cacheWrite":174,"totalTokens":432"""),
    ];

    /// <summary>pi-schema's <c>obj({})</c>, closed (v1.5.0's tests declare a tool without parameters with it; TypeBox's
    /// <c>Type.Object({})</c>, ToolRegistration.EmptyParameters, was open).</summary>
    public static readonly JsonObjectValue EmptyObject = (JsonObjectValue)JsonValue.Parse("""{"type":"object","properties":{},"additionalProperties":false}""");

    /// <summary><paramref name="schema"/> as pi-schema's obj() orders it: "required" right after "properties", in it and in the object
    /// schemas its properties, items and unions hold.</summary>
    public static JsonValue PiSchemaOrder(JsonValue schema)
    {
        if (schema is not JsonObjectValue node) return schema;
        var required = node.ContainsKey("properties") && node.TryGetValue("required", out var names) ? names : null;
        var members = new List<KeyValuePair<string, JsonValue>>();
        foreach (var (key, value) in node)
        {
            if (required is not null && key == "required") continue;
            members.Add(KeyValuePair.Create(key, key switch
            {
                "properties" when value is JsonObjectValue properties =>
                    JsonValue.Object(properties.Select(property => KeyValuePair.Create(property.Key, PiSchemaOrder(property.Value)))),
                "items" => PiSchemaOrder(value),
                "anyOf" or "oneOf" or "allOf" when value is JsonArrayValue branches => JsonValue.Array(branches.Select(PiSchemaOrder)),
                _ => value,
            }));
            if (required is not null && key == "properties") members.Add(KeyValuePair.Create("required", required));
        }
        return JsonValue.Object(members);
    }

    /// <summary>PiSchemaOrder over JSON text.</summary>
    public static string PiSchemaOrder(string schemaJson) => PiSchemaOrder(JsonValue.Parse(schemaJson)).ToJson();

    /// <summary>What v1.5.0 writes where <paramref name="capture"/> holds what v1.2.0 wrote: built-in declarations in pi-schema order, and
    /// pi-schema's validation messages.</summary>
    public static JsonValue Authored(JsonValue capture) => capture switch
    {
        JsonArrayValue items => JsonValue.Array(items.Select(Authored)),
        JsonObjectValue usage when AuthoredUsage(usage) is { } authored => authored,
        JsonObjectValue declaration when IsBuiltInDeclaration(declaration) => JsonValue.Object(declaration.Select(property => KeyValuePair.Create(property.Key,
            property.Key is "parameters" or "structuredOutputSchema" ? PiSchemaOrder(property.Value) : Authored(property.Value)))),
        JsonObjectValue properties => JsonValue.Object(properties.Select(property => KeyValuePair.Create(property.Key, Authored(property.Value)))),
        JsonStringValue text => Message(text.Value),
        _ => capture,
    };

    /// <summary><see cref="Authored(JsonValue)"/> over persisted JSON text (a JSONL line, a SQLite column); other text gets the messages
    /// only. A text whose declarations change must print back as captured before it changes, so nothing else in it moves.</summary>
    public static string AuthoredText(string text)
    {
        var messages = Usage(Message(text));
        if (messages.Length == 0 || messages[0] is not ('{' or '[') || !messages.Contains("\"required\":[", StringComparison.Ordinal)) return messages;
        var value = JsonValue.Parse(messages);
        var authored = Authored(value).ToJson();
        if (authored == value.ToJson()) return messages;
        if (value.ToJson() != messages) throw new InvalidOperationException($"a captured JSON text does not print back as captured: {messages[..Math.Min(200, messages.Length)]}");
        return authored;
    }

    private static JsonValue? AuthoredUsage(JsonObjectValue value)
    {
        if (!value.ContainsKey("totalTokens")) return null;
        var text = value.ToJson();
        foreach (var (captured, authored) in Usages)
            if (text.StartsWith(captured + ",", StringComparison.Ordinal)) return JsonValue.Parse(authored + text[captured.Length..]);
        return null;
    }

    private static bool IsBuiltInDeclaration(JsonObjectValue value) =>
        value.TryGetValue("name", out var name) && name is JsonStringValue { Value: var tool } && BuiltIns.Contains(tool) &&
        value.TryGetValue("parameters", out var parameters) && parameters is JsonObjectValue;

    private static string Message(string text)
    {
        foreach (var (captured, authored) in Messages) text = text.Replace(captured, authored, StringComparison.Ordinal);
        return text;
    }

    private static string Usage(string text)
    {
        foreach (var (captured, authored) in Usages) text = text.Replace(captured + ",", authored + ",", StringComparison.Ordinal);
        return text;
    }
}
