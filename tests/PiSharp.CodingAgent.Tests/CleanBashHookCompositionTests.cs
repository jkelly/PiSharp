using System.Runtime.CompilerServices;
using System.Reflection;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.CodingAgent.Execution;
using PiSharp.Contracts;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class CleanBashHookCompositionTests
{
    internal const string Prefix = "clean Bash hooks ";
    internal static (string Name, Func<Task> Run)[] Cases() =>
    [
        (Prefix + "final observer and returned transcript follow original progress and Bash checkpoint", FinalSettlement),
        (Prefix + "provider body and final Bash progress faults retain both original references", CombinedFailures)
    ];
    private static readonly ModelDescriptor Model = new("clean-hook", "openai-responses", "authored");
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Clean Bash hook contract failed."); }
    private static async Task<Exception> Failure(Task original)
    { try { await original; } catch (Exception error) { return error; } throw new InvalidOperationException("Expected original failure."); }
    private static IEnumerable<Exception> Leaves(Exception error) => error is AggregateException aggregate
        ? aggregate.InnerExceptions.SelectMany(Leaves) : [error];

    private static readonly FieldInfo Boundary = typeof(PersistentAgentSession).GetField("_automaticBashBoundaryOwner", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static async Task AtFinalFence(PersistentAgentSession session, Task original)
    {
        while (Boundary.GetValue(session) is null)
        {
            if (original.IsCompleted) { await original; throw new InvalidOperationException("Original settled before held final Bash fence."); }
            await Task.Yield();
        }
    }
    private static async Task FinalSettlement()
    {
        var transport = new Transport(); await using var session = await Open(transport);
        var executor = new Executor(); var progress = Gate(); var observerEntered = Gate(); var observerRelease = Gate();
        session.ConfigureUserBashExecution(executor, _ => progress.Task);
        using var observation = session.SubscribeOperationEvents(new Sink(async value =>
        {
            if (value is not SessionOperationSettled settled) return;
            Check(!session.HasPendingUserBashMessages && settled.Result!.Transcript.Last().Role == "user");
            Check(session.Snapshot.Log.Entries.Last().WireBody.Value.GetProperty("message").GetProperty("role").GetString() == "bashExecution");
            observerEntered.TrySetResult(); await observerRelease.Task;
        }));
        var run = session.PromptAsync([new TranscriptEntry("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"go\",\"timestamp\":1000}"))]);
        Task<UserBashResult>? bash = null; Task? accepted = null;
        try
        {
            await transport.Entered.Task; bash = session.ExecuteUserBashAsync("original"); accepted = executor.Progress!("held");
            executor.Done.TrySetResult(new("output", 0, false, false)); transport.Release.TrySetResult();
            await AtFinalFence(session, run); Check(!run.IsCompleted && !observerEntered.Task.IsCompleted);
            progress.TrySetResult(); await accepted; await bash; await observerEntered.Task;
            Check(!run.IsCompleted);
        }
        finally
        {
            progress.TrySetResult(); transport.Release.TrySetResult(); executor.Done.TrySetResult(new("", null, true, false)); observerRelease.TrySetResult();
            if (accepted is not null) await accepted; if (bash is not null) await bash;
            var result = await run;
            Check(result.Transcript.Length == session.Snapshot.Context.LlmMessages.Length && !session.HasPendingUserBashMessages);
        }
    }
    private static async Task CombinedFailures()
    {
        var transport = new Transport(); await using var session = await Open(transport);
        var executor = new Executor(); var progress = Gate(); var body = new IOException("original body"); var cleanup = new IOException("original progress");
        session.ConfigureUserBashExecution(executor, _ => progress.Task);
        session.ConfigureAutomaticRecovery(null, _ => ValueTask.FromException(body));
        var run = session.PromptAsync([new TranscriptEntry("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"go\",\"timestamp\":1000}"))]);
        await transport.Entered.Task; var bash = session.ExecuteUserBashAsync("original"); var accepted = executor.Progress!("held");
        executor.Done.TrySetResult(new("output", 0, false, false)); transport.Release.TrySetResult();
        try { await AtFinalFence(session, run); Check(!run.IsCompleted); }
        finally { progress.TrySetException(cleanup); }
        Check(ReferenceEquals(await Failure(accepted), cleanup) && ReferenceEquals(await Failure(bash), cleanup));
        var error = await Failure(run); var originals = Leaves(error).ToArray();
        Check(originals.Length == 2 && ReferenceEquals(originals[0], body) && ReferenceEquals(originals[1], cleanup));
    }
    private sealed class Sink(Func<SessionOperationEvent, ValueTask> observe) : ISessionOperationEventSink
    { public ValueTask EmitAsync(SessionOperationEvent observation, CancellationToken token) => observe(observation); }
    private sealed class Executor : IUserBashExecutor
    {
        internal readonly TaskCompletionSource<UserBashResult> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal UserBashProgress? Progress;
        public Task<UserBashResult> ExecuteAsync(UserBashExecutionRequest request, UserBashProgress progress, CancellationToken token)
        { Progress = progress; return Done.Task; }
    }
    private static Task<PersistentAgentSession> Open(Transport transport)
    {
        var cwd = Path.GetFullPath(Path.GetTempPath()); var identity = 0;
        var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
        { type = "session", version = 3, id = "fixture", timestamp = "2026-10-05T00:00:00.000Z", cwd }));
        return PersistentAgentSession.CreateAsync(Path.Combine(cwd, "clean-bash-hooks-no-disk.jsonl"), header,
            new(Model, transport, []), () => 1000, () => "hook-" + Interlocked.Increment(ref identity),
            new(SessionLogStoreOptions: new(StorageFactory: new MemoryFactory())));
    }
    private sealed class MemoryFactory : ISessionLogStorageFactory
    { public ValueTask<ISessionLogStorage> OpenAsync(string path, bool createNew, CancellationToken token) => ValueTask.FromResult<ISessionLogStorage>(new MemoryStorage()); }
    private sealed class MemoryStorage : ISessionLogStorage
    {
        private readonly MemoryStream bytes = new();
        public Stream ReadStream => bytes; public SessionLogStorageDurability Durability => SessionLogStorageDurability.VolatileMemory;
        public long Length => bytes.Length;
        public void PositionForAppend(long length) { if (bytes.Length != length) throw new IOException("Authored length changed."); bytes.Position = length; }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> value) => bytes.WriteAsync(value);
        public ValueTask FlushAsync() => ValueTask.CompletedTask;
        public void FlushToDisk() { }
        public ValueTask BeforeCheckpointAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => bytes.DisposeAsync();
    }
    private sealed class Transport : IChatTransport
    {
        internal readonly TaskCompletionSource Entered = Gate(), Release = Gate();
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            var final = new AssistantMessage(Model.Api, Model.Provider, Model.Id, 1000, [new TextContent("complete")], TokenUsage.Zero, StopReason.Stop);
            yield return new StreamStarted(final with { Content = [], StopReason = StopReason.Pending });
            Entered.TrySetResult(); await Release.Task;
            yield return new TextStarted(0, new("")); yield return new TextEnded(0, "complete"); yield return new StreamDone(StopReason.Stop, final);
        }
    }
}
