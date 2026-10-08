using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Cli.Reloading;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions.Abstractions.Reloading;
using PiSharp.Sessions.Lifecycle;
using PiSharp.Sessions.Serialization;

internal static class NativeHostReloadStopOrderTests
{
    internal const string Prefix = "native-host-reload-stop-order.";
    internal static (string Name, Func<Task> Run)[] Cases() =>
    [
        (Prefix + "physical stop precedes held lifetime cancellation and close retains every original fault", CancellationDependsOnStop),
        (Prefix + "physical stop precedes held discovery drain and concurrent close joins every original", DiscoveryDependsOnStop)
    ];

    private static async Task CancellationDependsOnStop()
    {
        await using var fixture = await Fixture.Create();
        var stopEntered = Gate(); var stopRelease = Gate(); var physicallyStopped = Gate(); var cancellationEntered = Gate();
        var cancelFault = new IOException("original lifetime cancellation");
        var stopFault = new IOException("original physical stop");
        var stopOce = new OperationCanceledException("faulted physical stop OCE");
        var bodyFault = new IOException("original resource body");
        var stops = 0; var bodies = 0;
        var stopFailures = Gate(); stopFailures.SetException([stopFault, stopOce]);
        Task? stopOriginal = null;
        fixture.Owner.RegisterOwnedResource(fixture.Initial, _ =>
        { Interlocked.Increment(ref bodies); return Task.FromException(bodyFault); }, () =>
        {
            Interlocked.Increment(ref stops); stopEntered.TrySetResult();
            stopOriginal = StopAfterRelease(); return stopOriginal;
        });
        async Task StopAfterRelease()
        {
            await stopRelease.Task; physicallyStopped.TrySetResult();
            try { await stopFailures.Task; }
            catch { throw stopFailures.Task.Exception!; }
        }
        using var registration = fixture.Initial.LifetimeToken.Register(() =>
        {
            cancellationEntered.TrySetResult();
            physicallyStopped.Task.GetAwaiter().GetResult();
            throw cancelFault;
        });
        // Launches are owned and joined below: synchronous cancellation is allowed to
        // block the reload caller, so the test driver must remain able to release stop.
        var reload = Task.Run(() => fixture.Coordinator.ReloadAsync()); Task? close = null; var closeReturned = Gate();
        try
        {
            // Check before launching close: normal close must not accidentally rescue
            // the broken reload ordering by starting its resource stops for it.
            await Within(stopEntered.Task); await Within(cancellationEntered.Task);
            Check(!reload.IsCompleted && !physicallyStopped.Task.IsCompleted);
            close = Task.Run(async () =>
            { var original = fixture.Owner.DisposeAsync().AsTask(); closeReturned.TrySetResult(); await original; });
            await Within(closeReturned.Task); Check(!close.IsCompleted && !reload.IsCompleted); stopRelease.TrySetResult();
            var reloadFailure = await Failure(reload); var closeFailure = await Failure(close);
            foreach (var original in new Exception[] { cancelFault, stopFault, stopOce, bodyFault })
            { Check(Contains(reloadFailure, original)); Check(Contains(closeFailure, original)); }
            Check(stops == 1 && bodies == 1 && stopOriginal is { IsFaulted: true });
        }
        finally
        {
            // Failure-only escape also releases the old-order cycle, allowing teardown
            // to join its originals instead of abandoning a blocked cancellation thread.
            stopRelease.TrySetResult(); physicallyStopped.TrySetResult();
            await Settle(reload); if (close is not null) await Settle(close);
            if (stopOriginal is not null) await Settle(stopOriginal);
        }
    }

