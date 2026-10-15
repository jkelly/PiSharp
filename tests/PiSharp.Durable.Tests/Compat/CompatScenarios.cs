// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): the scenarios of tools/DurableFixtures/scenarios.mjs, run on PiSharp. compat.* runs
// them to write PiSharp's own sessions (compared with the sessions the pinned upstream wrote), and to resume upstream's sessions with the
// same scripted model. Keep each scenario in step with its upstream twin: same faux responses, same tools and tasks, same order of calls.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using static ChatTests;
using static HarnessTests;
using static SessionTests;
using Deferred = HarnessTests.Deferred;

/// <summary>One scenario: <see cref="Run"/> writes the session and closes it; <see cref="Resume"/>, when present, reopens and finishes it.</summary>
internal sealed record CompatScenario(string Name, Func<Func<Task<IStorage>>, Task> Run, Func<Func<Task<IStorage>>, Task>? Resume = null);

/// <summary>The scenarios of tools/DurableFixtures/scenarios.mjs.</summary>
internal static class CompatScenarios
{
    private static readonly DurableContext Context = DurableContext.Background;
    /// <summary>The Harness clock (scenarios.mjs <c>NOW</c>).</summary>
    internal const double Now = 1767225600000;
    /// <summary>The timestamp of every scripted faux answer (scenarios.mjs <c>T</c>).</summary>
    private const double T = 1000;

    private static JsonObjectValue Answer(string text) => FauxAssistantMessage(text, timestamp: T);

    private static JsonObjectValue Calls(params (string Name, JsonObjectValue Args, string Id)[] list) =>
        FauxAssistantMessage(list.Select(call => FauxToolCall(call.Name, call.Args, call.Id)), stopReason: "toolUse", timestamp: T);

    private static ChatSetup Chat(FauxOptions? options = null) => new(new FauxModels((options ?? new FauxOptions()) with { Api = "faux" })) { Now = () => Now };

    private static async Task<OpenedChat> Open(ChatSetup setup, IStorage storage)
    {
        var harness = await Harness.OpenAsync(storage, new HarnessOptions
        {
            Models = setup.Models, Registry = setup.Registry, Settings = setup.Settings, Now = () => setup.Now(),
        }, Context);
        var root = await harness.RootAsync(Context, new AgentChange { Model = new ModelRef("faux", "faux-1") });
        return new OpenedChat(harness, root);
    }

    // ─── tool-interrupted ───────────────────────────────────────────────────────

    /// <summary>An unsafe tool that writes output and details, then blocks until its invocation is aborted.</summary>
    private sealed class WorkTool
    {
        private int runs;

        public WorkTool()
        {
            Registration = Define.Tool(new ToolRegistration
            {
                Name = "work",
                Description = "Works until interrupted",
                Execute = async (_, api, callContext) =>
                {
                    var run = Interlocked.Increment(ref runs);
                    api.Output($"run {run}\n");
                    await api.DetailsAsync(Obj(("run", run)), callContext);
                    if (run == 1)
                    {
                        Started.Resolve();
                        try { await Aborted(callContext.CancellationToken); } catch (OperationCanceledException) { }
                    }
                    return new ToolExecutionResult { Output = JsonValue.Array(FauxText($"finished run {run}")) };
                },
            });
        }

        public ToolRegistration Registration { get; }
        public Deferred Started { get; } = new();
        public int Runs => Volatile.Read(ref runs);
        public Extension Extension => Define.Extension(new Extension { Name = "work", Tools = [Registration] });
    }

    private static readonly JsonObjectValue SubagentParameters =
        O("""{"type":"object","required":["task"],"properties":{"task":{"type":"string","description":"What the subagent should do"}}}""");

    private static Extension CreateSubagent()
    {
        Extension subagent = null!;
        subagent = Define.Extension(new Extension
        {
            Name = "subagent",
            Tools =
            [
                Define.Tool(new ToolRegistration
                {
                    Name = "subagent",
                    Description = "Delegate a self-contained task to a subagent and get its answer back.",
                    Parameters = SubagentParameters,
                    Replay = ToolReplay.Safe,
                    Execute = async (args, api, callContext) =>
                    {
                        var child = await api.CommitAsync(async tx =>
                        {
                            var existing = (await tx.ScanConversationsAsync(new ConversationQuery { OwnerTaskId = api.TaskId }, 1)).Items.FirstOrDefault();
                            if (existing is not null) return existing.Id;
                            var created = await tx.CreateConversationAsync(ConversationOwnership.Task(api.TaskId));
                            await Agents.ConfigureAsync(tx, created.Id, new AgentChange { Extensions = ExtensionSelection.Edit(remove: [subagent]) });
                            return created.Id;
                        }, callContext);
                        await api.DetailsAsync(Obj(("conversationId", child.ToJson())), callContext);
                        var handle = (await api.ConversationAsync(child, callContext))!;
                        var request = SubmissionDraft.Input(S(((JsonObjectValue)args)["task"]), requestId: $"subagent:{api.TaskId.Value}");
                        var settled = await (await handle.SubmitAsync(request, callContext)).WaitAsync(callContext);
                        if (settled.Status != SubmissionStatus.Done || settled.Type != SubmissionType.Input)
                            throw new InvalidOperationException($"Subagent failed: {settled.Status}");
                        var entry = await api.CommitAsync(tx => tx.GetEntryAsync(Entries.AssistantEntry, settled.Answer!.Value), callContext);
                        var text = string.Concat(((JsonArrayValue)((JsonObjectValue)entry!.Model![0])["content"]).OfType<JsonObjectValue>()
                            .Where(block => S(block["type"]) == "text").Select(block => S(block["text"])));
                        return new ToolExecutionResult
                        {
                            Output = JsonValue.Array(FauxText(text)),
                            Details = Obj(("conversationId", child.ToJson())),
                        };
                    },
                }),
            ],
        });
        return subagent;
    }

