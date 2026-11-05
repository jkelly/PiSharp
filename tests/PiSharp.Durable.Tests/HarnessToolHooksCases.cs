// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/harness-tool-hooks.test.ts (87eff524f, 1c6928b30), ported in the
// Pi v1.5.0 sync (area tool-tasks). afterTool runs on every result a tool task writes, also those the Harness decides without executing the
// tool (#11043), and a call to a tool its request did not offer runs as a tool task flagged unoffered. Verbatim apart from the adaptations in
// HarnessSupport.cs, ChatSupport.cs and SessionSupport.cs, and these:
// - openNodeSqliteStorage is SqliteStorage.OpenAsync over a file in a fresh temporary directory, and the MemoryStorage cases open a
//   MemoryStorage. The store pass (StoreStorageNode.cs) reruns the cases as harness.tool-hooks.store.*, opening every file as a StoreStorage
//   and the MemoryStorage cases on a fresh StoreStorage, as area sleep's store pass does.
// - open's kill() is KillableStorage (HarnessRestartFailureCases.cs): once killed, every call of the storage hangs. The killed Harness is
//   never closed; its storage is closed after the case, as upstream's afterEach does.
// - Parameters is the JSON pi-schema builds for `obj({ text: opt(str()) })` (ToolsTests.EchoParameters), and `obj({ ok: bool() })` is Ok.
// - A ToolHookCall is the call's wire JSON; Seen keeps it with the result the hook got. waitFor is Eventually; `aborted(signal)` is
//   Aborted(token); commitWrites is PiSharp.Durable.Testing.CommitWrites.CommitWritesAsync; a hook that never returns awaits a task that
//   never completes.
// - `toEqual(undefined)` on a result's structuredOutput or details is the C# property being null (upstream's undefined), and a sorted
//   list of [id, codes] pairs is ordered by call ID.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Testing;
using static ChatTests;
using static HarnessTests;
using static Program;
using static SessionTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessToolHooksCases() =>
        ToolHooksTests.Cases("harness.tool-hooks.", () => new MemoryStorage(), async path => await SqliteStorage.OpenAsync(path));
}

