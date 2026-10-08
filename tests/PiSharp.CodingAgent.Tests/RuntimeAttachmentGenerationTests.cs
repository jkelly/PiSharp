using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class RuntimeAttachmentGenerationTests
{
    internal const string Prefix = "runtime-attachment-generation.";
    internal static (string Name, Func<Task> Run)[] Cases() =>
    [
        (Prefix + "initial binding repeats cleanup joins without duplicating original faults", FailedBindingFaultIdentity),
        (Prefix + "serialized replacement supplies target generation before acquisition and publication", Serialized),
        (Prefix + "veto releases fresh acquisition and reuses only the uncommitted generation", Veto),
        (Prefix + "actual owner binding precedes initial return and replacement notification", BoundBeforeExposure),
        (Prefix + "partial initial and replacement binding join all held stops before failed target retirement", FailedBinding)
    ];
    private static readonly ModelDescriptor Model = new("generation", "openai-responses", "authored");
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Runtime generation contract failed."); }

    private static async Task Serialized()
    {
        var fixture = new Fixture(); var entered = Gate(); var release = Gate();
        await using var initial = await fixture.Lifecycle.CreateAsync(fixture.A, fixture.Header("a"), Model);
        await fixture.Seed(); await using var owner = fixture.Lifecycle.Attach(initial); var previous = owner.Current;
        var publications = 0;
        owner.AttachmentChanged = _ => { publications++; return ValueTask.CompletedTask; };
        fixture.BeforeAcquire = async generation => { if (generation == 2) { entered.TrySetResult(); await release.Task; } };
        var switching = owner.SwitchAsync(previous, new(fixture.B)); Task<AgentSessionReplacement?>? stale = null;
        var failures = new List<Exception>();
        try
        {
            await entered.Task; Check(ReferenceEquals(owner.Current, previous) && !switching.IsCompleted);
            stale = owner.CreateAsync(previous, new(AgentSessionCreationKind.New)); Check(!stale.IsCompleted);
        }
        catch (Exception error) { failures.Add(error); }
        finally { release.TrySetResult(); }
        AgentSessionReplacement? switched = null;
        try { switched = await switching; } catch (Exception error) { failures.Add(error); }
        if (stale is not null)
        {
            try { await stale; throw new IOException("Stale replacement unexpectedly admitted."); }
            catch (InvalidOperationException) { }
            catch (OperationCanceledException canceled) when (stale.IsCanceled && canceled.CancellationToken.IsCancellationRequested &&
                previous.LifetimeToken.IsCancellationRequested && previous.Session.Snapshot.IsRetired &&
                switched is not null && ReferenceEquals(owner.Current, switched.Current) &&
                !owner.Current.LifetimeToken.IsCancellationRequested)
            { /* The queued wait links the retired attachment lifetime before admission. */ }
            catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count != 0) throw new AggregateException(failures);
        Check(switched is not null && switched.Current.Generation == 2 &&
            ReferenceEquals(owner.Current, switched.Current) && publications == 1 &&
            fixture.Generations.SequenceEqual(new long[] { 1, 2 }));
        var created = await owner.CreateAsync(owner.Current, new(AgentSessionCreationKind.New));
        Check(created is not null && created.Current.Generation == 3 && publications == 2 && fixture.Generations.SequenceEqual(new long[] { 1, 2, 3 }));
        await owner.DisposeAsync(); Check(fixture.Released == 3 && fixture.Backend.ActiveWriterCount == 0);
    }

    private static void CheckReservedBinding(ReplaceableAgentSession owner, AgentSessionAttachment attachment)
    {
        Check(ReferenceEquals(owner.Current, attachment));
        Check(owner.CaptureToolCatalogRegistryForBinding(attachment).InvocationOwnerGeneration == attachment.Generation);
        try { attachment.Session.CaptureToolCatalogRegistry(); throw new IOException("Ordinary catalog read bypassed reservation."); }
        catch (InvalidOperationException) { }
    }
    private static async Task BoundBeforeExposure()
    {
        var fixture = new Fixture(bindInvocationOwner: true); var bound = new List<long>(); var observed = new List<long>();
        fixture.Bind = (bindingOwner, bindingAttachment) =>
        {
            CheckReservedBinding(bindingOwner, bindingAttachment);
            Refused(() => bindingAttachment.Session.ConfigureCompactionObservation(null));
            Refused(() => bindingAttachment.Session.ConfigureSessionInfoObservation(null));
            var foreign = bindingAttachment with { };
            Refused(() => bindingOwner.ConfigureCompactionObservationForBinding(foreign, null));
            Refused(() => bindingOwner.ConfigureSessionInfoObservationForBinding(foreign, null));
            var marker = new AsyncLocal<object?> { Value = new object() };
            var bindingThread = Environment.CurrentManagedThreadId;
            var outside = Gate();
            try
            {
                // A promise has no queued delegate for this joining thread to inline.
                // The unsafe worker starts without the binding callback's ExecutionContext.
                if (!ThreadPool.UnsafeQueueUserWorkItem(_ =>
                {
                    try
                    {
                        var context = $"generation={bindingAttachment.Generation};bindingThread={bindingThread};probeThread={Environment.CurrentManagedThreadId};pool={Thread.CurrentThread.IsThreadPoolThread};markerPresent={marker.Value is not null}";
                        if (marker.Value is not null) throw new IOException("Negative binding probe inherited callback context. " + context);
                        Refused(() => bindingOwner.ConfigureCompactionObservationForBinding(bindingAttachment, null), context + ";operation=compaction");
                        Refused(() => bindingOwner.ConfigureSessionInfoObservationForBinding(bindingAttachment, null), context + ";operation=session-info");
                        Refused(() => bindingOwner.CaptureToolCatalogRegistryForBinding(bindingAttachment), context + ";operation=catalog");
                        outside.TrySetResult();
                    }
                    catch (Exception error) { outside.TrySetException(error); }
                }, null)) outside.TrySetException(new IOException("Negative binding probe could not be queued."));
                outside.Task.GetAwaiter().GetResult();
            }
            finally { marker.Value = null; }
            bindingOwner.ConfigureCompactionObservationForBinding(bindingAttachment, _ => ValueTask.CompletedTask);
            bindingOwner.ConfigureSessionInfoObservationForBinding(bindingAttachment, _ =>
            { observed.Add(bindingAttachment.Generation); return ValueTask.CompletedTask; });
            bound.Add(bindingAttachment.Generation);
        };
        await using var initial = await fixture.Lifecycle.CreateAsync(fixture.A, fixture.Header("a"), Model);
        await using var owner = await fixture.Lifecycle.AttachAsync(initial);
        Check(bound.SequenceEqual(new long[] { 1 }) && owner.Current.Session.CaptureToolCatalogRegistry().InvocationOwnerGeneration == 1);
        var previous = owner.Current;
        await owner.SetSessionNameAsync(previous, "initial observer");
        Check(observed.SequenceEqual(new long[] { 1 }));
        var notified = false;
        owner.AttachmentChanged = replacement =>
        { Check(bound.SequenceEqual(new long[] { 1, 2 }) && replacement.Current.Session.CaptureToolCatalogRegistry().InvocationOwnerGeneration == 2); notified = true; return ValueTask.CompletedTask; };
        await owner.CreateAsync(owner.Current, new(AgentSessionCreationKind.New));
        Check(notified && bound.SequenceEqual(new long[] { 1, 2 }));
        Refused(() => owner.ConfigureCompactionObservationForBinding(previous, null));
        Refused(() => owner.ConfigureSessionInfoObservationForBinding(previous, null));
        await owner.SetSessionNameAsync(owner.Current, "replacement observer");
        Check(observed.SequenceEqual(new long[] { 1, 2 }));
    }
    private static void Refused(Action action, string? context = null)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new IOException("Observation binding unexpectedly bypassed attachment or reservation authority. " + context);
    }
    private static async Task<Exception> Failure(Task original)
    { try { await original; } catch (Exception error) { return error; } throw new IOException("Expected joined binding failure."); }
    private static async Task FailedBinding()
    {
        foreach (var failInitial in new[] { true, false })
        {
            var fixture = new Fixture(bindInvocationOwner: true); var stopA = Gate(); var stopB = Gate(); var release = Gate();
            var original = new IOException("actual binding failure"); var failGeneration = failInitial ? 1 : 2;
            fixture.Bind = (bindingOwner, bindingAttachment) =>
            {
                CheckReservedBinding(bindingOwner, bindingAttachment);
                if (bindingAttachment.Generation != failGeneration) return;
                bindingOwner.RegisterOwnedResource(bindingAttachment, transaction => Task.CompletedTask,
                    () => { stopA.TrySetResult(); return release.Task; });
                bindingOwner.RegisterOwnedResource(bindingAttachment, transaction => Task.CompletedTask,
                    () => { stopB.TrySetResult(); return Task.CompletedTask; });
                throw original;
            };
            await using var initial = await fixture.Lifecycle.CreateAsync(fixture.A, fixture.Header("a"), Model);
            ReplaceableAgentSession? owner = null; var notified = false;
            try
            {
                if (!failInitial)
                { owner = await fixture.Lifecycle.AttachAsync(initial); owner.AttachmentChanged = value => { notified = true; return ValueTask.CompletedTask; }; }
                Task work = failInitial ? fixture.Lifecycle.AttachAsync(initial) : owner!.CreateAsync(owner!.Current, new(AgentSessionCreationKind.New));
                Exception? assertion = null;
                try
                {
                    await Task.WhenAll(stopA.Task, stopB.Task); Check(!work.IsCompleted && !notified);
                    if (owner is not null) Check(owner.Current.Generation == 2 && !owner.Current.Session.Snapshot.IsRetired);
                }
                catch (Exception error) { assertion = error; }
                finally { release.TrySetResult(); }
                var bindingFailure = await Failure(work);
                if (assertion is not null) throw new AggregateException(assertion, bindingFailure);
                if (failInitial) Check(ReferenceEquals(bindingFailure, original) && initial.Snapshot.IsRetired && fixture.Backend.ActiveWriterCount == 0);
                else Check(bindingFailure is AgentSessionReplacementNotificationException notification && ReferenceEquals(notification.InnerException, original) &&
                    ReferenceEquals(notification.Replacement.Current, owner!.Current) && owner!.Current.Session.Snapshot.IsRetired && !notified);
            }
            finally { release.TrySetResult(); if (owner is not null) await owner.DisposeAsync(); }
            Check(fixture.Backend.ActiveWriterCount == 0);
        }
    }

    private static async Task FailedBindingFaultIdentity()
    {
        var fixture = new Fixture(bindInvocationOwner: true);
        var bindingError = new IOException("binding original");
        var cleanupError = new IOException("cleanup original");
        var bodyEntered = Gate(); var releaseBody = Gate(); var bodyCount = 0;
        fixture.Bind = (bindingOwner, attachment) =>
        {
            bindingOwner.RegisterOwnedResource(attachment, async transaction =>
            {
                Interlocked.Increment(ref bodyCount); bodyEntered.TrySetResult();
                await releaseBody.Task; throw cleanupError;
            });
            throw bindingError;
        };
        await using var initial = await fixture.Lifecycle.CreateAsync(fixture.A, fixture.Header("a"), Model);
        var attaching = fixture.Lifecycle.AttachAsync(initial);
        Exception? assertion = null;
        try { await bodyEntered.Task; Check(!attaching.IsCompleted); }
        catch (Exception error) { assertion = error; }
        finally { releaseBody.TrySetResult(); }
        var failure = await Failure(attaching);
        if (assertion is not null) throw new AggregateException(assertion, failure);
        Check(failure is AggregateException);
        var leaves = ((AggregateException)failure).Flatten().InnerExceptions;
        Check(leaves.Count == 2 && leaves.Count(error => ReferenceEquals(error, bindingError)) == 1 &&
            leaves.Count(error => ReferenceEquals(error, cleanupError)) == 1 && bodyCount == 1 &&
            fixture.Released == 1 && fixture.Backend.ActiveWriterCount == 0 && initial.Snapshot.IsRetired);
    }
    private static async Task Veto()
    {
        var fixture = new Fixture();
        await using var initial = await fixture.Lifecycle.CreateAsync(fixture.A, fixture.Header("a"), Model);
        await fixture.Seed(); await using var owner = fixture.Lifecycle.Attach(initial); var previous = owner.Current;
        var vetoed = await owner.SwitchAsync(previous, new(fixture.B), beforeSwitch: (old, target, token) => ValueTask.FromResult(false));
        Check(vetoed is null && ReferenceEquals(owner.Current, previous) && fixture.Released == 1);
        var accepted = await owner.SwitchAsync(previous, new(fixture.B));
        Check(accepted is not null && accepted.Current.Generation == 2 && fixture.Generations.SequenceEqual(new long[] { 1, 2, 2 }));
        await owner.DisposeAsync(); Check(fixture.Released == 3 && fixture.Backend.ActiveWriterCount == 0);
    }

    private sealed class Fixture
    {
        internal readonly SessionStorageBackend Backend;
        internal readonly PersistentSessionLifecycle Lifecycle;
        internal readonly List<long> Generations = [];
        internal string A => Path.Combine(Backend.Directory, "a.jsonl");
        internal string B => Path.Combine(Backend.Directory, "b.jsonl");
        internal int Released;
        internal Func<long, Task>? BeforeAcquire;
        internal Action<ReplaceableAgentSession, AgentSessionAttachment>? Bind;
        private int ids;
        internal Fixture(bool bindInvocationOwner = false)
        {
            Backend = new(Path.Combine(Path.GetTempPath(), "runtime-generation-" + Guid.NewGuid().ToString("N")), SessionStorageMode.InMemory);
            var registry = new SessionRuntimeRegistry([new(Model, new UnusedTransport())], [], new Policy(),
                new SessionRuntimeRegistryOptions { BindNestedCallsToSessionOwner = bindInvocationOwner });
            Lifecycle = new(registry, () => 0, () => "entry-" + ++ids, backend: Backend,
                nextSessionId: () => "session-" + ++ids, runtimeForAttachment: async (cwd, generation, token) =>
                {
                    Check(cwd == Backend.Directory); Generations.Add(generation);
                    if (BeforeAcquire is { } before) await before(generation);
                    token.ThrowIfCancellationRequested();
                    return new SessionRuntimeLease(registry, new Release(this), Bind);
                });
        }
        internal SessionEntry Header(string id) => new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
        { type = "session", version = 3, id, timestamp = "2026-10-05T00:00:00.000Z", cwd = Backend.Directory }));
        internal async Task Seed()
        { await using var store = await SessionLogStore.CreateNewAsync(B, Header("b"), new(StorageFactory: Backend)); }
    }
    private sealed class Release(Fixture fixture) : IAsyncDisposable
    { public ValueTask DisposeAsync() { Interlocked.Increment(ref fixture.Released); return ValueTask.CompletedTask; } }
    private sealed class Policy : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(new ToolActionAuthorization(false)); }
    private sealed class UnusedTransport : IChatTransport
    {
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        { await Task.FromException(new InvalidOperationException("Generation fixture must not invoke a provider.")); yield break; }
    }
}
