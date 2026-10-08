using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Extensions;
using PiSharp.Cli.Mcp;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class ProfileInitializerTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() => [
        ("profile.initializer-pre-effect-refusal", Refusal),
        ("profile.actual-loader-model-publication-survives-mcp-replacement", Publication)
    ];
    private static void Check(bool value, string anchor) { if (!value) throw new IOException(anchor); }
    private static string? publishedFixtureRoot;
    internal static void AdmitPublishedFixtureRoot(string root)
    {
        if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root) || publishedFixtureRoot is not null)
            throw new ArgumentException("One explicit separately compiled fixture output root is required.", nameof(root));
        publishedFixtureRoot = root;
    }
    private static readonly ModelDescriptor Installed = new("installed", "native-install", "admitted");
    private sealed class Configuration : IExtensionProviderConfigurationAdapter
    {
        public ExtensionProviderDefinition Resolve(string name, JsonData configuration) =>
            new(name, [new(Installed, JsonData.Parse("{\"id\":\"installed\",\"api\":\"native-install\",\"provider\":\"admitted\"}"))], Completed);
    }
    private static async IAsyncEnumerable<StreamEvent> Completed(ExtensionProviderStreamRequest request,
        IExtensionContext? context, [EnumeratorCancellation] CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        yield return new StreamDone(StopReason.Stop, new AssistantMessage(request.Model.Api, request.Model.Provider,
            request.Model.Id, 1, [new TextContent("actual-installed")], TokenUsage.Zero, StopReason.Stop));
        await Task.CompletedTask;
    }
    private sealed class Resource : IAsyncDisposable
    {
        internal int Closes;
        public ValueTask DisposeAsync() { Closes++; return ValueTask.CompletedTask; }
    }
    private sealed class Original(Task task)
    { internal Task Task = task; internal Exception? Direct; internal AggregateException? Aggregate; internal bool Expected; }
    private static async Task<T> Join<T>(Task<T> task, List<Original> records)
    {
        var record = new Original(task); records.Add(record);
        try { return await task; } catch (Exception error) { record.Direct = error; record.Aggregate = task.Exception; throw; }
    }
    private static async Task Join(Task task, List<Original> records)
    {
        var record = new Original(task); records.Add(record);
        try { await task; } catch (Exception error) { record.Direct = error; record.Aggregate = task.Exception; throw; }
    }
    private static async Task Cleanup(Func<ValueTask> factory, List<Original> records, List<Exception> failures)
    { try { await Join(factory().AsTask(), records); } catch (Exception error) { failures.Add(error); } }
    private static void ThrowInventory(List<Original> records, List<Exception> failures)
    {
        foreach (var record in records)
        {
            if (record.Expected && failures.Count == 0) continue;
            if (record.Aggregate is not null) failures.Add(record.Aggregate);
            if (record.Direct is not null) failures.Add(record.Direct);
        }
        if (failures.Count != 0) throw new AggregateException("Exact profile control and cleanup originals.",
            failures.Distinct<Exception>(ReferenceEqualityComparer.Instance));
    }
    private static async Task Refusal()
    {
        var root = Path.Combine(Path.GetTempPath(), "profile-initializer-refusal-" + Guid.NewGuid().ToString("N"));
        var calls = 0; var originals = new List<Original>(); var failures = new List<Exception>();
        var installation = new NativeExtensionInitializerInstallation(registry =>
        { calls++; return new(registry, new ExtensionHostFlagValues(new Dictionary<string, ExtensionFlagValue>()), [], new Configuration()); }, (_, _) => calls++);
        var task = OfflineSessionProfile.CreateAsync(root, Path.Combine(root, "session.jsonl"), null, [], [], [],
            default, configuredInitializerInstallation: installation);
        try
        {
            try { _ = await Join(task, originals); throw new IOException("Missing native configuration was accepted."); }
            catch (ArgumentException) { }
            Check(task.IsFaulted && calls == 0 && !Directory.Exists(root), "refusal-before-profile-effects");
            originals.Single().Expected = true;
        }
        catch (Exception error) { failures.Add(error); }
        ThrowInventory(originals, failures);
    }
    private static async Task Publication()
    {
        var originals = new List<Original>(); var failures = new List<Exception>();
        try
        {
            await Join(PublicationTarget(volatileTarget: true), originals);
            await Join(PublicationTarget(volatileTarget: false), originals);
        }
        catch (Exception error) { failures.Add(error); }
        ThrowInventory(originals, failures);
    }
    private static async Task PublicationTarget(bool volatileTarget)
    {
        var root = Path.Combine(Path.GetTempPath(), "profile-initializer-model-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var package = Path.Combine(root, "package"); var snapshots = Path.Combine(root, "snapshots");
        Directory.CreateDirectory(package); Directory.CreateDirectory(snapshots);
        var sessionPath = Path.Combine(root, "session.jsonl"); var id = "fixture.profile." + Guid.NewGuid().ToString("N");
        var log = Path.Combine(root, "entry.log"); var originals = new List<Original>(); var failures = new List<Exception>();
        OfflineSessionProfile? profile = null; PersistentAgentSession? unattached = null;
        NativeExtensionRegistrationBridge? bridge = null; var binders = 0; var generations = new List<long>();
        var nativeResources = new List<Resource>(); var discoveryResources = new List<Resource>();
        var shutdownObservations = new List<JsonData>();
        try
        {
            var admittedRoot = publishedFixtureRoot ?? throw new IOException("No published fixture output admission.");
            const string stem = "PublishedFixture.ProfileInitializer";
            const string assembly = stem + ".dll";
            foreach (var suffix in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
                File.Copy(Path.Combine(admittedRoot, stem + suffix), Path.Combine(package, stem + suffix));
            using (var deps = JsonDocument.Parse(File.ReadAllText(Path.Combine(package, stem + ".deps.json"))))
            {
                var libraries = deps.RootElement.GetProperty("libraries");
                Check(libraries.GetProperty(stem + "/0.0.1").GetProperty("type").GetString() == "project" &&
                    libraries.EnumerateObject().All(item => item.Name == stem + "/0.0.1" ||
                        item.Name.StartsWith("PiSharp.Extensions.Abstractions/", StringComparison.Ordinal) ||
                        item.Name.StartsWith("PiSharp.Contracts/", StringComparison.Ordinal)), "minimal-published-fixture-dependency-closure");
            }
            var hashes = Directory.GetFiles(package).ToDictionary(file => Path.GetFileName(file),
                file => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))));
            var manifest = JsonSerializer.Serialize(new { schemaVersion = 0, id, packageVersion = "0.0.1",
                hostApiRange = new { minimum = "0.0.0", maximumExclusive = "0.1.0" }, runtimeKind = "native", assembly,
                entryType = "PublishedFixture.ProfileInitializer.Entry", tfm = "net10.0", rids = new[] { "win-x64" },
                requiredFeatures = new[] { "owned-descriptor-callbacks" }, declaredCapabilities = Array.Empty<string>(),
                resourcePaths = hashes.Keys.Where(name => name != assembly).ToArray(), explicitOverrides = Array.Empty<object>(), artifactHashes = hashes });
            var manifestPath = Path.Combine(root, "manifest.json"); var approvalPath = Path.Combine(root, "approval.json");
            File.WriteAllText(manifestPath, manifest);
            File.WriteAllText(approvalPath, JsonSerializer.Serialize(new { schemaVersion = 1, execution = "ApprovePublishedFixtureExecution",
                packageRoot = package, manifestValueSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))), artifactHashes = hashes,
                sourceScope = "Explicit", effectiveScopeId = "cli-native-explicit", policyRevision = "experimental-policy-0", hostGeneration = 1,
                sessionPath, workspace = root, snapshotRoot = snapshots, enabledTools = Array.Empty<string>(), enabledCommands = Array.Empty<string>() }));
            AppContext.SetData("PiSharp.ProfileInitializer." + id, log);
            AppContext.SetData("PiSharp.ProfileInitializer.Shutdown." + id,
                (Action<JsonData>)(observation => shutdownObservations.Add(observation)));
            var installation = new NativeExtensionInitializerInstallation(registry => bridge = new(registry,
                new ExtensionHostFlagValues(new Dictionary<string, ExtensionFlagValue>()), [], new Configuration()),
                (_, facade) => { binders++; facade.RegisterProvider("admitted", JsonData.EmptyObject); });
            McpProfileRuntimeAdmission mcp = (cwd, generation, registry, policy, token) =>
            {
                token.ThrowIfCancellationRequested(); generations.Add(generation);
                var native = new Resource(); var discovery = new Resource(); nativeResources.Add(native); discoveryResources.Add(discovery);
                return ValueTask.FromResult(new McpSessionRuntimeAdmission(registry, native, discovery, policy, new([], []), [], false,
                    (_, current) => new(current.WithToolCatalog(current.RegisteredTools, current.PreparedToolHooks), [])));
            };
            var configuration = NativeExtensionConfiguration.Optional(package, manifestPath, approvalPath, snapshots, [])!;
            profile = await Join(OfflineSessionProfile.CreateAsync(root, sessionPath, null, [], [], [], default,
                extension: configuration, mcpAdmission: mcp, configuredInitializerInstallation: installation), originals);
            Check(binders == 1 && File.ReadAllText(log) == "initialize\n", "actual-profile-single-initializer");
            var startup = profile.Registry; var startupCatalog = startup.CaptureModelCatalog();
            Check(startupCatalog.Bindings.Any(binding => binding.Model == Installed) &&
                startupCatalog.Bindings.Any(binding => binding.Model == profile.SelectedModel), "publication-before-session-resolution");
            _ = startup.Resolve(Installed, []);
            var backend = volatileTarget ? new SessionStorageBackend(root, SessionStorageMode.InMemory) : null;
            var sequence = 0;
            var lifecycle = profile.CreateLifecycle(() => 0, () => "entry-" + ++sequence, backend: backend);
            var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3,
                id = "initializer-profile", timestamp = "2026-10-06T00:00:00Z", cwd = root }));
            unattached = await Join(lifecycle.CreateAsync(sessionPath, header, profile.SelectedModel), originals);
            await Join(profile.AttachOwnerAsync(unattached, lifecycle: lifecycle), originals); unattached = null;
            var owner = profile.Sessions ?? throw new IOException("Profile owner missing.");
            var first = owner.Current.Session.CaptureToolCatalogRegistry();
            Check(!ReferenceEquals(first, startup) && first.CaptureModelCatalog().Bindings.Any(binding => binding.Model == Installed), "actual-mcp-clone-retains-installation");
            _ = first.Resolve(Installed, []);
            var previousRevision = first.CaptureModelCatalog().Revision;
            (bridge ?? throw new IOException("Actual registration bridge missing.")).RefreshModelCatalog();
            Check(first.CaptureModelCatalog().Revision > previousRevision &&
                first.CaptureModelCatalog().Revision == startup.CaptureModelCatalog().Revision, "mcp-clone-observes-real-publication-refresh");
            var previousSession = owner.Current.Session;
            await Join(owner.CreateAsync(owner.Current, new(AgentSessionCreationKind.New)), originals);
            var targetFile = owner.Current.Session.SessionFile;
            Check((targetFile is null) == volatileTarget, "actual-target-storage-route");
            Check(shutdownObservations.Count == 1, "actual-published-owner-shutdown-original");
            var shutdown = shutdownObservations.Single().Value;
            Check(shutdown.GetProperty("type").GetString() == "session_shutdown" &&
                shutdown.GetProperty("reason").GetString() == "new", "actual-shutdown-type-and-reason");
            if (volatileTarget)
                Check(!shutdown.TryGetProperty("targetSessionFile", out _), "volatile-target-file-is-absent");
            else
                Check(targetFile is not null && Path.IsPathFullyQualified(targetFile) && File.Exists(targetFile) &&
                    shutdown.GetProperty("targetSessionFile").ValueKind == JsonValueKind.String &&
                    shutdown.GetProperty("targetSessionFile").GetString() == targetFile, "persisted-target-file-is-exact");
            // Replacement retains asynchronous prior-session retirement; join that actual session close before count assertions.
            await Join(previousSession.DisposeAsync().AsTask(), originals);
            var second = owner.Current.Session.CaptureToolCatalogRegistry();
            Check(generations.SequenceEqual(new long[] { 1, 2 }) && !ReferenceEquals(first, second) &&
                second.CaptureModelCatalog().Bindings.Any(binding => binding.Model == Installed), "replacement-reacquires-real-mcp-runtime");
            _ = second.Resolve(Installed, []);
            Check(nativeResources[0].Closes == 1 && discoveryResources[0].Closes == 1 && nativeResources[1].Closes == 0,
                "old-generation-close-original-joined");
            await Join(profile.DisposeAsync().AsTask(), originals); profile = null;
            Check(nativeResources.All(resource => resource.Closes == 1) && discoveryResources.All(resource => resource.Closes == 1) &&
                (backend is null || backend.ActiveWriterCount == 0) && File.ReadAllText(log) == "initialize\ndispose\n" &&
                !Directory.EnumerateFileSystemEntries(snapshots).Any(), "profile-full-resource-and-loader-retirement");
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            if (profile is not null) await Cleanup(() => profile.DisposeAsync(), originals, failures);
            if (unattached is not null) await Cleanup(() => unattached.DisposeAsync(), originals, failures);
            try { AppContext.SetData("PiSharp.ProfileInitializer." + id, null); } catch (Exception error) { failures.Add(error); }
            try { AppContext.SetData("PiSharp.ProfileInitializer.Shutdown." + id, null); } catch (Exception error) { failures.Add(error); }
            try { Directory.Delete(root, true); } catch (Exception error) { failures.Add(error); }
        }
        ThrowInventory(originals, failures);
    }
}
