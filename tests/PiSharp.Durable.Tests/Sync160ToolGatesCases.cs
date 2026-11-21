// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/durable/test/harness-tool-gates.test.ts. Gates before a tool runs (spec
// suspendable §5): `beforeTool` sleeps, the `call` phase asked again on every wake, registry changes and opens that wake parked calls, and
// the deadline path of such a wake. Verbatim apart from the adaptations in HarnessSupport.cs/ChatSupport.cs, and these:
// - pi-schema builders are their JSON Schema: obj({ allow: bool() }) and obj({ command: str() }) are closed objects with the property
//   required.
// - The `{ count: 0 }` counters hooks and tools bump are Counter (Interlocked): handlers run on thread-pool threads.
// - `setup.now = () => Date.now() + offset` is setup.Now reading the offset with Volatile, as the Harness reads the clock on other threads.
// - waitFor is Eventually; openNodeSqliteStorage is SqliteStorage.OpenAsync over a file in a fresh temporary directory (afterEach's rm of
//   the directory is not ported, as in the other ports: Temp directories are the runner's).
// - toMatchObject of a task's state compares its JSON (MatchJson); a signal key is its JSON {signal, key}.
// - `text(results(...)[0])` of a missing result is "", as upstream's `text(undefined)` is.
// - "wake a sleep committed under a snapshot ...": the gate decides by the pass number it took when it entered (upstream reads
//   `passes.count` again after its await; one pass of a call runs at a time, so the two are the same number).
// Every case is ported.
using System.Collections.Immutable;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;
using static ChatTests;
using static HarnessTests;
using static Program;
using static SessionTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync160ToolGatesCases() => ToolGateTests.Cases();
}

