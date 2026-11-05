// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/harness-reports.test.ts (a30c2a81d, 440a5fcdb, 5f312dac4),
// ported in the Pi v1.5.0 sync (area reports). HarnessOptions.OnReport hears one Report: the error and what it is about (ReportInfo, by
// kind). Verbatim apart from the adaptations in HarnessSupport.cs and ChatSupport.cs, and these:
// - toEqual of a ReportInfo is record equality; toMatchObject compares the fields upstream names (a task's kind and conversation, not its
//   id).
// - registry.uninstall(old) disposes the installation; harness.observeCommits with a throwing advance is ObserveCommits with a
//   CommitObserver whose advance throws (area vocabulary, 5f312dac4): reported once as listener_error, then detached; root.agent() is
//   AgentAsync.
// - The host's setTimer throws synchronously, as upstream's does; the Session's document sweep and the scheduler both report it as
//   host_error setTimer, and the case reads the first report with the message.
// - ControlledStorage.failNextCommit refuses the next commit, which fails the Session (Storage's v1.5.0 refusal).
// - A throwing onWake runs on a thread-pool thread here (see harness-sleep's port); the case waits for its report, as upstream's does.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Storage;
using static ChatTests;
using static HarnessTests;
using static Program;
using static SessionTests;
using Deferred = HarnessTests.Deferred;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessReportsCases() => ReportsTests.Cases("harness.reports.");
}

