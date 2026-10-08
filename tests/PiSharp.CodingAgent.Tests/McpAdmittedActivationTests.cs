using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Cli.Mcp;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Agent;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Discovery;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Runtime;
using PiSharp.Sessions.Serialization;

internal static class McpAdmittedActivationTests
{
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("mcp-activation. schema object identity rejects same-name parsed impostor", SchemaIdentity),
        ("mcp-activation. semantic batch is inert affine and bound to actual adapters", SemanticReceipt),
        ("mcp-activation. unrelated catalog drift and replaced ids invalidate semantic receipt", StaleReceipt),
        ("mcp-activation. required implementations reject absent opaque receipts", MissingIdentity),
        ("mcp-activation. every enabled admission validates before first acquisition", AdmissionOrder),
        ("mcp-activation. empty ownership transfers once and repeats original cleanup", EmptyTransfer),
        ("mcp-activation. failed later acquisition joins held earlier cleanup and both originals", HeldCleanup),
        ("mcp-activation. semantic execution is fenced until actual owner bind and final policy", BoundExecution)
    ];
    private static readonly ModelDescriptor Model = new("mcp-activation", "openai-responses", "fixture");
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("MCP activation assertion failed."); }
    private static void Reject(Action action)
    { try { action(); } catch (InvalidOperationException) { return; } throw new InvalidOperationException("Unexpected activation admission."); }
    private static async Task<Exception> Failure(Task task)
    { try { await task; } catch (Exception error) { return error; } throw new InvalidOperationException("Expected activation failure."); }
    private static McpToolCatalogPlan Required(bool code = true, bool search = true) =>
        new([], ImmutableDictionary<string, string>.Empty, code, search, true, null);
    private static SessionRuntimeRegistry Base(Policy policy) => new([new(Model, new NoTransport())], [], policy);
    private static McpServerEntry Entry(string name, bool enabled = true)
    {
        var validation = McpConfigurationReader.Validate(name, JsonData.Parse(
            "{\"command\":\"fixture-only\",\"exposure\":\"direct\",\"enabled\":" + (enabled ? "true" : "false") + "}").Value);
        return new(name, validation.Config ?? throw new InvalidOperationException(validation.Error), "synthetic", McpConfigurationScope.Global);
    }
    private static Task SchemaIdentity()
    {
        Check(McpDiscoveryToolIdentity.IsCodemodeTool("codemode", McpDiscoveryToolIdentity.CodemodeSchema));
        Check(!McpDiscoveryToolIdentity.IsCodemodeTool("codemode", JsonData.Parse(McpDiscoveryToolIdentity.CodemodeSchema.ToString())));
        Check(!McpDiscoveryToolIdentity.IsToolSearchTool("codemode", McpDiscoveryToolIdentity.ToolSearchSchema));
        Check(McpDiscoveryToolIdentity.ToolSearchSchema.Value.GetProperty("properties").GetProperty("limit").GetProperty("type").GetString() == "number");
        return Task.CompletedTask;
    }
    private static async Task SemanticReceipt()
    {
        await using var fixture = await SemanticFixture.Create();
        var identity = McpPreparedDiscoveryIdentity.Capture(fixture.Capability, fixture.Binding, fixture.Catalog, fixture.Policy, 1);
        McpPreparedDiscoveryIdentity.ValidateRequired(Required(search: false), fixture.Catalog, [identity]);
        Check(fixture.Executions == 0);
        Reject(() => McpAdmittedDiscoveryRegistration.Register(fixture.Registry, fixture.Scope, [fixture.Definition]));
        var impostor = fixture.Catalog.WithToolCatalog([new(fixture.Binding.RegisteredToolDeclarations[0], new Impostor("codemode"))
            { Exposure = ToolExposure.ModelOnly }], null);
        Reject(() => McpPreparedDiscoveryIdentity.Capture(fixture.Capability, fixture.Binding, impostor, fixture.Policy, 1));
        Reject(() => McpPreparedDiscoveryIdentity.ValidateRequired(Required(search: false), impostor, [identity]));
        var wrongPolicy = new Policy();
        Reject(() => McpPreparedDiscoveryIdentity.Capture(fixture.Capability, fixture.Binding, fixture.Catalog, wrongPolicy, 1));
    }
    private static async Task StaleReceipt()
    {
        await using var fixture = await SemanticFixture.Create();
        var identity = McpPreparedDiscoveryIdentity.Capture(fixture.Capability, fixture.Binding, fixture.Catalog, fixture.Policy, 1);
        var before = fixture.Registry.CaptureSnapshot();
        fixture.Registry.PrepareToolCatalogReplacement(fixture.Scope, ["semantic-code"],
            [McpDiscoveryToolIdentity.CreateCodemode("semantic-code", "ordinary impostor", (data, context, token) => ValueTask.FromResult(data))], before).Commit();
        Reject(() => McpPreparedDiscoveryIdentity.ValidateRequired(Required(search: false), fixture.Catalog, [identity]));
        var replacement = new ExtensionAgentBinding(fixture.Registry, fixture.Policy, ValidateArguments);
        Reject(() => McpPreparedDiscoveryIdentity.Capture(fixture.Capability, replacement, fixture.Catalog, fixture.Policy, 1));
        Check(fixture.Executions == 0);
    }
    private static Task MissingIdentity()
    {
        var catalog = Base(new Policy());
        Reject(() => McpPreparedDiscoveryIdentity.ValidateRequired(Required(), catalog, []));
        McpPreparedDiscoveryIdentity.ValidateRequired(Required(false, false), catalog, []);
        return Task.CompletedTask;
    }
    private static async Task AdmissionOrder()
    {
        var policy = new Policy(); var entries = new McpServerCatalog([Entry("one"), Entry("two")], []); var acquires = 0; var validations = 0;
        var original = new IOException("validator-original");
        Task<McpPreOpenServerCapture> Acquire(McpServerEntry entry, SessionRuntimeRegistry current, CancellationToken token)
        { acquires++; throw new InvalidOperationException("Factory ran before all admissions validated."); }
        var failed = McpAdmittedActivationHost.AcquireAsync(entries,
            [new("one", entry => validations++, Acquire), new("two", entry => throw original, Acquire)],
            Base(policy), true, policy, (plan, current) => new(current, []));
        Check(ReferenceEquals(await Failure(failed), original) && validations == 1 && acquires == 0);
        var missing = McpAdmittedActivationHost.AcquireAsync(entries, [], Base(policy), true, policy, (plan, current) => new(current, []));
        _ = await Failure(missing); Check(acquires == 0);
        await using var disabled = await McpAdmittedActivationHost.AcquireAsync(new([Entry("disabled", false)], []), [], Base(policy), true,
            policy, (plan, current) => new(current, []));
    }
    private static async Task EmptyTransfer()
    {
        var policy = new Policy(); var baseline = Base(policy);
        var host = await McpAdmittedActivationHost.AcquireAsync(new([], []), [], baseline, true, policy, (plan, current) => new(current, []));
        var lease = host.TransferRuntimeOwnership();
        try { Check(ReferenceEquals(lease.Registry, baseline)); Reject(() => host.TransferRuntimeOwnership()); }
        finally { await lease.DisposeAsync(); await host.DisposeAsync(); }
        Check(ReferenceEquals(host.DisposeAsync().AsTask(), host.DisposeAsync().AsTask()));
    }
    private static async Task HeldCleanup()
    {
        var policy = new Policy(); await using var registry = new ExtensionRegistry();
        var scope = await registry.ActivateAsync("first-server", new EmptyExtension());
        var channel = new EmptyChannel(); var original = new IOException("later-acquire-original");
        var cleanup = new IOException("earlier-close-original"); channel.CloseFault = cleanup;
        var acquire = McpAdmittedActivationHost.AcquireAsync(new([Entry("first"), Entry("later")], []),
            [new("first", entry => { }, (entry, current, token) => McpPreOpenServerCapture.AcquireAsync(entry, registry, scope, current,
                policy, ValidateArguments, (server, cancellation) => ValueTask.FromResult<IMcpAdmittedRequestChannel>(channel),
                new(1, "synthetic"), (catalog, binding) => binding.PreparedHooks, token)),
             new("later", entry => { }, (entry, current, token) => Task.FromException<McpPreOpenServerCapture>(original))],
            Base(policy), true, policy, (plan, current) => new(current, []));
        Exception? failure = null;
        try { await channel.CloseEntered.Task; Check(!acquire.IsCompleted); }
        finally { channel.CloseRelease.TrySetResult(); failure = await Failure(acquire); }
        Check(failure is AggregateException aggregate && aggregate.Flatten().InnerExceptions.Any(error => ReferenceEquals(error, original)) &&
            aggregate.Flatten().InnerExceptions.Any(error => ReferenceEquals(error, cleanup)) && channel.Closes == 1);
    }
    private static async Task BoundExecution()
    {
        await using var fixture = await SemanticFixture.Create();
        var identity = McpPreparedDiscoveryIdentity.Capture(fixture.Capability, fixture.Binding, fixture.Catalog, fixture.Policy, 1);
        var invoker = ToolInvoker.WithNestedCalls(fixture.Binding.Adapters, fixture.Policy, new(1), fixture.Binding.PreparedHooks);
        var call = new ToolCallContent("code-native", "codemode", JsonData.Parse("{\"code\":\"return 1\"}"));
        var invocation = new ToolInvocation(new AssistantMessage(Model.Api, Model.Provider, Model.Id, 0, [call], TokenUsage.Zero, StopReason.ToolUse), call, 0);
        var unbound = await invoker.ExecuteAsync(invocation, default); Check(unbound.IsError && fixture.Executions == 0);
        var host = await McpAdmittedActivationHost.AcquireAsync(new([], []), [], fixture.Catalog, true, fixture.Policy,
            (plan, current) => new(current, [identity]));
        var lease = host.TransferRuntimeOwnership(); ReplaceableAgentSession? owner = null; Task<ToolResult>? running = null;
        var path = Path.Combine(Path.GetTempPath(), "PiSharp-discovery-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var failures = new List<Exception>();
        try
        {
            var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "discovery",
                timestamp = "2026-10-05T00:00:00.000Z", cwd = Path.GetTempPath() }));
            var session = await PersistentAgentSession.CreateAsync(path, header, lease.Registry, Model, () => 1, () => Guid.NewGuid().ToString("N"));
            owner = new(session, (request, token) => throw new InvalidOperationException("No replacement factory is admitted."));
            host.BindOwner(owner, owner.Current);
            fixture.Policy.Allowed = false; var denied = await invoker.ExecuteAsync(invocation, default); Check(denied.IsError && fixture.Executions == 0);
            fixture.Policy.Allowed = true; running = invoker.ExecuteAsync(invocation, default).AsTask();
            await fixture.Entered.Task; Check(!running.IsCompleted && fixture.Executions == 1);
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            fixture.Release.TrySetResult();
            if (running is not null) try { Check(!(await running).IsError); } catch (Exception error) { failures.Add(error); }
            if (owner is not null) try { await owner.DisposeAsync(); } catch (Exception error) { failures.Add(error); }
            try { await lease.DisposeAsync(); } catch (Exception error) { failures.Add(error); }
            try { await host.DisposeAsync(); } catch (Exception error) { failures.Add(error); }
            var full = Path.GetFullPath(path);
            if (Path.GetDirectoryName(full) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) ||
                !Path.GetFileName(full).StartsWith("PiSharp-discovery-", StringComparison.Ordinal)) throw new InvalidOperationException("Invalid fixture path.");
            if (File.Exists(full)) File.Delete(full);
        }
        if (failures.Count != 0) throw new AggregateException(failures);
    }
    private static ValueTask<bool> ValidateArguments(ExtensionToolRegistrationInfo tool, JsonData arguments, CancellationToken token)
    {
        var key = tool.Name == "codemode" ? "code" : "query";
        return ValueTask.FromResult(arguments.Value.ValueKind == JsonValueKind.Object && arguments.Value.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String);
    }
    private sealed class SemanticFixture : IAsyncDisposable
    {
        internal readonly ExtensionRegistry Registry = new(); internal readonly Policy Policy = new();
        internal readonly TaskCompletionSource Entered = Gate(), Release = Gate(); internal int Executions;
        internal RegistrationScope Scope = null!; internal McpDiscoveryExecutableDefinition Definition = null!;
        internal McpAdmittedDiscoveryRegistration Capability = null!; internal ExtensionAgentBinding Binding = null!; internal SessionRuntimeRegistry Catalog = null!;
        internal static async Task<SemanticFixture> Create()
        {
            var fixture = new SemanticFixture();
            try
            {
                fixture.Scope = await fixture.Registry.ActivateAsync("semantic-owner", new EmptyExtension());
                fixture.Definition = McpDiscoveryExecutableDefinition.CreateCodemode("semantic-code", "Admitted fixture executor", async (code, invocation, token) =>
                { Check(code == "return 1" && invocation.SessionGeneration == 1); fixture.Executions++; fixture.Entered.TrySetResult(); await fixture.Release.Task;
                    return JsonData.Parse("{\"content\":[{\"type\":\"text\",\"text\":\"ok\"}]}"); });
                fixture.Capability = McpAdmittedDiscoveryRegistration.Register(fixture.Registry, fixture.Scope, [fixture.Definition]).Single();
                fixture.Binding = new(fixture.Registry, fixture.Policy, ValidateArguments);
                fixture.Catalog = new SessionRuntimeRegistry([new(Model, new NoTransport())], [], fixture.Policy,
                    new SessionRuntimeRegistryOptions { BindNestedCallsToSessionOwner = true }).WithToolCatalog([new(fixture.Binding.RegisteredToolDeclarations[0], fixture.Binding.Adapters[0])
                    { Exposure = ToolExposure.ModelOnly }], fixture.Binding.PreparedHooks);
                return fixture;
            }
            catch { fixture.Release.TrySetResult(); await fixture.DisposeAsync(); throw; }
        }
        public ValueTask DisposeAsync() { Release.TrySetResult(); return Registry.DisposeAsync(); }
    }
    private sealed class EmptyExtension : IPiSharpExtension
    { public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) => ValueTask.CompletedTask; public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    private sealed class Policy : IToolActionPolicy
    { internal bool Allowed = true; public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(new ToolActionAuthorization(Allowed)); }
    private sealed class Impostor(string name) : IPreparedToolAdapter
    {
        public string Name => name;
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token) => throw new InvalidOperationException("Impostor must not execute.");
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token) => throw new InvalidOperationException("Impostor must not execute.");
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token) => throw new InvalidOperationException("Impostor must not execute.");
    }
    private sealed class NoTransport : IChatTransport
    { public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        { await Task.FromException(new InvalidOperationException("No provider execution admitted.")); yield break; } }
    private sealed class EmptyChannel : IMcpAdmittedRequestChannel
    {
        internal readonly TaskCompletionSource CloseEntered = Gate(), CloseRelease = Gate(); internal Exception? CloseFault; internal int Closes;
        private readonly Lazy<Task> close;
        internal EmptyChannel() { close = new(CloseCore, LazyThreadSafetyMode.ExecutionAndPublication); }
        public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask NotifyAsync(string method, JsonData? parameters, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<JsonData> RequestAsync(string method, JsonData? parameters, McpRequestOptions options, CancellationToken token) =>
            ValueTask.FromResult(JsonData.Parse(method == "initialize" ? "{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{\"tools\":{}},\"serverInfo\":{\"name\":\"synthetic\",\"version\":\"1\"}}" :
                method == "tools/list" ? "{\"tools\":[]}" : throw new InvalidOperationException("Unexpected synthetic MCP method.")));
        public Task CloseAsync() => close.Value;
        private async Task CloseCore() { Closes++; CloseEntered.TrySetResult(); await CloseRelease.Task; if (CloseFault is { } fault) throw fault; }
    }
}
