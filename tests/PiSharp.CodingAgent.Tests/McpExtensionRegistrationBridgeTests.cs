using System.Runtime.CompilerServices;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Cli.Mcp;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Runtime;

internal static class McpExtensionRegistrationBridgeTests
{
    internal const string Prefix = "mcp-extension-host-bridge.";
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        (Prefix + "registration-feeds-genuine-preopen-prepared-tool-pipeline", PreparedPipeline),
        (Prefix + "configured-precedence-foreign-scope-and-joined-retirement", Ownership),
        (Prefix + "substituted-acquisition-catalog-joins-both-resource-originals", WrongCatalog)
    ];
    private static void Require(bool value) { if (!value) throw new IOException("MCP extension host bridge assertion failed."); }
    private static JsonData Config(string command = "inert") => JsonData.Parse("{\"command\":\"" + command + "\",\"exposure\":\"direct\"}");
    private static string ExtensionPath => Path.Combine(Path.GetTempPath(), "admitted-mcp-source-only-extension");
    private static readonly ModelDescriptor Model = new("mcp-host-bridge", "openai-responses", "fixture");
    private static async Task<Exception> Failure(Task original)
    { try { await original; } catch (Exception error) { return error; } throw new IOException("Expected failure."); }
    private static SessionRuntimeRegistry Runtime(Policy policy) => new([new(Model, new NoTransport())], [], policy,
        new SessionRuntimeRegistryOptions { BindNestedCallsToSessionOwner = true });
    private sealed class EmptyExtension : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Resource(Func<Task> close) : IAsyncDisposable
    { private Task? original; public ValueTask DisposeAsync() => new(original ??= close()); }
    private sealed class Policy : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(new ToolActionAuthorization(false)); }
    private sealed class NoTransport : IChatTransport
    {
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        { await Task.FromException(new IOException("Provider is not admitted in this fixture.")); yield break; }
    }
    private sealed class Channel : IMcpAdmittedRequestChannel
    {
        internal int InitializeCalls, ListCalls, Closes;
        private Task? close;
        public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask NotifyAsync(string method, JsonData? parameters, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<JsonData> RequestAsync(string method, JsonData? parameters, McpRequestOptions options, CancellationToken token)
        {
            if (method == "initialize") { InitializeCalls++; return ValueTask.FromResult(JsonData.Parse("{\"protocolVersion\":\"2025-11-25\",\"serverInfo\":{\"name\":\"synthetic\",\"version\":\"1\"},\"capabilities\":{\"tools\":{}}}")); }
            if (method == "tools/list") { ListCalls++; return ValueTask.FromResult(JsonData.Parse("{\"tools\":[{\"name\":\"echo\",\"description\":\"inert\",\"inputSchema\":{\"type\":\"object\"}}]}")); }
            throw new IOException("No remote tool execution admitted.");
        }
        public Task CloseAsync() { if (close is not null) return close; Closes++; return close = Task.CompletedTask; }
    }
    private static async Task PreparedPipeline()
    {
        await using var extensions = new ExtensionRegistry(); await using var owner = await extensions.ActivateAsync("metadata-owner", new EmptyExtension());
        var bridge = new McpExtensionRegistrationBridge(extensions, McpConfigurationReader.Load(null, null, true));
        var facade = bridge.BindOwner(owner, ExtensionPath); facade.RegisterMcpServer("demo", Config());
        var published = bridge.CaptureSnapshot(); Require(published.Revision == 1 && published.Catalog.Servers.Single().Name == "demo");
        var policy = new Policy(); var channel = new Channel(); var fresh = new ExtensionRegistry(); var nativeReleased = 0; var discoveryReleased = 0;
        SessionRuntimeLease? lease = null; Exception? primary = null;
        try
        {
            var factory = bridge.CreateRuntimeFactory(async (cwd, generation, catalog, token) =>
            {
                Require(ReferenceEquals(catalog, published.Catalog));
                var scope = await fresh.ActivateAsync("captured-server", new EmptyExtension(), token);
                return new(Runtime(policy), new Resource(async () => { nativeReleased++; await fresh.DisposeAsync(); }),
                    new Resource(() => { discoveryReleased++; return Task.CompletedTask; }), policy, catalog,
                    [new("demo", entry => Require(ReferenceEquals(entry, catalog.Servers[0])), (entry, current, cancellation) =>
                        McpPreOpenServerCapture.AcquireAsync(entry, fresh, scope, current, policy,
                            (name, arguments, validationToken) => ValueTask.FromResult(true),
                            (configured, acquireToken) => ValueTask.FromResult<IMcpAdmittedRequestChannel>(channel),
                            new(generation, "0.99.1"), (registry, binding) => binding.PreparedHooks ?? registry.PreparedToolHooks, cancellation))],
                    false, (plan, current) => new(current, []));
            });
            lease = await factory.AcquireAsync(Path.GetTempPath(), 7);
            Require(channel.InitializeCalls == 1 && channel.ListCalls == 1 && fresh.CaptureSnapshot().Tools.Length == 1 &&
                lease.Registry.RegisteredTools.Count() == 1 && lease.Registry.UsesFinalActionPolicy(policy));
        }
        catch (Exception error) { primary = error; }
        finally
        {
            var errors = new List<Exception>(); if (primary is not null) errors.Add(primary);
            if (lease is not null) await McpSessionRuntimeFactory.JoinDisposalAsync(lease, "test runtime lease", errors);
            else await McpSessionRuntimeFactory.JoinDisposalAsync(fresh, "test untransferred registry", errors);
            McpSessionRuntimeFactory.Rethrow(errors);
        }
        Require(channel.Closes == 1 && nativeReleased == 1 && discoveryReleased == 1);
    }
    private static async Task Ownership()
    {
        await using var registry = new ExtensionRegistry(); await using var owner = await registry.ActivateAsync("actual-owner", new EmptyExtension());
        await using var foreignRegistry = new ExtensionRegistry(); await using var foreign = await foreignRegistry.ActivateAsync("actual-owner", new EmptyExtension());
        var configured = McpConfigurationReader.Load(new("global", "{\"mcpServers\":{\"shared\":{\"command\":\"configured\"}}}"), null, true);
        var bridge = new McpExtensionRegistrationBridge(registry, configured);
        var failure = await Failure(Task.Run(() => bridge.BindOwner(foreign, ExtensionPath)));
        Require(failure is ExtensionRegistrationException && bridge.CaptureSnapshot().Revision == 0);
        var facade = bridge.BindOwner(owner, ExtensionPath); facade.RegisterMcpServer("shared", Config("extension"));
        Require(bridge.CaptureSnapshot().Catalog.Servers.Single().Config.Raw.Value.GetProperty("command").GetString() == "configured");
        var disposal = owner.DisposeAsync().AsTask(); await bridge.RetireClosedOwnerAsync(owner, disposal);
        Require(disposal.IsCompletedSuccessfully && bridge.CaptureSnapshot().RegisteredServers.IsEmpty && bridge.CaptureSnapshot().Catalog.Servers.Single().Name == "shared");
        _ = await Failure(Task.Run(() => facade.GetMcpServers()));
    }
    private static async Task WrongCatalog()
    {
        await using var registry = new ExtensionRegistry(); var bridge = new McpExtensionRegistrationBridge(registry, McpConfigurationReader.Load(null, null, true));
        var policy = new Policy(); var released = new List<string>();
        var factory = bridge.CreateRuntimeFactory((cwd, generation, catalog, token) => ValueTask.FromResult(new McpSessionRuntimeAdmission(
            Runtime(policy), new Resource(() => { released.Add("native"); return Task.CompletedTask; }),
            new Resource(() => { released.Add("discovery"); return Task.CompletedTask; }), policy, new([], []), [], false, (plan, current) => new(current, []))));
        var failure = await Failure(factory.AcquireAsync(Path.GetTempPath(), 3).AsTask());
        Require(failure is InvalidOperationException && string.Join(',', released) == "discovery,native");
    }
}
