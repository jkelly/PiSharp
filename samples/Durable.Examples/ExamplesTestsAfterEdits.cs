// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/examples/32-tests-after-edits.ts (added in 7e15f9601, #11141),
// ported in the Pi v1.5.0 sync (area run-end; 33-tests-after-edits until the closeout gave the examples v1.5.0's names).
// ScriptedModels stands in for the faux provider, and the edit tool declares pi-schema's obj({ path: str(), content: str() }) as JSON
// Schema. It prints what upstream's console.log prints (its arguments joined with spaces). The tests' first attempt waits until the host
// has queued its question (PiSharp-only): the run works on other threads here, so without it the question could be queued after the fix
// run's input, or the tests could end the run before the host reads pi.live, where Node's event loop fixes upstream's order. On a file
// backend the storage is reopened in a plain Session and the transcript's kinds read back (PiSharp-only, as every example's read-back).
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Examples;

public static partial class Examples
{
    // pi-schema's obj({ path: str(), content: str() }).
    private static readonly JsonObjectValue EditParameters = (JsonObjectValue)JsonValue.Parse(
        """{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"}},"required":["path","content"],"additionalProperties":false}""");

    // 32: run the project's tests at the end of every run that edited files, with an afterRun hook: the run waits for the tests, a failure
    // starts a fix run, and input sent meanwhile waits for them too.
    private static async Task TestsAfterEditsAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        // ─── Product code: the project, an edit tool, and the test runner ─────────────

        // The project under test, in memory: the tests pass once `add` adds.
        var files = new Dictionary<string, string> { ["math.ts"] = "export const add = (a, b) => a - b;" };
        (bool Passed, string Output) RunTests()
        {
            var passed = files["math.ts"].Contains("a + b", StringComparison.Ordinal);
            return (passed, passed ? "1 passed" : "add(1, 2): expected 3, got -1");
        }

        var edit = Define.Tool(new ToolRegistration
        {
            Name = "edit",
            Description = "Replace a file's content",
            Parameters = EditParameters,
            Execute = (arguments, _, _) =>
            {
                var path = ((JsonStringValue)((JsonObjectValue)arguments)["path"]).Value;
                files[path] = ((JsonStringValue)((JsonObjectValue)arguments)["content"]).Value;
                return Task.FromResult(new ToolExecutionResult
                {
                    Output = JsonValue.Array([JsonValue.Object([new("type", "text"), new("text", $"wrote {path}")])]),
                });
            },
        });

        // PiSharp-only: released once the host has queued its question (see the file header).
        var questionQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // A durable task: it runs once the run answered, and the run ends after it. A crash resumes it.
        var runTestsTask = Tasks.DefineTask(new TaskDefinition
        {
            Name = "app.run-tests",
            Version = 1,
            Initial = _ => JsonValue.Object([new("phase", "run")]),
            Phases = new Dictionary<string, TaskPhaseHandler>
            {
                ["run"] = async (task, runtime, taskContext) =>
                {
                    var attempt = (long)((JsonNumberValue)((JsonObjectValue)task.Input)["attempt"]).Value;
                    if (attempt == 1) await questionQueued.Task.WaitAsync(taskContext.CancellationToken);
                    var result = RunTests();
                    await LogAsync(output, $"  [tests, attempt {attempt}] {result.Output}");
                    // The handle is fetched before the commit: inside it, runtime reads would wait for the commit itself.
                    var conversation = (await runtime.ConversationAsync(runtime.ConversationId, taskContext))!;
                    await runtime.CommitAsync(async (tx, _) =>
                    {
                        // Admitted with this commit, before its own table writes: placed when the run ends.
                        if (!result.Passed && attempt < 3)
                            await conversation.SubmitAsync(tx,
                                SubmissionDraft.Input($"The tests failed: {result.Output}. Fix it.", WhenBusy.FollowUp, $"fix-{attempt}"));
                        await tx.AppendEntryAsync(runtime.ConversationId, new EntryDraft
                        {
                            Kind = "app.tests",
                            Data = JsonValue.Object([new("passed", result.Passed), new("output", result.Output)]),
                        });
                        return (TaskState?)NextTaskState.Completed(result.Passed);
                    }, taskContext);
                },
            },
            Abort = (_, runtime, taskContext) => runtime.CommitAsync((_, _) => NextTaskState.Aborted(), taskContext),
        });

