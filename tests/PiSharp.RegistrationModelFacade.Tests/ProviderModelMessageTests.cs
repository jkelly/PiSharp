using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Agent;
using PiSharp.Extensions.Runtime;

internal static class ProviderModelMessageTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() => [
        ("provider staged catalog replace unregister builtin restoration", Catalog),
        ("provider actual Agent pipeline consumes admitted extension transport", AgentPipeline),
        ("provider owner close joins original move and iterator disposal", HeldClose),
        ("provider original multi faults plus disposal and faulted OCE remain faults", Faults),
        ("provider matching owned cancellation keeps original status and joins disposal", Cancellation),
        ("ignored model message actions remain real command owner work", IgnoredActions),
        ("action validation stale views and full host original fault inventory", ActionFaults),
        ("saved native context rejected in later same owner callback before host effects", ExactCallback)
    ];
    private static readonly ModelDescriptor Model = new("model", "test-api", "test-provider");
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static ExtensionProviderDefinition Definition(ExtensionProviderStreamCallback stream, string id = "model")
    {
        var model = Model with { Id = id };
        return new(model.Provider, [new(model, JsonData.Parse("{\"id\":\"" + id + "\",\"api\":\"test-api\",\"provider\":\"test-provider\"}"))], stream);
    }
    private sealed class Configuration(ExtensionProviderDefinition admitted) : IExtensionProviderConfigurationAdapter
    {
        public int Calls;
        public ExtensionProviderDefinition Resolve(string name, JsonData configuration) { Calls++; return admitted with { Name = name }; }
    }
    private static async IAsyncEnumerable<StreamEvent> Completed(ExtensionProviderStreamRequest request, IExtensionContext? context,
        [EnumeratorCancellation] CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var message = new AssistantMessage(request.Model.Api, request.Model.Provider, request.Model.Id, 1,
            [new TextContent("actual-provider")], TokenUsage.Zero, StopReason.Stop);
        yield return new StreamDone(StopReason.Stop, message); await Task.CompletedTask;
    }
    private static async Task<Exception?> Observe(string name, Task original)
    {
        Exception? caught = null;
        try { await original; } catch (Exception error) { caught = error; }
        finally { Audit.Record(name, original, caught); }
        return caught;
    }
    private static async Task<RegistrationScope> Activate(ExtensionRegistry registry, string id, Func<IExtensionRegistry, ValueTask> initialize)
    {
        var original = registry.ActivateAsync(id, new Extension(initialize)); Exception? caught = null;
        try { return await original; } catch (Exception error) { caught = error; throw; }
        finally { Audit.Record("provider-activate-" + id, original, caught); }
    }
    private static async Task SignalOrOriginal(Task signal, Task original)
    {
        var winner = await Task.WhenAny(signal, original).WaitAsync(TimeSpan.FromSeconds(5));
        await winner; Check(signal.IsCompletedSuccessfully, "Original settled before required control signal.");
    }
    private static async Task Drain(IAsyncEnumerable<StreamEvent> frames, CancellationToken token = default)
    { await foreach (var _ in frames.WithCancellation(token)) { } }
    private static void Throw(Exception? error) { if (error is not null) ExceptionDispatchInfo.Capture(error).Throw(); }
    private static async Task Catalog()
    {
        await using var registry = new ExtensionRegistry(); await using var foreign = new ExtensionRegistry();
        var original = Definition(Completed); var config = new Configuration(original);
        using var host = new ExtensionProviderRegistrationHost(registry, [original], config);
        IExtensionProviderRegistrationFacade? api = null;
        var scope = await Activate(registry, "catalog", owner => {
            api = host.Bind(owner); api.RegisterProvider(Definition(Completed, "override"));
            Check(host.CaptureModels().Single().Model == Model, "Staged provider leaked into catalog."); return ValueTask.CompletedTask;
        });
        host.CommitOwnerProviders(scope);
        Check(host.CaptureModels().Single().Model.Id == "override", "Committed replacement catalog missing.");
        api!.RegisterProvider(Definition(Completed, "replacement"));
        Check(host.CaptureModels().Single().Model.Id == "replacement", "Active replacement did not take effect.");
        api!.UnregisterProvider(Model.Provider); api.UnregisterProvider(Model.Provider);
        Check(host.CaptureModels().Single().Model == Model, "Unregister did not restore actual admitted builtin.");
        api!.RegisterProvider(Model.Provider, JsonData.EmptyObject); Check(config.Calls == 1, "Config overload did not call admitted adapter.");
        var other = await Activate(foreign, "foreign", _ => ValueTask.CompletedTask);
        try { host.Bind(other); throw new InvalidOperationException("Foreign provider owner accepted."); } catch (ArgumentException) { }
        var otherClose = other.DisposeAsync().AsTask(); Throw(await Observe("provider-foreign-close", otherClose));
        var close = scope.DisposeAsync().AsTask(); Throw(await Observe("provider-catalog-close", close));
        Check(host.CaptureModels().Single().Model == Model, "Owner close did not restore builtin.");
        try { api!.RegisterProvider(original); throw new InvalidOperationException("Closed facade accepted."); } catch (ObjectDisposedException) { }
    }
    private sealed class Sink : IAgentEventSink
    { public int Messages; public ValueTask EmitAsync(AgentEvent observation, CancellationToken token) { if (observation is AssistantMessageEnded) Messages++; return ValueTask.CompletedTask; } }
    private static async Task AgentPipeline()
    {
        await using var registry = new ExtensionRegistry();
        using var host = new ExtensionProviderRegistrationHost(registry, [], new Configuration(Definition(Completed)));
        var callbacks = 0;
        var scope = await Activate(registry, "agent-provider", owner => {
            host.Bind(owner).RegisterProvider(Definition((request, context, token) => {
                Check(context is not null && context.OwnerId == owner.OwnerId && context.OwnerGeneration == owner.OwnerGeneration, "Actual provider context missing.");
                callbacks++; return Completed(request, context, token);
            })); return ValueTask.CompletedTask;
        }); host.CommitOwnerProviders(scope);
        var sink = new Sink();
        await using var agent = new PiSharp.Agent.Agent(new(Model, new ExtensionProviderTransport(host), []), () => 1, sink);
        var original = agent.PromptAsync(new TranscriptEntry("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"ask\",\"timestamp\":1}")));
        Throw(await Observe("actual-agent-provider-prompt", original));
        Check(callbacks == 1 && sink.Messages == 1, "Existing Agent did not consume genuine registered provider stream.");
        var close = scope.DisposeAsync().AsTask(); Throw(await Observe("agent-provider-close", close));
        await SecondReadGuard();
    }
    private sealed class ReentryStream(RegistrationScope scope) : IAsyncEnumerable<StreamEvent>, IAsyncEnumerator<StreamEvent>
    {
        internal int Reads, Refused; internal Task? UnexpectedClose; internal Task<RegistrationQuiescenceLease>? UnexpectedPause;
        private void Guard()
        {
            try { UnexpectedPause ??= scope.QuiesceAsync().AsTask(); }
            catch (ExtensionRegistrationException error) when (error.Failure == ExtensionRegistrationFailure.ReentrantDisposal) { Refused++; }
            try { UnexpectedClose ??= scope.DisposeAsync().AsTask(); }
            catch (ExtensionRegistrationException error) when (error.Failure == ExtensionRegistrationFailure.ReentrantDisposal) { Refused++; }
        }
        public IAsyncEnumerator<StreamEvent> GetAsyncEnumerator(CancellationToken token = default) { Guard(); return this; }
        public ValueTask<bool> MoveNextAsync() { Reads++; if (Reads == 2) Guard(); return ValueTask.FromResult(Reads == 1); }
        public StreamEvent Current { get { Guard(); return new TextDelta(0, "guarded"); } }
        public ValueTask DisposeAsync() { Guard(); return ValueTask.CompletedTask; }
    }
    private static async Task SecondReadGuard()
    {
        await using var registry = new ExtensionRegistry();
        using var host = new ExtensionProviderRegistrationHost(registry, [], new Configuration(Definition(Completed)));
        ReentryStream? stream = null;
        var scope = await Activate(registry, "second-read-guard", owner => {
            stream = new(owner as RegistrationScope ?? throw new InvalidOperationException("The control requires its genuine native initializer scope."));
            host.Bind(owner).RegisterProvider(Definition((_, _, _) => stream!)); return ValueTask.CompletedTask;
        }); host.CommitOwnerProviders(scope);
        var original = Drain(host.StreamAsync(new(Model, []))); Exception? primary = null;
        try { Throw(await Observe("second-read-guard-drain-original", original)); }
        catch (Exception error) { primary = error; }
        finally
        {
            if (stream!.UnexpectedPause is { } pause) { await Observe("unexpected-self-pause-original", pause); if (pause.IsCompletedSuccessfully) pause.Result.Dispose(); }
            var close = stream.UnexpectedClose ?? scope.DisposeAsync().AsTask(); var closeFault = await Observe("second-read-owner-close-original", close);
            if (closeFault is not null) primary = primary is null ? closeFault : new AggregateException(primary, closeFault);
        }
        Throw(primary); Check(stream!.Reads == 2 && stream.Refused == 8 && stream.UnexpectedPause is null && stream.UnexpectedClose is null,
            "Every factory/current/dispose and second actual MoveNext requires its fresh owner callback frame.");
    }
    private sealed class HeldStream : IAsyncEnumerable<StreamEvent>, IAsyncEnumerator<StreamEvent>
    {
        internal readonly TaskCompletionSource<bool> Next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Disposal = Signal(), Entered = Signal(), Stopped = Signal(), DisposeEntered = Signal();
        internal CancellationToken Owned; private CancellationTokenRegistration registration; internal int Created, Consumed, Disposed;
        public StreamEvent Current => new TextDelta(0, "held");
        public IAsyncEnumerator<StreamEvent> GetAsyncEnumerator(CancellationToken token = default)
        { Created++; Owned = token; registration = token.Register(() => Stopped.TrySetResult()); return this; }
        public ValueTask<bool> MoveNextAsync() { Consumed++; Entered.TrySetResult(); return new(Next.Task); }
        public ValueTask DisposeAsync() { Disposed++; DisposeEntered.TrySetResult(); return new(DisposeCore()); }
        private async Task DisposeCore() { try { await Disposal.Task; } finally { registration.Dispose(); } }
        internal void Release() { Next.TrySetResult(false); Disposal.TrySetResult(); }
    }
    private static async Task HeldClose()
    {
        await using var registry = new ExtensionRegistry(); var held = new HeldStream();
        using var host = new ExtensionProviderRegistrationHost(registry, [], new Configuration(Definition(Completed)));
        IExtensionProviderRegistrationFacade? api = null;
        var scope = await Activate(registry, "provider-held", owner => {
            api = host.Bind(owner); api.RegisterProvider(Definition((_, context, _) => { Check(context?.OwnerGeneration == owner.OwnerGeneration, "Wrong actual stream context."); return held; })); return ValueTask.CompletedTask;
        }); host.CommitOwnerProviders(scope);
        var originalDrain = Drain(host.StreamAsync(new(Model, []))); Task? originalClose = null; Exception? primary = null;
        try
        {
            await SignalOrOriginal(held.Entered.Task, originalDrain);
            api!.RegisterProvider(Definition(Completed, "new-model"));
            Check(host.CaptureModels().Single().Model.Id == "new-model", "Held definition replacement not published.");
            originalClose = scope.DisposeAsync().AsTask(); await SignalOrOriginal(held.Stopped.Task, originalClose);
            Check(!originalClose.IsCompleted && !originalDrain.IsCompleted, "Owner close abandoned original MoveNext.");
            held.Next.TrySetResult(false); await SignalOrOriginal(held.DisposeEntered.Task, originalDrain);
            Check(!originalClose.IsCompleted && !originalDrain.IsCompleted && held.Disposed == 1, "Owner close skipped iterator disposal.");
        }
        catch (Exception error) { primary = error; }
        finally
        {
            held.Release(); var drainFault = await Observe("provider-held-drain", originalDrain);
            originalClose ??= scope.DisposeAsync().AsTask(); var closeFault = await Observe("provider-held-close", originalClose);
            await Observe("provider-held-original-next", held.Next.Task); await Observe("provider-held-original-disposal", held.Disposal.Task);
            if (drainFault is not null || closeFault is not null) primary = new AggregateException(new[] { primary, drainFault, closeFault }.OfType<Exception>());
        }
        Throw(primary); Check(held.Created == 1 && held.Consumed == 1 && host.CaptureModels().IsEmpty, "Stream originals or retired catalog mismatch.");
    }
    private static async Task Faults()
    {
        foreach (var faultedOce in new[] { false, true })
        {
            await using var registry = new ExtensionRegistry(); var held = new HeldStream();
            using var host = new ExtensionProviderRegistrationHost(registry, [], new Configuration(Definition(Completed)));
            var scope = await Activate(registry, faultedOce ? "fault-oce" : "fault-multi", owner => { host.Bind(owner).RegisterProvider(Definition((_, _, _) => held)); return ValueTask.CompletedTask; }); host.CommitOwnerProviders(scope);
            var first = faultedOce ? (Exception)new OperationCanceledException("faulted original") : new IOException("first");
            var shared = new IOException("shared"); var nested = new AggregateException(shared, shared); var disposalFault = new AggregateException();
            held.Next.SetException(faultedOce ? [first] : [first, nested]);
            if (faultedOce) held.Disposal.SetResult(); else held.Disposal.SetException(disposalFault);
            var original = Drain(host.StreamAsync(new(Model, []))); Exception? caught = null;
            try
            {
                caught = await Observe("provider-fault-drain", original);
                var callback = faultedOce ? caught as ExtensionProviderOriginalFaultException : (caught as AggregateException)?.InnerExceptions[0] as ExtensionProviderOriginalFaultException;
                Check(callback is not null && ReferenceEquals(callback.Original, held.Next.Task) && original.IsFaulted && !original.IsCanceled,
                    "Original provider inventory/status lost.");
                var graph = (AggregateException)callback!.Evidence;
                Check(ReferenceEquals(graph.InnerExceptions[0], first) && (faultedOce || graph.InnerExceptions.Count == 2 && ReferenceEquals(graph.InnerExceptions[1], nested)), "Full original callback graph lost.");
                if (!faultedOce) Check(caught is AggregateException { InnerExceptions.Count: 2 }, "Callback/disposal fault collision lost.");
            }
            finally
            {
                held.Release(); await Observe("provider-fault-original-next", held.Next.Task); await Observe("provider-fault-original-disposal", held.Disposal.Task);
                var close = scope.DisposeAsync().AsTask(); Throw(await Observe("provider-fault-close", close));
            }
        }
        await CurrentFault();
    }
    private sealed class CurrentFaultStream(Exception fault) : IAsyncEnumerable<StreamEvent>, IAsyncEnumerator<StreamEvent>
    {
        internal readonly Task<bool> OriginalMove = Task.FromResult(true);
        internal readonly Task OriginalDisposal = Task.CompletedTask;
        internal CancellationToken Owned; internal int Moves, Disposals;
        public IAsyncEnumerator<StreamEvent> GetAsyncEnumerator(CancellationToken token = default) { Owned = token; return this; }
        public ValueTask<bool> MoveNextAsync() { Moves++; return new(OriginalMove); }
        public StreamEvent Current => throw fault;
        public ValueTask DisposeAsync() { Disposals++; return new(OriginalDisposal); }
    }
    private static async Task CurrentFault()
    {
        await using var registry = new ExtensionRegistry();
        using var host = new ExtensionProviderRegistrationHost(registry, [], new Configuration(Definition(Completed)));
        var synchronousFault = new OperationCanceledException("synchronous Current fault with no cancellation");
        var stream = new CurrentFaultStream(synchronousFault);
        var scope = await Activate(registry, "current-fault", owner => { host.Bind(owner).RegisterProvider(Definition((_, _, _) => stream)); return ValueTask.CompletedTask; });
        host.CommitOwnerProviders(scope);
        var original = Drain(host.StreamAsync(new(Model, []))); Exception? caught = null;
        try
        {
            caught = await Observe("current-fault-drain-original", original);
            Check(caught is ExtensionProviderOriginalFaultException evidence && evidence.Original is null && ReferenceEquals(evidence.Evidence, synchronousFault) &&
                evidence.Message.Contains("current", StringComparison.Ordinal) && original.IsFaulted && !original.IsCanceled &&
                !stream.Owned.IsCancellationRequested && !synchronousFault.CancellationToken.IsCancellationRequested && stream.Moves == 1 && stream.Disposals == 1,
                "Synchronous Current OCE became cancellation or overwrote successful MoveNext evidence.");
        }
        finally
        {
            await Observe("current-fault-successful-move-original", stream.OriginalMove);
            await Observe("current-fault-disposal-original", stream.OriginalDisposal);
            var close = scope.DisposeAsync().AsTask(); Throw(await Observe("current-fault-owner-close-original", close));
        }
        Check(stream.OriginalMove.IsCompletedSuccessfully && stream.OriginalDisposal.IsCompletedSuccessfully, "Current fault changed preceding/succeeding original task status.");
    }
    private static async Task Cancellation()
    {
        await using var registry = new ExtensionRegistry(); using var stop = new CancellationTokenSource(); var held = new HeldStream();
        using var host = new ExtensionProviderRegistrationHost(registry, [], new Configuration(Definition(Completed)));
        var scope = await Activate(registry, "provider-cancel", owner => { host.Bind(owner).RegisterProvider(Definition((_, _, _) => held)); return ValueTask.CompletedTask; }); host.CommitOwnerProviders(scope);
        var original = Drain(host.StreamAsync(new(Model, []), stop.Token)); Exception? primary = null, canceled = null;
        try
        {
            await SignalOrOriginal(held.Entered.Task, original); stop.Cancel(); await SignalOrOriginal(held.Stopped.Task, original);
            Check(!original.IsCompleted, "Operation cancellation abandoned original move."); held.Next.TrySetCanceled(held.Owned); held.Disposal.TrySetResult();
        }
        catch (Exception error) { primary = error; }
        finally
        {
            held.Release(); canceled = await Observe("provider-owned-canceled-drain", original);
            await Observe("provider-owned-canceled-original", held.Next.Task); await Observe("provider-owned-canceled-disposal", held.Disposal.Task);
            var close = scope.DisposeAsync().AsTask(); Throw(await Observe("provider-cancel-close", close));
        }
        Throw(primary); Check(canceled is ExtensionProviderCanceledOriginalException evidence && ReferenceEquals(evidence.Original, held.Next.Task) && original.IsCanceled && held.Next.Task.IsCanceled,
            "Matching canceled original/status lost.");
    }
    private sealed class ActionHost : IExtensionRegistrationActionHost
    {
        internal readonly TaskCompletionSource Message = Signal(), Entered = Signal();
        internal ExtensionCustomMessage? Custom; internal ExtensionMessageOptions? CustomOptions;
        internal JsonData? User; internal ExtensionUserMessageOptions? UserOptions; internal int ModelCalls;
        public ValueTask<bool> SetModelAsync(ModelDescriptor model, CancellationToken token) { ModelCalls++; return ValueTask.FromResult(model == Model); }
        public ValueTask SendMessageAsync(ExtensionCustomMessage message, ExtensionMessageOptions? options, CancellationToken token)
        { Custom = message; CustomOptions = options; Entered.TrySetResult(); return new(Message.Task); }
        public ValueTask SendUserMessageAsync(JsonData content, ExtensionUserMessageOptions? options, CancellationToken token)
        { User = content; UserOptions = options; return ValueTask.CompletedTask; }
    }
    private static async Task IgnoredActions()
    {
        await using var registry = new ExtensionRegistry(); var effects = new ActionHost();
        using var providers = new ExtensionProviderRegistrationHost(registry, [Definition(Completed)], new Configuration(Definition(Completed)));
        var scope = await Activate(registry, "actions", owner => {
            owner.RegisterCommand(ExtensionRegistrationCommands.Create("actions", "actions", "actual host actions", providers, effects, (_, actions, context, _) => {
                Check(actions.OwnerGeneration == context.OwnerGeneration, "Action context generation differs.");
                _ = actions.SetModelAsync(Model); _ = actions.SendMessageAsync(new("custom", JsonData.Parse("\"text\""), false, JsonData.Parse("null")), new(false, ExtensionMessageDelivery.NextTurn));
                _ = actions.SendUserMessageAsync(JsonData.Parse("[{\"type\":\"text\",\"text\":\"user\"}]"), new(ExtensionMessageDelivery.FollowUp, false));
                return ValueTask.CompletedTask;
            })); return ValueTask.CompletedTask;
        });
        var original = registry.InvokeCommandAsync(registry.CaptureSnapshot(), "actions", JsonData.EmptyObject).AsTask(); Exception? primary = null;
        try
        {
            await SignalOrOriginal(effects.Entered.Task, original); Check(!original.IsCompleted, "Ignored actual message original detached.");
            Check(effects.ModelCalls == 1 && effects.Custom is { Display: false, Details: { } details } && details.Value.ValueKind == System.Text.Json.JsonValueKind.Null &&
                effects.CustomOptions == new ExtensionMessageOptions(false, ExtensionMessageDelivery.NextTurn) && effects.UserOptions == new ExtensionUserMessageOptions(ExtensionMessageDelivery.FollowUp, false), "Original message options/absence forwarding lost.");
        }
        catch (Exception error) { primary = error; }
        finally
        {
            effects.Message.TrySetResult(); var fault = await Observe("ignored-action-command", original); if (fault is not null) primary = new AggregateException(new[] { primary, fault }.OfType<Exception>());
            await Observe("ignored-real-message-original", effects.Message.Task);
            var close = scope.DisposeAsync().AsTask(); Throw(await Observe("actions-close", close));
        }
        Throw(primary);
    }
    private static async Task ActionFaults()
    {
        await using var registry = new ExtensionRegistry(); var effects = new ActionHost();
        using var providers = new ExtensionProviderRegistrationHost(registry, [Definition(Completed)], new Configuration(Definition(Completed)));
        IExtensionRegistrationActions? stale = null; var first = new IOException("action-first"); var second = new AggregateException();
        var scope = await Activate(registry, "action-fault", owner => {
            owner.RegisterCommand(ExtensionRegistrationCommands.Create("action-fault", "action-fault", "fault identity", providers, effects, (_, actions, _, _) => {
                stale = actions;
                try { actions.SetModelAsync(Model with { Id = "unknown" }); throw new IOException("Unknown model admitted."); } catch (ArgumentException) { }
                try { actions.SendUserMessageAsync(JsonData.Parse("{}")); throw new IOException("Invalid content admitted."); } catch (ArgumentException) { }
                _ = actions.SendMessageAsync(new("custom", JsonData.Parse("\"text\""), true)); return ValueTask.CompletedTask;
            })); return ValueTask.CompletedTask;
        });
        var original = registry.InvokeCommandAsync(registry.CaptureSnapshot(), "action-fault", JsonData.EmptyObject).AsTask(); Exception? caught = null;
        try { await SignalOrOriginal(effects.Entered.Task, original); effects.Message.TrySetException([first, second]); }
        finally
        {
            effects.Message.TrySetResult(); caught = await Observe("action-fault-command", original); await Observe("actual-host-multi-fault-original", effects.Message.Task);
            var close = scope.DisposeAsync().AsTask(); Throw(await Observe("action-fault-close", close));
        }
        Check(caught is ExtensionProviderOriginalFaultException evidence && ReferenceEquals(evidence.Original, effects.Message.Task) &&
            ((AggregateException)evidence.Evidence).InnerExceptions.Count == 2 && ReferenceEquals(((AggregateException)evidence.Evidence).InnerExceptions[0], first) && effects.ModelCalls == 0,
            "Ignored host original full fault inventory lost or validation called host.");
        try { stale!.GetModels(); throw new IOException("Stale action view accepted."); } catch (InvalidOperationException) { }
    }
    private static async Task ExactCallback()
    {
        await using var registry = new ExtensionRegistry(); var effects = new ActionHost();
        using var providers = new ExtensionProviderRegistrationHost(registry, [Definition(Completed)], new Configuration(Definition(Completed)));
        IExtensionCommandContext? saved = null; var callbacks = 0;
        var resolverFault = new OperationCanceledException("clear token resolver original fault");
        var guarded = ExtensionRegistrationCommands.Create("guarded", "guarded", "exact context", providers, effects,
            (_, _, _, _) => { callbacks++; return ValueTask.CompletedTask; });
        var automatic = ExtensionRegistrationCommands.Create("automatic", "automatic", "host capability required", providers,
            (_, _, _, _) => { callbacks++; return ValueTask.CompletedTask; });
        var scope = await Activate(registry, "exact-context", owner => {
            owner.RegisterCommand(new("save", "save", "capture native context", (_, context, _) => { saved = context; return ValueTask.CompletedTask; }));
            owner.RegisterCommand(new("later", "later", "same owner fresh frame", async (_, context, token) => {
                var rejected = guarded.ExecuteAsync(JsonData.EmptyObject, saved!, token).AsTask();
                var fault = await Observe("saved-context-rejection-original", rejected);
                Check(fault is InvalidOperationException && rejected.IsFaulted, "Saved context admitted in a later same-owner frame.");
                var missing = automatic.ExecuteAsync(JsonData.EmptyObject, context, token).AsTask();
                var missingFault = await Observe("missing-action-capability-original", missing);
                Check(missingFault is ExtensionProviderOriginalFaultException missingEvidence && missingEvidence.Original is null && missingEvidence.Evidence is NotSupportedException && missing.IsFaulted,
                    "Unconfigured host silently fabricated action effects.");
            }));
            owner.RegisterCommand(ExtensionRegistrationCommands.Create("resolver-fault", "resolver-fault", "resolver fault status", providers,
                _ => throw resolverFault, (_, _, _, _) => { callbacks++; return ValueTask.CompletedTask; }));
            return ValueTask.CompletedTask;
        });
        try
        {
            var save = registry.InvokeCommandAsync(registry.CaptureSnapshot(), "save", JsonData.EmptyObject).AsTask();
            Throw(await Observe("save-context-command-original", save));
            var later = registry.InvokeCommandAsync(registry.CaptureSnapshot(), "later", JsonData.EmptyObject).AsTask();
            Throw(await Observe("later-context-command-original", later));
            var failedResolution = registry.InvokeCommandAsync(registry.CaptureSnapshot(), "resolver-fault", JsonData.EmptyObject).AsTask();
            var resolutionEvidence = await Observe("clear-resolver-native-command-original", failedResolution);
            Check(resolutionEvidence is ExtensionProviderOriginalFaultException resolverEvidence && resolverEvidence.Original is null && ReferenceEquals(resolverEvidence.Evidence, resolverFault) &&
                failedResolution.IsFaulted && !failedResolution.IsCanceled && !resolverFault.CancellationToken.IsCancellationRequested,
                "Synchronous clear-token host resolver OCE became canceled or lost original cause identity.");
            Check(callbacks == 0 && effects.ModelCalls == 0 && effects.Custom is null, "Rejected context/capability invoked user or host code.");
        }
        finally { var close = scope.DisposeAsync().AsTask(); Throw(await Observe("exact-context-close-original", close)); }
    }
}
