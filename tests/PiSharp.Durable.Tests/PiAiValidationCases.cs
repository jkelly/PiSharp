// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/ai/test/validation.test.ts ("validateToolArguments"), as piai.validation.<case>,
// for PiSharp.Durable's port of packages/ai/src/utils/validation.ts (Internal/PiAi/Validation.cs, which validates on PiSharp.Contracts.Schema's
// pi-schema port, as PiSharp.Agent's ToolArgumentValidation does; the core does not reference PiSharp.Agent). Verbatim apart from these:
// - A TypeBox-built schema is the JSON TypeBox prints for it: Type.Object lists "required" first, and Type.Optional leaves the property out of it.
// - "still validates when Function constructor is unavailable": the C# validator never generates code, so only its coercion check remains.
// - toThrow(text) checks that the message contains text, as vitest's toThrow(string) does; toEqual compares JSON values.
// - "returns a copy of the arguments": the result is a new value, not the call's.
// Then the differential, piai.validation.matches-pisharp-agent-s-port: the copy and PiSharp.Agent's port return the same message or value
// for the 60 inputs of Fixtures/v1.2.0/piai-validation.upstream.json (TypeBox-built schemas and their plain JSON copies; the test project
// reaches PiSharp.Agent through PiSharp.Durable.Tools). That capture holds Pi v1.2.0's TypeBox messages and conversions, which v1.5.0's
// pi-schema validation no longer gives (decision 0013, area tools-schema); only its inputs are used.
using PiSharp.Contracts;
using PiSharp.Durable.Chord;
using static SessionTests;
using DurableValidation = PiSharp.Durable.Internal.PiAi.Validation.ToolArgumentValidation;
using AgentValidation = PiSharp.Agent.ToolArgumentValidation;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> PiAiValidationCases() => PiAiValidationTests.Cases();
}

/// <summary>The piai.validation.* cases.</summary>
internal static class PiAiValidationTests
{
    private static readonly Lazy<JsonArrayValue> Upstream = new(() => (JsonArrayValue)JsonValue.Parse(
        File.ReadAllText(Path.Combine(Program.RepositoryRoot(), "tests", "PiSharp.Durable.Tests", "Fixtures", "v1.2.0", "piai-validation.upstream.json"))));

    private static IEnumerable<(string Schema, ToolSchemaOrigin Origin, string Arguments)> Rows() =>
        Upstream.Value.Cast<JsonObjectValue>().Select(row => (row["schema"].ToJson(),
            S(row["origin"]) == "typebox" ? ToolSchemaOrigin.TypeBox : ToolSchemaOrigin.JsonSchema, row["arguments"].ToJson()));

    /// <summary>createToolCallWithPlainSchema's parameters: <c>{ value: schema }</c>, required.</summary>
    private static string Plain(string schema) => $$"""{"type":"object","properties":{"value":{{schema}}},"required":["value"]}""";

    private static void Accepts(string parameters, string arguments, string expected, string what)
    {
        var result = DurableValidation.ValidateJson("echo", parameters, arguments);
        Program.Check(result.IsValid, $"{what}: unexpected error {result.ErrorMessage}");
        Program.Check(JsonValue.DeepEquals(JsonValue.Parse(expected), result.Value), $"{what}: expected {expected}, actual {result.ArgumentsJson}");
    }

    private static void Throws(string parameters, string arguments, string text, string what)
    {
        var result = DurableValidation.ValidateJson("echo", parameters, arguments);
        Program.Check(result.ErrorMessage?.Contains(text, StringComparison.Ordinal) == true, $"{what}: expected an error containing {text}, got {result.ErrorMessage ?? result.ArgumentsJson}");
    }