    private static TaskState Completed(JsonValue result) => NextTaskState.Completed(result);

    internal static readonly IReadOnlyList<CompatScenario> All =
    [
        new("chat", async open =>
        {
            var setup = Chat();
            setup.Registry.Install(Define.Extension(new Extension
            {
                Name = "base",
                Sections = [Define.Section("preamble", "You are terse.", tag: false), Define.Section("cwd", "/repo")],
                Tools = [Define.Tool(new ToolRegistration
                {
                    Name = "noop", Description = "Does nothing",
                    Execute = (_, _, _) => Task.FromResult(new ToolExecutionResult { Output = JsonArrayValue.Empty }),
                })],
            }));
            setup.Faux.SetResponses(Answer("Hello there"));
            var (harness, root) = await Open(setup, await open());
            await (await root.SubmitAsync(SubmissionDraft.Input("hi", requestId: "r1"), Context)).WaitAsync(Context);
            await harness.CloseAsync(Context);
        }),
        new("tool-interrupted", async open =>
        {
            var setup = Chat();
            var tool = new WorkTool();
            setup.Registry.Install(tool.Extension);
            setup.Faux.SetResponses(Calls(("work", JsonObjectValue.Empty, "call-1")));
            var (harness, root) = await Open(setup, await open());
            await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await tool.Started.Task;
            await Eventually(async () => (await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context)) is { } live &&
                live.TryGetValue("tools", out var tools) && tools is JsonArrayValue { Count: > 0 } slots &&
                ((JsonObjectValue)slots[0]).ContainsKey("taskId"));
            await harness.CloseAsync(Context);
        }, async open =>
        {
            var setup = Chat();
            var tool = new WorkTool();
            setup.Registry.Install(tool.Extension);
            setup.Faux.SetResponses(Answer("done"));
            var (harness, _) = await Open(setup, await open());
            harness.Resume();
            await harness.WaitForIdleAsync(Context);
            Program.Equal(0, tool.Runs, "the unsafe tool did not run again");
            await harness.CloseAsync(Context);
        }),
        new("follow-up", async open =>
        {
            var setup = Chat();
            var reached = new Deferred();
            setup.Faux.SetResponses(new FauxResponseFactory(async request =>
            {
                reached.Resolve();
                try { await Aborted(request.Signal); } catch (OperationCanceledException) { }
                return Answer("never");
            }));
            var (harness, root) = await Open(setup, await open());
            await root.SubmitAsync(SubmissionDraft.Input("one"), Context);
            await reached.Task;
            await root.SubmitAsync(SubmissionDraft.Input("two", WhenBusy.FollowUp), Context);
            await harness.CloseAsync(Context);
        }, async open =>
        {
            var setup = Chat();
            setup.Faux.SetResponses(Answer("first"), Answer("second"));
            var (harness, _) = await Open(setup, await open());
            harness.Resume();
            await harness.WaitForIdleAsync(Context);
            await harness.CloseAsync(Context);
        }),
        new("compaction", async open =>
        {
            var setup = Chat(new FauxOptions { Models = [new FauxModelOptions("faux-1", 100_000, 900)] });
            setup.Stream = new ConversationStreamOptions { CacheRetention = "none" };
            setup.Compaction = new CompactionSettings { Enabled = false, ReserveTokens = 1000, KeepRecentTokens = 150, BackgroundTokens = 0 };
            setup.Registry.Install(Define.Extension(new Extension
            {
                Name = "section:preamble",
                Sections = [Define.Section("preamble", (_, _) => ValueTask.FromResult<string?>("You are helpful."), false)],
            }));
            static string Text(string label, int tokens) => $"{label} {new string('x', Math.Max(0, tokens * 4 - label.Length - 1))}";
            setup.Faux.SetResponses(Answer(Text("a1", 100)), Answer(Text("a2", 100)), Answer(Text("a3", 100)), Answer("SUMMARY"), Answer("done"));
            var (harness, root) = await Open(setup, await open());
            foreach (var user in new[] { Text("u1", 100), Text("u2", 100), Text("u3", 100) })
                await (await root.SubmitAsync(SubmissionDraft.Input(user), Context)).WaitAsync(Context);
            var id = await root.CompactAsync("focus on files", Context);
            var record = await harness.WaitForTaskAsync(id, Context);
            var result = (JsonObjectValue)((CompletedTaskOutcome)((SettlingTaskState)record.State).Outcome).Result!;
            await (await harness.SubmissionAsync(new SubmissionId((long)N(result["submissionId"])), Context))!.WaitAsync(Context);
            await (await root.SubmitAsync(SubmissionDraft.Input("next"), Context)).WaitAsync(Context);
            await harness.WaitForIdleAsync(Context);
            await harness.CloseAsync(Context);
        }),
        new("subagent", async open =>
        {
            var setup = Chat();
            setup.Registry.Install(CreateSubagent());
            setup.Faux.SetResponses(
                Calls(("subagent", Obj(("task", "Name three prime numbers.")), "call-1")),
                Answer("2, 3, and 5."),
                Answer("The subagent says: 2, 3, and 5."));
            var (harness, root) = await Open(setup, await open());
            await (await root.SubmitAsync(SubmissionDraft.Input("Use the subagent tool to find three prime numbers."), Context)).WaitAsync(Context);
            await harness.WaitForIdleAsync(Context);
            await harness.CloseAsync(Context);
        }),
        new("fork", async open =>
        {
            var setup = Chat();
            setup.Faux.SetResponses(Answer("first"), Answer("second"), Answer("third"));
            var (harness, root) = await Open(setup, await open());
            var one = await (await root.SubmitAsync(SubmissionDraft.Input("one"), Context)).WaitAsync(Context);
            await (await root.SubmitAsync(SubmissionDraft.Input("two"), Context)).WaitAsync(Context);
            var fork = await root.ForkAsync(one.Answer!.Value, new ConversationCreateOptions(ConversationOwnership.Ownerless), Context);
            await (await fork.SubmitAsync(SubmissionDraft.Input("three"), Context)).WaitAsync(Context);
            await harness.WaitForIdleAsync(Context);
            await harness.CloseAsync(Context);
        }),
        new("task-graph", async open =>
        {
            var setup = Chat();
            var leaf = Tasks.DefineTask(new TaskDefinition
            {
                Name = "compat.leaf",
                Version = 1,
                Initial = _ => O("""{"phase":"work"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["work"] = (task, runtime, ctx) =>
                        runtime.CommitAsync((_, _) => Completed(Obj(("leaf", ((JsonObjectValue)task.Input)["label"]))), ctx),
                },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            var middle = Tasks.DefineTask(new TaskDefinition
            {
                Name = "compat.middle",
                Version = 1,
                Initial = _ => O("""{"phase":"spawn"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["spawn"] = (task, runtime, ctx) => runtime.CommitAsync(async (tx, _) =>
                    {
                        var created = await tx.CreateTaskAsync(leaf, Obj(("label", "grandchild")), new TaskOptions(TaskOwnership.Task(task.Id)));
                        return (TaskState?)NextTaskState.Waiting(Obj(("phase", "join"), ("leaf", created.ToJson())), [created], JoinPolicy.AllSettled);
                    }, ctx),
                    ["join"] = (task, runtime, ctx) => runtime.CommitAsync((_, _) =>
                        Completed(Obj(("middle", true), ("leaf", ((JsonObjectValue)task.State.Checkpoint)["leaf"]))), ctx),
                },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            var rootTask = Tasks.DefineTask(new TaskDefinition
            {
                Name = "compat.root",
                Version = 1,
                Initial = _ => O("""{"phase":"spawn"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["spawn"] = (task, runtime, ctx) => runtime.CommitAsync(async (tx, _) =>
                    {
                        var owned = await tx.CreateConversationAsync(ConversationOwnership.Task(task.Id));
                        var created = await tx.CreateTaskAsync(middle, Obj(("depth", 1)), new TaskOptions(TaskOwnership.Task(task.Id)));
                        return (TaskState?)NextTaskState.Waiting(Obj(("phase", "join"), ("owned", owned.Id.ToJson()), ("middle", created.ToJson())),
                            [created], JoinPolicy.AllSettled);
                    }, ctx),
                    ["join"] = (task, runtime, ctx) => runtime.CommitAsync((_, _) =>
                        Completed(Obj(("root", true), ("middle", ((JsonObjectValue)task.State.Checkpoint)["middle"]))), ctx),
                },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            setup.Registry.Install(Define.Extension(new Extension { Name = "tasks", Tasks = [rootTask, middle, leaf] }));
            var (harness, root) = await Open(setup, await open());
            var id = await root.CommitAsync(tx => tx.CreateTaskAsync(rootTask, Obj(("name", "graph")), new TaskOptions(TaskOwnership.Conversation)), Context);
            harness.Resume();
            await harness.WaitForTaskAsync(id, Context);
            await harness.WaitForIdleAsync(Context);
            await harness.CloseAsync(Context);
        }),
    ];

    internal static CompatScenario Named(string name) => All.Single(scenario => scenario.Name == name);
}
