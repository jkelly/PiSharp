// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/harness-turn-limit.test.ts (f6a9db1d8, #11043), ported in the
// Pi v1.5.0 sync (area live-turns). Per-conversation turn limits (AgentState.maxTurns, Agent.MaxTurns here) and continuing a run that reached
// one. harness.turn-limit.* runs on MemoryStorage and a SQLite file as upstream; the store pass (StoreStorageNode.cs) reruns it as
// harness.turn-limit.store.*, opening the file as a StoreStorage and running the MemoryStorage cases on a fresh StoreStorage, as areas sleep,
// tool-tasks and admission do (upstream's vitest.store.config.ts lists harness-turn-limit.test.ts). Verbatim apart from the adaptations in
// HarnessSupport.cs and ChatSupport.cs, and these:
// - Tools have pi-schema's obj({}) parameters, ToolRegistration's default (area tools-schema).
// - `setup.settings.afterFailedRun = policy` and `setup.settings.progress = ...` are ChatSetup.AfterFailedRun and ChatSetup.Progress.
// - "rejects a limit that is not an integer 1 or more": AgentChange.MaxTurns is a long, so upstream's 1.5, NaN and Infinity cannot be given;
//   the case tries 0, -1 and 2^53 (not a safe integer, which Number.isSafeInteger rejects), each an ArgumentOutOfRangeException where
//   upstream's configure() rejects with a RangeError.
// - "keeps the stored count across a restart" opens the file behind a KillableStorage (HarnessRestartFailureCases.cs), upstream's Proxy
//   whose calls hang once dead; the killed storage is closed after the case (KilledStorages). openNodeSqliteStorage is the openFile the
//   group takes.
// - "lets terminate settle done at the limit, and writes a handoff's reset before ending": a handoff continues the run (38ff99177, area
//   run-end), so at the limit the handoff's round ends it unanswered with turn_limit, after its reset, as upstream expects.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;
using static ChatTests;
using static HarnessTests;
using static Program;
using static SessionTests;
using Deferred = HarnessTests.Deferred;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessTurnLimitCases() =>
        TurnLimitTests.Cases("harness.turn-limit.", () => new MemoryStorage(), async path => await SqliteStorage.OpenAsync(path));
}

