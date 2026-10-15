// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/harness-storage-failure.test.ts. Verbatim apart from the
// adaptations in HarnessSupport.cs and SessionSupport.cs, and these:
// - Upstream's storages extend ControlledStorage or MemoryStorage; here they are decorators over a ControlledStorage (DelegatingStorage).
// - SessionFailed's `cause` is SessionFailedException.InnerException; toMatchObject({ name: "SessionFailed" }) is the exception type.
// - "reports a listener or onReport whose promise rejects ...": a listener's promise is the task an asynchronous SubscribeCommits or
//   SubscribeClose overload returns. OnReport is an Action, so a handler that "rejects" is one that throws (an async void handler's
//   exception escapes to the thread pool, which no caller can contain); unhandled means TaskScheduler.UnobservedTaskException or
//   ReplicatedState.UnhandledError.
// - "contains a fulfilled thenable ..." and "contains a rejected promise from another realm": .NET has neither thenables nor realms. The
//   cases check Session.Observe (contained's handling of a returned promise) with a task that completes later and a task faulted on another
//   thread.
// - "faults only the task ... for a thrown value that cannot be formatted": a .NET throw is an Exception, so the value is an exception whose
//   Message getter throws, and the fault message is DurableErrors.MessageOf's fallback, the type name, where upstream's is "[object Object]".
// - process.on("unhandledRejection") is TaskScheduler.UnobservedTaskException after a full collection.
// - running(): upstream polls on timers, so by the time it sees the record `running` the reservation's continuations (the invocation's first
//   step commit and its phase's start) have run as microtasks. Here they continue on the thread pool and can queue behind the next commit the
//   test makes, so Running also awaits Waiting's phase start.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Storage;
using static HarnessTests;
using static Program;
using static SessionTests;
using Deferred = HarnessTests.Deferred;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessStorageFailureCases() => StorageFailureTests.Cases("harness.storage-failure.");
}

