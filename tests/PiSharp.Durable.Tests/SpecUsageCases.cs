// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/spec-usage.test.ts. The usage examples of docs/spec.md,
// compile-checked: each block is written in C# against the ported API, with the names the spec leaves to the application declared in
// SpecNames below; SpecUsage.Examples() is never called, because those names have no values. Adaptations:
// - TypeScript's type-level check (expectTypeOf(examples).returns.toHaveProperty("sequences")) is a reflection check of Examples()' return
//   type; "compiles" is this file compiling.
// - Spec names declared with `declare` are members of SpecNames that throw; pi-ai messages and tool calls are wire JSON; a pi-schema schema
//   is its JSON Schema object.
// - An onYield hook returns an OnYieldResult (GenerationHooks.OnYield; Pi v1.6.0): upstream's { continue: input } is
//   OnYieldResult.Continue(input), which a user input converts to implicitly, and { tasks } is OnYieldResult.Tasks(...). requestSecondPass
//   returns Task<OnYieldResult?>, null for upstream's undefined.
// - HarnessSettings getters are overridden properties of a HarnessSettings subclass.
// - `delete live.generation` and `escaped!.generation = undefined` are DraftObject.Delete("generation").
// - Upstream's `UserSettings implements HarnessSettings` stores the manager in a field; here it is a primary-constructor parameter.
// Pi v1.5.0 sync (area view, f3255de76): the "transcript" example of Section 9.3 (a client's transcript over the windowed view) is
// v1.5.0's. Pi v1.5.0 sync (area vocabulary, 5f312dac4): its watch listener takes the tagged call, as upstream's onValue(...) wraps it:
// a WatchValueCall renders, any other call is skipped.
// Pi v1.6.0 sync (faac585884ae2c013a6b1400f3e9e77f10b34bac): added the examples of Section 7.2 "work at the end of a run" (RunTests,
// TestsAfterEdits) and "gates" (Approval, Approvals), Section 7.3 "tools that park" (research, CiResult, runCi, ParkingSubagent) and
// Section 5.7 "a task that asks a person" (Answer, AskPerson), in upstream's order, their declared names (runTests, isRisky,
// canonicalDigest, webhooks, ci) and what examples() returns. Their adaptations:
// - A tool's execute() returns a result or a park: `{ sleep }` and `{ wait }` are ToolPark.Sleeping and ToolPark.Waiting, which convert to
//   the ToolExecutionResult that carries them; `executeTool(..., { key, wait: false })` is StartToolAsync; `cancel` is
//   ToolRegistration.Cancel. A gate's `{ sleep: { signals, until } }` is BeforeToolDecision(Sleep: new GateSleep(...)).
// - A signal's state is a SignalState: `state === undefined` is null, `"value" in state` is a non-null Value, `"expired" in state` is
//   Expired. Signal values, checkpoints and memos are JsonValue, so the examples read their fields from JSON; SubmissionSettled's value
//   is read with SubmissionRecord.FromJson. `String(value)` of a structured output is its string, or its JSON (Shown).
// - An afterRun hook returns its tasks themselves (null for none), not upstream's { tasks } wrapper; hookTask is Define.HookTask.
// - `webhooks.expect`, `ci.trigger`, `ci.cancel` and `ci.cancelByKey` are SpecNames.ExpectWebhook, TriggerCi, CancelCi and CancelCiByKey,
//   as `payments.charge` is ChargePayment; TriggerCi returns the build's ID. crypto.randomUUID() is Guid.NewGuid().
using System.Collections.Immutable;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using PiSharp.Durable.Env;
using PiSharp.Durable.Tools;
using static Program;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> SpecUsageCases()
    {
        yield return Case("harness.spec-usage.compiles-the-specs-usage-examples", () =>
        {
            var examples = typeof(SpecUsage).GetMethod(nameof(SpecUsage.Examples), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Check(examples is not null, "Examples is a function");
            Check(examples!.ReturnType.GetProperty("Sequences") is not null, "Examples returns an object with sequences");
        });
    }
}

/// <summary>Names the spec leaves to the application (upstream's <c>declare</c>d names). Never called.</summary>
internal static class SpecNames
{
    private static T None<T>() => throw new InvalidOperationException("The spec leaves this name to the application.");