/// <summary>The harness.tool-hooks.* cases (upstream harness-tool-hooks.test.ts).</summary>
internal static class ToolHooksTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    private static string SqlitePath() => Path.Combine(Temp("pi-durable-tool-hooks"), "session.sqlite");

    private sealed record Opened(IHarness Harness, IConversation Root, Action Kill);

    /// <summary>Opens a chat over the file at <paramref name="path"/>, resumed; <c>Kill</c> simulates a crash: every later storage call hangs
    /// (upstream <c>open</c>).</summary>
    private static async Task<Opened> Open(Func<string, Task<IStorage>> openFile, KilledStorages killed, string path, ChatSetup setup)
    {
        var (storage, kill) = killed.Killable(await openFile(path));
        var opened = await OpenChat(storage, setup);
        opened.Harness.Resume();
        return new Opened(opened.Harness, opened.Root, kill);
    }

    /// <summary>Lets deferred progress commits (persistWithinMs 0) reach Storage before a crash (upstream <c>stored</c>).</summary>
    private static Task Stored() => Task.Delay(50);

    /// <summary><c>obj({ ok: bool() })</c>.</summary>
    private static readonly JsonObjectValue Ok = O("""{"type":"object","properties":{"ok":{"type":"boolean"}},"required":["ok"],"additionalProperties":false}""");

    private static ToolRegistration Tool(string name, ToolExecute execute, Func<ToolRegistration, ToolRegistration>? extra = null)
    {
        var tool = Define.Tool(new ToolRegistration { Name = name, Description = name, Parameters = ToolsTests.EchoParameters, Execute = execute });
        return extra is null ? tool : extra(tool);
    }

    private static Task<ToolExecutionResult> Result(ToolExecutionResult result) => Task.FromResult(result);

    private static readonly ToolExecutionResult Empty = new() { Output = JsonArrayValue.Empty };

    private static JsonArrayValue TextContent(string text) => JsonValue.Array(FauxText(text));

    private static JsonObjectValue Calls(params (string Name, JsonObjectValue Args, string Id)[] list) => ToolsTests.Calls(list);

    private static readonly JsonObjectValue Done = FauxAssistantMessage("done");

    /// <summary>The tool result messages by call ID (upstream <c>results</c>).</summary>
    private static Dictionary<string, JsonObjectValue> Results(IEnumerable<EntryRecord> entries) =>
        ToolsTests.Results(entries).ToDictionary(message => S(message["toolCallId"]));

    /// <summary>The message's text items joined by <c>|</c>, other items as nothing (upstream <c>text</c>).</summary>
    private static string Text(JsonObjectValue? message) => message is null || !message.TryGetValue("content", out var content)
        ? ""
        : Text((JsonArrayValue)content);

    private static string Text(JsonArrayValue content) =>
        string.Join("|", content.OfType<JsonObjectValue>().Select(item => S(item["type"]) == "text" ? S(item["text"]) : ""));

    private static List<string?> Codes(ToolExecutionResult result) => [.. (result.Diagnostics ?? []).Select(diagnostic => diagnostic.Code)];

    private static List<string?> Codes(NestedToolExecutionResult result) => [.. result.Diagnostics.Select(diagnostic => diagnostic.Code)];

    /// <summary>The diagnostic codes of the result entry of <paramref name="callId"/> (upstream <c>diagnosticsOf</c>).</summary>
    private static List<string?> DiagnosticsOf(IEnumerable<EntryRecord> entries, string callId)
    {
        var entry = entries.FirstOrDefault(each => each.Kind == "pi.tool-result" && S(((JsonObjectValue)each.Model![0])["toolCallId"]) == callId);
        return entry?.Data is JsonObjectValue data && data.TryGetValue("diagnostics", out var list) && list is JsonArrayValue diagnostics
            ? [.. diagnostics.OfType<JsonObjectValue>().Select(diagnostic => diagnostic.TryGetValue("code", out var code) ? S(code) : null)]
            : [];
    }

    private static string Show(IEnumerable<string?> codes) => string.Join(",", codes.Select(code => code ?? "undefined"));

    private sealed record Seen(JsonObjectValue Call, ToolExecutionResult Result);

    private static string Id(JsonObjectValue call) => S(call["id"]);

    private static bool HasParent(JsonObjectValue call) => call.ContainsKey("parent");

    private static async Task<JsonObjectValue> Live(IHarness harness) =>
        await harness.SnapshotAsync(PiSharp.Durable.Live.LiveDoc, ConversationId.Root, Context) ?? JsonObjectValue.Empty;

    /// <summary>The task of the <paramref name="index"/>th tool slot, once it has one (upstream <c>toolTaskId</c>).</summary>
    private static async Task<TaskId> ToolTaskId(IHarness harness, int index = 0)
    {
        TaskId? id = null;
        await Eventually(async () =>
        {
            var live = await Live(harness);
            if (live.TryGetValue("tools", out var tools) && tools is JsonArrayValue slots && slots.Count > index &&
                ((JsonObjectValue)slots[index]).TryGetValue("taskId", out var taskId) && taskId is JsonNumberValue number)
                id = new TaskId((long)number.Value);
            return id is not null;
        });
        return id!.Value;
    }

    private static async Task<SubmissionRecord> Settled(IHarness harness, SubmissionId id) =>
        await (await harness.SubmissionAsync(id, Context))!.WaitAsync(Context);

    /// <summary>A hook that never returns: the host dies inside it.</summary>
    private static Task<ToolExecutionResult?> Never() => new TaskCompletionSource<ToolExecutionResult?>().Task;

    /// <summary>A tool that writes output, then blocks until its invocation is cancelled (upstream <c>blocking</c>).</summary>
    private sealed class Blocking
    {
        private int runs;

        public Blocking(string name, Func<ToolRegistration, ToolRegistration>? extra = null)
        {
            Registration = Tool(name, async (_, api, callContext) =>
            {
                var run = Interlocked.Increment(ref runs);
                api.Output($"{name} {new string('o', 40)}\n");
                await api.DetailsAsync(Obj(("run", run)), callContext);
                Started.Resolve();
                await Aborted(callContext.CancellationToken);
                return new ToolExecutionResult();
            }, extra);
        }

        public ToolRegistration Registration { get; }
        public HarnessTests.Deferred Started { get; } = new();
        public int Runs => Volatile.Read(ref runs);
    }

    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix, Func<IStorage> fresh, Func<string, Task<IStorage>> openFile)
    {
        // ─── afterTool on results the call phase decides ────────────────────

        var decided = prefix + "aftertool-on-results-the-call-phase-decides.";

        // #11043
        yield return Case(decided + "sees-and-replaces-unavailable-invalid-and-blocked-results-with-the-raw-arguments", async () =>
        {
            var setup = Setup();
            var ran = 0;
            var echo = Tool("echo", (_, _, _) =>
            {
                Interlocked.Increment(ref ran);
                return Result(new ToolExecutionResult { Output = TextContent("ran") });
            });
            AddTool(setup.Registry, echo);
            AddTool(setup.Registry, Tool("gone", (_, _, _) => Result(Empty)));
            var seen = new SyncList<Seen>();
            AddHooks(setup.Registry, HarnessTasks.ToolTask, new ToolHooks
            {
                BeforeTool = (call, _, _) => Task.FromResult(Id(call) switch
                {
                    "block" => new BeforeToolDecision(Block: "not today"),
                    "rewrite" => new BeforeToolDecision(O("""{"text":{"not":"a string"}}""")),
                    _ => (BeforeToolDecision?)null,
                }),
                AfterTool = (call, result, _, _) =>
                {
                    seen.Add(new Seen(call, result));
                    return Task.FromResult<ToolExecutionResult?>(result with { Output = TextContent($"hooked {Id(call)}"), Details = Obj(("by", "hook")) });
                },
            });
            IConversation? root = null;
            setup.Faux.SetResponses(
                new FauxResponseFactory(async _ =>
                {
                    // The tool is offered but no longer resolves when its task runs.
                    await root!.ConfigureAsync(new AgentChange { Tools = ToolSelection.Only(echo) }, Context);
                    return Calls(
                        ("gone", Obj(), "unavailable"),
                        ("echo", O("""{"text":{"not":"a string"}}"""), "invalid"),
                        ("echo", Obj(("text", "x")), "rewrite"),
                        ("echo", Obj(), "block"));
                }),
                Done);
            var opened = await OpenChat(fresh(), setup);
            root = opened.Root;
            var settled = await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            Equal(SubmissionStatus.Done, settled.Status, "status");
            Equal(0, Volatile.Read(ref ran), "ran");
            var byId = seen.Snapshot().ToDictionary(each => Id(each.Call));
            Seq(["block", "invalid", "rewrite", "unavailable"], byId.Keys.Order(StringComparer.Ordinal), "seen");
            Equal("tool_unavailable", Show(Codes(byId["unavailable"].Result)), "unavailable codes");
            Equal("invalid_arguments", Show(Codes(byId["invalid"].Result)), "invalid codes");
            Equal("invalid_arguments", Show(Codes(byId["rewrite"].Result)), "rewrite codes");
            Equal("blocked", Show(Codes(byId["block"].Result)), "block codes");
            // Hooks see the call as the model made it, also when it is invalid or `beforeTool` rewrote it.
            SameJson("""{"text":{"not":"a string"}}""", byId["invalid"].Call["arguments"], "invalid arguments");
            SameJson("""{"text":"x"}""", byId["rewrite"].Call["arguments"], "rewrite arguments");
            var entries = await AllEntries(root);
            var messages = Results(entries);
            foreach (var id in new[] { "unavailable", "invalid", "rewrite", "block" })
            {
                var message = messages[id];
                MatchJson("""{"isError":true,"details":{"by":"hook"}}""", message, id);
                Check(Text(message).StartsWith($"hooked {id}|<harness>", StringComparison.Ordinal), $"{id} text: {Text(message)}");
            }
            await opened.Harness.CloseAsync(Context);
        });

        // #11043
        yield return Case(decided + "bounds-a-replacement-output-with-the-tool-s-limits-or-the-defaults-for-an-unresolved-tool", async () =>
        {
            var setup = Setup();
            AddTool(setup.Registry, Tool("small", (_, _, _) => Result(Empty), tool => tool with { OutputLimits = new ToolOutputLimits { MaxBytes = 10 } }));
            AddHooks(setup.Registry, HarnessTasks.ToolTask, new ToolHooks
            {
                BeforeTool = (_, _, _) => Task.FromResult<BeforeToolDecision?>(new BeforeToolDecision(Block: "no")),
                AfterTool = (call, result, _, _) => Task.FromResult<ToolExecutionResult?>(result with
                {
                    Output = TextContent(Id(call) == "c2" ? new string('x', 100) : new string('y', 60 * 1024)),
                }),
            });
            IConversation? root = null;
            setup.Faux.SetResponses(
                new FauxResponseFactory(async _ =>
                {
                    await root!.ConfigureAsync(new AgentChange { Tools = ToolSelection.Only() }, Context);
                    return Calls(("small", Obj(), "c1"));
                }),
                Done);
            var opened = await OpenChat(fresh(), setup);
            root = opened.Root;
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            await root.ConfigureAsync(new AgentChange { Tools = Patch<ToolSelection>.Clear }, Context);
            setup.Faux.SetResponses(Calls(("small", Obj(), "c2")), Done);
            await (await root.SubmitAsync(SubmissionDraft.Input("again"), Context)).WaitAsync(Context);
            var entries = await AllEntries(root);
            var messages = Results(entries);
            // Unresolved: the default 50 KB.
            var unresolved = (JsonObjectValue)((JsonArrayValue)messages["c1"]["content"])[0];
            Equal("text", S(unresolved["type"]), "unresolved type");
            Equal(50 * 1024, S(unresolved["text"]).Length, "unresolved length");
            Equal("tool_unavailable,truncated", Show(DiagnosticsOf(entries, "c1")), "c1 diagnostics");
            var bounded = (JsonObjectValue)((JsonArrayValue)messages["c2"]["content"])[0];
            Equal(new string('x', 10), S(bounded["text"]), "bounded");
            Equal("blocked,truncated", Show(DiagnosticsOf(entries, "c2")), "c2 diagnostics");
            await opened.Harness.CloseAsync(Context);
        });

        // #11043
        yield return Case(decided + "lets-a-hook-s-terminate-on-a-blocked-call-count-toward-the-round", async () =>
        {
            var setup = Setup();
            AddTool(setup.Registry, Tool("echo", (_, _, _) => Result(Empty with { Control = new ToolControl { Terminate = true } })));
            AddHooks(setup.Registry, HarnessTasks.ToolTask, new ToolHooks
            {
                BeforeTool = (call, _, _) => Task.FromResult<BeforeToolDecision?>(Id(call) == "c2" ? new BeforeToolDecision(Block: "no") : null),
                AfterTool = (_, result, _, _) => Task.FromResult<ToolExecutionResult?>(result with { Control = new ToolControl { Terminate = true } }),
            });
            setup.Faux.SetResponses(Calls(("echo", Obj(), "c1"), ("echo", Obj(), "c2")), Done);
            var (harness, root) = await OpenChat(fresh(), setup);
            var settled = await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            Equal(SubmissionStatus.Done, settled.Status, "status");
            // The round terminated: no second request.
            Equal(1, setup.Faux.State.CallCount, "calls");
            await harness.CloseAsync(Context);
        });

        // #11043
        yield return Case(decided + "runs-on-nested-invalid-and-blocked-calls-the-caller-gets-diagnostics-and-details-not-output", async () =>
        {
            var setup = Setup();
            AddTool(setup.Registry, Tool("echo", (_, _, _) => Result(Empty)));
            var nested = new SyncList<NestedToolExecutionResult>();
            AddTool(setup.Registry, Tool("batch", async (_, api, callContext) =>
            {
                nested.Add(await api.ExecuteToolAsync("echo", O("""{"text":{"not":"a string"}}"""), callContext));
                nested.Add(await api.ExecuteToolAsync("echo", Obj(("text", "secret")), callContext));
                nested.Add(await api.ExecuteToolAsync("missing", Obj(), callContext));
                return new ToolExecutionResult();
            }));
            var seen = new SyncList<JsonObjectValue>();
            AddHooks(setup.Registry, HarnessTasks.ToolTask, new ToolHooks
            {
                BeforeTool = (call, _, _) => Task.FromResult<BeforeToolDecision?>(
                    ((JsonObjectValue)call["arguments"]).TryGetValue("text", out var text) && text is JsonStringValue { Value: "secret" }
                        ? new BeforeToolDecision(Block: "no secrets")
                        : null),
                AfterTool = (call, result, _, _) =>
                {
                    if (!HasParent(call)) return Task.FromResult<ToolExecutionResult?>(null);
                    seen.Add(call);
                    return Task.FromResult<ToolExecutionResult?>(result with
                    {
                        Output = TextContent("dropped"),
                        StructuredOutput = Obj(("dropped", true)),
                        Details = Obj(("hooked", Id(call))),
                        Diagnostics = [.. result.Diagnostics ?? [], new ToolDiagnostic(ToolDiagnosticSeverity.Info, "seen", "hooked")],
                    });
                },
            });
            setup.Faux.SetResponses(Calls(("batch", Obj(), "c1")), Done);
            var (harness, root) = await OpenChat(fresh(), setup);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            var calls = seen.Snapshot();
            Seq(["c1/1", "c1/2", "c1/3"], calls.Select(Id), "seen");
            Check(calls.All(call => S(((JsonObjectValue)call["parent"])["callId"]) == "c1"), "parents");
            var results = nested.Snapshot();
            Seq(["True:invalid_arguments,hooked", "True:blocked,hooked", "True:tool_unavailable,hooked"],
                results.Select(result => $"{result.IsError}:{Show(Codes(result))}"), "results");
            Seq(["""{"hooked":"c1/1"}""", """{"hooked":"c1/2"}""", """{"hooked":"c1/3"}"""], results.Select(result => result.Details?.ToJson()), "details");
            // A Harness error result has no structured output, even when a hook supplies one.
            Equal(null, results[1].StructuredOutput, "blocked structuredOutput");
            Equal(null, results[2].StructuredOutput, "unavailable structuredOutput");
            await harness.CloseAsync(Context);
        });

        // #11043
        yield return Case(decided + "gives-a-nested-call-a-hook-turns-into-a-success-its-output-as-value-unless-the-tool-has-a-schema", async () =>
        {
            var setup = Setup();
            AddTool(setup.Registry, Tool("plain", (_, _, _) => Result(Empty)));
            AddTool(setup.Registry, Tool("typed", (_, _, _) => Result(Empty with { StructuredOutput = Obj(("ok", true)) }),
                tool => tool with { StructuredOutputSchema = Ok }));
            var nested = new SyncList<NestedToolExecutionResult>();
            AddTool(setup.Registry, Tool("batch", async (_, api, callContext) =>
            {
                nested.Add(await api.ExecuteToolAsync("plain", Obj(), callContext));
                nested.Add(await api.ExecuteToolAsync("typed", Obj(), callContext));
                return new ToolExecutionResult();
            }));
            AddHooks(setup.Registry, HarnessTasks.ToolTask, new ToolHooks
            {
                BeforeTool = (call, _, _) => Task.FromResult<BeforeToolDecision?>(HasParent(call) ? new BeforeToolDecision(Block: "no") : null),
                AfterTool = (call, _, _, _) => Task.FromResult<ToolExecutionResult?>(HasParent(call)
                    ? new ToolExecutionResult { Output = TextContent($"fine {S(call["name"])}"), StructuredOutput = Obj(("ok", false)) }
                    : null),
            });
            setup.Faux.SetResponses(Calls(("batch", Obj(), "c1")), Done);
            var (harness, root) = await OpenChat(fresh(), setup);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            var results = nested.Snapshot();
            Seq(["False:\"fine plain\"", "False:undefined"], results.Select(result => $"{result.IsError}:{result.StructuredOutput?.ToJson() ?? "undefined"}"),
                "results");
            await harness.CloseAsync(Context);
        });

        // ─── afterTool on results rebuilt from the slot ─────────────────────

        var rebuilt = prefix + "aftertool-on-results-rebuilt-from-the-slot.";

        // #11043
        yield return KilledStorages.Case(rebuilt + "runs-on-an-interrupted-call-a-replaced-output-loses-the-slot-s-truncation-diagnostic", async killed =>
        {
            var path = SqlitePath();
            var setup = Setup();
            setup.Progress = new ProgressSettings { OutputIntervalMs = 0, PersistWithinMs = 0 };
            Func<ToolRegistration, ToolRegistration> limits = tool => tool with
            {
                OutputLimits = new ToolOutputLimits { MaxBytes = 10, Retain = ToolOutputRetention.Tail },
            };
            var work = new Blocking("work", limits);
            var keep = new Blocking("keep", limits);
            AddTool(setup.Registry, work.Registration);
            AddTool(setup.Registry, keep.Registration);
            var seen = new SyncList<Seen>();
            AddHooks(setup.Registry, HarnessTasks.ToolTask, new ToolHooks
            {
                AfterTool = (call, result, _, _) =>
                {
                    seen.Add(new Seen(call, result));
                    return Task.FromResult<ToolExecutionResult?>(S(call["name"]) == "work" ? result with { Output = TextContent("redacted") } : null);
                },
            });
            setup.Faux.SetResponses(Calls(("work", Obj(), "c1"), ("keep", Obj(), "c2")), Done);
            var opened = await Open(openFile, killed, path, setup);
            var id = (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).Id;
            await work.Started.Task;
            await keep.Started.Task;
            await Stored();
            opened.Kill();

            opened = await Open(openFile, killed, path, setup);
            Equal(SubmissionStatus.Done, (await Settled(opened.Harness, id)).Status, "status");
            Seq(["c1:interrupted", "c2:interrupted"],
                seen.Snapshot().Select(each => $"{Id(each.Call)}:{Show(Codes(each.Result))}").Order(StringComparer.Ordinal), "seen");
            var entries = await AllEntries(opened.Root);
            var messages = Results(entries);
            Check(Text(messages["c1"]).StartsWith("redacted|", StringComparison.Ordinal), $"c1 text: {Text(messages["c1"])}");
            MatchJson("""{"isError":true,"details":{"run":1}}""", messages["c1"], "c1");
            Equal("interrupted", Show(DiagnosticsOf(entries, "c1")), "c1 diagnostics");
            // The kept output keeps its truncation diagnostic, after the hooks' result.
            Equal("interrupted,truncated", Show(DiagnosticsOf(entries, "c2")), "c2 diagnostics");
            await opened.Harness.CloseAsync(Context);
        });

        // #11043
        yield return Case(rebuilt + "runs-on-an-abort-result-but-not-on-the-aborted-results-of-calls-that-never-started", async () =>
        {
            var setup = Setup();
            setup.ToolExecution = ToolExecutionMode.Sequential;
            var first = new Blocking("first");
            AddTool(setup.Registry, first.Registration);
            AddTool(setup.Registry, Tool("second", (_, _, _) => Result(Empty)));
            var seen = new SyncList<Seen>();
            AddHooks(setup.Registry, HarnessTasks.ToolTask, new ToolHooks
            {
                AfterTool = (call, result, _, _) =>
                {
                    seen.Add(new Seen(call, result));
                    return Task.FromResult<ToolExecutionResult?>(result with { Details = Obj(("hooked", true)) });
                },
            });
            setup.Faux.SetResponses(Calls(("first", Obj(), "c1"), ("second", Obj(), "c2")), Done);
            var (harness, root) = await OpenChat(fresh(), setup);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await first.Started.Task;
            await root.AbortAsync(Context);
            Equal(SubmissionStatus.Unanswered, (await submission.WaitAsync(Context)).Status, "status");
            Seq(["c1:aborted"], seen.Snapshot().Select(each => $"{Id(each.Call)}:{Show(Codes(each.Result))}"), "seen");
            var messages = Results(await AllEntries(root));
            MatchJson("""{"isError":true,"details":{"hooked":true}}""", messages["c1"], "c1");
            Check(!messages["c2"].ContainsKey("details"), $"c2 details: {messages["c2"].ToJson()}");
            await harness.CloseAsync(Context);
        });

        // #11043
        yield return Case(rebuilt + "reports-a-throwing-hook-on-an-abort-result-and-settles-the-result-as-built", async () =>
        {
            var setup = Setup();
            var work = new Blocking("work");
            AddTool(setup.Registry, work.Registration);
            AddHooks(setup.Registry, HarnessTasks.ToolTask, new ToolHooks
            {
                AfterTool = (_, _, _, _) => throw new InvalidOperationException("hook failed"),
            });
            setup.Faux.SetResponses(Calls(("work", Obj(), "c1")), Done);
            var (harness, root) = await OpenChat(fresh(), setup);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await work.Started.Task;
            await root.AbortAsync(Context);
            Equal(SubmissionStatus.Unanswered, (await submission.WaitAsync(Context)).Status, "status");
            var entries = await AllEntries(root);
            Check(Text(Results(entries)["c1"]).StartsWith($"work {new string('o', 40)}", StringComparison.Ordinal), "c1 text");
            Equal("aborted", Show(DiagnosticsOf(entries, "c1")), "c1 diagnostics");
            Check(setup.Reports.Snapshot().Any(error => error.Message == "hook failed"),
                $"reports: [{string.Join(", ", setup.Reports.Snapshot().Select(error => error.Message))}]");
            await harness.CloseAsync(Context);
        });

        // #11043
        yield return KilledStorages.Case(rebuilt + "settles-an-abort-without-the-hooks-in-the-abort-handler-s-last-attempt", async killed =>
        {
            var path = SqlitePath();
            var setup = Setup();
            setup.RestartLimit = 2;
            setup.Progress = new ProgressSettings { OutputIntervalMs = 0, PersistWithinMs = 0 };
            var work = new Blocking("work");
            AddTool(setup.Registry, work.Registration);
            var seen = new SyncList<Seen>();
            var hanging = new HarnessTests.Deferred();
            AddHooks(setup.Registry, HarnessTasks.ToolTask, new ToolHooks
            {
                AfterTool = (call, result, _, _) =>
                {
                    seen.Add(new Seen(call, result));
                    hanging.Resolve();
                    // Never returns: the host dies inside the hook.
                    return Never();
                },
            });
            setup.Faux.SetResponses(Calls(("work", Obj(), "c1")), Done);
            var opened = await Open(openFile, killed, path, setup);
            var id = (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).Id;
            await work.Started.Task;
            var taskId = await ToolTaskId(opened.Harness);
            await Stored();
            _ = opened.Root.AbortAsync(Context).ContinueWith(task => _ = task.Exception, TaskScheduler.Default);
            await hanging.Task;
            opened.Kill();

            // One restart, at the limit minus one: the attempt the scheduler would fault when cut off runs no hooks.
            opened = await Open(openFile, killed, path, setup);
            Equal(SubmissionStatus.Unanswered, (await Settled(opened.Harness, id)).Status, "status");
            Seq(["aborted"], seen.Snapshot().Select(each => Show(Codes(each.Result))), "seen");
            var entries = await AllEntries(opened.Root);
            Check(Text(Results(entries)["c1"]).StartsWith($"work {new string('o', 40)}", StringComparison.Ordinal), "c1 text");
            Equal("aborted", Show(DiagnosticsOf(entries, "c1")), "c1 diagnostics");
            MatchJson("""{"status":"terminal","outcome":{"status":"aborted"}}""", (await opened.Harness.GetTaskAsync(taskId, Context))?.State.ToJson(), "tool task");
            await opened.Harness.CloseAsync(Context);
        });

        // #11043
        yield return KilledStorages.Case(rebuilt + "sees-the-held-progress-of-an-aborted-call-and-the-result-stores-it", async killed =>
        {
            var path = SqlitePath();
            var setup = Setup();
            // Progress commits are held in memory until the result commit stores them.
            setup.Progress = new ProgressSettings { OutputIntervalMs = 0, PersistWithinMs = 60_000 };
            var work = new Blocking("work");
            AddTool(setup.Registry, work.Registration);
            var seen = new SyncList<Seen>();
            AddHooks(setup.Registry, HarnessTasks.ToolTask, new ToolHooks
            {
                AfterTool = (call, result, _, _) =>
                {
                    seen.Add(new Seen(call, result));
                    return Task.FromResult<ToolExecutionResult?>(null);
                },
            });
            setup.Faux.SetResponses(Calls(("work", Obj(), "c1")), Done);
            var opened = await Open(openFile, killed, path, setup);
            var submission = await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await work.Started.Task;
            await opened.Root.AbortAsync(Context);
            Equal(SubmissionStatus.Unanswered, (await submission.WaitAsync(Context)).Status, "status");
            var results = seen.Snapshot();
            Equal(1, results.Count, "seen");
            SameJson("""{"run":1}""", results[0].Result.Details, "seen details");
            Equal($"work {new string('o', 40)}\n", Text(results[0].Result.Output ?? JsonArrayValue.Empty), "seen output");
            await opened.Harness.CloseAsync(Context);

            opened = await Open(openFile, killed, path, setup);
            var message = Results(await AllEntries(opened.Root))["c1"];
            MatchJson("""{"isError":true,"details":{"run":1}}""", message, "stored");
            Check(Text(message).StartsWith($"work {new string('o', 40)}", StringComparison.Ordinal), $"stored text: {Text(message)}");
            await opened.Harness.CloseAsync(Context);
        });

        // #11043
        yield return KilledStorages.Case(rebuilt + "runs-on-an-abandoned-nested-call-s-result", async killed =>
        {
            var path = SqlitePath();
            var setup = Setup();
            var gated = new HarnessTests.Deferred();
            var gate = 1;
            AddTool(setup.Registry, Tool("gated", (_, _, _) => Result(Empty)));
            AddTool(setup.Registry, Tool("batch", async (_, api, callContext) =>
            {
                await api.ExecuteToolAsync("gated", Obj(), callContext);
                return new ToolExecutionResult();
            }));
            var seen = new SyncList<Seen>();
            AddHooks(setup.Registry, HarnessTasks.ToolTask, new ToolHooks
            {
                // Holds the nested call in its `call` phase, before intent, until the Harness closes.
                BeforeTool = async (call, _, hookContext) =>
                {
                    if (S(call["name"]) != "gated" || Volatile.Read(ref gate) == 0) return null;
                    gated.Resolve();
                    await Aborted(hookContext.CancellationToken);
                    return null;
                },
                AfterTool = (call, result, _, _) =>
                {
                    if (!HasParent(call)) return Task.FromResult<ToolExecutionResult?>(null);
                    seen.Add(new Seen(call, result));
                    return Task.FromResult<ToolExecutionResult?>(result with { Details = Obj(("hooked", true)) });
                },
            });
            setup.Faux.SetResponses(Calls(("batch", Obj(), "c1")), Done);
            var opened = await Open(openFile, killed, path, setup);
            var id = (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).Id;
            await gated.Task;
            await opened.Harness.CloseAsync(Context);
            Volatile.Write(ref gate, 0);

            opened = await Open(openFile, killed, path, setup);
            Equal(SubmissionStatus.Done, (await Settled(opened.Harness, id)).Status, "status");
            Seq(["c1/1:abandoned"], seen.Snapshot().Select(each => $"{Id(each.Call)}:{Show(Codes(each.Result))}"), "seen");
            await opened.Harness.CloseAsync(Context);
        });

        // #11043
        yield return KilledStorages.Case(rebuilt + "runs-on-a-restart-limit-result-at-the-limit-and-not-once-the-hooks-themselves-were-cut-off", async killed =>
        {
            var path = SqlitePath();
            var setup = Setup();
            setup.RestartLimit = 1;
            setup.Progress = new ProgressSettings { OutputIntervalMs = 0, PersistWithinMs = 0 };
            var work = new Blocking("work");
            AddTool(setup.Registry, work.Registration);
            var seen = new SyncList<Seen>();
            var hanging = new HarnessTests.Deferred();
            AddHooks(setup.Registry, HarnessTasks.ToolTask, new ToolHooks
            {
                AfterTool = (call, result, _, _) =>
                {
                    seen.Add(new Seen(call, result));
                    hanging.Resolve();
                    // Never returns: the host dies inside the hook.
                    return Never();
                },
            });
            setup.Faux.SetResponses(Calls(("work", Obj(), "c1")), Done);
            var opened = await Open(openFile, killed, path, setup);
            var id = (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).Id;
            await work.Started.Task;
            var taskId = await ToolTaskId(opened.Harness);
            await Stored();
            opened.Kill();

            // Cut off once, at the limit: the task ends itself, and the hooks run on its result.
            opened = await Open(openFile, killed, path, setup);
            await hanging.Task;
            Seq(["restart_limit"], seen.Snapshot().Select(each => Show(Codes(each.Result))), "seen at the limit");
            opened.Kill();

            // Past the limit: ends without the hooks.
            opened = await Open(openFile, killed, path, setup);
            Equal(SubmissionStatus.Done, (await Settled(opened.Harness, id)).Status, "status");
            Equal(1, seen.Count, "seen");
            Equal(1, work.Runs, "runs");
            var entries = await AllEntries(opened.Root);
            Equal("restart_limit", Show(DiagnosticsOf(entries, "c1")), "c1 diagnostics");
            MatchJson("""{"status":"terminal","outcome":{"status":"failed","error":{"detail":{"reason":"restart_limit"}}}}""",
                (await opened.Harness.GetTaskAsync(taskId, Context))?.State.ToJson(), "tool task");
            await opened.Harness.CloseAsync(Context);
        });

        // ─── calls to tools the request did not offer ───────────────────────

        var unoffered = prefix + "calls-to-tools-the-request-did-not-offer.";

        // #11043
        yield return Case(unoffered + "run-aftertool-inside-their-own-tool-task", async () =>
        {
            var setup = Setup();
            var seen = new SyncList<(string Id, TaskId TaskId)>();
            AddHooks(setup.Registry, HarnessTasks.ToolTask, new ToolHooks
            {
                AfterTool = (call, result, api, _) =>
                {
                    seen.Add((Id(call), api.TaskId));
                    return Task.FromResult<ToolExecutionResult?>(result with { Output = TextContent("try another tool") });
                },
            });
            setup.Faux.SetResponses(Calls(("ghost", Obj(), "c1")), Done);
            var (harness, root) = await OpenChat(fresh(), setup);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            var hooked = seen.Snapshot();
            Seq(["c1"], hooked.Select(each => each.Id), "seen");
            var task = await harness.GetTaskAsync(hooked[0].TaskId, Context);
            Equal("pi.tool", task?.Kind, "kind");
            MatchJson("""{"kind":"model","callId":"c1","unoffered":true}""", task?.Input, "input");
            var entries = await AllEntries(root);
            Check(Text(Results(entries)["c1"]).StartsWith("try another tool|", StringComparison.Ordinal), $"c1 text: {Text(Results(entries)["c1"])}");
            Equal("tool_unavailable", Show(DiagnosticsOf(entries, "c1")), "c1 diagnostics");
            await harness.CloseAsync(Context);
        });

        // #11043
        yield return Case(unoffered + "count-toward-a-round-s-terminate", async () =>
        {
            var setup = Setup();
            AddTool(setup.Registry, Tool("echo", (_, _, _) => Result(Empty)));
            AddHooks(setup.Registry, HarnessTasks.ToolTask, new ToolHooks
            {
                AfterTool = (_, result, _, _) => Task.FromResult<ToolExecutionResult?>(result with { Control = new ToolControl { Terminate = true } }),
            });
            setup.Faux.SetResponses(Calls(("echo", Obj(), "c1"), ("ghost", Obj(), "c2")), Done);
            var (harness, root) = await OpenChat(fresh(), setup);
            var settled = await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            Equal(SubmissionStatus.Done, settled.Status, "status");
            Equal(1, setup.Faux.State.CallCount, "calls");
            await harness.CloseAsync(Context);
        });

        // #11043
        yield return Case(unoffered + "take-their-turn-in-a-sequential-round-and-the-last-handoff-in-call-order-wins", async () =>
        {
            var setup = Setup();
            setup.ToolExecution = ToolExecutionMode.Sequential;
            var order = new SyncList<string>();
            foreach (var name in new[] { "a", "b" })
            {
                AddTool(setup.Registry, Tool(name, (_, _, _) =>
                {
                    order.Add(name);
                    return Result(Empty);
                }));
            }
            AddHooks(setup.Registry, HarnessTasks.ToolTask, new ToolHooks
            {
                AfterTool = (call, result, _, _) =>
                {
                    order.Add($"hook {Id(call)}");
                    return Task.FromResult<ToolExecutionResult?>(Id(call) == "c1" ? null : result with { Control = new ToolControl { Handoff = $"from {Id(call)}" } });
                },
            });
            setup.Faux.SetResponses(Calls(("a", Obj(), "c1"), ("ghost", Obj(), "c2"), ("b", Obj(), "c3")), Done);
            var (harness, root) = await OpenChat(fresh(), setup);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            Seq(["a", "hook c1", "hook c2", "b", "hook c3"], order.Snapshot(), "order");
            var resets = (await AllEntries(root)).Where(entry => entry.Kind == "pi.reset").ToList();
            Seq(["from c3"], resets.Select(entry => TextOf(entry.Model![0])), "resets");
            await harness.CloseAsync(Context);
        });

        // #11043
        yield return Case(unoffered + "each-get-a-tool-task-in-a-parallel-round-of-only-such-calls", async () =>
        {
            var setup = Setup();
            var seen = new SyncList<string>();
            AddHooks(setup.Registry, HarnessTasks.ToolTask, new ToolHooks
            {
                AfterTool = (call, _, _, _) =>
                {
                    seen.Add(Id(call));
                    return Task.FromResult<ToolExecutionResult?>(null);
                },
            });
            setup.Faux.SetResponses(Calls(("ghost", Obj(), "c1"), ("spirit", Obj(), "c2"), ("ghost", Obj(), "c3")), Done);
            var (harness, root) = await OpenChat(fresh(), setup);
            var settled = await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            Equal(SubmissionStatus.Done, settled.Status, "status");
            Seq(["c1", "c2", "c3"], seen.Snapshot().Order(StringComparer.Ordinal), "seen");
            var entries = await AllEntries(root);
            foreach (var id in new[] { "c1", "c2", "c3" }) Equal("tool_unavailable", Show(DiagnosticsOf(entries, id)), $"{id} diagnostics");
            // The round's results go to the next request.
            Equal(2, setup.Faux.State.CallCount, "calls");
            await harness.CloseAsync(Context);
        });

        // #11043
        yield return KilledStorages.Case(unoffered + "keep-their-flag-in-a-version-2-generation-s-sequential-round-also-one-migrated-from-version-1", async killed =>
        {
            foreach (var version in new[] { 2, 1 })
            {
                var path = SqlitePath();
                var setup = Setup();
                setup.ToolExecution = ToolExecutionMode.Sequential;
                var started = new HarnessTests.Deferred();
                var runs = 0;
                var a = Tool("a", async (_, _, callContext) =>
                {
                    if (Interlocked.Increment(ref runs) > 1) return Empty;
                    started.Resolve();
                    await Aborted(callContext.CancellationToken);
                    return new ToolExecutionResult();
                }, tool => tool with { Replay = ToolReplay.Safe });
                AddTool(setup.Registry, a);
                AddTool(setup.Registry, Tool("ghost", (_, _, _) => Result(new ToolExecutionResult { Output = TextContent("ran") })));
                setup.Faux.SetResponses(Calls(("a", Obj(), "c1"), ("ghost", Obj(), "c2")), Done);
                var opened = await Open(openFile, killed, path, setup);
                var root = opened.Root;
                // `ghost` is registered, but not offered to this request.
                await root.ConfigureAsync(new AgentChange { Tools = ToolSelection.Only(a) }, Context);
                var id = (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).Id;
                await started.Task;
                var live = await Live(opened.Harness);
                var generation = new TaskId((long)N(((JsonObjectValue)live["run"])["taskId"]));
                Seq(["c1:undefined", "c2:true"], ((JsonArrayValue)live["tools"]).OfType<JsonObjectValue>()
                    .Select(slot => $"{S(slot["callId"])}:{(slot.TryGetValue("unoffered", out var flag) ? flag.ToJson() : "undefined")}"), $"v{version} slots");
                Equal(2, (await opened.Harness.GetTaskAsync(generation, Context))?.Version, $"v{version} generation version");
                // The tools are offered when the round goes on: only the flag keeps `ghost` from running.
                await root.ConfigureAsync(new AgentChange { Tools = Patch<ToolSelection>.Clear }, Context);
                await opened.Harness.CloseAsync(Context);
                if (version == 1)
                {
                    var storage = await openFile(path);
                    var record = (await storage.GetTaskAsync(generation, Context))!;
                    await storage.CommitWritesAsync([new TaskWrite(record with { Version = 1 })], Context);
                    await storage.CloseAsync(Context);
                }

                opened = await Open(openFile, killed, path, setup);
                root = opened.Root;
                Equal(SubmissionStatus.Done, (await Settled(opened.Harness, id)).Status, $"v{version} status");
                Equal(2, (await opened.Harness.GetTaskAsync(generation, Context))?.Version, $"v{version} migrated version");
                var entries = await AllEntries(root);
                Equal("tool_unavailable", Show(DiagnosticsOf(entries, "c2")), $"v{version} c2 diagnostics");
                Check(!Text(Results(entries)["c2"]).Contains("ran", StringComparison.Ordinal), $"v{version} c2 text: {Text(Results(entries)["c2"])}");
                await opened.Harness.CloseAsync(Context);
            }
        });

        // #11043
        yield return Case(unoffered + "settle-unavailable-not-aborted-when-their-sequential-round-is-aborted-before-they-start", async () =>
        {
            var setup = Setup();
            setup.ToolExecution = ToolExecutionMode.Sequential;
            var started = new HarnessTests.Deferred();
            AddTool(setup.Registry, Tool("wait", async (_, _, callContext) =>
            {
                started.Resolve();
                await Aborted(callContext.CancellationToken);
                return new ToolExecutionResult();
            }));
            setup.Faux.SetResponses(Calls(("wait", Obj(), "c1"), ("ghost", Obj(), "c2")), Done);
            var (harness, root) = await OpenChat(fresh(), setup);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await started.Task;
            await root.AbortAsync(Context);
            Equal(SubmissionStatus.Unanswered, (await submission.WaitAsync(Context)).Status, "status");
            var entries = await AllEntries(root);
            Equal("aborted", Show(DiagnosticsOf(entries, "c1")), "c1 diagnostics");
            Equal("tool_unavailable", Show(DiagnosticsOf(entries, "c2")), "c2 diagnostics");
            await harness.CloseAsync(Context);
        });
    }
}