/// <summary>The harness.reports.* cases (upstream harness-reports.test.ts).</summary>
internal static class ReportsTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    private sealed record Reported(string Message, ReportInfo Info);

    private sealed record Opened(IHarness Harness, IRegistry Registry, SyncList<Reported> Reports);

    /// <summary>Opens a Harness with <paramref name="tasks"/> installed that collects every report's message and info (upstream
    /// <c>open</c>).</summary>
    private static async Task<Opened> Open(IReadOnlyList<DurableTask> tasks, IStorage? storage = null, Action<Wake>? onWake = null,
        SetTimer? setTimer = null)
    {
        var registry = Registry.Create();
        registry.Install(Define.Extension(new Extension { Name = "test", Tasks = [.. tasks] }));
        var reports = new SyncList<Reported>();
        var harness = await Harness.OpenAsync(storage ?? new MemoryStorage(), new HarnessOptions
        {
            Models = EmptyDurableModels.Instance,
            Registry = registry,
            OnReport = report => reports.Add(new Reported(report.Error.Message, report.Info)),
            OnWake = onWake,
            SetTimer = setTimer,
        }, Context);
        return new Opened(harness, registry, reports);
    }

    private static ReportInfo? InfoOf(SyncList<Reported> reports, string message) =>
        reports.Snapshot().FirstOrDefault(report => report.Message == message)?.Info;

    private static void SameInfo(ReportInfo expected, ReportInfo? actual, string what) =>
        Check(expected == actual, $"{what}: expected {expected}, actual {actual?.ToString() ?? "undefined"}");

    /// <summary>toMatchObject of an info about a task: kind, callback and extension where given, and the task's kind and conversation.</summary>
    private static void MatchTaskInfo(ReportKind kind, string? extension, HostCallback? callback, string taskKind, ConversationId conversationId,
        ReportInfo? actual, string what) =>
        Check(actual is not null && actual.Kind == kind && (extension is null || actual.Extension == extension) &&
            (callback is null || actual.Callback == callback) && actual.Task?.Kind == taskKind && actual.Task.ConversationId == conversationId,
            $"{what}: expected {ReportInfo.KindName(kind)} {extension}{callback} {taskKind} {conversationId}, actual {actual?.ToString() ?? "undefined"}");

    private static readonly TaskOptions ConversationOwned = new(TaskOwnership.Conversation);

    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix)
    {
        yield return Case(prefix + "reports.say-which-task-reported-a-failure-or-faulted", async () =>
        {
            var faulty = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.faulty",
                Version = 1,
                Initial = _ => O("""{"phase":"run"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["run"] = (_, runtime, _) =>
                    {
                        runtime.Report(new InvalidOperationException("noted"));
                        throw new InvalidOperationException("broken");
                    },
                },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            var (harness, _, reports) = await Open([faulty]);
            var root = await harness.RootAsync(Context);
            var id = await root.CommitAsync(tx => tx.CreateTaskAsync(faulty, JsonValue.Null, ConversationOwned), Context);
            harness.Resume();
            var receipt = await harness.WaitForTaskAsync(id, Context);
            Equal(TaskOutcomeStatus.Faulted, ((TerminalTaskState)receipt.State).Outcome.Status, "outcome");
            await Eventually(() => reports.Count == 2);
            // The task's own failures name the extension that installed it.
            var task = new ReportTask(id, "test.faulty", root.Id);
            var actual = reports.Snapshot();
            Equal("noted", actual[0].Message, "first message");
            SameInfo(new ReportInfo(ReportKind.TaskError) { Task = task, Extension = "test" }, actual[0].Info, "first info");
            Equal("broken", actual[1].Message, "second message");
            SameInfo(new ReportInfo(ReportKind.TaskFaulted) { Task = task, Extension = "test", Reason = ReportReason.Threw }, actual[1].Info, "second info");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "reports.name-the-extension-whose-definition-ran-also-after-another-one-replaced-it", async () =>
        {
            var gate = new Deferred();
            var started = new Deferred();
            DurableTask Definition(string name) => Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.replaced",
                Version = 1,
                Initial = _ => O("""{"phase":"run"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["run"] = async (_, _, _) =>
                    {
                        started.Resolve();
                        await gate.Task;
                        throw new InvalidOperationException($"broken in {name}");
                    },
                },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            var old = Definition("old");
            var (harness, registry, reports) = await Open([]);
            var installed = AddTask(registry, old, "old-extension");
            var root = await harness.RootAsync(Context);
            var id = await root.CommitAsync(tx => tx.CreateTaskAsync(old, JsonValue.Null, ConversationOwned), Context);
            harness.Resume();
            await started.Task;
            installed.Dispose();
            registry.Install(Define.Extension(new Extension { Name = "new-extension", Tasks = [Definition("new")] }));
            gate.Resolve();
            await harness.WaitForTaskAsync(id, Context);
            await Eventually(() => reports.Snapshot().Any(report => report.Info.Kind == ReportKind.TaskFaulted));
            var faulted = reports.Snapshot().First(report => report.Info.Kind == ReportKind.TaskFaulted);
            Equal("broken in old", faulted.Message, "message");
            Equal("old-extension", faulted.Info.Extension, "extension");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "reports.name-the-extension-of-a-failing-section-or-hook-and-tell-them-from-a-failing-environment-builder", async () =>
        {
            var setup = Setup();
            setup.Faux.SetResponses(FauxAssistantMessage("answer"));
            setup.Registry.Install(Define.Extension(new Extension
            {
                Name = "sections",
                Sections = [Define.Section("broken", (_, _) => throw new InvalidOperationException("section"))],
            }));
            setup.Registry.Install(Define.Extension(new Extension
            {
                Name = "hooks",
                Hooks = [Define.Hook(HarnessTasks.GenerationTask, new GenerationHooks
                {
                    AfterResponse = (_, _, _) => throw new InvalidOperationException("hook"),
                })],
            }));
            var reports = new SyncList<Reported>();
            var harness = await Harness.OpenAsync(new MemoryStorage(), new HarnessOptions
            {
                Models = setup.Models,
                Registry = setup.Registry,
                Env = (_, _) => throw new InvalidOperationException("env"),
                OnReport = report => reports.Add(new Reported(report.Error.Message, report.Info)),
            }, Context);
            var root = await harness.RootAsync(Context, new AgentChange { Model = new ModelRef("faux", "faux-1") });
            await (await root.SubmitAsync(SubmissionDraft.Input("hi"), Context)).WaitAsync(Context);
            MatchTaskInfo(ReportKind.HostError, null, HostCallback.Env, "pi.generation", root.Id, InfoOf(reports, "env"), "env");
            MatchTaskInfo(ReportKind.ExtensionError, "sections", null, "pi.generation", root.Id, InfoOf(reports, "section"), "section");
            MatchTaskInfo(ReportKind.ExtensionError, "hooks", null, "pi.generation", root.Id, InfoOf(reports, "hook"), "hook");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "reports.tell-extension-listener-wake-and-timer-failures-from-a-failed-session", async () =>
        {
            var sleeper = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.sleeper",
                Version = 1,
                Initial = _ => O("""{"phase":"run"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["run"] = (_, runtime, ctx) =>
                    {
                        // The host's setTimer throws: reported, and armed with the default timer instead.
                        runtime.SetTimer(() => { }, 10);
                        return runtime.SleepUntilAsync(runtime.Now() + 60_000, O("""{"phase":"done"}"""), ctx);
                    },
                    ["done"] = (_, runtime, ctx) => runtime.CommitAsync((_, _) => Completed(JsonValue.Null), ctx),
                },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            var storage = new ControlledStorage(new MemoryStorage());
            var (harness, registry, reports) = await Open([sleeper], storage,
                onWake: _ => throw new InvalidOperationException("alarm"),
                setTimer: (_, _, _) => throw new InvalidOperationException("no timers"));
            var echo = Define.Tool(new ToolRegistration
            {
                Name = "echo",
                Description = "echo",
                Parameters = ToolsSchema150.EmptyObject,
                Execute = (_, _, _) => Task.FromResult(new ToolExecutionResult { Output = JsonArrayValue.Empty }),
            });
            registry.Install(Define.Extension(new Extension { Name = "tools", Tools = [echo] }));
            registry.Install(Define.Extension(new Extension { Name = "wraps", Wraps = [Define.WrapTool(echo, tool => tool with { Name = "renamed" })] }));
            harness.ObserveCommits(new CommitObserver((_, _) => throw new InvalidOperationException("listener"), _ => { }));
            var root = await harness.RootAsync(Context);
            await root.AgentAsync(Context);
            await root.CommitAsync(tx => tx.CreateTaskAsync(sleeper, JsonValue.Null, ConversationOwned), Context);
            harness.Resume();
            await Eventually(() => new[] { "alarm", "no timers" }.All(message => reports.Snapshot().Any(report => report.Message == message)));
            SameInfo(new ReportInfo(ReportKind.ExtensionError) { ConversationId = root.Id, Extension = "wraps" },
                InfoOf(reports, "Wrapper renamed echo to renamed"), "wrapper");
            SameInfo(ReportInfo.ListenerError, InfoOf(reports, "listener"), "listener");
            SameInfo(ReportInfo.HostError(HostCallback.OnWake), InfoOf(reports, "alarm"), "alarm");
            SameInfo(ReportInfo.HostError(HostCallback.SetTimer), InfoOf(reports, "no timers"), "no timers");

            var failure = new InvalidOperationException("disk gone");
            storage.FailNextCommit(failure);
            try
            {
                await root.CommitAsync(tx => tx.CreateConversationAsync(ConversationOwnership.Ownerless), Context);
            }
            catch (Exception)
            {
                // The refused commit fails the Session.
            }
            await harness.Closed;
            SameInfo(ReportInfo.StorageFailed, InfoOf(reports, "disk gone"), "disk gone");
        });
    }
}