/// <summary>The harness.turn-limit.* cases (upstream harness-turn-limit.test.ts).</summary>
internal static class TurnLimitTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    private static readonly ToolExecutionResult NoContent = new() { Output = JsonArrayValue.Empty };

    private static ToolRegistration Tool(string name, ToolExecute execute, ToolReplay replay = ToolReplay.Unsafe) =>
        Define.Tool(new ToolRegistration { Name = name, Description = name, Execute = execute, Replay = replay });

    /// <summary>A tool-calling answer with one <c>noop</c> call.</summary>
    private static JsonObjectValue Round(string id) => FauxAssistantMessage([FauxToolCall("noop", JsonObjectValue.Empty, id)], stopReason: "toolUse");

    private static JsonObjectValue Done() => FauxAssistantMessage([FauxText("done")]);

    private sealed record Limited(ChatSetup Setup, IHarness Harness, IConversation Root);

    /// <summary>A chat with a <c>noop</c> tool and the given turn limit (upstream <c>limited</c>).</summary>
    private static async Task<Limited> Open(Func<IStorage> memory, long? maxTurns, Action<ChatSetup>? configure = null)
    {
        var setup = Setup();
        AddTool(setup.Registry, Tool("noop", (_, _, _) => Task.FromResult(NoContent)));
        configure?.Invoke(setup);
        var (harness, root) = await OpenChat(memory(), setup);
        if (maxTurns is { } limit) await root.ConfigureAsync(new AgentChange { MaxTurns = limit }, Context);
        return new Limited(setup, harness, root);
    }

    private static async Task<string?> LastKind(IConversation root) => (await AllEntries(root)).LastOrDefault()?.Kind;

    private static async Task<JsonObjectValue> LiveOf(IHarness harness, IConversation root) =>
        await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context) ?? JsonObjectValue.Empty;

    private static double? RunTurns(JsonObjectValue live) =>
        live.TryGetValue("run", out var run) && run is JsonObjectValue fields && fields.TryGetValue("turns", out var turns) && turns is JsonNumberValue count
            ? count.Value : null;

    /// <summary>toMatchObject of a settled submission: its status and reason, and its detail when given.</summary>
    private static void Settled(SubmissionRecord record, SubmissionStatus status, string? reason, string? detail, string what) =>
        Check(record.Status == status && (reason is null || record.Reason == reason) &&
            (detail is null || record.Detail is JsonStringValue text && text.Value == detail),
            $"{what}: expected {status} {reason} {detail}, actual {record.ToJson().ToJson()}");

    private static string SqlitePath() => Path.Combine(Temp("pi-durable-turns"), "session.sqlite");

    /// <param name="prefix">The case ID prefix.</param>
    /// <param name="memory">Upstream's <c>new MemoryStorage()</c>: a fresh storage for each chat.</param>
    /// <param name="openFile">Upstream's openNodeSqliteStorage: opens the storage over the file at a path.</param>
    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix, Func<IStorage> memory, Func<string, Task<IStorage>> openFile)
    {
        // #11043
        yield return Case(prefix + "ends-a-run-after-the-tool-round-that-reaches-the-limit-like-a-failure", async () =>
        {
            var (setup, harness, root) = await Open(memory, 1);
            setup.Faux.SetResponses(Round("c1"), Done());
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            Settled(await submission.WaitAsync(Context), SubmissionStatus.Unanswered, "turn_limit", "1 turn", "settled");
            Equal(1, setup.Faux.State.CallCount, "calls");
            // The transcript ends with the round's results; the generation completed, so nothing is reported.
            Equal(Entries.ToolResultEntry.Kind, await LastKind(root), "last kind");
            MatchJson($$"""{"reason":"turn_limit","inputs":[{{submission.Id.Value}}]}""", (await LiveOf(harness, root))["runFailure"], "run failure");
            var tasks = await harness.CommitAsync(tx => tx.ScanTasksAsync(new TaskQuery { ConversationId = root.Id }, 20), Context);
            var generation = tasks.Items.First(task => task.Kind == HarnessTasks.GenerationTask.Definition.Name);
            MatchJson("""{"status":"terminal","outcome":{"status":"completed"}}""", generation.State.ToJson(), "generation");
            Equal(0, setup.Reports.Count, "reports");
            await harness.CloseAsync(Context);
        });

        // #11043
        yield return Case(prefix + "counts-tool-rounds-only-a-run-answers-below-its-limit-and-an-answer-at-the-limit-settles-done", async () =>
        {
            var (setup, harness, root) = await Open(memory, 2);
            setup.Faux.SetResponses(Round("c1"), Done(), Round("c2"), Round("c3"), Done());
            Equal(SubmissionStatus.Done, (await (await root.SubmitAsync(SubmissionDraft.Input("one"), Context)).WaitAsync(Context)).Status, "first");
            // A new run starts with a fresh budget.
            var second = await (await root.SubmitAsync(SubmissionDraft.Input("two"), Context)).WaitAsync(Context);
            Settled(second, SubmissionStatus.Unanswered, "turn_limit", "2 turns", "second");
            await harness.CloseAsync(Context);
        });

        // #11043
        yield return Case(prefix + "runs-onyield-at-the-limit-and-a-continuation-starts-the-count-over", async () =>
        {
            var yields = 0;
            var (setup, harness, root) = await Open(memory, 2, chat => AddHooks(chat.Registry, HarnessTasks.GenerationTask, new GenerationHooks
            {
                OnYield = (_, _, _) => Task.FromResult<JsonValue?>(Interlocked.Increment(ref yields) == 1 ? (JsonValue)"keep going" : null),
            }));
            // Round (1 turn), answer: the continuation resets the count; then two rounds reach the limit.
            setup.Faux.SetResponses(Round("c1"), Done(), Round("c2"), Round("c3"), Done());
            var settled = await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            Settled(settled, SubmissionStatus.Unanswered, "turn_limit", null, "settled");
            Equal(1, Volatile.Read(ref yields), "yields");
            Equal(4, setup.Faux.State.CallCount, "calls");
            await harness.CloseAsync(Context);
        });

        // #11043
        yield return Case(prefix + "settles-a-steer-that-joined-the-run-at-an-earlier-round-with-turn_limit-too", async () =>
        {
            var gate = new Deferred();
            var started = new Deferred();
            var setup = Setup();
            var calls = 0;
            AddTool(setup.Registry, Tool("noop", async (_, _, _) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    started.Resolve();
                    await gate.Task;
                }
                return NoContent;
            }));
            var (harness, root) = await OpenChat(memory(), setup);
            await root.ConfigureAsync(new AgentChange { MaxTurns = 2 }, Context);
            setup.Faux.SetResponses(Round("c1"), Round("c2"), Done());
            var first = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await started.Task;
            var steer = await root.SubmitAsync(SubmissionDraft.Input("also this", WhenBusy.Steer), Context);
            gate.Resolve();
            foreach (var (submission, what) in new[] { (first, "first"), (steer, "steer") })
                Settled(await submission.WaitAsync(Context), SubmissionStatus.Unanswered, "turn_limit", "2 turns", what);
            SameJson($"[{first.Id.Value},{steer.Id.Value}]", ((JsonObjectValue)(await LiveOf(harness, root))["runFailure"])["inputs"], "inputs");
            Equal(2, setup.Faux.State.CallCount, "calls");
            await harness.CloseAsync(Context);
        });

        // #11043
        yield return Case(prefix + "leaves-items-queued-during-the-last-round-to-afterfailedrun", async () =>
        {
            foreach (var policy in new[] { AfterFailedRun.Keep, AfterFailedRun.Continue, AfterFailedRun.Withdraw })
            {
                var gate = new Deferred();
                var started = new Deferred();
                var setup = Setup();
                setup.AfterFailedRun = policy;
                AddTool(setup.Registry, Tool("noop", async (_, _, _) =>
                {
                    started.Resolve();
                    await gate.Task;
                    return NoContent;
                }));
                setup.Faux.SetResponses(Round("c1"), Done());
                var (harness, root) = await OpenChat(memory(), setup);
                await root.ConfigureAsync(new AgentChange { MaxTurns = 1 }, Context);
                var first = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
                await started.Task;
                var steer = await root.SubmitAsync(SubmissionDraft.Input("also", WhenBusy.FollowUp), Context);
                gate.Resolve();
                Equal(SubmissionStatus.Unanswered, (await first.WaitAsync(Context)).Status, $"{policy} first");
                await harness.WaitForIdleAsync(Context);
                var queued = await steer.StatusAsync(Context);
                if (policy == AfterFailedRun.Keep) Equal(SubmissionStatus.Queued, queued.Status, "keep");
                if (policy == AfterFailedRun.Continue) Equal(SubmissionStatus.Done, queued.Status, "continue");
                if (policy == AfterFailedRun.Withdraw) Settled(queued, SubmissionStatus.Unanswered, "run_failed", null, "withdraw");
                await harness.CloseAsync(Context);
            }
        });

        // #11043
        yield return Case(prefix + "applies-a-changed-limit-at-the-end-of-the-round-null-removes-it", async () =>
        {
            var setup = Setup();
            IConversation? root = null;
            var rounds = 0;
            AddTool(setup.Registry, Tool("noop", async (_, _, _) =>
            {
                // Lowered during the second round: that round ends the run.
                if (Interlocked.Increment(ref rounds) == 2) await root!.ConfigureAsync(new AgentChange { MaxTurns = 1 }, Context);
                return NoContent;
            }));
            setup.Faux.SetResponses(Round("c1"), Round("c2"), Round("c3"), Done());
            var opened = await OpenChat(memory(), setup);
            root = opened.Root;
            await root.ConfigureAsync(new AgentChange { MaxTurns = 5 }, Context);
            var settled = await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            Settled(settled, SubmissionStatus.Unanswered, "turn_limit", "2 turns", "settled");
            await root.ConfigureAsync(new AgentChange { MaxTurns = Patch<long>.Clear }, Context);
            Equal(null, (await root.AgentAsync(Context)).MaxTurns, "cleared");
            setup.Faux.SetResponses(Round("c4"), Round("c5"), Done());
            Equal(SubmissionStatus.Done, (await (await root.SubmitAsync(SubmissionDraft.Input("again"), Context)).WaitAsync(Context)).Status, "again");
            await opened.Harness.CloseAsync(Context);
        });

        // #11043
        yield return Case(prefix + "rejects-a-limit-that-is-not-an-integer-1-or-more", async () =>
        {
            var (_, harness, root) = await Open(memory, null);
            // Upstream's 1.5, NaN and Infinity have no long; 2^53 is past Number.isSafeInteger.
            foreach (var value in new long[] { 0, -1, 9_007_199_254_740_992 })
            {
                var rejected = false;
                try { await root.ConfigureAsync(new AgentChange { MaxTurns = value }, Context); }
                catch (ArgumentOutOfRangeException) { rejected = true; }
                Check(rejected, $"{value}: expected an ArgumentOutOfRangeException");
            }
            await root.ConfigureAsync(new AgentChange { MaxTurns = 3 }, Context);
            Equal(3L, (await root.AgentAsync(Context)).MaxTurns, "maxTurns");
            await harness.CloseAsync(Context);
        });

        // #11043
        yield return Case(prefix + "lets-terminate-settle-done-at-the-limit-and-writes-a-handoff-s-reset-before-ending", async () =>
        {
            var (setup, harness, root) = await Open(memory, 1, chat => AddHooks(chat.Registry, HarnessTasks.ToolTask, new ToolHooks
            {
                AfterTool = (call, result, _, _) => Task.FromResult<ToolExecutionResult?>(S(call["id"]) == "t"
                    ? result with { Control = new ToolControl { Terminate = true } }
                    : result with { Control = new ToolControl { Handoff = "fresh" } }),
            }));
            setup.Faux.SetResponses(Round("t"), Round("h"), Done());
            Equal(SubmissionStatus.Done, (await (await root.SubmitAsync(SubmissionDraft.Input("one"), Context)).WaitAsync(Context)).Status, "terminate");
            var handedOff = await (await root.SubmitAsync(SubmissionDraft.Input("two"), Context)).WaitAsync(Context);
            Settled(handedOff, SubmissionStatus.Unanswered, "turn_limit", null, "handoff");
            var resets = (await AllEntries(root)).Where(entry => Entries.ResetEntry.Is(entry)).ToList();
            Seq(["fresh"], resets.Select(entry => S(((JsonObjectValue)entry.Model![0])["content"])), "resets");
            await harness.CloseAsync(Context);
        });

        // #11043
        yield return Case(prefix + "is-resumed-by-a-continue-with-a-fresh-budget-on-the-round-s-results", async () =>
        {
            var (setup, harness, root) = await Open(memory, 1);
            string? continued = null;
            setup.Faux.SetResponses(Round("c1"), FauxStep.From(request =>
            {
                continued = request.Messages[^1] is JsonObjectValue message ? S(message["role"]) : null;
                return FauxAssistantMessage([FauxText("finished")]);
            }));
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            var resumed = await root.SubmitAsync(SubmissionDraft.Continue(), Context);
            Equal(0.0, RunTurns(await LiveOf(harness, root)), "turns");
            var settled = await resumed.WaitAsync(Context);
            if (settled.Status != SubmissionStatus.Done || settled.Answer is not { } answerId) throw new InvalidOperationException($"Unexpected {settled.Status}");
            var answer = (await AllEntries(root)).Find(entry => entry.Id == answerId);
            Equal("finished", TextOf(answer?.Model?[0]), "answer");
            Equal("toolResult", continued, "continued from");
            await harness.CloseAsync(Context);
        });

        // #11043
        yield return Case(prefix + "is-inherited-by-forks-and-children-which-may-set-their-own", async () =>
        {
            var setup = Setup();
            ConversationId? child = null;
            AddTool(setup.Registry, Tool("spawn", async (_, api, callContext) =>
            {
                child = await api.CommitAsync(async tx => (await tx.CreateConversationAsync(ConversationOwnership.Task(api.TaskId))).Id, callContext);
                return NoContent;
            }));
            setup.Faux.SetResponses(FauxAssistantMessage([FauxToolCall("spawn", JsonObjectValue.Empty, "c1")], stopReason: "toolUse"), Done());
            var (harness, root) = await OpenChat(memory(), setup);
            await root.ConfigureAsync(new AgentChange { MaxTurns = 4 }, Context);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            var spawned = await harness.ConversationAsync(child!.Value, Context);
            Equal(4L, (await spawned!.AgentAsync(Context)).MaxTurns, "child");
            var at = (await AllEntries(root))[^1].Id;
            var fork = await root.ForkAsync(at, new ConversationCreateOptions(ConversationOwnership.Ownerless), Context);
            Equal(4L, (await fork.AgentAsync(Context)).MaxTurns, "fork");
            var own = await harness.CreateConversationAsync(
                new ConversationCreateOptions(ConversationOwnership.Ownerless) { Agent = new AgentChange { MaxTurns = 2 } }, Context);
            Equal(2L, (await own.AgentAsync(Context)).MaxTurns, "own");
            await harness.CloseAsync(Context);
        });

        // #11043
        yield return KilledStorages.Case(prefix + "keeps-the-stored-count-across-a-restart", async killed =>
        {
            var path = SqlitePath();
            var setup = Setup();
            setup.Progress = new ProgressSettings { OutputIntervalMs = 0, PersistWithinMs = 0 };
            var started = new Deferred();
            var runs = 0;
            AddTool(setup.Registry, Tool("noop", async (_, _, callContext) =>
            {
                if (Interlocked.Increment(ref runs) == 2)
                {
                    started.Resolve();
                    await Aborted(callContext.CancellationToken);
                }
                return NoContent;
            }, ToolReplay.Safe));
            setup.Faux.SetResponses(Round("c1"), Round("c2"), Done());
            var (storage, kill) = killed.Killable(await openFile(path));
            var first = await OpenChat(storage, setup);
            await first.Root.ConfigureAsync(new AgentChange { MaxTurns = 2 }, Context);
            var id = (await first.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).Id;
            await started.Task;
            Equal(2.0, RunTurns(await LiveOf(first.Harness, first.Root)), "turns before the crash");
            kill();

            // The replay-safe call runs again after reopen; the stored count still ends the run at this round.
            Volatile.Write(ref runs, 0);
            var second = await OpenChat(await openFile(path), setup);
            second.Harness.Resume();
            Equal(2.0, RunTurns(await LiveOf(second.Harness, second.Root)), "turns after reopen");
            var settled = await (await second.Harness.SubmissionAsync(id, Context))!.WaitAsync(Context);
            Settled(settled, SubmissionStatus.Unanswered, "turn_limit", "2 turns", "settled");
            await second.Harness.CloseAsync(Context);
        });
    }
}
