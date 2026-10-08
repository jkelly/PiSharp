using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Mcp;
using PiSharp.Contracts;
using PiSharp.Extensions.Agent;
using PiSharp.Extensions.Mcp.Discovery;
using PiSharp.Extensions;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Runtime;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class McpRegisteredProfileDiscoveryTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
        [("mcp-registered-profile.semantic-identity-profile-refresh-retires-old-generation", Refresh)];
    private static void Require(bool value) { if (!value) throw new IOException("Registered MCP profile refresh assertion failed."); }
    private sealed class Extension : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Resource(Func<Task> close) : IAsyncDisposable
    { public ValueTask DisposeAsync() => new(close()); }
    private sealed class Channel : IMcpAdmittedRequestChannel
    {
        private Task? close; internal int Closes;
        public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask NotifyAsync(string method, JsonData? parameters, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<JsonData> RequestAsync(string method, JsonData? parameters, McpRequestOptions options, CancellationToken token)
            => ValueTask.FromResult(JsonData.Parse(method == "initialize"
                ? "{\"protocolVersion\":\"2025-11-25\",\"serverInfo\":{\"name\":\"injected\",\"version\":\"1\"},\"capabilities\":{\"tools\":{}}}"
                : method == "tools/list" ? "{\"tools\":[{\"name\":\"echo\",\"inputSchema\":{\"type\":\"object\",\"properties\":{}}}]}"
                : throw new IOException("No admitted external tool execution.")));
        public Task CloseAsync() => close ??= Close();
        private Task Close() { Closes++; return Task.CompletedTask; }
    }
    private sealed class OriginalInventoryFailure(Exception? primary, IEnumerable<Exception> faults, Task[] originals)
        : AggregateException("Registered discovery original failure inventory", primary is null ? faults : new[] { primary }.Concat(faults))
    { internal Task[] Originals { get; } = originals; }
    private static async Task Refresh()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-discovery-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var originals = new System.Collections.Concurrent.ConcurrentQueue<Task>();
        T Track<T>(T original) where T : Task { originals.Enqueue(original); return original; }
        var path = Path.Combine(root, "session.jsonl"); var metadata = new ExtensionRegistry();
        
        var bridge = new McpExtensionRegistrationBridge(metadata, McpConfigurationReader.Load(null, null, true));
        var semanticCatalogs = new List<McpPreparedDiscoveryCatalog>(); var nativeCloses = new int[2]; var discoveryCloses = new int[2]; var channels = new List<Channel>(); var acquired = 0;
        var admission = new McpRegisteredProfileAdmission(bridge, async (cwd, generation, native, policy, catalog, token) =>
        {
            Require(cwd == root && generation is 1 or 2 && native.UsesFinalActionPolicy(policy));
            var index = acquired++; var servers = new ExtensionRegistry();
            var channel = new Channel(); channels.Add(channel);
            var discovery = new ExtensionRegistry();
            try
            {
                var serverActivation = Track(servers.ActivateAsync("capture", new Extension())); var serverScope = await serverActivation;
                var discoveryActivation = Track(discovery.ActivateAsync("semantic-discovery", new Extension())); var discoveryScope = await discoveryActivation;
                var semantic = new McpRegisteredProfileDiscoveryAdmission(discovery, discoveryScope,
                    [McpDiscoveryExecutableDefinition.CreateCodemode("semantic-code", "Explicit injected semantic executor",
                        (code, invocation, cancellation) => ValueTask.FromResult(JsonData.Parse("{\"content\":[]}"))),
                     McpDiscoveryExecutableDefinition.CreateToolSearch("semantic-search", "Explicit injected search executor",
                        (query, limit, invocation, cancellation) => ValueTask.FromResult(JsonData.Parse("{\"content\":[]}")))],
                    policy, generation, (tool, arguments, cancellation) => ValueTask.FromResult(
                        arguments.Value.ValueKind == JsonValueKind.Object && arguments.Value.TryGetProperty(tool.Name == "codemode" ? "code" : "query", out var value) && value.ValueKind == JsonValueKind.String),
                    (current, binding) => current.PreparedToolHooks);

            return new(native, new Resource(() => { nativeCloses[index]++; return Task.CompletedTask; }),
                new Resource(async () =>
                {
                    discoveryCloses[index]++; Exception? failure = null;
                    foreach (var registry in new[] { discovery, servers })
                    {
                        Task? close = null;
                        try { close = Track(registry.DisposeAsync().AsTask()); await close; }
                        catch (Exception error) { var full = close?.Exception ?? error; failure = failure is null ? full : new AggregateException(failure, full); }
                    }
                    if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                }),
                policy, catalog, catalog.Servers.Select(entry => new McpServerActivationAdmission(entry.Name,
                    exact => Require(ReferenceEquals(exact, entry)), async (exact, current, cancellation) =>
                        await Track(McpPreOpenServerCapture.AcquireAsync(exact, servers, serverScope, current, policy,
                            (_, _, _) => ValueTask.FromResult(true), (_, _) => ValueTask.FromResult<IMcpAdmittedRequestChannel>(channel),
                            new(generation, "0.99.1"), (registry, binding) => binding.PreparedHooks ?? registry.PreparedToolHooks, cancellation)))).ToImmutableArray(),
                false, (plan, current) =>
                {
                    var prepared = semantic.Prepare(plan, current); semanticCatalogs.Add(prepared);
                    Require(prepared.Identities.Length == 2 && prepared.Identities.Any(identity => identity.Kind == McpDiscoveryKind.Codemode) &&
                        prepared.Identities.Any(identity => identity.Kind == McpDiscoveryKind.ToolSearch) &&
                        prepared.Identities.All(identity => identity.ReservedGeneration == generation));
                    return prepared;
                });
            }
            catch (Exception admissionFailure)
            {
                Exception retained = admissionFailure;
                foreach (var registry in new[] { discovery, servers })
                {
                    Task? cleanup = null;
                    try { cleanup = Track(registry.DisposeAsync().AsTask()); await cleanup; }
                    catch (Exception cleanupFailure) { retained = new AggregateException(retained, cleanup?.Exception ?? cleanupFailure); }
                }
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(retained).Throw(); throw;
            }
        });
        OfflineSessionProfile? profile = null; Exception? primary = null;
        try
        {
            var metadataActivation = Track(metadata.ActivateAsync("metadata", new Extension())); var metadataScope = await metadataActivation;
            var facade = bridge.BindOwner(metadataScope, root);
            facade.RegisterMcpServer("one", JsonData.Parse("{\"command\":\"inert\",\"exposure\":\"codemode\"}"));
            profile = await Track(OfflineSessionProfile.CreateAsync(root, path, null, [], [], [], CancellationToken.None));
            profile.ConfigureMcpRegistrationRuntime(admission);
            var backend = new SessionStorageBackend(root, SessionStorageMode.InMemory); var sequence = 0;
            var lifecycle = profile.CreateLifecycle(() => 0, () => "entry-" + ++sequence, backend: backend);
            var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3,
                id = "refresh", timestamp = "2026-10-06T00:00:00.000Z", cwd = root }));
            var session = await Track(lifecycle.CreateAsync(path, header, profile.SelectedModel));
            await Track(session.ConfigureAsync(new(SystemMessage: new("system", profile.InitialSystem))));
            await Track(profile.AttachOwnerAsync(session, lifecycle: lifecycle)); await Track(profile.ApplyInitialToolSelectionAsync(session, CancellationToken.None));
            var owner = profile.Sessions!; var previous = owner.Current;
            var capturedName = previous.Session.CaptureToolCatalogRegistry().RegisteredTools.Single(tool => tool.IsExtension && tool.Adapter.Name is not ("codemode" or "tool_search")).Adapter.Name;
            Require(semanticCatalogs.Count == 1 && previous.Session.GetActiveTools().Contains("codemode") && previous.Session.GetActiveTools().Contains("tool_search") &&
                previous.Session.CaptureToolCatalogRegistry().RegisteredTools.Any(tool => tool.Adapter.Name == "codemode" &&
                    tool.Exposure == ToolExposure.ModelOnly && tool.DefaultActive));
            facade.UnregisterMcpServer("one");
            var refresh = Track(profile.RefreshRegisteredMcpRuntimeAsync(previous)); var current = await refresh;
            Require(ReferenceEquals(owner.Current, current) && current.Generation == 2 && nativeCloses[0] == 1 &&
                discoveryCloses[0] == 1 && channels[0].Closes == 1 && !current.Session.CaptureToolCatalogRegistry().RegisteredTools.Any(tool => tool.Adapter.Name == capturedName) &&
                !current.Session.GetActiveTools().Contains(capturedName));
            Require(semanticCatalogs.Count == 2 && current.Session.GetActiveTools().Contains("codemode"));
            var required = new McpToolCatalogPlan([], ImmutableDictionary<string, string>.Empty, true, true, true, null);
            McpPreparedDiscoveryIdentity.ValidateRequired(required, semanticCatalogs[1].Registry, semanticCatalogs[1].Identities);
            var staleRefused = false;
            try { McpPreparedDiscoveryIdentity.ValidateRequired(required, semanticCatalogs[1].Registry, semanticCatalogs[0].Identities); }
            catch (InvalidOperationException) { staleRefused = true; }
            Require(staleRefused);
            var closeProfile = Track(profile.DisposeAsync().AsTask()); await closeProfile;
            Require(semanticCatalogs.Count == 2 && semanticCatalogs[1].Identities[0].ReservedGeneration == 2 && acquired == 2 && nativeCloses[1] == 1 && discoveryCloses[1] == 1 && backend.ActiveWriterCount == 0);
        }
        catch (Exception error) { primary = error; }
        finally
        {
            foreach (var cleanup in new Func<ValueTask>[] { () => profile?.DisposeAsync() ?? ValueTask.CompletedTask, metadata.DisposeAsync })
            {
                Task? original = null;
                try { original = Track(cleanup().AsTask()); await original; }
                catch (Exception error) { var evidence = original?.Exception ?? error; primary = primary is null ? evidence : new AggregateException(primary, evidence); }
            }
            try { Directory.Delete(root, true); }
            catch (Exception error) { primary = primary is null ? error : new AggregateException(primary, error); }
        }
        var inventory = originals.ToArray();
        var fullFaults = new List<Exception>();
        foreach (var original in inventory)
        {
            if (original.IsFaulted && original.Exception is { } full) fullFaults.Add(full);
            try { await original; }
            catch (Exception observed) { fullFaults.Add(observed); }
        }
        if (primary is not null || fullFaults.Count != 0)
            throw new OriginalInventoryFailure(primary, fullFaults, inventory);
    }
}
