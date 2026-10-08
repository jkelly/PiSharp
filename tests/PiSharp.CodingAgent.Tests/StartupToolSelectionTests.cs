using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using PiSharp.Cli.Extensions;
using PiSharp.Cli.Settings;
using PiSharp.Cli.Commands;
using PiSharp.CodingAgent;
using PiSharp.CodingAgent.Configuration;
using PiSharp.Contracts;
using PiSharp.Sessions.Storage;

internal static class StartupToolSelectionTests
{
    internal const string Prefix = "startup tools ";
    internal static (string Name, Func<Task> Run)[] Cases() =>
    [
        (Prefix + "pinned plain modifier malformed and empty projection", Projection),
        (Prefix + "merged modifiers preserve ordered user project semantics", Merge),
        (Prefix + "RPC CLI precedence aliases ordering duplicates and explicit empty", RpcSelection),
        (Prefix + "unknown unavailable names and missing CLI values reject", Invalid),
        (Prefix + "resumed RPC settings override recorded tools and absent leaves them", Resume),
        (Prefix + "one shot create and resume apply settings and CLI empty", OneShot),
        (Prefix + "unavailable recorded Bash rejects absent override and explicit selection resumes safely", ResumeAdmissionBoundary),
        (Prefix + "admitted extension settings append CLI empty and named selection", AdmittedExtension),
        .. AllowedToolSelectionTests.Cases(),
        .. ToolSelectionCliFlagTests.Cases(),
        .. CoreResumeToolSelectionTests.Cases()
    ];
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; actual {actual}."); }
    private static void Names(IEnumerable<string> expected, IEnumerable<string> actual) => Equal(string.Join('|', expected), string.Join('|', actual));
    private static ImmutableArray<string>? Resolve(string json) => StartupToolSelection.Resolve(JsonData.Parse(json));
    private static Task Projection()
    {
        Equal(true, Resolve("{}") is null);
        foreach (var json in new[] { "[]", "null", "false", "{}", "[0,null,false]" }) Names([], Resolve("{\"defaultTools\":" + json + "}")!.Value);
        Names(["read", "edit", "write", "ls"], Resolve("{\"defaultTools\":[\"-bash\",\"+ls\",\"+read\",\"+\"]}")!.Value);
        Names(["write", "read", "ls"], Resolve("{\"defaultTools\":[\"read\",\"write\",\"read\",\"-read\",\"+ls\"]}")!.Value);
        Names(["read", "-"], Resolve("{\"defaultTools\":[\"read\",\"+-\"]}")!.Value);
        return Task.CompletedTask;
    }
    private static async Task Merge()
    {
        using var fixture = new StartupSettingsTests.Fixture(); var fs = new StartupSettingsTests.Files();
        fs.Text[fixture.User] = "{\"defaultTools\":[\"write\",\"read\"]}";
        fs.Text[fixture.Project] = "{\"defaultTools\":[\"-write\",\"+edit\",\"+ls\"]}";
        var settings = await StartupSettings.LoadAsync(new(fixture.User, fixture.Project), fs);
        Names(["read", "edit", "ls"], StartupToolSelection.Resolve(settings.Values)!.Value);
        fs.Text[fixture.Project] = "{\"defaultTools\":[]}";
        Names(["write", "read"], StartupToolSelection.Resolve((await StartupSettings.LoadAsync(new(fixture.User, fixture.Project), fs)).Values)!.Value);
    }
    private static async Task RpcSelection()
    {
        using var fixture = new StartupSettingsTests.Fixture(); var fs = new StartupSettingsTests.Files();
        fs.Text[fixture.User] = "{\"defaultTools\":[\"bash\"]}";
        await using (var host = new StartupSettingsTests.Host(fixture, fs,
            ["--user-settings", fixture.User, "--tools", "write", "-t", " ls, edit,read,edit,, "], "new-lazy"))
        {
            await host.Response("ready", "get_state");
            // As in the settings resume fixture, state-only lazy setup is not a durable reopen input.
            Equal(false, File.Exists(Path.Combine(fixture.Root, "session.jsonl")));
            await host.MaterializeConversation(); Equal(0, await host.Finish());
        }
        Equal(true, File.Exists(Path.Combine(fixture.Root, "session.jsonl")));
        Names(["ls", "edit", "read"], await DurableTools(fixture)); Equal(1, fs.Reads.Count);
        await using (var host = new StartupSettingsTests.Host(fixture, fs, ["--tools", ""], "open"))
        { await host.Response("ready", "get_state"); Equal(0, await host.Finish()); }
        Names([], await DurableTools(fixture));
    }
    private static async Task Invalid()
    {
        foreach (var flags in new[] { new[] { "--tools", "unknown" }, new[] { "--tools", "bash" },
            new[] { "--tools", "find" }, new[] { "--tools", "grep" }, new[] { "--tools", "+read" }, new[] { "-t" } })
        {
            using var fixture = new StartupSettingsTests.Fixture();
            await using var host = new StartupSettingsTests.Host(fixture, null, flags, "new-lazy");
            Equal(2, await host.Completion); Equal(false, File.Exists(Path.Combine(fixture.Root, "session.jsonl")));
            Equal(true, host.Error.ToString().Contains("InvalidArguments", StringComparison.Ordinal));
        }
    }
    private static async Task Resume()
    {
        using var fixture = new StartupSettingsTests.Fixture(); var fs = new StartupSettingsTests.Files();
        await Create(fixture, ["--tools", "edit,ls"]);
        await using (var host = new StartupSettingsTests.Host(fixture, fs, [], "open"))
        { await host.Response("ready", "get_state"); Equal(0, await host.Finish()); }
        Names(["edit", "ls"], await DurableTools(fixture)); Equal(0, fs.Reads.Count);
        fs.Text[fixture.User] = "{\"defaultTools\":[\"write\",\"read\",\"-write\",\"+ls\"]}";
        await using (var host = new StartupSettingsTests.Host(fixture, fs, ["--user-settings", fixture.User], "open"))
        { await host.Response("ready", "get_state"); Equal(0, await host.Finish()); }
        Names(["read", "ls"], await DurableTools(fixture));
    }
    private static async Task OneShot()
    {
        using var fixture = new StartupSettingsTests.Fixture();
        await File.WriteAllTextAsync(fixture.User, "{\"defaultTools\":[\"edit\",\"read\"]}");
        await Create(fixture, ["--user-settings", fixture.User]); Names(["edit", "read"], await DurableTools(fixture));
        using var output = new StringWriter(); using var error = new StringWriter(); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Equal(0, await SessionCommands.RunAsync(["session", "resume", "--session", Path.Combine(fixture.Root, "session.jsonl"),
            "--workspace", fixture.Root, "--offline-script", fixture.Script, "--message", "empty tools fixture", "--user-settings", fixture.User,
            "--tools", ""], output, error, stop.Token));
        var report = JsonData.Parse(output.ToString()).Value;
        Equal(0, report.GetProperty("requests")[0].GetProperty("toolNames").GetArrayLength());
        Names([], await DurableTools(fixture)); Equal("", error.ToString());
    }
    private static async Task ResumeAdmissionBoundary()
    {
        using var fixture = new StartupSettingsTests.Fixture();
        await Create(fixture, ["--tools", "edit"]);
        var path = Path.Combine(fixture.Root, "session.jsonl"); var changed = false;
        // An owned transcript fixture with a well-formed declaration whose current binding is absent.
        // No Bash runner, executable, command authorization or execution is admitted by this fixture.
        var lines = (await File.ReadAllLinesAsync(path)).Select(line =>
        {
            var record = JsonNode.Parse(line)!;
            if (record["message"]?["role"]?.GetValue<string>() == "system" && record["message"]?["toolsAdded"] is JsonArray tools)
                foreach (var tool in tools)
                    if (tool?["name"]?.GetValue<string>() == "edit") { tool!["name"] = "bash"; changed = true; }
            return record.ToJsonString();
        }).ToArray();
        Equal(true, changed); await File.WriteAllLinesAsync(path, lines);
        var original = await File.ReadAllBytesAsync(path);
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var profile = await OfflineSessionProfile.CreateAsync(fixture.Root, path, null, [], [], [], stop.Token);
            try
            {
                await using var unexpected = await PersistentAgentSession.OpenWithRegistryAsync(path, profile.Registry, () => 1,
                    () => Guid.NewGuid().ToString("N"), fallbackModel: profile.SelectedModel, cancellationToken: stop.Token);
                throw new InvalidOperationException("Unavailable recorded Bash binding was admitted.");
            }
            catch (SessionRuntimeRegistryException error) { Equal(SessionRuntimeRegistryFailure.UnknownTool, error.Failure); }
        }
        var afterRegistry = await File.ReadAllBytesAsync(path);
        Equal(true, original.SequenceEqual(afterRegistry));
        await File.WriteAllTextAsync(fixture.User, "{\"defaultTools\":[\"read\"]}");
        foreach (var flags in new[] { new[] { "--tools", "read" }, new[] { "--tools", "" }, new[] { "--user-settings", fixture.User } })
        {
            await File.WriteAllBytesAsync(path, original);
            var selected = flags.Contains("") ? Array.Empty<string>() : new[] { "read" };
            await using (var host = new StartupSettingsTests.Host(fixture, null, flags, "open"))
            { await host.Response("ready", "get_state"); Equal(0, await host.Finish()); }
            Names(selected, await DurableTools(fixture));
            var afterRpc = await File.ReadAllBytesAsync(path);
            Equal(true, afterRpc.Length > original.Length && afterRpc.Take(original.Length).SequenceEqual(original));
            await File.WriteAllBytesAsync(path, original);
            using var output = new StringWriter(); using var error = new StringWriter(); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Equal(0, await SessionCommands.RunAsync(["session", "resume", "--session", path, "--workspace", fixture.Root,
                "--offline-script", fixture.Script, "--message", "unavailable old binding", .. flags], output, error, stop.Token));
            Equal("", error.ToString()); Names(selected, await DurableTools(fixture));
            var afterCommand = await File.ReadAllBytesAsync(path);
            Equal(true, afterCommand.Length > original.Length && afterCommand.Take(original.Length).SequenceEqual(original));
        }
    }
    private static async Task AdmittedExtension()
    {
        using var fixture = new StartupSettingsTests.Fixture();
        var package = Path.Combine(fixture.Root, "package"); var snapshots = Path.Combine(fixture.Root, "snapshots");
        var path = Path.Combine(fixture.Root, "session.jsonl"); var manifestPath = Path.Combine(fixture.Root, "manifest.json");
        var approvalPath = Path.Combine(fixture.Root, "approval.json"); Directory.CreateDirectory(package); Directory.CreateDirectory(snapshots);
        NativeSessionFixturePackage.CopyTo(package);
        var hashes = Directory.GetFiles(package).Order(StringComparer.Ordinal).ToDictionary(file => Path.GetFileName(file)!,
            file => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))), StringComparer.Ordinal);
        var manifest = JsonSerializer.Serialize(new { schemaVersion = 0, id = "fixture.namespace", packageVersion = "0.0.1",
            hostApiRange = new { minimum = "0.0.0", maximumExclusive = "0.1.0" }, runtimeKind = "native", assembly = NativeSessionFixturePackage.AssemblyFile,
            entryType = "NativeToolNamespaceFixture.Entry", tfm = "net10.0", rids = new[] { "win-x64" },
            requiredFeatures = new[] { "owned-descriptor-callbacks" }, declaredCapabilities = new[] { "tools" },
            resourcePaths = hashes.Keys.Where(name => name != NativeSessionFixturePackage.AssemblyFile).ToArray(), explicitOverrides = Array.Empty<object>(), artifactHashes = hashes });
        await File.WriteAllTextAsync(manifestPath, manifest);
        await File.WriteAllTextAsync(approvalPath, JsonSerializer.Serialize(new { schemaVersion = 1, execution = "ApprovePublishedFixtureExecution",
            packageRoot = package, manifestValueSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))), artifactHashes = hashes,
            sourceScope = "Explicit", effectiveScopeId = "cli-native-explicit", policyRevision = "experimental-policy-0", hostGeneration = 1,
            sessionPath = path, workspace = fixture.Root, snapshotRoot = snapshots, enabledTools = new[] { NativeToolNamespaceFixture.Entry.Tool } }));
        var extension = NativeExtensionConfiguration.Optional(package, manifestPath, approvalPath, snapshots, [NativeToolNamespaceFixture.Entry.Tool]);
        foreach (var selection in new[] { new InitialToolSelection(["read"], true), new InitialToolSelection([], true), new InitialToolSelection([], false),
            new InitialToolSelection([NativeToolNamespaceFixture.Entry.Tool, "ls"], false) })
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var profile = await OfflineSessionProfile.CreateAsync(fixture.Root, path, null, [], [], [], stop.Token,
                extension: extension, toolSelection: selection);
            var expected = selection.IncludeDefaultExtensions ? selection.Names.Add(NativeToolNamespaceFixture.Entry.Tool).ToArray() : selection.Names.ToArray();
            Names(expected, profile.InitialSystem.Value.GetProperty("toolsAdded").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()!));
            Names(expected, profile.Registry.Resolve(profile.SelectedModel, [new("system", profile.InitialSystem)]).ActiveToolDeclarations
                .Select(tool => tool.Value.GetProperty("name").GetString()!));
        }
        // Removing explicit fixture approval must reject package admission even with an empty selection.
        await File.WriteAllTextAsync(approvalPath, "{}");
        try
        {
            await using var unexpected = await OfflineSessionProfile.CreateAsync(fixture.Root, path, null, [], [], [], default,
                extension: extension, toolSelection: new([], false));
            throw new InvalidOperationException("Unapproved fixture package was admitted.");
        }
        catch (NativeExtensionException error) { Equal(NativeExtensionFailure.ApprovalMismatch, error.Failure); }
        Equal(false, File.Exists(path));
    }
    private static async Task Create(StartupSettingsTests.Fixture fixture, string[] flags)
    {
        using var output = new StringWriter(); using var error = new StringWriter(); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Equal(0, await SessionCommands.RunAsync(["session", "create", "--session", Path.Combine(fixture.Root, "session.jsonl"),
            "--workspace", fixture.Root, .. flags], output, error, stop.Token)); Equal("", error.ToString());
    }
    private static async Task<ImmutableArray<string>> DurableTools(StartupSettingsTests.Fixture fixture)
    {
        var path = Path.Combine(fixture.Root, "session.jsonl");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var profile = await OfflineSessionProfile.CreateAsync(fixture.Root, path, null, [], [], [], stop.Token);
        var storage = new StorageOpenWitness();
        try
        {
            await using var session = await PersistentAgentSession.OpenWithRegistryAsync(path, profile.Registry, () => 1, () => Guid.NewGuid().ToString("N"),
                options: new(SessionLogStoreOptions: new(StorageFactory: storage)), fallbackModel: profile.SelectedModel, cancellationToken: stop.Token);
            return session.GetActiveTools();
        }
        catch (SessionLogStoreException error)
        {
            // The production store intentionally sanitizes storage exceptions; retain the original only in this owned fixture.
            throw new InvalidOperationException($"DurableTools reopen failed: {error.Failure}; exists={File.Exists(path)}; " +
                $"storageOpenFailure={storage.Failure?.GetType().FullName ?? "none"}.", storage.Failure ?? error);
        }
    }
    private sealed class StorageOpenWitness : ISessionLogStorageFactory
    {
        internal Exception? Failure { get; private set; }
        public async ValueTask<ISessionLogStorage> OpenAsync(string path, bool createNew, CancellationToken token)
        {
            try { return await SessionLogStore.DefaultStorageFactory.OpenAsync(path, createNew, token); }
            catch (Exception error) { Failure = error; throw; }
        }
    }
}
