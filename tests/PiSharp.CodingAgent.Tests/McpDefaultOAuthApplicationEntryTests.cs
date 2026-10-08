using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Extensions;
using PiSharp.Cli.Mcp;
using PiSharp.Cli.Mcp.Authentication;
using PiSharp.Extensions.Mcp.Authentication;
using PiSharp.Extensions.Mcp.Transport;
using PiSharp.Extensions.Runtime.Mcp.Transport;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using System.Runtime.CompilerServices;
using System.Net;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Runtime;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class McpDefaultOAuthApplicationEntryTests
{
    // The root qualifier explicitly admits the separately compiled minimal three-artifact plugin.
    internal static Task RunAsync(string publishedFixtureRoot) => Installed(publishedFixtureRoot);
    private static void Require(bool value) { if (!value) throw new IOException("Actual installed MCP profile entry assertion failed."); }
    private sealed class Configuration : IExtensionProviderConfigurationAdapter
    { public ExtensionProviderDefinition Resolve(string name, JsonData configuration) => throw new IOException("No provider registration admitted."); }
    private sealed class EmptyExtension : IPiSharpExtension
    { public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) => ValueTask.CompletedTask; public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    private sealed class Resource(Func<Task> close) : IAsyncDisposable
    { private Task? original; public ValueTask DisposeAsync() => new(original ??= McpApplicationHostOriginals.Track(close(), "actual-installed-resource-disposal")); }
    private sealed class Channel : IMcpAdmittedRequestChannel
    {
        private Task? close; internal int Closes;
        public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask NotifyAsync(string method, JsonData? parameters, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<JsonData> RequestAsync(string method, JsonData? parameters, McpRequestOptions options, CancellationToken token) =>
            ValueTask.FromResult(JsonData.Parse(method == "initialize"
                ? "{\"protocolVersion\":\"2025-11-25\",\"serverInfo\":{\"name\":\"supplied\",\"version\":\"1\"},\"capabilities\":{\"tools\":{}}}"
                : method == "tools/list" ? "{\"tools\":[{\"name\":\"echo\",\"inputSchema\":{\"type\":\"object\",\"properties\":{}}}]}"
                : throw new IOException("No external tool execution admitted.")));
        public Task CloseAsync() => close ??= Closed();
        private Task Closed() { Closes++; return Task.CompletedTask; }
    }
    private static async Task Installed(string publishedFixtureRoot)
    {
        var root = Path.Combine(Path.GetTempPath(), "application-installed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var package = Path.Combine(root, "package"); var snapshots = Path.Combine(root, "snapshots");
        Directory.CreateDirectory(package); Directory.CreateDirectory(snapshots);
        var id = "fixture.application." + Guid.NewGuid().ToString("N"); var log = Path.Combine(root, "entry.log");
        var path = Path.Combine(root, "session.jsonl"); var faults = new List<Exception>(); OfflineSessionProfile? profile = null;
        var shutdown = new List<JsonData>(); var binders = 0; var factories = 0; var nativeCloses = 0; var discoveryCloses = 0;
        var mark = McpApplicationHostOriginals.MarkTracked();
        var stores = new Dictionary<long, OAuthStore>(); var owners = new Dictionary<long, IAsyncDisposable>();
        var reentries = 0; var tokenCalls = 0; var replayed = 0; var rejected = new Dictionary<long, string>();
        var routeEntries = new Dictionary<long, McpServerEntry>(); McpDefaultOAuthApplicationInstallation? authInstalled = null;
        ExtensionRegistry? actualRegistry = null; McpApplicationHostInstallation? installed = null;
        try
        {
            const string stem = "PublishedFixture.ProfileInitializer";
            foreach (var suffix in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
                File.Copy(Path.Combine(publishedFixtureRoot, stem + suffix), Path.Combine(package, stem + suffix));
            using (var deps = JsonDocument.Parse(File.ReadAllText(Path.Combine(package, stem + ".deps.json"))))
                Require(deps.RootElement.GetProperty("libraries").EnumerateObject().All(item => item.Name == stem + "/0.0.1" ||
                    item.Name.StartsWith("PiSharp.Contracts/", StringComparison.Ordinal) || item.Name.StartsWith("PiSharp.Extensions.Abstractions/", StringComparison.Ordinal)));
            var hashes = Directory.GetFiles(package).ToDictionary(file => Path.GetFileName(file), file => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))));
            var manifest = JsonSerializer.Serialize(new { schemaVersion = 0, id, packageVersion = "0.0.1",
                hostApiRange = new { minimum = "0.0.0", maximumExclusive = "0.1.0" }, runtimeKind = "native", assembly = stem + ".dll",
                entryType = "PublishedFixture.ProfileInitializer.Entry", tfm = "net10.0", rids = new[] { "win-x64" },
                requiredFeatures = new[] { "owned-descriptor-callbacks" }, declaredCapabilities = Array.Empty<string>(),
                resourcePaths = hashes.Keys.Where(name => name != stem + ".dll").ToArray(), explicitOverrides = Array.Empty<object>(), artifactHashes = hashes });
            var manifestPath = Path.Combine(root, "manifest.json"); var approvalPath = Path.Combine(root, "approval.json");
            File.WriteAllText(manifestPath, manifest);
            File.WriteAllText(approvalPath, JsonSerializer.Serialize(new { schemaVersion = 1, execution = "ApprovePublishedFixtureExecution",
                packageRoot = package, manifestValueSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))), artifactHashes = hashes,
                sourceScope = "Explicit", effectiveScopeId = "cli-native-explicit", policyRevision = "experimental-policy-0", hostGeneration = 1,
                sessionPath = path, workspace = root, snapshotRoot = snapshots, enabledTools = Array.Empty<string>(), enabledCommands = Array.Empty<string>() }));
            AppContext.SetData("PiSharp.ProfileInitializer." + id, log);
            AppContext.SetData("PiSharp.ProfileInitializer.Shutdown." + id, (Action<JsonData>)(value => shutdown.Add(value)));
            var native = new NativeExtensionInitializerInstallation(registry =>
                new(registry, new ExtensionHostFlagValues(new Dictionary<string, ExtensionFlagValue>()), [], new Configuration()), (_, _) => { });
            var application = new McpApplicationInitializerAdmission(registry =>
            {
                factories++; actualRegistry = registry;
                McpApplicationGenerationAcquisition baseAcquire = McpApplicationHostOriginals.Generation(async (request, token) =>
                {
                    var serverRegistry = new ExtensionRegistry();
                    try
                    {
                        var scope = await McpApplicationHostOriginals.Observe(serverRegistry.ActivateAsync("capture", new EmptyExtension(), token), "oauth-entry-server-activation");
                        return new(request, new Resource(async () => { nativeCloses++; await McpApplicationHostOriginals.Observe(serverRegistry.DisposeAsync().AsTask(), "oauth-entry-server-close"); }),
                            new Resource(() => { discoveryCloses++; return Task.CompletedTask; }),
                            [new(request.Catalog.Servers.Single(), serverRegistry, scope, (_, _, _) => ValueTask.FromResult(true),
                                new(request.Generation, "0.99.1"), (current, binding) => binding.PreparedHooks ?? current.PreparedToolHooks)], (_, current) => new(current, []));
                    }
                    catch (Exception error)
                    { var inventory = new List<Exception> { error }; await McpApplicationHostInstallationTests.Join(serverRegistry.DisposeAsync().AsTask(), inventory); Fail(inventory); throw; }
                });
                authInstalled = new([new("one", entry => Require(entry.Config.Raw.Value.GetProperty("url").GetString() == Endpoint.AbsoluteUri),
                    (request, entry, token) =>
                    {
                        var store = new OAuthStore(); stores.Add(request.Generation, store); routeEntries.Add(request.Generation, entry);
                        McpAdmittedHttpRequestFactory physical = message => new HttpOperation(async ct =>
                        {
                            var text = message.Content is null ? "" : await McpApplicationHostOriginals.Observe(message.Content.ReadAsStringAsync(ct), "oauth-entry-request-body");
                            if (message.RequestUri!.AbsolutePath == "/token")
                            {
                                tokenCalls++; Require(text.Contains("grant_type=refresh_token", StringComparison.Ordinal) && text.Contains("client_id=admitted-client", StringComparison.Ordinal));
                                return Http(200, "{\"access_token\":\"new\",\"token_type\":\"Bearer\",\"refresh_token\":\"refresh\"}");
                            }
                            Require(message.RequestUri == Endpoint);
                            if (message.Headers.Authorization?.Parameter == "old")
                            {
                                rejected[request.Generation] = text; var response = Http(401, ""); response.Headers.Add("WWW-Authenticate", "Bearer error=\"invalid_token\""); return response;
                            }
                            Require(message.Headers.Authorization?.Parameter == "new");
                            if (rejected.TryGetValue(request.Generation, out var first)) { Require(first == text); rejected.Remove(request.Generation); replayed++; }
                            using var rpc = JsonDocument.Parse(text);
                            if (!rpc.RootElement.TryGetProperty("id", out var rpcId)) return Http(202, "");
                            var method = rpc.RootElement.GetProperty("method").GetString();
                            object result = method == "initialize" ? new { protocolVersion = "2025-11-25", serverInfo = new { name = "supplied", version = "1" }, capabilities = new { tools = new { } } }
                                : method == "tools/list" ? (object)new { tools = new[] { new { name = "echo", inputSchema = new { type = "object", properties = new { } } } } }
                                : throw new IOException("No tool execution/network admitted.");
                            return Http(200, JsonSerializer.Serialize(new { jsonrpc = "2.0", id = rpcId.Clone(), result }));
                        });
                        return ValueTask.FromResult(OAuthResources(store, physical) with { AddClientAuthentication = async (proposal, ct) =>
                        {
                            await Task.Yield(); proposal.FormSet("client_id", proposal.Client.ClientId);
                            try { _ = owners[request.Generation].DisposeAsync(); throw new IOException("OAuth callback reentry was not refused."); }
                            catch (InvalidOperationException) { reentries++; }
                        }});
                    }, (entry, generation, authenticated) =>
                    {
                        var binding = new McpHttpBinding(Endpoint, ImmutableDictionary<string, string>.Empty, new(false, false));
                        return (actual, token) =>
                        {
                            Require(ReferenceEquals(entry, actual)); token.ThrowIfCancellationRequested();
                            return ValueTask.FromResult<IMcpAdmittedRequestChannel>(new McpJsonRpcRequestChannel(new McpStreamableHttpTransport(binding, authenticated)));
                        };
                    })], baseAcquire);
                var exactAuth = authInstalled ?? throw new IOException("OAuth installation absent.");
                return installed = new(registry, McpConfigurationReader.Load(null, null, true), exactAuth.ServerAdmissions,
                    async (request, token) =>
                    {
                        var acquired = await McpApplicationHostOriginals.Observe(exactAuth.AcquireGenerationAsync(request, token).AsTask(), "oauth-entry-generation");
                        owners.Add(request.Generation, acquired.NativeResources); return acquired;
                    });
            }, facade => { binders++; facade.RegisterMcpServer("one", JsonData.Parse("{\"url\":\"https://mcp.example.test/mcp\",\"exposure\":\"direct\"}")); });
            var configuration = NativeExtensionConfiguration.Optional(package, manifestPath, approvalPath, snapshots, [])!;
            profile = await McpApplicationHostOriginals.Observe(OfflineSessionProfile.CreateAsync(root, path, null, [], [], [], default,
                extension: configuration, configuredInitializerInstallation: native, applicationMcpHost: application), "installed-profile-create");
            Require(factories == 1 && binders == 1 && actualRegistry is not null && installed is not null &&
                installed.Bridge.IsBoundTo(actualRegistry) && installed.Bridge.CaptureSnapshot().RegisteredServers.Length == 1 && File.ReadAllText(log) == "initialize\n");
            var exactInstallation = installed ?? throw new IOException("No actual installation.");
            var backend = new SessionStorageBackend(root, SessionStorageMode.InMemory); var sequence = 0;
            var lifecycle = profile.CreateLifecycle(() => 0, () => "entry-" + ++sequence, backend: backend);
            var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "installed",
                timestamp = "2026-10-07T00:00:00Z", cwd = root }));
            var session = await McpApplicationHostOriginals.Observe(lifecycle.CreateAsync(path, header, profile.SelectedModel), "installed-session-create");
            await McpApplicationHostOriginals.Observe(profile.AttachOwnerAsync(session, lifecycle: lifecycle), "installed-profile-attach");
            await McpApplicationHostOriginals.Observe(profile.ApplyInitialToolSelectionAsync(session, default), "installed-selection");
            await McpApplicationHostOriginals.Observe(profile.StartLifecycleAsync("new", default).AsTask(), "installed-session-start");
            Require(session.CaptureToolCatalogRegistry().RegisteredTools.Any(tool => tool.Adapter.Name.Contains("echo", StringComparison.Ordinal)));
            var previousEntry = routeEntries[1]; var previousFactory = authInstalled!.ServerAdmissions.Single().CreateChannelFactory(previousEntry, 1);
            var current = await McpApplicationHostOriginals.Observe(profile.RefreshRegisteredMcpRuntimeAsync(profile.Sessions!.Current), "installed-refresh");
            try { _ = previousFactory(previousEntry, default); throw new IOException("Retired generation channel was acquired."); }
            catch (IOException error) when (error.Message.Contains("absent/retired", StringComparison.Ordinal)) { }
            Require(current.Generation == 2 && nativeCloses == 1 && discoveryCloses == 1 && tokenCalls == 2 && replayed == 2 && reentries == 2 && stores.Values.All(store => store.Value.Tokens!.AccessToken == "new" && store.Writes == 2 && store.Value.OAuthState == "entry-state"));
            await McpApplicationHostOriginals.Observe(profile.DisposeAsync().AsTask(), "installed-profile-close");
            Require(nativeCloses == 2 && discoveryCloses == 2 && tokenCalls == 2 && replayed == 2 && reentries == 2 &&
                File.ReadAllText(log) == "initialize\ndispose\n" && shutdown.Count > 0 && backend.ActiveWriterCount == 0 &&
                exactInstallation.Bridge.CaptureSnapshot().RegisteredServers.IsEmpty && Directory.GetFileSystemEntries(snapshots).Length == 0);
        }
        catch (Exception error) { faults.Add(error); }
        finally
        {
            if (profile is not null) await McpApplicationHostInstallationTests.Join(profile.DisposeAsync().AsTask(), faults);
            await McpApplicationHostOriginals.JoinTrackedSince(mark, faults);
            if (authInstalled is not null) Export(authInstalled);
            AppContext.SetData("PiSharp.ProfileInitializer." + id, null); AppContext.SetData("PiSharp.ProfileInitializer.Shutdown." + id, null);
            try { Directory.Delete(root, true); } catch (Exception error) { faults.Add(error); }
        }
        Fail(faults);
    }
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases(string publishedFixtureRoot) =>
    [
        ("default OAuth application rejects reused INNER native/discovery owners without closing prior generation", ReusedInnerOwners),
        ("default OAuth application adopts late returned owners before cancellation and preserves cleanup siblings", LateResources),
        ("default OAuth application retires map before held channel joins and rejects callback close reentry", LateChannel),
        ("default OAuth actual profile initializer captures authenticated HTTP replay and generation replacement", () => Installed(publishedFixtureRoot))
    ];
    private static readonly object captureGate = new();
    private static readonly List<(string Phase, Task Original, AggregateException? Aggregate, Exception? Direct)> captured = [];
    internal static (string Phase, Task Original, AggregateException? Aggregate, Exception? Direct)[] CapturedOriginals
    { get { lock (captureGate) return captured.Concat(McpApplicationHostOriginals.Capture()).ToArray(); } }
    private static void Export(McpDefaultOAuthApplicationInstallation owner)
    { lock (captureGate) foreach (var row in owner.CapturedOriginals) if (row.Original is { } original) captured.Add((row.Phase, original, row.Aggregate, row.Direct)); }
    private static readonly Uri Endpoint = new("https://mcp.example.test/mcp");
    private sealed class OAuthStore : IMcpAdmittedOAuthStateStore
    {
        internal int Writes;
        internal McpOAuthState Value = new(Endpoint.AbsoluteUri, JsonData.Parse("{\"client_id\":\"admitted-client\"}"),
            new("old", "Bearer", RefreshToken: "refresh"), CodeVerifier: "admitted-verifier", OAuthState: "entry-state",
            Discovery: JsonData.Parse("{\"authorizationServerUrl\":\"https://issuer.example.test/\",\"authorizationServerMetadata\":{\"issuer\":\"https://issuer.example.test/\",\"authorization_endpoint\":\"https://issuer.example.test/authorize\",\"token_endpoint\":\"https://issuer.example.test/token\",\"response_types_supported\":[\"code\"]}}"));
        public ValueTask<McpOAuthState?> LoadAsync() => ValueTask.FromResult<McpOAuthState?>(Value);
        public ValueTask SaveAsync(McpOAuthState value) { Value = value; Writes++; return ValueTask.CompletedTask; }
    }
    private sealed class HttpOperation(Func<CancellationToken, ValueTask<HttpResponseMessage>> send) : IMcpAdmittedHttpRequestOperation
    { public ValueTask<HttpResponseMessage> SendAsync(CancellationToken token) => new(McpApplicationHostOriginals.Track(send(token).AsTask(), "oauth-application-physical-send")); public Task StopAsync() => McpApplicationHostOriginals.Track(Task.CompletedTask, "oauth-application-physical-stop"); }
    private static HttpResponseMessage Http(int status, string body) => new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static McpDefaultOAuthHostResources OAuthResources(OAuthStore store, McpAdmittedHttpRequestFactory physical) => new(Endpoint,
        new("https://application.example.test/callback"), JsonData.Parse("{\"redirect_uris\":[\"https://application.example.test/callback\"],\"scope\":\"offline_access read\"}"),
        store, () => 1000, token => ValueTask.FromResult(new byte[32]), (uri, token) => throw new IOException("Browser was not admitted in refresh route."),
        (state, token) => state == "entry-state" ? ValueTask.CompletedTask : ValueTask.FromException(new IOException("State mismatch.")), physical,
        (uri, purpose) => uri == Endpoint || uri.Host == "issuer.example.test", new());
    private sealed class Policy : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(new ToolActionAuthorization(false)); }
    private sealed class NoTransport : IChatTransport
    {
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        { await Task.FromException(new IOException("Provider not admitted.")); yield break; }
    }
    private static McpApplicationGenerationRequest Request(long generation, McpServerCatalog? catalog = null)
    {
        var policy = new Policy(); var model = new ModelDescriptor("oauth-application", "openai-responses", "fixture");
        return new(Path.GetTempPath(), generation, new SessionRuntimeRegistry([new(model, new NoTransport())], [], policy), policy, catalog ?? new([], []));
    }
    private static async Task<Exception> Expected(Task original)
    { try { await McpApplicationHostOriginals.Observe(original, "oauth-application-expected"); } catch (Exception error) { return McpApplicationHostOriginals.Evidence(original).Aggregate ?? error; } throw new IOException("Expected actual original fault."); }
    private static bool Has(Exception root, Exception exact)
    {
        var pending = new Stack<Exception>(); var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance); pending.Push(root); var edges = 0;
        while (pending.Count != 0)
        {
            var item = pending.Pop(); if (!seen.Add(item)) continue; Require(seen.Count <= 1024); if (ReferenceEquals(item, exact)) return true;
            IEnumerable<Exception> children = item is AggregateException aggregate ? aggregate.InnerExceptions : item.InnerException is { } child ? [child] : [];
            foreach (var descendant in children) { Require(++edges <= 4096); pending.Push(descendant); }
        }
        return false;
    }
    private static async Task ReusedInnerOwners()
    {
        var nativeCloses = 0; var discoveryCloses = 0; var rejectedCloses = 0; var rejectedNativeCloses = 0; var calls = 0; var failures = new List<Exception>();
        var native = new Resource(() => { nativeCloses++; return Task.CompletedTask; }); var discovery = new Resource(() => { discoveryCloses++; return Task.CompletedTask; });
        var owner = new McpDefaultOAuthApplicationInstallation([], (request, token) =>
        {
            var current = ++calls;
            return ValueTask.FromResult(new McpApplicationGenerationResources(request,
                current <= 3 ? native : new Resource(() => { rejectedNativeCloses++; return Task.CompletedTask; }),
                current <= 2 || current == 4 ? discovery : new Resource(() => { rejectedCloses++; return Task.CompletedTask; }), [], (_, registry) => new(registry, [])));
        });
        McpApplicationGenerationResources? first = null;
        try
        {
            first = await McpApplicationHostOriginals.Observe(owner.AcquireGenerationAsync(Request(1), default).AsTask(), "oauth-first-generation");
            var both = owner.AcquireGenerationAsync(Request(2), default).AsTask(); _ = await Expected(both); Require(both.IsFaulted && nativeCloses == 0 && discoveryCloses == 0);
            var mixed = owner.AcquireGenerationAsync(Request(3), default).AsTask(); _ = await Expected(mixed);
            Require(mixed.IsFaulted && nativeCloses == 0 && discoveryCloses == 0 && rejectedCloses == 1);
            var reverse = owner.AcquireGenerationAsync(Request(4), default).AsTask(); _ = await Expected(reverse);
            Require(reverse.IsFaulted && nativeCloses == 0 && discoveryCloses == 0 && rejectedNativeCloses == 1);
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            if (first is not null) { await McpApplicationHostInstallationTests.Join(first.NativeResources.DisposeAsync().AsTask(), failures); await McpApplicationHostInstallationTests.Join(first.DiscoveryResources.DisposeAsync().AsTask(), failures); }
            Export(owner);
        }
        try { Require(nativeCloses == 1 && discoveryCloses == 1 && rejectedCloses == 1 && rejectedNativeCloses == 1); } catch (Exception error) { failures.Add(error); } Fail(failures);
    }
    private static async Task LateResources()
    {
        using var cancel = new CancellationTokenSource(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource<McpApplicationGenerationResources>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nativeLeaf = new IOException("late-base-native"); var discoveryLeaf = new IOException("late-base-discovery");
        var nativeOriginal = Task.FromException(nativeLeaf); var discoveryOriginal = Task.FromException(discoveryLeaf);
        McpApplicationGenerationRequest? actual = null; var failures = new List<Exception>();
        var owner = new McpDefaultOAuthApplicationInstallation([], (request, token) => { actual = request; entered.TrySetResult(); return new(returned.Task); });
        var original = owner.AcquireGenerationAsync(Request(1), cancel.Token).AsTask(); var released = false;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel(); Require(!original.IsCompleted);
            returned.TrySetResult(new(actual!, new Resource(() => nativeOriginal), new Resource(() => discoveryOriginal), [], (_, registry) => new(registry, []))); released = true;
            var failure = await Expected(original); Require(original.IsFaulted && returned.Task.IsCompletedSuccessfully && Has(failure, nativeLeaf) && Has(failure, discoveryLeaf));
            Require(owner.CapturedOriginals.Any(row => ReferenceEquals(row.Original, returned.Task) && row.Aggregate is null && row.Direct is null));
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            if (!released) returned.TrySetResult(new(actual!, new Resource(() => nativeOriginal), new Resource(() => discoveryOriginal), [], (_, registry) => new(registry, [])));
            try { await McpApplicationHostOriginals.Observe(original, "late-resource-finally"); } catch (Exception) { if (failures.Count != 0) failures.Add(McpApplicationHostOriginals.Evidence(original).Aggregate!); }
            Export(owner);
        }
        Fail(failures);
    }
    private static async Task LateChannel()
    {
        var loaded = McpConfigurationReader.Load(new("fixture", "{\"mcpServers\":{\"one\":{\"url\":\"https://mcp.example.test/mcp\"}}}"), null, true);
        var entry = loaded.Servers.Single(); var catalog = new McpServerCatalog([entry], []); var request = Request(1, catalog);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var reentry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var late = new TaskCompletionSource<IMcpAdmittedRequestChannel>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nativeLeaf = new IOException("actual-base-close-sibling"); var discoveryCloses = 0; var refusals = 0; var channel = new Channel();
        McpApplicationGenerationResources? installed = null; Task? close = null; Task<IMcpAdmittedRequestChannel>? acquire = null; var failures = new List<Exception>();
        var owner = new McpDefaultOAuthApplicationInstallation([new("one", value => Require(ReferenceEquals(value, entry)),
            (actual, value, token) => ValueTask.FromResult(OAuthResources(new OAuthStore(), message => throw new IOException("No physical request admitted."))),
            (value, generation, authenticated) => async (actual, token) =>
            {
                using var registration = token.Register(() => canceled.TrySetResult()); entered.TrySetResult(); await reentry.Task;
                try { _ = installed!.NativeResources.DisposeAsync(); throw new IOException("Channel callback close reentry accepted."); } catch (InvalidOperationException) { refusals++; }
                return await late.Task;
            })], async (actual, token) =>
            {
                var registry = new ExtensionRegistry();
                try
                {
                    var scope = await McpApplicationHostOriginals.Observe(registry.ActivateAsync("held", new EmptyExtension(), token), "held-channel-server-activation");
                    return new McpApplicationGenerationResources(actual,
                        new Resource(async () =>
                        {
                            await McpApplicationHostOriginals.Observe(registry.DisposeAsync().AsTask(), "held-channel-server-disposal");
                            await McpApplicationHostOriginals.Observe(Task.FromException(nativeLeaf), "held-channel-supplied-close-fault");
                        }), new Resource(() => { discoveryCloses++; return Task.CompletedTask; }),
                        [new(entry, registry, scope, (_, _, _) => ValueTask.FromResult(true), new(1, "fixture"), (current, capture) => current.PreparedToolHooks)], (_, current) => new(current, []));
                }
                catch (Exception error)
                { var faults = new List<Exception> { error }; await McpApplicationHostInstallationTests.Join(registry.DisposeAsync().AsTask(), faults); Fail(faults); throw; }
            });
        try
        {
            installed = await McpApplicationHostOriginals.Observe(owner.AcquireGenerationAsync(request, default).AsTask(), "held-channel-generation");
            var factory = owner.ServerAdmissions.Single().CreateChannelFactory(entry, 1); acquire = factory(entry, default).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); reentry.TrySetResult();
            // The actual callback's rejected close occurs before it awaits the held returned channel.
            var until = DateTime.UtcNow.AddSeconds(5); while (refusals != 1) { Require(DateTime.UtcNow < until); await Task.Yield(); }
            close = installed.NativeResources.DisposeAsync().AsTask(); Require(ReferenceEquals(close, installed.NativeResources.DisposeAsync().AsTask()));
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5)); Require(!close.IsCompleted && !acquire.IsCompleted);
            try { _ = factory(entry, default); throw new IOException("Retired map admitted late channel."); } catch (IOException error) when (error.Message.Contains("absent/retired", StringComparison.Ordinal)) { }
            late.TrySetResult(channel); _ = await Expected(acquire); var failure = await Expected(close); NoExtraCloseLeaves(failure, McpApplicationHostOriginals.Evidence(acquire).Aggregate!, nativeLeaf);
            Require(channel.Closes == 1 && Has(failure, nativeLeaf) && late.Task.IsCompletedSuccessfully && discoveryCloses == 0);
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            reentry.TrySetResult(); late.TrySetResult(channel);
            if (acquire is not null) try { await McpApplicationHostOriginals.Observe(acquire, "late-channel-finally"); } catch (Exception error) { if (failures.Count != 0) failures.Add(error); }
            if (installed is not null)
            {
                try { await McpApplicationHostOriginals.Observe(close ?? installed.NativeResources.DisposeAsync().AsTask(), "late-close-finally"); } catch (Exception error) { if (failures.Count != 0) failures.Add(error); }
                await McpApplicationHostInstallationTests.Join(installed.DiscoveryResources.DisposeAsync().AsTask(), failures);
            }
            Export(owner);
        }
        try { Require(discoveryCloses == 1); } catch (Exception error) { failures.Add(error); } Fail(failures);
    }
    private sealed class ApplicationFixtureFailure(Exception[] failures,
        (string Phase, Task Original, AggregateException? Aggregate, Exception? Direct)[] originals)
        : IOException("Application fixture primary/cleanup failures and actual raw originals retained.", new AggregateException(failures))
    { internal (string Phase, Task Original, AggregateException? Aggregate, Exception? Direct)[] RawOriginals { get; } = originals; }
    private static void Fail(List<Exception> failures) { if (failures.Count != 0) throw new ApplicationFixtureFailure(failures.ToArray(), CapturedOriginals); }
    private static void NoExtraCloseLeaves(Exception close, Exception acquisition, Exception nativeLeaf)
    {
        var allowed = new HashSet<Exception>(ReferenceEqualityComparer.Instance); var todo = new Stack<Exception>(); todo.Push(acquisition); var edges = 0;
        while (todo.Count != 0)
        {
            var item = todo.Pop(); if (!allowed.Add(item)) continue; Require(allowed.Count <= 1024);
            IEnumerable<Exception> children = item is AggregateException aggregate ? aggregate.InnerExceptions : item.InnerException is { } child ? [child] : [];
            foreach (var descendant in children) { Require(++edges <= 4096); todo.Push(descendant); }
        }
        allowed.Add(nativeLeaf); todo.Push(close); var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance); edges = 0;
        while (todo.Count != 0)
        {
            var item = todo.Pop(); if (!seen.Add(item)) continue; Require(seen.Count <= 1024); if (allowed.Contains(item)) continue;
            Require(item is AggregateException or McpDefaultOAuthApplicationFailure);
            IEnumerable<Exception> children = item is AggregateException aggregate ? aggregate.InnerExceptions : item.InnerException is { } child ? [child] : [];
            Require(children.Any()); foreach (var descendant in children) { Require(++edges <= 4096); todo.Push(descendant); }
        }
    }
}