    public static DurableContext Context => None<DurableContext>();
    public static IStorage Storage => None<IStorage>();
    public static IDurableModels Models => None<IDurableModels>();
    public static ISession Session => None<ISession>();
    public static ConversationId ConversationId => None<ConversationId>();
    public static EntryDraft Message => None<EntryDraft>();
    public static ModelRef Haiku => None<ModelRef>();
    public static ModelRef Sonnet => None<ModelRef>();
    public static string Worktree => None<string>();
    public static string Name => None<string>();
    public static IExecutionEnv LocalEnv(string cwd) => None<IExecutionEnv>();
    public static Task<IExecutionEnv> ContainerEnv(string image, string cwd, DurableContext context) => None<Task<IExecutionEnv>>();
    public static string? RenderAgentsMd() => None<string?>();
    public static string? RenderSkills() => None<string?>();
    public static bool IsDangerous(JsonObjectValue call) => None<bool>();
    public static bool Writes(JsonObjectValue call) => None<bool>();
    /// <summary>The input that continues the run, or null (upstream: <c>{ continue: UserInput } | undefined</c>).</summary>
    public static Task<OnYieldResult?> RequestSecondPass(JsonObjectValue answer, IHookApi api, DurableContext context) => None<Task<OnYieldResult?>>();
    public static void RecordMetric(string name, double ms) => None<object>();
    public static Task<string> AnswerText(IToolExecutionApi api, EntryId entry, DurableContext context) => None<Task<string>>();
    public static Task<string> ChargePayment(string key) => None<Task<string>>();
    public static Task CancelPayment(JsonValue checkpoint) => None<Task>();
    public static string NewKey() => None<string>();
    public static EntryDraft ReceiptEntry(string receipt) => None<EntryDraft>();
    public static ISettingsManager Manager => None<ISettingsManager>();
    public static DurableTask Anchor => None<DurableTask>();
    public static DurableTask Reporter => None<DurableTask>();
    public static ToolRegistration SubagentTool => None<ToolRegistration>();
    public static IConversation Conversation => None<IConversation>();
    public static void Render(ConversationTranscriptView view) => None<object>();
    public static Task<(bool Passed, string Output)> RunTests(IExecutionEnv? env, string command, DurableContext context) =>
        None<Task<(bool Passed, string Output)>>();
    public static bool IsRisky(JsonObjectValue arguments) => None<bool>();
    public static string CanonicalDigest(JsonObjectValue value) => None<string>();
    // The host's webhook registry ("when the webhook for `match` arrives, signal this key to this task") and a CI client.
    public static Task ExpectWebhook(string match, TaskId taskId, SignalToken token, string key, DurableContext context) => None<Task>();
    /// <summary>Triggers a build and returns its ID (upstream: <c>ci.trigger({ ref, correlationId, idempotencyKey }, context)</c>).</summary>
    public static Task<string> TriggerCi(string gitRef, string correlationId, string idempotencyKey, DurableContext context) => None<Task<string>>();
    public static Task CancelCi(string buildId, DurableContext context) => None<Task>();
    public static Task CancelCiByKey(string idempotencyKey, DurableContext context) => None<Task>();

    internal interface ISettingsManager
    {
        double TimeoutMs { get; }
        bool AutoCompact { get; set; }
    }
}

/// <summary>The spec's usage examples (upstream <c>examples()</c>).</summary>
internal static class SpecUsage
{
    private static ValueTask<string?> Text(string? text) => ValueTask.FromResult(text);

    /// <summary>pi-ai tool result content with one text item: <c>[{ type: "text", text }]</c>.</summary>
    private static JsonArrayValue TextOutput(string text) => JsonValue.Array([JsonValue.Object([
        KeyValuePair.Create("type", (JsonValue)"text"),
        KeyValuePair.Create("text", (JsonValue)text),
    ])]);

    /// <summary>JavaScript's <c>String(value)</c> of a structured output: a string as it is, anything else as its JSON.</summary>
    private static string Shown(JsonValue? value) => value is JsonStringValue text ? text.Value : value?.ToJson() ?? "undefined";

    /// <summary>A pi-schema schema as its JSON Schema object.</summary>
    private static JsonObjectValue Schema(string json) => (JsonObjectValue)JsonValue.Parse(json);

