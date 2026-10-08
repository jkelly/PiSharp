using System.Text.Json;
using PiSharp.Cli.Interactive;
using PiSharp.Tui.Input;

internal static class ConfigurationLoadingCases
{
    internal static readonly string[] Ids = ["missing-file", "unreadable-file", "invalid-json", "null-root", "boolean-root",
        "empty-array-root", "one-bom", "two-boms", "definition-order", "array-shape-and-dedup", "empty-array-disables",
        "null-filtered", "invalid-values-filtered", "legacy-migration", "modern-wins-collision", "modern-null-blocks-legacy",
        "extras-and-numeric-order", "array-root-unknowns", "duplicate-property-last", "untrimmed-key-preserved"];

    internal static void Run(JsonElement fixture, ConsumerEvidence evidence)
    {
        var scenarios = fixture.GetProperty("cases").EnumerateArray().ToArray();
        if (!scenarios.Select(row => row.GetProperty("id").GetString()).SequenceEqual(Ids))
            throw new InvalidDataException("Authored configuration plan differs from admitted fixture.");
        foreach (var scenario in scenarios)
        {
            var id = scenario.GetProperty("id").GetString()!;
            evidence.Begin("configuration", id, scenario.Clone());
            var reads = new List<string>();
            var configuration = TerminalKeybindingConfigurationLoader.Load("C:\\authored-agent", "win32", readText: path =>
            {
                reads.Add(path);
                if (scenario.GetProperty("readError").GetBoolean()) throw new IOException("Authored unavailable keybindings file");
                return scenario.GetProperty("raw").GetString();
            });
            var registry = configuration.CreateBindings();
            var users = registry.GetUserBindings().Select(pair => new
            { id = pair.Key, value = Shape(pair.Value!) }).ToArray();
            var expected = scenario.GetProperty("expected");
            var expectedUsers = expected.GetProperty("users").EnumerateArray().ToArray();
            // Compare JSON values independent of whitespace in the authored fixture.
            var passed = configuration.LoadStatus == expected.GetProperty("status").GetString() &&
                configuration.Migrated == expected.GetProperty("migrated").GetBoolean() && reads.Count == 1 &&
                reads[0] == "C:\\authored-agent\\keybindings.json" && users.Length == expectedUsers.Length &&
                users.Select((value, index) => value.id == expectedUsers[index].GetProperty("id").GetString() &&
                    Equivalent(value.value, expectedUsers[index].GetProperty("value"))).All(value => value);
            evidence.Observe("configuration-loaded", new { configuration.AgentDirectory, configuration.ConfigPath,
                configuration.Platform, configuration.LoadStatus, configuration.Migrated, reads, users,
                resolvedLeft = registry.GetKeys("tui.editor.cursorLeft"), resolvedSubmit = registry.GetKeys("tui.input.submit") });
            evidence.Complete(new { id, passed, authoredNotSourceCaptured = true });
        }
    }

    private static bool Equivalent(JsonElement left, JsonElement right) => left.ValueKind == right.ValueKind &&
        (left.ValueKind == JsonValueKind.String ? left.GetString() == right.GetString() :
            left.EnumerateArray().Select(value => value.GetString()).SequenceEqual(right.EnumerateArray().Select(value => value.GetString())));
    private static JsonElement Shape(TerminalKeybindingValue value) => value.IsScalar ?
        JsonSerializer.SerializeToElement(value.Scalar) : JsonSerializer.SerializeToElement(value.Keys);