    internal static IEnumerable<(string, Func<Task>)> Cases()
    {
        yield return Program.Case("piai.validation.still-validates-when-function-constructor-is-unavailable", () =>
        {
            Accepts("""{"type":"object","required":["count"],"properties":{"count":{"type":"number"}}}""", """{"count":"42"}""", """{"count":42}""", "count");
            return Task.CompletedTask;
        });

        yield return Program.Case("piai.validation.coerces-serialized-plain-json-schemas-like-typebox-schemas", () =>
        {
            (string Schema, string Input, string Expected)[] passing =
            [
                ("""{"type":"number"}""", "\"42\"", "42"),
                ("""{"type":"number"}""", "true", "1"),
                ("""{"type":"number"}""", "null", "0"),
                ("""{"type":"integer"}""", "\"42\"", "42"),
                ("""{"type":"boolean"}""", "\"true\"", "true"),
                ("""{"type":"boolean"}""", "\"false\"", "false"),
                ("""{"type":"boolean"}""", "1", "true"),
                ("""{"type":"boolean"}""", "0", "false"),
                // TypeBox's conversion rules come first: null is "null" for a required string.
                ("""{"type":"string"}""", "null", "\"null\""),
                ("""{"type":"string"}""", "true", "\"true\""),
                ("""{"type":"null"}""", "\"\"", "null"),
                ("""{"type":"null"}""", "0", "null"),
                ("""{"type":"null"}""", "false", "null"),
                ("""{"type":["number","string"]}""", "\"1\"", "\"1\""),
                ("""{"type":["boolean","number"]}""", "\"1\"", "1"),
                ("""{"type":"boolean"}""", "\"1\"", "true"),
                ("""{"type":"boolean"}""", "\"0\"", "false"),
                ("""{"type":"null"}""", "\"null\"", "null"),
                ("""{"type":"integer"}""", "\"42.1\"", "42"),
                ("""{"type":"array","items":{"type":"number"}}""", "\"7\"", "[7]"),
            ];
            foreach (var (schema, input, expected) in passing)
                Accepts(Plain(schema), $$"""{"value":{{input}}}""", $$"""{"value":{{expected}}}""", $"{schema} {input}");
            return Task.CompletedTask;
        });

        yield return Program.Case("piai.validation.treats-null-as-omission-for-optional-non-nullable-properties", () =>
        {
            // Type.Object({ path: Type.String(), offset: Type.Optional(Type.Number()), nullable: Type.Optional(Type.Union([Type.String(),
            // Type.Null()])), metadata: Type.Object({ enabled: Type.Optional(Type.Boolean()) }) }).
            Accepts("""
                {"type":"object","required":["path","metadata"],"properties":{"path":{"type":"string"},"offset":{"type":"number"},"nullable":{"anyOf":[{"type":"string"},{"type":"null"}]},"metadata":{"type":"object","properties":{"enabled":{"type":"boolean"}}}}}
                """, """{"path":"file.txt","offset":null,"nullable":null,"metadata":{"enabled":null}}""",
                """{"path":"file.txt","nullable":null,"metadata":{}}""", "arguments");
            return Task.CompletedTask;
        });

        yield return Program.Case("piai.validation.preserves-optional-nulls-whose-referenced-schema-is-nullable", () =>
        {
            Accepts("""{"type":"object","properties":{"value":{"$ref":"#/$defs/value"}},"$defs":{"value":{"anyOf":[{"type":"number"},{"type":"null"}]}}}""",
                """{"value":null}""", """{"value":null}""", "arguments");
            return Task.CompletedTask;
        });

        yield return Program.Case("piai.validation.preserves-a-value-that-already-matches-a-nullable-union-arm", () =>
        {
            Accepts("""{"type":"object","required":["value"],"properties":{"value":{"anyOf":[{"type":"number"},{"type":"null"}]}}}""",
                """{"value":null}""", """{"value":null}""", "arguments");
            return Task.CompletedTask;
        });

        yield return Program.Case("piai.validation.preserves-a-value-that-already-matches-a-oneof-nullable-union-arm", () =>
        {
            Accepts(Plain("""{"oneOf":[{"type":"number"},{"type":"null"}]}"""), """{"value":null}""", """{"value":null}""", "arguments");
            return Task.CompletedTask;
        });

        yield return Program.Case("piai.validation.still-coerces-nullable-unions-when-the-original-value-does-not-match-any-arm", () =>
        {
            Accepts(Plain("""{"anyOf":[{"type":"number"},{"type":"null"}]}"""), """{"value":"42"}""", """{"value":42}""", "arguments");
            return Task.CompletedTask;
        });

        yield return Program.Case("piai.validation.accepts-null-for-nullable-array-schemas-with-items", () =>
        {
            Accepts(Plain("""{"type":["array","null"],"items":{"type":"string"}}"""), """{"value":null}""", """{"value":null}""", "arguments");
            return Task.CompletedTask;
        });

        yield return Program.Case("piai.validation.rejects-invalid-coercions-for-serialized-plain-json-schemas", () =>
        {
            (string Schema, string Input)[] failing =
            [
                ("""{"type":"boolean"}""", "\"yes\""),
                ("""{"type":"number"}""", "\"abc\""),
                ("""{"type":"null"}""", "\"nil\""),
                ("""{"type":"integer"}""", "\"4x\""),
            ];
            foreach (var (schema, input) in failing) Throws(Plain(schema), $$"""{"value":{{input}}}""", "Validation failed", $"{schema} {input}");
            return Task.CompletedTask;
        });

        yield return Program.Case("piai.validation.lists-pi-schema-s-errors-then-the-arguments-it-received", () =>
        {
            Throws(Plain("""{"type":"number"}"""), """{"value":"many"}""",
                "Validation failed for tool \"echo\":\n  - value: expected number, got string\n\nReceived arguments:\n{\n  \"value\": \"many\"\n}", "message");
            return Task.CompletedTask;
        });

        yield return Program.Case("piai.validation.reports-a-missing-required-property-at-the-property", () =>
        {
            Throws(Plain("""{"type":"string"}"""), "{}", "  - value: missing", "message");
            return Task.CompletedTask;
        });

        yield return Program.Case("piai.validation.returns-a-copy-of-the-arguments-also-when-nothing-is-coerced", () =>
        {
            var arguments = JsonValue.Parse("""{"value":"x"}""");
            var result = DurableValidation.Validate("echo", (JsonObjectValue)JsonValue.Parse(Plain("""{"type":"string"}""")), arguments);
            Program.Equal("""{"value":"x"}""", result.ArgumentsJson, "arguments");
            Program.Check(!ReferenceEquals(arguments, result.Value), "a copy");
            return Task.CompletedTask;
        });

        // #10897
        yield return Program.Case("piai.validation.reports-malformed-arguments-json-instead-of-validating-empty-arguments", () =>
        {
            var result = DurableValidation.Validate("echo", (JsonObjectValue)JsonValue.Parse("""{"type":"object","properties":{"value":{"type":"string"}}}"""),
                JsonObjectValue.Empty, argumentsError: "Unexpected token");
            Program.Equal("Invalid arguments for tool \"echo\": Unexpected token", result.ErrorMessage, "message");
            return Task.CompletedTask;
        });

        yield return Program.Case("piai.validation.matches-pisharp-agent-s-port", () =>
        {
            var count = 0;
            foreach (var (schema, origin, arguments) in Rows())
            {
                var copy = DurableValidation.ValidateJson("probe", schema, arguments, origin);
                var agent = AgentValidation.ValidateJson("probe", schema, arguments, origin);
                var what = $"{origin} {schema} {arguments}";
                Program.Equal(agent.ErrorMessage, copy.ErrorMessage, what + " error");
                Program.Equal(agent.ArgumentsJson, copy.ArgumentsJson, what + " result");
                count++;
            }
            Program.Equal(60, count, "rows");
            return Task.CompletedTask;
        });
    }
}
