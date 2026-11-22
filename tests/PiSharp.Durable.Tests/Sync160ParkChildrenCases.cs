// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/durable/test/harness-park-children.test.ts. Work a parked tool owns: nested
// calls started without waiting, tasks created under keys, which children a restart keeps, and submissions that signal a task when they
// settle. Verbatim apart from the adaptations in HarnessSupport.cs/ChatSupport.cs, and these:
// - pi-schema builders are their JSON Schema: obj({ topic: str() }) and obj({ text: str() }) are closed objects with the property required.
// - `api.executeTool(name, args, ctx, { key, wait: false })` is api.StartToolAsync(name, args, ctx, new NestedCallOptions { Key }), whose
//   NestedToolCall has TaskId and ResultAsync; `api.createTask(task, input, { ownership, key }, ctx)` is the CreateTaskAsync overload that
//   takes the key. A park is the result that carries it (ToolPark.Sleeping, ToolPark.Waiting).
// - waitFor is Eventually. `String(result.structuredOutput)` is the string itself ("undefined" for none).
// - "park on their handles with policy any ...": upstream waits for the caller's slot to show `waiting` between the two signals, which it
//   shows from its first park on; its single thread makes the caller's second park land before the second signal anyway. On the thread
//   pool the second nested call could end before the caller is reserved for the first one, and the two would arrive in one wake. The case
//   therefore first waits until the caller ran again (its second wake is recorded), then for the slot as upstream does. The recorded wakes
//   are compared as JSON, an undefined wake as null.
// - "return the task a key names ...": `seen` holds a TaskId or a message, as upstream's holds a number or a string; `typeof first ===
//   "number"` is `first is TaskId`.
// - openNodeSqliteStorage(path) is SqliteStorage.OpenAsync over a file in a fresh temporary directory (Temp); upstream's afterEach removal
//   of the directories has no counterpart.
// - "abandons the parked children of a replay-unsafe call cut off while it ran": upstream's process death leaves the first Harness open
//   while another opens the same SQLite file, and closes the first one at the end. Here the first Harness fails (the internal
//   Session.Fail, as harness.tools-recovery.* and harness.deferred-progress.* crash), so nothing more of it reaches Storage; it is closed
//   by that, so the case does not close it again. `ctx.abortSignal` is the call context's CancellationToken.
// - The afterTool hook's `hooked.parent !== undefined` is the hook call's JSON having `parent`; returning undefined is a null result.
// - `storage.commits` is ControlledStorage.Commits over a MemoryStorage, read by index up to CommitCount as the Harness may still commit;
//   `toHaveProperty([name])` of a task write's memos is Memos.ContainsKey(name), and Object.keys(memos) is Memos.Keys.
// - `17 as never` and the partial records cast to never are plain JSON values.
// Every case of the upstream file is ported.
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
    private static IEnumerable<(string, Func<Task>)> Sync160ParkChildrenCases() => ParkChildrenTests.Cases();
}

