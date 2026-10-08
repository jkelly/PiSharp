using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Interactive;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal sealed class StartupOwnedFiles
{
    private StartupOwnedFiles(string root) { Root = root; }
    internal string Root { get; }
    internal string Session => Path.Combine(Root, "session.jsonl");
    internal string Script => Path.Combine(Root, "script.json");
    internal string Target => Path.Combine(Root, "effect.txt");
    internal string Package => Path.Combine(Root, "package");
    internal string Manifest => Path.Combine(Root, "manifest.json");
    internal string Approval => Path.Combine(Root, "approval.json");
    internal string Settings => Path.Combine(Root, "settings");
    internal string Snapshots => Path.Combine(Root, "snapshots");
    internal string Prompt => "startup-workflow";
    internal string Saved => "startup-approved 文🙂\n";
    internal bool Plugin { get; private set; }
    internal TerminalKeybindingConfiguration Configuration { get; private set; } = null!;
    internal string[] ExtensionArgs => Plugin ? ["--extension-package", Package, "--extension-manifest", Manifest, "--extension-approval", Approval,
        "--extension-snapshot-root", Snapshots, "--enable-extension-tool", "fixture.startup.result"] : [];
    internal string[] Args() => new[] { "session", "terminal", "--terminal-preview", "--session", Session, "--workspace", Root,
        "--offline-api", "openai-responses", "--offline-script", Script, "--allow-write", Target }.Concat(ExtensionArgs).ToArray();
    internal static async Task<StartupOwnedFiles> Create(string reviewRoot, bool plugin, ConsumerEvidence e)
    {
        var parent = Path.Combine(Path.GetFullPath(reviewRoot), "artifacts", "startup-selector-consumer-runs"); Directory.CreateDirectory(parent);
        var root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        StartupBarrierCases.Check(!Directory.Exists(root), "Fresh owned workflow directory required."); Directory.CreateDirectory(root);
        var f = new StartupOwnedFiles(root) { Plugin = plugin };
        Directory.CreateDirectory(f.Settings); Directory.CreateDirectory(f.Snapshots);
        await NewFile(Path.Combine(root, "ownership.json"), JsonSerializer.Serialize(new { root, source = "admitted runtime consumer", settingsOwned = true }));
        await NewFile(Path.Combine(f.Settings, "keybindings.json"), "{\"tui.select.down\":\"x\",\"tui.select.confirm\":\"ctrl+j\"}");
        f.Configuration = TerminalKeybindingConfigurationLoader.Load(f.Settings, "win32", new Dictionary<string, string?>());
        StartupBarrierCases.Check(f.Configuration.LoadStatus == "loaded" && f.Configuration.ConfigPath == Path.Combine(f.Settings, "keybindings.json"), "Command configuration escaped owned settings or failed to load.");
        if (plugin)
        {
            // Copy the separately admitted, project-only fixture publication, never the YAML-enabled host graph.
            var hashes = PreparePublishedFixture(reviewRoot, f.Package, e);
            var entry = Path.Combine(f.Package, FixtureAssembly + ".dll");
            var manifest = JsonSerializer.Serialize(new { schemaVersion = 0, id = "fixture.startup.result", packageVersion = "0.0.1",
                hostApiRange = new { minimum = "0.0.0", maximumExclusive = "0.1.0" }, runtimeKind = "native", assembly = FixtureAssembly + ".dll",
                entryType = "StartupPublishedFixture.Entry", tfm = "net10.0", rids = new[] { "win-x64" },
                requiredFeatures = new[] { "owned-descriptor-callbacks", "registered-input-tool-reducers" }, declaredCapabilities = new[] { "tools", "observations" },
                resourcePaths = hashes.Keys.Where(x => x != FixtureAssembly + ".dll").ToArray(), explicitOverrides = Array.Empty<object>(), artifactHashes = hashes });
            await NewFile(f.Manifest, manifest);
            await NewFile(f.Approval, JsonSerializer.Serialize(new { schemaVersion = 1, execution = "ApprovePublishedFixtureExecution", packageRoot = f.Package,
                manifestValueSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))), artifactHashes = hashes,
                sourceScope = "Explicit", effectiveScopeId = "cli-native-explicit", policyRevision = "experimental-policy-0", hostGeneration = 1,
                sessionPath = f.Session, workspace = f.Root, snapshotRoot = f.Snapshots, enabledTools = new[] { "fixture.startup.result" } }));
            e.Observe("runtime-owned-native-startup-package-pins", new { f.Package, entry, entrySha256 = Hash(entry), manifestSha256 = Hash(f.Manifest), approvalSha256 = Hash(f.Approval), artifacts = hashes });
        }
        object[] turns = plugin ? [Tool("fixture.startup.result", "startup-result", new { }, [f.Prompt]),
            Tool("write", "startup-effect", new { path = f.Target, content = f.Saved }, ["startup:startup-approved"]), Text("STARTUP_DONE 文🙂", ["Successfully wrote"])] : [Text("unused")];
        await NewFile(f.Script, JsonSerializer.Serialize(new { schemaVersion = 1, turns }));
        // Existing real create route in process; no subprocess, process termination or global environment changes.
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        var result = await SessionCommands.RunAsync(new[] { "session", "create", "--session", f.Session, "--workspace", f.Root,
            "--offline-api", "openai-responses" }.Concat(f.ExtensionArgs).ToArray(), stdout, stderr);
        e.Observe("actual-owned-session-create", new { result, output = stdout.ToString(), error = stderr.ToString(), f.Root, settingsSha256 = Hash(f.Configuration.ConfigPath) });
        StartupBarrierCases.Check(result == 0 && stderr.ToString().Length == 0, "Actual command did not create the owned startup session.");
        using var receipt = JsonDocument.Parse(stdout.ToString());
        StartupBarrierCases.Check(receipt.RootElement.GetProperty("durableCheckpointAcknowledged").GetBoolean(), "Actual create did not acknowledge its durable checkpoint.");
        return f;
    }
    internal async Task Complete(ConsumerEvidence e)
    {
        var log = await new SessionLogReader().ReadFileAsync(Session);
        e.Observe("original-command-returned-durable-log", new { log.SourceComplete, status = log.Status.ToString(), log.ValidatedPrefixByteLength,
            originalBytes = log.OriginalBytes.Length, physicalBytes = new FileInfo(Session).Length, path = Session, sha256 = Hash(Session) });
        StartupBarrierCases.Check(log.SourceComplete && log.Status == SessionLogReadStatus.Complete && log.ValidatedPrefixByteLength == log.OriginalBytes.Length &&
            new FileInfo(Session).Length == log.OriginalBytes.Length, "Command returned without a complete physical durable log.");
        await using var exclusive = await SessionLogStore.OpenAsync(Session);
        StartupBarrierCases.Check(exclusive.Snapshot.CommittedByteLength == log.OriginalBytes.Length, "Original host retained the writer or durable acknowledgement differs.");
    }
    internal async Task AssertWriterOwned()
    {
        try { await using var unexpected = await SessionLogStore.OpenAsync(Session); }
        catch (SessionLogStoreException error) when (error.Failure == SessionLogStoreFailure.OpenFailed) { return; }
        throw new InvalidOperationException("Live original RPC host did not exclusively own its durable writer.");
    }
    internal static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    internal const string FixtureAssembly = "PublishedFixture.TerminalCompanion";
    internal const string FixtureOutput = "artifacts/extensions/published-fixtures/terminal-companion";

    internal static Dictionary<string, string> PreparePublishedFixture(string reviewRoot, string package, ConsumerEvidence e)
    {
        var output = Path.Combine(Path.GetFullPath(reviewRoot), FixtureOutput);
        var names = new[] { FixtureAssembly + ".dll", FixtureAssembly + ".deps.json", FixtureAssembly + ".runtimeconfig.json" };
        var depsPath = Path.Combine(output, names[1]);
        var depsBytes = File.ReadAllBytes(depsPath);
        using var deps = JsonDocument.Parse(depsBytes);
        RequireFixtureLibraries(deps.RootElement);
        // Focused regression: the real generated closure must be package-free, and an
        // external package must be rejected without changing any published artifact.
        var negative = System.Text.Json.Nodes.JsonNode.Parse(depsBytes)!.AsObject();
        negative["libraries"]!.AsObject()["YamlDotNet/16.3.0"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "package" };
        using var rejected = JsonDocument.Parse(negative.ToJsonString());
        var rejectedPackage = false;
        try { RequireFixtureLibraries(rejected.RootElement); }
        catch (InvalidDataException) { rejectedPackage = true; }
        StartupBarrierCases.Check(rejectedPackage, "Published fixture dependency guard accepted an external package.");
        Directory.CreateDirectory(package);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var source = Path.Combine(output, name); var destination = Path.Combine(package, name);
            File.Copy(source, destination, overwrite: false);
            var hash = Hash(source);
            StartupBarrierCases.Check(Hash(destination) == hash, "Published fixture copy changed original bytes: " + name);
            hashes.Add(name, hash);
        }
        e.Observe("published-terminal-fixture-original-dependency-closure", new { output, package,
            depsSha256 = Hash(depsPath), artifacts = hashes, externalPackageRejected = rejectedPackage,
            originalDepsAndRuntimeConfigPreserved = true });
        return hashes;
    }

    private static void RequireFixtureLibraries(JsonElement deps)
    {
        var allowed = new HashSet<string>([FixtureAssembly, "PiSharp.Contracts", "PiSharp.Extensions.Abstractions"], StringComparer.Ordinal);
        var entryPresent = false;
        foreach (var library in deps.GetProperty("libraries").EnumerateObject())
        {
            var identity = library.Name.Split('/');
            if (identity.Length != 2 || !allowed.Remove(identity[0]) ||
                library.Value.GetProperty("type").GetString() is not ("project" or "reference"))
                throw new InvalidDataException("Published terminal fixture has an unsupported dependency: " + library.Name);
            entryPresent |= identity[0] == FixtureAssembly;
        }
        if (!entryPresent) throw new InvalidDataException("Published terminal fixture is missing its entry dependency.");
    }
    private static async Task NewFile(string path, string text)
    { await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); await file.WriteAsync(Encoding.UTF8.GetBytes(text)); await file.FlushAsync(); }
    private static object Tool(string name, string call, object arguments, string[] required) => new { requiredInputTexts = required, events = new object[]
    {
        new { type = "response.output_item.added", output_index = 0, item = new { type = "function_call", id = "fc-" + call, call_id = call, name, arguments = "" } },
        new { type = "response.output_item.done", output_index = 0, item = new { type = "function_call", id = "fc-" + call, call_id = call, name, arguments = JsonSerializer.Serialize(arguments) } }, Completed()
    } };
    private static object Text(string text, string[]? required = null) => new { requiredInputTexts = required ?? [], events = new object[]
    {
        new { type = "response.output_item.added", output_index = 0, item = new { type = "message", id = "startup-text", content = Array.Empty<object>() } },
        new { type = "response.output_text.delta", output_index = 0, item_id = "startup-text", delta = text },
        new { type = "response.output_item.done", output_index = 0, item = new { type = "message", id = "startup-text", content = new[] { new { type = "output_text", text } } } }, Completed()
    } };
    private static object Completed() => new { type = "response.completed", response = new { status = "completed", output = Array.Empty<object>(), usage = new { input_tokens = 8, output_tokens = 4, total_tokens = 12 } } };
}
