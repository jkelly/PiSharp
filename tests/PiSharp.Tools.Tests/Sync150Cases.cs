// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/ai/src/utils/validation.ts (validateToolArguments on pi-schema).
//
// Pi v1.5.0 sync cases of this suite. Authored, not captured: the 1.1.0 validation goldens (ToolValidation/, captured from pi-ai 1.1.0
// with TypeBox 1.3.27) hold inputs whose verdicts changed with pi-schema and are not recaptured. Their 384 inputs are checked here
// against what the 1.5.0 source guarantees.
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Agent;
using PiSharp.Contracts;

internal static class Sync150Cases
{
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("sync150.validation-golden-inputs-keep-the-pi-schema-message-and-coercion-guarantees", GoldenInputs),
    ];

    // For every golden input: a failure is `Validation failed for tool "<name>":` with one `  - <path>: <message>` line per error and the
    // arguments as JSON.stringify(arguments, null, 2) prints them (unchanged since 1.1.0, so the golden error's tail where it failed then);
    // valid arguments validate again to themselves; the schema origin and TypeBox's "~kind" metadata change no result.
    private static Task GoldenInputs()
    {
        var root = ToolValidationTests.Goldens(); var schemas = root.GetProperty("schemas"); var count = 0; var failures = new List<string>();
        foreach (var item in root.GetProperty("cases").EnumerateArray())
        {
            var name = item.GetProperty("schema").GetString()!; var entry = schemas.GetProperty(name);
            var schema = entry.GetProperty("schema").GetRawText(); var args = item.GetProperty("argsJson").GetString()!;
            var result = ToolArgumentValidation.ValidateJson(name, schema, args, ToolValidationTests.Origin(entry.GetProperty("origin").GetString()!));
            count++;
            var plain = ToolArgumentValidation.ValidateJson(name, Strip(JsonNode.Parse(schema))!.ToJsonString(), args, ToolSchemaOrigin.JsonSchema);
            if (ToolValidationTests.Outcome(plain) != ToolValidationTests.Outcome(result))
                failures.Add($"{name} {args}: origin or metadata changed the result: {ToolValidationTests.Outcome(result)} / {ToolValidationTests.Outcome(plain)}");
            if (result.IsValid)
            {
                var again = ToolArgumentValidation.ValidateJson(name, schema, result.ArgumentsJson!, ToolSchemaOrigin.JsonSchema);
                if (!again.IsValid || again.ArgumentsJson != result.ArgumentsJson) failures.Add($"{name} {args}: {result.ArgumentsJson} validates to {ToolValidationTests.Outcome(again)}");
                continue;
            }

            var message = result.ErrorMessage!;
            var header = $"Validation failed for tool \"{name}\":\n";
            var split = message.IndexOf("\n\nReceived arguments:\n", StringComparison.Ordinal);
            if (!message.StartsWith(header, StringComparison.Ordinal) || split < 0) { failures.Add($"{name} {args}: {message}"); continue; }
            var lines = message[header.Length..split].Split('\n');
            if (lines.Any(line => !line.StartsWith("  - ", StringComparison.Ordinal) || !line.Contains(": ", StringComparison.Ordinal) || line.Length <= 6))
                failures.Add($"{name} {args}: error lines {message[header.Length..split]}");
            var expected = item.GetProperty("expected");
            if (!expected.GetProperty("ok").GetBoolean() && expected.GetProperty("error").GetString() is { } before && before.IndexOf("\n\nReceived arguments:\n", StringComparison.Ordinal) is var at and >= 0
                && before[at..] != message[split..])
                failures.Add($"{name} {args}: received arguments {message[split..]} instead of {before[at..]}");
        }

        Check(count == 384, "Golden case count changed: " + count);
        Check(failures.Count == 0, failures.Count + " golden inputs break a guarantee:\n" + string.Join("\n", failures.Take(8)));
        return Task.CompletedTask;
    }

    private static JsonNode? Strip(JsonNode? node) => node switch
    {
        JsonObject value => new JsonObject(value.Where(property => !property.Key.StartsWith('~')).Select(property => KeyValuePair.Create(property.Key, Strip(property.Value)))),
        JsonArray value => new JsonArray([.. value.Select(Strip)]),
        _ => node?.DeepClone()
    };

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
