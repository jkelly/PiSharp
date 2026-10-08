using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Sessions.Serialization;

internal static class OwnedResourceRetirementTests
{
    private static readonly ModelDescriptor Model = new("resource-retirement", "openai-responses", "fixture");
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("owned-resource. held publication joins before shutdown attachment cancellation", Shutdown),
        ("owned-resource. queued normal close is claimed once by actual switch", QueuedSwitch),
        ("owned-resource. source veto and target preflight failure leave resources untouched", Veto),
        ("owned-resource. callbacks reject self-wait and escaped transaction", Reentry),
        ("owned-resource. unawaited admitted publication and body failures both join", Faults),
        ("owned-resource. stale attachment cannot register or borrow transaction", Stale),
        ("owned-resource. committed attachment lifecycle permits metadata-only registration", AfterReplacementRegistration),
        ("owned-resource. originating input callback withdraws under actual switch reservation", InputSwitch),
        ("owned-resource. normal close initiates stop before active provider idle", ActiveNormal),
        ("owned-resource. shutdown initiates stop before cancellation-resistant provider idle", ActiveShutdown),
        ("owned-resource. stop reentry rejects and stop plus body faults remain original", StopFaults)
    ];
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Owned resource assertion failed."); }
    private static async Task Shutdown()
    {
        using var fixture = new Fixture(); var owner = await fixture.Owner();
        var attachment = owner.Current; var entered = Gate(); var release = Gate(); var committed = false;
        var lease = owner.RegisterOwnedResource(attachment, async tx =>
        {
            await tx.PrepareAndPublishCatalogAsync(async (registry, active, token) =>
            {
                Check(!token.CanBeCanceled && !attachment.LifetimeToken.IsCancellationRequested);
                entered.TrySetResult(); await release.Task;
                return new(registry.WithToolCatalog(registry.RegisteredTools, registry.PreparedToolHooks), active, () => committed = true);
            });
            Check(committed && !attachment.LifetimeToken.IsCancellationRequested);
        });
        var stopping = owner.DisposeAsync().AsTask();
        try { await entered.Task; Check(!stopping.IsCompleted && !attachment.LifetimeToken.IsCancellationRequested); }
        finally { release.TrySetResult(); await JoinAll(stopping); }
        Check(committed && attachment.LifetimeToken.IsCancellationRequested && ReferenceEquals(lease.CloseAsync(), lease.CloseAsync()));
    }
    private static async Task QueuedSwitch()
    {
        using var fixture = new Fixture(); await using var owner = await fixture.Owner();
        var attachment = owner.Current; var entered = Gate(); var release = Gate(); var stopEntered = Gate(); var stopRelease = Gate(); var calls = 0; var stops = 0;
        owner.BeforeRetirement = async (_, _, _) => { entered.TrySetResult(); await release.Task; };
        var lease = owner.RegisterOwnedResource(attachment, async tx =>
        {
            calls++;
            await tx.PrepareAndPublishCatalogAsync((registry, names, _) => ValueTask.FromResult(
                new PreparedSessionToolCatalog(registry.WithToolCatalog(registry.RegisteredTools, registry.PreparedToolHooks), names, () => { })));
        }, () => { stops++; stopEntered.TrySetResult(); return stopRelease.Task; });
        var switching = owner.SwitchAsync(attachment, new(fixture.B)); Task? closing = null;
        try { await entered.Task; closing = lease.CloseAsync(); await stopEntered.Task; Check(!closing.IsCompleted && calls == 0 && stops == 1); }
        finally { stopRelease.TrySetResult(); release.TrySetResult(); await JoinAll(switching, closing ?? lease.CloseAsync()); }
        Check(calls == 1 && stops == 1 && owner.Current.Generation == 2 && ReferenceEquals(closing, lease.CloseAsync()));
    }
    private static async Task Veto()
    {
        using var fixture = new Fixture(); await using var owner = await fixture.Owner(); var calls = 0; var stops = 0;
        owner.RegisterOwnedResource(owner.Current, _ => { calls++; return Task.CompletedTask; }, () => { stops++; return Task.CompletedTask; });
        owner.BeforeReplacement = (_, _, _) => ValueTask.FromResult(false);
        Check(await owner.SwitchAsync(owner.Current, new(fixture.B)) is null && calls == 0 && stops == 0);
        owner.BeforeReplacement = null;
        owner.ValidateTargetAttachment = (_, _, _) => ValueTask.FromException(new InvalidOperationException("target-original"));
        try { await owner.SwitchAsync(owner.Current, new(fixture.B)); throw new Exception("accepted target failure"); }
        catch (InvalidOperationException error) { Check(error.Message == "target-original"); }
        Check(calls == 0 && stops == 0 && owner.Current.Generation == 1);
    }
    private static async Task Reentry()
    {
        using var fixture = new Fixture(); await using var owner = await fixture.Owner();
        var entered = Gate(); var release = Gate();
        ReplaceableAgentSession.OwnedResourceRetirementTransaction? escaped = null;
        ReplaceableAgentSession.OwnedResourceLease? lease = null;
        lease = owner.RegisterOwnedResource(owner.Current, async tx =>
        {
            escaped = tx;
            Reject(() => lease!.CloseAsync()); Reject(() => owner.DisposeAsync());
            Reject(() => owner.RegisterOwnedResource(owner.Current, _ => Task.CompletedTask));
            entered.TrySetResult(); await release.Task;
            await tx.PrepareAndPublishCatalogAsync((registry, names, _) =>
            {
                Reject(() => tx.PrepareAndPublishCatalogAsync((_, _, _) => throw new Exception("recursive callback invoked")));
                Reject(() => lease!.CloseAsync());
                return ValueTask.FromResult(new PreparedSessionToolCatalog(
                    registry.WithToolCatalog(registry.RegisteredTools, registry.PreparedToolHooks), names, () => { }));
            });
        });
        var original = lease.CloseAsync();
        try
        {
            await entered.Task;
            Reject(() => escaped!.PrepareAndPublishCatalogAsync((_, _, _) => throw new Exception("foreign callback invoked")));
            Check(!original.IsCompleted);
        }
        finally { release.TrySetResult(); await original; }
        Check(ReferenceEquals(lease.CloseAsync(), lease.CloseAsync()));
        Reject(() => escaped!.PrepareAndPublishCatalogAsync((registry, names, _) => ValueTask.FromResult(
            new PreparedSessionToolCatalog(registry.WithToolCatalog(registry.RegisteredTools, null), names, () => { }))));
    }
    private static async Task Faults()
    {
        using var fixture = new Fixture(); var owner = await fixture.Owner(); var entered = Gate(); var release = Gate();
        var publication = new IOException("publication-original"); var body = new IOException("body-original");
        var lease = owner.RegisterOwnedResource(owner.Current, tx =>
        {
            _ = tx.PrepareAndPublishCatalogAsync(async (_, _, _) => { entered.TrySetResult(); await release.Task; throw publication; });
            return Task.FromException(body);
        });
        var closing = lease.CloseAsync(); Exception? first = null;
        var failures = new List<Exception>();
        try
        {
            try { await entered.Task; Check(!closing.IsCompleted); }
            finally { release.TrySetResult(); try { await closing; } catch (Exception error) { first = error; } }
            Check(first is AggregateException aggregate && aggregate.InnerExceptions.Contains(body) && aggregate.InnerExceptions.Contains(publication));
            try { await lease.CloseAsync(); } catch (Exception error) { Check(ReferenceEquals(first, error)); }
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            release.TrySetResult();
            try { await closing; } catch (Exception error) { if (!Expected(error)) failures.Add(error); }
            try { await owner.DisposeAsync(); failures.Add(new Exception("owner hid original resource failure")); }
            catch (Exception error) { if (!Expected(error)) failures.Add(error); }
        }
        if (failures.Count != 0) throw new AggregateException(failures);
        bool Expected(Exception error) => error is AggregateException aggregate &&
            aggregate.Flatten().InnerExceptions.Count >= 2 && aggregate.Flatten().InnerExceptions.All(leaf =>
                ReferenceEquals(leaf, body) || ReferenceEquals(leaf, publication));
    }
    private static async Task Stale()
    {
        using var fixture = new Fixture(); await using var owner = await fixture.Owner(); var old = owner.Current;
        await owner.SwitchAsync(old, new(fixture.B));
        Reject(() => owner.RegisterOwnedResource(old, _ => Task.CompletedTask));
        for (var index = 0; index < ReplaceableAgentSession.MaximumOwnedResources; index++)
            owner.RegisterOwnedResource(owner.Current, _ => Task.CompletedTask);
        Reject(() => owner.RegisterOwnedResource(owner.Current, _ => Task.CompletedTask));
    }
    private static async Task AfterReplacementRegistration()
    {
        using var fixture = new Fixture(); var owner = await fixture.Owner(); var closed = 0;
        owner.AfterReplacement = replacement =>
        {
            owner.RegisterOwnedResource(replacement.Current, _ => { closed++; return Task.CompletedTask; });
            return ValueTask.CompletedTask;
        };
        try { await owner.SwitchAsync(owner.Current, new(fixture.B)); Check(closed == 0); }
        finally { await owner.DisposeAsync(); }
        Check(closed == 1);
    }
    private static async Task InputSwitch()
    {
        using var fixture = new Fixture(); await using var owner = await fixture.Owner(); var old = owner.Current; var withdrawals = 0;
        owner.RegisterOwnedResource(old, async tx =>
        {
            await tx.PrepareAndPublishCatalogAsync((registry, names, _) => ValueTask.FromResult(
                new PreparedSessionToolCatalog(registry.WithToolCatalog(registry.RegisteredTools, registry.PreparedToolHooks), names, () => withdrawals++)));
        });
        await old.Session.SubmitInputAsync(new("/switch", PromptInputSource.Rpc), new Admission(async token =>
        { await owner.SwitchAsync(old, new(fixture.B), cancellationToken: token); }));
        await old.Session.WaitForIdleAsync();
        Check(withdrawals == 1 && owner.Current.Generation == 2 && old.Session.Snapshot.IsRetired);
    }
    private static Task ActiveNormal() => ActiveStop(shutdown: false);
    private static Task ActiveShutdown() => ActiveStop(shutdown: true);
    private static async Task ActiveStop(bool shutdown)
    {
        var provider = new HoldingTransport(); using var fixture = new Fixture(provider); var owner = await fixture.Owner();
        var attachment = owner.Current; var stopRelease = Gate(); var stopEntered = Gate(); var stops = 0; var withdrew = false;
        var lease = owner.RegisterOwnedResource(attachment, async tx =>
        {
            Check(stops == 1 && stopRelease.Task.IsCompleted && !attachment.LifetimeToken.IsCancellationRequested);
            await tx.PrepareAndPublishCatalogAsync((registry, names, _) => ValueTask.FromResult(
                new PreparedSessionToolCatalog(registry.WithToolCatalog(registry.RegisteredTools, registry.PreparedToolHooks), names,
                    () => withdrew = true)));
        }, () =>
        {
            stops++; provider.Stop.TrySetResult(); stopEntered.TrySetResult(); return stopRelease.Task;
        });
        var run = attachment.Session.SubmitInputAsync(new("hello", PromptInputSource.Rpc)); Task? close = null;
        var errors = new List<Exception>();
        try
        {
            await provider.Entered.Task;
            close = shutdown ? owner.DisposeAsync().AsTask() : lease.CloseAsync();
            await stopEntered.Task;
            Check(!close.IsCompleted && stops == 1 && !withdrew && !attachment.LifetimeToken.IsCancellationRequested);
        }
        catch (Exception error) { errors.Add(error); }
        finally
        {
            provider.Stop.TrySetResult(); stopRelease.TrySetResult();
            try { await run; } catch (OperationCanceledException) when (shutdown) { } catch (Exception error) { errors.Add(error); }
            if (close is not null) try { await close; } catch (Exception error) { errors.Add(error); }
            try { await owner.DisposeAsync(); } catch (Exception error) { errors.Add(error); }
        }
        Check(withdrew && stops == 1);
        if (errors.Count != 0) throw new AggregateException(errors);
    }
    private static async Task StopFaults()
    {
        using var fixture = new Fixture(); var owner = await fixture.Owner(); var entered = Gate(); var release = Gate();
        var stopFault = new IOException("stop-original"); var bodyFault = new IOException("body-original");
        ReplaceableAgentSession.OwnedResourceLease? lease = null;
        lease = owner.RegisterOwnedResource(owner.Current, _ => Task.FromException(bodyFault), async () =>
        {
            Reject(() => lease!.CloseAsync()); Reject(() => owner.DisposeAsync());
            entered.TrySetResult(); await release.Task; Reject(() => lease!.CloseAsync()); throw stopFault;
        });
        var original = lease.CloseAsync(); var errors = new List<Exception>(); Exception? first = null;
        try { await entered.Task; Check(!original.IsCompleted && ReferenceEquals(original, lease.CloseAsync())); }
        catch (Exception error) { errors.Add(error); }
        finally
        {
            release.TrySetResult();
            try { await original; } catch (Exception error) { first = error; if (!Expected(error)) errors.Add(error); }
            try { await owner.DisposeAsync(); } catch (Exception error) { if (!Expected(error)) errors.Add(error); }
        }
        Check(first is not null && ReferenceEquals(original, lease.CloseAsync()));
        try { await lease.CloseAsync(); } catch (Exception error) { Check(ReferenceEquals(first, error)); }
        if (errors.Count != 0) throw new AggregateException(errors);
        bool Expected(Exception error) => error is AggregateException aggregate && aggregate.Flatten().InnerExceptions.Count >= 2 &&
            aggregate.Flatten().InnerExceptions.All(leaf => ReferenceEquals(leaf, stopFault) || ReferenceEquals(leaf, bodyFault));
    }
    private static void Reject(Action action)
    { var rejected = false; try { action(); } catch (InvalidOperationException) { rejected = true; } Check(rejected); }
    private static async Task JoinAll(params Task[] originals)
    {
        var errors = new List<Exception>(); foreach (var task in originals) try { await task; } catch (Exception error) { errors.Add(error); }
        if (errors.Count > 0) throw new AggregateException(errors);
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "PiSharp-owned-resource-" + Guid.NewGuid().ToString("N"));
        public string A => Path.Combine(Root, "a.jsonl"); public string B => Path.Combine(Root, "b.jsonl");
        private readonly SessionRuntimeRegistry registry;
        private int ids;
        public Fixture(IChatTransport? transport = null)
        { Directory.CreateDirectory(Root); registry = new([new(Model, transport ?? new NoTransport())], [], new Deny()); }
        public async Task<ReplaceableAgentSession> Owner()
        {
            await using (var target = await Create(B)) { }
            var initial = await Create(A);
            return new(initial, (request, token) => PersistentAgentSession.OpenWithRegistryAsync(request.Path, registry,
                () => 1, () => "open-" + Interlocked.Increment(ref ids), fallbackModel: Model, cancellationToken: token));
        }
        private Task<PersistentAgentSession> Create(string path)
        {
            var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3,
                id = Path.GetFileNameWithoutExtension(path), timestamp = "2026-10-05T00:00:00.000Z", cwd = Root }));
            return PersistentAgentSession.CreateAsync(path, header, registry, Model, () => 1, () => "entry-" + Interlocked.Increment(ref ids));
        }
        public void Dispose()
        {
            var target = Path.GetFullPath(Root); var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (Path.GetDirectoryName(target) != temp || !Path.GetFileName(target).StartsWith("PiSharp-owned-resource-", StringComparison.Ordinal))
                throw new InvalidOperationException("Invalid owned fixture cleanup path.");
            Directory.Delete(target, recursive: true);
        }
    }
    private sealed class NoTransport : IChatTransport
    {
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        { await Task.FromException(new InvalidOperationException("Resource fixtures must not call providers.")); yield break; }
    }
    private sealed class HoldingTransport : IChatTransport
    {
        public readonly TaskCompletionSource Entered = Gate(), Stop = Gate();
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            Entered.TrySetResult(); await Stop.Task; // Deliberately ignores cancellation until transferred owner stop.
            var message = new AssistantMessage(Model.Api, Model.Provider, Model.Id, 1, [new TextContent("done")], TokenUsage.Zero, StopReason.Stop);
            yield return new StreamStarted(message with { Content = [], StopReason = StopReason.Pending });
            yield return new TextStarted(0, new("")); yield return new TextEnded(0, "done"); yield return new StreamDone(StopReason.Stop, message);
        }
    }
    private sealed class Admission(Func<CancellationToken, Task> callback) : IPromptInputAdmission
    {
        public async ValueTask<PromptInputDecision> ReduceAsync(PromptInput input, CancellationToken token)
        { await callback(token); return new(PromptInputAction.Handled); }
    }
    private sealed class Deny : IToolActionPolicy
    {
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
            => ValueTask.FromResult(new ToolActionAuthorization(false));
    }
}
