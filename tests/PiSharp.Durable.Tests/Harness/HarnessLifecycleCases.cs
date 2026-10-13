// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/test/harness-lifecycle.test.ts. Verbatim apart from the adaptations in
// HarnessSupport.cs, ChatSupport.cs and Session/SessionSupport.cs, and these:
// - Backends. Upstream opens these on MemoryStorage (some through ControlledStorage) and reopens a SQLite file for "lets a new Harness open
//   the same Storage once close resolved". harness.lifecycle.* does the same; harness.lifecycle.jsonl.* reruns every case on JSONL (the
//   reopen case reopening a JSONL directory) and harness.lifecycle.sqlite.* reruns the cases that need no reopen on a fresh SQLite file.
// - `{...context, abortSignal}` is DurableContext.WithCancel(); `controller.abort(error)` is Cancel(error).
// - `class FailingClose extends ControlledStorage` is FailingCloseStorage, a decorator over ControlledStorage.
// - "leaves no subscription behind ...": upstream replaces `harness.subscribeCommits` to count the subscriptions made after the patch. A
//   C# method cannot be replaced, so the case counts the Session's commit listeners (by reflection) against the count before the cancelled
//   acquisitions; it reads the same thing: no observer subscription outlives a cancelled acquisition.
// - `/closed/.test(String(error))` tests the exception's message.
using System.Reflection;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Storage.Jsonl;
using static ChatTests;
using static HarnessTests;
using static Program;
using static SessionTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessLifecycleCases() =>
        LifecycleTests.Cases("harness.lifecycle.", () => Task.FromResult<IStorage>(new MemoryStorage()), LifecycleSqliteLocation);

    private static IEnumerable<(string, Func<Task>)> HarnessLifecycleJsonlCases() =>
        LifecycleTests.Cases("harness.lifecycle.jsonl.", () => LifecycleJsonlLocation()(), LifecycleJsonlLocation);

    private static IEnumerable<(string, Func<Task>)> HarnessLifecycleSqliteCases() =>
        LifecycleTests.Cases("harness.lifecycle.sqlite.", () => LifecycleSqliteLocation()(), null);

    private static StructuredTests.OpenLocation LifecycleSqliteLocation()
    {
        var path = Path.Combine(Temp("lifecycle-sqlite"), "session.sqlite");
        return async () => await SqliteStorage.OpenAsync(path);
    }

    private static StructuredTests.OpenLocation LifecycleJsonlLocation()
    {
        var directory = Temp("lifecycle-jsonl");
        return async () => await JsonlStorage.OpenAsync(directory, DurableContext.Background);
    }
}

