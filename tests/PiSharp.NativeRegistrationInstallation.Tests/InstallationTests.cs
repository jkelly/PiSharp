using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using PiSharp.Agent;
using PiSharp.Cli.Extensions;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;

internal static class InstallationTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() => [
        ("native initializer stages flags providers and actual Agent consumes installed transport", Success),
        ("failed initializer retains full original inventory and publishes no metadata defaults", Failed),
        ("requested initializer cancellation joins held original before native retirement", HeldInitializer),
        ("binder initializer faulted OCE and foreign canceled originals remain faults", FaultedCancellation),
        ("publication fault joins held cleanup and preserves both complete originals", PublicationCollision),
        ("installed provider owner close joins held move and disposal then refuses facade", HeldProvider)
    ];
    private static readonly ModelDescriptor Model = new("installed", "native-install", "admitted");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static ExtensionProviderDefinition Definition(ExtensionProviderStreamCallback callback, string name = "admitted") =>
        new(name, [new(Model with { Provider = name }, JsonData.Parse("{\"id\":\"installed\",\"api\":\"native-install\",\"provider\":\"" + name + "\"}"))], callback);
    private sealed class Configuration : IExtensionProviderConfigurationAdapter
    {
        internal int Calls;
        public ExtensionProviderDefinition Resolve(string name, JsonData configuration) { Calls++; return Definition(Completed, name); }
    }
    private static async IAsyncEnumerable<StreamEvent> Completed(ExtensionProviderStreamRequest request, IExtensionContext? context,
        [EnumeratorCancellation] CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        yield return new StreamStarted(new AssistantMessage(request.Model.Api, request.Model.Provider, request.Model.Id, 1,
            [], TokenUsage.Zero, StopReason.Pending));
        yield return new TextStarted(0, new TextContent(""));
        yield return new TextDelta(0, "installed-real-stream");
        yield return new TextEnded(0, "installed-real-stream");
        yield return new StreamDone(StopReason.Stop, new AssistantMessage(request.Model.Api, request.Model.Provider, request.Model.Id, 1,
            [new TextContent("installed-real-stream")], TokenUsage.Zero, StopReason.Stop));
        await Task.CompletedTask;
    }
    private sealed class Extension(Func<IExtensionRegistry, CancellationToken, ValueTask> initialize,
        Func<ValueTask>? cleanup = null) : IPiSharpExtension
    {
        internal int Disposals;
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) => initialize(registry, token);
        public ValueTask DisposeAsync() { Disposals++; return cleanup?.Invoke() ?? ValueTask.CompletedTask; }
    }
    private sealed class Sink : IAgentEventSink
    {
        internal int Messages;
        public ValueTask EmitAsync(AgentEvent observation, CancellationToken cancellationToken) { if (observation is AssistantMessageEnded) Messages++; return ValueTask.CompletedTask; }
    }
    private static async Task<Exception?> Observe(string name, Task original)
    {
        Exception? observed = null;
        try { await original; } catch (Exception error) { observed = error; }
        finally { Evidence.Record(name, original, observed); }
        return observed;
    }
    private static async Task SignalOrOriginal(Task signal, Task original)
    {
        var winner = await Task.WhenAny(signal, original).WaitAsync(TimeSpan.FromSeconds(5));
        await winner; Check(signal.IsCompletedSuccessfully, "Original settled before required signal.");
    }
    private static async Task Close(RegistrationScope scope)
    { var original = scope.DisposeAsync().AsTask(); var error = await Observe("native-owner-close", original); if (error is not null) throw error; }
    private static bool Contains(Exception? root, Exception exact)
    {
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance); var queue = new Queue<Exception>();
        if (root is not null) queue.Enqueue(root);
        while (queue.TryDequeue(out var item))
        {
            if (ReferenceEquals(item, exact)) return true;
            if (!seen.Add(item)) continue;
            if (item is AggregateException aggregate) foreach (var child in aggregate.InnerExceptions) queue.Enqueue(child);
            else if (item.InnerException is { } child) queue.Enqueue(child);
        }
        return false;
    }
    private static async Task Success()
    {
        await using var registry = new ExtensionRegistry();
        var values = new ExtensionHostFlagValues(new Dictionary<string, ExtensionFlagValue> { ["override"] = ExtensionFlagValue.Boolean(false) });
        var config = new Configuration(); using var bridge = new NativeExtensionRegistrationBridge(registry, values, [], config);
        NativeExtensionRegistrationFacade? facade = null;
        var extension = new Extension((owner, _) => {
            Check(facade is not null && facade.OwnerId == owner.OwnerId && facade.OwnerGeneration == owner.OwnerGeneration, "Binder lost real owner identity.");
            facade!.RegisterFlag("mode", new(ExtensionFlagKind.String, DefaultValue: ExtensionFlagValue.String("first")));
            facade.RegisterFlag("mode", new(ExtensionFlagKind.String, DefaultValue: ExtensionFlagValue.String("second")));
            facade.RegisterFlag("override", new(ExtensionFlagKind.Boolean, DefaultValue: ExtensionFlagValue.Boolean(true)));
            facade.RegisterProvider("admitted", JsonData.EmptyObject);
            Check(bridge.CaptureModels().IsEmpty && bridge.CaptureFlags().IsEmpty && !values.TryGetValue("mode", out var stagedMode) &&
                facade.GetFlag("override")?.BooleanValue == false, "Initializer effects leaked or false override vanished.");
            return ValueTask.CompletedTask;
        });
        var activation = bridge.ActivateOwnerAsync("success", extension, value => facade = value);
        var error = await Observe("installed-activation", activation); Check(error is null, "Successful installation failed.");
        var result = activation.Result;
        try
        {
            Check(result.ActivationOriginal.IsCompletedSuccessfully && result.Facade == facade && config.Calls == 1 &&
                bridge.CaptureModels().Single().Model == Model && bridge.CaptureFlags().Length == 2 &&
                values.TryGetValue("mode", out var value) && value?.StringValue == "first", "Installation receipt/catalog/default mismatch.");
            var sink = new Sink(); await using var agent = new PiSharp.Agent.Agent(new(Model, bridge.CreateTransport(), []), () => 1, sink);
            var prompt = agent.PromptAsync(new TranscriptEntry("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"ask\",\"timestamp\":1}")));
            Check(await Observe("actual-installed-agent-prompt", prompt) is null && sink.Messages == 1, "Actual installed transport was not consumed.");
        }
        finally { await Close(result.Scope); }
        Check(extension.Disposals == 1 && bridge.CaptureModels().IsEmpty && bridge.CaptureFlags().IsEmpty, "Retired owner retained installation.");
    }
    private static async Task Failed()
    {
        await using var registry = new ExtensionRegistry(); var values = new ExtensionHostFlagValues(new Dictionary<string, ExtensionFlagValue>());
        using var bridge = new NativeExtensionRegistrationBridge(registry, values, [], new Configuration());
        var one = new IOException("original-one"); var shared = new IOException("shared"); var nested = new AggregateException(shared, shared);
        var source = new TaskCompletionSource(); source.SetException([one, nested]); NativeExtensionRegistrationFacade? facade = null;
        var extension = new Extension((_, _) => { facade!.RegisterFlag("never", new(ExtensionFlagKind.String, DefaultValue: ExtensionFlagValue.String("never"))); facade.RegisterProvider(Definition(Completed)); return new(source.Task); });
        var activation = bridge.ActivateOwnerAsync("failed", extension, value => facade = value);
        var fault = await Observe("failed-installation", activation);
        await Observe("failed-initializer-original", source.Task);
        Check(activation.IsFaulted && !activation.IsCanceled && Contains(fault, one) && Contains(fault, nested) &&
            source.Task.Exception is { InnerExceptions.Count: 2 } && extension.Disposals == 1 &&
            bridge.CaptureModels().IsEmpty && bridge.CaptureFlags().IsEmpty && !values.TryGetValue("never", out _), "Failed native initializer lost inventory or published defaults.");
    }
    private static async Task HeldInitializer()
    {
        await using var registry = new ExtensionRegistry(); using var stop = new CancellationTokenSource();
        using var bridge = new NativeExtensionRegistrationBridge(registry, new ExtensionHostFlagValues(new Dictionary<string, ExtensionFlagValue>()), [], new Configuration());
        var entered = Signal(); var canceled = Signal(); var held = Signal(); NativeExtensionRegistrationFacade? facade = null;
        var extension = new Extension(async (_, token) => {
            using var registration = token.Register(() => canceled.TrySetResult());
            facade!.RegisterProvider(Definition(Completed)); entered.TrySetResult(); await held.Task;
        });
        var activation = bridge.ActivateOwnerAsync("held-init", extension, value => facade = value, stop.Token);
        Exception? primary = null, outcome = null;
        try
        {
            await SignalOrOriginal(entered.Task, activation); stop.Cancel(); await SignalOrOriginal(canceled.Task, activation);
            Check(!activation.IsCompleted && extension.Disposals == 0 && bridge.CaptureModels().IsEmpty, "Cancellation detached held initializer or leaked catalog.");
        }
        catch (Exception error) { primary = error; }
        finally { held.TrySetResult(); outcome = await Observe("held-initializer-installation", activation); await Observe("held-initializer-original", held.Task); }
        if (primary is not null) throw primary;
        Check(outcome is not null && extension.Disposals == 1 && bridge.CaptureModels().IsEmpty, "Canceled initialization published or skipped cleanup.");
    }
    private static async Task FaultedCancellation()
    {
        foreach (var vector in new[] { "binder", "faulted", "foreign-canceled" })
        {
            await using var registry = new ExtensionRegistry();
            using var bridge = new NativeExtensionRegistrationBridge(registry, new ExtensionHostFlagValues(new Dictionary<string, ExtensionFlagValue>()), [], new Configuration());
            var oce = new OperationCanceledException("clear synchronous binder");
            Task callback = vector == "binder" ? Task.CompletedTask : vector == "foreign-canceled" ? Task.FromCanceled(new CancellationToken(true)) : Task.FromException(oce);
            var calls = 0; var extension = new Extension((_, _) => { calls++; return new(callback); });
            var activation = bridge.ActivateOwnerAsync(vector, extension, _ => { if (vector == "binder") throw oce; });
            var fault = await Observe("unowned-cancellation-" + vector, activation);
            if (vector != "binder") await Observe("unowned-initializer-original-" + vector, callback);
            Check(activation.IsFaulted && !activation.IsCanceled && extension.Disposals == 1 &&
                (vector == "foreign-canceled" ? callback.IsCanceled : Contains(fault, oce)) &&
                calls == (vector == "binder" ? 0 : 1), "Unowned cancellation became successful/canceled installation or lost source cause.");
        }
    }
    private sealed class RejectingValues(Exception cause) : IExtensionHostFlagValues
    {
        public bool TryGetValue(string name, out ExtensionFlagValue? value) { value = null; return false; }
        public void CommitDefaults(ImmutableArray<KeyValuePair<string, ExtensionFlagValue>> defaults) => throw cause;
    }
    private static async Task PublicationCollision()
    {
        var registry = new ExtensionRegistry(); var publication = new OperationCanceledException("unrequested publication fault");
        using var bridge = new NativeExtensionRegistrationBridge(registry, new RejectingValues(publication), [], new Configuration());
        var disposing = Signal(); var cleanup = Signal(); var one = new IOException("cleanup-one"); var two = new IOException("cleanup-two");
        NativeExtensionRegistrationFacade? facade = null;
        var extension = new Extension((_, _) => { facade!.RegisterProvider(Definition(Completed)); facade.RegisterFlag("pending", new(ExtensionFlagKind.Boolean)); return ValueTask.CompletedTask; },
            () => { disposing.TrySetResult(); return new(cleanup.Task); });
        var activation = bridge.ActivateOwnerAsync("publication", extension, value => facade = value); Exception? primary = null, fault = null;
        try { await SignalOrOriginal(disposing.Task, activation); Check(!activation.IsCompleted && bridge.CaptureModels().IsEmpty && bridge.CaptureFlags().IsEmpty, "Rejected publication skipped original cleanup or retained metadata."); }
        catch (Exception error) { primary = error; }
        finally { cleanup.TrySetException([one, two]); fault = await Observe("publication-and-cleanup-installation", activation); await Observe("actual-cleanup-original", cleanup.Task); }
        var registryClose = registry.DisposeAsync().AsTask();
        var registryCloseFault = await Observe("publication-registry-retained-cleanup-original", registryClose);
        if (!registryClose.IsFaulted || !OnlyExpectedCleanup(registryClose.Exception) ||
            !Contains(registryCloseFault, one) || !Contains(registryCloseFault, two))
            primary = new AggregateException(new[] { primary, registryCloseFault,
                new InvalidOperationException("Registry close did not retain the expected cleanup originals.") }.OfType<Exception>());
        if (primary is not null) throw primary;
        Check(activation.IsFaulted && !activation.IsCanceled && Contains(fault, publication) && Contains(fault, one) && Contains(fault, two) &&
            cleanup.Task.Exception is { InnerExceptions.Count: 2 }, "Publication/cleanup original inventory collision lost.");
        bool OnlyExpectedCleanup(Exception? root)
        {
            if (root is null) return false;
            var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
            var queue = new Queue<Exception>(); queue.Enqueue(root); var edges = 0;
            while (queue.TryDequeue(out var item))
            {
                if (!seen.Add(item)) continue;
                if (seen.Count > 1024) return false;
                if (ReferenceEquals(item, one) || ReferenceEquals(item, two)) continue;
                IEnumerable<Exception> children;
                if (item is AggregateException aggregate && aggregate.InnerExceptions.Count > 0)
                    children = aggregate.InnerExceptions;
                else if (item is ExtensionRegistrationException registration &&
                    registration.Failure == ExtensionRegistrationFailure.CleanupFailed && registration.Operation == "dispose" &&
                    registration.OwnerId is "registry" or "publication" && registration.InnerException is { } inner)
                    children = [inner];
                else if (item is NativeRegistrationOriginalException native && ReferenceEquals(native.Original, cleanup.Task) &&
                    ReferenceEquals(native.Evidence, native.InnerException))
                    children = [native.Evidence];
                else return false;
                foreach (var child in children) { if (++edges > 4096) return false; queue.Enqueue(child); }
            }
            return true;
        }
    }
    private sealed class HeldStream : IAsyncEnumerable<StreamEvent>, IAsyncEnumerator<StreamEvent>
    {
        internal readonly TaskCompletionSource<bool> Move = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Entered = Signal(), Stopped = Signal(), DisposeEntered = Signal(), Cleanup = Signal();
        private CancellationTokenRegistration registration;
        public StreamEvent Current => new TextDelta(0, "held");
        public IAsyncEnumerator<StreamEvent> GetAsyncEnumerator(CancellationToken token = default) { registration = token.Register(() => Stopped.TrySetResult()); return this; }
        public ValueTask<bool> MoveNextAsync() { Entered.TrySetResult(); return new(Move.Task); }
        public async ValueTask DisposeAsync() { DisposeEntered.TrySetResult(); try { await Cleanup.Task; } finally { registration.Dispose(); } }
    }
    private static async Task Drain(IAsyncEnumerable<StreamEvent> stream) { await foreach (var _ in stream) { } }
    private static async Task HeldProvider()
    {
        await using var registry = new ExtensionRegistry();
        using var bridge = new NativeExtensionRegistrationBridge(registry, new ExtensionHostFlagValues(new Dictionary<string, ExtensionFlagValue>()), [], new Configuration());
        var stream = new HeldStream(); NativeExtensionRegistrationFacade? facade = null;
        var extension = new Extension((_, _) => { facade!.RegisterProvider(Definition((_, _, _) => stream)); return ValueTask.CompletedTask; });
        var activation = bridge.ActivateOwnerAsync("held-provider", extension, value => facade = value);
        Check(await Observe("held-provider-installation", activation) is null, "Held provider installation failed.");
        var scope = activation.Result.Scope; var drain = Drain(bridge.CreateTransport().StreamAsync(new(Model, [], 1))); Task? close = null; Exception? primary = null;
        try
        {
            await SignalOrOriginal(stream.Entered.Task, drain); close = scope.DisposeAsync().AsTask(); await SignalOrOriginal(stream.Stopped.Task, close);
            Check(!drain.IsCompleted && !close.IsCompleted, "Native owner close detached actual MoveNext.");
            stream.Move.TrySetResult(false); await SignalOrOriginal(stream.DisposeEntered.Task, drain);
            Check(!drain.IsCompleted && !close.IsCompleted, "Native owner close detached iterator disposal.");
            try { facade!.RegisterProvider(Definition(Completed)); throw new IOException("Retired facade accepted."); } catch (InvalidOperationException) { }
        }
        catch (Exception error) { primary = error; }
        finally
        {
            stream.Move.TrySetResult(false); stream.Cleanup.TrySetResult();
            var drainFault = await Observe("held-installed-stream", drain); close ??= scope.DisposeAsync().AsTask(); var closeFault = await Observe("held-installed-owner-close", close);
            await Observe("held-native-move-original", stream.Move.Task); await Observe("held-native-disposal-original", stream.Cleanup.Task);
            if (drainFault is not null || closeFault is not null) primary = new AggregateException(new[] { primary, drainFault, closeFault }.OfType<Exception>());
        }
        if (primary is not null) throw primary;
        Check(extension.Disposals == 1 && bridge.CaptureModels().IsEmpty, "Installed provider lifecycle did not retire.");
    }
}
