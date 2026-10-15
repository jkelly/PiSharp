// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/harness-structured-output.test.ts ("structured output"), as
// harness.structured-output.<case>. Verbatim apart from the adaptations in HarnessSupport.cs and ChatSupport.cs, and these:
// - Schemas are the JSON TypeBox prints: `Type.Object({ count: Type.Number() })` is Count, `Type.Null()` is {"type":"null"}, and
//   `Type.Object({})` is ToolRegistration.EmptyParameters.
// - A result's `toHaveProperty("structuredOutput")` is NestedToolExecutionResult.StructuredOutput being null (absent; a JSON null is
//   JsonValue.Null), and a stored message's is its JSON key; toEqual and toMatchObject compare the result's JSON (ToJson), whose durationMs
//   and taskId are matched as numbers.
// - `new NodeExecutionEnv({ cwd })` is LocalExecutionEnv (Git Bash on Windows).
// - Upstream runs these on MemoryStorage; so do they here.
using System.Collections.Immutable;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Tools;
using PiSharp.Durable.Tools.Env;
using static ChatTests;
using static HarnessTests;
using static Program;
using static SessionTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessStructuredOutputCases() => StructuredOutputTests.Cases("harness.structured-output.");
}

/// <summary>The harness.structured-output.* cases (upstream harness-structured-output.test.ts).</summary>
internal static class StructuredOutputTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    private static readonly JsonObjectValue Count = O("""{"type":"object","required":["count"],"properties":{"count":{"type":"number"}}}""");

    /// <summary>A tool that returns <paramref name="result"/>, with <paramref name="schema"/> as its structuredOutputSchema.</summary>
    private static ToolRegistration Returning(string name, ToolExecutionResult result, JsonObjectValue? schema = null,
        Func<ToolRegistration, ToolRegistration>? extra = null)
    {
        var tool = Define.Tool(new ToolRegistration
        {
            Name = name, Description = name, Parameters = ToolRegistration.EmptyParameters, StructuredOutputSchema = schema,
            Execute = (_, _, _) => Task.FromResult(result),
        });
        return extra is null ? tool : extra(tool);
    }

    private static JsonObjectValue Call(string name) => FauxAssistantMessage([FauxToolCall(name, JsonObjectValue.Empty, "c1")], stopReason: "toolUse");

    private static readonly JsonObjectValue Done = FauxAssistantMessage("done");

    private static JsonArrayValue Output(params JsonObjectValue[] items) => JsonValue.Array(items);

    private static ToolRegistration Probe(Func<IToolExecutionApi, DurableContext, Task> calls) => Define.Tool(new ToolRegistration
    {
        Name = "probe",
        Description = "probe",
        Parameters = ToolRegistration.EmptyParameters,
        Execute = async (_, api, callContext) =>
        {
            await calls(api, callContext);
            return new ToolExecutionResult();
        },
    });

    /// <summary>Runs <paramref name="name"/> as a nested call of a <c>probe</c> tool and returns what the probe received.</summary>
    private static async Task<NestedToolExecutionResult> Nested(ChatSetup setup, string name)
    {
        NestedToolExecutionResult? received = null;
        AddTool(setup.Registry, Probe(async (api, callContext) => received = await api.ExecuteToolAsync(name, JsonObjectValue.Empty, callContext)));
        setup.Faux.SetResponses(Call("probe"), Done);
        var (harness, root) = await OpenChat(new MemoryStorage(), setup);
        await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
        await harness.CloseAsync(Context);
        return received!;
    }

    /// <summary>Runs <paramref name="name"/> as a model-issued call and returns its transcript message.</summary>
    private static async Task<JsonObjectValue> Direct(ChatSetup setup, string name)
    {
        setup.Faux.SetResponses(Call(name), Done);
        var (harness, root) = await OpenChat(new MemoryStorage(), setup);
        await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
        var entry = (await AllEntries(root)).First(candidate => Entries.ToolResultEntry.Is(candidate));
        await harness.CloseAsync(Context);
        return (JsonObjectValue)entry.Model![0];
    }

    private static List<string?> Codes(NestedToolExecutionResult result) => [.. result.Diagnostics.Select(diagnostic => diagnostic.Code)];

    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix)
    {
        yield return Case(prefix + "gives-callers-a-schema-less-tool-s-output-one-text-as-a-string-one-image-as-itself-else-the-list", async () =>
        {
            var text = FauxText("one");
            var image = Obj(("type", "image"), ("data", "AAAA"), ("mimeType", "image/png"));
            (JsonArrayValue Output, JsonValue Value)[] cases =
            [
                (Output(text), "one"),
                (Output(image), image),
                (Output(), ""),
                (Output(text, image), Output(text, image)),
                (Output(text, text), Output(text, text)),
            ];
            foreach (var (output, value) in cases)
            {
                var setup = Setup();
                AddTool(setup.Registry, Returning("plain", new ToolExecutionResult { Output = output }));
                var received = await Nested(setup, "plain");
                MatchExactly(Obj(("taskId", "<number>"), ("structuredOutput", value), ("isError", false), ("diagnostics", JsonArrayValue.Empty),
                    ("durationMs", "<number>")).ToJson(), received.ToJson(), "received for " + output.ToJson());
            }
        });

        yield return Case(prefix + "gives-callers-the-bounded-output-as-the-model-sees-it-without-the-rendered-diagnostics", async () =>
        {
            var setup = Setup();
            AddTool(setup.Registry, Returning("long", new ToolExecutionResult
            {
                Output = Output(FauxText("a\nb\nc")),
                Diagnostics = [new ToolDiagnostic(ToolDiagnosticSeverity.Info, "note")],
            }, extra: tool => tool with { OutputLimits = new ToolOutputLimits { MaxLines = 1 } }));
            var received = await Nested(setup, "long");
            SameJson("\"a\\n\"", received.StructuredOutput, "structuredOutput");
            Seq([null, "truncated"], Codes(received), "codes");
        });

        yield return Case(prefix + "stores-exactly-what-callers-and-events-get-without-the-model-s-output-and-keeps-a-stored-null", async () =>
        {
            var setup = Setup();
            AddTool(setup.Registry, Returning("plain", new ToolExecutionResult { Output = Output(FauxText("a")) }));
            AddTool(setup.Registry, Returning("nothing", new ToolExecutionResult { StructuredOutput = JsonValue.Null }, O("""{"type":"null"}""")));
            var received = new SyncList<NestedToolExecutionResult>();
            AddTool(setup.Registry, Probe(async (api, callContext) =>
            {
                received.Add(await api.ExecuteToolAsync("plain", JsonObjectValue.Empty, callContext));
                received.Add(await api.ExecuteToolAsync("nothing", JsonObjectValue.Empty, callContext));
            }));
            setup.Faux.SetResponses(Call("probe"), Done);
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            // The result each nested call's document got, as committed.
            var stored = new SyncList<JsonValue>();
            harness.SubscribeCommits((publication, _) =>
            {
                foreach (var change in publication.Changes)
                {
                    if (change is not DocumentCommitChange { Record.Kind: "pi.tool.nested-call" } document) continue;
                    if (document.Value is { } value && value.TryGetValue("result", out var result) && document.Ops.Length > 0) stored.Add(result);
                }
            });
            var stream = await harness.WatchEventsAsync(root.Id, Context);
            var ends = new SyncList<NestedToolExecutionResult>();
            stream.Start((batch, _) =>
            {
                foreach (var item in batch)
                    if (item is ToolExecutionEndEvent { ParentToolCallId: not null } end) ends.Add(end.Result!);
                return Task.CompletedTask;
            });
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            await harness.WaitForIdleAsync(Context);
            await stream.StopAsync();
            await harness.CloseAsync(Context);

            var results = received.Snapshot();
            MatchJson("""{"structuredOutput":"a","isError":false}""", results[0].ToJson(), "plain");
            MatchJson("""{"structuredOutput":null,"isError":false}""", results[1].ToJson(), "nothing");
            Check(!results[0].ToJson().ContainsKey("output"), "no output");
            Equal(JsonValue.Array(results.Select(result => (JsonValue)result.ToJson())).ToJson(), JsonValue.Array(stored.Snapshot()).ToJson(), "stored");
            Equal(JsonValue.Array(results.Select(result => (JsonValue)result.ToJson())).ToJson(),
                JsonValue.Array(ends.Snapshot().Select(result => (JsonValue)result.ToJson())).ToJson(), "ends");
        });

        yield return Case(prefix + "gives-callers-a-schema-tool-s-validated-structured-output-which-the-transcript-does-not-store", async () =>
        {
            var setup = Setup();
            var result = new ToolExecutionResult { Output = Output(FauxText("3 things")), StructuredOutput = Obj(("count", 3)) };
            AddTool(setup.Registry, Returning("counter", result, Count));
            MatchJson("""{"structuredOutput":{"count":3},"isError":false}""", (await Nested(setup, "counter")).ToJson(), "nested");

            var modelSetup = Setup();
            AddTool(modelSetup.Registry, Returning("counter", result, Count));
            var stored = await Direct(modelSetup, "counter");
            MatchJson("""{"isError":false,"content":[{"type":"text","text":"3 things"}]}""", stored, "stored");
            Check(!stored.ContainsKey("structuredOutput"), "no structuredOutput in the transcript");
        });

        yield return Case(prefix + "turns-structured-output-that-breaks-the-schema-into-an-error-result-for-callers-and-a-report-for-the-model-s-calls", async () =>
        {
            (string Name, ToolExecutionResult Result, bool Schema, string Message)[] cases =
            [
                ("wrong", new ToolExecutionResult { StructuredOutput = Obj(("count", "three")) }, true, "does not match its schema: count:"),
                ("missing", new ToolExecutionResult { Output = JsonArrayValue.Empty }, true, "returned no structuredOutput"),
                ("undeclared", new ToolExecutionResult { StructuredOutput = Obj(("count", 3)) }, false, "declares no structuredOutputSchema"),
            ];
            foreach (var (name, result, schema, message) in cases)
            {
                var setup = Setup();
                AddTool(setup.Registry, Returning(name, result, schema ? Count : null));
                var received = await Nested(setup, name);
                Equal(true, received.IsError, name + " isError");
                // The broken value is dropped; a schema-less tool's error result still carries its output, here none.
                if (schema) Equal<JsonValue?>(null, received.StructuredOutput, name + " structuredOutput");
                else SameJson("\"\"", received.StructuredOutput, name + " structuredOutput");
                Seq(["invalid_structured_output"], Codes(received), name + " codes");
                Check(received.Diagnostics[0].Message.Contains(message, StringComparison.Ordinal), name + ": " + received.Diagnostics[0].Message);

                var modelSetup = Setup();
                AddTool(modelSetup.Registry, Returning(name, result, schema ? Count : null));
                // The model never sees structured output: its call stands, and the host gets a report.
                var stored = await Direct(modelSetup, name);
                Equal(false, stored["isError"] is JsonBooleanValue { Value: true }, name + " stored isError");
                Check(!stored["content"].ToJson().Contains(message, StringComparison.Ordinal), name + " content");
                Check(string.Join("\n", modelSetup.Reports.Snapshot().Select(error => error.Message)).Contains(message, StringComparison.Ordinal),
                    name + " reports: " + string.Join("\n", modelSetup.Reports.Snapshot().Select(error => error.Message)));
            }
        });

        yield return Case(prefix + "gives-callers-the-output-of-an-error-result-of-a-tool-without-a-schema", async () =>
        {
            var setup = Setup();
            AddTool(setup.Registry, Returning("bad", new ToolExecutionResult { Output = Output(FauxText("bad")), IsError = true }));
            MatchJson("""{"isError":true,"structuredOutput":"bad"}""", (await Nested(setup, "bad")).ToJson(), "received");
        });

        yield return Case(prefix + "gives-callers-no-structured-output-for-an-error-result-the-harness-wrote-only-diagnostics", async () =>
        {
            var setup = Setup();
            var received = await Nested(setup, "missing");
            MatchJson("""{"isError":true,"diagnostics":[{"code":"tool_unavailable"}]}""", received.ToJson(), "received");
            Equal<JsonValue?>(null, received.StructuredOutput, "structuredOutput");
        });

        yield return Case(prefix + "gives-callers-a-schema-tool-s-error-text-as-a-diagnostic-never-as-structured-output", async () =>
        {
            var setup = Setup();
            AddTool(setup.Registry, Returning("fails", new ToolExecutionResult { Output = Output(FauxText("nope")), IsError = true }, Count));
            // Its text reaches the caller as an error diagnostic: structuredOutput is only ever the tool's own value.
            var received = await Nested(setup, "fails");
            MatchJson("""{"isError":true,"diagnostics":[{"severity":"error","code":"tool_error","message":"nope"}]}""", received.ToJson(), "received");
            Equal<JsonValue?>(null, received.StructuredOutput, "structuredOutput");

            // The model's call keeps the text in its output, without the extra diagnostic.
            var modelSetup = Setup();
            AddTool(modelSetup.Registry, Returning("fails", new ToolExecutionResult { Output = Output(FauxText("nope")), IsError = true }, Count));
            var stored = await Direct(modelSetup, "fails");
            SameJson("""[{"type":"text","text":"nope"}]""", stored["content"], "stored content");

            // A thrown error's message is in its tool_error diagnostic, not in the output.
            var thrown = Setup();
            AddTool(thrown.Registry, Define.Tool(new ToolRegistration
            {
                Name = "throws",
                Description = "throws",
                Parameters = ToolRegistration.EmptyParameters,
                StructuredOutputSchema = Count,
                Execute = (_, _, _) => throw new InvalidOperationException("permission denied"),
            }));
            var thrownResult = await Nested(thrown, "throws");
            // One diagnostic: the thrown message, not a second one from its (empty) output.
            MatchJson("""{"isError":true,"diagnostics":[{"code":"tool_error","message":"permission denied"}]}""", thrownResult.ToJson(), "thrown");
            Equal(1, thrownResult.Diagnostics.Length, "thrown diagnostics");
            Equal<JsonValue?>(null, thrownResult.StructuredOutput, "thrown structuredOutput");

            // An error result whose own diagnostics say what went wrong gets no second one from its output.
            var explained = Setup();
            AddTool(explained.Registry, Returning("fails", new ToolExecutionResult
            {
                Output = Output(FauxText("partial log")),
                IsError = true,
                Diagnostics = [new ToolDiagnostic(ToolDiagnosticSeverity.Error, "quota exceeded", "quota")],
            }, Count));
            SameJson("""[{"severity":"error","code":"quota","message":"quota exceeded"}]""",
                (await Nested(explained, "fails")).ToJson()["diagnostics"], "explained diagnostics");

            // An error result that has structured output keeps it.
            var kept = Setup();
            AddTool(kept.Registry, Returning("fails", new ToolExecutionResult
            {
                Output = Output(FauxText("nope")),
                StructuredOutput = Obj(("count", 0)),
                IsError = true,
            }, Count));
            MatchJson("""{"isError":true,"structuredOutput":{"count":0}}""", (await Nested(kept, "fails")).ToJson(), "kept");
        });

        yield return Case(prefix + "keeps-structured-output-when-aftertool-replaces-the-output-and-validates-one-aftertool-replaces", async () =>
        {
            var setup = Setup();
            AddTool(setup.Registry, Returning("plain", new ToolExecutionResult { Output = Output(FauxText("secret")) }));
            AddTool(setup.Registry, Returning("counter", new ToolExecutionResult { StructuredOutput = Obj(("count", 1)) }, Count));
            AddTool(setup.Registry, Returning("other", new ToolExecutionResult { StructuredOutput = Obj(("count", 1)) }, Count));
            AddHooks(setup.Registry, HarnessTasks.ToolTask, new ToolHooks
            {
                AfterTool = (hookCall, result, _, _) => Task.FromResult<ToolExecutionResult?>(S(hookCall["name"]) switch
                {
                    "other" => result with { StructuredOutput = Obj(("count", "broken")) },
                    "probe" => result,
                    _ => result with { Output = Output(FauxText("rewritten")) },
                }),
            });
            NestedToolExecutionResult? plain = null, counter = null, other = null;
            AddTool(setup.Registry, Probe(async (api, callContext) =>
            {
                plain = await api.ExecuteToolAsync("plain", JsonObjectValue.Empty, callContext);
                counter = await api.ExecuteToolAsync("counter", JsonObjectValue.Empty, callContext);
                other = await api.ExecuteToolAsync("other", JsonObjectValue.Empty, callContext);
            }));
            setup.Faux.SetResponses(Call("probe"), Done);
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            await harness.CloseAsync(Context);
            // A schema-less tool's value is its output, so redacting the output redacts what programs get.
            SameJson("\"rewritten\"", plain!.StructuredOutput, "plain");
            MatchJson("""{"structuredOutput":{"count":1}}""", counter!.ToJson(), "counter");
            Equal(true, other!.IsError, "other isError");
            Seq(["invalid_structured_output"], Codes(other), "other codes");
        });

        yield return Case(prefix + "gives-callers-of-bash-its-retained-output-and-exit-code-also-for-a-nonzero-exit", async () =>
        {
            var directory = Temp("structured-output");
            try
            {
                var setup = Setup();
                AddTool(setup.Registry, CodingTools.CreateBashTool());
                var received = new SyncList<NestedToolExecutionResult>();
                AddTool(setup.Registry, Probe(async (api, callContext) =>
                {
                    received.Add(await api.ExecuteToolAsync("bash", Obj(("command", "printf ok")), callContext));
                    received.Add(await api.ExecuteToolAsync("bash", Obj(("command", "printf bad; exit 4")), callContext));
                }));
                setup.Faux.SetResponses(Call("probe"), Done);
                var env = new LocalExecutionEnv(directory);
                var (harness, root) = await OpenChat(new MemoryStorage(), setup, (_, _) => Task.FromResult<IExecutionEnv?>(env));
                await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
                await harness.CloseAsync(Context);
                var results = received.Snapshot();
                MatchJson("""{"isError":false,"structuredOutput":{"output":"ok","truncated":false,"exitCode":0}}""", results[0].ToJson(), "ok");
                MatchJson("""{"isError":true,"structuredOutput":{"output":"bad","truncated":false,"exitCode":4}}""", results[1].ToJson(), "bad");
                Seq(["exit_code"], Codes(results[1]), "codes");
            }
            finally
            {
                try { Directory.Delete(directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        });
    }
}