    /// <summary>Never called: the declared names have no values.</summary>
    internal static SpecExamples Examples()
    {
        var context = SpecNames.Context;
        var readTool = CodingTools.CreateReadTool();
        var editTool = CodingTools.CreateEditTool();
        var bashTool = CodingTools.CreateBashTool();

        // ─── Section 7.1: extensions and host setup ──────────────────────────────────

        var contextFiles = new Extension { Name = "context-files", Sections = [Define.Section("agents-md", (_, _) => Text(SpecNames.RenderAgentsMd()))] };
        var skills = new Extension { Name = "skills", Sections = [Define.Section("skills", (_, _) => Text(SpecNames.RenderSkills()))] };
        var skillsV2 = new Extension { Name = "skills", Sections = [Define.Section("skills", (_, _) => Text(SpecNames.RenderSkills()))] };
        var coding = new Extension
        {
            Name = "coding",
            Sections =
            [
                Define.Section("preamble", "You are an expert coding assistant.", tag: false),
                // The environment the host built for this conversation, in the conversation's directory.
                Define.Section("cwd", (input, _) => Text(input.Env is { } env ? $"Working directory: {env.Cwd}" : null)),
            ],
        };
        var permissions = new Extension
        {
            Name = "permissions",
            Hooks =
            [
                Define.Hook(HarnessTasks.ToolTask, new ToolHooks
                {
                    BeforeTool = (call, _, _) =>
                        Task.FromResult(SpecNames.IsDangerous(call) ? new BeforeToolDecision(Block: "Needs approval") : null),
                }),
            ],
        };
        // A role and a review loop, for conversations that select it.
        var reviewer = new Extension
        {
            Name = "reviewer",
            Sections = [Define.Section("role", "You review diffs. Report problems as a list. Never edit files.")],
            Hooks = [Define.Hook(HarnessTasks.GenerationTask, new GenerationHooks { OnYield = SpecNames.RequestSecondPass })],
        };

        var timing = new Extension
        {
            Name = "timing",
            Wraps =
            [
                Define.WrapTool(bashTool, tool => tool with
                {
                    Execute = async (args, api, callContext) =>
                    {
                        var start = Environment.TickCount64;
                        try
                        {
                            return await tool.Execute(args, api, callContext);
                        }
                        finally
                        {
                            SpecNames.RecordMetric("bash", Environment.TickCount64 - start);
                        }
                    },
                }),
            ],
        };
        // A bash inside a Python virtualenv for one conversation: it replaces CodingTools' bash in place, and Timing, if selected, wraps it.
        var venv = new Extension
        {
            Name = "venv",
            Tools = [CodingTools.CreateBashTool(new BashToolOptions { CommandPrefix = "source .venv/bin/activate" })],
        };

        // ─── Section 7.2: hooks reading extension state ──────────────────────────────

        var planModeDoc = Documents.DefineConversationDoc("app.plan-mode", 1, DocumentFork.Current,
            () => JsonValue.Object([KeyValuePair.Create("enabled", (JsonValue)JsonValue.Boolean(false))]), checkpointWhen: (_, _, _) => false);
        var planMode = new Extension
        {
            Name = "plan-mode",
            Hooks =
            [
                Define.Hook(HarnessTasks.ToolTask, new ToolHooks
                {
                    // An absent document means plan mode is off.
                    BeforeTool = async (call, api, hookContext) =>
                        (await api.SnapshotAsync(planModeDoc, api.ConversationId, hookContext))?["enabled"] is JsonBooleanValue { Value: true } &&
                        SpecNames.Writes(call)
                            ? new BeforeToolDecision(Block: "Plan mode: read-only")
                            : null,
                }),
            ],
        };

        // ─── Section 7.2: work at the end of a run ───────────────────────────────────

        var runTests = Tasks.DefineTask(new TaskDefinition
        {
            Name = "app.run-tests",
            Version = 1,
            Initial = _ => JsonValue.Object([KeyValuePair.Create("phase", (JsonValue)"run")]),
            Phases = new Dictionary<string, TaskPhaseHandler>
            {
                ["run"] = async (task, runtime, taskContext) =>
                {
                    // The app's helper over `IExecutionEnv.ExecAsync()`: (Passed, Output). A rerun after a crash runs it again.
                    var command = ((JsonStringValue)((JsonObjectValue)task.Input)["command"]).Value;
                    var result = await SpecNames.RunTests(await runtime.EnvAsync(taskContext), command, taskContext);
                    var handle = (await runtime.ConversationAsync(runtime.ConversationId, taskContext))!;
                    await runtime.CommitAsync(async (tx, _) =>
                    {
                        // Admitted in this commit, before its table writes; placed when the run ends, in queue order with other input.
                        if (!result.Passed)
                            await handle.SubmitAsync(tx, SubmissionDraft.Input($"Tests failed:\n{result.Output}", whenBusy: WhenBusy.FollowUp));
                        await tx.AppendEntryAsync(runtime.ConversationId, new EntryDraft
                        {
                            Kind = "app.tests",
                            Data = JsonValue.Object([KeyValuePair.Create("passed", (JsonValue)JsonValue.Boolean(result.Passed))]),
                        });
                        return (TaskState?)NextTaskState.Completed(result.Output);
                    }, taskContext);
                },
            },
            Abort = (_, runtime, taskContext) => runtime.CommitAsync((_, _) => NextTaskState.Aborted(), taskContext),
        });
        var testsAfterEdits = new Extension
        {
            Name = "tests-after-edits",
            Tasks = [runTests],
            Hooks =
            [
                Define.Hook(HarnessTasks.GenerationTask, new GenerationHooks
                {
                    AfterRun = async (run, api, hookContext) =>
                    {
                        var range = new EntryQueryBounds { MinEntryId = run.First, MaxEntryId = run.Last, Order = ScanOrder.Ascending };
                        var items = (await api.EntriesAsync(range, 1000, null, hookContext)).Items;
                        var edited = items.Any(entry =>
                        {
                            var message = entry.Model is { Count: > 0 } model ? model[0] as JsonObjectValue : null;
                            return Entries.ToolResultEntry.Is(entry) &&
                                message is not null && message["role"] is JsonStringValue { Value: "toolResult" } &&
                                message["toolName"] is JsonStringValue { Value: "edit" or "write" };
                        });
                        IReadOnlyList<HookTask>? tasks = edited
                            ? [Define.HookTask(runTests, JsonValue.Object([KeyValuePair.Create("command", (JsonValue)"npm test")]))]
                            : null;
                        return tasks;
                    },
                }),
            ],
        };

        // ─── Section 7.2: gates ──────────────────────────────────────────────────────

        var approval = Signals.DefineSignal(new SignalDefinition
        {
            Name = "approvals.decision",
            Version = 1,
            Schema = Schema("""{"type":"object","properties":{"allow":{"type":"boolean"}},"required":["allow"],"additionalProperties":false}"""),
        });
        const double day = 24 * 60 * 60 * 1000;

        var approvals = new Extension
        {
            Name = "approvals",
            Hooks =
            [
                Define.Hook(HarnessTasks.ToolTask, new ToolHooks
                {
                    BeforeTool = async (call, api, hookContext) =>
                    {
                        var arguments = (JsonObjectValue)call["arguments"];
                        if (call["name"] is not JsonStringValue { Value: "bash" } || !SpecNames.IsRisky(arguments)) return null;
                        var key = SpecNames.CanonicalDigest(arguments); // other arguments, another question
                        var state = await api.ReadSignalAsync(approval, key, hookContext);
                        // One deadline per question, fixed at the first pass: later passes (wakes, registry changes) keep it.
                        var until = ((JsonNumberValue)await api.MemoAsync($"approvals.until.{key}", api.Now() + day, hookContext)).Value;
                        if (state is null) return new BeforeToolDecision(Sleep: new GateSleep(until, ImmutableArray.Create(approval.Key(key))));
                        if (state.Expired) return new BeforeToolDecision(Block: "No approval within a day");
                        return ((JsonObjectValue)state.Value!)["allow"] is JsonBooleanValue { Value: true }
                            ? null
                            : new BeforeToolDecision(Block: "Not approved");
                    },
                }),
            ],
        };

        // ─── Section 7.3: tools that park ────────────────────────────────────────────

        var researchParameters = Schema(
            """{"type":"object","properties":{"x":{"type":"string"},"y":{"type":"string"}},"required":["x","y"],"additionalProperties":false}""");

        var research = Define.Tool(new ToolRegistration
        {
            Name = "research_both",
            Description = "Research two topics in parallel",
            Parameters = researchParameters,
            Replay = ToolReplay.Safe,
            Execute = async (args, api, callContext) =>
            {
                var topics = (JsonObjectValue)args;
                var a = await api.StartToolAsync("research", JsonValue.Object([KeyValuePair.Create("topic", topics["x"])]), callContext,
                    new NestedCallOptions { Key = "a" });
                var b = await api.StartToolAsync("research", JsonValue.Object([KeyValuePair.Create("topic", topics["y"])]), callContext,
                    new NestedCallOptions { Key = "b" });
                var done = new HashSet<TaskId>();
                if (api.Checkpoint is JsonObjectValue parked)
                    foreach (var id in (JsonArrayValue)parked["done"]) done.Add(new TaskId((long)((JsonNumberValue)id).Value));
                if (api.Wake is { Reason: WakeReasonKind.Tasks } wake)
                    foreach (var id in wake.Ended) done.Add(id);
                var open = new[] { a, b }.Where(call => !done.Contains(call.TaskId)).ToList();
                if (open.Count > 0)
                {
                    var checkpoint = JsonValue.Object([
                        KeyValuePair.Create("done", (JsonValue)JsonValue.Array(done.Select(id => (JsonValue)id.ToJson()))),
                    ]);
                    return ToolPark.Waiting(open.Select(call => call.TaskId), JoinPolicy.Any, checkpoint);
                }
                var results = await Task.WhenAll(a.ResultAsync(callContext), b.ResultAsync(callContext));
                return new ToolExecutionResult
                {
                    Output = TextOutput($"{Shown(results[0].StructuredOutput)}\n{Shown(results[1].StructuredOutput)}"),
                };
            },
        });

        const double hour = 60 * 60 * 1000;
        var ciResult = Signals.DefineSignal(new SignalDefinition
        {
            Name = "app.ci-result",
            Version = 1,
            Schema = Schema(
                """{"type":"object","properties":{"status":{"enum":["passed","failed"]},"logUrl":{"type":"string"}},"required":["status","logUrl"],"additionalProperties":false}"""),
        });
        var ciParameters = Schema("""{"type":"object","properties":{"ref":{"type":"string"}},"required":["ref"],"additionalProperties":false}""");

        var runCi = Define.Tool(new ToolRegistration
        {
            Name = "run_ci",
            Description = "Run CI on a git ref and report the result",
            Parameters = ciParameters,
            Replay = ToolReplay.Safe,
            Execute = async (args, api, callContext) =>
            {
                // The result, the deadline's expiry, or nothing yet (a fresh call).
                var state = await api.ReadSignalAsync(ciResult, "done", callContext);
                if (state is { Value: JsonObjectValue reported })
                {
                    var status = ((JsonStringValue)reported["status"]).Value;
                    var logUrl = ((JsonStringValue)reported["logUrl"]).Value;
                    return new ToolExecutionResult { Output = TextOutput($"CI {status}: {logUrl}"), IsError = status != "passed" };
                }
                if (state is not null)
                {
                    var buildId = api.Checkpoint is JsonObjectValue parked ? ((JsonStringValue)parked["buildId"]).Value : null;
                    return new ToolExecutionResult { Output = TextOutput($"CI build {buildId} never reported"), IsError = true };
                }
                // The tool's own idempotency key with CI, stable across reruns (memos are first-writer-wins).
                var runId = ((JsonStringValue)await api.MemoAsync("ci.run", Guid.NewGuid().ToString(), callContext)).Value;
                // Registered before triggering, so a fast webhook already finds this task.
                await SpecNames.ExpectWebhook(runId, api.TaskId, ciResult, "done", callContext);
                var gitRef = ((JsonStringValue)((JsonObjectValue)args)["ref"]).Value;
                var build = await SpecNames.TriggerCi(gitRef, runId, runId, callContext);
                return ToolPark.Sleeping(
                    until: api.Now() + 2 * hour,
                    signals: [ciResult.Key("done")],
                    checkpoint: JsonValue.Object([KeyValuePair.Create("buildId", (JsonValue)build)]));
            },
            Cancel = async (_, api, callContext) =>
            {
                if (api.Checkpoint is JsonObjectValue parked) await SpecNames.CancelCi(((JsonStringValue)parked["buildId"]).Value, callContext);
                else await SpecNames.CancelCiByKey(((JsonStringValue)(await api.MemoAsync("ci.run", callContext))!).Value, callContext);
            },
        });

        Extension parkingSubagent = null!;
        parkingSubagent = new Extension
        {
            Name = "parking-subagent",
            Tools =
            [
                Define.Tool(new ToolRegistration
                {
                    Name = "subagent",
                    Description = "Delegate a task to a subagent and get its answer back",
                    Parameters = Schema("""{"type":"object","properties":{"task":{"type":"string"}},"required":["task"],"additionalProperties":false}"""),
                    Replay = ToolReplay.Safe,
                    Execute = async (args, api, callContext) =>
                    {
                        var task = ((JsonStringValue)((JsonObjectValue)args)["task"]).Value;
                        var state = await api.ReadSignalAsync(Signals.SubmissionSettled, "answered", callContext);
                        if (state is { Value: { } value })
                        {
                            var settled = SubmissionRecord.FromJson(value);
                            if (settled.Status != SubmissionStatus.Done || settled.Type != SubmissionType.Input)
                                return new ToolExecutionResult { Output = TextOutput($"Subagent ended: {settled.Status}"), IsError = true };
                            return new ToolExecutionResult { Output = TextOutput(await SpecNames.AnswerText(api, settled.Answer!.Value, callContext)) };
                        }
                        var child = await api.CommitAsync(async tx =>
                        {
                            var existing = (await tx.ScanConversationsAsync(new ConversationQuery { OwnerTaskId = api.TaskId }, 1)).Items.FirstOrDefault();
                            if (existing is not null) return existing.Id;
                            var created = await tx.CreateConversationAsync(ConversationOwnership.Task(api.TaskId));
                            await Agents.ConfigureAsync(tx, created.Id, new AgentChange { Extensions = ExtensionSelection.Edit(remove: [parkingSubagent]) });
                            return created.Id;
                        }, callContext);
                        var handle = (await api.ConversationAsync(child, callContext))!;
                        // The request ID makes a rerun find the same submission.
                        await handle.SubmitAsync(
                            SubmissionDraft.Input(task, requestId: $"subagent:{api.TaskId.Value}", notify: new SubmissionNotify(api.TaskId, "answered")),
                            callContext);
                        return ToolPark.Sleeping(signals: [Signals.SubmissionSettled.Key("answered")]);
                    },
                }),
            ],
        };

        // ─── Section 7.3: subagents ──────────────────────────────────────────────────

        Extension subagent = null!;
        subagent = new Extension
        {
            Name = "subagent",
            Tools =
            [
                Define.Tool(new ToolRegistration
                {
                    Name = "subagent",
                    Description = "Delegate a self-contained task to a subagent and get its answer back.",
                    Parameters = (JsonObjectValue)JsonValue.Parse("""{"type":"object","properties":{"task":{"type":"string"}},"required":["task"],"additionalProperties":false}"""),
                    Replay = ToolReplay.Safe,
                    Execute = async (args, api, callContext) =>
                    {
                        var task = ((JsonStringValue)((JsonObjectValue)args)["task"]).Value;
                        var child = await api.CommitAsync(async tx =>
                        {
                            var existing = (await tx.ScanConversationsAsync(new ConversationQuery { OwnerTaskId = api.TaskId }, 1)).Items.FirstOrDefault();
                            if (existing is not null) return existing.Id;
                            // Starts as a copy of this conversation's agent: model, thinking level, cwd, extensions, tools.
                            var created = await tx.CreateConversationAsync(ConversationOwnership.Task(api.TaskId));
                            // Without this extension, the child is not offered this tool.
                            await Agents.ConfigureAsync(tx, created.Id, new AgentChange { Extensions = ExtensionSelection.Edit(remove: [subagent]) });
                            return created.Id;
                        }, callContext);
                        await api.DetailsAsync(JsonValue.Object([KeyValuePair.Create("conversationId", (JsonValue)child.ToJson())]), callContext);
                        var handle = (await api.ConversationAsync(child, callContext))!;
                        var request = SubmissionDraft.Input(task, requestId: $"subagent:{api.TaskId.Value}");
                        var settled = await (await handle.SubmitAsync(request, callContext)).WaitAsync(callContext);
                        if (settled.Status != SubmissionStatus.Done || settled.Type != SubmissionType.Input)
                            throw new InvalidOperationException($"Subagent failed: {settled.Status}");
                        return new ToolExecutionResult
                        {
                            Output = JsonValue.Array([JsonValue.Object([
                                KeyValuePair.Create("type", (JsonValue)"text"),
                                KeyValuePair.Create("text", (JsonValue)await SpecNames.AnswerText(api, settled.Answer!.Value, callContext)),
                            ])]),
                        };
                    },
                }),
            ],
        };

        var subagentTools = new Extension { Name = "subagent-tools", Tasks = [SpecNames.Anchor, SpecNames.Reporter], Tools = [SpecNames.SubagentTool] };

        // ─── Section 7.4 ─────────────────────────────────────────────────────────────

        var chat = new Extension { Name = "chat", Sections = [Define.Section("preamble", "You are a helpful assistant.", tag: false)] };

        // ─── Section 5.1: a task with intent, effect, outcome ────────────────────────

        var payment = Tasks.DefineTask(new TaskDefinition
        {
            Name = "app.payment",
            Version = 1,
            Initial = _ => JsonValue.Object([KeyValuePair.Create("phase", (JsonValue)"prepare")]),
            Phases = new Dictionary<string, TaskPhaseHandler>
            {
                // Intent, effect, outcome.
                ["prepare"] = (_, runtime, taskContext) => runtime.CommitAsync((_, _) => NextTaskState.Running(JsonValue.Object([
                    KeyValuePair.Create("phase", (JsonValue)"charge"), KeyValuePair.Create("key", (JsonValue)SpecNames.NewKey())])), taskContext),
                ["charge"] = async (task, runtime, taskContext) =>
                {
                    var receipt = await SpecNames.ChargePayment(((JsonStringValue)((JsonObjectValue)task.Checkpoint)["key"]).Value); // idempotent by key
                    await runtime.CommitAsync(async (tx, current) =>
                    {
                        var entry = await tx.AppendEntryAsync(current.ConversationId, SpecNames.ReceiptEntry(receipt));
                        return (TaskState?)NextTaskState.Completed(JsonValue.Object([KeyValuePair.Create("entryId", (JsonValue)entry.Id.ToJson())]));
                    }, taskContext);
                },
            },
            // The abort handler decides the outcome; returning without one faults the task.
            Abort = async (task, runtime, taskContext) =>
            {
                await SpecNames.CancelPayment(task.Checkpoint);
                await runtime.CommitAsync((_, _) => NextTaskState.Aborted("user"), taskContext);
            },
        });

        // ─── Section 5.7: a task that asks a person ──────────────────────────────────

        var answer = Signals.DefineSignal(new SignalDefinition
        {
            Name = "app.answer",
            Version = 1,
            Schema = Schema("""{"type":"object","properties":{"text":{"type":"string"}},"required":["text"],"additionalProperties":false}"""),
        });

        var askPerson = Tasks.DefineTask(new TaskDefinition
        {
            Name = "app.ask-person",
            Version = 1,
            Initial = _ => JsonValue.Object([KeyValuePair.Create("phase", (JsonValue)"ask")]),
            Phases = new Dictionary<string, TaskPhaseHandler>
            {
                ["ask"] = (_, runtime, taskContext) => runtime.CommitAsync((_, _) => NextTaskState.Sleeping(
                    JsonValue.Object([KeyValuePair.Create("phase", (JsonValue)"answered")]),
                    runtime.Now() + 4 * hour,
                    [answer.Key("answer")]), taskContext),
                ["answered"] = async (_, runtime, taskContext) =>
                {
                    var state = await runtime.ReadSignalAsync(answer, "answer", taskContext); // a Value when answered, Expired after the deadline
                    var result = state?.Value is JsonObjectValue answered
                        ? JsonValue.Object([KeyValuePair.Create("text", answered["text"])])
                        : JsonObjectValue.Empty;
                    await runtime.CommitAsync((_, _) => NextTaskState.Completed(result), taskContext);
                },
            },
            Abort = (_, runtime, taskContext) => runtime.CommitAsync((_, _) => NextTaskState.Aborted(), taskContext),
        });

        var follow = Tasks.DefineTask(new TaskDefinition
        {
            Name = "app.follow",
            Version = 1,
            Initial = _ => JsonValue.Object([KeyValuePair.Create("phase", (JsonValue)"follow")]),
            Phases = new Dictionary<string, TaskPhaseHandler> { ["follow"] = (_, _, _) => Task.CompletedTask },
            Abort = (_, _, _) => Task.CompletedTask,
        });

        // ─── Sequences ───────────────────────────────────────────────────────────────

        var sequences = new SpecSequences
        {
            // Section 2.2: a tool's commit creates a configured child.
            ChildInToolCommit = async (tx, taskId) =>
            {
                // In a tool's commit. The child starts as a copy of this conversation's agent: model, extensions, tools, cwd.
                var child = await tx.CreateConversationAsync(ConversationOwnership.Task(taskId));
                // A cheaper model, only the read tool, and its own worktree; everything else stays as copied.
                await Agents.ConfigureAsync(tx, child.Id, new AgentChange { Model = SpecNames.Haiku, Tools = ToolSelection.Only(readTool), Cwd = SpecNames.Worktree });
            },

            // Section 2.2: settings read live through getters.
            LiveSettings = () =>
            {
                var manager = SpecNames.Manager;
                manager.AutoCompact = false; // no Session write; every conversation follows at its next threshold check
                return new UserSettings(manager);
            },

            // Section 2.2: an environment per conversation.
            ContainerEnv = async () =>
            {
                var registry = Registry.Create();
                // Absent: the conversation runs locally. Only conversations with this document run in a container.
                // Subagents do not copy it: their creator writes it too when they should run in the container.
                var containerDoc = Documents.DefineConversationDoc("app.container", 1, DocumentFork.Current,
                    () => JsonValue.Object([KeyValuePair.Create("image", (JsonValue)"node:22")]), checkpointWhen: (_, _, _) => false);
                var harness = await Harness.OpenAsync(SpecNames.Storage, new HarnessOptions
                {
                    Models = SpecNames.Models,
                    Registry = registry,
                    Env = async (target, envContext) =>
                    {
                        var container = await target.Read.SnapshotAsync(containerDoc, target.ConversationId, envContext);
                        return container is not null
                            ? await SpecNames.ContainerEnv(((JsonStringValue)container["image"]).Value, target.Cwd ?? "/work", envContext)
                            : SpecNames.LocalEnv(target.Cwd ?? Directory.GetCurrentDirectory()); // cached LocalExecutionEnv per directory
                    },
                }, context);
                return harness;
            },

            // Sections 2.2 and 7.1: host setup, tool filters, extension selection, plan mode, and reload.
            Host = async () =>
            {
                var registry = Registry.Create();
                foreach (var extension in new[] { CodingTools.Extension, coding, contextFiles, skills, permissions, reviewer }) registry.Install(extension);
                var harness = await Harness.OpenAsync(SpecNames.Storage, new HarnessOptions
                {
                    Models = SpecNames.Models,
                    Registry = registry,
                    // Reviewer is installed but not selected by default: only conversations that select it get its role and hooks.
                    Settings = new HarnessSettings { Extensions = [CodingTools.Extension, coding, contextFiles, skills, permissions] },
                    Env = (target, _) => Task.FromResult<IExecutionEnv?>(SpecNames.LocalEnv(target.Cwd ?? Directory.GetCurrentDirectory())), // cached LocalExecutionEnv per directory
                }, context);
                // The conversation remembers its model and directory; a restart elsewhere keeps both.
                var root = await harness.RootAsync(context, new AgentChange { Model = SpecNames.Sonnet, Cwd = Directory.GetCurrentDirectory() });

                await root.ConfigureAsync(new AgentChange { Tools = ToolSelection.Without(editTool) }, context);
                await root.ConfigureAsync(new AgentChange { Tools = ToolSelection.Without(bashTool) }, context); // edit is offered again
                await root.ConfigureAsync(new AgentChange { Tools = Patch<ToolSelection>.Clear }, context); // every tool of the selected extensions again

                registry.Install(timing);
                registry.Install(venv); // installed, but not in the default selection
                var conversation = root;
                await conversation.ConfigureAsync(new AgentChange { Extensions = ExtensionSelection.Edit(add: [venv]) }, context);

                registry.Install(planMode);
                // PlanMode is in the default selection; /plan toggles this conversation's state.
                await root.CommitAsync(async tx => (await tx.DocAsync(planModeDoc, root.Id)).Set("enabled", JsonValue.Boolean(true)), context);

                registry.Install(subagent);
                registry.Install(chat);
                registry.Install(skillsV2); // same name: conversations selecting skills render v2 at their next request
                registry.Uninstall(skills); // selecting conversations get a system delta removing its section; nothing is rewritten
                // Restart: the host installs its extensions again; stored names resolve against them.
                return harness;
            },

            // Section 7.3: a named subagent's spawn commit.
            Spawn = async tx =>
            {
                // In subagentTool's spawn commit, after the name checks:
                var anchor = await tx.CreateTaskAsync(SpecNames.Anchor, JsonValue.Null, new TaskOptions(TaskOwnership.Conversation) { Background = true });
                // Owned by a task of the parent: starts as a copy of the parent's agent.
                var child = await tx.CreateConversationAsync(ConversationOwnership.Task(anchor));
                await Agents.ConfigureAsync(tx, child.Id, new AgentChange
                {
                    Extensions = ExtensionSelection.Edit(remove: [subagentTools]),
                    Instructions = $"You are the subagent \"{SpecNames.Name}\". Answer the main agent's requests.",
                });
            },

            // Section 9.3: a client's transcript over the windowed view.
            Transcript = async () =>
            {
                var conversation = SpecNames.Conversation;
                var transcript = new ConversationTranscript((query, limit, ctx) => conversation.EntriesAsync(query, limit, null, ctx));
                var watch = await conversation.WatchAsync(context);
                SpecNames.Render(transcript.Merge(watch.Value));
                watch.Start((call, _) =>
                {
                    if (call is WatchValueCall<ConversationView> { Value: var value }) SpecNames.Render(transcript.Merge(value));
                    return Task.CompletedTask;
                });
                // A marker scrolled into view:
                SpecNames.Render(await transcript.LoadEarlierAsync(context));
                // Reconnect after a drop:
                var resumed = await conversation.WatchAsync(context, transcript.Resume);
                return resumed;
            },

            // Section 4: table reads before the first table write; documents stay usable.
            TableRules = async () =>
            {
                var conversationId = SpecNames.ConversationId;
                await SpecNames.Session.CommitAsync(async tx =>
                {
                    var conversation = await tx.GetConversationAsync(conversationId); // table read
                    var live = await tx.DocAsync(Live.LiveDoc, conversationId);

                    await tx.AppendEntryAsync(conversationId, SpecNames.Message); // first table write
                    live.Delete("generation"); // document mutation remains valid
                    await tx.CreateTaskAsync(follow, JsonValue.Object([]), new TaskOptions(TaskOwnership.Conversation) { ConversationId = conversationId }); // further table writes are fine
                    _ = conversation;
                }, context);
            },

            // Section 3.4: drafts are revoked after their commit.
            RevokedDraft = async () =>
            {
                DraftObject? escaped = null;
                await SpecNames.Session.CommitAsync(async tx =>
                {
                    escaped = await tx.DocAsync(Live.LiveDoc, SpecNames.ConversationId);
                }, context);
                escaped!.Delete("generation"); // throws: the draft was revoked
            },
        };
        return new SpecExamples(sequences, payment, testsAfterEdits, approvals, research, runCi, parkingSubagent, askPerson);
    }

