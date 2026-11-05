// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/src/harness/generation.ts (classify).
// Authored Pi v1.5.0 expectations over the Pi v1.2.0 captures (decision 0013: fixtures are not recaptured; area model-requests). Nothing here
// is a capture, and a byte comparison with records an upstream v1.5.0 run wrote needs a recapture. v1.5.0's classify records the requested
// thinking level on the provider message it classifies (d5b3de22d): `message.thinkingLevel = thinkingLevel`, the message's last key. v1.2.0
// recorded none. The captured scenarios never set a level, so each request asks for the agent's default, "off", and every message classify
// sees, as the entries, events, frames and JSON texts hold it, ends with "thinkingLevel":"off". Authored adds it to each assistant message
// with a stop reason other than "pending": a pending message is the partial pi.live holds while the provider streams, which classify has not
// seen. (A partial that an abort converts never passes classify either; no capture holds one.) The captures also hold no deferred answer, so
// no poll checkpoint changes. A resume of a captured session classifies only the messages it adds: the form that takes the captured records
// leaves the messages they already held as captured.
using PiSharp.Durable.Chord;

/// <summary>The authored v1.5.0 forms of v1.2.0 captures (see the file header).</summary>
internal static class ModelRequests150
{
    /// <summary>The level the captured scenarios' requests ask for: the agent's default.</summary>
    public const string ThinkingLevel = "off";

    /// <summary>What v1.5.0 writes where <paramref name="capture"/> holds what v1.2.0 wrote, for a run that wrote every message itself.</summary>
    public static JsonValue Authored(JsonValue capture) => Authored(capture, held: null);

    /// <summary>What v1.5.0 writes where <paramref name="capture"/> holds what v1.2.0 wrote resuming a session that held
    /// <paramref name="before"/>: the messages <paramref name="before"/> holds keep their captured form.</summary>
    public static JsonValue Authored(JsonValue capture, JsonValue before)
    {
        var held = new HashSet<string>(StringComparer.Ordinal);
        Collect(before, held);
        return Authored(capture, held);
    }

    /// <summary><see cref="Authored(JsonValue)"/> over persisted JSON text (a JSONL line, a SQLite column, a JSON-mode output line). A text
    /// that changes must print back as captured first, so nothing else in it moves.</summary>
    public static string AuthoredText(string text)
    {
        if (text.Length == 0 || text[0] is not ('{' or '[') || !text.Contains("\"role\":\"assistant\"", StringComparison.Ordinal)) return text;
        var value = JsonValue.Parse(text);
        var authored = Authored(value).ToJson();
        if (authored == value.ToJson()) return text;
        if (value.ToJson() != text) throw new InvalidOperationException($"a captured JSON text does not print back as captured: {text[..Math.Min(200, text.Length)]}");
        return authored;
    }

    private static JsonValue Authored(JsonValue capture, HashSet<string>? held) => capture switch
    {
        JsonArrayValue items => JsonValue.Array(items.Select(item => Authored(item, held))),
        JsonObjectValue message when Classified(message) =>
            held?.Contains(message.ToJson()) == true ? message : message.SetItem("thinkingLevel", ThinkingLevel),
        JsonObjectValue properties => JsonValue.Object(properties.Select(property => KeyValuePair.Create(property.Key, Authored(property.Value, held)))),
        _ => capture,
    };

    private static void Collect(JsonValue value, HashSet<string> held)
    {
        switch (value)
        {
            case JsonArrayValue items:
                foreach (var item in items) Collect(item, held);
                break;
            case JsonObjectValue message when Classified(message):
                held.Add(message.ToJson());
                break;
            case JsonObjectValue properties:
                foreach (var property in properties) Collect(property.Value, held);
                break;
        }
    }

    private static bool Classified(JsonObjectValue value) =>
        value.TryGetValue("role", out var role) && role is JsonStringValue { Value: "assistant" } &&
        value.TryGetValue("stopReason", out var stopReason) && stopReason is JsonStringValue { Value: not "pending" } &&
        !value.ContainsKey("thinkingLevel");
}
