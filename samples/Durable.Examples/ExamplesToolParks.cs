// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/durable/test/examples/40-tool-waits-for-webhook.ts, 41-tool-polls.ts,
// 42-parallel-children.ts and 43-subagent-waits.ts (Pi v1.6.0): tools that park. Each prints what upstream's console.log prints, its
// arguments joined with spaces: a string as it is, any other value as JSON in place of util.inspect. ScriptedModels stands in for the faux
// provider, and pi-schema objects are their JSON Schema. A park is the result that carries it (ToolPark.Sleeping, ToolPark.Waiting), and
// `executeTool(..., { key, wait: false })` is StartToolAsync.
// PiSharp-only:
// - 40: upstream leaves the first Harness open while a second opens the same SQLite file ("the process dies here"). Here the first one is
//   closed while the call is cut off after it triggered the build, as 12-recovery closes its Harness in the middle of a step: the stored
//   state is the same, an unfinished replay-safe call, and nothing more of the first process reaches storage. On memory storage the example
//   uses a temporary SQLite file, as upstream always does. Its "wake while parked" deadline is on the system clock, as upstream's.
// - The hosts wait for the state they print (a slot's status, an inactive wake) where upstream waits 1 or 5 ms, and ExampleTurn holds a
//   tool's line until the host printed the delivery that woke it: tools run on other threads here.
// - On a file backend the storage is reopened in a plain Session and the transcript's kinds read back, as every example's read-back.
using System.Collections.Concurrent;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;

namespace PiSharp.Durable.Examples;

public static partial class Examples
{
    private const double Hour = 60 * 60 * 1000;

    /// <summary>A JavaScript number as <c>console.log</c> prints it, for the values here: an integer without a fraction.</summary>
    private static string Number(double value) => value == Math.Floor(value) && Math.Abs(value) < 1e15
        ? ((long)value).ToString(System.Globalization.CultureInfo.InvariantCulture)
        : value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    private static JsonArrayValue TextOutput(string text) => JsonValue.Array([JsonValue.Object([new("type", "text"), new("text", text)])]);

    private static string Arg(JsonValue arguments, string name) => ((JsonStringValue)((JsonObjectValue)arguments)[name]).Value;

    /// <summary>The first tool slot of a conversation's <c>pi.live</c>, if any.</summary>
    private static async Task<JsonObjectValue?> FirstSlotAsync(IHarness harness, ConversationId conversation, string slots = "tools") =>
        (await SlotsAsync(harness, conversation, slots)).FirstOrDefault();

    private static async Task<List<JsonObjectValue>> SlotsAsync(IHarness harness, ConversationId conversation, string slots = "tools") =>
        await harness.SnapshotAsync(Live.LiveDoc, conversation, Context) is { } live && live.TryGetValue(slots, out var found) && found is JsonArrayValue list
            ? [.. list.OfType<JsonObjectValue>()] : [];

    private static string SlotStatus(JsonObjectValue? slot) =>
        slot is not null && slot.TryGetValue("status", out var status) && status is JsonStringValue name ? name.Value : "undefined";

    /// <summary>The text of the newest tool result of a conversation.</summary>
    private static async Task<(string Text, JsonObjectValue Message)> LastToolResultAsync(IConversation root, int limit = 10)
    {
        var page = await root.EntriesAsync(new EntryQueryBounds(), limit, null, Context);
        var message = (JsonObjectValue)page.Items.First(entry => Entries.ToolResultEntry.Is(entry)).Model![0];
        return (ResultText(message), message);
    }

    // 40: a tool that starts a CI build and parks until the build's webhook arrives, holding no process meanwhile. A crash before the park
    // reruns the tool, which finds its build again through its own idempotency key; Stop while it is parked runs the tool's `cancel`, which
    // cancels the build.
    private static async Task ToolWaitsForWebhookAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        // ─── The outside world: a CI service and the host's webhook registry ──────────

