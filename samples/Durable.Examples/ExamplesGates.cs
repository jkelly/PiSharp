// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/durable/test/examples/44-approval-gate.ts, 45-review-on-yield.ts and
// 46-after-tools-terminate.ts (Pi v1.6.0): gates before a tool runs, and hooks that start tasks. Each prints what upstream's console.log
// prints, its arguments joined with spaces: a string as it is, any other value as JSON in place of util.inspect. ScriptedModels stands in for
// the faux provider (its CallCount is `faux.state.callCount`), and pi-schema objects are their JSON Schema.
// - 44's canonicalDigest is the base64url of the arguments' JSON text, as upstream's Buffer.from(JSON.stringify(value)).toString("base64url").
// PiSharp-only:
// - The hosts wait for the state they print where upstream waits 5 ms, and ExampleTurn holds a line until the host printed its own: hooks
//   and tasks run on other threads here.
// - 45: upstream's `reviewing()` polls pi.live for `run.yield` and then waits 5 ms, which is what lets the next review task start and
//   replace `release` before the host calls it. Here each review registers its own release, and the host waits for the review it means (the
//   first, the second, ...) by number.
// - 46: upstream's `asked()` likewise relies on 5 ms for the answered task to leave `asking` and the next one to enter; here the host waits
//   for a task it has not answered yet.
// - On a file backend the storage is reopened in a plain Session and the transcript's kinds read back, as every example's read-back.
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Examples;

public static partial class Examples
{
    // 44: gates before a tool runs: an approvals extension parks a risky `bash` call until a person approves it, keyed by the arguments it
    // saw, with a deadline fixed at its first pass. A second gate (quiet hours) is asked again on every wake, since the whole `call` phase
    // runs again. Denial and expiry end the call as blocks.
    private static async Task ApprovalGateAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        const double day = 24 * 60 * 60 * 1000;
        // Date.UTC(2026, 10, 9, 9).
        double clock = new DateTimeOffset(2026, 11, 9, 9, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var bash = Define.Tool(new ToolRegistration
        {
            Name = "bash",
            Description = "Run a shell command",
            Parameters = TextSchema("command"),
            Execute = (arguments, _, _) => Task.FromResult(new ToolExecutionResult { Output = TextOutput($"ran: {Arg(arguments, "command")}") }),
        });

        // ─── The approvals extension ──────────────────────────────────────────────────

        var approval = Signals.DefineSignal(new SignalDefinition
        {
            Name = "approvals.decision",
            Version = 1,
            // pi-schema's obj({ allow: bool() }).
            Schema = (JsonObjectValue)JsonValue.Parse(
                """{"type":"object","properties":{"allow":{"type":"boolean"}},"required":["allow"],"additionalProperties":false}"""),
        });
        static bool IsRisky(JsonObjectValue arguments) => arguments["command"] is JsonStringValue command && command.Value.StartsWith("rm ", StringComparison.Ordinal);
        // A digest of the arguments; other arguments, another question. (Sorting keys is left out: there is one argument.)
        static string CanonicalDigest(JsonValue value) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(value.ToJson())).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        // What shows the question keeps its own record, by task ID and key.
        var questions = new ConcurrentDictionary<string, (TaskId TaskId, string Command)>();

        var approvals = new Extension
        {
            Name = "approvals",
            Hooks =
            [
                Define.Hook(HarnessTasks.ToolTask, new ToolHooks
                {
                    BeforeTool = async (call, api, ctx) =>
                    {
                        var arguments = (JsonObjectValue)call["arguments"];
                        if (call["name"] is not JsonStringValue { Value: "bash" } || !IsRisky(arguments)) return null;
                        var key = CanonicalDigest(arguments);
                        var state = await api.ReadSignalAsync(approval, key, ctx);
                        // One deadline per question, fixed at the first pass: later passes (wakes, registry changes) keep it.
                        var until = ((JsonNumberValue)await api.MemoAsync($"approvals.until.{key}", api.Now() + day, ctx)).Value;
                        if (state is null)
                        {
                            questions[key] = (api.TaskId, Arg(arguments, "command"));
                            return new BeforeToolDecision(Sleep: new GateSleep(until, ImmutableArray.Create(approval.Key(key))));
                        }
                        questions.TryRemove(key, out _);
                        if (state.Expired) return new BeforeToolDecision(Block: "No approval within a day");
                        return ((JsonObjectValue)state.Value!)["allow"] is JsonBooleanValue { Value: true } ? null : new BeforeToolDecision(Block: "Not approved");
                    },
                }),
            ],
        };

