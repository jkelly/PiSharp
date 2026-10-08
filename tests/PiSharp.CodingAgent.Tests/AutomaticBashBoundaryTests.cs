using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.CodingAgent.Execution;
using PiSharp.Contracts;
using PiSharp.Sessions.Compaction;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class AutomaticBashBoundaryTests
{
    internal const string Prefix = "automatic Bash boundary ";
    internal static (string Name, Func<Task> Run)[] Cases() =>
    [
        (Prefix + "threshold includes deferred output in its decision and acknowledged plan", Threshold),
        (Prefix + "overflow joins executor cleanup before omission compaction and one retry", Overflow),
        (Prefix + "cancelled provider owner still joins original progress before flushing", Cancellation),
        (Prefix + "first failed Bash join retains body progress cleanup and joins second original", FailedOriginals),
        (Prefix + "held flush checkpoint precedes summary and refuses callback self waits", Checkpoint),
        (Prefix + "unacknowledged Bash flush retains pending messages and starts no summary", FailedFlush),
        (Prefix + "manual and no Bash threshold behavior retain their existing controls", NoBash)
    ];
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static readonly FieldInfo Fence = typeof(PersistentAgentSession).GetField("_automaticBashBoundaryOwner", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static async Task AtFence(Fixture fixture, Task original)
    {
        while (Fence.GetValue(fixture.Session) is null)
        {
            if (original.IsCompleted) { await original; throw new InvalidOperationException("Automatic owner settled before its held boundary."); }
            await Task.Yield();
        }
    }
    private static void Reject(Func<Task> action)
    {
        try { _ = action(); } catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Callback or automatic boundary admitted a forbidden self wait/mutation.");
    }
    private static async Task Threshold()
    {
        var fixture = await Fixture.Create(false);
        fixture.Summary.Validate = () => Require(fixture.Session.Snapshot.Log.Entries.Count(BashEntry) == 1 &&
            !fixture.Session.HasPendingUserBashMessages && !fixture.Session.IsUserBashRunning, "Summary sampled Bash before its acknowledged checkpoint.");
        var run = fixture.Start(); Task<UserBashResult>? command = null; Exception? primary = null;
        try
        {
            await Entered(fixture.Provider.Streaming.Task, run);
            command = fixture.Session.ExecuteUserBashAsync("large result");
            fixture.First.Result.TrySetResult(new(new string('b', 14000), 0, false, false)); await command;
            Require(fixture.Session.HasPendingUserBashMessages && !fixture.Session.Snapshot.Log.Entries.Any(BashEntry), "Streaming result lost deferred ownership.");
            fixture.Provider.Finish.TrySetResult(); var result = await run;
            Require(result.Reason == AgentLoopStopReason.Completed && fixture.Summary.Calls > 0 &&
                fixture.Session.Snapshot.Log.Entries.Count(e => e.Kind == SessionEntryKind.Compaction) == 1 && fixture.Session.Snapshot.Fault is null,
                "Bash output was not included in the required threshold decision.");
            var entries = fixture.Session.Snapshot.Log.Entries.ToArray();
            Require(Array.FindIndex(entries, BashEntry) < Array.FindIndex(entries, e => e.Kind == SessionEntryKind.Compaction), "Compaction checkpoint overtook Bash acknowledgement.");
        }
        catch (Exception error) { primary = error; }
        finally { fixture.Release(); await Join(primary, () => (Task?)command ?? Task.CompletedTask, () => run, () => fixture.DisposeAsync().AsTask()); }
    }
    private static async Task Overflow()
    {
        var fixture = await Fixture.Create(true); fixture.First.HoldCleanup = true;
        var run = fixture.Start(); Task<UserBashResult>? command = null; Exception? primary = null;
        try
        {
            await Entered(fixture.Provider.Streaming.Task, run); command = fixture.Session.ExecuteUserBashAsync("held cleanup");
            fixture.First.Result.TrySetResult(new("original output", 0, false, false));
            await Entered(fixture.First.CleanupEntered.Task, command);
            fixture.Provider.Finish.TrySetResult(); await AtFence(fixture, run);
            Require(!run.IsCompleted && !command.IsCompleted && fixture.Summary.Calls == 0 && fixture.Provider.Calls == 3,
                "Overflow planning escaped the executor cleanup original.");
            Reject(() => fixture.Session.ExecuteUserBashAsync("late command"));
            fixture.First.CleanupFinish.TrySetResult(); await command; var result = await run;
            Require(result.Reason == AgentLoopStopReason.Completed && fixture.Provider.Calls == 4 &&
                fixture.Session.Snapshot.Log.Entries.Count(e => e.Kind == SessionEntryKind.Compaction) == 1 && fixture.Session.Snapshot.Fault is null,
                "Joined Bash changed the one compact-and-retry overflow contract.");
            var entries = fixture.Session.Snapshot.Log.Entries.ToArray();
            Require(Array.FindIndex(entries, BashEntry) < Array.FindIndex(entries, e => e.Kind == SessionEntryKind.ContextEdit) &&
                fixture.Observations.Single().Reason == SessionCompactionReason.Overflow && fixture.Observations.Single().WillRetry,
                "Overflow omission/planning preceded Bash or lost observation metadata.");
        }
        catch (Exception error) { primary = error; }
        finally { fixture.Release(); await Join(primary, () => (Task?)command ?? Task.CompletedTask, () => run, () => fixture.DisposeAsync().AsTask()); }
    }
    private static async Task Cancellation()
    {
        var fixture = await Fixture.Create(false); fixture.First.PublishProgress = true;
        var progressEntered = Gate(); var progressFinish = Gate();
        fixture.Session.ConfigureUserBashExecution(fixture.Executor, async _ => { Reject(() => fixture.Session.WaitForIdleAsync()); Reject(() => fixture.Session.AbortUserBashAsync()); progressEntered.TrySetResult(); await progressFinish.Task; });
        using var stop = new CancellationTokenSource(); var run = fixture.Start(stop.Token); Task<UserBashResult>? command = null; Exception? primary = null;
        try
        {
            await Entered(fixture.Provider.Streaming.Task, run); command = fixture.Session.ExecuteUserBashAsync("observer original");
            await Entered(progressEntered.Task, command); fixture.First.Result.TrySetResult(new("output", 0, false, false));
            await fixture.First.Original!; fixture.Provider.Finish.TrySetResult(); await AtFence(fixture, run); stop.Cancel();
            Require(!run.IsCompleted && !command.IsCompleted && fixture.Summary.Calls == 0,
                "Cancellation replaced the held progress original with a completed wrapper.");
            progressFinish.TrySetResult(); await command; await run;
            Require(!fixture.Session.HasPendingUserBashMessages && fixture.Session.Snapshot.Log.Entries.Count(BashEntry) == 1 &&
                fixture.Summary.Calls == 0, "Cancelled boundary dropped pending messages or initiated optional compaction.");
        }
        catch (Exception error) { primary = error; }
        finally { progressFinish.TrySetResult(); fixture.Release(); await Join(primary, () => (Task?)command ?? Task.CompletedTask, () => run, () => fixture.DisposeAsync().AsTask()); }
    }
    private static async Task FailedOriginals()
    {
        var fixture = await Fixture.Create(false); fixture.First.PublishProgress = true; fixture.First.HoldCleanup = true;
        var body = new IOException("executor body original"); var cleanup = new IOException("executor cleanup original"); var progress = new IOException("progress original");
        var progressEntered = Gate(); var progressFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.First.CleanupFailure = cleanup; fixture.ExpectedErrors.AddRange([body, cleanup, progress]);
        fixture.Session.ConfigureUserBashExecution(fixture.Executor, _ => { progressEntered.TrySetResult(); return progressFinish.Task; });
        var run = fixture.Start(); Task<UserBashResult>? first = null, second = null; Exception? primary = null, terminalRefusal = null;
        try
        {
            await Entered(fixture.Provider.Streaming.Task, run);
            first = fixture.Session.ExecuteUserBashAsync("fails"); second = fixture.Session.ExecuteUserBashAsync("still owned");
            await Entered(progressEntered.Task, first); fixture.Provider.Finish.TrySetResult(); await AtFence(fixture, run);
            fixture.First.Result.TrySetException(body); await fixture.First.CleanupEntered.Task;
            progressFinish.TrySetException(progress); fixture.First.CleanupFinish.TrySetResult(); var firstError = await Failure(first);
            Require(Contains(firstError, body) && Contains(firstError, cleanup) && Contains(firstError, progress), "Composite Bash replaced original failure references.");
            Require(!run.IsCompleted && !second.IsCompleted && fixture.Summary.Calls == 0, "Failed first join abandoned the second original.");
            fixture.Second.Result.TrySetResult(new("retained second output", 0, false, false)); await second;
            var failedRun = await Failure(run);
            Require(Contains(failedRun, body) && Contains(failedRun, cleanup) && Contains(failedRun, progress) && fixture.Summary.Calls == 0,
                "Preplan failure disappeared or a summary consumed incomplete state.");
            var runLeaves = CloseLeaves(failedRun).ToArray();
            terminalRefusal = runLeaves.Single(value => value is PersistentAgentSessionException { Fault.Failure: PersistentAgentSessionFailure.Faulted, InnerException: null });
            Require(runLeaves.Length == 4 && new Exception[] { body, cleanup, progress }.All(marker =>
                runLeaves.Count(value => ReferenceEquals(value, marker)) == 1), "Run settlement changed the original inventory or terminal poison refusal.");
            Require(fixture.Session.HasPendingUserBashMessages || fixture.Session.Snapshot.Log.Entries.Any(BashEntry), "Successful queued result was consumed without acknowledgement.");
            fixture.ExpectedPoison = fixture.Session.Snapshot.Fault;
            Require(fixture.ExpectedPoison is { Failure: PersistentAgentSessionFailure.RunFailed }, "Failed originals did not retain the deliberate RunFailed poison.");
        }
        catch (Exception error) { primary = error; }
        finally
        {
            progressFinish.TrySetException(progress); fixture.Release();
            await Join(primary, () => ObserveExpected(first, body, cleanup, progress), () => (Task?)second ?? Task.CompletedTask,
                () => ObserveExpected(run, body, cleanup, progress, terminalRefusal!), () => fixture.DisposeAsync().AsTask());
        }
    }
    private static async Task Checkpoint()
    {
        var fixture = await Fixture.Create(false);
        var pendingCheckpoint = Gate(); var checkpointFinish = Gate(); var callbackRefusals = 0;
        fixture.Storage.BeforeBashCheckpoint = async () =>
        {
            Reject(() => fixture.Session.WaitForIdleAsync()); callbackRefusals++;
            Reject(() => fixture.Session.StopAdmissionAndJoinAsync()); callbackRefusals++;
            pendingCheckpoint.TrySetResult(); await checkpointFinish.Task;
        };
        var run = fixture.Start(); Task<UserBashResult>? command = null; Exception? primary = null;
        try
        {
            await Entered(fixture.Provider.Streaming.Task, run); command = fixture.Session.ExecuteUserBashAsync("checkpoint held");
            fixture.First.Result.TrySetResult(new(new string('b', 14000), 0, false, false)); await command;
            fixture.Provider.Finish.TrySetResult(); await Entered(pendingCheckpoint.Task, run);
            Require(!run.IsCompleted && fixture.Session.HasPendingUserBashMessages && fixture.Summary.Calls == 0 &&
                !fixture.Session.Snapshot.Log.Entries.Any(BashEntry), "Planning or dequeue preceded original flush acknowledgement.");
            Reject(() => fixture.Session.ExecuteUserBashAsync("late during flush"));
            checkpointFinish.TrySetResult(); await run;
            Require(callbackRefusals == 2 && fixture.Summary.Calls > 0 && !fixture.Session.HasPendingUserBashMessages,
                "Flush callback self-wait guard or post-ack planning failed.");
        }
        catch (Exception error) { primary = error; }
        finally { checkpointFinish.TrySetResult(); fixture.Release(); await Join(primary, () => (Task?)command ?? Task.CompletedTask, () => run, () => fixture.DisposeAsync().AsTask()); }
    }
    private static async Task FailedFlush()
    {
        var fixture = await Fixture.Create(false);
        fixture.Storage.BeforeBashCheckpoint = () => throw new IOException("unacknowledged checkpoint");
        var run = fixture.Start(); Task<UserBashResult>? command = null; Exception? primary = null;
        try
        {
            await Entered(fixture.Provider.Streaming.Task, run); command = fixture.Session.ExecuteUserBashAsync("no checkpoint");
            fixture.First.Result.TrySetResult(new(new string('b', 14000), 0, false, false)); await command;
            fixture.Provider.Finish.TrySetResult(); var runError = await Failure(run);
            var runLeaves = CloseLeaves(runError).ToArray();
            var checkpoint = runLeaves.OfType<SessionLogStoreException>().Single();
            Require(checkpoint is { Failure: SessionLogStoreFailure.CheckpointFailed, MayHaveWritten: true, DurableFlushCompleted: true },
                "Failed flush lost its exact admitted durable-but-unacknowledged checkpoint diagnostic.");
            Require(run.IsFaulted && runLeaves.Length == 2 && runLeaves.Single(value => !ReferenceEquals(value, checkpoint)) is
                PersistentAgentSessionException { Fault.Failure: PersistentAgentSessionFailure.Faulted, InnerException: null },
                "Failed flush settlement lost its terminal poison refusal or added another fault.");
            Require(ReferenceEquals(await Failure(run), runError), "Repeated failed run replaced its original exception.");
            fixture.ExpectedErrors.AddRange(runLeaves);
            fixture.ExpectedPoison = fixture.Session.Snapshot.Fault;
            Require(fixture.ExpectedPoison is { Failure: PersistentAgentSessionFailure.AppendFailed, StorageFailure: SessionLogStoreFailure.CheckpointFailed,
                MayHaveWritten: true, DurableFlushCompleted: true }, "Failed checkpoint poison no longer matches the admitted append.");
            Require(fixture.Session.HasPendingUserBashMessages && fixture.Summary.Calls == 0 && !fixture.Session.Snapshot.Log.Entries.Any(BashEntry),
                "Unacknowledged flush consumed pending Bash or started a summary.");
        }
        catch (Exception error) { primary = error; }
        finally { fixture.Release(); await Join(primary, () => (Task?)command ?? Task.CompletedTask,
            () => ObserveExpected(run, fixture.ExpectedErrors.ToArray()), () => fixture.DisposeAsync().AsTask()); }
    }
    private static async Task NoBash()
    {
        var fixture = await Fixture.Create(false); var run = fixture.Start(); Exception? primary = null;
        try
        {
            await Entered(fixture.Provider.Streaming.Task, run); fixture.Provider.Finish.TrySetResult(); await run;
            Require(fixture.Summary.Calls == 0 && fixture.Session.Snapshot.Fault is null, "Unchanged non-Bash context crossed threshold.");
            var receipt = await fixture.Session.CompactAsync("clean-boundary", new(Automatic: false, Settings: new(true, 128, 128)), fixture.Summary);
            Require(receipt is not null && fixture.Summary.Calls > 0 && Fence.GetValue(fixture.Session) is null,
                "Automatic lease leaked into manual compaction admission.");
        }
        catch (Exception error) { primary = error; }
        finally { fixture.Release(); await Join(primary, () => run, () => fixture.DisposeAsync().AsTask()); }
    }
    private static bool BashEntry(SessionEntry entry) => entry.Type == "message" && entry.WireBody.Value.GetProperty("message").GetProperty("role").GetString() == "bashExecution";
    private static async Task Entered(Task gate, Task original)
    {
        if (await Task.WhenAny(gate, original) == original && !gate.IsCompleted) { await original; throw new InvalidOperationException("Original operation settled before held control entry."); }
        await gate;
    }
    private static async Task<Exception> Failure(Task operation)
    { try { await operation; } catch (Exception error) { return error; } throw new InvalidOperationException("Original operation unexpectedly succeeded."); }
    private static bool Contains(Exception error, Exception expected) => ReferenceEquals(error, expected) ||
        error is AggregateException aggregate && aggregate.InnerExceptions.Any(inner => Contains(inner, expected)) ||
        error.InnerException is { } wrapped && Contains(wrapped, expected);
    private static IEnumerable<Exception> CloseLeaves(Exception error) => error switch
    {
        AggregateException { InnerExceptions.Count: > 0 } aggregate => aggregate.InnerExceptions.SelectMany(CloseLeaves),
        PersistentAgentSessionException { Fault.Failure: PersistentAgentSessionFailure.CleanupFailed,
            InnerException: AggregateException { InnerExceptions.Count: > 0 } cleanup } => cleanup.InnerExceptions.SelectMany(CloseLeaves),
        _ => [error]
    };
    private static async Task ObserveExpected(Task? original, params Exception[] expected)
    {
        if (original is null) return;
        try { await original; }
        catch (Exception error) when (CloseLeaves(error).Count() == expected.Length &&
            expected.All(marker => CloseLeaves(error).Count(value => ReferenceEquals(value, marker)) == 1)) { }
    }
    private static async Task Join(Exception? primary, params Func<Task>[] operations)
    {
        var failures = new List<Exception>(); if (primary is not null) failures.Add(primary);
        foreach (var operation in operations)
            try { await operation(); } catch (Exception error) { if (!failures.Any(value => ReferenceEquals(value, error))) failures.Add(error); }
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException(failures);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly List<SessionCompactionObservation> Observations = [];
        internal readonly List<Exception> ExpectedErrors = [];
        internal PersistentAgentSessionFault? ExpectedPoison;
        internal readonly Slot First = new(), Second = new();
        internal readonly StorageFactory Storage = new();
        internal readonly Summary Summary = new();
        internal readonly ProviderTransport Provider;
        internal readonly Executor Executor;
        internal PersistentAgentSession Session = null!;
        private Fixture(bool overflow) { Provider = new(overflow); Executor = new([First, Second]); }
        internal static async Task<Fixture> Create(bool overflow)
        {
            var fixture = new Fixture(overflow);
            var folder = Path.Combine(Path.GetTempPath(), "PiSharp-clean-auto-bash-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
            { type = "session", version = 3, id = "clean-boundary", timestamp = "2026-10-05T00:00:00.000Z", cwd = folder }));
            var identity = 0; var ticks = DateTimeOffset.Parse("2026-10-05T00:00:00.000Z").ToUnixTimeMilliseconds();
            fixture.Session = await PersistentAgentSession.CreateAsync(Path.Combine(folder, "session.jsonl"), header,
                new(ProviderTransport.Model, fixture.Provider, []), () => Interlocked.Increment(ref ticks), () => "clean-" + ++identity,
                new(SessionLogStoreOptions: new(StorageFactory: fixture.Storage)));
            try
            {
                await fixture.Session.PromptAsync(Input(new string('a', 2000))); await fixture.Session.PromptAsync(Input(new string('c', 2000)));
                fixture.Session.ConfigureUserBashExecution(fixture.Executor);
                fixture.Session.ConfigureCompactionObservation(value => { fixture.Observations.Add(value); return ValueTask.CompletedTask; });
                fixture.Session.ConfigureAutomaticCompaction(fixture.Summary, new(true, 128, 128), overflow ? 100000 : 3000);
                if (overflow) fixture.Session.ConfigureAutomaticRecovery(1000);
                return fixture;
            }
            catch (Exception error) { await Join(error, () => fixture.Session.DisposeAsync().AsTask()); throw; }
        }
        private static TranscriptEntry Input(string value) => new("user", JsonData.Parse(JsonSerializer.Serialize(new { role = "user", content = value, timestamp = 1 })));
        internal Task<AgentLoopResult> Start(CancellationToken token = default) => Session.PromptAsync(Input("boundary provider"), token);
        internal void Release()
        {
            Provider.Finish.TrySetResult();
            foreach (var slot in new[] { First, Second }) { slot.Result.TrySetResult(new("released fixture", 0, false, false)); slot.CleanupFinish.TrySetResult(); }
        }
        public async ValueTask DisposeAsync()
        {
            Release();
            var unexpected = new List<Exception>();
            foreach (var slot in new[] { First, Second })
                foreach (var original in new Task?[] { slot.Original, slot.ProgressOriginal })
                    if (original is not null)
                        try { await original; }
                        catch (Exception error) when (ExpectedCloseError(error)) { }
                        catch (Exception error) { unexpected.Add(error); }
            var close = Session.DisposeAsync().AsTask();
            try
            {
                await close;
                Require(ExpectedPoison is null, "Poisoned pending flush unexpectedly closed without its terminal refusal.");
            }
            catch (Exception error) when (ExpectedPoison is not null && error is PersistentAgentSessionException
                { Fault.Failure: PersistentAgentSessionFailure.CleanupFailed, InnerException: AggregateException { InnerExceptions.Count: 1 } cleanup } &&
                cleanup.InnerExceptions[0] is PersistentAgentSessionException { Fault.Failure: PersistentAgentSessionFailure.Faulted, InnerException: null })
            {
                Require(close.IsFaulted && Session.Snapshot.IsDisposed && ReferenceEquals(ExpectedPoison, Session.Snapshot.Fault) &&
                    ReferenceEquals(close, Session.DisposeAsync().AsTask()) && ReferenceEquals(await Failure(close), error),
                    "Poisoned close changed the fault snapshot or repeated cleanup original.");
            }
            catch (Exception error) { unexpected.Add(error); }
            Require(Storage.CloseOriginal is { IsCompletedSuccessfully: true }, "Session close did not join the physical storage disposal original.");
            if (unexpected.Count == 1) ExceptionDispatchInfo.Capture(unexpected[0]).Throw();
            if (unexpected.Count > 1) throw new AggregateException(unexpected);
        }
        private bool ExpectedCloseError(Exception error)
        {
            var leaves = CloseLeaves(error).ToArray();
            return leaves.Length > 0 && leaves.All(leaf => ExpectedErrors.Any(expected => ReferenceEquals(leaf, expected))) &&
                leaves.All(leaf => leaves.Count(value => ReferenceEquals(value, leaf)) == 1);
        }
    }
    private sealed class Executor(Slot[] slots) : IUserBashExecutor
    {
        private int next;
        public Task<UserBashResult> ExecuteAsync(UserBashExecutionRequest request, UserBashProgress progress, CancellationToken token)
        {
            var index = Interlocked.Increment(ref next) - 1;
            if (index >= slots.Length) throw new InvalidOperationException("Unexpected executor admission.");
            var slot = slots[index];
            return slot.Original = slot.Run(progress);
        }
    }
    private sealed class Slot
    {
        internal bool PublishProgress, HoldCleanup;
        internal Exception? CleanupFailure;
        internal readonly TaskCompletionSource<UserBashResult> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource CleanupEntered = Gate(), CleanupFinish = Gate();
        internal Task<UserBashResult>? Original;
        internal Task? ProgressOriginal;
        internal async Task<UserBashResult> Run(UserBashProgress progress)
        {
            // Deliberately return without joining progress: the session must retain the observer's exact original.
            if (PublishProgress) ProgressOriginal = progress("observed delta");
            var failures = new List<Exception>(); UserBashResult? value = null;
            try { value = await Result.Task; } catch (Exception error) { failures.Add(error); }
            CleanupEntered.TrySetResult();
            if (!HoldCleanup) CleanupFinish.TrySetResult();
            try { await CleanupFinish.Task; if (CleanupFailure is not null) throw CleanupFailure; }
            catch (Exception error) { failures.Add(error); }
            if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures.Count > 1) throw new AggregateException(failures);
            return value!;
        }
    }
    private sealed class Summary : ISessionSummaryGenerator
    {
        internal int Calls; internal Action? Validate;
        public ValueTask<SessionGeneratedSummary> GenerateAsync(SessionSummaryRequest request, CancellationToken token = default)
        { Validate?.Invoke(); token.ThrowIfCancellationRequested(); Calls++; return ValueTask.FromResult(new SessionGeneratedSummary("clean boundary summary", TokenUsage.Zero)); }
    }
    private sealed class ProviderTransport(bool overflow) : IChatTransport
    {
        internal static readonly ModelDescriptor Model = new("clean-boundary-model", "openai-responses", "fixture");
        internal int Calls; internal readonly TaskCompletionSource Streaming = Gate(), Finish = Gate();
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            var call = ++Calls; var failed = call == 3 && overflow;
            var message = new AssistantMessage(Model.Api, Model.Provider, Model.Id, call, [new TextContent("provider done")], TokenUsage.Zero,
                failed ? StopReason.Error : StopReason.Stop, failed ? JsonFields.Empty.Set("errorMessage", JsonData.Parse("\"input exceeds the context window\"")) : null);
            yield return new StreamStarted(message with { Content = [], StopReason = StopReason.Pending });
            if (call == 3) { Streaming.TrySetResult(); await Finish.Task; }
            yield return new TextStarted(0, new("")); yield return new TextEnded(0, "provider done");
            yield return failed ? new StreamError(StopReason.Error, message) : new StreamDone(StopReason.Stop, message);
        }
    }
    private sealed class StorageFactory : ISessionLogStorageFactory
    {
        internal Func<Task>? BeforeBashCheckpoint;
        internal Task? CloseOriginal;
        public async ValueTask<ISessionLogStorage> OpenAsync(string path, bool createNew, CancellationToken token) =>
            new Storage(await SessionLogStore.DefaultStorageFactory.OpenAsync(path, createNew, token), this);
    }
    private sealed class Storage(ISessionLogStorage inner, StorageFactory fixture) : ISessionLogStorage
    {
        private bool bashBatch;
        private readonly object closeGate = new();
        public Stream ReadStream => inner.ReadStream;
        public SessionLogStorageDurability Durability => inner.Durability;
        public long Length => inner.Length;
        public void PositionForAppend(long expectedLength) => inner.PositionForAppend(expectedLength);
        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes)
        {
            bashBatch |= System.Text.Encoding.UTF8.GetString(bytes.Span).Contains("bashExecution", StringComparison.Ordinal);
            return inner.WriteAsync(bytes);
        }
        public ValueTask FlushAsync() => inner.FlushAsync();
        public void FlushToDisk() => inner.FlushToDisk();
        public async ValueTask BeforeCheckpointAsync()
        {
            if (bashBatch && fixture.BeforeBashCheckpoint is { } callback) await callback();
            await inner.BeforeCheckpointAsync(); bashBatch = false;
        }
        public ValueTask DisposeAsync()
        { lock (closeGate) return new(fixture.CloseOriginal ??= inner.DisposeAsync().AsTask()); }
    }
}
