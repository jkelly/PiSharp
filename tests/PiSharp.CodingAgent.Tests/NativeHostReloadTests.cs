using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Cli.Reloading;
using PiSharp.CodingAgent;
using PiSharp.CodingAgent.ToolSelection;
using PiSharp.Contracts;
using PiSharp.Extensions.Abstractions.Reloading;
using PiSharp.Extensions.Runtime.Reloading;
using PiSharp.Sessions.Serialization;

internal static class NativeHostReloadTests
{
    internal const string Prefix = "native-host-reload.";
    internal static (string Name, Func<Task> Run)[] Cases() =>
    [
        (Prefix + "same disk writer publishes new invocation generation policy flags and durable catalog", Durable),
        (Prefix + "old owned withdrawal changes provenance before reload publication", Withdrawal),
        (Prefix + "stage failure preserves old authority before runtime acquisition", StageFailure),
        (Prefix + "malformed prepared multicast commit cleans claimed candidate before invalidation", PreparedMulticast),
        (Prefix + "held original stop body and runtime disposal retain every fault through close", OriginalJoins),
        (Prefix + "post-invalidation commit failure never resurrects old authority", CommitFailure),
        (Prefix + "concurrent close cancels admission but joins original held staging", CloseDuringStage),
        (Prefix + "callback reentry and multicast reject before effects", Reentry),
        (Prefix + "faulted OCE and genuine cancellation retain different task states", CancellationKinds),
        (Prefix + "start failure retains committed new runtime until owner disposal", StartFailure)
    ];

    private static async Task Durable()
    {
        await using var fixture = await Fixture.Create(); var old = fixture.Owner.Current;
        await fixture.Owner.SetSessionNameAsync(old, "before reload");
        Check(fixture.MetadataObserved.SequenceEqual(new long[] { 1 }));
        var notifications = 0;
        fixture.Owner.AttachmentChanged = replacement =>
        {
            Check(replacement.Reason == "reload" && ReferenceEquals(old.Session, replacement.Current.Session));
            Check(replacement.Current.Session.CaptureToolCatalogRegistry().InvocationOwnerGeneration == 2);
            notifications++; return ValueTask.CompletedTask;
        };
        var coordinator = fixture.Coordinator(); var original = coordinator.ReloadAsync();
        Check(ReferenceEquals(original, coordinator.ReloadAsync()));
        var receipt = await original; Check(receipt.Workflow.Authority == ResourceReloadAuthority.New && receipt.Workflow.Failures.IsEmpty);
        var current = receipt.Current!;
        Check(ReferenceEquals(current, fixture.Owner.Current) && ReferenceEquals(current.Session, old.Session));
        Check(old.LifetimeToken.IsCancellationRequested && !current.LifetimeToken.IsCancellationRequested && notifications == 1);
        Throws<InvalidOperationException>(() => fixture.Owner.ValidateAttachment(old));
        Throws<InvalidOperationException>(() => fixture.Owner.ConfigureCompactionObservationForBinding(old, null));
        Throws<InvalidOperationException>(() => fixture.Owner.ConfigureSessionInfoObservationForBinding(old, null));
        await fixture.Owner.SetSessionNameAsync(current, "after reload");
        Check(fixture.MetadataObserved.SequenceEqual(new long[] { 1, 2 }));
        var registry = current.Session.CaptureToolCatalogRegistry();
        Check(registry.UsesFinalActionPolicy(fixture.Policy) && ReferenceEquals(registry.LifetimeToolSelection, fixture.Selection));
        Check(registry.InvocationOwnerGeneration == 2 && fixture.OldReleased == 1 && fixture.NewReleased == 0);
        Check(current.Session.GetActiveTools().SequenceEqual(new[] { "keep" }) && registry.RegisteredTools.Length == 1);
        Check(receipt.Workflow.Candidate!.Flags["retained"].Boolean == false);
        Check(receipt.Workflow.Candidate.Flags["empty"].Text == "");
        Check(fixture.Bound.SequenceEqual(new long[] { 1, 2 }) && fixture.Events.SequenceEqual(new[]
            { "settings", "queues", "providers", "resources", "describe", "build", "shutdown", "commit", "before-start", "start", "mcp", "extend" }));
        var entries = current.Session.Snapshot.Log.Entries.Length;
        await fixture.Owner.DisposeAsync(); Check(fixture.NewReleased == 1);
        await using var reopened = await PersistentAgentSession.OpenWithRegistryAsync(fixture.Path, fixture.NewRegistry!, () => 2, () => "reopen",
            fallbackModel: new ModelDescriptor("reload", "openai-responses", "authored"));
        Check(reopened.Snapshot.Log.Entries.Length == entries && entries > 0);
    }

