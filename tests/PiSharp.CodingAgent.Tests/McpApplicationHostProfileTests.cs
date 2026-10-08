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

internal static class McpApplicationHostProfileTests
{
    private static void Require(bool value) { if (!value) throw new IOException("Application profile assertion failed."); }
    internal static async Task Refresh()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcp-application-profile-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var metadata = new ExtensionRegistry(); OfflineSessionProfile? profile = null;
        var mark = McpApplicationHostOriginals.MarkTracked();
        var failures = new List<Exception>(); var nativeCloses = new int[2]; var discoveryCloses = new int[2];
        var channels = new List<Channel>(); var acquired = 0;
        try
        {
            var scope = await McpApplicationHostOriginals.Observe(metadata.ActivateAsync("actual-metadata", new Extension()), "metadata-activation");
            var installation = new McpApplicationHostInstallation(metadata, McpConfigurationReader.Load(null, null, true),
                [new("one", McpTransportKind.Stdio,
                    entry => Require(entry.Config.Raw.Value.GetProperty("command").GetString() == "explicit-inert-command"),
                    (entry, generation) => (actual, token) =>
                    {
                        Require(ReferenceEquals(entry, actual)); token.ThrowIfCancellationRequested();
                        var channel = new Channel(); channels.Add(channel); return ValueTask.FromResult<IMcpAdmittedRequestChannel>(channel);
                    })], McpApplicationHostOriginals.Generation(async (request, token) =>
                {
                    Require(request.Cwd == root && request.Generation is 1 or 2 && request.NativeRegistry.UsesFinalActionPolicy(request.ExactPolicy));
                    var index = acquired++; var registries = new List<ExtensionRegistry>();
                    var owners = ImmutableArray.CreateBuilder<McpApplicationServerOwner>();
                    try
                    {
                        foreach (var entry in request.Catalog.Servers.Where(entry => entry.Config.Enabled))
                        {
                            var registry = new ExtensionRegistry(); registries.Add(registry);
                            var serverScope = await McpApplicationHostOriginals.Observe(registry.ActivateAsync("actual-capture", new Extension(), token), "server-activation");
                            owners.Add(new(entry, registry, serverScope, (_, _, _) => ValueTask.FromResult(true),
                                new(request.Generation, "0.99.1"), (current, binding) => binding.PreparedHooks ?? current.PreparedToolHooks));
                        }
                        return new(request, new Resource(async () =>
                        {
                            nativeCloses[index]++; var cleanup = new List<Exception>();
                            foreach (var registry in registries) await McpApplicationHostInstallationTests.Join(registry.DisposeAsync().AsTask(), cleanup);
                            McpApplicationHostInstallationTests.Throw(cleanup);
                        }), new Resource(() => { discoveryCloses[index]++; return Task.CompletedTask; }), owners.ToImmutable(), (_, current) => new(current, []));
                    }
                    catch (Exception error)
                    {
                        var cleanup = new List<Exception> { error };
                        foreach (var registry in registries) await McpApplicationHostInstallationTests.Join(registry.DisposeAsync().AsTask(), cleanup);
                        McpApplicationHostInstallationTests.Throw(cleanup); throw;
                    }
                }));
            var facade = installation.Bridge.BindOwner(scope, root);
            facade.RegisterMcpServer("one", JsonData.Parse("{\"command\":\"explicit-inert-command\",\"exposure\":\"direct\"}"));
            var path = Path.Combine(root, "session.jsonl");
            profile = await McpApplicationHostOriginals.Observe(OfflineSessionProfile.CreateAsync(root, path, null, [], [], [], CancellationToken.None), "profile-create");
            profile.ConfigureMcpRegistrationRuntime(installation.CreateRegisteredAdmission());
            var backend = new SessionStorageBackend(root, SessionStorageMode.InMemory); var sequence = 0;
            var lifecycle = profile.CreateLifecycle(() => 0, () => "entry-" + ++sequence, backend: backend);
            var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3,
                id = "application-refresh", timestamp = "2026-10-07T00:00:00.000Z", cwd = root }));
            var session = await McpApplicationHostOriginals.Observe(lifecycle.CreateAsync(path, header, profile.SelectedModel), "session-create");
            await McpApplicationHostOriginals.Observe(session.ConfigureAsync(new(SystemMessage: new("system", profile.InitialSystem))), "session-configure");
            await McpApplicationHostOriginals.Observe(profile.AttachOwnerAsync(session, lifecycle: lifecycle), "profile-attach");
            await McpApplicationHostOriginals.Observe(profile.ApplyInitialToolSelectionAsync(session, CancellationToken.None), "initial-selection");
            var owner = profile.Sessions!; var previous = owner.Current;
            var name = previous.Session.CaptureToolCatalogRegistry().RegisteredTools.Single(tool => tool.IsExtension).Adapter.Name;
            Require(previous.Session.GetActiveTools().Contains(name));
            var scopeDisposal = scope.DisposeAsync().AsTask();
            await McpApplicationHostOriginals.Observe(scopeDisposal, "metadata-scope-disposal");
            await McpApplicationHostOriginals.Observe(installation.Bridge.RetireClosedOwnerAsync(scope, scopeDisposal), "metadata-withdrawal");
            var current = await McpApplicationHostOriginals.Observe(profile.RefreshRegisteredMcpRuntimeAsync(previous), "profile-refresh");
            Require(current.Generation == 2 && ReferenceEquals(owner.Current, current) && channels.Single().Closes == 1 &&
                nativeCloses[0] == 1 && discoveryCloses[0] == 1 && !current.Session.GetActiveTools().Contains(name) &&
                !current.Session.CaptureToolCatalogRegistry().RegisteredTools.Any(tool => tool.Adapter.Name == name));
            await McpApplicationHostOriginals.Observe(profile.DisposeAsync().AsTask(), "profile-close");
            Require(acquired == 2 && nativeCloses[1] == 1 && discoveryCloses[1] == 1 && backend.ActiveWriterCount == 0);
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            if (profile is not null) await McpApplicationHostInstallationTests.Join(profile.DisposeAsync().AsTask(), failures);
            await McpApplicationHostInstallationTests.Join(metadata.DisposeAsync().AsTask(), failures);
            await McpApplicationHostOriginals.JoinTrackedSince(mark, failures);
            try { Directory.Delete(root, true); } catch (Exception error) { failures.Add(error); }
        }
        McpApplicationHostInstallationTests.Throw(failures);
    }
    private sealed class Extension : IPiSharpExtension
    { public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) => ValueTask.CompletedTask; public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    private sealed class Resource(Func<Task> close) : IAsyncDisposable
    { private Task? original; public ValueTask DisposeAsync() => new(original ??= McpApplicationHostOriginals.Track(close(), "actual-profile-resource-disposal")); }
    private sealed class Channel : IMcpAdmittedRequestChannel
    {
        private Task? close; internal int Closes;
        public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask NotifyAsync(string method, JsonData? parameters, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<JsonData> RequestAsync(string method, JsonData? parameters, McpRequestOptions options, CancellationToken token)
            => ValueTask.FromResult(JsonData.Parse(method == "initialize"
                ? "{\"protocolVersion\":\"2025-11-25\",\"serverInfo\":{\"name\":\"admitted\",\"version\":\"1\"},\"capabilities\":{\"tools\":{}}}"
                : method == "tools/list" ? "{\"tools\":[{\"name\":\"echo\",\"inputSchema\":{\"type\":\"object\",\"properties\":{}}}]}"
                : throw new IOException("External tool execution not admitted.")));
        public Task CloseAsync() => close ??= Close();
        private Task Close() { Closes++; return Task.CompletedTask; }
    }
}
