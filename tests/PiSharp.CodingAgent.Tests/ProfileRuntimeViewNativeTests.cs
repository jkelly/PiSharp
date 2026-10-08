using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Extensions;
using PiSharp.Cli.Reloading;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions.Abstractions.Reloading;
using PiSharp.Sessions.Serialization;

internal static class ProfileRuntimeViewNativeTests
{
    internal static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("profile view actual native reload targets old activation and final quit targets new activation", () => Lifecycle(false)),
        ("profile view empty reload preserves foreign same-owner callbacks and removes exact retired hooks", () => Lifecycle(true))
    ];
    private static async Task Lifecycle(bool replaceWithEmpty)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-profile-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var path = Path.Combine(root, "session.jsonl");
        var ids = new[] { "fixture.view." + Guid.NewGuid().ToString("N"), "fixture.view." + Guid.NewGuid().ToString("N") };
        var profile = await OfflineSessionProfile.CreateAsync(root, path, null, [], [], [], default);
        var sequence = 0;
        try
        {
            var configurations = new NativeExtensionConfiguration[2];
            for (var i = 0; i < 2; i++) configurations[i] = Publish(root, path, ids[i], i);
            var lifecycle = new PersistentSessionLifecycle(profile.Registry, () => 1, () => "entry-" + ++sequence,
                runtimeForAttachment: (_, generation, _) =>
                {
                    var registry = profile.Registry;
                    var ownership = profile.CaptureInitialRuntimeViewOwnership(new Resource(), generation);
                    return ValueTask.FromResult(new SessionRuntimeLease(registry, ownership.Resources, ownership.BindOwner));
                });
            var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
                { type = "session", version = 3, id = "native-view", timestamp = "2026-10-05T00:00:00.000Z", cwd = root }));
            var session = await lifecycle.CreateAsync(path, header, profile.SelectedModel);
            await profile.AttachOwnerAsync(session, lifecycle: lifecycle);
            var iteration = 0;
            Func<AgentSessionAttachment, AgentSessionCreationRequest, CancellationToken, ValueTask<bool>> foreignCreation =
                (_, _, _) => ValueTask.FromResult(true);
            profile.ConfigureReload(new(new(new object()), [], true, new([], []), new()
            {
                StageSettingsAsync = (_, _) => ValueTask.FromResult(new NativeHostReloadPayload(new object())),
                SyncQueueModesAsync = (_, _) => ValueTask.CompletedTask,
                ResetApiProvidersAsync = (_, _) => ValueTask.CompletedTask,
                ReloadResourcesAsync = (_, _) => ValueTask.CompletedTask,
                DescribeRuntimeAsync = (_, _) => ValueTask.FromResult(new HostReloadRuntime([], [])),
                BuildRuntimeAsync = async (candidate, _, token) =>
                {
                    if (iteration == 2)
                        return profile.PrepareRuntimeView(candidate, null, null, null, null, profile.Registry, new Resource(),
                            (owner, _) => owner.BeforeCreation = foreignCreation, () => { });
                    var preflight = await configurations[iteration++].PreflightAsync(path, root, token);
                    var extension = await NativeExtensionActivation.LoadAsync(preflight, token);
                    try
                    {
                        profile.BindPreparedExtension(extension);
                        var registry = new SessionRuntimeRegistry([new(profile.SelectedModel, new UnusedTransport())], [], new Policy());
                        return profile.PrepareRuntimeView(candidate, extension, null, null, null, registry, new Resource(), null, () => { });
                    }
                    catch { await extension.DisposeAsync(); throw; }
                },
                SessionShutdownAsync = (_, _, _) => ValueTask.CompletedTask,
                CleanupPreparationAsync = _ => ValueTask.CompletedTask,
                BeforeSessionStartAsync = (_, _) => ValueTask.CompletedTask,
                SessionStartAsync = (_, _, _) => ValueTask.CompletedTask,
                ReportUnhandledMcpServersAsync = (_, _) => ValueTask.CompletedTask,
                ExtendResourcesAsync = (_, _, _) => ValueTask.CompletedTask
            }));
            for (var i = 0; i < 2; i++)
            {
                var receipt = await profile.ReloadAsync(profile.Sessions!.Current);
                Check(receipt.Workflow.Failures.IsEmpty);
            }
            Check((await File.ReadAllLinesAsync(Path.Combine(root, "0.log"))).SequenceEqual(["start:reload", "shutdown:reload", "dispose"]));
            if (replaceWithEmpty)
            {
                var owner = profile.Sessions!;
                Check(owner.BeforeReplacement is not null && owner.BeforeCreation is not null);
                var empty = await profile.ReloadAsync(owner.Current);
                Check(empty.Workflow.Failures.IsEmpty);
                Check(ReferenceEquals(owner.BeforeCreation, foreignCreation));
                Check(owner.BeforeReplacement is null && owner.AfterReplacement is null && owner.BeforeRetirement is null &&
                    owner.ValidateTargetAttachment is null);
                Check((await File.ReadAllLinesAsync(Path.Combine(root, "1.log"))).SequenceEqual(["start:reload", "shutdown:reload", "dispose"]));
                await profile.DisposeAsync();
                return;
            }
            var retained = profile.CaptureShutdownSessionSnapshot();
            await profile.Sessions!.StopAdmissionAndJoinAsync();
            await profile.DispatchSessionShutdownAsync(retained);
            await profile.DisposeAsync();
            Check((await File.ReadAllLinesAsync(Path.Combine(root, "1.log"))).SequenceEqual(["start:reload", "shutdown:quit", "dispose"]));
        }
        finally
        {
            try { await profile.DisposeAsync(); } catch (Exception) { }
            foreach (var id in ids) AppContext.SetData("PiSharp.ProfileViewFixture." + id, null);
            if (Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) &&
                Path.GetFileName(root).StartsWith("pisharp-profile-native-", StringComparison.Ordinal)) Directory.Delete(root, true);
        }
    }
    private static NativeExtensionConfiguration Publish(string root, string session, string id, int index)
    {
        var package = Path.Combine(root, index + "-package"); var snapshots = Path.Combine(root, index + "-snapshots");
        Directory.CreateDirectory(package); Directory.CreateDirectory(snapshots); NativeSessionFixturePackage.CopyTo(package);
        var hashes = Directory.GetFiles(package).ToDictionary(file => Path.GetFileName(file)!,
            file => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))));
        var manifest = JsonSerializer.Serialize(new { schemaVersion = 0, id, packageVersion = "0.0.1",
            hostApiRange = new { minimum = "0.0.0", maximumExclusive = "0.1.0" }, runtimeKind = "native", assembly = NativeSessionFixturePackage.AssemblyFile,
            entryType = "NativeProfileViewFixture.Entry", tfm = "net10.0", rids = new[] { "win-x64" }, requiredFeatures = new[] { "owned-descriptor-callbacks" },
            declaredCapabilities = new[] { "observations" }, resourcePaths = hashes.Keys.Where(name => name != NativeSessionFixturePackage.AssemblyFile).ToArray(),
            explicitOverrides = Array.Empty<object>(), artifactHashes = hashes });
        var manifestPath = Path.Combine(root, index + "-manifest.json"); var approvalPath = Path.Combine(root, index + "-approval.json");
        File.WriteAllText(manifestPath, manifest);
        File.WriteAllText(approvalPath, JsonSerializer.Serialize(new { schemaVersion = 1, execution = "ApprovePublishedFixtureExecution",
            packageRoot = package, manifestValueSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))), artifactHashes = hashes,
            sourceScope = "Explicit", effectiveScopeId = "cli-native-explicit", policyRevision = "experimental-policy-0", hostGeneration = 1,
            sessionPath = session, workspace = root, snapshotRoot = snapshots, enabledTools = Array.Empty<string>() }));
        AppContext.SetData("PiSharp.ProfileViewFixture." + id, JsonData.Parse(JsonSerializer.Serialize(new { log = Path.Combine(root, index + ".log") })));
        return NativeExtensionConfiguration.Optional(package, manifestPath, approvalPath, snapshots, [])!;
    }
    private sealed class Resource : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    private sealed class Policy : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(new ToolActionAuthorization(false)); }
    private sealed class UnusedTransport : IChatTransport
    {
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        { await Task.FromException(new InvalidOperationException("No provider work admitted.")); yield break; }
    }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Native profile lifecycle contract failed."); }
}