    private static async Task Withdrawal()
    {
        await using var fixture = await Fixture.Create(); var withdrawn = false;
        fixture.Owner.RegisterOwnedResource(fixture.Owner.Current, async transaction =>
        {
            await transaction.PrepareAndPublishCatalogAsync((registry, names, _) =>
                ValueTask.FromResult(new PreparedSessionToolCatalog(registry.WithToolCatalog([], null), names, () => withdrawn = true)));
        });
        var receipt = await fixture.Coordinator().ReloadAsync();
        Check(withdrawn && receipt.Workflow.Authority == ResourceReloadAuthority.New && receipt.Workflow.Failures.IsEmpty);
        Check(fixture.Owner.Current.Session.Snapshot.Log.Entries.Length >= 2);
    }

    private static async Task StageFailure()
    {
        await using var fixture = await Fixture.Create(); var old = fixture.Owner.Current;
        var error = new IOException("stage build failure"); fixture.BuildFailure = error;
        var coordinator = fixture.Coordinator(); var original = coordinator.ReloadAsync(); var receipt = await original;
        Check(receipt.Workflow.Authority == ResourceReloadAuthority.Old && Contains(receipt.Workflow.Failures[0].Cause, error));
        Check(ReferenceEquals(fixture.Owner.Current, old) && !old.LifetimeToken.IsCancellationRequested && fixture.OldReleased == 0);
        fixture.Owner.ValidateAttachment(old);
        fixture.BuildFailure = null;
        var retry = await fixture.Coordinator().ReloadAsync();
        Check(retry.Workflow.Authority == ResourceReloadAuthority.New);
        Check(ReferenceEquals(original, coordinator.ReloadAsync()));
    }

    private static async Task OriginalJoins()
    {
        await using var fixture = await Fixture.Create(); var stop = Gate(); var entered = Gate(); var dispose = Gate(); var disposeEntered = Gate();
        var stopA = new IOException("stop A"); var stopB = new OperationCanceledException("faulted stop B");
        var bodyA = new IOException("body A"); var bodyB = new IOException("body B");
        var releaseA = new IOException("release A"); var releaseB = new IOException("release B");
        var stopFaults = Faults(stopA, stopB); var bodyFaults = Faults(bodyA, bodyB); var releaseFaults = Faults(releaseA, releaseB);
        fixture.OldDispose = () => { disposeEntered.TrySetResult(); return CombineAfter(dispose.Task, releaseFaults); };
        fixture.Owner.RegisterOwnedResource(fixture.Owner.Current, _ => bodyFaults,
            () => { entered.TrySetResult(); return CombineAfter(stop.Task, stopFaults); });
        var coordinator = fixture.Coordinator(); var original = coordinator.ReloadAsync(); Task? closing = null;
        try
        {
            await entered.Task; Check(!original.IsCompleted && ReferenceEquals(original, coordinator.ReloadAsync()));
            Throws<InvalidOperationException>(() => fixture.Owner.ValidateAttachment(fixture.Initial));
            closing = fixture.Owner.DisposeAsync().AsTask(); Check(!closing.IsCompleted);
            stop.TrySetResult(); await disposeEntered.Task; Check(!original.IsCompleted && !closing.IsCompleted);
            dispose.TrySetResult();
            var failure = await Failure(original);
            foreach (var error in new Exception[] { stopA, stopB, bodyA, bodyB, releaseA, releaseB }) Check(Contains(failure, error));
            await Failure(closing); Check(fixture.OldReleased == 1 && fixture.NewReleased == 1);
        }
        finally
        {
            stop.TrySetResult(); dispose.TrySetResult(); await Settle(original);
            if (closing is not null) await Settle(closing);
        }
    }

