using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Extensions;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;

internal static class NativeToolNamespaceTests
{
    internal const string Prefix = "native namespace ";
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
        [
            (Prefix + "fixture package stays isolated from CLI and NuGet dependencies", () => { NativeSessionFixturePackage.VerifyOutput(); return Task.CompletedTask; }),
            (Prefix + "actual native loader CLI mapping and persistent session prepare namespace metadata", ActualProfile)
        ];

    private static async Task ActualProfile()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-native-namespace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var package = Path.Combine(root, "package"); var snapshots = Path.Combine(root, "snapshots");
        var path = Path.Combine(root, "session.jsonl"); var manifestPath = Path.Combine(root, "manifest.json");
        var approvalPath = Path.Combine(root, "approval.json"); Directory.CreateDirectory(package); Directory.CreateDirectory(snapshots);
        try
        {
            // Package only the isolated extension fixture, never the CLI/test host dependency graph.
            NativeSessionFixturePackage.CopyTo(package);
            var hashes = Directory.GetFiles(package).Order(StringComparer.Ordinal).ToDictionary(file => Path.GetFileName(file)!, Hash, StringComparer.Ordinal);
            var manifest = JsonSerializer.Serialize(new { schemaVersion = 0, id = "fixture.namespace", packageVersion = "0.0.1",
                hostApiRange = new { minimum = "0.0.0", maximumExclusive = "0.1.0" }, runtimeKind = "native", assembly = NativeSessionFixturePackage.AssemblyFile,
                entryType = "NativeToolNamespaceFixture.Entry", tfm = "net10.0", rids = new[] { "win-x64" },
                requiredFeatures = new[] { "owned-descriptor-callbacks" }, declaredCapabilities = new[] { "tools" },
                resourcePaths = hashes.Keys.Where(name => name != NativeSessionFixturePackage.AssemblyFile).ToArray(), explicitOverrides = Array.Empty<object>(), artifactHashes = hashes });
            await File.WriteAllTextAsync(manifestPath, manifest);
            await File.WriteAllTextAsync(approvalPath, JsonSerializer.Serialize(new { schemaVersion = 1, execution = "ApprovePublishedFixtureExecution",
                packageRoot = package, manifestValueSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))), artifactHashes = hashes,
                sourceScope = "Explicit", effectiveScopeId = "cli-native-explicit", policyRevision = "experimental-policy-0", hostGeneration = 1,
                sessionPath = path, workspace = root, snapshotRoot = snapshots, enabledTools = new[] { NativeToolNamespaceFixture.Entry.Tool } }));
            var extension = NativeExtensionConfiguration.Optional(package, manifestPath, approvalPath, snapshots, [NativeToolNamespaceFixture.Entry.Tool]);
            await using var profile = await OfflineSessionProfile.CreateAsync(root, path, null,
                [JsonData.Parse(JsonSerializer.Serialize(SessionCommandTests.CompletionsText("namespace response")))], [], [], default,
                offlineApi: "openai-completions", extension: extension);
            var system = new TranscriptEntry("system", profile.InitialSystem);
            var selection = profile.Registry.Resolve(profile.SelectedModel, [system]);
            Check(selection.LoadoutDiagnostics.IsEmpty, "Namespace lookup failed in actual leased CLI callback.");
            var projected = await selection.Configuration.Hooks!.FinalTransformRequestMessages!([system], default);
            var declaration = new SessionSystemReplay().Replay(projected).Tools.Single(tool => tool.Value.GetProperty("name").GetString() == NativeToolNamespaceFixture.Entry.Tool);
            Check(declaration.Value.GetProperty("description").GetString() == NativeToolNamespaceFixture.Entry.PreparedDescription &&
                !declaration.Value.TryGetProperty("namespace", out _), "CLI namespace metadata was dropped or serialized as execution authority.");
            var sequence = 0;
            var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "namespace-cli",
                timestamp = "2026-10-04T00:00:00.000Z", cwd = root }));
            var session = await PersistentAgentSession.CreateAsync(path, header, profile.Registry, profile.SelectedModel, () => 1, () => "entry-" + ++sequence);
            try
            {
                await session.ConfigureAsync(new(SystemMessage: system)); profile.AttachOwner(session);
                await session.PromptAsync(new TranscriptEntry("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"namespace test\",\"timestamp\":1}")));
                Check(profile.UsedTurns == 1 && profile.Actions.Length == 0 && session.Snapshot.Fault is null &&
                    session.GetActiveTools().Contains(NativeToolNamespaceFixture.Entry.Tool), "Actual session request/effect/activation path changed.");
                var canonical = new SessionSystemReplay().Replay(session.Snapshot.Context.LlmMessages).Tools
                    .Single(tool => tool.Value.GetProperty("name").GetString() == NativeToolNamespaceFixture.Entry.Tool);
                Check(canonical.Value.GetProperty("description").GetString() == "canonical namespace probe",
                    "Namespace preparation changed canonical durable declarations.");
            }
            finally { await session.DisposeAsync(); }
        }
        finally
        {
            // All profile/loader/registry/session originals are awaited before owned test files are removed.
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
