using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Mcp;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Runtime;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class McpRegisteredProfileRefreshTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
        [("mcp-registered-profile.refresh-withdraws-capture-and-session-close-joins-both-generations", Refresh)];
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
    private static async Task Refresh()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-refresh-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "session.jsonl"); var metadata = new ExtensionRegistry();
        var metadataActivation = metadata.ActivateAsync("metadata", new Extension()); var metadataScope = await metadataActivation;
        var bridge = new McpExtensionRegistrationBridge(metadata, McpConfigurationReader.Load(null, null, true));
        var facade = bridge.BindOwner(metadataScope, root);
        facade.RegisterMcpServer("one", JsonData.Parse("{\"command\":\"inert\",\"exposure\":\"direct\"}"));
        var nativeCloses = new int[2]; var discoveryCloses = new int[2]; var channels = new List<Channel>(); var acquired = 0;
        var admission = new McpRegisteredProfileAdmission(bridge, async (cwd, generation, native, policy, catalog, token) =>
        {
            Require(cwd == root && generation is 1 or 2 && native.UsesFinalActionPolicy(policy));
            var index = acquired++; var servers = new ExtensionRegistry(); var serverActivation = servers.ActivateAsync("capture", new Extension());
            var serverScope = await serverActivation; var channel = new Channel(); channels.Add(channel);
            return new(native, new Resource(() => { nativeCloses[index]++; return Task.CompletedTask; }),
                new Resource(async () => { discoveryCloses[index]++; var close = servers.DisposeAsync().AsTask(); await close; }),
                policy, catalog, catalog.Servers.Select(entry => new McpServerActivationAdmission(entry.Name,
                    exact => Require(ReferenceEquals(exact, entry)), async (exact, current, cancellation) =>
                        await McpPreOpenServerCapture.AcquireAsync(exact, servers, serverScope, current, policy,
                            (_, _, _) => ValueTask.FromResult(true), (_, _) => ValueTask.FromResult<IMcpAdmittedRequestChannel>(channel),
                            new(generation, "0.99.1"), (registry, binding) => binding.PreparedHooks ?? registry.PreparedToolHooks, cancellation))).ToImmutableArray(),
                false, (_, current) => new(current, []));
        });
        OfflineSessionProfile? profile = null; Exception? primary = null;
        try
        {
            profile = await OfflineSessionProfile.CreateAsync(root, path, null, [], [], [], CancellationToken.None);
            profile.ConfigureMcpRegistrationRuntime(admission);
            var backend = new SessionStorageBackend(root, SessionStorageMode.InMemory); var sequence = 0;
            var lifecycle = profile.CreateLifecycle(() => 0, () => "entry-" + ++sequence, backend: backend);
            var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3,
                id = "refresh", timestamp = "2026-10-06T00:00:00.000Z", cwd = root }));
            var session = await lifecycle.CreateAsync(path, header, profile.SelectedModel);
            await session.ConfigureAsync(new(SystemMessage: new("system", profile.InitialSystem)));
            await profile.AttachOwnerAsync(session, lifecycle: lifecycle); await profile.ApplyInitialToolSelectionAsync(session, CancellationToken.None);
            var owner = profile.Sessions!; var previous = owner.Current;
            var capturedName = previous.Session.CaptureToolCatalogRegistry().RegisteredTools.Single(tool => tool.IsExtension).Adapter.Name;
            Require(previous.Session.GetActiveTools().Contains(capturedName)); facade.UnregisterMcpServer("one");
            var refresh = profile.RefreshRegisteredMcpRuntimeAsync(previous); var current = await refresh;
            Require(ReferenceEquals(owner.Current, current) && current.Generation == 2 && nativeCloses[0] == 1 &&
                discoveryCloses[0] == 1 && channels[0].Closes == 1 && !current.Session.CaptureToolCatalogRegistry().RegisteredTools.Any(tool => tool.Adapter.Name == capturedName) &&
                !current.Session.GetActiveTools().Contains(capturedName));
            var closeProfile = profile.DisposeAsync().AsTask(); await closeProfile;
            Require(acquired == 2 && nativeCloses[1] == 1 && discoveryCloses[1] == 1 && backend.ActiveWriterCount == 0);
        }
        catch (Exception error) { primary = error; }
        finally
        {
            foreach (var cleanup in new Func<ValueTask>[] { () => profile?.DisposeAsync() ?? ValueTask.CompletedTask, metadata.DisposeAsync })
            {
                Task? original = null;
                try { original = cleanup().AsTask(); await original; }
                catch (Exception error) { var evidence = original?.Exception ?? error; primary = primary is null ? evidence : new AggregateException(primary, evidence); }
            }
            try { Directory.Delete(root, true); }
            catch (Exception error) { primary = primary is null ? error : new AggregateException(primary, error); }
        }
        if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
    }
}
