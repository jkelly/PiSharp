using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Extensions;
using PiSharp.Cli.Mcp;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Runtime;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class McpApplicationInstalledProfileEntryTests
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
        var channels = new List<Channel>(); ExtensionRegistry? actualRegistry = null; McpApplicationHostInstallation? installed = null;
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
                return installed = new(registry, McpConfigurationReader.Load(null, null, true),
                    [new("one", McpTransportKind.Stdio, entry => Require(entry.Config.Raw.Value.GetProperty("command").GetString() == "inert-admitted"),
                        (entry, generation) => (exact, token) => { Require(ReferenceEquals(entry, exact)); token.ThrowIfCancellationRequested();
                            var channel = new Channel(); channels.Add(channel); return ValueTask.FromResult<IMcpAdmittedRequestChannel>(channel); })], McpApplicationHostOriginals.Generation(async (request, token) =>
                    {
                        var serverRegistry = new ExtensionRegistry();
                        try
                        {
                            var scope = await McpApplicationHostOriginals.Observe(serverRegistry.ActivateAsync("capture", new EmptyExtension(), token), "installed-server-activation");
                            return new(request, new Resource(async () => { nativeCloses++; await McpApplicationHostOriginals.Observe(serverRegistry.DisposeAsync().AsTask(), "installed-server-disposal"); }),
                                new Resource(() => { discoveryCloses++; return Task.CompletedTask; }),
                                [new(request.Catalog.Servers.Single(), serverRegistry, scope, (_, _, _) => ValueTask.FromResult(true),
                                    new(request.Generation, "0.99.1"), (current, binding) => binding.PreparedHooks ?? current.PreparedToolHooks)], (_, current) => new(current, []));
                        }
                        catch (Exception error)
                        { var inventory = new List<Exception> { error }; await McpApplicationHostInstallationTests.Join(serverRegistry.DisposeAsync().AsTask(), inventory); McpApplicationHostInstallationTests.Throw(inventory); throw; }
                    }));
            }, facade => { binders++; facade.RegisterMcpServer("one", JsonData.Parse("{\"command\":\"inert-admitted\",\"exposure\":\"direct\"}")); });
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
            var current = await McpApplicationHostOriginals.Observe(profile.RefreshRegisteredMcpRuntimeAsync(profile.Sessions!.Current), "installed-refresh");
            Require(current.Generation == 2 && nativeCloses == 1 && discoveryCloses == 1 && channels[0].Closes == 1);
            await McpApplicationHostOriginals.Observe(profile.DisposeAsync().AsTask(), "installed-profile-close");
            Require(nativeCloses == 2 && discoveryCloses == 2 && channels.All(channel => channel.Closes == 1) &&
                File.ReadAllText(log) == "initialize\ndispose\n" && shutdown.Count > 0 && backend.ActiveWriterCount == 0 &&
                exactInstallation.Bridge.CaptureSnapshot().RegisteredServers.IsEmpty && Directory.GetFileSystemEntries(snapshots).Length == 0);
        }
        catch (Exception error) { faults.Add(error); }
        finally
        {
            if (profile is not null) await McpApplicationHostInstallationTests.Join(profile.DisposeAsync().AsTask(), faults);
            await McpApplicationHostOriginals.JoinTrackedSince(mark, faults);
            AppContext.SetData("PiSharp.ProfileInitializer." + id, null); AppContext.SetData("PiSharp.ProfileInitializer.Shutdown." + id, null);
            try { Directory.Delete(root, true); } catch (Exception error) { faults.Add(error); }
        }
        McpApplicationHostInstallationTests.Throw(faults);
    }
}
