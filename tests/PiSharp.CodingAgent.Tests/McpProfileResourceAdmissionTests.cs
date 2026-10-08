using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Cli.Mcp;
using PiSharp.Cli.Commands;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Agent;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Resources;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Runtime;
using PiSharp.Extensions.Runtime.Mcp.Resources;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

// Authored actual factory/lifecycle controls, not registered or executed in this source slice.
internal static class McpProfileResourceAdmissionTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("mcp-profile-resource-admission.actual-admitted-factory-registers-before-discovery-and-binds-native-calls", Pipeline),
        ("mcp-profile-resource-admission.absent-resource-dependency-preserves-existing-catalog", Absent),
        ("mcp-profile-resource-admission.held-output-joins-actual-attachment-shutdown", HeldOutput),
        ("mcp-profile-resource-admission.multicast-registration-callbacks-refuse-before-effects", Multicast),
        ("mcp-profile-resource-admission.actual-profile-catalog-and-policy-denial", ProfileCaller),
        ("mcp-profile-resource-admission.held-rollback-keeps-full-original-faults", HeldRollback),
        ("mcp-profile-resource-admission.alias-base-owners-refuse-before-resource-callback", Alias),
        ("mcp-profile-resource-admission.acquisition-cancellation-provenance", AcquisitionStates),
        ("mcp-profile-resource-admission.foreign-value-equal-request-rolls-back-owned-response", ForeignRequest),
        ("mcp-profile-resource-admission.successful-composed-owner-stable-close-task", StableClose)
    ];
    private static readonly ModelDescriptor Model = new("resource-factory", "openai-responses", "fixture");
    private static readonly ImmutableArray<string> ResourceNames = [McpResourceTools.ListResources, McpResourceTools.ListTemplates, McpResourceTools.ReadResource];
    private static JsonData Json(string value) => JsonData.Parse(value);
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value) { if (!value) throw new IOException("Resource factory control failed."); }
    private sealed class EmptyExtension : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Policy : IToolActionPolicy
    {
        internal int Calls;
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        { Calls++; return ValueTask.FromResult(new ToolActionAuthorization(true)); }
    }
    private sealed class Channel : IMcpAdmittedRequestChannel
    {
        internal readonly List<string> Methods = []; internal McpInvocationIdentity? Identity;
        internal readonly TaskCompletionSource Closed = Gate(); internal int Closes;
        public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask NotifyAsync(string method, JsonData? parameters, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<JsonData> RequestAsync(string method, JsonData? parameters, McpRequestOptions options, CancellationToken token)
        {
            if (method == "initialize") return ValueTask.FromResult(Json("""{"protocolVersion":"2025-11-25","capabilities":{"resources":{}},"serverInfo":{"name":"synthetic","version":"1"}}"""));
            Methods.Add(method); Identity = options.InvocationIdentity;
            return ValueTask.FromResult(Json(method switch
            {
                "resources/list" => """{"resources":[{"uri":"data://a","name":"a"}]}""",
                "resources/templates/list" => """{"resourceTemplates":[{"uriTemplate":"data://{x}","name":"a"}]}""",
                "resources/read" => """{"contents":[{"uri":"data://a.bin","blob":"AQI="}]}""",
                _ => throw new IOException("Unexpected resource method")
            }));
        }
        public Task CloseAsync() { Closes++; Closed.TrySetResult(); return Task.CompletedTask; }
    }
    private sealed class Transport : IChatTransport
    {
        private int calls;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            var first = Interlocked.Increment(ref calls) == 1;
            AssistantContent content = first ? new ToolCallContent("factory-resource", McpResourceTools.ReadResource, Json("""{"server":"demo","uri":"data://a.bin"}""")) : new TextContent("done");
            var final = new AssistantMessage(Model.Api, Model.Provider, Model.Id, 1, [content], TokenUsage.Zero, first ? StopReason.ToolUse : StopReason.Stop);
            yield return new StreamStarted(final with { Content = [], StopReason = StopReason.Pending });
            if (content is ToolCallContent tool) { yield return new ToolCallStarted(0, tool with { Arguments = JsonData.EmptyObject }); yield return new ToolCallEnded(0, tool); }
            else { yield return new TextStarted(0, new("")); yield return new TextEnded(0, "done"); }
            await Task.CompletedTask; yield return new StreamDone(final.StopReason, final);
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly ExtensionRegistry ServerRegistry = new(), ResourceRegistry = new();
        internal readonly Policy Policy = new(); internal readonly Channel Channel = new();
        internal ReplaceableAgentSession Owner = null!; internal McpAdmittedResourceRegistration? Registration;
        internal int DiscoveryTools, Saves; internal OfflineSessionProfile? Profile; internal string? Workspace; internal McpResourceOutputSaver? SaveOverride;
        internal static async Task<Fixture> Create(bool optedIn, bool actualProfile = false)
        {
            var f = new Fixture();
            try
            {
                var serverScope = await f.ServerRegistry.ActivateAsync("factory-server", new EmptyExtension());
                var resourceScope = await f.ResourceRegistry.ActivateAsync("factory-resources", new EmptyExtension());
                var baseRegistry = new SessionRuntimeRegistry([new(Model, new Transport())], [], f.Policy, new() { BindNestedCallsToSessionOwner = true });
                var entry = new McpServerEntry("demo", McpConfigurationReader.Validate("demo", Json("""{"command":"inert","exposure":"direct"}""").Value).Config!, "fixture", McpConfigurationScope.Extension);
                McpProfileRuntimeAdmission baseAdmission = (cwd, generation, actualRegistry, exactPolicy, token) => ValueTask.FromResult(new McpSessionRuntimeAdmission(
                    actualRegistry, new NativeOwner(), f.ServerRegistry, exactPolicy, new([entry], []),
                    [new("demo", actual => Check(ReferenceEquals(actual, entry)), (actual, current, cancellation) =>
                        McpPreOpenServerCapture.AcquireAsync(actual, f.ServerRegistry, serverScope, current, exactPolicy,
                            (_, _, _) => ValueTask.FromResult(true), (_, _) => ValueTask.FromResult<IMcpAdmittedRequestChannel>(f.Channel),
                            new(generation, "0.99.1"), (registry, binding) => binding.PreparedHooks ?? registry.PreparedToolHooks, cancellation))],
                    false, (plan, current) =>
                    {
                        f.DiscoveryTools = current.RegisteredTools.Count(tool => new[] { McpResourceTools.ListResources, McpResourceTools.ListTemplates, McpResourceTools.ReadResource }.Contains(tool.Adapter.Name));
                        Check(plan.ResourceToolsExposure == McpExposure.Direct && f.DiscoveryTools == (optedIn ? 3 : 0));
                        return new(current, []);
                    }));
                McpProfileRuntimeAdmission admission = optedIn ? McpProfileResourceAdmissions.WithResources(baseAdmission,
                    (request, token) =>
                    {
                        f.Registration = new(f.ResourceRegistry, resourceScope, request.ExactPolicy, (_, _, _) => ValueTask.FromResult(true),
                            (current, binding) => binding.PreparedHooks ?? current.PreparedToolHooks,
                            (data, extension, identity, cancellation) =>
                            {
                                f.Saves++;
                                return f.SaveOverride is { } saver ? saver(data, extension, identity, cancellation) : ValueTask.FromResult("synthetic/resource" + extension);
                            });
                        return ValueTask.FromResult(new McpProfileResourceAdmissionResult(request, f.Registration, f.ResourceRegistry));
                    }) : baseAdmission;
                var factory = new McpSessionRuntimeFactory((cwd, generation, token) => admission(cwd, generation, baseRegistry, f.Policy, token));
                var backend = new SessionStorageBackend(Path.Combine(Path.GetTempPath(), "resource-factory-" + Guid.NewGuid().ToString("N")), SessionStorageMode.InMemory);
                if (actualProfile)
                {
                    f.Workspace = Path.Combine(Path.GetTempPath(), "profile-resource-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(f.Workspace);
                    f.Profile = await OfflineSessionProfile.CreateAsync(f.Workspace, Path.Combine(f.Workspace, "profile.jsonl"),
                        null, [], [], [], CancellationToken.None, mcpAdmission: admission);
                    var actualBackend = new SessionStorageBackend(f.Workspace, SessionStorageMode.InMemory);
                    var sequence = 0;
                    var actualLifecycle = f.Profile.CreateLifecycle(() => 1, () => "profile-" + ++sequence, backend: actualBackend);
                    var actualHeader = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3,
                        id = "profile-resource", timestamp = "2026-10-05T00:00:00.000Z", cwd = f.Workspace }));
                    var actualSession = await actualLifecycle.CreateAsync(Path.Combine(f.Workspace, "profile.jsonl"), actualHeader, f.Profile.SelectedModel);
                    try { await f.Profile.AttachOwnerAsync(actualSession, lifecycle: actualLifecycle); }
                    catch { await actualSession.DisposeAsync(); throw; }
                    f.Owner = f.Profile.Sessions!;
                    await f.Profile.ApplyInitialToolSelectionAsync(actualSession, CancellationToken.None);
                    return f;
                }
                var ids = 0; var lifecycle = new PersistentSessionLifecycle(baseRegistry, () => 1, () => "resource-" + ++ids, backend: backend, runtimeForAttachment: factory.AcquireAsync);
                var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "resources", timestamp = "2026-10-05T00:00:00.000Z", cwd = backend.Directory }));
                var session = await lifecycle.CreateAsync(Path.Combine(backend.Directory, "resources.jsonl"), header, Model);
                try { f.Owner = await lifecycle.AttachAsync(session); }
                catch { await session.DisposeAsync(); throw; }
                // Ordinary registration does not activate tools. Select the actual bound catalog
                // through the existing guarded, acknowledged session API before exercising calls.
                if (optedIn)
                {
                    var admitted = session.CaptureToolCatalogRegistry();
                    Check(admitted.RegisteredTools.Length == ResourceNames.Length && ResourceNames.All(name =>
                        admitted.RegisteredTools.Count(tool => tool.Adapter.Name == name) == 1));
                    await session.SetActiveToolsAsync(ResourceNames, CancellationToken.None);
                    Check(session.GetActiveTools().SequenceEqual(ResourceNames) &&
                        admitted.Resolve(session.Snapshot.Context, Model).Configuration.Tools.Select(tool => tool.Name).SequenceEqual(ResourceNames));
                }
                else Check(session.GetActiveTools().IsEmpty);
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            try { if (Owner is not null) await Owner.DisposeAsync(); }
            finally
            {
                try { if (Profile is not null) await Profile.DisposeAsync(); }
                finally { try { await ServerRegistry.DisposeAsync(); } finally { await ResourceRegistry.DisposeAsync(); } }
                if (Workspace is not null) Directory.Delete(Workspace, recursive: true);
            }
        }
    }
    private sealed class NativeOwner : IAsyncDisposable
    { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }

    private sealed class ClosingOwner(Func<ValueTask> dispose) : IAsyncDisposable
    {
        internal int Calls; internal readonly TaskCompletionSource Entered = Gate();
        public ValueTask DisposeAsync() { Calls++; Entered.TrySetResult(); return dispose(); }
    }
    private static McpSessionRuntimeAdmission EmptyAdmission(SessionRuntimeRegistry registry, IToolActionPolicy policy,
        IAsyncDisposable native, IAsyncDisposable discovery) => new(registry, native, discovery, policy,
            new McpServerCatalog([], []), [], false, (_, current) => new(current, []));
    private static IEnumerable<Exception> Walk(Exception error)
    {
        yield return error;
        if (error is AggregateException aggregate)
            foreach (var child in aggregate.InnerExceptions) foreach (var item in Walk(child)) yield return item;
        else if (error.InnerException is { } child)
            foreach (var item in Walk(child)) yield return item;
    }
    private static async Task<Exception> Failure(Task original)
    { try { await original; } catch (Exception error) { return error; } throw new IOException("Expected failure"); }
    private static async Task HeldRollback()
    {
        var policy = new Policy(); var registry = new SessionRuntimeRegistry([new(Model, new Transport())], [], policy);
        var nativeTask = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var discoveryTask = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var native = new ClosingOwner(() => new(nativeTask.Task)); var discovery = new ClosingOwner(() => new(discoveryTask.Task));
        var resourceTask = new TaskCompletionSource<McpProfileResourceAdmissionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var admission = McpProfileResourceAdmissions.WithResources((_, _, actual, exact, _) =>
            ValueTask.FromResult(EmptyAdmission(actual, exact, native, discovery)), (_, _) => new(resourceTask.Task));
        var first = new IOException("resource first"); var second = new IOException("resource second");
        resourceTask.SetException([first, second]);
        var work = admission(Path.GetTempPath(), 1, registry, policy, default).AsTask();
        await discovery.Entered.Task;
        Exception? assertion = null;
        try { Check(!work.IsCompleted && native.Calls == 0); } catch (Exception assertionError) { assertion = assertionError; }
        var discoveryFirst = new IOException("discovery first"); var discoverySecond = new IOException("discovery second");
        discoveryTask.SetException([discoveryFirst, discoverySecond]); await native.Entered.Task;
        try { Check(!work.IsCompleted); } catch (Exception assertionError) { assertion ??= assertionError; }
        var nativeFirst = new IOException("native first"); var nativeSecond = new IOException("native second");
        nativeTask.SetException([nativeFirst, nativeSecond]);
        var error = await Failure(work); var all = Walk(error).ToArray();
        Check(new Exception[] { first, second, discoveryFirst, discoverySecond, nativeFirst, nativeSecond }
            .All(expected => all.Any(actual => ReferenceEquals(actual, expected))));
        Check(all.OfType<McpProfileResourceAcquisitionException>().Single().Original == resourceTask.Task);
        Check(all.OfType<McpFactoryDisposalException>().Select(item => item.Original).SequenceEqual(new[] { discoveryTask.Task, nativeTask.Task }));
        Check(native.Calls == 1 && discovery.Calls == 1 && work.IsFaulted);
        if (assertion is not null) throw assertion;
    }
    private static async Task Alias()
    {
        var policy = new Policy(); var registry = new SessionRuntimeRegistry([new(Model, new Transport())], [], policy);
        var owner = new ClosingOwner(() => ValueTask.CompletedTask); var effects = 0;
        var admission = McpProfileResourceAdmissions.WithResources((_, _, actual, exact, _) =>
            ValueTask.FromResult(EmptyAdmission(actual, exact, owner, owner)), (_, _) =>
            { effects++; throw new IOException("Must not enter resource callback"); });
        await Failure(admission(Path.GetTempPath(), 1, registry, policy, default).AsTask());
        Check(effects == 0 && owner.Calls == 1);
    }
    private static async Task ForeignRequest()
    {
        var policy = new Policy(); var registry = new SessionRuntimeRegistry([new(Model, new Transport())], [], policy);
        await using var extensions = new ExtensionRegistry(); var scope = await extensions.ActivateAsync("foreign-request", new EmptyExtension());
        var native = new ClosingOwner(() => ValueTask.CompletedTask); var discovery = new ClosingOwner(() => ValueTask.CompletedTask);
        var resources = new ClosingOwner(() => extensions.DisposeAsync()); var effects = 0;
        var registration = new McpAdmittedResourceRegistration(extensions, scope, policy,
            (_, _, _) => { effects++; return ValueTask.FromResult(true); },
            (current, _) => { effects++; return current.PreparedToolHooks; },
            (_, _, _, _) => { effects++; return ValueTask.FromResult("inert"); });
        var admission = McpProfileResourceAdmissions.WithResources((_, _, actual, exact, _) =>
            ValueTask.FromResult(EmptyAdmission(actual, exact, native, discovery)), (request, _) =>
            ValueTask.FromResult(new McpProfileResourceAdmissionResult(request with { }, registration, resources)));
        var error = await Failure(admission(Path.GetTempPath(), 1, registry, policy, default).AsTask());
        Check(error is InvalidOperationException && effects == 0 && resources.Calls == 1 && native.Calls == 1 && discovery.Calls == 1);
    }
    private static async Task StableClose()
    {
        var policy = new Policy(); var registry = new SessionRuntimeRegistry([new(Model, new Transport())], [], policy);
        await using var extensions = new ExtensionRegistry(); var scope = await extensions.ActivateAsync("stable-resource", new EmptyExtension());
        var native = new ClosingOwner(() => ValueTask.CompletedTask); var discovery = new ClosingOwner(() => ValueTask.CompletedTask);
        var released = Gate();
        var resources = new ClosingOwner(async () => { await released.Task; await extensions.DisposeAsync(); });
        var registration = new McpAdmittedResourceRegistration(extensions, scope, policy, (_, _, _) => ValueTask.FromResult(true),
            (current, _) => current.PreparedToolHooks, (_, _, _, _) => ValueTask.FromResult("inert"));
        var admission = McpProfileResourceAdmissions.WithResources((_, _, actual, exact, _) =>
            ValueTask.FromResult(EmptyAdmission(actual, exact, native, discovery)), (request, _) =>
            ValueTask.FromResult(new McpProfileResourceAdmissionResult(request, registration, resources)));
        var acquired = await admission(Path.GetTempPath(), 1, registry, policy, default);
        var first = acquired.NativeResources.DisposeAsync().AsTask(); var second = acquired.NativeResources.DisposeAsync().AsTask();
        await resources.Entered.Task;
        Exception? failure = null;
        try { Check(ReferenceEquals(first, second) && !first.IsCompleted && native.Calls == 0); }
        catch (Exception error) { failure = error; }
        finally { released.TrySetResult(); }
        await first; await acquired.DiscoveryResources.DisposeAsync();
        Check(resources.Calls == 1 && native.Calls == 1 && discovery.Calls == 1);
        if (failure is not null) throw failure;
    }
    private static async Task AcquisitionStates()
    {
        foreach (var baseFailure in new[] { false, true })
            foreach (var kind in new[] { "sync", "fault", "cancel" })
            {
                var policy = new Policy(); var registry = new SessionRuntimeRegistry([new(Model, new Transport())], [], policy);
                var native = new ClosingOwner(() => ValueTask.CompletedTask); var discovery = new ClosingOwner(() => ValueTask.CompletedTask);
                var cancellation = new OperationCanceledException("original OCE");
                var baseTask = new TaskCompletionSource<McpSessionRuntimeAdmission>(TaskCreationOptions.RunContinuationsAsynchronously);
                var resourceTask = new TaskCompletionSource<McpProfileResourceAdmissionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (kind != "sync")
                {
                    if (baseFailure) { if (kind == "cancel") baseTask.SetCanceled(); else baseTask.SetException(cancellation); }
                    else { if (kind == "cancel") resourceTask.SetCanceled(); else resourceTask.SetException(cancellation); }
                }
                var admission = McpProfileResourceAdmissions.WithResources((_, _, actual, exact, _) => !baseFailure
                    ? ValueTask.FromResult(EmptyAdmission(actual, exact, native, discovery))
                    : kind == "sync" ? throw cancellation : new(baseTask.Task), (_, _) =>
                    kind == "sync" ? throw cancellation : new(resourceTask.Task));
                var original = admission(Path.GetTempPath(), 1, registry, policy, default).AsTask();
                var error = await Failure(original);
                if (kind == "cancel")
                {
                    Check(error is McpProfileResourceAcquisitionCancellation canceled &&
                        ReferenceEquals(canceled.Original, baseFailure ? baseTask.Task : resourceTask.Task) && original.IsCanceled);
                }
                else Check(error is McpProfileResourceAcquisitionException fault && original.IsFaulted &&
                    (kind == "sync" ? fault.Original is null && ReferenceEquals(fault.InnerException, cancellation)
                        : ReferenceEquals(fault.Original, baseFailure ? baseTask.Task : resourceTask.Task) &&
                          Walk(fault).Any(item => ReferenceEquals(item, cancellation))));
                Check(native.Calls == (baseFailure ? 0 : 1) && discovery.Calls == (baseFailure ? 0 : 1));
            }
    }

    // e3b profile's fixed target policy denies dynamically admitted MCP extension targets.
    // This reaches the actual profile lifecycle and executor; no synthetic policy bypass.
    private static async Task ProfileCaller()
    {
        await using var f = await Fixture.Create(true, actualProfile: true);
        var session = f.Owner.Current.Session; var model = f.Profile!.SelectedModel;
        var catalog = session.CaptureToolCatalogRegistry();
        Check(catalog.InvocationOwnerGeneration == f.Owner.Current.Generation && f.DiscoveryTools == 3);
        var configuration = catalog.Resolve(session.Snapshot.Context, model).Configuration;
        var call = new ToolCallContent("profile-resource", McpResourceTools.ReadResource, Json("""{"server":"demo","uri":"data://a.bin"}"""));
        var invocation = new ToolInvocation(new AssistantMessage(model.Api, model.Provider, model.Id, 1, [call], TokenUsage.Zero, StopReason.ToolUse), call, 0);
        var result = await configuration.Tools.Single(tool => tool.Name == McpResourceTools.ReadResource).Executor.ExecuteAsync(invocation, default);
        Check(result.IsError && f.Channel.Methods.Count == 0 && f.Saves == 0);
        await f.Profile.DisposeAsync(); Check(f.Channel.Closes == 1);
    }

    private static async Task Pipeline()
    {
        await using var f = await Fixture.Create(true); var session = f.Owner.Current.Session;
        var configuration = session.CaptureToolCatalogRegistry().Resolve(session.Snapshot.Context, Model).Configuration;
        foreach (var name in new[] { McpResourceTools.ListResources, McpResourceTools.ListTemplates, McpResourceTools.ReadResource })
        {
            var call = new ToolCallContent("factory-resource", name, name == McpResourceTools.ReadResource ? Json("""{"server":"demo","uri":"data://a.bin"}""") : Json("{}"));
            var invocation = new ToolInvocation(new AssistantMessage(Model.Api, Model.Provider, Model.Id, 1, [call], TokenUsage.Zero, StopReason.ToolUse), call, 0);
            var result = await configuration.Tools.Single(tool => tool.Name == name).Executor.ExecuteAsync(invocation, default);
            Check(!result.IsError && f.Channel.Identity is { OwnerId: "factory-resources", ToolCallId: "factory-resource" });
            Check(f.Channel.Identity!.SessionGeneration == f.Owner.Current.Generation);
        }
        Check(f.DiscoveryTools == 3 && f.Policy.Calls == 3 && f.Saves == 1 && f.Channel.Methods.SequenceEqual(new[] { "resources/list", "resources/templates/list", "resources/read" }));
    }
    private static async Task Absent()
    {
        await using var f = await Fixture.Create(false);
        Check(f.DiscoveryTools == 0 && f.Owner.Current.Session.CaptureToolCatalogRegistry().RegisteredTools.IsEmpty && f.Channel.Methods.Count == 0);
    }
    private static async Task HeldOutput()
    {
        var f = await Fixture.Create(true); var entered = Gate(); var saved = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? prompt = null; Task? closing = null; Exception? admissionError = null; Exception? promptSelected = null;
        var failures = new List<Exception>();
        try
        {
            Check(f.Owner.Current.Session.GetActiveTools().SequenceEqual(ResourceNames));
            f.SaveOverride = (_, _, _, _) => { entered.TrySetResult(); return new(saved.Task); };
            prompt = f.Owner.Current.Session.PromptAsync(new TranscriptEntry("user", Json("""{"role":"user","content":"read","timestamp":1}""")));
            await Task.WhenAny(entered.Task, prompt);
            if (!entered.Task.IsCompleted)
            {
                await prompt;
                throw new IOException("Resource prompt completed without entering the admitted output saver.");
            }
            closing = f.Owner.DisposeAsync().AsTask(); await f.Channel.Closed.Task;
            Check(!prompt.IsCompleted && !closing.IsCompleted && f.Saves == 1);
        }
        catch (Exception error) { admissionError = error; }
        finally
        {
            promptSelected = await Settle(prompt, closing, () => saved.TrySetResult("synthetic/held.bin"),
                () => f.Owner.DisposeAsync().AsTask(), f.DisposeAsync, failures);
        }
        if (admissionError is not null && !ReferenceEquals(admissionError, promptSelected)) failures.Insert(0, admissionError);
        ThrowFailures(failures); // All explicit fixture cleanup has already joined; no implicit disposal can replace this inventory.
        Check(f.Channel.Closes == 1 && entered.Task.IsCompletedSuccessfully && saved.Task.IsCompletedSuccessfully);
        await FaultInventoryControl(false); await FaultInventoryControl(true);

        static async Task<Exception?> Settle(Task? originalPrompt, Task? admittedClose, Action release,
            Func<Task> startClose, Func<ValueTask> disposeFixture, List<Exception> all)
        {
            release(); Exception? selectedPrompt = null;
            if (originalPrompt is not null)
            {
                try { await originalPrompt; }
                catch (Exception error)
                {
                    selectedPrompt = error;
                    if (!(admittedClose is not null && originalPrompt.IsCanceled && error is OperationCanceledException))
                        all.Add(new IOException("Resource held-output original prompt failed.", (Exception?)originalPrompt.Exception ?? error));
                }
            }
            Task? closeOriginal = admittedClose;
            if (closeOriginal is null)
                try { closeOriginal = startClose(); }
                catch (Exception error) { all.Add(new IOException("Resource held-output synchronous owner close failed.", error)); }
            if (closeOriginal is not null)
                try { await closeOriginal; }
                catch (Exception error) { all.Add(new IOException("Resource held-output original owner close failed.", (Exception?)closeOriginal.Exception ?? error)); }
            Task? fixtureOriginal = null;
            try { fixtureOriginal = disposeFixture().AsTask(); await fixtureOriginal; }
            catch (Exception error) { all.Add(new IOException("Resource held-output original fixture cleanup failed.", (Exception?)fixtureOriginal?.Exception ?? error)); }
            return selectedPrompt;
        }
        static void ThrowFailures(List<Exception> all)
        {
            if (all.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(all[0]).Throw();
            if (all.Count > 1) throw new AggregateException("Resource held-output originals failed after fixture cleanup joined.", all);
        }
        static async Task FaultInventoryControl(bool singletonOce)
        {
            var originalPrompt = Gate(); var originalClose = Gate(); var originalCleanup = Gate();
            var released = Gate(); var closeEntered = Gate(); var cleanupEntered = Gate();
            var all = new List<Exception>(); var closeCalls = 0; var cleanupCalls = 0;
            var primary = new IOException("held prompt primary"); var promptOce = new OperationCanceledException();
            var closeOne = new OperationCanceledException(); var closeTwo = new IOException("held close sibling");
            var promptInventory = singletonOce ? new Exception[] { promptOce } : new Exception[] { primary, promptOce };
            var settlement = Settle(originalPrompt.Task, null, () => released.TrySetResult(),
                () => { closeCalls++; closeEntered.TrySetResult(); return originalClose.Task; },
                () => { cleanupCalls++; cleanupEntered.TrySetResult(); return new(originalCleanup.Task); }, all);
            var escaping = Finish(); Exception? retained = null;
            try
            {
                await released.Task.WaitAsync(TimeSpan.FromSeconds(10)); Check(!escaping.IsCompleted && closeCalls == 0 && cleanupCalls == 0);
                originalPrompt.TrySetException(promptInventory);
                await closeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)); Check(!escaping.IsCompleted && originalPrompt.Task.IsFaulted && cleanupCalls == 0);
                originalClose.TrySetException(new Exception[] { closeOne, closeTwo });
                await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)); Check(!escaping.IsCompleted && originalClose.Task.IsFaulted && closeCalls == 1 && cleanupCalls == 1);
            }
            finally
            {
                originalPrompt.TrySetException(promptInventory); originalClose.TrySetException(new Exception[] { closeOne, closeTwo });
                // The fixture reawaits the same stable owner fault; that repeated cause must never replace prompt/sibling inventories.
                originalCleanup.TrySetException(closeOne);
                try { await escaping; } catch (AggregateException error) { retained = error; }
            }
            Check(retained is not null && escaping.IsFaulted && originalCleanup.Task.IsFaulted && all.Count == 3);
            var observed = Walk(retained!).ToArray();
            Check(promptInventory.All(expected => observed.Count(actual => ReferenceEquals(actual, expected)) == 1));
            Check(observed.Count(actual => ReferenceEquals(actual, closeOne)) == 2 && observed.Count(actual => ReferenceEquals(actual, closeTwo)) == 1);
            async Task Finish() { await settlement; ThrowFailures(all); }
        }
    }
    private static async Task Multicast()
    {
        await using var registry = new ExtensionRegistry(); var scope = await registry.ActivateAsync("multicast", new EmptyExtension()); var effects = 0;
        McpProfileRuntimeAdmission baseAdmission = (_, _, _, _, _) => { effects++; throw new IOException("Must not acquire"); };
        McpProfileResourceAdmission resourceAdmission = (_, _) => { effects++; throw new IOException("Must not acquire"); };
        for (var index = 0; index < 2; index++)
        {
            try { _ = McpProfileResourceAdmissions.WithResources(index == 0 ? baseAdmission + baseAdmission : baseAdmission,
                index == 1 ? resourceAdmission + resourceAdmission : resourceAdmission); throw new IOException("Multicast bridge admitted"); }
            catch (ArgumentException) { }
        }
        ExtensionToolArgumentValidator validator = (_, _, _) => { effects++; return ValueTask.FromResult(true); };
        McpPreparedHookComposer composer = (current, _) => { effects++; return current.PreparedToolHooks; };
        McpResourceOutputSaver saver = (_, _, _, _) => { effects++; return ValueTask.FromResult("synthetic"); };
        for (var index = 0; index < 3; index++)
        {
            try { _ = new McpAdmittedResourceRegistration(registry, scope, new Policy(), index == 0 ? validator + validator : validator,
                index == 1 ? composer + composer : composer, index == 2 ? saver + saver : saver); throw new IOException("Multicast admitted"); }
            catch (ArgumentException) { }
        }
        Check(effects == 0 && registry.CaptureSnapshot().Tools.IsEmpty);
    }
}
