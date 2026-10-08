using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Cli.Mcp;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Runtime;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class McpSessionRuntimeFactoryTests
{
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("mcp-session-factory. invalid target is rejected before owning acquisition", InvalidTarget),
        ("mcp-session-factory. repeated release joins the held original native cleanup and fault", HeldRelease),
        ("mcp-session-factory. activation rejection joins held native cleanup and retains both faults", RejectedActivation),
        ("mcp-session-factory. wrong server generation joins actual capture cleanup before native release", WrongCaptureGeneration),
        ("mcp-session-factory. actual lifecycle supplies cwd and binds each fresh target before return", Lifecycle)
    ];
    private static readonly ModelDescriptor Model = new("mcp-factory", "openai-responses", "fixture");
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value) { if (!value) throw new IOException("Runtime assembly assertion failed."); }
    private static async Task<Exception> Failure(Task original)
    { try { await original; } catch (Exception error) { return error; } throw new IOException("Expected original failure."); }
    private static IEnumerable<Exception> OriginalEvidence(Exception error)
    {
        yield return error;
        if (error is AggregateException aggregate)
            foreach (var inner in aggregate.InnerExceptions) foreach (var item in OriginalEvidence(inner)) yield return item;
        else if (error.InnerException is { } inner)
            foreach (var item in OriginalEvidence(inner)) yield return item;
    }
    private static SessionRuntimeRegistry Registry(Policy policy) => new([new(Model, new NoTransport())], [], policy,
        new SessionRuntimeRegistryOptions { BindNestedCallsToSessionOwner = true });
    private static McpSessionRuntimeAdmission Admission(Policy policy, IAsyncDisposable resources) =>
        new(Registry(policy), resources, new Resource(() => Task.CompletedTask), policy, new McpServerCatalog([], []), [], false, (plan, current) => new(current, []));
    private static async Task InvalidTarget()
    {
        var acquisitions = 0;
        var factory = new McpSessionRuntimeFactory((cwd, generation, token) =>
        { acquisitions++; throw new IOException("Invalid input acquired native resources."); });
        Check(await Failure(factory.AcquireAsync("relative", 1).AsTask()) is ArgumentException);
        Check(await Failure(factory.AcquireAsync(Path.GetTempPath(), 0).AsTask()) is ArgumentOutOfRangeException);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Check(await Failure(factory.AcquireAsync(Path.GetTempPath(), 1, canceled.Token).AsTask()) is OperationCanceledException);
        Check(acquisitions == 0);
    }
    private static async Task HeldRelease()
    {
        var policy = new Policy(); var entered = Gate(); var release = Gate(); var calls = 0;
        var original = new IOException("native cleanup original");
        var native = new Resource(async () => { calls++; entered.TrySetResult(); await release.Task; throw original; });
        var factory = new McpSessionRuntimeFactory((cwd, generation, token) => ValueTask.FromResult(Admission(policy, native)));
        var lease = await factory.AcquireAsync(Path.GetTempPath(), 7);
        var first = lease.DisposeAsync().AsTask(); var second = lease.DisposeAsync().AsTask();
        Exception? assertion = null;
        try { await entered.Task; Check(ReferenceEquals(first, second) && !first.IsCompleted && calls == 1); }
        catch (Exception error) { assertion = error; }
        finally { release.TrySetResult(); }
        var firstFailure = await Failure(first); var secondFailure = await Failure(second);
        if (assertion is not null) throw new AggregateException(assertion, firstFailure, secondFailure);
        Check(ReferenceEquals(firstFailure, secondFailure) && OriginalEvidence(firstFailure).Contains(original) &&
            firstFailure is McpFactoryDisposalException { Owner: "native", Original.IsFaulted: true } && calls == 1 && first.IsFaulted && second.IsFaulted);
    }
    private static async Task RejectedActivation()
    {
        var policy = new Policy(); var entered = Gate(); var release = Gate();
        var rejection = new IOException("discovery preparation original"); var cleanup = new IOException("native cleanup original");
        var discoveryCleanup = new IOException("discovery cleanup original"); var nativeEntered = false; var nativeCalls = 0; var discoveryCalls = 0;
        var native = new Resource(() => { nativeEntered = true; nativeCalls++; throw cleanup; });
        var discovery = new Resource(async () => { discoveryCalls++; entered.TrySetResult(); await release.Task; throw discoveryCleanup; });
        var factory = new McpSessionRuntimeFactory((cwd, generation, token) => ValueTask.FromResult(
            Admission(policy, native) with { DiscoveryResources = discovery, PrepareDiscovery = (plan, current) => throw rejection }));
        var work = factory.AcquireAsync(Path.GetTempPath(), 4).AsTask();
        Exception? assertion = null;
        try { await entered.Task; Check(!work.IsCompleted && !nativeEntered); }
        catch (Exception error) { assertion = error; }
        finally { release.TrySetResult(); }
        var failed = await Failure(work);
        if (assertion is not null) throw new AggregateException(assertion, failed);
        var evidence = OriginalEvidence(failed).ToArray();
        Check(nativeEntered && work.IsFaulted && evidence.Contains(rejection) && evidence.Contains(discoveryCleanup) && evidence.Contains(cleanup) &&
            nativeCalls == 1 && discoveryCalls == 1 && evidence.OfType<McpFactoryDisposalException>().Any(error => error.Owner == "discovery" && error.Original?.IsFaulted == true) &&
            evidence.OfType<McpFactoryDisposalException>().Any(error => error.Owner == "native" && error.Original is null && ReferenceEquals(error.InnerException, cleanup)));
    }
    private static async Task Lifecycle()
    {
        var backend = new SessionStorageBackend(Path.Combine(Path.GetTempPath(), "mcp-factory-" + Guid.NewGuid().ToString("N")), SessionStorageMode.InMemory);
        var policy = new Policy(); var generations = new List<long>(); var released = 0; var ids = 0;
        var factory = new McpSessionRuntimeFactory((cwd, generation, token) =>
        {
            Check(cwd == backend.Directory); generations.Add(generation);
            return ValueTask.FromResult(Admission(policy, new Resource(() => { released++; return Task.CompletedTask; })));
        });
        var lifecycle = new PersistentSessionLifecycle(Registry(policy), () => 0, () => "entry-" + ++ids,
            nextSessionId: () => "session-" + ++ids, backend: backend, runtimeForAttachment: factory.AcquireAsync);
        var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
        { type = "session", version = 3, id = "initial", timestamp = "2026-10-05T00:00:00.000Z", cwd = backend.Directory }));
        await using var initial = await lifecycle.CreateAsync(Path.Combine(backend.Directory, "initial.jsonl"), header, Model);
        await using var owner = await lifecycle.AttachAsync(initial);
        Check(owner.Current.Session.CaptureToolCatalogRegistry().InvocationOwnerGeneration == 1);
        await owner.CreateAsync(owner.Current, new(AgentSessionCreationKind.New));
        Check(owner.Current.Session.CaptureToolCatalogRegistry().InvocationOwnerGeneration == 2 && generations.SequenceEqual(new long[] { 1, 2 }));
        await owner.DisposeAsync(); Check(released == 2 && backend.ActiveWriterCount == 0);
    }
    private static async Task WrongCaptureGeneration()
    {
        var policy = new Policy(); var channel = new Channel(); var extensions = new ExtensionRegistry();
        var scope = await extensions.ActivateAsync("factory-mismatch", new EmptyExtension());
        var nativeReleased = 0; var discoveryReleased = 0;
        var entry = new McpServerEntry("demo", McpConfigurationReader.Validate("demo",
            JsonData.Parse("{\"command\":\"inert\",\"exposure\":\"direct\"}").Value).Config!, "fixture", McpConfigurationScope.Extension);
        var factory = new McpSessionRuntimeFactory((cwd, generation, token) =>
        {
            var admission = Admission(policy, new Resource(() => { nativeReleased++; return Task.CompletedTask; }));
            return ValueTask.FromResult(admission with
            {
                Catalog = new McpServerCatalog([entry], []),
                DiscoveryResources = new Resource(async () => { discoveryReleased++; await extensions.DisposeAsync(); }),
                Servers = [new("demo", validated => Check(ReferenceEquals(validated, entry)), (actual, current, cancellation) =>
                    McpPreOpenServerCapture.AcquireAsync(actual, extensions, scope, current, policy,
                        (name, arguments, validationToken) => ValueTask.FromResult(true),
                        (configured, acquireToken) => ValueTask.FromResult<IMcpAdmittedRequestChannel>(channel),
                        new(generation - 1, "0.99.1"), (registry, binding) => binding.PreparedHooks ?? registry.PreparedToolHooks, cancellation))]
            });
        });
        var work = factory.AcquireAsync(Path.GetTempPath(), 3).AsTask();
        Exception? assertion = null;
        try { await channel.Entered.Task; Check(!work.IsCompleted && nativeReleased == 0 && discoveryReleased == 0); }
        catch (Exception error) { assertion = error; }
        finally { channel.Release.TrySetResult(); }
        var failure = await Failure(work);
        if (assertion is not null) throw new AggregateException(assertion, failure);
        Check(failure is InvalidOperationException && channel.Closes == 1 && nativeReleased == 1 && discoveryReleased == 1);
    }
    private sealed class EmptyExtension : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Channel : IMcpAdmittedRequestChannel
    {
        internal readonly TaskCompletionSource Entered = Gate(), Release = Gate();
        internal int Closes;
        private Task? close;
        public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask NotifyAsync(string method, JsonData? parameters, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<JsonData> RequestAsync(string method, JsonData? parameters, McpRequestOptions options, CancellationToken token) =>
            ValueTask.FromResult(JsonData.Parse(method == "initialize"
                ? "{\"protocolVersion\":\"2025-11-25\",\"serverInfo\":{\"name\":\"synthetic\",\"version\":\"1\"},\"capabilities\":{\"tools\":{}}}"
                : method == "tools/list" ? "{\"tools\":[]}" : throw new IOException("No tool execution admitted.")));
        public Task CloseAsync() => close ??= CloseCore();
        private async Task CloseCore() { Closes++; Entered.TrySetResult(); await Release.Task; }
    }
    private sealed class Resource(Func<Task> close) : IAsyncDisposable
    { public ValueTask DisposeAsync() => new(close()); }
    private sealed class Policy : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(new ToolActionAuthorization(false)); }
    private sealed class NoTransport : IChatTransport
    {
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        { await Task.FromException(new IOException("Runtime assembly must not invoke provider.")); yield break; }
    }
}
