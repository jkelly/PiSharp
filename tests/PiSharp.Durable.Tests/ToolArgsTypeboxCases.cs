// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/tool-args-typebox.test.ts ("tool arguments of a TypeBox schema"),
// as tool-args-typebox.<case>, ported in the Pi v1.5.0 sync (area tools-schema). Upstream's case is a type-level check: pi-schema's Static types
// the arguments of a TypeBox-built schema (TypeBox 1.x's shape, with its "~kind" and "~optional" keys) as { path: string; offset?: number;
// tags: string[] } in execute() and prepareArguments(). C# tools take JSON values, so there is no static type to check (expectTypeOf has no C#
// counterpart); the case checks the same at run time instead: the TypeBox-shaped schema is an ordinary JSON Schema to pi-schema (the "~" keys
// are unknown keywords), the value prepareArguments returns is validated against it, and execute receives values of that type only: a string
// path, a number offset (coerced from "3"), an array of string tags. A call without tags never reaches execute.
using System.Collections.Concurrent;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Storage;
using static ChatTests;
using static HarnessTests;
using static Program;
using static SessionTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> ToolArgsTypeboxCases() => ToolArgsTypeboxTests.Cases("tool-args-typebox.");
}

/// <summary>The tool-args-typebox.* cases (upstream tool-args-typebox.test.ts).</summary>
internal static class ToolArgsTypeboxTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    /// <summary>Upstream's <c>parameters</c>: TObject&lt;{ path: TString; offset: TOptional&lt;TInteger&gt;; tags: TArray&lt;TString&gt; }&gt;.</summary>
    private static readonly JsonObjectValue Parameters = O("""
        {"~kind":"Object","type":"object","properties":{"path":{"~kind":"String","type":"string"},"offset":{"~kind":"Integer","type":"integer","~optional":true},"tags":{"~kind":"Array","type":"array","items":{"~kind":"String","type":"string"}}},"required":["path","tags"]}
        """);

    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix)
    {
        yield return Case(prefix + "are-typed-by-the-schema-in-execute-and-prepare-arguments", async () =>
        {
            var executed = new ConcurrentQueue<string>();
            var tool = Define.Tool(new ToolRegistration
            {
                Name = "read",
                Description = "Reads a file",
                Parameters = Parameters,
                // A comma-separated string where the array belongs, which the model commonly sends.
                PrepareArguments = args => args is JsonObjectValue fields && fields.TryGetValue("tags", out var tags) && tags is JsonStringValue list
                    ? fields.SetItem("tags", JsonValue.Array(list.Value.Split(',').Select(tag => (JsonValue)tag)))
                    : args,
                Execute = (args, _, _) =>
                {
                    executed.Enqueue(args.ToJson());
                    return Task.FromResult(new ToolExecutionResult());
                },
            });
            var setup = Setup();
            setup.ToolExecution = ToolExecutionMode.Sequential;
            AddTool(setup.Registry, tool);
            setup.Faux.SetResponses(
                FauxAssistantMessage([FauxToolCall("read", O("""{"path":"a.txt","offset":"3","tags":"x,y"}"""), "typed"),
                    FauxToolCall("read", O("""{"path":"b.txt"}"""), "untyped")], stopReason: "toolUse"),
                FauxAssistantMessage("done"));
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            Seq(["""{"path":"a.txt","offset":3,"tags":["x","y"]}"""], executed, "execute's arguments");
            var rejected = (await AllEntries(root)).Where(entry => entry.Kind == "pi.tool-result").Select(entry => (JsonObjectValue)entry.Model![0])
                .Single(message => S(message["toolCallId"]) == "untyped");
            Check(rejected["isError"] is JsonBooleanValue { Value: true }, "a call without tags is an error");
            var text = string.Concat(((JsonArrayValue)rejected["content"]).Cast<JsonObjectValue>().Select(item => S(item["text"])));
            Check(text.Contains("Validation failed for tool \"read\":\n  - tags: missing", StringComparison.Ordinal), "its message: " + text);
            await harness.CloseAsync(Context);
        });
    }
}