        // A second gate, installed first: asked again on every wake, it reads the clock each time.
        var quietChecks = 0;
        var quietHours = new Extension
        {
            Name = "quiet-hours",
            Hooks =
            [
                Define.Hook(HarnessTasks.ToolTask, new ToolHooks
                {
                    BeforeTool = (_, api, _) =>
                    {
                        Interlocked.Increment(ref quietChecks);
                        var hour = DateTimeOffset.FromUnixTimeMilliseconds((long)api.Now()).UtcDateTime.Hour;
                        return Task.FromResult(hour is >= 22 or < 6 ? new BeforeToolDecision(Block: "Quiet hours: no commands at night") : null);
                    },
                }),
            ],
        };

        // ─── Host ─────────────────────────────────────────────────────────────────────

        static ScriptedReply Command(string text) => ScriptedReply.ToolCall("bash", JsonValue.Object([new("command", text)]));
        var models = new ScriptedModels(
            Command("rm -rf build"), "Cleaned.",
            Command("rm -rf dist"), "Not cleaned.",
            Command("rm -rf cache"), "Nobody answered.");
        var registry = Registry.Create();
        foreach (var extension in new[] { new Extension { Name = "tools", Tools = [bash] }, quietHours, approvals }) registry.Install(extension);
        var harness = await Harness.OpenAsync(await open(),
            new HarnessOptions { Models = models, Registry = registry, Now = () => Volatile.Read(ref clock), OnWake = _ => { } }, Context);
        var root = await harness.RootAsync(Context, new AgentChange { Model = Faux });

        async Task<(string Key, TaskId TaskId, string Command)> Asked()
        {
            await UntilAsync(() => !questions.IsEmpty);
            await UntilAsync(async () => SlotStatus(await FirstSlotAsync(harness, root.Id)) == "sleeping" && !harness.Wake().Active);
            var (key, question) = questions.Single();
            return (key, question.TaskId, question.Command);
        }
        async Task Run(string input, Func<string, TaskId, Task> answer)
        {
            Interlocked.Exchange(ref quietChecks, 0);
            var submission = await root.SubmitAsync(SubmissionDraft.Input(input), Context);
            var question = await Asked();
            var slot = await FirstSlotAsync(harness, root.Id);
            await LogAsync(output, $"asked to approve \"{question.Command}\"; the call is {SlotStatus(slot)}; wake:", WakeJson(harness.Wake()));
            await answer(question.Key, question.TaskId);
            await submission.WaitAsync(Context);
            var result = (await LastToolResultAsync(root, 20)).Text.Replace("\n", " ", StringComparison.Ordinal);
            await LogAsync(output, $"  result: {result} (quiet-hours gate asked {Volatile.Read(ref quietChecks)} times)");
        }
        static JsonObjectValue Allow(bool allow) => JsonValue.Object([new("allow", allow)]);

        // 1. Approved: the whole call phase runs again, both gates pass, and the command runs.
        await Run("Clean build", async (key, taskId) =>
            await LogAsync(output, "  approve:", ResultName(await harness.SignalAsync(taskId, approval, key, Allow(true), Context))));
        // 2. Denied: a block.
        await Run("Clean dist", async (key, taskId) =>
            await LogAsync(output, "  deny:", ResultName(await harness.SignalAsync(taskId, approval, key, Allow(false), Context))));
        // 3. Nobody answers within a day: the deadline wakes it, and the gate blocks.
        await Run("Clean cache", (_, _) =>
        {
            Volatile.Write(ref clock, Volatile.Read(ref clock) + day);
            harness.Resume();
            return Task.CompletedTask;
        });
        await harness.CloseAsync(Context);