    private static async Task PreparedMulticast()
    {
        await using var fixture = await Fixture.Create(); fixture.MulticastCommit = true;
        var receipt = await fixture.Coordinator().ReloadAsync();
        Check(receipt.Workflow.Authority == ResourceReloadAuthority.Old && fixture.NewReleased == 1 && fixture.OldReleased == 0);
        Check(!fixture.Initial.LifetimeToken.IsCancellationRequested && !fixture.Events.Contains("commit"));
        fixture.Owner.ValidateAttachment(fixture.Initial);
    }

    private static async Task CommitFailure()
    {
        await using var fixture = await Fixture.Create(); var original = new IOException("prepared commit failed");
        fixture.CommitFailure = original;
        var failure = await Failure(fixture.Coordinator().ReloadAsync());
        Check(Contains(failure, original) && fixture.Initial.LifetimeToken.IsCancellationRequested);
        Check(fixture.Initial.Session.Snapshot.IsRetired && fixture.NewReleased == 1 && fixture.OldReleased == 1);
        Throws<ObjectDisposedException>(() => fixture.Owner.ValidateAttachment(fixture.Initial));
    }

    private static async Task CloseDuringStage()
    {
        await using var fixture = await Fixture.Create(); var entered = Gate(); var release = Gate();
        fixture.Stage = async token => { entered.TrySetResult(); await release.Task; Check(token.IsCancellationRequested); };
        var reload = fixture.Coordinator().ReloadAsync(); Task? closing = null;
        try
        {
            await entered.Task; closing = fixture.Owner.DisposeAsync().AsTask();
            Check(!reload.IsCompleted && !closing.IsCompleted); release.TrySetResult();
            var receipt = await reload; Check(receipt.Workflow.Authority == ResourceReloadAuthority.Old);
            await closing; Check(fixture.OldReleased == 1 && fixture.NewReleased == 1);
        }
        finally { release.TrySetResult(); await Settle(reload); if (closing is not null) await Settle(closing); }
    }

    private static async Task Reentry()
    {
        await using var fixture = await Fixture.Create(); NativeHostReloadCoordinator? coordinator = null;
        var rejected = false;
        fixture.Stage = _ =>
        {
            Throws<InvalidOperationException>(() => coordinator!.ReloadAsync());
            Throws<InvalidOperationException>(() => fixture.Owner.DisposeAsync());
            rejected = true; return Task.CompletedTask;
        };
        Func<AgentSessionReplacement, ValueTask> one = _ => ValueTask.CompletedTask;
        fixture.Owner.AttachmentChanged = one + one;
        coordinator = fixture.Coordinator(); Throws<ArgumentException>(() => coordinator.ReloadAsync());
        Check(fixture.Events.Count == 0); fixture.Owner.AttachmentChanged = null;
        await coordinator.ReloadAsync(); Check(rejected);
    }