    private static async Task DiscoveryDependsOnStop()
    {
        var discovery = new HeldCatalogFiles();
        await using var fixture = await Fixture.Create(discovery);
        var stopEntered = Gate(); var stopRelease = Gate(); var stops = 0; var bodies = 0;
        Task? stopOriginal = null;
        fixture.Owner.RegisterOwnedResource(fixture.Initial, _ =>
        { Interlocked.Increment(ref bodies); return Task.CompletedTask; }, () =>
        {
            Interlocked.Increment(ref stops); stopEntered.TrySetResult();
            stopOriginal = StopAfterRelease(); return stopOriginal;
        });
        async Task StopAfterRelease()
        { await stopRelease.Task; discovery.PhysicallyStopped.TrySetResult(); }
        var read = fixture.Owner.ListSessionsAsync(fixture.Initial, new());
        Task? close = null; Task? reload = null; var closeReturned = Gate();
        try
        {
            await Within(discovery.Entered.Task);
            reload = Task.Run(() => fixture.Coordinator.ReloadAsync());
            // This assertion fails for discovery-before-stop ordering even if normal
            // shutdown could otherwise start stop and conceal that dependency cycle.
            await Within(stopEntered.Task);
            Check(!read.IsCompleted && !reload.IsCompleted && !discovery.PhysicallyStopped.Task.IsCompleted);
            close = Task.Run(async () =>
            { var original = fixture.Owner.DisposeAsync().AsTask(); closeReturned.TrySetResult(); await original; });
            await Within(closeReturned.Task); Check(!close.IsCompleted && !read.IsCompleted && !reload.IsCompleted);
            stopRelease.TrySetResult();
            await Within(reload); await Within(close); await Settle(read);
            Check(read.IsCanceled && stops == 1 && bodies == 1 && stopOriginal is { IsCompletedSuccessfully: true });
            Check(discovery.ReturnedStreams == discovery.DisposedStreams && discovery.ReturnedStreams == 1);
        }
        finally
        {
            stopRelease.TrySetResult(); discovery.PhysicallyStopped.TrySetResult();
            await Settle(read); if (reload is not null) await Settle(reload);
            if (close is not null) await Settle(close); if (stopOriginal is not null) await Settle(stopOriginal);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "pisharp-reload-stop-order-" + Guid.NewGuid().ToString("N"));
        internal ReplaceableAgentSession Owner = null!;
        internal AgentSessionAttachment Initial = null!;
        internal NativeHostReloadCoordinator Coordinator = null!;
        internal static async Task<Fixture> Create(HeldCatalogFiles? files = null)
        {
            var fixture = new Fixture(); Directory.CreateDirectory(fixture.root);
            var model = new ModelDescriptor("reload-stop", "openai-responses", "authored");
            var registry = new SessionRuntimeRegistry([new(model, new UnusedTransport())], [], new Policy());
            var headerJson = JsonSerializer.Serialize(new
                { type = "session", version = 3, id = "reload-stop", timestamp = "2026-10-05T00:00:00.000Z", cwd = fixture.root });
            if (files is not null) files.Header = Encoding.UTF8.GetBytes(headerJson + "\n");
            var catalog = files is null ? null : new SessionCatalog([new("held", fixture.root)], fileSystem: files);
            var ids = 0; var lifecycle = new PersistentSessionLifecycle(registry, () => 1, () => "entry-" + ++ids, catalog: catalog);
            var session = await lifecycle.CreateAsync(Path.Combine(fixture.root, "session.jsonl"), new SessionEntryCodec().Parse(headerJson), model);
            fixture.Owner = lifecycle.Attach(session); fixture.Initial = fixture.Owner.Current;
            fixture.Coordinator = new(fixture.Owner, fixture.Initial, new(new object()), [], false, new([], []), new()
            {
                StageSettingsAsync = (_, _) => ValueTask.FromResult(new NativeHostReloadPayload(new object())),
                SyncQueueModesAsync = (_, _) => ValueTask.CompletedTask,
                ResetApiProvidersAsync = (_, _) => ValueTask.CompletedTask,
                ReloadResourcesAsync = (_, _) => ValueTask.CompletedTask,
                DescribeRuntimeAsync = (_, _) => ValueTask.FromResult(new HostReloadRuntime([], [])),
                BuildRuntimeAsync = (_, _, _) => ValueTask.FromResult(new PreparedNativeHostReload(new(registry), () => { })),
                SessionShutdownAsync = (_, _, _) => ValueTask.CompletedTask,
                CleanupPreparationAsync = _ => ValueTask.CompletedTask,
                BeforeSessionStartAsync = (_, _) => ValueTask.CompletedTask,
                SessionStartAsync = (_, _, _) => ValueTask.CompletedTask,
                ReportUnhandledMcpServersAsync = (_, _) => ValueTask.CompletedTask,
                ExtendResourcesAsync = (_, _, _) => ValueTask.CompletedTask
            });
            return fixture;
        }
        public async ValueTask DisposeAsync()
        {
            await Settle(Owner.DisposeAsync().AsTask());
            if (Path.GetFileName(root).StartsWith("pisharp-reload-stop-order-", StringComparison.Ordinal) &&
                Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())))
                Directory.Delete(root, recursive: true);
        }
    }
    private sealed class HeldCatalogFiles : ISessionCatalogFileSystem
    {
        internal readonly TaskCompletionSource Entered = Gate(), PhysicallyStopped = Gate();
        internal byte[] Header = [];
        internal int ReturnedStreams, DisposedStreams;
        public bool IsDirectoryLink(string directory) => false;
        public IEnumerable<string> EnumerateFileNames(string directory) => ["session.jsonl"];
        public SessionCatalogFileMetadata GetMetadata(string path) => new(Header.Length, 1, false);
        public async ValueTask<Stream> OpenReadAsync(string path, CancellationToken token)
        {
            Entered.TrySetResult(); await PhysicallyStopped.Task;
            Interlocked.Increment(ref ReturnedStreams); return new ObservedStream(this);
        }
        private sealed class ObservedStream(HeldCatalogFiles files) : MemoryStream(files.Header, writable: false)
        {
            private bool disposed;
            protected override void Dispose(bool disposing)
            {
                if (!disposed) { disposed = true; Interlocked.Increment(ref files.DisposedStreams); }
                base.Dispose(disposing);
            }
        }
    }
    private sealed class Policy : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(new ToolActionAuthorization(false)); }
    private sealed class UnusedTransport : IChatTransport
    {
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        { await Task.FromException(new InvalidOperationException("Stop-order fixture must not invoke a provider.")); yield break; }
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Within(Task original) => original.WaitAsync(TimeSpan.FromSeconds(10));
    private static async Task Settle(Task original) { try { await original; } catch (Exception) { } }
    private static async Task<Exception> Failure(Task original)
    { try { await Within(original); } catch (Exception error) { return original.Exception ?? error; } throw new IOException("Expected original failure."); }
    private static bool Contains(Exception error, Exception original)
        => ReferenceEquals(error, original) || (error is AggregateException aggregate
            ? aggregate.InnerExceptions.Any(child => Contains(child, original))
            : error.InnerException is { } inner && Contains(inner, original));
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Reload stop ordering contract failed."); }
}
