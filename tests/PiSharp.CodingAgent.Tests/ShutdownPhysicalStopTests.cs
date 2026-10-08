using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Sessions.Serialization;

// Held source-only controls. Synthetic session files are created only if separately authorized to execute.
internal static class ShutdownPhysicalStopTests
{
    private static readonly ModelDescriptor Model = new("shutdown-order", "openai-responses", "fixture");
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Shutdown ordering assertion failed."); }
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("shutdown-stop. physical original precedes synchronous cancellation callback joins and guards", PhysicalBeforeCancellation),
        ("shutdown-stop. callback original multifault inventory and stable close identity", MultipleOriginalFaults),
        ("shutdown-stop. faulted OCE and actually canceled physical originals retain distinct states", StopTaskStates),
        ("shutdown-stop. queued retirement waits postcommit notification then genuinely acknowledges publication", QueuedRetirement),
    ];
    private static void Reject(Action action)
    { try { action(); throw new Exception("Reentry accepted."); } catch (InvalidOperationException) { } }
    private static async Task<Exception?> Failure(Task task)
    { try { await task; return null; } catch (Exception error) { return error; } }
    private static IEnumerable<ReplaceableAgentSession.OwnedResourceStopException> StopFaults(Exception? error)
    {
        if (error is ReplaceableAgentSession.OwnedResourceStopException stop) yield return stop;
        if (error is AggregateException aggregate)
            foreach (var child in aggregate.InnerExceptions) foreach (var nested in StopFaults(child)) yield return nested;
        else if (error?.InnerException is { } inner) foreach (var nested in StopFaults(inner)) yield return nested;
    }

    private static async Task PhysicalBeforeCancellation()
    {
        using var fixture = new Fixture(); var owner = await fixture.Owner();
        var notification = Gate(); var physicalEntered = Gate(); var physicalOriginal = Gate(); var cancellationEntered = Gate();
        CancellationTokenRegistration registration = default; ReplaceableAgentSession.OwnedResourceLease? lease = null;
        owner.AfterReplacement = async replacement =>
        {
            lease = owner.RegisterOwnedResource(replacement.Current, _ => Task.CompletedTask,
                () => { physicalEntered.TrySetResult(); return physicalOriginal.Task; });
            registration = replacement.Current.LifetimeToken.UnsafeRegister(_ =>
            {
                Check(physicalEntered.Task.IsCompleted && ReferenceEquals(lease!.PhysicalStopOriginal, physicalOriginal.Task));
                Reject(() => owner.StopAdmissionAndJoinAsync()); Reject(() => owner.DisposeAsync());
                cancellationEntered.TrySetResult(); physicalOriginal.Task.GetAwaiter().GetResult();
            }, null);
            notification.TrySetResult(); await Task.Delay(Timeout.Infinite, replacement.Current.LifetimeToken);
        };
        var switching = owner.SwitchAsync(owner.Current, new(fixture.B));
        Task<Task>? invocation = null; Task? close = null;
        try
        {
            await Task.WhenAny(notification.Task, switching); Check(notification.Task.IsCompleted);
            // Retain the initiator too: synchronous cancellation may block its call before the ValueTask returns.
            invocation = Task.Factory.StartNew(() => owner.DisposeAsync().AsTask(), CancellationToken.None,
                TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
            await cancellationEntered.Task; close = owner.DisposeAsync().AsTask();
            Check(!close.IsCompleted && !physicalOriginal.Task.IsCompleted && owner.Current.LifetimeToken.IsCancellationRequested);
            physicalOriginal.TrySetResult(); var first = await invocation;
            Check(ReferenceEquals(first, close)); await close;
            Check(await Failure(switching) is AgentSessionReplacementNotificationException { InnerException: OperationCanceledException });
        }
        finally
        {
            physicalOriginal.TrySetResult(); registration.Dispose();
            var disposal = close ?? owner.DisposeAsync().AsTask();
            if (invocation is not null) { var original = await invocation; await Failure(original); }
            await Failure(switching); await Failure(disposal);
        }
    }

    private static async Task MultipleOriginalFaults()
    {
        using var fixture = new Fixture(); var owner = await fixture.Owner();
        var one = new IOException("physical-original-one"); var two = new InvalidOperationException("physical-original-two");
        var rejected = Gate(); rejected.SetException([one, two]); var calls = 0;
        var lease = owner.RegisterOwnedResource(owner.Current, _ => Task.CompletedTask, () => { calls++; return rejected.Task; });
        var close = lease.CloseAsync(); Exception? first;
        try
        {
            first = await Failure(close); var witness = StopFaults(first).Single();
            Check(calls == 1 && ReferenceEquals(lease.PhysicalStopOriginal, rejected.Task) && ReferenceEquals(witness.OriginalTask, rejected.Task));
            Check(ReferenceEquals(witness.ObservedException, one) && witness.OriginalTaskException is { InnerExceptions.Count: 2 } faults
                && ReferenceEquals(faults.InnerExceptions[0], one) && ReferenceEquals(faults.InnerExceptions[1], two));
            Check(ReferenceEquals(close, lease.CloseAsync()) && ReferenceEquals(first, await Failure(lease.CloseAsync())));
            var shutdown = owner.DisposeAsync().AsTask(); Check(ReferenceEquals(shutdown, owner.DisposeAsync().AsTask()));
            Check(StopFaults(await Failure(shutdown)).Any(witness => ReferenceEquals(witness.OriginalTask, rejected.Task)) && calls == 1);
        }
        finally { await Failure(close); await Failure(owner.DisposeAsync().AsTask()); }
    }

    private static async Task StopTaskStates()
    {
        foreach (var canceled in new[] { false, true })
        {
            using var fixture = new Fixture(); var owner = await fixture.Owner(); using var token = new CancellationTokenSource(); token.Cancel();
            var originalError = new OperationCanceledException("physical-OCE-original", token.Token);
            var original = canceled ? Task.FromCanceled(token.Token) : Task.FromException(originalError);
            var lease = owner.RegisterOwnedResource(owner.Current, _ => Task.CompletedTask, () => original);
            try
            {
                var witness = StopFaults(await Failure(lease.CloseAsync())).Single();
                Check(ReferenceEquals(witness.OriginalTask, original) && original.IsCanceled == canceled && original.IsFaulted != canceled);
                Check(canceled ? witness.ObservedException is OperationCanceledException && witness.OriginalTaskException is null
                    : ReferenceEquals(witness.ObservedException, originalError) && witness.OriginalTaskException is { InnerExceptions.Count: 1 });
            }
            finally { await Failure(owner.DisposeAsync().AsTask()); }
        }
    }

    private static async Task QueuedRetirement()
    {
        using var fixture = new Fixture(); var owner = await fixture.Owner(); var entered = Gate(); var cleanup = Gate(); var release = Gate();
        ReplaceableAgentSession.OwnedResourceLease? lease = null; var acknowledged = false; var bodies = 0;
        owner.AfterReplacement = async replacement =>
        {
            lease = owner.RegisterOwnedResource(replacement.Current, async tx =>
            {
                bodies++;
                await tx.PrepareAndPublishCatalogAsync((registry, names, token) =>
                {
                    Check(!token.CanBeCanceled);
                    return ValueTask.FromResult(new PreparedSessionToolCatalog(
                        registry.WithToolCatalog(registry.RegisteredTools, registry.PreparedToolHooks), names, () => acknowledged = true));
                });
                Check(acknowledged);
            });
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, replacement.Current.LifetimeToken); }
            finally { cleanup.TrySetResult(); await release.Task; }
        };
        var switching = owner.SwitchAsync(owner.Current, new(fixture.B)); Task? normal = null; Task? stop = null;
        try
        {
            await Task.WhenAny(entered.Task, switching); Check(entered.Task.IsCompleted);
            normal = lease!.CloseAsync(); Check(!normal.IsCompleted && bodies == 0);
            stop = owner.DisposeAsync().AsTask(); await cleanup.Task;
            Check(!normal.IsCompleted && !stop.IsCompleted && bodies == 0 && owner.Current.LifetimeToken.IsCancellationRequested);
            release.TrySetResult(); await stop; await normal;
            Check(acknowledged && bodies == 1 && ReferenceEquals(normal, lease!.CloseAsync()));
            Check(await Failure(switching) is AgentSessionReplacementNotificationException { InnerException: OperationCanceledException });
        }
        finally
        {
            release.TrySetResult(); var disposal = stop ?? owner.DisposeAsync().AsTask();
            await Failure(switching); if (normal is not null) await Failure(normal); await Failure(disposal);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "PiSharp-shutdown-order-" + Guid.NewGuid().ToString("N"));
        public string B => Path.Combine(root, "b.jsonl");
        private readonly SessionRuntimeRegistry registry = new([new(Model, new NoTransport())], [], new Deny());
        private int ids;
        public async Task<ReplaceableAgentSession> Owner()
        {
            Directory.CreateDirectory(root); await using (var staged = await Create(B)) { }
            var initial = await Create(Path.Combine(root, "a.jsonl"));
            return new(initial, (request, token) => PersistentAgentSession.OpenWithRegistryAsync(request.Path, registry,
                () => 1, () => "open-" + Interlocked.Increment(ref ids), fallbackModel: Model, cancellationToken: token));
        }
        private Task<PersistentAgentSession> Create(string path)
        {
            var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3,
                id = Path.GetFileNameWithoutExtension(path), timestamp = "2026-10-05T00:00:00.000Z", cwd = root }));
            return PersistentAgentSession.CreateAsync(path, header, registry, Model, () => 1, () => "entry-" + Interlocked.Increment(ref ids));
        }
        public void Dispose()
        {
            var absolute = Path.GetFullPath(root); var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (Path.GetDirectoryName(absolute) != parent || !Path.GetFileName(absolute).StartsWith("PiSharp-shutdown-order-", StringComparison.Ordinal))
                throw new InvalidOperationException("Invalid fixture cleanup path.");
            if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
        }
    }
    private sealed class NoTransport : IChatTransport
    {
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        { await Task.FromException(new InvalidOperationException("Shutdown fixture must not invoke provider.")); yield break; }
    }
    private sealed class Deny : IToolActionPolicy
    {
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) =>
            ValueTask.FromResult(new ToolActionAuthorization(false));
    }
}