/// <summary>The sync160.tool-gates.* cases (upstream harness-tool-gates.test.ts).</summary>
internal static class ToolGateTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    /// <summary><c>defineSignal({ name: "test.approval", version: 1, schema: obj({ allow: bool() }) })</c>.</summary>
    private static readonly SignalToken Approval = Signals.DefineSignal(new SignalDefinition
    {
        Name = "test.approval",
        Version = 1,
        Schema = O("""{"type":"object","properties":{"allow":{"type":"boolean"}},"required":["allow"],"additionalProperties":false}"""),
    });

    private const double Day = 24 * 60 * 60 * 1000;

    /// <summary><c>obj({ command: str() })</c>.</summary>
    private static readonly JsonObjectValue CommandParameters =
        O("""{"type":"object","properties":{"command":{"type":"string"}},"required":["command"],"additionalProperties":false}""");

    private static readonly JsonObjectValue Allow = O("""{"allow":true}""");

    private static readonly JsonObjectValue Deny = O("""{"allow":false}""");

    /// <summary>A count that handlers on different threads bump (upstream's <c>{ count: 0 }</c>).</summary>
    private sealed class Counter
    {
        private int count;
        public int Count => Volatile.Read(ref count);
        public int Increment() => Interlocked.Increment(ref count);
    }

    private static string SqlitePath() => Path.Combine(Temp("pi-durable-gates"), "session.sqlite");

    /// <summary>A tool-calling answer with one <c>bash</c> call <c>c1</c> (upstream <c>call</c>).</summary>
    private static JsonObjectValue Call(string command = "rm -rf build") =>
        FauxAssistantMessage(new[] { FauxToolCall("bash", Obj(("command", command)), "c1") }, stopReason: "toolUse");

    /// <summary><c>fauxAssistantMessage([fauxText("done")])</c>.</summary>
    private static readonly JsonObjectValue Done = FauxAssistantMessage("done");

    /// <summary>The tool result messages, oldest first (upstream <c>results</c>).</summary>
    private static List<JsonObjectValue> Results(IEnumerable<EntryRecord> entries) =>
        [.. entries.Where(entry => entry.Kind == "pi.tool-result").Select(entry => (JsonObjectValue)entry.Model![0])];

    /// <summary>The message's text items joined by <c>|</c>, other items as nothing; "" without a message (upstream <c>text</c>).</summary>
    private static string Text(JsonObjectValue? message) =>
        message is null || !message.TryGetValue("content", out var content) || content is not JsonArrayValue items
            ? ""
            : string.Join("|", items.Select(item =>
                item is JsonObjectValue block && block.TryGetValue("type", out var type) && type is JsonStringValue { Value: "text" } ? S(block["text"]) : ""));

    /// <summary>The text of the first tool result of <paramref name="conversation"/>.</summary>
    private static async Task<string> FirstResultText(IConversation conversation) => Text(Results(await AllEntries(conversation)).FirstOrDefault());

    /// <summary>A <c>bash</c> stand-in that counts its runs (upstream <c>bashTool</c>).</summary>
    private static ToolRegistration BashTool(Counter runs) => Define.Tool(new ToolRegistration
    {
        Name = "bash",
        Description = "bash",
        Parameters = CommandParameters,
        Execute = (args, _, _) =>
        {
            runs.Increment();
            return Task.FromResult(new ToolExecutionResult { Output = JsonValue.Array(FauxText($"ran {S(((JsonObjectValue)args)["command"])}")) });
        },
    });

    /// <summary>The approval gate of spec §5.1: sleeps on an approval keyed by the command, with a deadline fixed at its first pass (upstream
    /// <c>approvals</c>).</summary>
    private static HookRegistration Approvals(Counter passes) => Define.Hook(HarnessTasks.ToolTask, new ToolHooks
    {
        BeforeTool = async (gated, api, ctx) =>
        {
            passes.Increment();
            var key = S(((JsonObjectValue)gated["arguments"])["command"]).Replace(" ", "_", StringComparison.Ordinal);
            var state = await api.ReadSignalAsync(Approval, key, ctx);
            var until = N(await api.MemoAsync($"approvals.until.{key}", (JsonValue)(api.Now() + Day), ctx));
            if (state is null) return new BeforeToolDecision(Sleep: new GateSleep(until, ImmutableArray.Create(Approval.Key(key))));
            if (state.Expired) return new BeforeToolDecision(Block: "No approval within a day");
            return ((JsonObjectValue)state.Value!)["allow"] is JsonBooleanValue { Value: true } ? null : new BeforeToolDecision(Block: "Not approved");
        },
    });

    /// <summary>The extension <c>approvals</c> holding the approval gate.</summary>
    private static Extension ApprovalsExtension(Counter passes) => Define.Extension(new Extension { Name = "approvals", Hooks = [Approvals(passes)] });

    /// <summary>Opens a chat, resumed (upstream <c>open</c>).</summary>
    private static async Task<OpenedChat> Open(ChatSetup setup, IStorage? storage = null)
    {
        var opened = await OpenChat(storage ?? new MemoryStorage(), setup);
        opened.Harness.Resume();
        return opened;
    }

    /// <summary>The task of the root's first tool slot, once it sleeps at its gates (upstream <c>gated</c>).</summary>
    private static async Task<TaskId> Gated(IHarness harness)
    {
        TaskId? id = null;
        await Eventually(async () =>
        {
            var live = await harness.SnapshotAsync(Live.LiveDoc, ConversationId.Root, Context);
            if (live is null || !live.TryGetValue("tools", out var tools) || tools is not JsonArrayValue { Count: > 0 } slots) return false;
            var slot = (JsonObjectValue)slots[0];
            id = slot.TryGetValue("taskId", out var taskId) && taskId is JsonNumberValue number ? new TaskId((long)number.Value) : null;
            return id is not null && slot.TryGetValue("status", out var status) && status is JsonStringValue { Value: "sleeping" };
        });
        return id!.Value;
    }

    private static async Task<SubmissionStatus> Settled(IHarness harness, SubmissionId id) =>
        (await (await harness.SubmissionAsync(id, Context))!.WaitAsync(Context)).Status;

    internal static IEnumerable<(string, Func<Task>)> Cases()
    {
        // ─── a beforeTool sleep ─────────────────────────────────────────────

        const string sleep = "sync160.tool-gates.a-beforetool-sleep.";

        yield return Case(sleep + "parks-the-call-before-it-runs-and-runs-it-once-approved", async () =>
        {
            var setup = Setup();
            var runs = new Counter();
            var passes = new Counter();
            AddTool(setup.Registry, BashTool(runs));
            setup.Registry.Install(ApprovalsExtension(passes));
            setup.Faux.SetResponses(Call(), Done);
            var (harness, root) = await Open(setup);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            var id = await Gated(harness);
            MatchJson("""{"status":"sleeping","checkpoint":{"phase":"call"},"signals":[{"signal":"test.approval","key":"rm_-rf_build"}]}""",
                (await harness.GetTaskAsync(id, Context))?.State.ToJson(), "state");
            await Eventually(() => !harness.Wake().Active);
            Equal(0, runs.Count, "runs while asleep");
            await harness.SignalAsync(id, Approval, "rm_-rf_build", Allow, Context);
            Equal(SubmissionStatus.Done, (await submission.WaitAsync(Context)).Status, "status");
            Equal(1, runs.Count, "runs");
            Equal(2, passes.Count, "passes");
            Equal("ran rm -rf build", await FirstResultText(root), "result");
            await harness.CloseAsync(Context);
        });

        yield return Case(sleep + "blocks-on-a-denial-and-on-the-deadline-after-which-a-late-answer-is-closed", async () =>
        {
            foreach (var outcome in new[] { "denied", "expired" })
            {
                var setup = Setup();
                double offset = 0;
                setup.Now = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + Volatile.Read(ref offset);
                var runs = new Counter();
                AddTool(setup.Registry, BashTool(runs));
                setup.Registry.Install(ApprovalsExtension(new Counter()));
                setup.Faux.SetResponses(Call(), Done);
                var (harness, root) = await Open(setup);
                var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
                var id = await Gated(harness);
                if (outcome == "denied") await harness.SignalAsync(id, Approval, "rm_-rf_build", Deny, Context);
                else
                {
                    Volatile.Write(ref offset, Day);
                    harness.Resume();
                }
                Equal(SubmissionStatus.Done, (await submission.WaitAsync(Context)).Status, $"{outcome} status");
                Equal(0, runs.Count, $"{outcome} runs");
                var text = await FirstResultText(root);
                Check(text.Contains(outcome == "denied" ? "Not approved" : "No approval within a day", StringComparison.Ordinal), $"{outcome} result: {text}");
                if (outcome == "expired")
                    Equal(SignalResult.Closed, await harness.SignalAsync(id, Approval, "rm_-rf_build", Allow, Context), "late answer");
                await harness.CloseAsync(Context);
            }
        });

        yield return Case(sleep + "asks-every-gate-again-on-each-wake-in-extension-order-and-stops-at-the-first-that-sleeps", async () =>
        {
            var setup = Setup();
            var runs = new Counter();
            var passes = new Counter();
            var quiet = new Counter();
            AddTool(setup.Registry, BashTool(runs));
            setup.Registry.Install(ApprovalsExtension(passes));
            setup.Registry.Install(Define.Extension(new Extension
            {
                Name = "quiet-hours",
                Hooks =
                [
                    Define.Hook(HarnessTasks.ToolTask, new ToolHooks
                    {
                        BeforeTool = (_, _, _) =>
                        {
                            quiet.Increment();
                            return Task.FromResult<BeforeToolDecision?>(null);
                        },
                    }),
                ],
            }));
            setup.Faux.SetResponses(Call(), Done);
            var (harness, root) = await Open(setup);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            var id = await Gated(harness);
            Seq([1, 0], new[] { passes.Count, quiet.Count }, "passes and quiet while asleep");
            await harness.SignalAsync(id, Approval, "rm_-rf_build", Allow, Context);
            await submission.WaitAsync(Context);
            Seq([2, 1, 1], new[] { passes.Count, quiet.Count, runs.Count }, "passes, quiet and runs");
            await harness.CloseAsync(Context);
        });

        yield return Case(sleep + "ends-the-call-invalid-park-when-the-sleep-waits-for-nothing", async () =>
        {
            var setup = Setup();
            var runs = new Counter();
            AddTool(setup.Registry, BashTool(runs));
            setup.Registry.Install(Define.Extension(new Extension
            {
                Name = "bad",
                Hooks =
                [
                    Define.Hook(HarnessTasks.ToolTask, new ToolHooks
                    {
                        BeforeTool = (_, _, _) => Task.FromResult<BeforeToolDecision?>(new BeforeToolDecision(Sleep: new GateSleep())),
                    }),
                ],
            }));
            setup.Faux.SetResponses(Call(), Done);
            var (harness, root) = await Open(setup);
            Equal(SubmissionStatus.Done, (await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context)).Status, "status");
            Equal(0, runs.Count, "runs");
            var text = await FirstResultText(root);
            Check(text.Contains("cannot wait at its gates", StringComparison.Ordinal), $"result: {text}");
            await harness.CloseAsync(Context);
        });

        // ─── registry changes and opens ─────────────────────────────────────

        const string registry = "sync160.tool-gates.registry-changes-and-opens.";

        yield return Case(registry + "wake-a-call-parked-at-its-gates-so-the-gates-installed-then-decide", async () =>
        {
            var setup = Setup();
            var runs = new Counter();
            var passes = new Counter();
            AddTool(setup.Registry, BashTool(runs));
            var gate = ApprovalsExtension(passes);
            setup.Registry.Install(gate);
            setup.Faux.SetResponses(Call(), Done);
            var (harness, root) = await Open(setup);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await Gated(harness);
            // Another extension installed: asked again, and still asleep.
            setup.Registry.Install(Define.Extension(new Extension { Name = "other" }));
            await Eventually(() => passes.Count == 2);
            // The gate uninstalled: the next pass runs the call.
            setup.Registry.Uninstall(gate);
            Equal(SubmissionStatus.Done, (await submission.WaitAsync(Context)).Status, "status");
            Seq([2, 1], new[] { passes.Count, runs.Count }, "passes and runs");
            await harness.CloseAsync(Context);
        });

        yield return Case(registry + "wake-a-sleep-committed-under-a-snapshot-a-publication-replaced-while-the-gate-ran", async () =>
        {
            var setup = Setup();
            var passes = new Counter();
            var entered = new HarnessTests.Deferred();
            var release = new HarnessTests.Deferred();
            AddTool(setup.Registry, BashTool(new Counter()));
            setup.Registry.Install(Define.Extension(new Extension
            {
                Name = "slow",
                Hooks =
                [
                    Define.Hook(HarnessTasks.ToolTask, new ToolHooks
                    {
                        BeforeTool = async (_, _, _) =>
                        {
                            var pass = passes.Increment();
                            if (pass == 1)
                            {
                                entered.Resolve();
                                await release.Task;
                            }
                            return pass < 3 ? new BeforeToolDecision(Sleep: new GateSleep(Signals: ImmutableArray.Create(Approval.Key($"k{pass}")))) : null;
                        },
                    }),
                ],
            }));
            setup.Faux.SetResponses(Call(), Done);
            var (harness, root) = await Open(setup);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await entered.Task;
            setup.Registry.Install(Define.Extension(new Extension { Name = "other" }));
            release.Resolve();
            // The first sleep is woken at once; the second one waits for its key.
            var id = await Gated(harness);
            await Eventually(() => passes.Count == 2);
            await harness.SignalAsync(id, Approval, "k2", Allow, Context);
            Equal(SubmissionStatus.Done, (await submission.WaitAsync(Context)).Status, "status");
            Equal(3, passes.Count, "passes");
            await harness.CloseAsync(Context);
        });

        yield return Case(registry + "wake-a-call-parked-at-its-gates-at-open-before-its-deadline-so-a-gate-removed-meanwhile-lets-it-run", async () =>
        {
            var path = SqlitePath();
            var passes = new Counter();
            var runs = new Counter();
            ChatSetup Build(bool withGate)
            {
                var built = Setup();
                AddTool(built.Registry, BashTool(runs));
                if (withGate) built.Registry.Install(ApprovalsExtension(passes));
                built.Faux.SetResponses(Call(), Done);
                return built;
            }
            var opened = await Open(Build(true), await SqliteStorage.OpenAsync(path));
            var submissionId = (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).Id;
            await Gated(opened.Harness);
            await opened.Harness.CloseAsync(Context);
            // The next process installs no approvals: the call runs at open, no signal, no deadline.
            var setup = Build(false);
            setup.Faux.SetResponses(Done);
            opened = await Open(setup, await SqliteStorage.OpenAsync(path));
            Equal(SubmissionStatus.Done, await Settled(opened.Harness, submissionId), "status");
            Seq([1, 1], new[] { passes.Count, runs.Count }, "passes and runs");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(registry + "leave-a-call-parked-in-execute-alone", async () =>
        {
            var path = SqlitePath();
            var setup = Setup();
            var runs = new Counter();
            AddTool(setup.Registry, Define.Tool(new ToolRegistration
            {
                Name = "bash",
                Description = "bash",
                Parameters = CommandParameters,
                Execute = (_, api, _) =>
                {
                    runs.Increment();
                    return Task.FromResult(api.Wake is null
                        ? (ToolExecutionResult)ToolPark.Sleeping(signals: [Approval.Key("done")])
                        : new ToolExecutionResult { Output = JsonArrayValue.Empty });
                },
            }));
            setup.Faux.SetResponses(Call(), Done);
            var opened = await Open(setup, await SqliteStorage.OpenAsync(path));
            await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            var id = await Gated(opened.Harness);
            setup.Registry.Install(Define.Extension(new Extension { Name = "other" }));
            await opened.Harness.CloseAsync(Context);
            opened = await Open(setup, await SqliteStorage.OpenAsync(path));
            var reopened = opened.Harness;
            await Eventually(() => !reopened.Wake().Active);
            MatchJson("""{"status":"sleeping","checkpoint":{"phase":"parked"}}""", (await reopened.GetTaskAsync(id, Context))?.State.ToJson(), "state");
            Equal(1, runs.Count, "runs");
            await reopened.CloseAsync(Context);
        });

        yield return Case(registry + "take-the-deadline-path-for-a-gate-whose-deadline-passed-so-its-keys-expire", async () =>
        {
            var path = SqlitePath();
            var setup = Setup();
            double offset = 0;
            setup.Now = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + Volatile.Read(ref offset);
            var passes = new Counter();
            var runs = new Counter();
            AddTool(setup.Registry, BashTool(runs));
            setup.Registry.Install(ApprovalsExtension(passes));
            setup.Faux.SetResponses(Call(), Done);
            var opened = await OpenChat(await SqliteStorage.OpenAsync(path), setup);
            opened.Harness.Resume();
            var submissionId = (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).Id;
            var id = await Gated(opened.Harness);
            await opened.Harness.CloseAsync(Context);
            // Reopened after the deadline: the open's wake expires the key, and the gate blocks.
            Volatile.Write(ref offset, 2 * Day);
            opened = await Open(setup, await SqliteStorage.OpenAsync(path));
            Equal(SubmissionStatus.Done, await Settled(opened.Harness, submissionId), "status");
            Equal(2, passes.Count, "passes");
            Equal(0, runs.Count, "runs");
            var text = await FirstResultText(opened.Root);
            Check(text.Contains("No approval within a day", StringComparison.Ordinal), $"result: {text}");
            Equal(SignalResult.Closed, await opened.Harness.SignalAsync(id, Approval, "rm_-rf_build", Allow, Context), "late answer");
            await opened.Harness.CloseAsync(Context);
        });
    }
}