        // Builds by idempotency key: triggering twice with one key starts one build.
        var builds = new List<(string Key, string Id, string Ref, bool Cancelled)>();
        var gate = new Lock();
        string Trigger(string gitRef, string idempotencyKey)
        {
            lock (gate)
            {
                var index = builds.FindIndex(build => build.Key == idempotencyKey);
                if (index < 0)
                {
                    builds.Add((idempotencyKey, $"build-{builds.Count + 1}", gitRef, false));
                    index = builds.Count - 1;
                }
                return builds[index].Id;
            }
        }
        void CancelWhere(Func<(string Key, string Id, string Ref, bool Cancelled), bool> match)
        {
            lock (gate)
                for (var index = 0; index < builds.Count; index++)
                    if (match(builds[index])) builds[index] = builds[index] with { Cancelled = true };
        }
        string BuildIds() { lock (gate) return Json(JsonValue.Array(builds.Select(build => (JsonValue)build.Id))); }

        // "When the webhook for `match` arrives, signal this key to this task": durable and idempotent in a real host.
        var expected = new ConcurrentDictionary<string, (TaskId TaskId, SignalToken Token, string Key)>();

        // ─── The tool ─────────────────────────────────────────────────────────────────

        var ciResult = Signals.DefineSignal(new SignalDefinition
        {
            Name = "example.ci-result",
            Version = 1,
            // pi-schema's obj({ status: oneOf(["passed", "failed"]), logUrl: str() }).
            Schema = (JsonObjectValue)JsonValue.Parse(
                """{"type":"object","properties":{"status":{"enum":["passed","failed"]},"logUrl":{"type":"string"}},"required":["status","logUrl"],"additionalProperties":false}"""),
        });

