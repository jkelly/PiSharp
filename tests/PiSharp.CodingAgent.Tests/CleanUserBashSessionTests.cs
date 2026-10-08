using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.CodingAgent.Execution;
using PiSharp.Contracts;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class CleanUserBashSessionTests
{
    internal static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("clean user Bash executor and progress originals hold repeated abort and close", ExecutorAndProgress),
        ("clean user Bash failed first original still joins second and retains stable abort fault", AllOriginalFaults),
        ("clean user Bash synchronous executor failure still joins accepted original progress", SynchronousFailure),
        ("clean user Bash cancellation failure still stops every operation and retains original causes", CancellationFault),
        ("clean user Bash externally registered cancellation callbacks refuse synchronous self waits", () => ExternalCancellationCallback(false)),
        ("clean user Bash unsafe cancellation callbacks refuse synchronous self waits", () => ExternalCancellationCallback(true)),
        ("clean user Bash admitted checkpoint remains owned through cancellation and close", Checkpoint),
        ("clean user Bash deferred records retain assistant ordering original command and exclusion", Deferred)
    ];
    private static readonly ModelDescriptor Model = new("clean-bash-fixture", "openai-responses", "authored");
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class Marker(string label) : Exception(label) { }

    private static async Task ExecutorAndProgress()
    {
        var f = new Fixture(); var held = new HeldExecutor(); var progressOriginal = Gate();
        UserBashExecutionUpdate? observation = null;
        var session = await f.Open(); session.ConfigureUserBashExecution(held, update => { observation = update; return progressOriginal.Task; });
        var operation = session.ExecuteUserBashAsync("original", id: "correlation");
        var progress = held.Report!("source delta"); held.Execution.SetResult(new("final", 0, false, false));
        var abort = session.AbortUserBashAsync(); var secondAbort = session.AbortUserBashAsync();
        var close = session.StopAdmissionAndJoinAsync(); Exception? primary = null;
        try
        {
            Require(ReferenceEquals(abort, secondAbort) && held.Token.IsCancellationRequested, "Repeated abort changed original task or missed physical cancellation.");
            Require(observation?.Id == "correlation" && observation.Delta == "source delta", "Original progress delta or correlation changed.");
            Require(!operation.IsCompleted && !progress.IsCompleted && !abort.IsCompleted && !close.IsCompleted,
                "Executor completion detached its accepted original progress or close.");
            Require(session.Snapshot.Log.Entries.Length == 2, "Result persisted before original progress settled.");
            progressOriginal.SetResult(); await operation; await progress; await abort; await close;
            Require(session.Snapshot.Log.Entries.Length == 3 && !session.IsUserBashRunning, "Owned result did not checkpoint exactly once.");
        }
        catch (Exception error) { primary = error; }
        finally
        {
            progressOriginal.TrySetResult(); held.Execution.TrySetResult(new("", null, true, false));
            await Settle(primary, operation, progress, abort, secondAbort, close, session.DisposeAsync().AsTask());
        }
    }
    private static async Task AllOriginalFaults()
    {
        var f = new Fixture(); var first = new HeldExecutor(); var second = new HeldExecutor();
        var session = await f.Open(); session.ConfigureUserBashExecution(new Sequence(first, second));
        var one = session.ExecuteUserBashAsync("one"); var two = session.ExecuteUserBashAsync("two");
        var abort = session.AbortUserBashAsync(); var marker = new Marker("original execution"); Exception? primary = null;
        try
        {
            first.Execution.SetException(marker);
            Require(ReferenceEquals(await Fault(one), marker), "Original executor fault identity was replaced.");
            Require(!abort.IsCompleted && !two.IsCompleted && second.Token.IsCancellationRequested, "First fault abandoned cancellation/join of the second original.");
            second.Execution.SetResult(new("second", null, true, false)); await two;
            Require(ReferenceEquals(await Fault(abort), marker) && ReferenceEquals(abort, session.AbortUserBashAsync()),
                "Repeated abort did not retain the exact original task/fault.");
        }
        catch (Exception error) { primary = error; }
        finally
        {
            first.Execution.TrySetException(marker); second.Execution.TrySetResult(new("", null, true, false));
            await Settle(primary, Observe(one), two, Observe(abort), Observe(session.DisposeAsync().AsTask()));
        }
    }
    private static async Task ExternalCancellationCallback(bool unsafeRegistration)
    {
        var f = new Fixture(); var held = new HeldExecutor(); var session = await f.Open();
        session.ConfigureUserBashExecution(held); var operation = session.ExecuteUserBashAsync("held");
        var results = new List<bool>(); var returned = new List<Task>();
        void Probe(Func<Task> enter)
        {
            try
            {
                var original = enter(); returned.Add(original);
                if (!original.IsCompleted) { results.Add(false); return; }
                original.GetAwaiter().GetResult(); results.Add(false);
            }
            catch (InvalidOperationException error) when (error.Message == "Bash-owned callbacks cannot await their own session settlement.")
            { results.Add(true); }
        }
        void Callback()
        {
            Probe(session.AbortUserBashAsync); Probe(() => session.WaitForIdleAsync());
            Probe(session.StopAdmissionAndJoinAsync); Probe(() => session.DisposeAsync().AsTask());
        }
        // Register in the test caller's context, outside the executor's AsyncLocal scope.
        using var registration = unsafeRegistration ? held.Token.UnsafeRegister(_ => Callback(), null) : held.Token.Register(Callback);
        var abort = session.AbortUserBashAsync(); Exception? primary = null;
        try
        {
            Require(results.Count == 4 && results.All(value => value), "External cancellation callback bypassed the original-owner guard.");
            Require(!operation.IsCompleted && !abort.IsCompleted, "Rejecting reentry abandoned the physical original.");
            held.Execution.SetResult(new("", null, true, false)); await operation; await abort;
            await session.WaitForIdleAsync(); await session.DisposeAsync();
        }
        catch (Exception error) { primary = error; }
        finally
        {
            held.Execution.TrySetResult(new("", null, true, false));
            await Settle(primary, new[] { operation, abort, session.DisposeAsync().AsTask() }.Concat(returned.Select(Observe)).ToArray());
        }
    }
    private static async Task SynchronousFailure()
    {
        var f = new Fixture(); var originalProgress = Gate(); var marker = new Marker("synchronous original");
        var executor = new SynchronousFailureExecutor(marker); var session = await f.Open();
        session.ConfigureUserBashExecution(executor, _ => originalProgress.Task);
        var operation = session.ExecuteUserBashAsync("owned"); var abort = session.AbortUserBashAsync(); Exception? primary = null;
        try
        {
            Require(!operation.IsCompleted && !abort.IsCompleted && executor.Accepted is { IsCompleted: false },
                "Synchronous executor failure abandoned accepted original progress.");
            originalProgress.SetResult();
            Require(ReferenceEquals(await Fault(operation), marker) && ReferenceEquals(await Fault(abort), marker), "Synchronous original fault identity changed.");
            Require(session.Snapshot.Log.Entries.Length == 2, "Failed execution manufactured a Bash result.");
        }
        catch (Exception error) { primary = error; }
        finally
        {
            originalProgress.TrySetResult();
            await Settle(primary, Observe(operation), Observe(abort), executor.Accepted ?? Task.CompletedTask, Observe(session.DisposeAsync().AsTask()));
        }
    }
    private static async Task CancellationFault()
    {
        var f = new Fixture(); var first = new HeldExecutor(); var second = new HeldExecutor(); var marker = new Marker("original stop callback");
        var session = await f.Open(); session.ConfigureUserBashExecution(new Sequence(first, second));
        var one = session.ExecuteUserBashAsync("first"); var two = session.ExecuteUserBashAsync("second");
        using var registration = first.Token.Register(() => throw marker);
        var abort = session.AbortUserBashAsync(); var repeat = session.AbortUserBashAsync(); Exception? primary = null;
        bool Contains(Exception error) => ReferenceEquals(error, marker) || error is AggregateException aggregate && aggregate.InnerExceptions.Any(Contains);
        try
        {
            Require(first.Token.IsCancellationRequested && second.Token.IsCancellationRequested && !abort.IsCompleted,
                "A throwing cancellation callback skipped shutdown/join of another owned operation.");
            first.Execution.SetResult(new("", null, true, false));
            Require(Contains(await Fault(one)) && !abort.IsCompleted, "Cancellation cause changed or first completion abandoned second original.");
            second.Execution.SetResult(new("", null, true, false)); await two;
            var cause = await Fault(abort);
            Require(Contains(cause) && ReferenceEquals(abort, repeat) && ReferenceEquals(cause, await Fault(repeat)),
                "Repeated abort replaced the original cancellation task/fault.");
        }
        catch (Exception error) { primary = error; }
        finally
        {
            first.Execution.TrySetResult(new("", null, true, false)); second.Execution.TrySetResult(new("", null, true, false));
            await Settle(primary, Observe(one), two, Observe(abort), Observe(repeat), Observe(session.DisposeAsync().AsTask()));
        }
    }
    private static async Task Checkpoint()
    {
        var f = new Fixture(); var held = new HeldExecutor(); var session = await f.Open();
        session.ConfigureUserBashExecution(held); f.Storage.HoldCheckpoint = true;
        var operation = session.ExecuteUserBashAsync("persisted"); held.Execution.SetResult(new("received", 0, false, false));
        Task? abort = null, close = null; Exception? primary = null;
        try
        {
            await f.Storage.CheckpointEntered.Task;
            abort = session.AbortUserBashAsync(); close = session.DisposeAsync().AsTask();
            Require(!operation.IsCompleted && !abort.IsCompleted && !close.IsCompleted && session.HasPendingUserBashMessages,
                "An admitted append was detached before its original checkpoint.");
            Require(session.Snapshot.Log.Entries.Length == 2, "Unacknowledged Bash record was published.");
            f.Storage.CheckpointRelease.TrySetResult(); await operation; await abort; await close;
            Require(session.Snapshot.Log.Entries.Length == 3 && !session.HasPendingUserBashMessages, "Acknowledged Bash record was dropped or repeated.");
        }
        catch (Exception error) { primary = error; }
        finally
        {
            f.Storage.CheckpointRelease.TrySetResult(); held.Execution.TrySetResult(new("", null, true, false));
            await Settle(primary, operation, abort ?? Task.CompletedTask, close ?? session.DisposeAsync().AsTask());
        }
    }
    private static async Task Deferred()
    {
        var f = new Fixture(); f.Transport.Hold = true; var session = await f.Open();
        var first = new HeldExecutor(); var second = new HeldExecutor(); session.ConfigureUserBashExecution(new Sequence(first, second));
        var run = session.PromptAsync(new TranscriptEntry("user", JsonData.Parse("""{"role":"user","content":"start","timestamp":1000}""")));
        Task? one = null, two = null; Exception? primary = null;
        try
        {
            await f.Transport.Entered.Task;
            one = session.ExecuteUserBashAsync("first", id: "only-on-update"); first.Execution.SetResult(new("visible", 9, false, false)); await one;
            two = session.ExecuteUserBashAsync("second", true); second.Execution.SetResult(new("excluded", 0, false, false)); await two;
            Require(session.HasPendingUserBashMessages && !session.Snapshot.Log.Entries.Any(entry => entry.Type == "message" &&
                entry.WireBody.Value.GetProperty("message").GetProperty("role").GetString() == "bashExecution"), "Streaming inserted a Bash record into assistant ordering.");
            f.Transport.Release.TrySetResult(); await run; await session.WaitForIdleAsync();
            var records = session.Snapshot.Log.Entries.Where(entry => entry.Type == "message").Select(entry => entry.WireBody.Value.GetProperty("message")).ToArray();
            Require(records.Select(value => value.GetProperty("role").GetString()).SequenceEqual(new[] { "user", "assistant", "bashExecution", "bashExecution" }),
                "Deferred records did not follow acknowledged assistant completion order.");
            Require(records[2].GetProperty("command").GetString() == "first" && !records[2].TryGetProperty("id", out _) &&
                records[3].GetProperty("excludeFromContext").GetBoolean() && session.Snapshot.Agent.Messages.Length == 3,
                "Original command/correlation/exclusion or finalized context changed.");
        }
        catch (Exception error) { primary = error; }
        finally
        {
            f.Transport.Release.TrySetResult(); first.Execution.TrySetResult(new("", null, true, false)); second.Execution.TrySetResult(new("", null, true, false));
            await Settle(primary, run, one ?? Task.CompletedTask, two ?? Task.CompletedTask, session.DisposeAsync().AsTask());
        }
    }
    private static async Task<Exception> Fault(Task original)
    { try { await original; } catch (Exception error) { return error; } throw new Exception("Expected original fault."); }
    private static async Task Observe(Task original) { try { await original; } catch (Exception) { } }
    private static async Task Settle(Exception? primary, params Task[] originals)
    {
        var errors = new List<Exception>(); if (primary is not null) errors.Add(primary);
        foreach (var original in originals.Distinct<Task>(ReferenceEqualityComparer.Instance))
            try { await original; } catch (Exception error) { if (!errors.Any(value => ReferenceEquals(value, error))) errors.Add(error); }
        if (errors.Count == 1) ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count != 0) throw new AggregateException(errors);
    }
    private sealed class HeldExecutor : IUserBashExecutor
    {
        internal readonly TaskCompletionSource<UserBashResult> Execution = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token; internal UserBashProgress? Report;
        public Task<UserBashResult> ExecuteAsync(UserBashExecutionRequest request, UserBashProgress progress, CancellationToken token)
        { Token = token; Report = progress; return Execution.Task; }
    }
    private sealed class Sequence(params HeldExecutor[] calls) : IUserBashExecutor
    {
        private int _next;
        public Task<UserBashResult> ExecuteAsync(UserBashExecutionRequest request, UserBashProgress progress, CancellationToken token)
            => calls[Interlocked.Increment(ref _next) - 1].ExecuteAsync(request, progress, token);
    }
    private sealed class SynchronousFailureExecutor(Exception marker) : IUserBashExecutor
    {
        internal Task? Accepted;
        public Task<UserBashResult> ExecuteAsync(UserBashExecutionRequest request, UserBashProgress progress, CancellationToken token)
        { Accepted = progress("accepted before original throw"); throw marker; }
    }
    private sealed class Fixture
    {
        internal readonly MemoryStorage Storage = new(); internal readonly Transport Transport = new();
        internal async Task<PersistentAgentSession> Open()
        {
            var cwd = Path.GetFullPath(Path.GetTempPath()); var identity = 0;
            var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
            { type = "session", version = 3, id = "fixture", timestamp = "2026-10-05T00:00:00.000Z", cwd }));
            return await PersistentAgentSession.CreateAsync(Path.Combine(cwd, "authored-clean-bash-no-disk.jsonl"), header,
                new(Model, Transport, []), () => 1000, () => "owned-" + Interlocked.Increment(ref identity),
                new(SessionLogStoreOptions: new(StorageFactory: new MemoryFactory(Storage))));
        }
    }
    private sealed class MemoryFactory(MemoryStorage storage) : ISessionLogStorageFactory
    { public ValueTask<ISessionLogStorage> OpenAsync(string path, bool createNew, CancellationToken token) => ValueTask.FromResult<ISessionLogStorage>(storage); }
    private sealed class MemoryStorage : ISessionLogStorage
    {
        private readonly MemoryStream _bytes = new(); internal bool HoldCheckpoint;
        internal readonly TaskCompletionSource CheckpointEntered = Gate(), CheckpointRelease = Gate();
        public Stream ReadStream => _bytes; public SessionLogStorageDurability Durability => SessionLogStorageDurability.VolatileMemory;
        public long Length => _bytes.Length;
        public void PositionForAppend(long length) { if (_bytes.Length != length) throw new IOException("Authored checkpoint length changed."); _bytes.Position = length; }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes) => _bytes.WriteAsync(bytes);
        public ValueTask FlushAsync() => ValueTask.CompletedTask;
        public void FlushToDisk() { } // Explicit volatile backend; no disk durability is reported.
        public async ValueTask BeforeCheckpointAsync()
        { if (HoldCheckpoint) { CheckpointEntered.TrySetResult(); await CheckpointRelease.Task; } }
        public ValueTask DisposeAsync() => _bytes.DisposeAsync();
    }
    private sealed class Transport : IChatTransport
    {
        internal bool Hold; internal readonly TaskCompletionSource Entered = Gate(), Release = Gate();
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            var final = new AssistantMessage(Model.Api, Model.Provider, Model.Id, 1000, [new TextContent("complete")], TokenUsage.Zero, StopReason.Stop);
            yield return new StreamStarted(final with { Content = [], StopReason = StopReason.Pending });
            Entered.TrySetResult(); if (Hold) await Release.Task;
            yield return new TextStarted(0, new("")); yield return new TextEnded(0, "complete"); yield return new StreamDone(StopReason.Stop, final);
        }
    }
}