internal static class StorageFailureTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    /// <summary>
    /// Waits for its signal, records that it saw it, and ends <c>aborted</c> through its abort handler (upstream <c>Waiting</c>). Resolves
    /// <paramref name="started"/> when its phase starts (see the header).
    /// </summary>
    private static DurableTask Waiting(SyncList<string> signals, Deferred started) => Tasks.DefineTask(new TaskDefinition
    {
        Name = "test.waiting",
        Version = 1,
        Initial = _ => O("""{"phase":"work"}"""),
        Phases = new Dictionary<string, TaskPhaseHandler>
        {
            ["work"] = async (_, runtime, _) =>
            {
                started.Resolve();
                try { await Aborted(runtime.Signal); }
                catch (OperationCanceledException) { }
                signals.Add("aborted");
            },
        },
        Abort = (_, runtime, abortContext) => runtime.CommitAsync((_, _) => NextTaskState.Aborted(), abortContext),
    });

    private sealed record Started(IHarness Harness, IConversation Root, SyncList<Exception> Reports, SyncList<string> Signals, DurableTask Waiting)
    {
        /// <summary>Resolves when Waiting's phase starts.</summary>
        public required Deferred PhaseStarted { get; init; }
    }

    private static async Task<Started> Start(IStorage storage)
    {
        var signals = new SyncList<string>();
        var started = new Deferred();
        var waiting = Waiting(signals, started);
        var opened = await OpenTasks(storage, [waiting]);
        var root = await opened.Harness.RootAsync(Context);
        return new Started(opened.Harness, root, opened.Reports, signals, waiting) { PhaseStarted = started };
    }

    /// <summary>Upstream <c>running()</c>, and Waiting's phase has started (see the header).</summary>
    private static async Task Running(IHarness harness, TaskId id, Deferred phaseStarted)
    {
        while ((await harness.GetTaskAsync(id, Context))?.State.Status != DurableTaskStatus.Running) await Task.Delay(1);
        await phaseStarted.Task;
    }

    private static Task<EntryRecord> Note(IConversation root) => root.CommitAsync(tx => tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "note" }), Context);

    /// <summary><c>rejects.toMatchObject({ name: "SessionFailed", cause })</c>.</summary>
    private static async Task FailedWith(Exception cause, Func<Task> run, string what)
    {
        var error = await ThrowsAsync<SessionFailedException>(run, what);
        Same(cause, error.InnerException, what + " cause");
    }

    /// <summary><c>rejects.toBe(error)</c>.</summary>
    private static async Task RejectsWith(Exception expected, Func<Task> run, string what)
    {
        try { await run(); }
        catch (Exception error)
        {
            Same(expected, error, what);
            return;
        }
        throw new InvalidOperationException(what + ": expected a rejection");
    }

    private static TaskOptions OwnConversation() => new(TaskOwnership.Conversation);

    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix)
    {
        // ─── a failed Storage call ───

        yield return Case(prefix + "ends-everything-with-the-first-error-the-call-later-calls-waits-watches-streams-running-tasks", async () =>
        {
            var storage = new ControlledStorage(new MemoryStorage());
            var started = await Start(storage);
            var (harness, root, reports, signals, waiting) = started;
            var id = await root.CommitAsync(tx => tx.CreateTaskAsync(waiting, JsonValue.Null, OwnConversation()), Context);
            harness.Resume();
            await Running(harness, id, started.PhaseStarted);
            var task = harness.WaitForTaskAsync(id, Context);
            var idle = harness.WaitForIdleAsync(Context);
            var watch = (await harness.WatchDocAsync(Live.LiveDoc, root.Id, Context))!;
            var stream = await harness.WatchEventsAsync(root.Id, Context);
            stream.Start((_, _) => Task.CompletedTask);

            var failure = new IOException("disk gone");
            storage.FailNextCommit(failure);
            // The call that hit it gets the storage error itself.
            await RejectsWith(failure, () => Note(root), "the failing call");

            await FailedWith(failure, () => task, "task wait");
            await FailedWith(failure, () => idle, "idle wait");
            Equal(WatchEnd.SessionFailed(failure), await watch.Closed, "watch");
            Equal(WatchEnd.SessionFailed(failure), await stream.Closed, "stream");
            // Every later call, and a wait that starts now.
            await FailedWith(failure, () => harness.GetTaskAsync(id, Context), "getTask");
            await FailedWith(failure, () => harness.WaitForIdleAsync(Context), "waitForIdle");
            await FailedWith(failure, () => root.SubmitAsync(SubmissionDraft.Input("x"), Context), "submit");
            Equal(SessionEnd.Failed(failure), await harness.Closed, "closed");
            Seq(["aborted"], signals.Snapshot(), "signals");
            Seq([failure], reports.Snapshot(), "reports");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "fails-it-from-a-read-off-the-session-line-as-a-context-read-makes", async () =>
        {
            var storage = new FailingCalls(new MemoryStorage());
            var (harness, root, reports, _, _) = await Start(storage);
            await Note(root);
            storage.Failing.Add("scanRange");
            await RejectsWith(storage.Error, () => root.ContextAsync(Context), "context read");
            Equal(SessionEnd.Failed(storage.Error), await harness.Closed, "closed");
            Seq([storage.Error], reports.Snapshot(), "reports");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "ends-storage-calls-underway-with-session-failed-and-closes-once-they-have-settled", async () =>
        {
            var storage = new FailingCalls(new MemoryStorage());
            var (harness, root, _, _, _) = await Start(storage);
            var release = new Deferred();
            await Note(root);
            var held = new HeldRange(new Deferred(), release.Task);
            storage.HeldRange = held;
            var reading = root.ContextAsync(Context);
            await held.Entered.Task;
            // Another call fails the Session while the context read is underway.
            storage.Failing.Add("mintId");
            await RejectsWith(storage.Error, () => Note(root), "the failing call");
            for (var i = 0; i < 5; i++) await Task.Delay(1);
            Check(!harness.Closed.IsCompleted, "not closed while a call is underway");
            // The read succeeds at the backend, yet its caller gets the failure: nothing it read is used.
            release.Resolve();
            await FailedWith(storage.Error, () => reading, "reading");
            Equal(SessionEnd.Failed(storage.Error), await harness.Closed, "closed");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "closes-the-backend-only-after-a-paged-read-underway-which-cannot-start-another-page", async () =>
        {
            var storage = new Paged(new MemoryStorage());
            var (harness, root, _, _, _) = await Start(storage);
            // More entries than one page of the context read.
            await root.CommitAsync(async tx =>
            {
                for (var n = 0; n < 300; n++) await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "note" });
            }, Context);
            var reading = root.ContextAsync(Context);
            await storage.FirstPage.Task;
            var closing = harness.CloseAsync(Context);
            storage.ReleaseFirst.Resolve();
            await RejectsAsync(() => reading, "is closed", "reading");
            await closing;
            Seq(["first", "close"], storage.Pages.Snapshot(), "pages");
            Equal(SessionEnd.Closed, await harness.Closed, "closed");
        });

        yield return Case(prefix + "fails-it-when-storage-cannot-close-an-earlier-failure-stays-the-cause", async () =>
        {
            var healthyStorage = new FailingClose(new MemoryStorage());
            var healthy = await Start(healthyStorage);
            await RejectsWith(healthyStorage.CloseError, () => healthy.Harness.CloseAsync(Context), "close");
            Equal(SessionEnd.Failed(healthyStorage.CloseError), await healthy.Harness.Closed, "healthy closed");
            Seq([healthyStorage.CloseError], healthy.Reports.Snapshot(), "healthy reports");

            var storage = new FailingClose(new MemoryStorage());
            var failed = await Start(storage);
            var failure = new IOException("disk gone");
            storage.Controlled.FailNextCommit(failure);
            await RejectsWith(failure, () => Note(failed.Root), "the failing call");
            Equal(SessionEnd.Failed(failure), await failed.Harness.Closed, "failed closed");
            Seq([failure], failed.Reports.Snapshot(), "failed reports");
        });

        yield return Case(prefix + "rejects-the-harnesss-own-calls-with-session-failed-after-a-failure", async () =>
        {
            var storage = new ControlledStorage(new MemoryStorage());
            var (harness, root, _, _, _) = await Start(storage);
            var failure = new IOException("disk gone");
            storage.FailNextCommit(failure);
            await RejectsWith(failure, () => Note(root), "the failing call");
            Same(failure, Throws<SessionFailedException>(harness.Resume, "resume").InnerException, "resume cause");
            await FailedWith(failure, () => harness.RootAsync(Context), "root");
            await FailedWith(failure, () => harness.ConversationAsync(root.Id, Context), "conversation");
            await FailedWith(failure, () => harness.CreateConversationAsync(new ConversationCreateOptions(ConversationOwnership.Ownerless), Context),
                "createConversation");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "ends-what-a-task-holds-with-session-failed-not-as-cancelled-by-its-signal", async () =>
        {
            var ends = new SyncList<object>();
            var watching = new Deferred();
            var watcher = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.watcher",
                Version = 1,
                Initial = _ => O("""{"phase":"work"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["work"] = async (_, runtime, phaseContext) =>
                    {
                        var doc = (await runtime.WatchDocAsync(Live.LiveDoc, runtime.ConversationId, phaseContext))!;
                        var handle = (await runtime.ConversationAsync(runtime.ConversationId, phaseContext))!;
                        var idle = handle.WaitForIdleAsync(phaseContext).ContinueWith(completed =>
                            completed.IsCompletedSuccessfully ? "resolved" : completed.Exception!.InnerException!.GetType().Name, TaskScheduler.Default);
                        watching.Resolve();
                        ends.Add(await doc.Closed);
                        ends.Add(await idle);
                    },
                },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            var storage = new ControlledStorage(new MemoryStorage());
            var opened = await OpenTasks(storage, [watcher]);
            var root = await opened.Harness.RootAsync(Context);
            await root.CommitAsync(tx => tx.CreateTaskAsync(watcher, JsonValue.Null, OwnConversation()), Context);
            opened.Harness.Resume();
            await watching.Task;
            var failure = new IOException("disk gone");
            storage.FailNextCommit(failure);
            await RejectsWith(failure, () => Note(root), "the failing call");
            await opened.Harness.Closed;
            var seen = ends.Snapshot();
            Equal(2, seen.Count, "ends");
            Equal(WatchEnd.SessionFailed(failure), seen[0], "watch end");
            Equal(nameof(SessionFailedException), seen[1], "idle wait");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "fails-it-from-a-failed-id-mint", async () =>
        {
            var storage = new FailingCalls(new MemoryStorage());
            var (harness, root, _, _, _) = await Start(storage);
            storage.Failing.Add("mintId");
            await RejectsWith(storage.Error, () => Note(root), "the failing call");
            Equal(SessionEnd.Failed(storage.Error), await harness.Closed, "closed");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "does-not-commit-what-a-callback-did-after-it-caught-a-failed-read", async () =>
        {
            var storage = new FailingCalls(new MemoryStorage());
            var (harness, root, _, _, _) = await Start(storage);
            var written = storage.Controlled.AdmittedCommits.Count;
            storage.Failing.Add("task");
            await FailedWith(storage.Error, () => root.CommitAsync(async tx =>
            {
                try { await tx.GetTaskAsync(new TaskId(1)); }
                catch (Exception) { }
                await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "note" });
            }, Context), "commit");
            Equal(written, storage.Controlled.AdmittedCommits.Count, "admitted commits");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "fails-it-for-a-storage-request-error-from-a-commit-whose-effect-is-unknown", async () =>
        {
            var storage = new ControlledStorage(new MemoryStorage());
            var (harness, root, _, _, _) = await Start(storage);
            var rejection = new StorageRequestException("bad batch");
            storage.FailNextCommit(rejection);
            await RejectsWith(rejection, () => Note(root), "the failing call");
            Equal(SessionEnd.Failed(rejection), await harness.Closed, "closed");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "fails-only-the-call-for-an-invalid-request", async () =>
        {
            var (harness, root, reports, _, _) = await Start(new MemoryStorage());
            var entry = await Note(root);
            // pi.live keeps no history; a rewindable token of its kind asks for it anyway (upstream `LiveDoc as never`).
            var liveAsRewindable = Documents.DefineRewindableConversationDoc(Live.LiveDoc.Definition.Kind, 1, DocumentFork.AsOf, () => JsonObjectValue.Empty);
            var requests = new List<Func<Task>>
            {
                () => root.EntriesAsync(new EntryQueryBounds(), 10, O("""{"after":"x"}"""), Context),
                async () =>
                {
                    var page = await root.EntriesAsync(new EntryQueryBounds { Order = ScanOrder.Ascending }, 1, null, Context);
                    await root.EntriesAsync(new EntryQueryBounds { Order = ScanOrder.Descending }, 1, page.Next ?? O("""{"after":1,"order":"ascending"}"""), Context);
                },
                () => root.CommitAsync(tx => tx.ScanEntriesAsync(new EntryQuery(new ConversationId(999_999)), 10), Context),
                () => harness.SnapshotAsOfAsync(liveAsRewindable, root.Id, entry.Id, Context),
            };
            var index = 0;
            foreach (var request in requests) await ThrowsAsync<StorageRequestException>(request, $"request {index++}");
            await Note(root);
            await Task.Delay(1);
            Check(!harness.Closed.IsCompleted, "not ended");
            Seq([], reports.Snapshot(), "reports");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "fails-it-for-a-throw-in-its-own-commit-listeners-which-would-leave-memory-behind-storage", async () =>
        {
            var storage = new ControlledStorage(new MemoryStorage());
            var (harness, root, reports, _, _) = await Start(storage);
            var bug = new InvalidOperationException("listener bug");
            ((Session)harness).ObserveCommits((_, _) => throw bug);
            // The commit itself is durable and resolves; the Session fails behind it.
            await Note(root);
            Equal(SessionEnd.Failed(bug), await harness.Closed, "closed");
            Seq([bug], reports.Snapshot(), "reports");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "fails-it-for-a-storage-error-during-a-host-close-before-the-backend-closes", async () =>
        {
            var storage = new ControlledStorage(new MemoryStorage());
            var (harness, root, reports, _, _) = await Start(storage);
            var held = storage.HoldCommits();
            var failure = new IOException("disk gone");
            storage.FailNextCommit(failure);
            var committing = Note(root);
            await held.Entered;
            var closing = harness.CloseAsync(Context);
            held.Release();
            await RejectsWith(failure, () => committing, "committing");
            await closing;
            Equal(SessionEnd.Failed(failure), await harness.Closed, "closed");
            Seq([failure], reports.Snapshot(), "reports");
        });

        yield return Case(prefix + "ends-an-abort-that-joins-a-run-ignoring-its-signal-and-a-reentrant-report-finds-the-session-failed", async () =>
        {
            var release = new Deferred();
            var entered = new Deferred();
            var stubborn = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.stubborn",
                Version = 1,
                Initial = _ => O("""{"phase":"work"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["work"] = async (_, _, _) =>
                    {
                        entered.Resolve();
                        await release.Task;
                    },
                },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            var storage = new ControlledStorage(new MemoryStorage());
            var registry = Registry.Create();
            registry.Install(Define.Extension(new Extension { Name = "tasks", Tasks = [stubborn] }));
            IHarness? harness = null;
            Task? reentered = null;
            harness = await Harness.OpenAsync(storage, new HarnessOptions
            {
                Models = EmptyDurableModels.Instance,
                Registry = registry,
                OnReport = _ => reentered ??= harness!.WaitForIdleAsync(Context),
            }, Context);
            var root = await harness.RootAsync(Context);
            var id = await root.CommitAsync(tx => tx.CreateTaskAsync(stubborn, JsonValue.Null, OwnConversation()), Context);
            harness.Resume();
            await entered.Task;
            var held = storage.HoldCommits();
            var aborting = harness.AbortTaskAsync(id, Context);
            await held.Entered;
            held.Release();
            // The abort mark commits and the abort joins the run, which ignores its signal; then the Session fails.
            var failure = new IOException("disk gone");
            await Task.Delay(5);
            storage.FailNextCommit(failure);
            await RejectsWith(failure, () => Note(root), "the failing call");
            await FailedWith(failure, () => aborting, "aborting");
            Check(reentered is not null, "onReport ran");
            await FailedWith(failure, () => reentered!, "reentered");
            release.Resolve();
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "does-not-fail-it-for-a-read-its-caller-cancelled", async () =>
        {
            var controller = Context.WithCancel();
            var cancelled = new InvalidOperationException("caller gave up");
            var storage = new CancelledRead(new MemoryStorage(), controller, cancelled);
            var (harness, root, _, _, _) = await Start(storage);
            await RejectsWith(cancelled, () => root.ContextAsync(controller.Context), "context read");
            await Note(root);
            await harness.CloseAsync(Context);
        });

        // ─── listener and report errors ───

        yield return Case(prefix + "reports-a-listener-or-on-report-whose-promise-rejects-without-an-unhandled-rejection", async () =>
        {
            var unhandled = new SyncList<Exception>();
            EventHandler<UnobservedTaskExceptionEventArgs> onUnobserved = (_, args) => unhandled.Add(args.Exception);
            Action<Exception> onUnhandled = unhandled.Add;
            TaskScheduler.UnobservedTaskException += onUnobserved;
            ReplicatedState.UnhandledError += onUnhandled;
            try
            {
                var (harness, root, reports, _, _) = await Start(new MemoryStorage());
                static async Task AsyncThrow()
                {
                    await Task.Yield();
                    throw new InvalidOperationException("async listener");
                }
                harness.SubscribeCommits((_, _) => AsyncThrow());
                harness.SubscribeClose(AsyncThrow);
                await Note(root);
                await harness.CloseAsync(Context);
                await Eventually(() => reports.Count == 2);
                Seq(["async listener", "async listener"], reports.Snapshot().Select(error => error.Message), "reports");

                var rejecting = await Harness.OpenAsync(new MemoryStorage(), new HarnessOptions
                {
                    Models = EmptyDurableModels.Instance,
                    Registry = Registry.Create(),
                    OnReport = _ => throw new InvalidOperationException("report broke"),
                }, Context);
                rejecting.SubscribeCommits((_, _) => throw new InvalidOperationException("sync listener"));
                var again = await rejecting.RootAsync(Context);
                await Note(again);
                await rejecting.CloseAsync(Context);
                await Task.Delay(10);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Seq([], unhandled.Snapshot().Select(error => error.Message), "unhandled");
            }
            finally
            {
                TaskScheduler.UnobservedTaskException -= onUnobserved;
                ReplicatedState.UnhandledError -= onUnhandled;
            }
        });

        yield return Case(prefix + "reports-a-throwing-commit-or-close-listener-and-runs-the-others-the-commit-stands", async () =>
        {
            var (harness, root, reports, _, _) = await Start(new MemoryStorage());
            var seen = new SyncList<string>();
            harness.SubscribeCommits((_, _) => throw new InvalidOperationException("commit listener"));
            harness.SubscribeCommits((_, _) => seen.Add("commit"));
            harness.SubscribeClose(() => throw new InvalidOperationException("close listener"));
            harness.SubscribeClose(() => seen.Add("close"));
            await Note(root);
            await harness.CloseAsync(Context);
            Seq(["commit", "close"], seen.Snapshot(), "seen");
            Seq(["commit listener", "close listener"], reports.Snapshot().Select(error => error.Message), "reports");
        });

        yield return Case(prefix + "keeps-feeding-the-other-observers-of-a-view-when-one-throws", async () =>
        {
            var (harness, root, reports, _, _) = await Start(new MemoryStorage());
            var views = ConversationViews.Of(harness);
            var failure = new InvalidOperationException("observer broke");
            await views.AttachAsync(root.Id, (_, _, _) => Task.FromResult((true, (ViewObserver)new ThrowingObserver(failure))), Context);
            var watch = await root.WatchAsync(Context);
            var seen = new SyncList<ConversationView>();
            watch.Start((value, _, _) =>
            {
                seen.Add(value);
                return Task.CompletedTask;
            });
            await Note(root);
            await Eventually(() => seen.Count > 0);
            Seq([failure], reports.Snapshot(), "reports");
            await harness.CloseAsync(Context);
            Equal(WatchEnd.SessionClosed, await watch.Closed, "watch end");
        });

        yield return Case(prefix + "contains-a-fulfilled-thenable-that-calls-only-its-fulfillment-handler", async () =>
        {
            var unhandled = new SyncList<Exception>();
            EventHandler<UnobservedTaskExceptionEventArgs> onUnobserved = (_, args) => unhandled.Add(args.Exception);
            TaskScheduler.UnobservedTaskException += onUnobserved;
            try
            {
                var errors = new SyncList<Exception>();
                // A task that completes later, as the thenable settles in a later microtask.
                PiSharp.Durable.Session.Observe(Task.Delay(1), errors.Add);
                await Task.Delay(10);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Seq([], errors.Snapshot(), "errors");
                Seq([], unhandled.Snapshot(), "unhandled");
            }
            finally
            {
                TaskScheduler.UnobservedTaskException -= onUnobserved;
            }
        });

        yield return Case(prefix + "contains-a-rejected-promise-from-another-realm", async () =>
        {
            var unhandled = new SyncList<Exception>();
            EventHandler<UnobservedTaskExceptionEventArgs> onUnobserved = (_, args) => unhandled.Add(args.Exception);
            TaskScheduler.UnobservedTaskException += onUnobserved;
            try
            {
                // A task faulted on another thread, as a promise from another realm.
                var foreign = Task.Run(() => throw new InvalidOperationException("foreign"));
                var errors = new SyncList<Exception>();
                PiSharp.Durable.Session.Observe(foreign, errors.Add);
                await Eventually(() => errors.Count > 0);
                await Task.Delay(10);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Seq(["foreign"], errors.Snapshot().Select(error => error.Message), "errors");
                Seq([], unhandled.Snapshot(), "unhandled");
            }
            finally
            {
                TaskScheduler.UnobservedTaskException -= onUnobserved;
            }
        });

        yield return Case(prefix + "reports-a-throwing-watch-listener-which-ends-that-watch", async () =>
        {
            var (harness, root, reports, _, _) = await Start(new MemoryStorage());
            var watch = (await harness.WatchDocAsync(Live.LiveDoc, root.Id, Context))!;
            var failure = new InvalidOperationException("listener broke");
            watch.Start((_, _, _) => throw failure);
            await root.CommitAsync(async tx => (await tx.DocAsync(Live.LiveDoc, root.Id))["compactions"] = JsonArrayValue.Empty, Context);
            Equal(new WatchEnd(WatchEndReason.ListenerError, failure), await watch.Closed, "watch end");
            Seq([failure], reports.Snapshot(), "reports");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "faults-only-the-task-and-ends-only-the-watch-for-a-thrown-value-that-cannot-be-formatted", async () =>
        {
            var unformattable = new UnformattableException();
            var throws = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.throws-unformattable",
                Version = 1,
                Initial = _ => O("""{"phase":"work"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler> { ["work"] = (_, _, _) => throw unformattable },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            var opened = await OpenTasks(new MemoryStorage(), [throws]);
            var root = await opened.Harness.RootAsync(Context);
            var watch = (await opened.Harness.WatchDocAsync(Live.LiveDoc, root.Id, Context))!;
            watch.Start((_, _, _) => throw unformattable);
            var id = await root.CommitAsync(tx => tx.CreateTaskAsync(throws, JsonValue.Null, OwnConversation()), Context);
            opened.Harness.Resume();
            var outcome = ((SettlingTaskState)(await opened.Harness.WaitForTaskAsync(id, Context)).State).Outcome;
            SameJson("""{"status":"faulted","error":{"message":"[object UnformattableException]"}}""", outcome.ToJson(), "outcome");
            await root.CommitAsync(async tx => (await tx.DocAsync(Live.LiveDoc, root.Id))["compactions"] = JsonArrayValue.Empty, Context);
            Equal(WatchEndReason.ListenerError, (await watch.Closed).Reason, "watch end");
            await Note(root);
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "reports-a-throwing-clock-once-also-when-the-report-handler-reads-the-clock-again", () =>
        {
            var reports = new List<Exception>();
            Func<double>? clock = null;
            clock = DurableHarness.SafeNow(() => throw new InvalidOperationException("clock broke"), true, error =>
            {
                reports.Add(error);
                // Reentry, as a handler that commits does: the clock is read again before this report returns.
                if (reports.Count < 5) clock!();
            });
            Check(double.IsFinite(clock()), "a number");
            clock();
            Seq(["clock broke"], reports.Select(error => error.Message), "reports");
        });

        yield return Case(prefix + "keeps-working-when-on-report-throws-and-falls-back-to-the-system-clock-when-the-clock-throws", async () =>
        {
            var signals = new SyncList<string>();
            var phaseStarted = new Deferred();
            var waiting = Waiting(signals, phaseStarted);
            var registry = Registry.Create();
            registry.Install(Define.Extension(new Extension { Name = "tasks", Tasks = [waiting] }));
            var harness = await Harness.OpenAsync(new MemoryStorage(), new HarnessOptions
            {
                Models = EmptyDurableModels.Instance,
                Registry = registry,
                Now = () => throw new InvalidOperationException("clock broke"),
                OnReport = _ => throw new InvalidOperationException("report broke"),
            }, Context);
            var root = await harness.RootAsync(Context);
            var id = await root.CommitAsync(tx => tx.CreateTaskAsync(waiting, JsonValue.Null, OwnConversation()), Context);
            harness.Resume();
            await Running(harness, id, phaseStarted);
            Check((await harness.GetTaskAsync(id, Context))?.StartedAt is double, "startedAt");
            await harness.AbortTaskAsync(id, Context);
            Equal(TaskOutcomeStatus.Aborted, ((SettlingTaskState)(await harness.WaitForTaskAsync(id, Context)).State).Outcome.Status, "outcome");
            await harness.CloseAsync(Context);
        });
    }

    /// <summary>An exception whose message cannot be read (upstream's <c>Object.create(null)</c>).</summary>
    private sealed class UnformattableException : Exception
    {
        public override string Message => throw new InvalidOperationException("no message");
    }

    private sealed class ThrowingObserver(Exception failure) : ViewObserver
    {
        public override void Publication(ConversationView before, ConversationView after, System.Collections.Immutable.ImmutableArray<PiSharp.Durable.Chord.Delta.Op> ops,
            CommitPublication publication, DurableContext context) => throw failure;

        public override void CloseSession(Exception? failure) { }
    }

    private sealed record HeldRange(Deferred Entered, Task Release);

    /// <summary>
    /// Fails each Storage method named in <see cref="Failing"/> once, with <see cref="Error"/> (upstream <c>FailingCalls</c>). A scan of entries
    /// with a limit above 1 is the context read's scan of its range, off the Session line; <see cref="HeldRange"/> holds it until released.
    /// </summary>
    private sealed class FailingCalls(IStorage inner) : DelegatingStorage(new ControlledStorage(inner))
    {
        public Exception Error { get; } = new IOException("disk gone");
        public HashSet<string> Failing { get; } = [];
        public HeldRange? HeldRange { get; set; }
        public ControlledStorage Controlled => (ControlledStorage)Inner;

        private void Fail(string method)
        {
            lock (Failing) if (Failing.Remove(method)) throw Error;
        }

        public override async Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context)
        {
            Fail("task");
            return await base.GetTaskAsync(id, context);
        }

        public override async Task<Page<EntryRecord>> ScanEntriesAsync(EntryQuery query, int limit, JsonObjectValue? cursor, DurableContext context)
        {
            if (limit > 1)
            {
                if (HeldRange is { } held)
                {
                    held.Entered.Resolve();
                    await held.Release;
                }
                Fail("scanRange");
            }
            return await base.ScanEntriesAsync(query, limit, cursor, context);
        }

        public override async Task<TId> MintIdAsync<TId>()
        {
            Fail("mintId");
            return await base.MintIdAsync<TId>();
        }
    }

    /// <summary>Records the context read's pages and holds the first (upstream's <c>class Paged</c>).</summary>
    private sealed class Paged(IStorage inner) : DelegatingStorage(new ControlledStorage(inner))
    {
        public SyncList<string> Pages { get; } = new();
        public Deferred FirstPage { get; } = new();
        public Deferred ReleaseFirst { get; } = new();

        public override async Task<Page<EntryRecord>> ScanEntriesAsync(EntryQuery query, int limit, JsonObjectValue? cursor, DurableContext context)
        {
            if (limit > 1)
            {
                Pages.Add(cursor is null ? "first" : "next");
                if (cursor is null)
                {
                    FirstPage.Resolve();
                    await ReleaseFirst.Task;
                }
            }
            return await base.ScanEntriesAsync(query, limit, cursor, context);
        }

        public override Task CloseAsync(DurableContext context)
        {
            Pages.Add("close");
            return base.CloseAsync(context);
        }
    }

    /// <summary>Closes, then throws (upstream's <c>class FailingClose</c>).</summary>
    private sealed class FailingClose(IStorage inner) : DelegatingStorage(new ControlledStorage(inner))
    {
        public Exception CloseError { get; } = new IOException("close failed");
        public ControlledStorage Controlled => (ControlledStorage)Inner;

        public override async Task CloseAsync(DurableContext context)
        {
            await base.CloseAsync(context);
            throw CloseError;
        }
    }

    /// <summary>Cancels the caller's context and throws for its entry scan (upstream's <c>class CancelledRead</c>).</summary>
    private sealed class CancelledRead(IStorage inner, DurableCancellation controller, Exception cancelled) : DelegatingStorage(inner)
    {
        public override Task<Page<EntryRecord>> ScanEntriesAsync(EntryQuery query, int limit, JsonObjectValue? cursor, DurableContext context)
        {
            if (context.CancellationToken == controller.Context.CancellationToken)
            {
                controller.Cancel(cancelled);
                throw cancelled;
            }
            return base.ScanEntriesAsync(query, limit, cursor, context);
        }
    }
}

/// <summary>An IStorage decorator whose members a test storage overrides (upstream's storages extend MemoryStorage).</summary>
internal class DelegatingStorage(IStorage inner) : IStorage
{
    public IStorage Inner { get; } = inner;

    public virtual Task<Seq> CommitAsync(IReadOnlyList<StorageWrite> writes, DurableContext context) => Inner.CommitAsync(writes, context);
    public virtual Task<TId> MintIdAsync<TId>() where TId : struct, IDurableId<TId> => Inner.MintIdAsync<TId>();
    public virtual Task<ConversationRecord?> GetConversationAsync(ConversationId id, DurableContext context) => Inner.GetConversationAsync(id, context);
    public virtual Task<Page<ConversationRecord>> ScanConversationsAsync(ConversationQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        Inner.ScanConversationsAsync(query, limit, cursor, context);
    public virtual Task<CommittedEntry?> GetEntryAsync(EntryId id, DurableContext context) => Inner.GetEntryAsync(id, context);
    public virtual Task<CommittedEntry?> GetEntryAsync(ConversationId conversationId, EntryId id, DurableContext context) =>
        Inner.GetEntryAsync(conversationId, id, context);
    public virtual Task<EntryRecord?> FindLatestHeadMarkerAsync(ConversationId conversationId, EntryId? atOrBeforeEntryId, DurableContext context) =>
        Inner.FindLatestHeadMarkerAsync(conversationId, atOrBeforeEntryId, context);
    public virtual Task<Page<EntryRecord>> ScanEntriesAsync(EntryQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        Inner.ScanEntriesAsync(query, limit, cursor, context);
    public virtual Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context) => Inner.GetTaskAsync(id, context);
    public virtual Task<Page<TaskRecord>> ScanTasksAsync(TaskQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        Inner.ScanTasksAsync(query, limit, cursor, context);
    public virtual Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id, DurableContext context) => Inner.GetSubmissionAsync(id, context);
    public virtual Task<Page<SubmissionRecord>> ScanSubmissionsAsync(SubmissionQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        Inner.ScanSubmissionsAsync(query, limit, cursor, context);
    public virtual Task<SubmissionRecord?> GetSubmissionByRequestAsync(ConversationId conversationId, string requestId, DurableContext context) =>
        Inner.GetSubmissionByRequestAsync(conversationId, requestId, context);
    public virtual Task<DocumentRecord?> FindDocumentAsync(DocumentAddress address, DocumentPoint at, DurableContext context) =>
        Inner.FindDocumentAsync(address, at, context);
    public virtual Task<StoredDocument?> GetDocumentAsync(DocumentId id, DocumentPoint at, DurableContext context) => Inner.GetDocumentAsync(id, at, context);
    public virtual Task<Page<DocumentRecord>> ScanDocumentsAsync(DocumentQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
        Inner.ScanDocumentsAsync(query, limit, cursor, context);
    public virtual Task CloseAsync(DurableContext context) => Inner.CloseAsync(context);
}