        await ReopenAsync(open, output, async reopened =>
            await output.WriteLineAsync("transcript: " + Kinds(await TranscriptAsync(reopened, root.Id, 40))));
    }

    /// <summary>The user and assistant messages of a transcript, as 45 prints them: <c>&gt; input</c> and the answer's text.</summary>
    private static async Task<List<string>> SpokenAsync(IConversation root)
    {
        var page = await root.EntriesAsync(new EntryQueryBounds { Order = ScanOrder.Ascending }, 50, null, Context);
        var lines = new List<string>();
        foreach (var entry in page.Items)
        {
            if (entry.Model is not { Count: > 0 } model || model[0] is not JsonObjectValue message) continue;
            var role = message.TryGetValue("role", out var value) && value is JsonStringValue name ? name.Value : null;
            if (role == "user") lines.Add("> " + (message["content"] is JsonStringValue content ? content.Value : Json(message["content"])));
            if (role == "assistant") lines.Add(AssistantText(message));
        }
        return lines;
    }

    private static string AssistantText(JsonObjectValue message) => string.Concat(((JsonArrayValue)message["content"]).OfType<JsonObjectValue>()
        .Where(part => part["type"] is JsonStringValue { Value: "text" }).Select(part => ((JsonStringValue)part["text"]).Value));

    // 45: an `onYield` hook that asks a review task whether an answer stands. While the review runs, the answer is in the transcript but the
    // run's input stays open, so whoever waits on it sees only a reviewed answer. The review's `{ continue }` sends the agent back to work
    // in the same run; a user message queued meanwhile wins over it.
    private static async Task ReviewOnYieldAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        // The review: reads the answer the hook asked about from `run.yield`, and sends the agent back when it is too short.
        var releases = new ConcurrentQueue<TaskCompletionSource>();
        var review = Tasks.DefineTask(new TaskDefinition
        {
            Name = "example.review",
            Version = 1,
            Initial = _ => JsonValue.Object([new("phase", "review")]),
            Phases = new Dictionary<string, TaskPhaseHandler>
            {
                ["review"] = async (_, runtime, ctx) =>
                {
                    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    releases.Enqueue(release);
                    await release.Task.WaitAsync(ctx.CancellationToken);
                    var live = await runtime.SnapshotAsync(Live.LiveDoc, runtime.ConversationId, ctx);
                    var yielded = (JsonObjectValue)((JsonObjectValue)live!["run"])["yield"];
                    var entry = await runtime.EntryAsync(new EntryId((long)((JsonNumberValue)yielded["answer"]).Value), ctx);
                    var message = entry?.Model is { Count: > 0 } model ? model[0] as JsonObjectValue : null;
                    var text = message is not null && message["role"] is JsonStringValue { Value: "assistant" } ? AssistantText(message) : "";
                    var decision = text.Length < 20 ? new YieldDecision("Too short: explain your reasoning.") : new YieldDecision();
                    await output.WriteLineAsync($"  [review] \"{text}\" -> {Json(decision.ToJson())}");
                    await runtime.CommitAsync((_, _) => NextTaskState.Completed(decision.ToJson()), ctx);
                },
            },
            Abort = (_, runtime, ctx) => runtime.CommitAsync((_, _) => NextTaskState.Aborted(), ctx),
        });

        // A review of every answer, a continuation's too.
        var reviewer = new Extension
        {
            Name = "reviewer",
            Tasks = [review],
            Hooks =
            [
                Define.Hook(HarnessTasks.GenerationTask, new GenerationHooks
                {
                    OnYield = (_, _, _) => Task.FromResult<OnYieldResult?>(OnYieldResult.Tasks([Define.HookTask(review, JsonValue.Null)])),
                }),
            ],
        };

        var models = new ScriptedModels(
            "Yes.",
            "Yes: the build passed and the tests are green, so it can ship.",
            "No.",
            "Answering the queued question instead.");
        var registry = Registry.Create();
        registry.Install(reviewer);
        var harness = await Harness.OpenAsync(await open(), new HarnessOptions { Models = models, Registry = registry }, Context);
        var root = await harness.RootAsync(Context, new AgentChange { Model = Faux });
        var released = 0;
        // The review the host means next, once it runs: its release.
        async Task<Action> Reviewing()
        {
            var wanted = released + 1;
            await UntilAsync(() => releases.Count >= wanted);
            released = wanted;
            var release = releases.ElementAt(wanted - 1);
            return () => release.TrySetResult();
        }

        // 1. The review sends the agent back; the input settles with the reviewed answer.
        var first = await root.SubmitAsync(SubmissionDraft.Input("Can we ship?"), Context);
        var release = await Reviewing();
        await LogAsync(output, "while reviewing, the input is:", StatusName((await first.StatusAsync(Context)).Status));
        release();
        (await Reviewing())();
        await LogAsync(output, "input:", StatusName((await first.WaitAsync(Context)).Status));
        await LogAsync(output, Json(JsonValue.Array((await SpokenAsync(root)).Select(line => (JsonValue)line))));

        // 2. A follow-up queued during the review wins over the review's continuation.
        var second = await root.SubmitAsync(SubmissionDraft.Input("Is it Friday?"), Context);
        release = await Reviewing();
        await root.SubmitAsync(SubmissionDraft.Input("Skip that: what is 2 + 2?", WhenBusy.FollowUp), Context);
        release();
        await second.WaitAsync(Context);
        (await Reviewing())();
        await root.WaitForIdleAsync(Context);
        await LogAsync(output, Json(JsonValue.Array((await SpokenAsync(root)).Skip(4).Select(line => (JsonValue)line))));
        await harness.CloseAsync(Context);

        await ReopenAsync(open, output, async reopened =>
            await output.WriteLineAsync("transcript: " + Kinds(await TranscriptAsync(reopened, root.Id, 40))));
    }

    // 46: an `afterTools` hook that asks a person, after every tool round, whether the agent should go on. Its task parks on the person's
    // answer, so the Session needs no process meanwhile; "go on" answers the run with a steer the next request sees, "stop" ends the run
    // with `{ terminate: true }`, as a round whose calls all asked to terminate.
    private static async Task AfterToolsTerminateAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        var choice = Signals.DefineSignal(new SignalDefinition
        {
            Name = "example.go-on",
            Version = 1,
            // pi-schema's obj({ choice: oneOf(["go on", "stop"]) }).
            Schema = (JsonObjectValue)JsonValue.Parse(
                """{"type":"object","properties":{"choice":{"enum":["go on","stop"]}},"required":["choice"],"additionalProperties":false}"""),
        });
        var asking = new ConcurrentDictionary<TaskId, bool>();

        // Asks, sleeps until the person answers, then steers or ends the run.
        var askToGoOn = Tasks.DefineTask(new TaskDefinition
        {
            Name = "example.ask-to-go-on",
            Version = 1,
            Initial = _ => JsonValue.Object([new("phase", "ask")]),
            Phases = new Dictionary<string, TaskPhaseHandler>
            {
                ["ask"] = (_, runtime, ctx) =>
                {
                    asking[runtime.TaskId] = true;
                    return runtime.CommitAsync((_, _) => NextTaskState.Sleeping(JsonValue.Object([new("phase", "answered")]), null, [choice.Key("choice")]), ctx);
                },
                ["answered"] = async (_, runtime, ctx) =>
                {
                    asking.TryRemove(runtime.TaskId, out var _);
                    var state = await runtime.ReadSignalAsync(choice, "choice", ctx);
                    var chosen = state?.Value is JsonObjectValue value ? ((JsonStringValue)value["choice"]).Value : "stop";
                    var handle = (await runtime.ConversationAsync(runtime.ConversationId, ctx))!;
                    await runtime.CommitAsync(async (tx, _) =>
                    {
                        if (chosen == "stop") return (TaskState?)NextTaskState.Completed(new AfterToolsDecision(Terminate: true).ToJson());
                        // A steer in the task's terminal commit: the round's boundary places it before the next request.
                        await handle.SubmitAsync(tx, SubmissionDraft.Input("The person says: go on.", WhenBusy.Steer));
                        return NextTaskState.Completed(JsonValue.Null);
                    }, ctx);
                },
            },
            Abort = (_, runtime, ctx) => runtime.CommitAsync((_, _) => NextTaskState.Aborted(), ctx),
        });

        var step = Define.Tool(new ToolRegistration
        {
            Name = "step",
            Description = "Do one step of the work",
            Parameters = TextSchema("what"),
            Execute = (arguments, _, _) => Task.FromResult(new ToolExecutionResult { Output = TextOutput($"did {Arg(arguments, "what")}") }),
        });

        static ScriptedReply CallStep(string what) => ScriptedReply.ToolCall("step", JsonValue.Object([new("what", what)]));
        var models = new ScriptedModels(CallStep("step 1"), CallStep("step 2"), "Not reached.");
        var registry = Registry.Create();
        registry.Install(new Extension
        {
            Name = "checkpoints",
            Tools = [step],
            Tasks = [askToGoOn],
            Hooks =
            [
                Define.Hook(HarnessTasks.GenerationTask, new GenerationHooks
                {
                    AfterTools = (_, _, _, _) => Task.FromResult<IReadOnlyList<HookTask>?>([Define.HookTask(askToGoOn, JsonValue.Null)]),
                }),
            ],
        });
        var harness = await Harness.OpenAsync(await open(), new HarnessOptions { Models = models, Registry = registry, OnWake = _ => { } }, Context);
        var root = await harness.RootAsync(Context, new AgentChange { Model = Faux });
        var answered = new HashSet<TaskId>();
        async Task<TaskId> Asked()
        {
            await UntilAsync(() => asking.Keys.Any(id => !answered.Contains(id)));
            await UntilAsync(() => !harness.Wake().Active);
            var id = asking.Keys.Single(id => !answered.Contains(id));
            answered.Add(id);
            return id;
        }
        static JsonObjectValue Choose(string value) => JsonValue.Object([new("choice", value)]);

        var submission = await root.SubmitAsync(SubmissionDraft.Input("Do the work, step by step"), Context);
        var task = await Asked();
        await LogAsync(output, "round 1 done, asking the person; wake:", WakeJson(harness.Wake()));
        await LogAsync(output, "answer:", ResultName(await harness.SignalAsync(task, choice, "choice", Choose("go on"), Context)));
        task = await Asked();
        await LogAsync(output, "round 2 done, asking again; wake:", WakeJson(harness.Wake()));
        await LogAsync(output, "answer:", ResultName(await harness.SignalAsync(task, choice, "choice", Choose("stop"), Context)));
        var settled = await submission.WaitAsync(Context);
        await LogAsync(output, "run:", StatusName(settled.Status), "with requests:",
            models.CallCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var page = await root.EntriesAsync(new EntryQueryBounds { Order = ScanOrder.Ascending }, 50, null, Context);
        foreach (var entry in page.Items)
        {
            if (entry.Model is not { Count: > 0 } model || model[0] is not JsonObjectValue message) continue;
            var role = message.TryGetValue("role", out var value) && value is JsonStringValue name ? name.Value : null;
            if (role == "user") await LogAsync(output, ">", message["content"] is JsonStringValue content ? content.Value : Json(message["content"]));
            if (role == "toolResult") await LogAsync(output, "  tool:", ResultText(message));
        }
        await harness.CloseAsync(Context);

        await ReopenAsync(open, output, async reopened =>
            await output.WriteLineAsync("transcript: " + Kinds(await TranscriptAsync(reopened, root.Id, 40))));
    }
}
