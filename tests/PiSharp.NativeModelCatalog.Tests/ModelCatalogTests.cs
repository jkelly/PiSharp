using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Cli.Extensions;
using PiSharp.CodingAgent;
using PiSharp.CodingAgent.ToolSelection;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;
using PiSharp.Sessions.Serialization;

internal static class ModelCatalogTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() => [
        ("real known session selects late installed provider and persists actual next stream", LateSession),
        ("real factory creates session using already installed late provider binding", LateFactory),
        ("catalog validation and requested cancellation refuse before capability callbacks", Admission),
        ("reentrant capability publication cannot overwrite newer atomic binding thinking snapshot", Atomic),
        ("tool catalog and initial-selection copies share actual model publications without capability replay", Copies),
        ("captured selection retains exact transport while later resolution uses replacement", Captured)
    ];
    private static readonly ModelDescriptor Seed = new("known", "catalog-api", "baseline");
    private static readonly ModelDescriptor Late = new("late", "catalog-api", "registered");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Deny : IToolActionPolicy
    {
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new ToolActionAuthorization(false)); }
    }
    private sealed class Transport(ModelDescriptor expected, string output, ImmutableArray<string> supported) : IChatTransport, IThinkingLevelTransport
    {
        internal int Capabilities, Requests; internal Action? OnCapabilities;
        public ImmutableArray<string> GetSupportedThinkingLevels(ModelDescriptor model)
        { Check(model == expected, "Wrong binding metadata identity."); Capabilities++; OnCapabilities?.Invoke(); return supported; }
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); Check(request.Model == expected, "Actual transport selected wrong model."); Requests++;
            yield return new StreamStarted(new AssistantMessage(expected.Api, expected.Provider, expected.Id, request.Timestamp,
                [], TokenUsage.Zero, StopReason.Pending));
            yield return new TextStarted(0, new TextContent(""));
            yield return new TextDelta(0, output);
            yield return new TextEnded(0, output);
            yield return new StreamDone(StopReason.Stop, new AssistantMessage(expected.Api, expected.Provider, expected.Id, request.Timestamp,
                [new TextContent(output)], TokenUsage.Zero, StopReason.Stop)); await Task.CompletedTask;
        }
    }
    private sealed class Configuration : IExtensionProviderConfigurationAdapter
    {
        public ExtensionProviderDefinition Resolve(string name, JsonData configuration) => Definition();
    }
    private static ExtensionProviderDefinition Definition() => new(Late.Provider,
        [new(Late, JsonData.Parse("{\"id\":\"late\",\"api\":\"catalog-api\",\"provider\":\"registered\"}"))], ProviderStream);
    private static async IAsyncEnumerable<StreamEvent> ProviderStream(ExtensionProviderStreamRequest request, IExtensionContext? context,
        [EnumeratorCancellation] CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Check(context is not null && context.OwnerId == "late-owner", "Actual installed provider lost native owner context.");
        yield return new StreamStarted(new AssistantMessage(Late.Api, Late.Provider, Late.Id, request.Timestamp,
            [], TokenUsage.Zero, StopReason.Pending));
        yield return new TextStarted(0, new TextContent(""));
        yield return new TextDelta(0, "actual-late-provider");
        yield return new TextEnded(0, "actual-late-provider");
        yield return new StreamDone(StopReason.Stop, new AssistantMessage(Late.Api, Late.Provider, Late.Id, request.Timestamp,
            [new TextContent("actual-late-provider")], TokenUsage.Zero, StopReason.Stop)); await Task.CompletedTask;
    }
    private sealed class Extension(Func<IExtensionRegistry, ValueTask> initialize) : IPiSharpExtension
    { public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) => initialize(registry); public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    private static async Task<Exception?> Observe(string name, Task original)
    {
        Exception? caught = null;
        try { await original; } catch (Exception error) { caught = error; }
        finally { Evidence.Record(name, original, caught); }
        return caught;
    }
    private static async Task Join(string name, Task original)
    { var error = await Observe(name, original); if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw(); }
    private static SessionRuntimeRegistry Runtime(Transport seed, SessionRuntimeRegistryOptions? options = null) => new([new(Seed, seed)], [], new Deny(), options);
    private static string FreshRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "PiSharp-native-model-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); return root; // Finite local fixture artifact retained for root audit.
    }
    private static Task<PersistentAgentSession> Create(string root, SessionRuntimeRegistry runtime, ModelDescriptor selected)
    {
        var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3,
            id = "model-catalog", timestamp = "2026-10-06T00:00:00.000Z", cwd = root })); var sequence = 0;
        return PersistentAgentSession.CreateAsync(Path.Combine(root, "session.jsonl"), header, runtime, selected, () => 1, () => "entry-" + ++sequence);
    }
    private static async Task LateSession()
    {
        await using var native = new ExtensionRegistry();
        using var bridge = new NativeExtensionRegistrationBridge(native, new ExtensionHostFlagValues(new Dictionary<string, ExtensionFlagValue>()), [], new Configuration());
        var seed = new Transport(Seed, "known-body", ["off"]); var runtime = Runtime(seed); var baseline = runtime.CaptureModelCatalog();
        bridge.ConfigureModelCatalog(runtime, baseline.Bindings);
        var creation = Create(FreshRoot(), runtime, Seed); await Join("real-known-session-factory", creation); await using var session = creation.Result;
        NativeExtensionRegistrationFacade? facade = null; RegistrationScope? activeScope = null;
        var activation = bridge.ActivateOwnerAsync("late-owner", new Extension(owner => {
            facade!.RegisterProvider(Definition());
            owner.RegisterCommand(new("close-refusal", "close-refusal", "exact native close admission", async (_, _, _) => {
                var revision = runtime.CaptureModelCatalog().Revision;
                var refusal = bridge.RetireOwnerAsync(activeScope!);
                var error = await Observe("same-owner-retirement-admission-refusal", refusal);
                Check(error is ExtensionRegistrationException registration && registration.Failure == ExtensionRegistrationFailure.ReentrantDisposal &&
                    refusal.IsFaulted && !refusal.IsCanceled && !activeScope!.ExtensionLifetimeCancellationToken.IsCancellationRequested &&
                    runtime.CaptureModelCatalog().Revision == revision && bridge.CaptureModels().Any(row => row.Model == Late),
                    "Synchronous native admission refusal withdrew active metadata or changed catalog.");
            }));
            return ValueTask.CompletedTask;
        }), value => facade = value);
        await Join("real-late-provider-activation", activation); var scope = activation.Result.Scope;
        activeScope = scope;
        try
        {
            var command = native.InvokeCommandAsync(native.CaptureSnapshot(), "close-refusal", JsonData.EmptyObject).AsTask();
            await Join("real-native-same-owner-refusal-command", command);
            var published = runtime.CaptureModelCatalog();
            Check(published.Revision == 1 && published.Bindings.Length == 2, "Late provider binding was not actually installed.");
            var configure = session.ConfigureAsync(new(Model: Late)); await Join("real-late-model-durable-configure", configure);
            var selected = session.Snapshot.Context.Model;
            Check(selected is not null && selected.Provider == Late.Provider && selected.ModelId == Late.Id,
                "Real session selected model differs from durable late model.");
            var prompt = session.SubmitInputAsync(new("ask", PromptInputSource.Extension)); await Join("real-late-model-session-prompt", prompt);
            Check(prompt.Result.Run is not null && session.Snapshot.Context.LlmMessages.Any(message => message.Role == "assistant" &&
                message.WireBody.ToString().Contains("actual-late-provider", StringComparison.Ordinal)), "Actual late provider stream was not committed to session.");
            facade!.RegisterProvider(Definition());
            Check(runtime.CaptureModelCatalog().Revision == 2, "Active replacement did not refresh actual catalog.");
            facade.UnregisterProvider(Late.Provider);
            Check(runtime.CaptureModelCatalog().Revision == 3 && runtime.CaptureModelCatalog().Bindings.Length == 1, "Unregister did not restore actual baseline.");
            var before = session.Snapshot;
            var rejected = session.ConfigureAsync(new(Model: Late)); var fault = await Observe("unregistered-late-model-configure", rejected);
            Check(fault is SessionRuntimeRegistryException error && error.Failure == SessionRuntimeRegistryFailure.UnknownModel &&
                session.Snapshot.Log.CommittedByteLength == before.Log.CommittedByteLength, "Unregistered model changed durable selection.");
            var restore = session.ConfigureAsync(new(Model: Seed)); await Join("real-baseline-model-restoration", restore);
        }
        finally { var close = bridge.RetireOwnerAsync(scope); await Join("late-owner-close-and-catalog-refresh", close); }
    }
    private static async Task LateFactory()
    {
        await using var native = new ExtensionRegistry();
        using var bridge = new NativeExtensionRegistrationBridge(native, new ExtensionHostFlagValues(new Dictionary<string, ExtensionFlagValue>()), [], new Configuration());
        var runtime = Runtime(new(Seed, "seed", ["off"])); var baseline = runtime.CaptureModelCatalog(); bridge.ConfigureModelCatalog(runtime, baseline.Bindings); NativeExtensionRegistrationFacade? facade = null;
        var activation = bridge.ActivateOwnerAsync("late-owner", new Extension(_ => { facade!.RegisterProvider(Definition()); return ValueTask.CompletedTask; }), value => facade = value);
        await Join("late-factory-provider-activation", activation);
        try
        {
            Check(runtime.CaptureModelCatalog().Revision == 1, "Activation did not install factory catalog.");
            var create = Create(FreshRoot(), runtime, Late); await Join("actual-late-model-factory", create); await using var session = create.Result;
            Check(session.Snapshot.Context.Model?.ModelId == Late.Id, "Factory could not select actual installed late model.");
            var prompt = session.SubmitInputAsync(new("factory-ask", PromptInputSource.Extension)); await Join("actual-late-factory-prompt", prompt);
            Check(prompt.Result.Run is not null, "Late factory transport did not run.");
        }
        finally { var close = bridge.RetireOwnerAsync(activation.Result.Scope); await Join("late-factory-owner-close-and-catalog-refresh", close); }
        var retiredRevision = runtime.CaptureModelCatalog().Revision;
        var repeated = bridge.RetireOwnerAsync(activation.Result.Scope);
        var repeatedFault = await Observe("already-retired-owner-admission-refusal", repeated);
        Check(repeatedFault is InvalidOperationException && repeated.IsFaulted && !repeated.IsCanceled &&
            runtime.CaptureModelCatalog().Revision == retiredRevision && runtime.CaptureModelCatalog().Bindings.SequenceEqual(baseline.Bindings),
            "Retired owner admission changed the actual catalog.");
    }
    private static Task Admission()
    {
        var source = new Transport(Seed, "seed", ["off"]); var runtime = Runtime(source); var baseline = runtime.CaptureModelCatalog(); var calls = source.Capabilities;
        try { runtime.PublishModelCatalog(0, [new(Seed, source), new(Seed, source)]); throw new IOException("Duplicate catalog admitted."); }
        catch (SessionRuntimeRegistryException error) { Check(error.Failure == SessionRuntimeRegistryFailure.InvalidRegistration, "Wrong duplicate refusal."); }
        using var stop = new CancellationTokenSource(); stop.Cancel();
        try { runtime.PublishModelCatalog(0, [new(Seed, source)], stop.Token); throw new IOException("Canceled publication admitted."); }
        catch (OperationCanceledException error) { Check(error.CancellationToken == stop.Token, "Wrong owned publication token."); }
        Check(source.Capabilities == calls && runtime.CaptureModelCatalog().Revision == baseline.Revision &&
            ReferenceEquals(runtime.Resolve(Seed, []).Configuration.Transport, source), "Invalid publication invoked capabilities or changed catalog.");
        return Task.CompletedTask;
    }
    private static Task Atomic()
    {
        var seed = new Transport(Seed, "seed", ["off"]); var runtime = Runtime(seed);
        var winner = new Transport(Seed, "winner", ["off"]); var stale = new Transport(Seed, "stale", ["low"]);
        stale.OnCapabilities = () => runtime.PublishModelCatalog(0, [new(Seed, winner)]);
        try { runtime.PublishModelCatalog(0, [new(Seed, stale)]); throw new IOException("Reentrant stale publication overwrote current."); }
        catch (InvalidOperationException) { }
        var selection = runtime.Resolve(Seed, []);
        Check(selection.ModelCatalogRevision == 1 && ReferenceEquals(selection.Configuration.Transport, winner) &&
            runtime.GetSupportedThinkingLevels(Seed).SequenceEqual(new[] { "off" }), "Binding and thinking came from different publications.");
        try { runtime.Resolve(Seed, [], "low"); throw new IOException("Stale thinking accepted."); }
        catch (SessionRuntimeRegistryException error) { Check(error.Failure == SessionRuntimeRegistryFailure.UnsupportedThinkingLevel, "Wrong thinking rejection."); }
        return Task.CompletedTask;
    }
    private static Task Copies()
    {
        var seed = new Transport(Seed, "seed", ["off"]); var runtime = Runtime(seed, new() { LifetimeToolSelection = AllowedToolSelection.Create([]) });
        var calls = seed.Capabilities; var toolsCopy = runtime.WithToolCatalog([], null); var lifetimeCopy = toolsCopy.WithInitialToolSelectionFromCatalog();
        Check(seed.Capabilities == calls, "Copy replayed transport capability callback.");
        var late = new Transport(Late, "late", ["off"]); runtime.PublishModelCatalog(0, [new(Seed, seed), new(Late, late)]);
        Check(toolsCopy.CaptureModelCatalog().Revision == 1 && lifetimeCopy.CaptureModelCatalog().Revision == 1 &&
            ReferenceEquals(lifetimeCopy.Resolve(Late, []).Configuration.Transport, late) && lifetimeCopy.UsesFinalActionPolicy(new Deny()) == false,
            "Copy lost model publication or widened policy identity.");
        return Task.CompletedTask;
    }
    private static Task Captured()
    {
        var old = new Transport(Seed, "old", ["off"]); var runtime = Runtime(old); var captured = runtime.Resolve(Seed, []);
        var replacement = new Transport(Seed, "new", ["off"]); runtime.PublishModelCatalog(0, [new(Seed, replacement)]);
        Check(captured.ModelCatalogRevision == 0 && ReferenceEquals(captured.Configuration.Transport, old) &&
            runtime.Resolve(Seed, []).ModelCatalogRevision == 1 && ReferenceEquals(runtime.Resolve(Seed, []).Configuration.Transport, replacement),
            "Captured binding was mutated by replacement."); return Task.CompletedTask;
    }
}