        var attempts = 0;
        var testsAfterEdits = new Extension
        {
            Name = "tests-after-edits",
            Tools = [edit],
            Tasks = [runTestsTask],
            Hooks =
            [
                Define.Hook(HarnessTasks.GenerationTask, new GenerationHooks
                {
                    AfterRun = async (run, api, hookContext) =>
                    {
                        // The run's own entries, from its first input to its answer; a compaction never removes them.
                        var range = new EntryQueryBounds { MinEntryId = run.First, MaxEntryId = run.Last, Order = ScanOrder.Ascending };
                        var page = await api.EntriesAsync(range, 1000, null, hookContext);
                        var edited = page.Items.Any(entry => Entries.ToolResultEntry.Is(entry) && entry.Model is { Count: > 0 } model &&
                            model[0] is JsonObjectValue message && message["role"] is JsonStringValue { Value: "toolResult" } &&
                            message["toolName"] is JsonStringValue { Value: "edit" });
                        // Each fix run asks again with a fresh turn budget: the extension caps the loop itself.
                        return edited ? [Define.RunEndTask(runTestsTask, JsonValue.Object([new("attempt", ++attempts)]))] : null;
                    },
                }),
            ],
        };

        // ─── Host setup ─────────────────────────────────────────────────────────────

        static ScriptedReply Edit(string content) =>
            ScriptedReply.ToolCall("edit", JsonValue.Object([new("path", "math.ts"), new("content", content)]));
        var models = new ScriptedModels(
            // The first run edits, wrongly.
            Edit("export const add = (a, b) => a - b + 0;"),
            "I cleaned up add.",
            // The user's question, queued first, runs first; it edits nothing, so no tests run after it.
            "Not yet: add subtracts. The fix is queued next.",
            // The fix run the failed tests started.
            Edit("export const add = (a, b) => a + b;"),
            "Fixed: add adds now.");

        var registry = Registry.Create();
        registry.Install(testsAfterEdits);
        var harness = await Harness.OpenAsync(await open(), new HarnessOptions { Models = models, Registry = registry }, Context);
        var root = await harness.RootAsync(Context, new AgentChange { Model = Faux });

        var submission = await root.SubmitAsync(SubmissionDraft.Input("Clean up math.ts."), Context);
        // Resolves with the answer; the conversation stays busy while the tests run.
        await LogAsync(output, "answered:", StatusName((await submission.WaitAsync(Context)).Status));
        var live = await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context);
        await LogAsync(output, "busy with after-run tasks:",
            live?.TryGetValue("run", out var run) == true && run is JsonObjectValue held && held.ContainsKey("afterRun") ? "true" : "false");
        // Sent while the tests run: it waits for them. Follow-ups run in queue order, so it runs before the fix run.
        await root.SubmitAsync(SubmissionDraft.Input("Are the tests green now?"), Context);
        questionQueued.TrySetResult();

        await root.WaitForIdleAsync(Context);
        var entries = await root.EntriesAsync(new EntryQueryBounds { Order = ScanOrder.Ascending }, 100, null, Context);
        foreach (var entry in entries.Items)
        {
            var message = entry.Model is { Count: > 0 } model ? model[0] as JsonObjectValue : null;
            var role = message is not null && message.TryGetValue("role", out var value) && value is JsonStringValue name ? name.Value : null;
            if (entry.Kind == "app.tests") await LogAsync(output, "tests:", ((JsonStringValue)((JsonObjectValue)entry.Data!)["output"]).Value);
            else if (role == "user") await LogAsync(output, ">", message!["content"] is JsonStringValue content ? content.Value : Json(message["content"]));
            else if (role == "assistant")
            {
                var text = string.Concat(((JsonArrayValue)message!["content"]).OfType<JsonObjectValue>()
                    .Where(part => part["type"] is JsonStringValue { Value: "text" }).Select(part => ((JsonStringValue)part["text"]).Value));
                if (text != "") await LogAsync(output, "agent:", text);
            }
        }
        await harness.CloseAsync(Context);

        await ReopenAsync(open, output, async reopened =>
            await output.WriteLineAsync("transcript: " + Kinds(await TranscriptAsync(reopened, root.Id, 40))));
    }
}