    /// <summary>The user's settings, read live through getters.</summary>
    private sealed class UserSettings(SpecNames.ISettingsManager manager) : HarnessSettings
    {
        public override ConversationStreamOptions? Stream { get => new() { TimeoutMs = manager.TimeoutMs }; init { } }
        public override CompactionSettings? Compaction { get => new() { Enabled = manager.AutoCompact }; init { } }
    }
}

/// <summary>What <see cref="SpecUsage.Examples"/> returns (upstream
/// <c>{ sequences, Payment, TestsAfterEdits, Approvals, research, runCi, ParkingSubagent, AskPerson }</c>).</summary>
internal sealed record SpecExamples(SpecSequences Sequences, DurableTask Payment, Extension TestsAfterEdits, Extension Approvals,
    ToolRegistration Research, ToolRegistration RunCi, Extension ParkingSubagent, DurableTask AskPerson);

/// <summary>The spec's sequences (upstream <c>sequences</c>).</summary>
internal sealed class SpecSequences
{
    public required Func<ITx, TaskId, Task> ChildInToolCommit { get; init; }
    public required Func<HarnessSettings> LiveSettings { get; init; }
    public required Func<Task<IHarness>> ContainerEnv { get; init; }
    public required Func<Task<IHarness>> Host { get; init; }
    public required Func<ITx, Task> Spawn { get; init; }
    public required Func<Task<IWatchHandle<ConversationView>>> Transcript { get; init; }
    public required Func<Task> TableRules { get; init; }
    public required Func<Task> RevokedDraft { get; init; }
}