/// <summary>The sync160.park-children.* cases (upstream harness-park-children.test.ts).</summary>
internal static class ParkChildrenTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    /// <summary>pi-schema's <c>obj({ [name]: str() })</c>: a closed object with the property required.</summary>
    private static JsonObjectValue StringProperty(string name) =>
        O($$$"""{"type":"object","properties":{"{{{name}}}":{"type":"string"}},"required":["{{{name}}}"],"additionalProperties":false}""");

    private static readonly SignalToken Done =
        Signals.DefineSignal(new SignalDefinition { Name = "test.done", Version = 1, Schema = StringProperty("text") });

    private static readonly JsonObjectValue Topic = StringProperty("topic");

    private static string SqlitePath() => Path.Combine(Temp("pi-durable-children"), "session.sqlite");

    private static ToolRegistration Tool(string name, ToolExecute execute, Func<ToolRegistration, ToolRegistration>? extra = null)
    {
        var tool = Define.Tool(new ToolRegistration { Name = name, Description = name, Parameters = Topic, Execute = execute });
        return extra is null ? tool : extra(tool);
    }

    private static JsonObjectValue Call(string name, JsonObjectValue? args = null) =>
        FauxAssistantMessage([FauxToolCall(name, args ?? Obj(("topic", "")), "c1")], stopReason: "toolUse");

    private static readonly JsonObjectValue DoneAnswer = FauxAssistantMessage("done");

    private static List<JsonObjectValue> Results(IEnumerable<EntryRecord> entries) =>
        [.. entries.Where(entry => entry.Kind == "pi.tool-result").Select(entry => (JsonObjectValue)entry.Model![0])];

    /// <summary>The text of a result message.</summary>
    private static string Text(JsonObjectValue? message) => message is null ? "" :
        string.Join("|", ((JsonArrayValue)message["content"]).Cast<JsonObjectValue>().Select(item => S(item["type"]) == "text" ? S(item["text"]) : ""));

    /// <summary><c>String(value)</c> of a nested result's structured output.</summary>
    private static string Str(JsonValue? value) => value switch
    {
        null => "undefined",
        JsonStringValue text => text.Value,
        _ => value.ToJson(),
    };

    private static async Task<OpenedChat> Open(ChatSetup setup, IStorage? storage = null)
    {
        var opened = await OpenChat(storage ?? new MemoryStorage(), setup);
        opened.Harness.Resume();
        return opened;
    }

    /// <summary>A nested tool that parks on <c>Done</c> under the key <c>done</c> until it holds a value, and answers with its text
    /// (upstream <c>research</c>).</summary>
    private static readonly ToolRegistration Research = Tool("research", async (args, api, ctx) =>
    {
        var state = await api.ReadSignalAsync(Done, "done", ctx);
        if (state is { Expired: false, Value: JsonObjectValue value })
            return new ToolExecutionResult { Output = JsonValue.Array(FauxText($"{S(((JsonObjectValue)args)["topic"])}: {S(value["text"])}")) };
        return (ToolExecutionResult)ToolPark.Sleeping(signals: [Done.Key("done")]);
    }, tool => tool with { Replay = ToolReplay.Safe });

    /// <summary>The nested calls of the root conversation that are parked, by call ID (upstream <c>parkedNested</c>).</summary>
    private static async Task<Dictionary<string, TaskId>> ParkedNested(IHarness harness, int count)
    {
        var parked = new Dictionary<string, TaskId>();
        await Eventually(async () =>
        {
            var live = await harness.SnapshotAsync(Live.LiveDoc, ConversationId.Root, Context);
            var slots = live is not null && live.TryGetValue("nestedTools", out var listed) && listed is JsonArrayValue array
                ? array.OfType<JsonObjectValue>()
                : [];
            parked = slots.Where(slot => slot.TryGetValue("status", out var status) && status is JsonStringValue { Value: "sleeping" })
                .ToDictionary(slot => S(slot["callId"]), slot => new TaskId((long)N(slot["taskId"])));
            return parked.Count == count;
        });
        return parked;
    }

    /// <summary>The status of the first slot of <c>pi.live.tools</c>; null without one (upstream <c>...?.tools?.[0]?.status</c>).</summary>
    private static async Task<string?> FirstToolStatus(IHarness harness, ConversationId conversationId)
    {
        var live = await harness.SnapshotAsync(Live.LiveDoc, conversationId, Context);
        return live is not null && live.TryGetValue("tools", out var tools) && tools is JsonArrayValue { Count: > 0 } slots &&
            slots[0] is JsonObjectValue slot && slot.TryGetValue("status", out var status) && status is JsonStringValue text
            ? text.Value
            : null;
    }

    /// <summary>A task that completes with null in its one phase (upstream <c>Idle</c> and <c>Child</c>).</summary>
    private static DurableTask Completing(string name) => Tasks.DefineTask(new TaskDefinition
    {
        Name = name,
        Version = 1,
        Initial = _ => O("""{"phase":"run"}"""),
        Phases = new Dictionary<string, TaskPhaseHandler> { ["run"] = (_, runtime, ctx) => runtime.CommitAsync((_, _) => Completed(null), ctx) },
        Abort = (_, runtime, ctx) => runtime.CommitAsync((_, _) => AbortedWith("test"), ctx),
    });

    /// <summary>Sleeps on <c>SubmissionSettled</c> under <c>settled</c>, then completes with the settled record's status (upstream
    /// <c>Waiter</c>).</summary>
    private static readonly DurableTask Waiter = Tasks.DefineTask(new TaskDefinition
    {
        Name = "test.waiter",
        Version = 1,
        Initial = _ => O("""{"phase":"sleep"}"""),
        Phases = new Dictionary<string, TaskPhaseHandler>
        {
            ["sleep"] = (_, runtime, ctx) => runtime.CommitAsync((_, _) =>
                NextTaskState.Sleeping(O("""{"phase":"woke"}"""), null, [Signals.SubmissionSettled.Key("settled")]), ctx),
            ["woke"] = async (_, runtime, ctx) =>
            {
                var state = await runtime.ReadSignalAsync(Signals.SubmissionSettled, "settled", ctx);
                JsonValue? status = state is { Expired: false, Value: JsonObjectValue settled } ? settled["status"] : null;
                await runtime.CommitAsync((_, _) => Completed(status), ctx);
            },
        },
        Abort = (_, runtime, ctx) => runtime.CommitAsync((_, _) => AbortedWith("test"), ctx),
    });

    private static Task<TaskId> StartOwned(IConversation root, DurableTask task) =>
        root.CommitAsync(tx => tx.CreateTaskAsync(task, JsonValue.Null, new TaskOptions(TaskOwnership.Conversation)), Context);

    /// <summary>The batches committed so far (upstream <c>storage.commits</c>). Read by index: the Harness may commit meanwhile, and a
    /// list's enumerator would notice.</summary>
    private static List<IReadOnlyList<StorageWrite>> Batches(ControlledStorage storage)
    {
        var count = storage.CommitCount;
        var batches = new List<IReadOnlyList<StorageWrite>>(count);
        for (var index = 0; index < count; index++) batches.Add(storage.Commits[index]);
        return batches;
    }

    /// <summary>Whether <paramref name="batch"/> writes submission <paramref name="id"/> as <c>done</c>.</summary>
    private static bool SettlesDone(IReadOnlyList<StorageWrite> batch, SubmissionId id) =>
        batch.Any(write => write is SubmissionWrite { Value: var record } && record.Id == id && record.Status == SubmissionStatus.Done);

    internal static IEnumerable<(string, Func<Task>)> Cases()
    {
        const string prefix = "sync160.park-children.";

        yield return Case(prefix + "nested-calls-without-waiting.park-on-their-handles-with-policy-any-and-collect-the-results-by-key-across-wakes", async () =>
        {
            var setup = Setup();
            AddTool(setup.Registry, Research);
            var wakes = new SyncList<JsonValue?>();
            AddTool(setup.Registry, Tool("both", async (_, api, ctx) =>
            {
                wakes.Add(api.Wake?.ToJson());
                var a = await api.StartToolAsync("research", Obj(("topic", "x")), ctx, new NestedCallOptions { Key = "a" });
                var b = await api.StartToolAsync("research", Obj(("topic", "y")), ctx, new NestedCallOptions { Key = "b" });
                var done = new List<TaskId>();
                if (api.Checkpoint is JsonObjectValue checkpoint && checkpoint.TryGetValue("done", out var stored) && stored is JsonArrayValue ids)
                    done.AddRange(ids.Select(id => new TaskId((long)N(id))));
                if (api.Wake is { Reason: WakeReasonKind.Tasks } wake)
                    foreach (var id in wake.Ended)
                        if (!done.Contains(id)) done.Add(id);
                var open = new[] { a, b }.Where(nested => !done.Contains(nested.TaskId)).ToList();
                if (open.Count > 0)
                {
                    return (ToolExecutionResult)ToolPark.Waiting(open.Select(nested => nested.TaskId), JoinPolicy.Any,
                        Obj(("done", JsonValue.Array(done.Select(id => (JsonValue)id.ToJson())))));
                }
                var results = await Task.WhenAll(a.ResultAsync(ctx), b.ResultAsync(ctx));
                return new ToolExecutionResult
                {
                    Output = JsonValue.Array(FauxText($"{Str(results[0].StructuredOutput)} + {Str(results[1].StructuredOutput)}")),
                };
            }, tool => tool with { Replay = ToolReplay.Safe }));
            setup.Faux.SetResponses(Call("both"), DoneAnswer);
            var (harness, root) = await Open(setup);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            var parked = await ParkedNested(harness, 2);
            var (first, second) = (parked["c1/a"], parked["c1/b"]);
            await Eventually(() => !harness.Wake().Active);
            await harness.SignalAsync(second, Done, "done", Obj(("text", "bee")), Context);
            // Adapted (see the file header): the caller ran again for the first nested call before the second one is signalled.
            await Eventually(() => wakes.Count == 2);
            await Eventually(async () => await FirstToolStatus(harness, root.Id) == "waiting");
            await harness.SignalAsync(first, Done, "done", Obj(("text", "ay")), Context);
            Equal(SubmissionStatus.Done, (await submission.WaitAsync(Context)).Status, "status");
            Equal("x: ay + y: bee", Text(Results(await AllEntries(root)).FirstOrDefault()), "result");
            SameJson($$"""[null,{"reason":"tasks","ended":[{{second.Value}}]},{"reason":"tasks","ended":[{{first.Value}}]}]""",
                JsonValue.Array(wakes.Snapshot().Select(wake => wake ?? JsonValue.Null)), "wakes");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "nested-calls-without-waiting.need-explicit-keys-after-a-wake-for-nested-calls-and-tasks-alike", async () =>
        {
            var setup = Setup();
            AddTool(setup.Registry, Research);
            var idle = Completing("test.idle");
            AddTask(setup.Registry, idle);
            var errors = new SyncList<string>();
            AddTool(setup.Registry, Tool("woken", async (_, api, ctx) =>
            {
                if (api.Wake is null) return (ToolExecutionResult)ToolPark.Sleeping(until: 0);
                try
                {
                    await api.StartToolAsync("research", Obj(("topic", "x")), ctx);
                }
                catch (Exception error)
                {
                    errors.Add(error.Message);
                }
                try
                {
                    await api.CreateTaskAsync(idle, JsonValue.Null, new TaskOptions(TaskOwnership.Task(api.TaskId)), ctx);
                }
                catch (Exception error)
                {
                    errors.Add(error.Message);
                }
                return new ToolExecutionResult { Output = JsonArrayValue.Empty };
            }));
            setup.Faux.SetResponses(Call("woken"), DoneAnswer);
            var (harness, root) = await Open(setup);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            Seq(["Tool call c1 needs explicit keys after a wake", "Tool call c1 needs explicit keys after a wake"], errors.Snapshot(), "errors");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "tasks-created-under-a-key.return-the-task-a-key-names-and-refuse-another-kind-other-input-or-a-nested-calls-key", async () =>
        {
            var setup = Setup();
            AddTool(setup.Registry, Research);
            var child = Completing("test.keyed");
            AddTask(setup.Registry, child);
            // A TaskId, or the message of what was refused.
            var seen = new SyncList<object>();
            AddTool(setup.Registry, Tool("keyed", async (_, api, ctx) =>
            {
                var own = new TaskOptions(TaskOwnership.Task(api.TaskId));
                async Task Attempt(Func<Task<object>> run)
                {
                    try
                    {
                        seen.Add(await run());
                    }
                    catch (Exception error)
                    {
                        seen.Add(error.Message);
                    }
                }
                await Attempt(async () => await api.CreateTaskAsync(child, Obj(("n", 1)), own, "k", ctx));
                await Attempt(async () => await api.CreateTaskAsync(child, Obj(("n", 1)), own, "k", ctx));
                await Attempt(async () => await api.CreateTaskAsync(child, Obj(("n", 2)), own, "k", ctx));
                await api.StartToolAsync("research", Obj(("topic", "x")), ctx, new NestedCallOptions { Key = "n" });
                await Attempt(async () => await api.CreateTaskAsync(child, Obj(("n", 1)), own, "n", ctx));
                await Attempt(async () => await api.StartToolAsync("research", Obj(("topic", "x")), ctx, new NestedCallOptions { Key = "k" }));
                return new ToolExecutionResult { Output = JsonArrayValue.Empty };
            }));
            setup.Faux.SetResponses(Call("keyed"), DoneAnswer);
            var (harness, root) = await Open(setup);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            var all = seen.Snapshot();
            Check(all.Count == 5, $"attempts: {all.Count}");
            Check(all[0] is TaskId, $"first is a task ID: {all[0]}");
            Check(Equals(all[0], all[1]), $"the key names the same task again: {all[0]}, {all[1]}");
            Seq([
                "Key k of tool call c1 was already used for another call or task",
                "Key n of tool call c1 was already used for another call or task",
                "Nested call c1/k was already made with another tool or other arguments",
            ], all.Skip(2).Select(item => item as string ?? $"not refused: {item}"), "refused");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "children-after-a-restart.keeps-the-children-of-a-parked-replay-unsafe-call", async () =>
        {
            var path = SqlitePath();
            var setup = Setup();
            AddTool(setup.Registry, Research);
            AddTool(setup.Registry, Tool("unsafe", async (_, api, ctx) =>
            {
                var nested = await api.StartToolAsync("research", Obj(("topic", "x")), ctx, new NestedCallOptions { Key = "a" });
                if (api.Wake is null) return (ToolExecutionResult)ToolPark.Waiting([nested.TaskId], JoinPolicy.AllSettled);
                return new ToolExecutionResult { Output = JsonValue.Array(FauxText(Str((await nested.ResultAsync(ctx)).StructuredOutput))) };
            }));
            setup.Faux.SetResponses(Call("unsafe"), DoneAnswer);
            var first = await Open(setup, await SqliteStorage.OpenAsync(path));
            var submissionId = (await first.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).Id;
            var parked = await ParkedNested(first.Harness, 1);
            await first.Harness.CloseAsync(Context);
            var opened = await Open(setup, await SqliteStorage.OpenAsync(path));
            var child = parked["c1/a"];
            await Eventually(() => !opened.Harness.Wake().Active);
            // Not abandoned: its owner was parked, not cut off while running.
            Equal<bool?>(false, (await opened.Harness.GetTaskAsync(child, Context))?.AbortRequested, "abort requested");
            await opened.Harness.SignalAsync(child, Done, "done", Obj(("text", "kept")), Context);
            Equal(SubmissionStatus.Done, (await (await opened.Harness.SubmissionAsync(submissionId, Context))!.WaitAsync(Context)).Status, "status");
            Equal("x: kept", Text(Results(await AllEntries(opened.Root)).FirstOrDefault()), "result");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "children-after-a-restart.abandons-the-parked-children-of-a-replay-unsafe-call-cut-off-while-it-ran", async () =>
        {
            var path = SqlitePath();
            var setup = Setup();
            AddTool(setup.Registry, Research);
            var nestedResults = new SyncList<string>();
            AddHooks(setup.Registry, HarnessTasks.ToolTask, new ToolHooks
            {
                AfterTool = (hooked, result, _, _) =>
                {
                    if (hooked.ContainsKey("parent"))
                        foreach (var diagnostic in result.Diagnostics ?? []) nestedResults.Add(diagnostic.Message);
                    return Task.FromResult<ToolExecutionResult?>(null);
                },
            });
            var started = new Deferred();
            AddTool(setup.Registry, Tool("unsafe", async (_, api, ctx) =>
            {
                await api.StartToolAsync("research", Obj(("topic", "x")), ctx, new NestedCallOptions { Key = "a" });
                started.Resolve();
                await Aborted(ctx.CancellationToken);
                return new ToolExecutionResult { Output = JsonArrayValue.Empty };
            }));
            setup.Faux.SetResponses(Call("unsafe"), DoneAnswer);
            var crashed = await Open(setup, await SqliteStorage.OpenAsync(path));
            var submissionId = (await crashed.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).Id;
            await started.Task;
            var parked = await ParkedNested(crashed.Harness, 1);
            // The process dies here: another one opens the same database. Adapted (see the file header): the first Harness fails.
            ((Session)crashed.Harness).Fail(new InvalidOperationException("crash"));
            await crashed.Harness.Closed;
            var next = await Open(setup, await SqliteStorage.OpenAsync(path));
            Equal(SubmissionStatus.Done, (await (await next.Harness.SubmissionAsync(submissionId, Context))!.WaitAsync(Context)).Status, "status");
            var child = await next.Harness.WaitForTaskAsync(parked["c1/a"], Context);
            Equal<TaskAbortReason?>(TaskAbortReason.Restart, child.AbortReason, "abort reason");
            MatchJson("""{"status":"aborted"}""", OutcomeJson(child), "outcome");
            Seq(["Tool research was parked: its caller ended with a restart"], nestedResults.Snapshot(), "nested results");
            var stored = await next.Harness.SnapshotAsync(Live.LiveDoc, next.Root.Id, Context);
            Check(stored is null || !stored.ContainsKey("nestedTools"), "no nested slots: " + stored?.ToJson());
            await next.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "submissions-that-notify-a-task.deliver-submission-settled-in-the-commit-that-settles-the-submission", async () =>
        {
            var setup = Setup();
            AddTask(setup.Registry, Waiter);
            var gate = new Deferred();
            setup.Faux.SetResponses(new FauxResponseFactory(async _ =>
            {
                await gate.Task;
                return FauxAssistantMessage("answer");
            }));
            var storage = new ControlledStorage(new MemoryStorage());
            var (harness, root) = await Open(setup, storage);
            var waiter = await StartOwned(root, Waiter);
            await Eventually(async () => (await harness.GetTaskAsync(waiter, Context))?.State.Status == DurableTaskStatus.Sleeping);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go", notify: new SubmissionNotify(waiter, "settled")), Context);
            gate.Resolve();
            Equal(SubmissionStatus.Done, (await submission.WaitAsync(Context)).Status, "status");
            SameJson("""{"status":"completed","result":"done"}""", OutcomeJson(await harness.WaitForTaskAsync(waiter, Context)), "outcome");
            // One batch settles the submission and stores the signal in the waiter.
            var settling = Batches(storage).FirstOrDefault(batch => SettlesDone(batch, submission.Id));
            var signalled = settling?.OfType<TaskWrite>().FirstOrDefault(write => write.Value.Id == waiter);
            Check(signalled?.Value.Memos?.ContainsKey("pi.signal.pi.submission-settled/settled") == true,
                "the settling batch stores the signal in the waiter: " + (signalled?.Value.Memos?.ToJson() ?? "no write of the waiter"));
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "submissions-that-notify-a-task.deliver-it-for-a-submission-created-settled-before-the-task-ran-which-then-wakes-at-once", async () =>
        {
            var setup = Setup();
            var (harness, root) = await Open(setup);
            // Blocked until its definition is installed, so it has not run when the write settles at its admission.
            var waiter = await StartOwned(root, Waiter);
            var write = await root.SubmitAsync(SubmissionDraft.Write(new EntryDraft { Kind = "test.note", Data = "hi" },
                notify: new SubmissionNotify(waiter, "settled")), Context);
            Equal(SubmissionStatus.Done, (await write.StatusAsync(Context)).Status, "status");
            Equal<double?>(null, (await harness.GetTaskAsync(waiter, Context))?.StartedAt, "not started");
            AddTask(setup.Registry, Waiter);
            SameJson("""{"status":"completed","result":"done"}""", OutcomeJson(await harness.WaitForTaskAsync(waiter, Context)), "outcome");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "submissions-that-notify-a-task.compose-in-one-commit-two-queued-writes-placed-together-signal-one-task-under-two-keys", async () =>
        {
            var setup = Setup();
            var gate = new Deferred();
            setup.Faux.SetResponses(new FauxResponseFactory(async _ =>
            {
                await gate.Task;
                return FauxAssistantMessage("answer");
            }));
            var storage = new ControlledStorage(new MemoryStorage());
            var (harness, root) = await Open(setup, storage);
            var two = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.two-keys",
                Version = 1,
                Initial = _ => O("""{"phase":"sleep"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["sleep"] = (_, runtime, ctx) => runtime.CommitAsync((_, _) => NextTaskState.Sleeping(O("""{"phase":"woke"}"""), null,
                        [Signals.SubmissionSettled.Key("one"), Signals.SubmissionSettled.Key("two")]), ctx),
                    ["woke"] = (_, runtime, ctx) => runtime.CommitAsync((_, _) => Completed(null), ctx),
                },
                Abort = (_, runtime, ctx) => runtime.CommitAsync((_, _) => AbortedWith("test"), ctx),
            });
            AddTask(setup.Registry, two);
            var waiter = await StartOwned(root, two);
            await Eventually(async () => (await harness.GetTaskAsync(waiter, Context))?.State.Status == DurableTaskStatus.Sleeping);
            // Busy: the writes, which the model sees, queue until the run's final boundary places both.
            var run = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            Task<ISubmission> Note(string key) => root.SubmitAsync(SubmissionDraft.Write(
                new EntryDraft { Kind = "test.note", Model = JsonValue.Array(Obj(("role", "user"), ("content", key), ("timestamp", 0))) },
                notify: new SubmissionNotify(waiter, key)), Context);
            var first = await Note("one");
            var second = await Note("two");
            gate.Resolve();
            await run.WaitAsync(Context);
            SubmissionStatus[] statuses = [(await first.WaitAsync(Context)).Status, (await second.WaitAsync(Context)).Status];
            Seq([SubmissionStatus.Done, SubmissionStatus.Done], statuses, "statuses");
            var placing = Batches(storage).FirstOrDefault(batch => SettlesDone(batch, second.Id));
            // Both placed in this batch, and the waiter written once: one transaction composed both deliveries.
            Check(placing is not null && SettlesDone(placing, first.Id), "one batch places both writes");
            var writes = placing?.OfType<TaskWrite>().Where(write => write.Value.Id == waiter).ToList() ?? [];
            Equal(1, writes.Count, "writes of the waiter");
            var memos = writes.FirstOrDefault()?.Value.Memos?.Keys.ToList() ?? [];
            Seq(["pi.signal.pi.submission-settled/one", "pi.signal.pi.submission-settled/two"], memos, "memos");
            await harness.WaitForTaskAsync(waiter, Context);
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "submissions-that-notify-a-task.refuse-a-delivery-of-submission-settled-that-is-no-settled-record", async () =>
        {
            var setup = Setup();
            AddTask(setup.Registry, Waiter);
            var (harness, root) = await Open(setup);
            var waiter = await StartOwned(root, Waiter);
            Equal(SignalResult.Invalid, await harness.SignalAsync(waiter, Signals.SubmissionSettled, "settled", (JsonValue)17, Context), "a number");
            // A done input without its entry and answer is no settled record either.
            var incomplete = O("""{"id":2,"conversationId":1,"type":"input","status":"done"}""");
            Equal(SignalResult.Invalid, await harness.SignalAsync(waiter, Signals.SubmissionSettled, "settled", incomplete, Context), "a partial record");
            var badEntry = O("""{"id":2,"conversationId":1,"type":"input","status":"unanswered","reason":"x","entry":"e1"}""");
            Equal(SignalResult.Invalid, await harness.SignalAsync(waiter, Signals.SubmissionSettled, "settled", badEntry, Context), "a bad entry");
            await harness.AbortTaskAsync(waiter, Context);
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "submissions-that-notify-a-task.settle-without-failing-when-the-task-to-notify-is-gone", async () =>
        {
            var setup = Setup();
            setup.Faux.SetResponses(FauxAssistantMessage("answer"));
            var (harness, root) = await Open(setup);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go", notify: new SubmissionNotify(new TaskId(999_999), "settled")), Context);
            Equal(SubmissionStatus.Done, (await submission.WaitAsync(Context)).Status, "status");
            await harness.CloseAsync(Context);
        });
    }
}