    private static async Task CancellationKinds()
    {
        foreach (var canceled in new[] { false, true })
        {
            await using var fixture = await Fixture.Create(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            var cause = new OperationCanceledException("faulted OCE");
            var original = canceled ? Task.FromCanceled(cancellation.Token) : Task.FromException(cause);
            fixture.Stage = _ => original;
            var receipt = await fixture.Coordinator().ReloadAsync();
            Check(receipt.Workflow.Authority == ResourceReloadAuthority.Old);
            var states = Errors(receipt.Workflow.Failures[0].Cause).OfType<HostReloadCallbackFailure>().ToArray();
            Check(states.Any(state => state.OriginalTaskIsCanceled == canceled &&
                (canceled ? state.OriginalTaskException is null : state.OriginalTaskException is not null && Contains(state, cause))));
        }
    }

    private static async Task StartFailure()
    {
        await using var fixture = await Fixture.Create(); var error = new IOException("start failure"); fixture.StartFailure = error;
        var receipt = await fixture.Coordinator().ReloadAsync();
        Check(receipt.Workflow.Authority == ResourceReloadAuthority.New && Contains(receipt.Workflow.Failures[0].Cause, error));
        Check(ReferenceEquals(receipt.Current, fixture.Owner.Current) && fixture.NewReleased == 0);
        fixture.Owner.ValidateAttachment(receipt.Current!); await fixture.Owner.DisposeAsync(); Check(fixture.NewReleased == 1);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private static readonly ModelDescriptor Model = new("reload", "openai-responses", "authored");
        private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pisharp-native-reload-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(root, "session.jsonl");
        internal readonly Policy Policy = new();
        internal readonly AllowedToolSelection Selection = AllowedToolSelection.Create(tools: ["keep"]);
        internal readonly List<string> Events = [];
        internal readonly List<long> Bound = [];
        internal readonly List<long> MetadataObserved = [];
        internal ReplaceableAgentSession Owner = null!;
        internal AgentSessionAttachment Initial = null!;
        internal SessionRuntimeRegistry? NewRegistry;
        internal Func<CancellationToken, Task>? Stage;
        internal Func<Task>? OldDispose;
        internal Exception? BuildFailure, CommitFailure, StartFailure;
        internal bool MulticastCommit;
        internal int OldReleased, NewReleased;
        private int ids;

        internal static async Task<Fixture> Create()
        {
            var fixture = new Fixture(); Directory.CreateDirectory(fixture.root);
            var registry = fixture.Registry(fixture.Policy, fixture.Selection);
            var lifecycle = new PersistentSessionLifecycle(registry, () => 1, () => "entry-" + ++fixture.ids,
                runtimeForAttachment: (_, _, _) => ValueTask.FromResult(new SessionRuntimeLease(registry,
                    new Release(() => { fixture.OldReleased++; return fixture.OldDispose?.Invoke() ?? Task.CompletedTask; }), fixture.Bind)));
            var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
                { type = "session", version = 3, id = "reload", timestamp = "2026-10-05T00:00:00.000Z", cwd = fixture.root }));
            var session = await lifecycle.CreateAsync(fixture.Path, header, Model);
            fixture.Owner = await lifecycle.AttachAsync(session); fixture.Initial = fixture.Owner.Current;
            await session.SetActiveToolsAsync(["keep"]);
            return fixture;
        }
        private SessionRuntimeRegistry Registry(Policy policy, AllowedToolSelection? selection = null)
            => new([new(Model, new UnusedTransport())], [Tool("keep"), Tool("escape")], policy,
                new SessionRuntimeRegistryOptions { BindNestedCallsToSessionOwner = true, LifetimeToolSelection = selection });
        private static SessionRegisteredTool Tool(string name) => new(
            JsonData.Parse(JsonSerializer.Serialize(new { name, description = "reload fixture", parameters = new { type = "object" } })), new Adapter(name));
        private void Bind(ReplaceableAgentSession owner, AgentSessionAttachment attachment)
        {
            Check(owner.CaptureToolCatalogRegistryForBinding(attachment).InvocationOwnerGeneration == attachment.Generation);
            Throws<InvalidOperationException>(() => attachment.Session.ConfigureCompactionObservation(null));
            Throws<InvalidOperationException>(() => attachment.Session.ConfigureSessionInfoObservation(null));
            owner.ConfigureCompactionObservationForBinding(attachment, _ => ValueTask.CompletedTask);
            owner.ConfigureSessionInfoObservationForBinding(attachment, _ =>
            { MetadataObserved.Add(attachment.Generation); return ValueTask.CompletedTask; });
            Bound.Add(attachment.Generation);
        }
        internal NativeHostReloadCoordinator Coordinator()
        {
            ValueTask Step(string name) { Events.Add(name); return ValueTask.CompletedTask; }
            return new(Owner, Initial, new(new object()), new Dictionary<string, HostReloadFlagValue>
                { ["retained"] = new(false), ["empty"] = new("") }, true, new([], []), new()
            {
                StageSettingsAsync = async (_, token) =>
                {
                    Events.Add("settings");
                    if (Stage is not null)
                    {
                        var original = Stage(token);
                        try { await original; }
                        catch (Exception error) { throw new HostReloadCallbackFailure(original.IsCanceled, original.Exception, error); }
                    }
                    return new(new object());
                },
                SyncQueueModesAsync = (_, _) => Step("queues"), ResetApiProvidersAsync = (_, _) => Step("providers"),
                ReloadResourcesAsync = (_, _) => Step("resources"),
                DescribeRuntimeAsync = (_, _) =>
                { Events.Add("describe"); return ValueTask.FromResult(new HostReloadRuntime(new Dictionary<string, HostReloadFlagValue> { ["retained"] = new(true) },
                    [new("keep", false, HostReloadToolExposure.Direct), new("escape", true, HostReloadToolExposure.Direct)])); },
                BuildRuntimeAsync = (_, _, _) =>
                {
                    Events.Add("build"); if (BuildFailure is not null) throw BuildFailure;
                    NewRegistry = Registry(new Policy());
                    Action commit = () => { Events.Add("commit"); if (CommitFailure is not null) throw CommitFailure; };
                    if (MulticastCommit) commit += commit;
                    return ValueTask.FromResult(new PreparedNativeHostReload(new(NewRegistry,
                        new Release(() => { NewReleased++; return Task.CompletedTask; }), Bind), commit));
                },
                SessionShutdownAsync = (_, reason, _) => { Check(reason == "reload"); return Step("shutdown"); },
                CleanupPreparationAsync = _ => ValueTask.CompletedTask,
                BeforeSessionStartAsync = (_, _) => Step("before-start"),
                SessionStartAsync = (_, _, _) => { Events.Add("start"); if (StartFailure is not null) throw StartFailure; return ValueTask.CompletedTask; },
                ReportUnhandledMcpServersAsync = (_, _) => Step("mcp"), ExtendResourcesAsync = (_, _, _) => Step("extend")
            });
        }
        public async ValueTask DisposeAsync()
        {
            await Settle(Owner.DisposeAsync().AsTask());
            if (System.IO.Path.GetFileName(root).StartsWith("pisharp-native-reload-", StringComparison.Ordinal) &&
                System.IO.Path.GetDirectoryName(root) == System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath())))
                Directory.Delete(root, recursive: true);
        }
    }
    private sealed class Release(Func<Task> release) : IAsyncDisposable
    { public ValueTask DisposeAsync() => new(release()); }
    private sealed class Policy : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(new ToolActionAuthorization(false)); }
    private sealed class Adapter(string name) : IPreparedToolAdapter
    {
        public string Name => name;
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token)
            => throw new InvalidOperationException("Reload fixture must not execute tools.");
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(false);
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token)
            => throw new InvalidOperationException("Reload fixture must not execute tools.");
    }
    private sealed class UnusedTransport : IChatTransport
    {
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        { await Task.FromException(new InvalidOperationException("Reload fixture must not invoke a provider.")); yield break; }
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Faults(params Exception[] faults)
    { var completion = Gate(); completion.SetException(faults); return completion.Task; }
    private static Task CombineAfter(Task gate, Task faults)
    {
        var completion = Gate(); _ = Complete(); return completion.Task;
        async Task Complete()
        {
            try { await gate; await faults; completion.TrySetResult(); }
            catch (Exception error)
            { if (faults.Exception is { } all) completion.TrySetException(all.InnerExceptions); else completion.TrySetException(error); }
        }
    }
    private static IEnumerable<Exception> Errors(Exception error)
    {
        yield return error;
        if (error is AggregateException aggregate)
        { foreach (var child in aggregate.InnerExceptions) foreach (var item in Errors(child)) yield return item; }
        else if (error.InnerException is { } inner) foreach (var item in Errors(inner)) yield return item;
    }
    private static bool Contains(Exception actual, Exception expected) => Errors(actual).Any(item => ReferenceEquals(item, expected));
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Native host reload contract failed."); }
    private static void Throws<T>(Action operation) where T : Exception
    { try { operation(); } catch (T) { return; } throw new IOException("Expected " + typeof(T).Name); }
    private static async Task<Exception> Failure(Task original)
    { try { await original; } catch (Exception error) { return original.Exception ?? error; } throw new IOException("Expected original failure."); }
    private static async Task Settle(Task original) { try { await original; } catch (Exception) { } }
}