    internal static object Controls(ConsumerEvidence evidence, string reviewRoot)
    {
        var controls = new List<object>(); var allPassed = true;
        void Check(string id, bool passed, object observation)
        { allPassed &= passed; var row = new { id, passed, observation }; controls.Add(row); evidence.Observe("configuration-control", row); }
        var baseIds = TerminalKeybindingDefinitions.Tui.Select(pair => pair.Key).ToArray();
        foreach (var profile in new[]
        {
            (Name: "windows", Platform: "win32", Environment: new Dictionary<string, string?>(), Undo: "ctrl+z", Previous: "ctrl+up", Follow: "ctrl+q", Suspend: Array.Empty<string>(), Tree: new[] { "ctrl+left", "alt+left" }),
            (Name: "wsl-distro", Platform: "linux", Environment: new Dictionary<string, string?> { ["WSL_DISTRO_NAME"] = "Ubuntu" }, Undo: "alt+z", Previous: "ctrl+up", Follow: "ctrl+q", Suspend: new[] { "ctrl+z" }, Tree: new[] { "ctrl+left", "alt+left" }),
            (Name: "wsl-interop", Platform: "linux", Environment: new Dictionary<string, string?> { ["WSL_INTEROP"] = " " }, Undo: "alt+z", Previous: "ctrl+up", Follow: "ctrl+q", Suspend: new[] { "ctrl+z" }, Tree: new[] { "ctrl+left", "alt+left" }),
            (Name: "linux", Platform: "linux", Environment: new Dictionary<string, string?>(), Undo: "ctrl+-", Previous: "ctrl+shift+up", Follow: "alt+enter", Suspend: new[] { "ctrl+z" }, Tree: new[] { "ctrl+left", "alt+left" }),
            (Name: "mac", Platform: "darwin", Environment: new Dictionary<string, string?> { ["WSL_DISTRO_NAME"] = "ignored" }, Undo: "ctrl+-", Previous: "ctrl+shift+up", Follow: "alt+enter", Suspend: new[] { "ctrl+z" }, Tree: new[] { "alt+left", "ctrl+left" })
        })
        {
            var configuration = TerminalKeybindingConfigurationLoader.Load("authored", profile.Platform, profile.Environment, _ => null);
            var registry = configuration.CreateBindings(); var definitions = TerminalAgentKeybindingDefinitions.Create(profile.Platform, profile.Environment);
            Check(profile.Name, definitions.Count == 90 && definitions.Take(47).Select(pair => pair.Key).SequenceEqual(baseIds) &&
                definitions.All(pair => pair.Value.Description is not null) && registry.GetKeys("tui.editor.undo").SequenceEqual(new[] { profile.Undo }) &&
                registry.GetKeys("tui.altScreen.previousPrompt")[0] == profile.Previous && registry.GetKeys("app.message.followUp").SequenceEqual(new[] { profile.Follow }) &&
                registry.GetKeys("app.suspend").SequenceEqual(profile.Suspend) && registry.GetKeys("app.tree.foldOrUp").SequenceEqual(profile.Tree),
                new { profile.Name, profile.Platform, profile.Environment, definitionIds = definitions.Select(pair => pair.Key).ToArray(),
                    resolved = registry.GetResolvedBindings().Select(pair => new { id = pair.Key, value = Shape(pair.Value) }).ToArray() });
        }
        var content = "{\"cursorLeft\":\"alt+j\",\"app.exit\":\"ctrl+e\"}";
        var env = new Dictionary<string, string?> { ["WSL_DISTRO_NAME"] = "Ubuntu" }; var reads = new List<string>();
        var first = TerminalKeybindingConfigurationLoader.Load("agent", "linux", env, path => { reads.Add(path); return content; });
        env["WSL_DISTRO_NAME"] = null; content = "{\"tui.editor.cursorRight\":\"alt+k\"}"; var second = first.Reload();
        var mutable = first.CreateBindings(); mutable.SetUserBindings([]);
        Check("reload-replaces-and-owner-snapshots", first.CreateBindings().GetKeys("tui.editor.cursorLeft").SequenceEqual(new[] { "alt+j" }) &&
            second.CreateBindings().GetKeys("tui.editor.cursorLeft").SequenceEqual(TerminalKeybindingDefinitions.Tui.Single(pair => pair.Key == "tui.editor.cursorLeft").Value.DefaultKeys.Keys) &&
            second.CreateBindings().GetKeys("tui.editor.cursorRight").SequenceEqual(new[] { "alt+k" }) &&
            second.CreateBindings().GetKeys("tui.editor.undo").SequenceEqual(new[] { "alt+z" }) && reads.SequenceEqual(new[] { "agent/keybindings.json", "agent/keybindings.json" }),
            new { reads, first = first.CreateBindings().GetUserBindings().Keys.ToArray(), second = second.CreateBindings().GetUserBindings().Keys.ToArray(), liveOwnerReloadImplemented = false });
        foreach (var bounded in new[]
        {
            (Id: "character-bound", Raw: new string(' ', TerminalKeybindingConfigurationLoader.MaximumConfigurationCharacters + 1)),
            (Id: "entry-bound", Raw: JsonSerializer.Serialize(Enumerable.Range(0, 1025).ToDictionary(index => "x" + index, _ => "a"))),
            (Id: "binding-array-bound", Raw: JsonSerializer.Serialize(new Dictionary<string, string[]> { ["app.exit"] = Enumerable.Repeat("a", 257).ToArray() })),
            (Id: "key-character-bound", Raw: JsonSerializer.Serialize(new Dictionary<string, string> { ["app.exit"] = new string('x', 257) })),
            (Id: "json-depth-bound", Raw: new string('[', 65) + "0" + new string(']', 65))
        })
        {
            var configuration = TerminalKeybindingConfigurationLoader.Load("agent", "win32", readText: _ => bounded.Raw);
            Check(bounded.Id, configuration.LoadStatus == "unreadable-invalid-or-bounded" && configuration.CreateBindings().GetUserBindings().Count == 0,
                new { characters = bounded.Raw.Length, configuration.LoadStatus, nativeAdaptation = true });
        }
        foreach (var path in new[]
        {
            (Id: "default-win", Platform: "win32", Home: "C:\\Users\\test", Input: (string?)null, Expected: "C:\\Users\\test\\.pi\\agent"),
            (Id: "default-posix", Platform: "linux", Home: "/home/test", Input: (string?)null, Expected: "/home/test/.pi/agent"),
            (Id: "tilde", Platform: "darwin", Home: "/Users/test", Input: (string?)"~/custom/../agent", Expected: "/Users/test/agent"),
            (Id: "windows-shell", Platform: "win32", Home: "C:\\Users\\test", Input: (string?)"/mnt/d/test/agent", Expected: "D:\\test\\agent"),
            (Id: "windows-file-url", Platform: "win32", Home: "C:\\Users\\test", Input: (string?)"file:///D:/test%20agent", Expected: "D:\\test agent"),
            (Id: "posix-file-url", Platform: "linux", Home: "/home/test", Input: (string?)"file:///tmp/test%20agent", Expected: "/tmp/test agent"),
            (Id: "untrimmed-env", Platform: "linux", Home: "/home/test", Input: (string?)" agent ", Expected: " agent ")
        })
        {
            var actual = TerminalKeybindingConfigurationLoader.ResolveAgentDirectory(path.Home, path.Platform,
                new Dictionary<string, string?> { ["PI_CODING_AGENT_DIR"] = path.Input });
            Check(path.Id, actual == path.Expected, new { path.Home, path.Platform, path.Input, path.Expected, actual });
        }
        var directory = Path.Combine(reviewRoot, "artifacts", "keybindings-file-control-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "keybindings.json"); var bytes = new System.Text.UTF8Encoding(false).GetBytes("\uFEFF{\"app.exit\":\"ctrl+e\"}");
        using (var writer = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { writer.Write(bytes); writer.Flush(true); }
        var loadedFile = TerminalKeybindingConfigurationLoader.Load(directory, OperatingSystem.IsWindows() ? "win32" : "linux");
        Check("actual-read-only-file-and-bom", loadedFile.CreateBindings().GetKeys("app.exit").SequenceEqual(new[] { "ctrl+e" }) &&
            File.ReadAllBytes(file).SequenceEqual(bytes), new { file, sha256 = EvidenceAdmission.Hash(file), retainedForReview = true });
        return new { passed = allPassed, controls, sourceCaptured = false, platformProfilesExecutedOnPhysicalTerminals = false };
    }

    internal static readonly string[] DefaultIds = Ids.Select(id => "load-default-" + id).Concat(new[]
    {
        "load-default-empty-wsl", "load-default-distro-wsl", "load-default-interop-wsl", "load-default-file-url",
        "load-default-tilde", "load-default-reload-replacement", "load-default-restoration-fault"
    }).ToArray();

    internal static void RunLoadDefault(JsonElement fixture, ConsumerEvidence evidence, string reviewRoot)
    {
        using var profiles = JsonDocument.Parse(DefaultAgentDefinitionProfiles);
        foreach (var id in DefaultIds)
        {
            evidence.Begin("load-default", id, new { callsActualLoadDefault = true, isolatedProcessEnvironment = true, authoredNotSourceCaptured = true });
            if (id == "load-default-restoration-fault")
            {
                var restoredAfterFault = false;
                try
                {
                    IsolateDefault(reviewRoot, evidence, scope =>
                    {
                        scope.Configure(scope.AgentDirectory, "authored-wsl", "authored-interop");
                        scope.CreateSettings("{}"); var configuration = TerminalKeybindingConfigurationLoader.LoadDefault();
                        evidence.Observe("default-fault-loaded", new { configuration.LoadStatus, configuration.ConfigPath });
                        throw new IOException("Authored LoadDefault failure after process-environment mutation");
                    }).GetAwaiter().GetResult();
                }
                catch (IOException error) when (error.Message == "Authored LoadDefault failure after process-environment mutation")
                { restoredAfterFault = true; }
                evidence.Complete(new { passed = restoredAfterFault, restorationOnFaultVerified = restoredAfterFault }); continue;
            }
            var row = fixture.GetProperty("cases").EnumerateArray().FirstOrDefault(value =>
                "load-default-" + value.GetProperty("id").GetString() == id);
            var result = IsolateDefault(reviewRoot, evidence, scope =>
            {
                if (row.ValueKind != JsonValueKind.Undefined)
                {
                    var raw = row.GetProperty("raw").GetString(); var unreadable = row.GetProperty("readError").GetBoolean();
                    if (raw is not null || unreadable) scope.CreateSettings(raw ?? "{}");
                    var before = scope.SettingsHash(); TerminalKeybindingConfiguration configuration;
                    using (var denied = unreadable ? new FileStream(scope.SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null)
                        configuration = TerminalKeybindingConfigurationLoader.LoadDefault();
                    var after = scope.SettingsHash(); var expected = row.GetProperty("expected");
                    var users = configuration.CreateBindings().GetUserBindings().Select(pair => new { id = pair.Key, value = Shape(pair.Value!) }).ToArray();
                    var expectedUsers = expected.GetProperty("users").EnumerateArray().ToArray();
                    var passed = configuration.LoadStatus == expected.GetProperty("status").GetString() &&
                        configuration.Migrated == expected.GetProperty("migrated").GetBoolean() && before == after &&
                        users.Length == expectedUsers.Length && users.Select((value, index) => value.id == expectedUsers[index].GetProperty("id").GetString() &&
                            Equivalent(value.value, expectedUsers[index].GetProperty("value"))).All(value => value) &&
                        ExactDefaultConfiguration(configuration, scope, profiles.RootElement, scope.Profile);
                    evidence.Observe("actual-load-default-file-case", new { id, completeAuthoredScenario = row.Clone(), configuration.LoadStatus,
                        configuration.Migrated, configuration.Platform, configuration.AgentDirectory, configuration.ConfigPath, before, after, users,
                        unreadableUsesOwnedExclusiveFile = unreadable });
                    return Task.FromResult<object>(new { id, passed, actualDefaultLoaderInvoked = true, authoredNotSourceCaptured = true });
                }
                if (id == "load-default-reload-replacement")
                {
                    scope.Configure(scope.AgentDirectory, "authored-wsl", null); scope.CreateSettings("{\"cursorLeft\":\"alt+j\",\"exit\":\"ctrl+e\"}");
                    var initialProfile = scope.Profile; var initial = TerminalKeybindingConfigurationLoader.LoadDefault();
                    var prior = Path.Combine(scope.AgentDirectory, "keybindings.original.json"); File.Move(scope.SettingsPath, prior, overwrite: false);
                    scope.CreateSettings("{\"tui.editor.cursorRight\":\"alt+k\"}"); scope.Configure(scope.AgentDirectory, null, null);
                    var reloaded = initial.Reload(); var fresh = TerminalKeybindingConfigurationLoader.LoadDefault();
                    var passed = initial.CreateBindings().GetKeys("tui.editor.cursorLeft").SequenceEqual(new[] { "alt+j" }) &&
                        reloaded.CreateBindings().GetKeys("tui.editor.cursorLeft").SequenceEqual(new[] { "left", "ctrl+b" }) &&
                        reloaded.CreateBindings().GetKeys("tui.editor.cursorRight").SequenceEqual(new[] { "alt+k" }) &&
                        !reloaded.CreateBindings().GetUserBindings().ContainsKey("app.exit") &&
                        ExactDefaultConfiguration(reloaded, scope, profiles.RootElement, initialProfile) && ExactDefaultConfiguration(fresh, scope, profiles.RootElement, scope.Profile);
                    evidence.Observe("actual-default-reload", new { prior, originalSha256 = EvidenceAdmission.Hash(prior), replacementSha256 = scope.SettingsHash(),
                        initialProfile, freshProfile = scope.Profile, initial.Migrated, reloaded = reloaded.CreateBindings().GetUserBindings().Keys.ToArray(),
                        allOriginalSettingBytesRetained = true, liveOwnerReloadQualified = false });
                    return Task.FromResult<object>(new { id, passed, actualDefaultLoaderInvoked = true });
                }
                scope.CreateSettings("{}");
                var representation = scope.AgentDirectory; string? distro = null, interop = null;
                if (id == "load-default-empty-wsl") { distro = ""; interop = ""; }
                if (id == "load-default-distro-wsl") distro = "authored-wsl";
                if (id == "load-default-interop-wsl") interop = " ";
                if (id == "load-default-file-url") representation = new Uri(scope.AgentDirectory + Path.DirectorySeparatorChar).AbsoluteUri;
                if (id == "load-default-tilde")
                {
                    var relative = Path.GetRelativePath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), scope.AgentDirectory);
                    if (Path.IsPathRooted(relative)) throw new InvalidOperationException("Tilde control requires review root on the home volume; no user-home settings fallback is permitted.");
                    representation = "~/" + relative.Replace(Path.DirectorySeparatorChar, '/');
                }
                scope.Configure(representation, distro, interop); var beforeHash = scope.SettingsHash();
                var loaded = TerminalKeybindingConfigurationLoader.LoadDefault();
                var defaultsPassed = loaded.LoadStatus == "loaded" && !loaded.Migrated && loaded.CreateBindings().GetUserBindings().Count == 0 &&
                    beforeHash == scope.SettingsHash() && ExactDefaultConfiguration(loaded, scope, profiles.RootElement, scope.Profile);
                evidence.Observe("actual-default-environment-profile", new { id, selectedProfile = scope.Profile, loaded.Platform, loaded.AgentDirectory,
                    loaded.ConfigPath, exactSourceDefinitionCount = 90, sha256 = scope.SettingsHash(), defaultHomeSettingsOpened = false });
                return Task.FromResult<object>(new { id, passed = defaultsPassed, actualDefaultLoaderInvoked = true, physicalPlatformQualified = false });
            }).GetAwaiter().GetResult();
            evidence.Complete(result);
        }
    }