        // Set by the host below to make the first attempt "crash" after it triggered the build.
        var crashAfterTrigger = true;
        var died = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var runCi = Define.Tool(new ToolRegistration
        {
            Name = "run_ci",
            Description = "Run CI on a git ref and report the result",
            Parameters = TextSchema("ref"),
            // A rerun after a crash is safe: it finds its build again by its idempotency key.
            Replay = ToolReplay.Safe,
            Execute = async (arguments, api, ctx) =>
            {
                // The result, the deadline's expiry, or nothing yet (a fresh call, or a rerun after a crash before the park).
                var state = await api.ReadSignalAsync(ciResult, "done", ctx);
                if (state?.Value is JsonObjectValue value)
                {
                    var status = ((JsonStringValue)value["status"]).Value;
                    return new ToolExecutionResult
                    {
                        Output = TextOutput($"CI {status}: {((JsonStringValue)value["logUrl"]).Value}"), IsError = status != "passed",
                    };
                }
                if (state is not null)
                {
                    var build = api.Checkpoint is JsonObjectValue parked ? ((JsonStringValue)parked["buildId"]).Value : "undefined";
                    return new ToolExecutionResult { Output = TextOutput($"CI build {build} never reported"), IsError = true };
                }
                // The tool's own idempotency key, stable across reruns: memos are first-writer-wins.
                var runId = ((JsonStringValue)await api.MemoAsync("ci.run", Guid.NewGuid().ToString(), ctx)).Value;
                // Register before triggering, so a fast webhook already finds this task.
                expected[runId] = (api.TaskId, ciResult, "done");
                var buildId = Trigger(Arg(arguments, "ref"), runId);
                await api.DetailsAsync(JsonValue.Object([new("buildId", buildId)]), ctx);
                if (Volatile.Read(ref crashAfterTrigger))
                {
                    died.TrySetResult();
                    // The process dies here; this attempt never parks.
                    await Task.Delay(Timeout.Infinite, ctx.CancellationToken);
                }
                return ToolPark.Sleeping(api.Now() + 2 * Hour, [ciResult.Key("done")], JsonValue.Object([new("buildId", buildId)]));
            },
            Cancel = async (_, api, ctx) =>
            {
                // Parked: the checkpoint names the build. Cut off before a park was stored: the idempotency key finds it.
                var parked = api.Checkpoint is JsonObjectValue checkpoint ? ((JsonStringValue)checkpoint["buildId"]).Value : null;
                await output.WriteLineAsync($"  [cancel] cancelling {parked ?? "the build of this call's key"}");
                if (parked is not null) CancelWhere(build => build.Id == parked);
                else if (await api.MemoAsync("ci.run", ctx) is JsonStringValue key) CancelWhere(build => build.Key == key.Value);
            },
        });

        // ─── Host ─────────────────────────────────────────────────────────────────────

        static ScriptedReply CallCi(string gitRef) => ScriptedReply.ToolCall("run_ci", JsonValue.Object([new("ref", gitRef)]));
        var models = new ScriptedModels(CallCi("main"), "CI is green.", CallCi("feature"));
        var registry = Registry.Create();
        registry.Install(new Extension { Name = "ci", Tools = [runCi] });

        // The process restarts, so the storage must survive close: a memory run uses a temporary SQLite file, as upstream does.
        var storage = await open();
        string? directory = null;
        if (storage is MemoryStorage)
        {
            await storage.CloseAsync(Context);
            directory = ExampleDirectory("pi-durable-ci-");
            open = async () => await SqliteStorage.OpenAsync(Path.Combine(directory, "session.sqlite"));
            storage = await open();
        }
        Task<IHarness> Open(IStorage opened) =>
            Harness.OpenAsync(opened, new HarnessOptions { Models = models, Registry = registry, OnWake = _ => { } }, Context);
        async Task<TaskId> Parked(IHarness harness, IConversation root)
        {
            await UntilAsync(async () => SlotStatus(await FirstSlotAsync(harness, root.Id)) == "sleeping");
            return new TaskId((long)((JsonNumberValue)(await FirstSlotAsync(harness, root.Id))!["taskId"]).Value);
        }

        // 1. The first process starts the build and dies before the park is stored.
        var first = await Open(storage);
        var firstRoot = await first.RootAsync(Context, new AgentChange { Model = Faux });
        first.Resume();
        var submissionId = (await firstRoot.SubmitAsync(SubmissionDraft.Input("Run CI on main"), Context)).Id;
        await died.Task;
        await LogAsync(output, "builds after the first attempt:", BuildIds());
        await first.CloseAsync(Context);

        // 2. Another process opens the same database. The replay-safe call runs again and finds its build by its key.
        Volatile.Write(ref crashAfterTrigger, false);
        var harness = await Open(await open());
        var root = await harness.RootAsync(Context);
        harness.Resume();
        await Parked(harness, root);
        await LogAsync(output, "builds after the rerun:", BuildIds());
        await LogAsync(output, "parked slot:", Json(await FirstSlotAsync(harness, root.Id)));
        await UntilAsync(() => !harness.Wake().Active);
        // Parked work holds no invocation: only its deadline needs a wakeup.
        await LogAsync(output, "wake while parked:", WakeJson(harness.Wake()), "deadline in",
            Number(Math.Round((harness.Wake().At!.Value - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) / Hour)), "h");

        // 3. The webhook arrives; the host delivers it, and the call runs again with the result. Retries are duplicates.
        var match = expected.Keys.Single();
        async Task<string> Deliver(JsonObjectValue payload)
        {
            var target = expected[match];
            return ResultName(await harness.SignalAsync(target.TaskId, target.Token, target.Key, payload, Context));
        }
        var passed = JsonValue.Object([new("status", "passed"), new("logUrl", "https://ci/1")]);
        await LogAsync(output, "webhook:", await Deliver(passed));
        await LogAsync(output, "retry:", await Deliver(passed));
        await (await harness.SubmissionAsync(submissionId, Context))!.WaitAsync(Context);
        await LogAsync(output, "result:", (await LastToolResultAsync(root)).Text);

        // 4. A second build, stopped while parked: `cancel` cancels it, and the call ends aborted.
        await root.SubmitAsync(SubmissionDraft.Input("Run CI on feature"), Context);
        await Parked(harness, root);
        await root.AbortAsync(Context);
        string Builds()
        {
            lock (gate)
                return Json(JsonValue.Array(builds.Select(build =>
                    (JsonValue)JsonValue.Object([new("id", build.Id), new("ref", build.Ref), new("cancelled", build.Cancelled)]))));
        }
        await LogAsync(output, "builds:", Builds());
        await LogAsync(output, "result:", (await LastToolResultAsync(root)).Text);

        await harness.CloseAsync(Context);
        if (directory is null)
            await ReopenAsync(open, output, async reopened => await output.WriteLineAsync("transcript: " + Kinds(await TranscriptAsync(reopened, root.Id, 20))));
        else
        {
            // Upstream: rm(directory, { recursive: true, force: true }).
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // 41: a tool that polls a slow job without holding a process: it parks on time between polls, with a backoff, keeping its poll count in
    // the park's checkpoint. A host that wakes the process itself sees only the next deadline.
    private static async Task ToolPollsAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        // The Harness clock, moved by the host's "alarm" below.
        double clock = 0;

        // A slow job service: starting twice with one idempotency key starts one job, done on its fourth status check.
        var checks = 0;
        var startedJobs = new ConcurrentDictionary<string, string>();
        string Start(string idempotencyKey) => startedJobs.GetOrAdd(idempotencyKey, _ => $"job-{startedJobs.Count + 7}");
        string Status() => Interlocked.Increment(ref checks) >= 4 ? "done" : "running";

        var waitForJob = Define.Tool(new ToolRegistration
        {
            Name = "wait_for_job",
            Description = "Start a job and wait until it is done",
            Parameters = TextSchema("name"),
            Replay = ToolReplay.Safe,
            Execute = async (_, api, ctx) =>
            {
                var checkpoint = api.Checkpoint as JsonObjectValue;
                // The first run starts the job under its own idempotency key, memoized first, so a rerun after a crash before the first park
                // finds the same job.
                var id = checkpoint is not null
                    ? ((JsonStringValue)checkpoint["id"]).Value
                    : Start(((JsonStringValue)await api.MemoAsync("job.key", Guid.NewGuid().ToString(), ctx)).Value);
                var status = Status();
                var polls = (checkpoint is null ? 0 : (int)((JsonNumberValue)checkpoint["polls"]).Value) + 1;
                await output.WriteLineAsync($"  [poll {polls} at {Number(Volatile.Read(ref clock) / 1000)}s] {id} is {status}");
                if (status == "done") return new ToolExecutionResult { Output = TextOutput($"{id} done after {polls} polls") };
                // Back off: 1s, 2s, 4s, ... up to a minute.
                var delayMs = Math.Min((checkpoint is null ? 500 : ((JsonNumberValue)checkpoint["delayMs"]).Value) * 2, 60_000);
                return ToolPark.Sleeping(api.Now() + delayMs,
                    checkpoint: JsonValue.Object([new("id", id), new("polls", polls), new("delayMs", delayMs)]));
            },
        });

        var models = new ScriptedModels(
            ScriptedReply.ToolCall("wait_for_job", JsonValue.Object([new("name", "nightly")])),
            "The nightly job is done.");

        // The host's alarm: `onWake` says when the Session next needs a process; the "alarm" fires by moving the clock there.
        var wakes = new ConcurrentQueue<Wake>();
        var registry = Registry.Create();
        registry.Install(new Extension { Name = "jobs", Tools = [waitForJob] });
        var harness = await Harness.OpenAsync(await open(),
            new HarnessOptions { Models = models, Registry = registry, Now = () => Volatile.Read(ref clock), OnWake = wakes.Enqueue }, Context);
        var root = await harness.RootAsync(Context, new AgentChange { Model = Faux });
        var submission = await root.SubmitAsync(SubmissionDraft.Input("Run the nightly job"), Context);

        var settled = submission.WaitAsync(Context);
        while (!settled.IsCompleted)
        {
            await Task.Delay(5);
            var wake = harness.Wake();
            // Parked between polls: nothing runs, only the next poll time is set.
            if (!wake.Active && wake.At is { } at && at > Volatile.Read(ref clock))
            {
                await LogAsync(output, "asleep until", Number(at / 1000), "s:", WakeJson(wake));
                Volatile.Write(ref clock, at);
                harness.Resume();
            }
        }
        await settled;
        var (text, message) = await LastToolResultAsync(root);
        await LogAsync(output, "result:", text);
        await LogAsync(output, "durationMs (first run to result, on the Harness clock):", Json(message["durationMs"]));
        await LogAsync(output, "onWake calls:", wakes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await harness.CloseAsync(Context);

        await ReopenAsync(open, output, async reopened =>
            await output.WriteLineAsync("transcript: " + Kinds(await TranscriptAsync(reopened, root.Id, 20))));
    }

    // 42: a tool that starts two nested calls without waiting for them and parks until either ends, so the Session holds no process while
    // they run. Each wake adds the ended calls to its checkpoint; the last one collects every result by key.
    private static async Task ParallelChildrenAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        // A research call parks until a (simulated) external researcher reports back.
        var report = Signals.DefineSignal(new SignalDefinition { Name = "example.report", Version = 1, Schema = TextSchema("text") });
        var research = Define.Tool(new ToolRegistration
        {
            Name = "research",
            Description = "Research a topic",
            Parameters = TextSchema("topic"),
            Replay = ToolReplay.Safe,
            Execute = async (arguments, api, ctx) =>
            {
                var state = await api.ReadSignalAsync(report, "report", ctx);
                if (state?.Value is JsonObjectValue value)
                    return new ToolExecutionResult { Output = TextOutput($"{Arg(arguments, "topic")}: {((JsonStringValue)value["text"]).Value}") };
                return ToolPark.Sleeping(signals: [report.Key("report")]);
            },
        });

        var runs = 0;
        var turn = new ExampleTurn();
        var researchBoth = Define.Tool(new ToolRegistration
        {
            Name = "research_both",
            Description = "Research two topics in parallel",
            // pi-schema's obj({ x: str(), y: str() }).
            Parameters = (JsonObjectValue)JsonValue.Parse(
                """{"type":"object","properties":{"x":{"type":"string"},"y":{"type":"string"}},"required":["x","y"],"additionalProperties":false}"""),
            Replay = ToolReplay.Safe,
            Execute = async (arguments, api, ctx) =>
            {
                // Keys name the calls across wakes: the same key finds the same call again.
                var a = await api.StartToolAsync("research", JsonValue.Object([new("topic", Arg(arguments, "x"))]), ctx, new NestedCallOptions { Key = "a" });
                var b = await api.StartToolAsync("research", JsonValue.Object([new("topic", Arg(arguments, "y"))]), ctx, new NestedCallOptions { Key = "b" });
                var done = new List<TaskId>();
                if (api.Checkpoint is JsonObjectValue checkpoint && checkpoint["done"] is JsonArrayValue ids)
                    done.AddRange(ids.Select(id => new TaskId((long)((JsonNumberValue)id).Value)));
                if (api.Wake is { Reason: WakeReasonKind.Tasks } wake)
                    foreach (var id in wake.Ended)
                        if (!done.Contains(id)) done.Add(id);
                await turn.WaitAsync(ctx.CancellationToken);
                await output.WriteLineAsync(
                    $"  [research_both] run with wake {(api.Wake is null ? "undefined" : Json(api.Wake.ToJson()))}, {done.Count} of 2 done");
                Interlocked.Increment(ref runs);
                var open = new[] { a, b }.Where(call => !done.Contains(call.TaskId)).ToList();
                if (open.Count > 0)
                    return ToolPark.Waiting(open.Select(call => call.TaskId), JoinPolicy.Any,
                        JsonValue.Object([new("done", JsonValue.Array(done.Select(id => (JsonValue)id.ToJson())))]));
                var results = await Task.WhenAll(a.ResultAsync(ctx), b.ResultAsync(ctx));
                static string Text(NestedToolExecutionResult result) => result.StructuredOutput is JsonStringValue text ? text.Value : Json(result.StructuredOutput);
                return new ToolExecutionResult { Output = TextOutput($"{Text(results[0])}\n{Text(results[1])}") };
            },
        });

        var models = new ScriptedModels(
            ScriptedReply.ToolCall("research_both", JsonValue.Object([new("x", "durable objects"), new("y", "sqlite")])),
            "Both researched.");
        var registry = Registry.Create();
        registry.Install(new Extension { Name = "research", Tools = [research, researchBoth] });
        var harness = await Harness.OpenAsync(await open(), new HarnessOptions { Models = models, Registry = registry, OnWake = _ => { } }, Context);
        var root = await harness.RootAsync(Context, new AgentChange { Model = Faux });
        var submission = await root.SubmitAsync(SubmissionDraft.Input("Research both"), Context);

        // Wait until both nested calls are parked; their tool call waits on them.
        Task<List<JsonObjectValue>> Nested() => SlotsAsync(harness, root.Id, "nestedTools");
        async Task<bool> Waiting() => SlotStatus(await FirstSlotAsync(harness, root.Id)) == "waiting";
        await UntilAsync(async () => (await Nested()).Count(slot => SlotStatus(slot) == "sleeping") == 2 && await Waiting());
        await UntilAsync(() => !harness.Wake().Active);
        var tools = await SlotsAsync(harness, root.Id);
        static JsonValue Names(IEnumerable<JsonObjectValue> slots, string name) =>
            JsonValue.Array(slots.Select(slot => (JsonValue)$"{((JsonStringValue)slot[name]).Value} {SlotStatus(slot)}"));
        await LogAsync(output, "tool:", Json(Names(tools, "name")), "nested:", Json(Names(await Nested(), "callId")));
        await LogAsync(output, "wake while everything is parked:", WakeJson(harness.Wake()));

        var nested = await Nested();
        static TaskId TaskOf(JsonObjectValue slot) => new((long)((JsonNumberValue)slot["taskId"]).Value);
        static JsonObjectValue Text(string text) => JsonValue.Object([new("text", text)]);
        turn.Hold();
        await LogAsync(output, "report b:", ResultName(await harness.SignalAsync(TaskOf(nested[1]), report, "report", Text("embedded, single file"), Context)));
        turn.Release();
        // The parent wakes for b, runs again, and waits on a.
        await UntilAsync(async () => Volatile.Read(ref runs) >= 2 && await Waiting());
        turn.Hold();
        await LogAsync(output, "report a:",
            ResultName(await harness.SignalAsync(TaskOf(nested[0]), report, "report", Text("one per object, with storage"), Context)));
        turn.Release();
        await submission.WaitAsync(Context);
        await LogAsync(output, "result:\n" + (await LastToolResultAsync(root)).Text);
        await harness.CloseAsync(Context);

        await ReopenAsync(open, output, async reopened =>
            await output.WriteLineAsync("transcript: " + Kinds(await TranscriptAsync(reopened, root.Id, 20))));
    }

    // 43: a subagent tool that parks until its child conversation answers, notified by the child's submission when it settles. The child
    // itself parks on a person's answer, so while the person thinks, every task is parked and the Session needs no process.
    private static async Task SubagentWaitsAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        // The child's tool: it asks a person and parks until the answer comes.
        var answer = Signals.DefineSignal(new SignalDefinition { Name = "example.answer", Version = 1, Schema = TextSchema("text") });
        var asked = new ConcurrentDictionary<TaskId, string>();
        var askPerson = Define.Tool(new ToolRegistration
        {
            Name = "ask_person",
            Description = "Ask the person a question and wait for the answer",
            Parameters = TextSchema("question"),
            Replay = ToolReplay.Safe,
            Execute = async (arguments, api, ctx) =>
            {
                var state = await api.ReadSignalAsync(answer, "answer", ctx);
                if (state?.Value is JsonObjectValue value) return new ToolExecutionResult { Output = TextOutput(((JsonStringValue)value["text"]).Value) };
                asked[api.TaskId] = Arg(arguments, "question");
                return ToolPark.Sleeping(signals: [answer.Key("answer")]);
            },
        });

        // The parent's tool: delegate to a subagent and park until its submission settles.
        Extension? subagent = null;
        subagent = new Extension
        {
            Name = "subagent",
            Tools =
            [
                Define.Tool(new ToolRegistration
                {
                    Name = "subagent",
                    Description = "Delegate a task to a subagent and get its answer back",
                    Parameters = TextSchema("task"),
                    Replay = ToolReplay.Safe,
                    Execute = async (arguments, api, ctx) =>
                    {
                        var state = await api.ReadSignalAsync(Signals.SubmissionSettled, "answered", ctx);
                        if (state?.Value is JsonObjectValue settled)
                        {
                            var status = ((JsonStringValue)settled["status"]).Value;
                            if (status != "done" || settled["type"] is JsonStringValue { Value: "write" })
                                return new ToolExecutionResult { Output = TextOutput($"Subagent ended: {status}"), IsError = true };
                            // Any entry by ID, read in a commit without writes.
                            var answerId = new EntryId((long)((JsonNumberValue)settled["answer"]).Value);
                            var entry = await api.CommitAsync(tx => tx.GetEntryAsync(answerId), ctx);
                            var message = (JsonObjectValue)entry!.Model![0];
                            return new ToolExecutionResult
                            {
                                Output = JsonValue.Array(((JsonArrayValue)message["content"]).OfType<JsonObjectValue>()
                                    .Where(part => part["type"] is JsonStringValue { Value: "text" }).Select(part => (JsonValue)part)),
                            };
                        }
                        // The parent's own selection without this extension, computed before the commit.
                        var extensions = (await api.AgentAsync(ctx)).Extensions.Where(extension => extension.Name != subagent!.Name).ToArray();
                        var child = await api.CommitAsync(async tx =>
                        {
                            var existing = (await tx.ScanConversationsAsync(new ConversationQuery { OwnerTaskId = api.TaskId }, 1)).Items.FirstOrDefault();
                            if (existing is not null) return existing.Id;
                            var created = await tx.CreateConversationAsync(ConversationOwnership.Task(api.TaskId));
                            await Agents.ConfigureAsync(tx, created.Id, new AgentChange { Extensions = ExtensionSelection.Only(extensions) });
                            return created.Id;
                        }, ctx);
                        var handle = (await api.ConversationAsync(child, ctx))!;
                        // The request ID makes a rerun find the same submission; `notify` signals this call when it settles.
                        await handle.SubmitAsync(SubmissionDraft.Input(Arg(arguments, "task"), requestId: $"subagent:{api.TaskId}",
                            notify: new SubmissionNotify(api.TaskId, "answered")), ctx);
                        return ToolPark.Sleeping(signals: [Signals.SubmissionSettled.Key("answered")]);
                    },
                }),
            ],
        };

        var models = new ScriptedModels(
            // Parent: delegate.
            ScriptedReply.ToolCall("subagent", JsonValue.Object([new("task", "Find out whether we may deploy today")])),
            // Child: ask the person, then answer.
            ScriptedReply.ToolCall("ask_person", JsonValue.Object([new("question", "May we deploy today?")])),
            "The person says yes, after 5 pm.",
            // Parent: final answer.
            "We may deploy today, after 5 pm.");
        var registry = Registry.Create();
        registry.Install(new Extension { Name = "people", Tools = [askPerson] });
        registry.Install(subagent);
        var harness = await Harness.OpenAsync(await open(), new HarnessOptions { Models = models, Registry = registry, OnWake = _ => { } }, Context);
        var root = await harness.RootAsync(Context, new AgentChange { Model = Faux });
        var submission = await root.SubmitAsync(SubmissionDraft.Input("Can we deploy today?"), Context);

        await UntilAsync(() => !asked.IsEmpty);
        await UntilAsync(() => !harness.Wake().Active);
        var parentSlot = await FirstSlotAsync(harness, root.Id);
        await LogAsync(output, "parent call:", parentSlot is null ? "undefined" : ((JsonStringValue)parentSlot["name"]).Value, SlotStatus(parentSlot));
        await LogAsync(output, "the person is asked:", Json(JsonValue.Array(asked.Values.Select(question => (JsonValue)question))));
        await LogAsync(output, "wake while everything waits for the person:", WakeJson(harness.Wake()));

        // The person answers through whatever showed the question; the host delivers it.
        var question = asked.Keys.Single();
        await LogAsync(output, "answer:",
            ResultName(await harness.SignalAsync(question, answer, "answer", JsonValue.Object([new("text", "Yes, after 5 pm")]), Context)));
        await LogAsync(output, "parent:", StatusName((await submission.WaitAsync(Context)).Status));
        await LogAsync(output, "subagent result:", (await LastToolResultAsync(root)).Text);
        await harness.CloseAsync(Context);

        await ReopenAsync(open, output, async reopened =>
            await output.WriteLineAsync("transcript: " + Kinds(await TranscriptAsync(reopened, root.Id, 20))));
    }
}
