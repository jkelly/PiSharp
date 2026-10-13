// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): differential cases for PiSharp.Durable's internal copy of packages/ai/src/utils/validation.ts
// (Internal/PiAi/Validation.cs, copied from PiSharp.Agent's ToolArgumentValidation because the core does not reference PiSharp.Agent). There
// is no upstream test file: Fixtures/piai-validation.upstream.json holds what the pinned pi-ai's validateToolArguments returns or throws under
// Node for TypeBox-built schemas and their plain JSON copies (printed by Fixtures/capture-piai-validation.mjs). The first case compares the
// copy with it; the second compares the copy with PiSharp.Agent's port on the same inputs (the test project reaches PiSharp.Agent through
// PiSharp.Durable.Tools).
using PiSharp.Contracts;
using PiSharp.Durable.Chord;
using static SessionTests;
using DurableValidation = PiSharp.Durable.Internal.PiAi.Validation.ToolArgumentValidation;
using AgentValidation = PiSharp.Agent.ToolArgumentValidation;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> PiAiValidationCases() => PiAiValidationTests.Cases("piai.validation.");
}

/// <summary>The piai.validation.* differential cases.</summary>
internal static class PiAiValidationTests
{
    private static readonly Lazy<JsonArrayValue> Upstream = new(() => (JsonArrayValue)JsonValue.Parse(
        File.ReadAllText(Path.Combine(Program.RepositoryRoot(), "tests", "PiSharp.Durable.Tests", "Fixtures", "piai-validation.upstream.json"))));

    private static IEnumerable<(string Schema, ToolSchemaOrigin Origin, string Arguments, JsonObjectValue Row)> Rows() =>
        Upstream.Value.Cast<JsonObjectValue>().Select(row => (row["schema"].ToJson(),
            S(row["origin"]) == "typebox" ? ToolSchemaOrigin.TypeBox : ToolSchemaOrigin.JsonSchema, row["arguments"].ToJson(), row));

    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix)
    {
        yield return Program.Case(prefix + "matches-the-pinned-pi-ai", () =>
        {
            var count = 0;
            foreach (var (schema, origin, arguments, row) in Rows())
            {
                var result = DurableValidation.ValidateJson("probe", schema, arguments, origin);
                var what = $"{origin} {schema} {arguments}";
                if (row.TryGetValue("error", out var error)) Program.Equal(S(error), result.ErrorMessage, what + " error");
                else
                {
                    Program.Check(result.IsValid, $"{what}: unexpected error {result.ErrorMessage}");
                    Program.Equal(S(row["result"]), result.ArgumentsJson, what + " result");
                }
                count++;
            }
            Program.Equal(60, count, "rows");
        });

        yield return Program.Case(prefix + "matches-pisharp-agent-s-port", () =>
        {
            foreach (var (schema, origin, arguments, _) in Rows())
            {
                var copy = DurableValidation.ValidateJson("probe", schema, arguments, origin);
                var agent = AgentValidation.ValidateJson("probe", schema, arguments, origin);
                var what = $"{origin} {schema} {arguments}";
                Program.Equal(agent.ErrorMessage, copy.ErrorMessage, what + " error");
                Program.Equal(agent.ArgumentsJson, copy.ArgumentsJson, what + " result");
            }
        });
    }
}