    private static bool ExactDefaultConfiguration(TerminalKeybindingConfiguration configuration, DefaultEnvironment scope,
        JsonElement profiles, string profile)
    {
        var registry = configuration.CreateBindings(); var definitions = profiles.GetProperty(profile).EnumerateArray().ToArray();
        return configuration.Platform == DefaultEnvironment.NativePlatform && scope.SamePath(configuration.AgentDirectory, scope.AgentDirectory) &&
            scope.SamePath(configuration.ConfigPath, scope.SettingsPath) && registry.GetResolvedBindings().Keys.SequenceEqual(definitions.Select(row => row.GetProperty("id").GetString())) &&
            definitions.All(row =>
            {
                var definition = registry.GetDefinition(row.GetProperty("id").GetString()!); var expected = row.GetProperty("defaultKeys");
                return definition is not null && definition.Description == row.GetProperty("description").GetString() && Equivalent(Shape(definition.DefaultKeys), expected);
            });
    }

    // One separately admitted consumer process, sequential controls; no machine/user environment writes.
    private static readonly SemaphoreSlim DefaultEnvironmentGate = new(1, 1);
    internal static async Task<object> IsolateDefault(string reviewRoot, ConsumerEvidence evidence, Func<DefaultEnvironment, Task<object>> body)
    {
        await DefaultEnvironmentGate.WaitAsync(); DefaultEnvironment? scope = null; object? result = null; Exception? failure = null;
        try { scope = new(reviewRoot); result = await body(scope); }
        catch (Exception error) { failure = error; }
        finally
        {
            try
            {
                scope?.Dispose();
                if (scope is not null) evidence.Observe("default-process-environment-restored", new { scope.AgentDirectory, scope.Restored,
                    processEnvironmentOnly = true, originalValuesDisclosed = false, originalUserSettingsOpened = false });
            }
            catch (Exception cleanup) { failure = failure is null ? cleanup : new AggregateException("LoadDefault control and environment restoration failed", failure, cleanup); }
            finally { DefaultEnvironmentGate.Release(); }
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return result ?? throw new InvalidOperationException("Missing completed LoadDefault control result.");
    }

    internal sealed class DefaultEnvironment : IDisposable
    {
        private static readonly string[] Names = ["PI_CODING_AGENT_DIR", "WSL_DISTRO_NAME", "WSL_INTEROP"];
        private readonly string?[] original = Names.Select(name => Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process)).ToArray();
        private string? distro, interop; private bool disposed;
        internal static string NativePlatform => OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : "linux";
        internal string AgentDirectory { get; }
        internal string SettingsPath => Path.Combine(AgentDirectory, "keybindings.json");
        internal bool Restored { get; private set; }
        internal string Profile => NativePlatform == "win32" ? "windows" : NativePlatform == "darwin" ? "mac" :
            !string.IsNullOrEmpty(distro) || !string.IsNullOrEmpty(interop) ? "wsl" : "linux";
        internal DefaultEnvironment(string reviewRoot)
        {
            var root = Path.GetFullPath(reviewRoot); var artifacts = Path.Combine(root, "artifacts");
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0 || Directory.Exists(artifacts) && (File.GetAttributes(artifacts) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Isolated review artifacts must not redirect outside the admitted root.");
            Directory.CreateDirectory(artifacts); AgentDirectory = Path.Combine(artifacts, "load-default owned " + Guid.NewGuid().ToString("N"));
            var relative = Path.GetRelativePath(root, AgentDirectory);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Directory.Exists(AgentDirectory) || File.Exists(AgentDirectory))
                throw new IOException("Fresh owned settings root required.");
            Directory.CreateDirectory(AgentDirectory);
            if ((File.GetAttributes(AgentDirectory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Owned settings root redirected.");
            using (var ownership = new FileStream(Path.Combine(AgentDirectory, "ownership.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { ownership.Write(JsonSerializer.SerializeToUtf8Bytes(new { reviewRoot = root, AgentDirectory, settingsPath = SettingsPath,
                purpose = "fresh isolated LoadDefault regression root", processEnvironmentOnly = true })); ownership.Flush(true); }
            try { Configure(AgentDirectory, null, null); }
            catch (Exception setup)
            { try { Dispose(); } catch (Exception cleanup) { throw new AggregateException("Environment setup and restoration failed", setup, cleanup); } throw; }
        }
        internal bool SamePath(string left, string right) => string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        internal void Configure(string representation, string? selectedDistro, string? selectedInterop)
        {
            var resolved = TerminalKeybindingConfigurationLoader.ResolveAgentDirectory(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), NativePlatform,
                new Dictionary<string, string?> { ["PI_CODING_AGENT_DIR"] = representation });
            if (!SamePath(resolved, AgentDirectory)) throw new IOException("LoadDefault must resolve to the exclusively owned settings directory before any read.");
            Environment.SetEnvironmentVariable(Names[0], representation, EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable(Names[1], selectedDistro, EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable(Names[2], selectedInterop, EnvironmentVariableTarget.Process); distro = selectedDistro; interop = selectedInterop;
        }
        internal void CreateSettings(string raw)
        {
            using var file = new FileStream(SettingsPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.Write(new System.Text.UTF8Encoding(false).GetBytes(raw)); file.Flush(true);
        }
        internal string? SettingsHash() => File.Exists(SettingsPath) ? EvidenceAdmission.Hash(SettingsPath) : null;
        public void Dispose()
        {
            if (disposed) return; disposed = true; var errors = new List<Exception>();
            for (var index = 0; index < Names.Length; index++)
                try { Environment.SetEnvironmentVariable(Names[index], original[index], EnvironmentVariableTarget.Process); }
                catch (Exception error) { errors.Add(error); }
            for (var index = 0; index < Names.Length; index++)
                try { if (Environment.GetEnvironmentVariable(Names[index], EnvironmentVariableTarget.Process) != original[index]) errors.Add(new IOException("Original process variable restoration differs: " + Names[index])); }
                catch (Exception error) { errors.Add(error); }
            Restored = errors.Count == 0; if (!Restored) throw new AggregateException("LoadDefault process environment restoration failed", errors);
        }
    }

    // Concrete Source declaration inventory, authored without executing Source; all 90 definitions per profile.
    private const string DefaultAgentDefinitionProfiles = """
        {"windows":[{"id":"tui.editor.cursorUp","defaultKeys":"up","description":"Move cursor up"},{"id":"tui.editor.cursorDown","defaultKeys":"down","description":"Move cursor down"},{"id":"tui.editor.historyPrevious","defaultKeys":[],"description":"Select previous prompt history entry"},{"id":"tui.editor.historyNext","defaultKeys":[],"description":"Select next prompt history entry"},{"id":"tui.editor.cursorLeft","defaultKeys":["left","ctrl+b"],"description":"Move cursor left"},{"id":"tui.editor.cursorRight","defaultKeys":["right","ctrl+f"],"description":"Move cursor right"},{"id":"tui.editor.cursorWordLeft","defaultKeys":["alt+left","ctrl+left","alt+b"],"description":"Move cursor word left"},{"id":"tui.editor.cursorWordRight","defaultKeys":["alt+right","ctrl+right","alt+f"],"description":"Move cursor word right"},{"id":"tui.editor.cursorLineStart","defaultKeys":["home","ctrl+a"],"description":"Move to line start"},{"id":"tui.editor.cursorLineEnd","defaultKeys":["end","ctrl+e"],"description":"Move to line end"},{"id":"tui.editor.jumpForward","defaultKeys":"ctrl+]","description":"Jump forward to character"},{"id":"tui.editor.jumpBackward","defaultKeys":"ctrl+alt+]","description":"Jump backward to character"},{"id":"tui.editor.pageUp","defaultKeys":["pageUp","ctrl+pageUp"],"description":"Page up"},{"id":"tui.editor.pageDown","defaultKeys":["pageDown","ctrl+pageDown"],"description":"Page down"},{"id":"tui.editor.deleteCharBackward","defaultKeys":"backspace","description":"Delete character backward"},{"id":"tui.editor.deleteCharForward","defaultKeys":["delete","ctrl+d"],"description":"Delete character forward"},{"id":"tui.editor.deleteWordBackward","defaultKeys":["ctrl+w","alt+backspace"],"description":"Delete word backward"},{"id":"tui.editor.deleteWordForward","defaultKeys":["alt+d","alt+delete"],"description":"Delete word forward"},{"id":"tui.editor.deleteToLineStart","defaultKeys":"ctrl+u","description":"Delete to line start"},{"id":"tui.editor.deleteToLineEnd","defaultKeys":"ctrl+k","description":"Delete to line end"},{"id":"tui.editor.yank","defaultKeys":"ctrl+y","description":"Yank"},{"id":"tui.editor.yankPop","defaultKeys":"alt+y","description":"Yank pop"},{"id":"tui.editor.undo","defaultKeys":"ctrl+z","description":"Undo"},{"id":"tui.input.newLine","defaultKeys":["shift+enter","ctrl+j"],"description":"Insert newline"},{"id":"tui.input.submit","defaultKeys":"enter","description":"Submit input"},{"id":"tui.input.tab","defaultKeys":"tab","description":"Tab / autocomplete"},{"id":"tui.input.copy","defaultKeys":"ctrl+c","description":"Copy selection"},{"id":"tui.select.up","defaultKeys":"up","description":"Move selection up"},{"id":"tui.select.down","defaultKeys":"down","description":"Move selection down"},{"id":"tui.select.pageUp","defaultKeys":"pageUp","description":"Selection page up"},{"id":"tui.select.pageDown","defaultKeys":"pageDown","description":"Selection page down"},{"id":"tui.select.confirm","defaultKeys":"enter","description":"Confirm selection"},{"id":"tui.select.cancel","defaultKeys":["escape","ctrl+c"],"description":"Cancel selection"},{"id":"tui.altScreen.pageUp","defaultKeys":"pageUp","description":"Scroll viewport up one page"},{"id":"tui.altScreen.pageDown","defaultKeys":"pageDown","description":"Scroll viewport down one page"},{"id":"tui.altScreen.halfPageUp","defaultKeys":[],"description":"Scroll viewport up half a page"},{"id":"tui.altScreen.halfPageDown","defaultKeys":[],"description":"Scroll viewport down half a page"},{"id":"tui.altScreen.lineUp","defaultKeys":[],"description":"Scroll viewport up one line"},{"id":"tui.altScreen.lineDown","defaultKeys":[],"description":"Scroll viewport down one line"},{"id":"tui.altScreen.previousPrompt","defaultKeys":"ctrl+up","description":"Jump to previous semantic prompt"},{"id":"tui.altScreen.nextPrompt","defaultKeys":"ctrl+down","description":"Jump to next semantic prompt"},{"id":"tui.altScreen.search","defaultKeys":"ctrl+f","description":"Search the primary scroll view"},{"id":"tui.altScreen.searchNext","defaultKeys":["enter","ctrl+g"],"description":"Select the next search match"},{"id":"tui.altScreen.searchPrevious","defaultKeys":["shift+enter","ctrl+shift+g"],"description":"Select the previous search match"},{"id":"tui.altScreen.searchClose","defaultKeys":"escape","description":"Close transcript search"},{"id":"tui.altScreen.top","defaultKeys":"ctrl+home","description":"Scroll viewport to top"},{"id":"tui.altScreen.bottom","defaultKeys":"ctrl+end","description":"Scroll viewport to bottom"},{"id":"app.interrupt","defaultKeys":"escape","description":"Cancel or abort"},{"id":"app.clear","defaultKeys":"ctrl+c","description":"Clear editor"},{"id":"app.exit","defaultKeys":"ctrl+d","description":"Exit when editor is empty"},{"id":"app.suspend","defaultKeys":[],"description":"Suspend to background"},{"id":"app.thinking.cycle","defaultKeys":"shift+tab","description":"Cycle thinking level"},{"id":"app.thinking.save","defaultKeys":"ctrl+s","description":"Save thinking level"},{"id":"app.model.cycleForward","defaultKeys":"ctrl+p","description":"Cycle to next model"},{"id":"app.model.cycleBackward","defaultKeys":"alt+p","description":"Cycle to previous model"},{"id":"app.model.select","defaultKeys":"ctrl+l","description":"Open model selector"},{"id":"app.tools.expand","defaultKeys":"ctrl+o","description":"Toggle tool output"},{"id":"app.thinking.toggle","defaultKeys":"ctrl+t","description":"Toggle thinking blocks"},{"id":"app.session.toggleNamedFilter","defaultKeys":"ctrl+n","description":"Toggle named session filter"},{"id":"app.editor.external","defaultKeys":"ctrl+g","description":"Open external editor"},{"id":"app.message.copy","defaultKeys":"ctrl+x","description":"Copy selection or last assistant message"},{"id":"app.message.followUp","defaultKeys":"ctrl+q","description":"Queue follow-up message"},{"id":"app.message.dequeue","defaultKeys":"alt+q","description":"Restore queued messages"},{"id":"app.clipboard.pasteImage","defaultKeys":"alt+v","description":"Paste files on macOS, images, or text from clipboard"},{"id":"app.session.new","defaultKeys":[],"description":"Start a new session"},{"id":"app.session.tree","defaultKeys":[],"description":"Open session tree"},{"id":"app.session.fork","defaultKeys":[],"description":"Fork current session"},{"id":"app.session.resume","defaultKeys":[],"description":"Resume a session"},{"id":"app.tree.foldOrUp","defaultKeys":["ctrl+left","alt+left"],"description":"Fold tree branch or move up"},{"id":"app.tree.unfoldOrDown","defaultKeys":["ctrl+right","alt+right"],"description":"Unfold tree branch or move down"},{"id":"app.tree.editLabel","defaultKeys":"shift+l","description":"Edit tree label"},{"id":"app.tree.toggleLabelTimestamp","defaultKeys":"shift+t","description":"Toggle tree label timestamps"},{"id":"app.session.togglePath","defaultKeys":"ctrl+p","description":"Toggle session path display"},{"id":"app.session.toggleSort","defaultKeys":"ctrl+s","description":"Toggle session sort mode"},{"id":"app.session.rename","defaultKeys":"ctrl+r","description":"Rename session"},{"id":"app.session.delete","defaultKeys":"ctrl+d","description":"Delete session"},{"id":"app.session.deleteNoninvasive","defaultKeys":"ctrl+backspace","description":"Delete session when query is empty"},{"id":"app.models.save","defaultKeys":"ctrl+s","description":"Save model selection"},{"id":"app.models.enableAll","defaultKeys":"ctrl+a","description":"Enable all models"},{"id":"app.models.clearAll","defaultKeys":"ctrl+x","description":"Clear all models"},{"id":"app.models.toggleProvider","defaultKeys":"ctrl+p","description":"Toggle all models for provider"},{"id":"app.models.reorderUp","defaultKeys":"alt+up","description":"Move model up in order"},{"id":"app.models.reorderDown","defaultKeys":"alt+down","description":"Move model down in order"},{"id":"app.tree.filter.default","defaultKeys":"ctrl+d","description":"Tree filter: default view"},{"id":"app.tree.filter.noTools","defaultKeys":"ctrl+t","description":"Tree filter: hide tool results"},{"id":"app.tree.filter.userOnly","defaultKeys":"ctrl+u","description":"Tree filter: user messages only"},{"id":"app.tree.filter.labeledOnly","defaultKeys":"ctrl+l","description":"Tree filter: labeled entries only"},{"id":"app.tree.filter.all","defaultKeys":"ctrl+a","description":"Tree filter: show all entries"},{"id":"app.tree.filter.cycleForward","defaultKeys":"ctrl+o","description":"Tree filter: cycle forward"},{"id":"app.tree.filter.cycleBackward","defaultKeys":"shift+ctrl+o","description":"Tree filter: cycle backward"}],"wsl":[{"id":"tui.editor.cursorUp","defaultKeys":"up","description":"Move cursor up"},{"id":"tui.editor.cursorDown","defaultKeys":"down","description":"Move cursor down"},{"id":"tui.editor.historyPrevious","defaultKeys":[],"description":"Select previous prompt history entry"},{"id":"tui.editor.historyNext","defaultKeys":[],"description":"Select next prompt history entry"},{"id":"tui.editor.cursorLeft","defaultKeys":["left","ctrl+b"],"description":"Move cursor left"},{"id":"tui.editor.cursorRight","defaultKeys":["right","ctrl+f"],"description":"Move cursor right"},{"id":"tui.editor.cursorWordLeft","defaultKeys":["alt+left","ctrl+left","alt+b"],"description":"Move cursor word left"},{"id":"tui.editor.cursorWordRight","defaultKeys":["alt+right","ctrl+right","alt+f"],"description":"Move cursor word right"},{"id":"tui.editor.cursorLineStart","defaultKeys":["home","ctrl+a"],"description":"Move to line start"},{"id":"tui.editor.cursorLineEnd","defaultKeys":["end","ctrl+e"],"description":"Move to line end"},{"id":"tui.editor.jumpForward","defaultKeys":"ctrl+]","description":"Jump forward to character"},{"id":"tui.editor.jumpBackward","defaultKeys":"ctrl+alt+]","description":"Jump backward to character"},{"id":"tui.editor.pageUp","defaultKeys":["pageUp","ctrl+pageUp"],"description":"Page up"},{"id":"tui.editor.pageDown","defaultKeys":["pageDown","ctrl+pageDown"],"description":"Page down"},{"id":"tui.editor.deleteCharBackward","defaultKeys":"backspace","description":"Delete character backward"},{"id":"tui.editor.deleteCharForward","defaultKeys":["delete","ctrl+d"],"description":"Delete character forward"},{"id":"tui.editor.deleteWordBackward","defaultKeys":["ctrl+w","alt+backspace"],"description":"Delete word backward"},{"id":"tui.editor.deleteWordForward","defaultKeys":["alt+d","alt+delete"],"description":"Delete word forward"},{"id":"tui.editor.deleteToLineStart","defaultKeys":"ctrl+u","description":"Delete to line start"},{"id":"tui.editor.deleteToLineEnd","defaultKeys":"ctrl+k","description":"Delete to line end"},{"id":"tui.editor.yank","defaultKeys":"ctrl+y","description":"Yank"},{"id":"tui.editor.yankPop","defaultKeys":"alt+y","description":"Yank pop"},{"id":"tui.editor.undo","defaultKeys":"alt+z","description":"Undo"},{"id":"tui.input.newLine","defaultKeys":["shift+enter","ctrl+j"],"description":"Insert newline"},{"id":"tui.input.submit","defaultKeys":"enter","description":"Submit input"},{"id":"tui.input.tab","defaultKeys":"tab","description":"Tab / autocomplete"},{"id":"tui.input.copy","defaultKeys":"ctrl+c","description":"Copy selection"},{"id":"tui.select.up","defaultKeys":"up","description":"Move selection up"},{"id":"tui.select.down","defaultKeys":"down","description":"Move selection down"},{"id":"tui.select.pageUp","defaultKeys":"pageUp","description":"Selection page up"},{"id":"tui.select.pageDown","defaultKeys":"pageDown","description":"Selection page down"},{"id":"tui.select.confirm","defaultKeys":"enter","description":"Confirm selection"},{"id":"tui.select.cancel","defaultKeys":["escape","ctrl+c"],"description":"Cancel selection"},{"id":"tui.altScreen.pageUp","defaultKeys":"pageUp","description":"Scroll viewport up one page"},{"id":"tui.altScreen.pageDown","defaultKeys":"pageDown","description":"Scroll viewport down one page"},{"id":"tui.altScreen.halfPageUp","defaultKeys":[],"description":"Scroll viewport up half a page"},{"id":"tui.altScreen.halfPageDown","defaultKeys":[],"description":"Scroll viewport down half a page"},{"id":"tui.altScreen.lineUp","defaultKeys":[],"description":"Scroll viewport up one line"},{"id":"tui.altScreen.lineDown","defaultKeys":[],"description":"Scroll viewport down one line"},{"id":"tui.altScreen.previousPrompt","defaultKeys":"ctrl+up","description":"Jump to previous semantic prompt"},{"id":"tui.altScreen.nextPrompt","defaultKeys":"ctrl+down","description":"Jump to next semantic prompt"},{"id":"tui.altScreen.search","defaultKeys":"ctrl+f","description":"Search the primary scroll view"},{"id":"tui.altScreen.searchNext","defaultKeys":["enter","ctrl+g"],"description":"Select the next search match"},{"id":"tui.altScreen.searchPrevious","defaultKeys":["shift+enter","ctrl+shift+g"],"description":"Select the previous search match"},{"id":"tui.altScreen.searchClose","defaultKeys":"escape","description":"Close transcript search"},{"id":"tui.altScreen.top","defaultKeys":"ctrl+home","description":"Scroll viewport to top"},{"id":"tui.altScreen.bottom","defaultKeys":"ctrl+end","description":"Scroll viewport to bottom"},{"id":"app.interrupt","defaultKeys":"escape","description":"Cancel or abort"},{"id":"app.clear","defaultKeys":"ctrl+c","description":"Clear editor"},{"id":"app.exit","defaultKeys":"ctrl+d","description":"Exit when editor is empty"},{"id":"app.suspend","defaultKeys":"ctrl+z","description":"Suspend to background"},{"id":"app.thinking.cycle","defaultKeys":"shift+tab","description":"Cycle thinking level"},{"id":"app.thinking.save","defaultKeys":"ctrl+s","description":"Save thinking level"},{"id":"app.model.cycleForward","defaultKeys":"ctrl+p","description":"Cycle to next model"},{"id":"app.model.cycleBackward","defaultKeys":"alt+p","description":"Cycle to previous model"},{"id":"app.model.select","defaultKeys":"ctrl+l","description":"Open model selector"},{"id":"app.tools.expand","defaultKeys":"ctrl+o","description":"Toggle tool output"},{"id":"app.thinking.toggle","defaultKeys":"ctrl+t","description":"Toggle thinking blocks"},{"id":"app.session.toggleNamedFilter","defaultKeys":"ctrl+n","description":"Toggle named session filter"},{"id":"app.editor.external","defaultKeys":"ctrl+g","description":"Open external editor"},{"id":"app.message.copy","defaultKeys":"ctrl+x","description":"Copy selection or last assistant message"},{"id":"app.message.followUp","defaultKeys":"ctrl+q","description":"Queue follow-up message"},{"id":"app.message.dequeue","defaultKeys":"alt+q","description":"Restore queued messages"},{"id":"app.clipboard.pasteImage","defaultKeys":"alt+v","description":"Paste files on macOS, images, or text from clipboard"},{"id":"app.session.new","defaultKeys":[],"description":"Start a new session"},{"id":"app.session.tree","defaultKeys":[],"description":"Open session tree"},{"id":"app.session.fork","defaultKeys":[],"description":"Fork current session"},{"id":"app.session.resume","defaultKeys":[],"description":"Resume a session"},{"id":"app.tree.foldOrUp","defaultKeys":["ctrl+left","alt+left"],"description":"Fold tree branch or move up"},{"id":"app.tree.unfoldOrDown","defaultKeys":["ctrl+right","alt+right"],"description":"Unfold tree branch or move down"},{"id":"app.tree.editLabel","defaultKeys":"shift+l","description":"Edit tree label"},{"id":"app.tree.toggleLabelTimestamp","defaultKeys":"shift+t","description":"Toggle tree label timestamps"},{"id":"app.session.togglePath","defaultKeys":"ctrl+p","description":"Toggle session path display"},{"id":"app.session.toggleSort","defaultKeys":"ctrl+s","description":"Toggle session sort mode"},{"id":"app.session.rename","defaultKeys":"ctrl+r","description":"Rename session"},{"id":"app.session.delete","defaultKeys":"ctrl+d","description":"Delete session"},{"id":"app.session.deleteNoninvasive","defaultKeys":"ctrl+backspace","description":"Delete session when query is empty"},{"id":"app.models.save","defaultKeys":"ctrl+s","description":"Save model selection"},{"id":"app.models.enableAll","defaultKeys":"ctrl+a","description":"Enable all models"},{"id":"app.models.clearAll","defaultKeys":"ctrl+x","description":"Clear all models"},{"id":"app.models.toggleProvider","defaultKeys":"ctrl+p","description":"Toggle all models for provider"},{"id":"app.models.reorderUp","defaultKeys":"alt+up","description":"Move model up in order"},{"id":"app.models.reorderDown","defaultKeys":"alt+down","description":"Move model down in order"},{"id":"app.tree.filter.default","defaultKeys":"ctrl+d","description":"Tree filter: default view"},{"id":"app.tree.filter.noTools","defaultKeys":"ctrl+t","description":"Tree filter: hide tool results"},{"id":"app.tree.filter.userOnly","defaultKeys":"ctrl+u","description":"Tree filter: user messages only"},{"id":"app.tree.filter.labeledOnly","defaultKeys":"ctrl+l","description":"Tree filter: labeled entries only"},{"id":"app.tree.filter.all","defaultKeys":"ctrl+a","description":"Tree filter: show all entries"},{"id":"app.tree.filter.cycleForward","defaultKeys":"ctrl+o","description":"Tree filter: cycle forward"},{"id":"app.tree.filter.cycleBackward","defaultKeys":"shift+ctrl+o","description":"Tree filter: cycle backward"}],"linux":[{"id":"tui.editor.cursorUp","defaultKeys":"up","description":"Move cursor up"},{"id":"tui.editor.cursorDown","defaultKeys":"down","description":"Move cursor down"},{"id":"tui.editor.historyPrevious","defaultKeys":[],"description":"Select previous prompt history entry"},{"id":"tui.editor.historyNext","defaultKeys":[],"description":"Select next prompt history entry"},{"id":"tui.editor.cursorLeft","defaultKeys":["left","ctrl+b"],"description":"Move cursor left"},{"id":"tui.editor.cursorRight","defaultKeys":["right","ctrl+f"],"description":"Move cursor right"},{"id":"tui.editor.cursorWordLeft","defaultKeys":["alt+left","ctrl+left","alt+b"],"description":"Move cursor word left"},{"id":"tui.editor.cursorWordRight","defaultKeys":["alt+right","ctrl+right","alt+f"],"description":"Move cursor word right"},{"id":"tui.editor.cursorLineStart","defaultKeys":["home","ctrl+a"],"description":"Move to line start"},{"id":"tui.editor.cursorLineEnd","defaultKeys":["end","ctrl+e"],"description":"Move to line end"},{"id":"tui.editor.jumpForward","defaultKeys":"ctrl+]","description":"Jump forward to character"},{"id":"tui.editor.jumpBackward","defaultKeys":"ctrl+alt+]","description":"Jump backward to character"},{"id":"tui.editor.pageUp","defaultKeys":["pageUp","ctrl+pageUp"],"description":"Page up"},{"id":"tui.editor.pageDown","defaultKeys":["pageDown","ctrl+pageDown"],"description":"Page down"},{"id":"tui.editor.deleteCharBackward","defaultKeys":"backspace","description":"Delete character backward"},{"id":"tui.editor.deleteCharForward","defaultKeys":["delete","ctrl+d"],"description":"Delete character forward"},{"id":"tui.editor.deleteWordBackward","defaultKeys":["ctrl+w","alt+backspace"],"description":"Delete word backward"},{"id":"tui.editor.deleteWordForward","defaultKeys":["alt+d","alt+delete"],"description":"Delete word forward"},{"id":"tui.editor.deleteToLineStart","defaultKeys":"ctrl+u","description":"Delete to line start"},{"id":"tui.editor.deleteToLineEnd","defaultKeys":"ctrl+k","description":"Delete to line end"},{"id":"tui.editor.yank","defaultKeys":"ctrl+y","description":"Yank"},{"id":"tui.editor.yankPop","defaultKeys":"alt+y","description":"Yank pop"},{"id":"tui.editor.undo","defaultKeys":"ctrl+-","description":"Undo"},{"id":"tui.input.newLine","defaultKeys":["shift+enter","ctrl+j"],"description":"Insert newline"},{"id":"tui.input.submit","defaultKeys":"enter","description":"Submit input"},{"id":"tui.input.tab","defaultKeys":"tab","description":"Tab / autocomplete"},{"id":"tui.input.copy","defaultKeys":"ctrl+c","description":"Copy selection"},{"id":"tui.select.up","defaultKeys":"up","description":"Move selection up"},{"id":"tui.select.down","defaultKeys":"down","description":"Move selection down"},{"id":"tui.select.pageUp","defaultKeys":"pageUp","description":"Selection page up"},{"id":"tui.select.pageDown","defaultKeys":"pageDown","description":"Selection page down"},{"id":"tui.select.confirm","defaultKeys":"enter","description":"Confirm selection"},{"id":"tui.select.cancel","defaultKeys":["escape","ctrl+c"],"description":"Cancel selection"},{"id":"tui.altScreen.pageUp","defaultKeys":"pageUp","description":"Scroll viewport up one page"},{"id":"tui.altScreen.pageDown","defaultKeys":"pageDown","description":"Scroll viewport down one page"},{"id":"tui.altScreen.halfPageUp","defaultKeys":[],"description":"Scroll viewport up half a page"},{"id":"tui.altScreen.halfPageDown","defaultKeys":[],"description":"Scroll viewport down half a page"},{"id":"tui.altScreen.lineUp","defaultKeys":[],"description":"Scroll viewport up one line"},{"id":"tui.altScreen.lineDown","defaultKeys":[],"description":"Scroll viewport down one line"},{"id":"tui.altScreen.previousPrompt","defaultKeys":["ctrl+shift+up","ctrl+up"],"description":"Jump to previous semantic prompt"},{"id":"tui.altScreen.nextPrompt","defaultKeys":["ctrl+shift+down","ctrl+down"],"description":"Jump to next semantic prompt"},{"id":"tui.altScreen.search","defaultKeys":"ctrl+shift+f","description":"Search the primary scroll view"},{"id":"tui.altScreen.searchNext","defaultKeys":["enter","ctrl+g"],"description":"Select the next search match"},{"id":"tui.altScreen.searchPrevious","defaultKeys":["shift+enter","ctrl+shift+g"],"description":"Select the previous search match"},{"id":"tui.altScreen.searchClose","defaultKeys":"escape","description":"Close transcript search"},{"id":"tui.altScreen.top","defaultKeys":"ctrl+home","description":"Scroll viewport to top"},{"id":"tui.altScreen.bottom","defaultKeys":"ctrl+end","description":"Scroll viewport to bottom"},{"id":"app.interrupt","defaultKeys":"escape","description":"Cancel or abort"},{"id":"app.clear","defaultKeys":"ctrl+c","description":"Clear editor"},{"id":"app.exit","defaultKeys":"ctrl+d","description":"Exit when editor is empty"},{"id":"app.suspend","defaultKeys":"ctrl+z","description":"Suspend to background"},{"id":"app.thinking.cycle","defaultKeys":"shift+tab","description":"Cycle thinking level"},{"id":"app.thinking.save","defaultKeys":"ctrl+s","description":"Save thinking level"},{"id":"app.model.cycleForward","defaultKeys":"ctrl+p","description":"Cycle to next model"},{"id":"app.model.cycleBackward","defaultKeys":"shift+ctrl+p","description":"Cycle to previous model"},{"id":"app.model.select","defaultKeys":"ctrl+l","description":"Open model selector"},{"id":"app.tools.expand","defaultKeys":"ctrl+o","description":"Toggle tool output"},{"id":"app.thinking.toggle","defaultKeys":"ctrl+t","description":"Toggle thinking blocks"},{"id":"app.session.toggleNamedFilter","defaultKeys":"ctrl+n","description":"Toggle named session filter"},{"id":"app.editor.external","defaultKeys":"ctrl+g","description":"Open external editor"},{"id":"app.message.copy","defaultKeys":"ctrl+x","description":"Copy selection or last assistant message"},{"id":"app.message.followUp","defaultKeys":"alt+enter","description":"Queue follow-up message"},{"id":"app.message.dequeue","defaultKeys":"alt+up","description":"Restore queued messages"},{"id":"app.clipboard.pasteImage","defaultKeys":"ctrl+v","description":"Paste files on macOS, images, or text from clipboard"},{"id":"app.session.new","defaultKeys":[],"description":"Start a new session"},{"id":"app.session.tree","defaultKeys":[],"description":"Open session tree"},{"id":"app.session.fork","defaultKeys":[],"description":"Fork current session"},{"id":"app.session.resume","defaultKeys":[],"description":"Resume a session"},{"id":"app.tree.foldOrUp","defaultKeys":["ctrl+left","alt+left"],"description":"Fold tree branch or move up"},{"id":"app.tree.unfoldOrDown","defaultKeys":["ctrl+right","alt+right"],"description":"Unfold tree branch or move down"},{"id":"app.tree.editLabel","defaultKeys":"shift+l","description":"Edit tree label"},{"id":"app.tree.toggleLabelTimestamp","defaultKeys":"shift+t","description":"Toggle tree label timestamps"},{"id":"app.session.togglePath","defaultKeys":"ctrl+p","description":"Toggle session path display"},{"id":"app.session.toggleSort","defaultKeys":"ctrl+s","description":"Toggle session sort mode"},{"id":"app.session.rename","defaultKeys":"ctrl+r","description":"Rename session"},{"id":"app.session.delete","defaultKeys":"ctrl+d","description":"Delete session"},{"id":"app.session.deleteNoninvasive","defaultKeys":"ctrl+backspace","description":"Delete session when query is empty"},{"id":"app.models.save","defaultKeys":"ctrl+s","description":"Save model selection"},{"id":"app.models.enableAll","defaultKeys":"ctrl+a","description":"Enable all models"},{"id":"app.models.clearAll","defaultKeys":"ctrl+x","description":"Clear all models"},{"id":"app.models.toggleProvider","defaultKeys":"ctrl+p","description":"Toggle all models for provider"},{"id":"app.models.reorderUp","defaultKeys":"alt+up","description":"Move model up in order"},{"id":"app.models.reorderDown","defaultKeys":"alt+down","description":"Move model down in order"},{"id":"app.tree.filter.default","defaultKeys":"ctrl+d","description":"Tree filter: default view"},{"id":"app.tree.filter.noTools","defaultKeys":"ctrl+t","description":"Tree filter: hide tool results"},{"id":"app.tree.filter.userOnly","defaultKeys":"ctrl+u","description":"Tree filter: user messages only"},{"id":"app.tree.filter.labeledOnly","defaultKeys":"ctrl+l","description":"Tree filter: labeled entries only"},{"id":"app.tree.filter.all","defaultKeys":"ctrl+a","description":"Tree filter: show all entries"},{"id":"app.tree.filter.cycleForward","defaultKeys":"ctrl+o","description":"Tree filter: cycle forward"},{"id":"app.tree.filter.cycleBackward","defaultKeys":"shift+ctrl+o","description":"Tree filter: cycle backward"}],"mac":[{"id":"tui.editor.cursorUp","defaultKeys":"up","description":"Move cursor up"},{"id":"tui.editor.cursorDown","defaultKeys":"down","description":"Move cursor down"},{"id":"tui.editor.historyPrevious","defaultKeys":[],"description":"Select previous prompt history entry"},{"id":"tui.editor.historyNext","defaultKeys":[],"description":"Select next prompt history entry"},{"id":"tui.editor.cursorLeft","defaultKeys":["left","ctrl+b"],"description":"Move cursor left"},{"id":"tui.editor.cursorRight","defaultKeys":["right","ctrl+f"],"description":"Move cursor right"},{"id":"tui.editor.cursorWordLeft","defaultKeys":["alt+left","ctrl+left","alt+b"],"description":"Move cursor word left"},{"id":"tui.editor.cursorWordRight","defaultKeys":["alt+right","ctrl+right","alt+f"],"description":"Move cursor word right"},{"id":"tui.editor.cursorLineStart","defaultKeys":["home","ctrl+a"],"description":"Move to line start"},{"id":"tui.editor.cursorLineEnd","defaultKeys":["end","ctrl+e"],"description":"Move to line end"},{"id":"tui.editor.jumpForward","defaultKeys":"ctrl+]","description":"Jump forward to character"},{"id":"tui.editor.jumpBackward","defaultKeys":"ctrl+alt+]","description":"Jump backward to character"},{"id":"tui.editor.pageUp","defaultKeys":["pageUp","ctrl+pageUp"],"description":"Page up"},{"id":"tui.editor.pageDown","defaultKeys":["pageDown","ctrl+pageDown"],"description":"Page down"},{"id":"tui.editor.deleteCharBackward","defaultKeys":"backspace","description":"Delete character backward"},{"id":"tui.editor.deleteCharForward","defaultKeys":["delete","ctrl+d"],"description":"Delete character forward"},{"id":"tui.editor.deleteWordBackward","defaultKeys":["ctrl+w","alt+backspace"],"description":"Delete word backward"},{"id":"tui.editor.deleteWordForward","defaultKeys":["alt+d","alt+delete"],"description":"Delete word forward"},{"id":"tui.editor.deleteToLineStart","defaultKeys":"ctrl+u","description":"Delete to line start"},{"id":"tui.editor.deleteToLineEnd","defaultKeys":"ctrl+k","description":"Delete to line end"},{"id":"tui.editor.yank","defaultKeys":"ctrl+y","description":"Yank"},{"id":"tui.editor.yankPop","defaultKeys":"alt+y","description":"Yank pop"},{"id":"tui.editor.undo","defaultKeys":"ctrl+-","description":"Undo"},{"id":"tui.input.newLine","defaultKeys":["shift+enter","ctrl+j"],"description":"Insert newline"},{"id":"tui.input.submit","defaultKeys":"enter","description":"Submit input"},{"id":"tui.input.tab","defaultKeys":"tab","description":"Tab / autocomplete"},{"id":"tui.input.copy","defaultKeys":"ctrl+c","description":"Copy selection"},{"id":"tui.select.up","defaultKeys":"up","description":"Move selection up"},{"id":"tui.select.down","defaultKeys":"down","description":"Move selection down"},{"id":"tui.select.pageUp","defaultKeys":"pageUp","description":"Selection page up"},{"id":"tui.select.pageDown","defaultKeys":"pageDown","description":"Selection page down"},{"id":"tui.select.confirm","defaultKeys":"enter","description":"Confirm selection"},{"id":"tui.select.cancel","defaultKeys":["escape","ctrl+c"],"description":"Cancel selection"},{"id":"tui.altScreen.pageUp","defaultKeys":"pageUp","description":"Scroll viewport up one page"},{"id":"tui.altScreen.pageDown","defaultKeys":"pageDown","description":"Scroll viewport down one page"},{"id":"tui.altScreen.halfPageUp","defaultKeys":[],"description":"Scroll viewport up half a page"},{"id":"tui.altScreen.halfPageDown","defaultKeys":[],"description":"Scroll viewport down half a page"},{"id":"tui.altScreen.lineUp","defaultKeys":[],"description":"Scroll viewport up one line"},{"id":"tui.altScreen.lineDown","defaultKeys":[],"description":"Scroll viewport down one line"},{"id":"tui.altScreen.previousPrompt","defaultKeys":["ctrl+shift+up","ctrl+up"],"description":"Jump to previous semantic prompt"},{"id":"tui.altScreen.nextPrompt","defaultKeys":["ctrl+shift+down","ctrl+down"],"description":"Jump to next semantic prompt"},{"id":"tui.altScreen.search","defaultKeys":"ctrl+shift+f","description":"Search the primary scroll view"},{"id":"tui.altScreen.searchNext","defaultKeys":["enter","ctrl+g"],"description":"Select the next search match"},{"id":"tui.altScreen.searchPrevious","defaultKeys":["shift+enter","ctrl+shift+g"],"description":"Select the previous search match"},{"id":"tui.altScreen.searchClose","defaultKeys":"escape","description":"Close transcript search"},{"id":"tui.altScreen.top","defaultKeys":"ctrl+home","description":"Scroll viewport to top"},{"id":"tui.altScreen.bottom","defaultKeys":"ctrl+end","description":"Scroll viewport to bottom"},{"id":"app.interrupt","defaultKeys":"escape","description":"Cancel or abort"},{"id":"app.clear","defaultKeys":"ctrl+c","description":"Clear editor"},{"id":"app.exit","defaultKeys":"ctrl+d","description":"Exit when editor is empty"},{"id":"app.suspend","defaultKeys":"ctrl+z","description":"Suspend to background"},{"id":"app.thinking.cycle","defaultKeys":"shift+tab","description":"Cycle thinking level"},{"id":"app.thinking.save","defaultKeys":"ctrl+s","description":"Save thinking level"},{"id":"app.model.cycleForward","defaultKeys":"ctrl+p","description":"Cycle to next model"},{"id":"app.model.cycleBackward","defaultKeys":"shift+ctrl+p","description":"Cycle to previous model"},{"id":"app.model.select","defaultKeys":"ctrl+l","description":"Open model selector"},{"id":"app.tools.expand","defaultKeys":"ctrl+o","description":"Toggle tool output"},{"id":"app.thinking.toggle","defaultKeys":"ctrl+t","description":"Toggle thinking blocks"},{"id":"app.session.toggleNamedFilter","defaultKeys":"ctrl+n","description":"Toggle named session filter"},{"id":"app.editor.external","defaultKeys":"ctrl+g","description":"Open external editor"},{"id":"app.message.copy","defaultKeys":"ctrl+x","description":"Copy selection or last assistant message"},{"id":"app.message.followUp","defaultKeys":"alt+enter","description":"Queue follow-up message"},{"id":"app.message.dequeue","defaultKeys":"alt+up","description":"Restore queued messages"},{"id":"app.clipboard.pasteImage","defaultKeys":"ctrl+v","description":"Paste files on macOS, images, or text from clipboard"},{"id":"app.session.new","defaultKeys":[],"description":"Start a new session"},{"id":"app.session.tree","defaultKeys":[],"description":"Open session tree"},{"id":"app.session.fork","defaultKeys":[],"description":"Fork current session"},{"id":"app.session.resume","defaultKeys":[],"description":"Resume a session"},{"id":"app.tree.foldOrUp","defaultKeys":["alt+left","ctrl+left"],"description":"Fold tree branch or move up"},{"id":"app.tree.unfoldOrDown","defaultKeys":["alt+right","ctrl+right"],"description":"Unfold tree branch or move down"},{"id":"app.tree.editLabel","defaultKeys":"shift+l","description":"Edit tree label"},{"id":"app.tree.toggleLabelTimestamp","defaultKeys":"shift+t","description":"Toggle tree label timestamps"},{"id":"app.session.togglePath","defaultKeys":"ctrl+p","description":"Toggle session path display"},{"id":"app.session.toggleSort","defaultKeys":"ctrl+s","description":"Toggle session sort mode"},{"id":"app.session.rename","defaultKeys":"ctrl+r","description":"Rename session"},{"id":"app.session.delete","defaultKeys":"ctrl+d","description":"Delete session"},{"id":"app.session.deleteNoninvasive","defaultKeys":"ctrl+backspace","description":"Delete session when query is empty"},{"id":"app.models.save","defaultKeys":"ctrl+s","description":"Save model selection"},{"id":"app.models.enableAll","defaultKeys":"ctrl+a","description":"Enable all models"},{"id":"app.models.clearAll","defaultKeys":"ctrl+x","description":"Clear all models"},{"id":"app.models.toggleProvider","defaultKeys":"ctrl+p","description":"Toggle all models for provider"},{"id":"app.models.reorderUp","defaultKeys":"alt+up","description":"Move model up in order"},{"id":"app.models.reorderDown","defaultKeys":"alt+down","description":"Move model down in order"},{"id":"app.tree.filter.default","defaultKeys":"ctrl+d","description":"Tree filter: default view"},{"id":"app.tree.filter.noTools","defaultKeys":"ctrl+t","description":"Tree filter: hide tool results"},{"id":"app.tree.filter.userOnly","defaultKeys":"ctrl+u","description":"Tree filter: user messages only"},{"id":"app.tree.filter.labeledOnly","defaultKeys":"ctrl+l","description":"Tree filter: labeled entries only"},{"id":"app.tree.filter.all","defaultKeys":"ctrl+a","description":"Tree filter: show all entries"},{"id":"app.tree.filter.cycleForward","defaultKeys":"ctrl+o","description":"Tree filter: cycle forward"},{"id":"app.tree.filter.cycleBackward","defaultKeys":"shift+ctrl+o","description":"Tree filter: cycle backward"}]}
        """;
}