/// <summary>The harness.lifecycle.* cases (upstream harness-lifecycle.test.ts).</summary>
internal static class LifecycleTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    /// <summary>
    /// The harness.lifecycle.* cases on storages from <paramref name="fresh"/>; the reopen case uses locations from <paramref name="reopenable"/>
    /// and is left out without one.
    /// </summary>
    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix, Func<Task<IStorage>> fresh, Func<StructuredTests.OpenLocation>? reopenable) =>
        All(prefix, fresh, reopenable);

    // ─── Support ────────────────────────────────────────────────────────────

    /// <summary>Upstream <c>oneStep(name, run)</c>: a one-phase task running <paramref name="run"/> and completing with null.</summary>
    private static DurableTask OneStep(string name, Func<Task>? run = null) => Tasks.DefineTask(new TaskDefinition
    {
        Name = name,
        Version = 1,
        Initial = _ => O("""{"phase":"run"}"""),
        Phases = new Dictionary<string, TaskPhaseHandler>
        {
            ["run"] = async (_, runtime, ctx) =>
            {
                if (run is not null) await run();
                await runtime.CommitAsync((_, _) => Completed(null), ctx);
            },
        },
        Abort = (_, _, _) => Task.CompletedTask,
    });

    /// <summary>Upstream <c>start(conversation, task, background)</c>.</summary>
    private static Task<TaskId> Start(IConversation conversation, DurableTask task, bool background = false) =>
        conversation.CommitAsync(tx => tx.CreateTaskAsync(task, JsonValue.Null, new TaskOptions(TaskOwnership.Conversation) { Background = background }),
            Context);

    /// <summary>Upstream <c>unloadDocuments(harness)</c>: drops the loaded trackers, so the next acquisition reads Storage on the line.</summary>
    private static Task UnloadDocuments(IHarness harness) => ((DurableHarness)harness).UnloadDocumentsAsync();

    /// <summary>Upstream <c>storageOpen(storage)</c>: whether a raw Storage read still succeeds, as code of a joined invocation relies on.</summary>
    private static async Task<bool> StorageOpen(IStorage storage)
    {
        try
        {
            await storage.GetTaskAsync(new TaskId(1), Context);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Upstream <c>seedRunningTask(storage)</c>: commits a conversation with a <c>running</c> task, as a crash leaves it, so open has a
    /// reconciliation commit to fail.</summary>
    private static async Task SeedRunningTask(IStorage storage)
    {
        var conversationId = await storage.MintIdAsync<ConversationId>();
        var id = await storage.MintIdAsync<TaskId>();
        var task = new TaskRecord
        {
            Id = id,
            ConversationId = conversationId,
            Kind = "test.seeded",
            Version = 1,
            Input = JsonValue.Null,
            Background = false,
            AbortRequested = false,
            State = NextTaskState.Running(O("""{"phase":"run"}""")),
        };
        await storage.CommitAsync([new ConversationWrite(new ConversationRecord(conversationId)), new TaskWrite(task)], Context);
    }

    /// <summary>How many commit listeners the Harness's Session holds.</summary>
    private static int CommitSubscriptions(IHarness harness)
    {
        var field = typeof(PiSharp.Durable.Session).GetField("commitListeners", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var listeners = (System.Collections.ICollection)field.GetValue(harness)!;
        lock (listeners.SyncRoot) return listeners.Count;
    }

    /// <summary><c>promise.then(() => "resolved", error => /closed/.test(String(error)) ? "closed" : String(error))</c>.</summary>
    private static async Task<string> Outcome(Func<Task> operation)
    {
        try
        {
            await operation();
            return "resolved";
        }
        catch (Exception error)
        {
            return error.Message.Contains("closed", StringComparison.Ordinal) ? "closed" : error.ToString();
        }
    }

    /// <summary><c>promise.then(value => value === undefined ? "undefined" : "resolved", error => ...)</c>.</summary>
    private static async Task<string> Settle<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation() is null ? "undefined" : "resolved";
        }
        catch (Exception error)
        {
            return error.Message.Contains("closed", StringComparison.Ordinal) ? "closed" : error.ToString();
        }
    }

    // ─── Cases ──────────────────────────────────────────────────────────────

    private static IEnumerable<(string, Func<Task>)> All(string prefix, Func<Task<IStorage>> fresh, Func<StructuredTests.OpenLocation>? reopenable)
    {
        // ─── Harness open ───────────────────────────────────────────────────

        yield return Case(prefix + "closes-without-a-cancelled-caller-context-and-rethrows-the-original-error-when-open-fails", async () =>
        {
            var storage = new ControlledStorage(await fresh());
            await SeedRunningTask(storage);
            var reader = new CountingReader(Registry.Create());
            var held = storage.HoldCommits();
            storage.FailNextCommit(new InvalidOperationException("disk full"));
            var controller = Context.WithCancel();
            var opening = Harness.OpenAsync(storage, new HarnessOptions { Models = EmptyDurableModels.Instance, Registry = reader }, controller.Context);
            await held.Entered;
            controller.Cancel(new InvalidOperationException("caller gave up"));
            held.Release();
            await RejectsAsync(() => opening, "disk full", "open");
            Equal(0, reader.Subscriptions, "subscriptions");
            Check(!await StorageOpen(storage), "storage closed");
        });

        yield return Case(prefix + "reports-a-failing-close-and-still-rethrows-the-open-error", async () =>
        {
            var controlled = new ControlledStorage(await fresh());
            var storage = new FailingCloseStorage(controlled);
            await SeedRunningTask(storage);
            controlled.FailNextCommit(new InvalidOperationException("disk full"));
            var reports = new SyncList<Exception>();
            var opening = Harness.OpenAsync(storage, new HarnessOptions
            {
                Models = EmptyDurableModels.Instance,
                Registry = Registry.Create(),
                OnReport = reports.Add,
            }, Context);
            await RejectsAsync(() => opening, "disk full", "open");
            Seq(["close failed"], reports.Snapshot().Select(error => error.Message), "reports");
        });

        // ─── Harness close ──────────────────────────────────────────────────

        yield return Case(prefix + "joins-a-task-handler-that-ignores-its-signal-before-closing-storage", async () =>
        {
            var storage = await fresh();
            var reached = new HarnessTests.Deferred();
            var gate = new HarnessTests.Deferred();
            bool? readAfterRelease = null;
            var stubborn = OneStep("test.close-stubborn", async () =>
            {
                reached.Resolve();
                await gate.Task;
                readAfterRelease = await StorageOpen(storage);
            });
            var (harness, _, _) = await OpenTasks(storage, [stubborn]);
            await Start(await harness.RootAsync(Context), stubborn);
            harness.Resume();
            await reached.Task;
            var closing = harness.CloseAsync(Context);
            Check(!await Settled(closing), "close waits");
            Check(await StorageOpen(storage), "storage open while joining");
            gate.Resolve();
            await closing;
            Equal(true, readAfterRelease, "read after release");
            Check(!await StorageOpen(storage), "storage closed");
        });

        yield return Case(prefix + "joins-a-tool-execute-and-a-hook-that-ignore-their-signal-before-closing-storage", async () =>
        {
            foreach (var where in new[] { "tool", "hook" })
            {
                var setup = Setup();
                var storage = await fresh();
                var reached = new HarnessTests.Deferred();
                var gate = new HarnessTests.Deferred();
                bool? readAfterRelease = null;
                async Task Stubborn()
                {
                    reached.Resolve();
                    await gate.Task;
                    readAfterRelease = await StorageOpen(storage);
                }
                AddTool(setup.Registry, Define.Tool(new ToolRegistration
                {
                    Name = "wait",
                    Description = "wait",
                    Execute = async (_, _, _) =>
                    {
                        if (where == "tool") await Stubborn();
                        return new ToolExecutionResult { Content = JsonArrayValue.Empty };
                    },
                }));
                AddHooks(setup.Registry, HarnessTasks.GenerationTask, new GenerationHooks
                {
                    BeforeRequest = async (_, _, _) =>
                    {
                        if (where == "hook") await Stubborn();
                        return null;
                    },
                });
                setup.Faux.SetResponses(FauxAssistantMessage([FauxToolCall("wait", JsonObjectValue.Empty, "c1")], stopReason: "toolUse"),
                    FauxAssistantMessage("done"));
                var (harness, root) = await OpenChat(storage, setup);
                await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
                await reached.Task;
                var closing = harness.CloseAsync(Context);
                Check(!await Settled(closing), $"{where}: close waits");
                Check(await StorageOpen(storage), $"{where}: storage open while joining");
                gate.Resolve();
                await closing;
                Equal(true, readAfterRelease, $"{where}: read after release");
                Check(!await StorageOpen(storage), $"{where}: storage closed");
            }
        });

        if (reopenable is not null)
        {
            yield return Case(prefix + "lets-a-new-harness-open-the-same-storage-once-close-resolved-no-old-invocation-code-runs", async () =>
            {
                var location = reopenable();
                var log = new SyncList<string>();
                var gate = new HarnessTests.Deferred();
                var generation = 1;
                var stubborn = OneStep("test.generations", async () =>
                {
                    var mine = Volatile.Read(ref generation);
                    log.Add($"start {mine}");
                    if (mine == 1) await gate.Task;
                    log.Add($"end {mine}");
                });
                var first = await OpenTasks(await location(), [stubborn]);
                var id = await Start(await first.Harness.RootAsync(Context), stubborn);
                first.Harness.Resume();
                await Eventually(() => log.Count == 1);
                async Task Closing()
                {
                    await first.Harness.CloseAsync(Context);
                    log.Add("closed");
                }
                var closing = Closing();
                Check(!await Settled(closing), "close waits");
                gate.Resolve();
                await closing;
                Seq(["start 1", "end 1", "closed"], log.Snapshot(), "log after close");
                Volatile.Write(ref generation, 2);
                var second = await OpenTasks(await location(), [stubborn]);
                second.Harness.Resume();
                await second.Harness.WaitForTaskAsync(id, Context);
                Seq(["start 1", "end 1", "closed", "start 2", "end 2"], log.Snapshot(), "log after reopen");
                await second.Harness.CloseAsync(Context);
            });
        }

        yield return Case(prefix + "keeps-shutting-down-after-a-cancelled-close-and-a-second-close-awaits-the-same-shutdown", async () =>
        {
            var storage = await fresh();
            var reached = new HarnessTests.Deferred();
            var gate = new HarnessTests.Deferred();
            var stubborn = OneStep("test.close-cancelled", async () =>
            {
                reached.Resolve();
                await gate.Task;
            });
            var (harness, _, _) = await OpenTasks(storage, [stubborn]);
            await Start(await harness.RootAsync(Context), stubborn);
            harness.Resume();
            await reached.Task;
            var controller = Context.WithCancel();
            var cancelled = harness.CloseAsync(controller.Context);
            controller.Cancel(new InvalidOperationException("stop waiting"));
            await RejectsAsync(() => cancelled, "stop waiting", "cancelled close");
            // Admission stays sealed and the invocation still holds Storage open.
            await RejectsAsync(() => harness.CommitAsync(_ => { }, Context), "closed", "commit after close");
            Check(await StorageOpen(storage), "storage open while joining");
            var second = harness.CloseAsync(Context);
            Check(!await Settled(second), "second close waits");
            gate.Resolve();
            await second;
            Check(!await StorageOpen(storage), "storage closed");
        });

        yield return Case(prefix + "settles-durably-a-commit-whose-committer-was-cancelled-while-it-was-in-storage", async () =>
        {
            var storage = new ControlledStorage(await fresh());
            var (harness, _, _) = await OpenTasks(storage, []);
            var root = await harness.RootAsync(Context);
            var held = storage.HoldCommits();
            var controller = Context.WithCancel();
            var committing = root.CommitAsync(async tx => (await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "note" })).Id, controller.Context);
            await held.Entered;
            controller.Cancel(new InvalidOperationException("committer gave up"));
            held.Release();
            var id = await committing;
            var page = await root.EntriesAsync(new EntryQueryBounds(), 10, null, Context);
            Seq([id], page.Items.Select(entry => entry.Id), "entries");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "publishes-no-frame-to-states-and-watches-from-a-commit-that-settles-during-close", async () =>
        {
            var notes = Documents.DefineSessionDoc("test.close-frames", 1, () => O("""{"text":""}"""));
            var storage = new ControlledStorage(await fresh());
            var (harness, _, _) = await OpenTasks(storage, []);
            var root = await harness.RootAsync(Context);
            await harness.CommitAsync(async tx => (await tx.DocAsync(notes)).Set("text", "before"), Context);
            var docState = (await harness.DocumentStateAsync(notes, Context))!;
            var docWatch = (await harness.WatchDocAsync(notes, Context))!;
            var viewState = await root.ViewStateAsync(Context);
            var viewWatch = await root.WatchAsync(Context);
            var graphState = await harness.TaskGraphAsync(Context);
            var graphWatch = await harness.WatchTaskGraphAsync(Context);
            var frames = new SyncList<string>();
            graphState.Subscribe((_, _, delivery) => { if (delivery.Kind == ReplicatedStateDeliveryKind.Update) frames.Add("graphState"); });
            graphWatch.Start((_, _, _) => { frames.Add("graphWatch"); return Task.CompletedTask; });
            docState.Subscribe((_, _, delivery) => { if (delivery.Kind == ReplicatedStateDeliveryKind.Update) frames.Add("docState"); });
            viewState.Subscribe((_, _, delivery) => { if (delivery.Kind == ReplicatedStateDeliveryKind.Update) frames.Add("viewState"); });
            docWatch.Start((_, _, _) => { frames.Add("docWatch"); return Task.CompletedTask; });
            viewWatch.Start((_, _, _) => { frames.Add("viewWatch"); return Task.CompletedTask; });
            var docValue = docState.Value;
            var viewValue = viewState.Value;

            var held = storage.HoldCommits();
            var committing = harness.CommitAsync(async tx =>
            {
                (await tx.DocAsync(notes)).Set("text", "during close");
                await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "note" });
                await tx.CreateTaskAsync(OneStep("test.close-graph"), JsonValue.Null, new TaskOptions(TaskOwnership.Conversation) { ConversationId = root.Id });
            }, Context);
            await held.Entered;
            var closing = harness.CloseAsync(Context);
            held.Release();
            await committing;
            await closing;
            await Flush();
            Seq([], frames.Snapshot(), "frames");
            Same(docValue, docState.Value, "doc state value");
            Same(viewValue, viewState.Value, "view state value");
            SameJson("""{"tasks":{}}""", graphState.Value.ToJson(), "graph state value");
            Equal(WatchEnd.SessionClosed, await graphWatch.Closed, "graph watch end");
            Equal(WatchEnd.SessionClosed, await docWatch.Closed, "doc watch end");
            Equal(WatchEnd.SessionClosed, await viewWatch.Closed, "view watch end");
            // The commit itself settled.
            Check(storage.LastCommit.Any(write => write is EntryWrite), "entry committed");
        });

        yield return Case(prefix + "leaves-no-subscription-behind-a-watch-acquisition-cancelled-on-the-line", async () =>
        {
            var notes = Documents.DefineSessionDoc("test.cancelled-watch", 1, () => O("""{"text":""}"""));
            var storage = new ControlledStorage(await fresh());
            var (harness, _, _) = await OpenTasks(storage, []);
            var root = await harness.RootAsync(Context);
            await harness.CommitAsync(async tx => (await tx.DocAsync(notes)).Set("text", "x"), Context);
            // Count commit subscriptions of document observers.
            var subscriptions = CommitSubscriptions(harness);

            // Cancel a document watch while its acquisition loads the document on the line.
            await UnloadDocuments(harness);
            var find = storage.HoldFindDocument();
            var controller = Context.WithCancel();
            var docWatch = harness.WatchDocAsync(notes, controller.Context);
            await find.Entered;
            controller.Cancel(new InvalidOperationException("cancelled"));
            find.Release();
            await RejectsAsync(() => docWatch, "cancelled", "doc watch");
            Equal(subscriptions, CommitSubscriptions(harness), "subscriptions after the doc watch");

            // Cancel a view watch while it builds its mount on the line.
            await UnloadDocuments(harness);
            find = storage.HoldFindDocument();
            controller = Context.WithCancel();
            var viewWatch = root.WatchAsync(controller.Context);
            await find.Entered;
            controller.Cancel(new InvalidOperationException("cancelled"));
            find.Release();
            await RejectsAsync(() => viewWatch, "cancelled", "view watch");
            // No observer kept the mount: the next observer builds a new one, and the one after that another.
            var first = await root.ViewStateAsync(Context);
            var firstValue = first.Value;
            first.Dispose();
            var second = await root.ViewStateAsync(Context);
            Check(!ReferenceEquals(firstValue, second.Value), "a new mount");
            second.Dispose();
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "rejects-conversation-and-harness-operations-once-close-begins-inspect-queued-before-reports-closing", async () =>
        {
            var storage = new ControlledStorage(await fresh());
            var (harness, _, _) = await OpenTasks(storage, []);
            var root = await harness.RootAsync(Context);
            var entry = await root.CommitAsync(tx => tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "note" }), Context);
            var submission = await root.SubmitAsync(SubmissionDraft.Write(new EntryDraft { Kind = "note" }), Context);

            var held = storage.HoldCommits();
            var blocking = harness.CommitAsync(async tx => { await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "blocker" }); }, Context);
            await held.Entered;
            var queuedInspect = harness.InspectAsync(Context);
            var closing = harness.CloseAsync(Context);
            var operations = new (string Name, Func<Task> Run)[]
            {
                ("submit", () => root.SubmitAsync(SubmissionDraft.Input("x"), Context)),
                ("agent", () => root.AgentAsync(Context)),
                ("configure", () => root.ConfigureAsync(new AgentChange { ThinkingLevel = "low" }, Context)),
                ("commit", () => root.CommitAsync(_ => { }, Context)),
                ("context", () => root.ContextAsync(Context)),
                ("entries", () => root.EntriesAsync(new EntryQueryBounds(), 10, null, Context)),
                ("fork", () => root.ForkAsync(entry.Id, new ConversationCreateOptions(ConversationOwnership.Ownerless), Context)),
                ("compact", () => root.CompactAsync(null, Context)),
                ("reset", () => root.ResetAsync(null, Context)),
                ("abort", () => root.AbortAsync(Context)),
                ("conversationIdle", () => root.WaitForIdleAsync(Context)),
                ("viewState", () => root.ViewStateAsync(Context)),
                ("watch", () => root.WatchAsync(Context)),
                ("taskGraph", () => harness.TaskGraphAsync(Context)),
                ("watchTaskGraph", () => harness.WatchTaskGraphAsync(Context)),
                ("status", () => submission.StatusAsync(Context)),
                ("wait", () => submission.WaitAsync(Context)),
                ("abortSubmission", () => submission.AbortAsync(Context)),
                ("root", () => harness.RootAsync(Context)),
                ("conversation", () => harness.ConversationAsync(root.Id, Context)),
                ("createConversation", () => harness.CreateConversationAsync(new ConversationCreateOptions(ConversationOwnership.Ownerless), Context)),
                ("getTask", () => harness.GetTaskAsync(new TaskId(1), Context)),
                ("inspect", () => harness.InspectAsync(Context)),
                ("submission", () => harness.SubmissionAsync(submission.Id, Context)),
                ("abortTask", () => harness.AbortTaskAsync(new TaskId(1), Context)),
                ("waitForTask", () => harness.WaitForTaskAsync(new TaskId(1), Context)),
                ("harnessIdle", () => harness.WaitForIdleAsync(Context)),
                ("usage", () => harness.UsageAsync(Context)),
            };
            var outcomes = new List<string>();
            foreach (var (name, operation) in operations) outcomes.Add($"{name}: {await Outcome(operation)}");
            Seq(operations.Select(operation => $"{operation.Name}: closed"), outcomes, "outcomes");
            Throws(() => harness.Resume(), "closed", "resume");
            held.Release();
            await blocking;
            Equal(HarnessScheduling.Closing, (await queuedInspect).Scheduling, "queued inspect");
            await closing;
        });

        yield return Case(prefix + "completes-reads-queued-at-the-seal-and-rejects-queued-waits-and-acquisitions-that-would-follow-commits", async () =>
        {
            var notes = Documents.DefineSessionDoc("test.queued-at-seal", 1, () => O("""{"text":""}"""));
            var absent = Documents.DefineSessionDoc("test.queued-absent", 1, () => O("""{"text":""}"""));
            var pending = OneStep("test.queued-pending");
            var storage = new ControlledStorage(await fresh());
            var (harness, _, _) = await OpenTasks(storage, []);
            var root = await harness.RootAsync(Context);
            await harness.CommitAsync(async tx => { await tx.DocAsync(notes); }, Context);
            var taskId = await Start(root, pending);
            var write = await root.SubmitAsync(SubmissionDraft.Write(new EntryDraft { Kind = "note" }), Context);
            await write.WaitAsync(Context);

            var held = storage.HoldCommits();
            var blocking = harness.CommitAsync(async tx => { await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "blocker" }); }, Context);
            await held.Entered;
            var queued = new (string Name, Task<string> Outcome)[]
            {
                ("context", Settle(() => root.ContextAsync(Context))),
                ("snapshot", Settle(() => harness.SnapshotAsync(notes, Context))),
                ("settledWait", Settle(() => write.WaitAsync(Context))),
                ("absentWatch", Settle(() => harness.WatchDocAsync(absent, Context))),
                ("absentState", Settle(() => harness.DocumentStateAsync(absent, Context))),
                ("waitForTask", Settle(() => harness.WaitForTaskAsync(taskId, Context))),
                ("documentState", Settle(() => harness.DocumentStateAsync(notes, Context))),
                ("watchDoc", Settle(() => harness.WatchDocAsync(notes, Context))),
                ("viewState", Settle(() => root.ViewStateAsync(Context))),
                ("watch", Settle(() => root.WatchAsync(Context))),
                ("taskGraph", Settle(() => harness.TaskGraphAsync(Context))),
            };
            var closing = harness.CloseAsync(Context);
            held.Release();
            await blocking;
            var outcomes = new List<string>();
            foreach (var (name, outcome) in queued) outcomes.Add($"{name}: {await outcome}");
            Seq([
                "context: resolved",
                "snapshot: resolved",
                "settledWait: resolved",
                "absentWatch: undefined",
                "absentState: undefined",
                "waitForTask: closed",
                "documentState: closed",
                "watchDoc: closed",
                "viewState: closed",
                "watch: closed",
                "taskGraph: closed",
            ], outcomes, "outcomes");
            await closing;
        });

        // ─── scheduling on a paused Harness ─────────────────────────────────

        // Upstream paused(): a paused Harness with one background task pending and a queued write submission.
        async Task<(IHarness Harness, IConversation Root, TaskId Id, SubmissionId SubmissionId, Func<bool> Ran)> Paused()
        {
            var ran = 0;
            var marker = OneStep("test.marker", () =>
            {
                Volatile.Write(ref ran, 1);
                return Task.CompletedTask;
            });
            var (harness, _, _) = await OpenTasks(await fresh(), [marker]);
            var root = await harness.RootAsync(Context);
            // Background, so idle waits and conversation abort leave it alone.
            var id = await Start(root, marker, background: true);
            var submissionId = await ((DurableHarness)harness).CommitWithAsync(async tx =>
                (await tx.CreateSubmissionAsync(new SubmissionCreate { ConversationId = root.Id, Type = SubmissionType.Write, Status = SubmissionStatus.Queued })).Id,
                Context);
            return (harness, root, id, submissionId, () => Volatile.Read(ref ran) == 1);
        }

        yield return Case(prefix + "never-schedules-from-a-read-only-viewer", async () =>
        {
            var (harness, root, id, submissionId, ran) = await Paused();
            var notes = Documents.DefineSessionDoc("test.viewer-notes", 1, () => O("""{"text":""}"""));
            await harness.CommitAsync(async tx => { await tx.DocAsync(notes); }, Context);
            await harness.InspectAsync(Context);
            await harness.GetTaskAsync(id, Context);
            await harness.UsageAsync(Context);
            await harness.ConversationAsync(root.Id, Context);
            await harness.SnapshotAsync(notes, Context);
            (await harness.DocumentStateAsync(notes, Context))!.Dispose();
            await (await harness.WatchDocAsync(notes, Context))!.StopAsync();
            var submission = (await harness.SubmissionAsync(submissionId, Context))!;
            await submission.StatusAsync(Context);
            await root.AgentAsync(Context);
            await root.ContextAsync(Context);
            await root.EntriesAsync(new EntryQueryBounds(), 10, null, Context);
            (await root.ViewStateAsync(Context)).Dispose();
            await (await root.WatchAsync(Context)).StopAsync();
            (await harness.TaskGraphAsync(Context)).Dispose();
            await (await harness.WatchTaskGraphAsync(Context)).StopAsync();
            await Flush();
            await Flush();
            Check(!ran(), "the background task did not run");
            Equal(HarnessScheduling.Paused, (await harness.InspectAsync(Context)).Scheduling, "scheduling");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "schedules-from-every-progress-call", async () =>
        {
            var calls = new (string Name, Func<IHarness, IConversation, TaskId, SubmissionId, Task> Call)[]
            {
                ("submit", (_, root, _, _) => root.SubmitAsync(SubmissionDraft.Write(new EntryDraft { Kind = "note" }), Context)),
                ("compact", (_, root, _, _) => root.CompactAsync(null, Context)),
                ("abort", (_, root, _, _) => root.AbortAsync(Context)),
                ("conversationIdle", (_, root, _, _) => root.WaitForIdleAsync(Context)),
                ("submissionWait", async (harness, _, _, submissionId) => await (await harness.SubmissionAsync(submissionId, Context))!.WaitAsync(Context)),
                ("waitForTask", (harness, _, id, _) => harness.WaitForTaskAsync(id, Context)),
                ("harnessIdle", (harness, _, _, _) => harness.WaitForIdleAsync(Context)),
            };
            foreach (var (name, call) in calls)
            {
                var (harness, root, id, submissionId, ran) = await Paused();
                // A queued write's wait settles only on placement; close rejects it.
                var pending = Swallow(call(harness, root, id, submissionId));
                try
                {
                    await Eventually(ran);
                }
                catch (InvalidOperationException)
                {
                    throw new InvalidOperationException($"{name} did not enable scheduling");
                }
                await harness.CloseAsync(Context);
                await pending;
            }

            static async Task Swallow(Task task)
            {
                try { await task; }
                catch { }
            }
        });

        // ─── registry changes before resume ─────────────────────────────────

        yield return Case(prefix + "runs-what-the-registry-holds-at-resume-a-definition-installed-or-replaced-after-open", async () =>
        {
            var log = new SyncList<string>();
            DurableTask DefineLate(string label) => OneStep("test.late-definition", () =>
            {
                log.Add(label);
                return Task.CompletedTask;
            });
            var first = await OpenTasks(await fresh(), []);
            var root = await first.Harness.RootAsync(Context);
            var missing = await Start(root, DefineLate("unused"));
            var inspected = (await first.Harness.InspectAsync(Context)).Tasks;
            Check(inspected.Length == 1 && inspected[0].State is { Kind: TaskInspectionKind.Blocked, Reason: BlockedReason.MissingTask },
                $"blocked: {string.Join(", ", inspected.Select(task => task.State))}");
            var installed = AddTask(first.Registry, DefineLate("v1"));
            inspected = (await first.Harness.InspectAsync(Context)).Tasks;
            Check(inspected.Length == 1 && inspected[0].State.Kind == TaskInspectionKind.Ready, $"ready: {string.Join(", ", inspected.Select(task => task.State))}");
            // The same extension name replaces the definition in place, still before resume.
            installed.Dispose();
            AddTask(first.Registry, DefineLate("v2"));
            first.Harness.Resume();
            await first.Harness.WaitForTaskAsync(missing, Context);
            Seq(["v2"], log.Snapshot(), "log");
            await first.Harness.CloseAsync(Context);
        });
    }
}

/// <summary>Closes its storage, then throws (upstream's <c>class FailingClose extends ControlledStorage</c>, as a decorator).</summary>
internal sealed class FailingCloseStorage(ControlledStorage inner) : IStorage
{
    public async Task CloseAsync(DurableContext context)
    {
        await inner.CloseAsync(context);
        throw new InvalidOperationException("close failed");
    }

    public Task<Seq> CommitAsync(IReadOnlyList<StorageWrite> writes, DurableContext context) => inner.CommitAsync(writes, context);
    public Task<TId> MintIdAsync<TId>() where TId : struct, IDurableId<TId> => inner.MintIdAsync<TId>();
    public Task<StoredDocument?> GetDocumentAsync(DocumentId id, DocumentPoint at, DurableContext context) => inner.GetDocumentAsync(id, at, context);
    public Task<DocumentRecord?> FindDocumentAsync(DocumentAddress address, DocumentPoint at, DurableContext context) =>
        inner.FindDocumentAsync(address, at, context);
    public Task<ConversationRecord?> GetConversationAsync(ConversationId id, DurableContext context) => inner.GetConversationAsync(id, context);
    public Task<Page<ConversationRecord>> ScanConversationsAsync(ConversationQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        inner.ScanConversationsAsync(query, limit, cursor, context);
    public Task<CommittedEntry?> GetEntryAsync(EntryId id, DurableContext context) => inner.GetEntryAsync(id, context);
    public Task<CommittedEntry?> GetEntryAsync(ConversationId conversationId, EntryId id, DurableContext context) =>
        inner.GetEntryAsync(conversationId, id, context);
    public Task<EntryRecord?> FindLatestHeadMarkerAsync(ConversationId conversationId, EntryId? atOrBeforeEntryId, DurableContext context) =>
        inner.FindLatestHeadMarkerAsync(conversationId, atOrBeforeEntryId, context);
    public Task<Page<EntryRecord>> ScanEntriesAsync(EntryQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        inner.ScanEntriesAsync(query, limit, cursor, context);
    public Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context) => inner.GetTaskAsync(id, context);
    public Task<Page<TaskRecord>> ScanTasksAsync(TaskQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        inner.ScanTasksAsync(query, limit, cursor, context);
    public Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id, DurableContext context) => inner.GetSubmissionAsync(id, context);
    public Task<Page<SubmissionRecord>> ScanSubmissionsAsync(SubmissionQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        inner.ScanSubmissionsAsync(query, limit, cursor, context);
    public Task<SubmissionRecord?> GetSubmissionByRequestAsync(ConversationId conversationId, string requestId, DurableContext context) =>
        inner.GetSubmissionByRequestAsync(conversationId, requestId, context);
    public Task<Page<DocumentRecord>> ScanDocumentsAsync(DocumentQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        inner.ScanDocumentsAsync(query, limit, cursor, context);
}
