using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Cli.Packages;
using PiSharp.Cli.Pi;

// Package manager cases (package-manager.ts, pi-manifest.ts, utils/git.ts, package-manager-cli.ts). Expectations are authored from the
// upstream tests test/package-manager.test.ts, test/package-manager-ssh.test.ts, test/git-ssh-url.test.ts,
// test/package-command-paths.test.ts, test/git-update.test.ts and test/suite/regressions/7187-malformed-package-manifest.test.ts, plus
// the node-semver, minimatch and hosted-git-info behaviour those sources call. Cases that run npm or git say so and skip without them.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> PackageCases() =>
    [
        ("package.helpers.semver", Sync(PackageSemver)),
        ("package.helpers.minimatch", Sync(PackageMinimatch)),
        ("package.helpers.ignore", Sync(PackageIgnore)),
        ("package.source.git-url", Sync(PackageGitUrl)),
        ("package.source.parse-and-identity", Sync(PackageSourceParsing)),
        ("package.resolve.basics", PackageResolveBasics),
        ("package.resolve.skill-metadata", PackageResolveSkillMetadata),
        ("package.resolve.agents-skills", PackageResolveAgentsSkills),
        ("package.resolve.ignore-files", PackageResolveIgnoreFiles),
        ("package.resolve.extension-sources", PackageResolveExtensionSources),
        ("package.resolve.manifest-globs", PackageResolveManifestGlobs),
        ("package.resolve.top-level-patterns", PackageResolveTopLevelPatterns),
        ("package.resolve.package-filters", PackageResolvePackageFilters),
        ("package.resolve.dedupe", PackageResolveDedupe),
        ("package.resolve.multi-file-extensions", PackageResolveMultiFileExtensions),
        ("package.resolve.malformed-manifest-7187", PackageMalformedManifest),
        ("package.settings.normalization", Sync(PackageSettingsNormalization)),
        ("package.settings.persistence", Sync(PackageSettingsPersistence)),
        ("package.commands.npm-argv", PackageNpmArgv),
        ("package.commands.git-argv", PackageGitArgv),
        ("package.commands.install-paths", Sync(PackageInstallPaths)),
        ("package.commands.offline-and-updates", PackageOfflineAndUpdates),
        ("package.cli.parse-and-help", PackageCliParseAndHelp),
        ("package.cli.trust", PackageCliTrust),
        ("package.cli.local-directory", PackageCliLocalDirectory),
        ("package.cli.update-trust", PackageCliUpdateTrust),
        ("package.cli.config", PackageCliConfig),
        ("package.session.resources-and-trust", PackageSessionResources),
        ("package.e2e.npm-registry (needs npm)", PackageNpmEndToEnd),
        ("package.e2e.git-repository (needs git)", PackageGitEndToEnd),
    ];

    // ---- helpers -------------------------------------------------------------------------------------------------------------

    /// <summary>Records the argv of every child process; handlers decide what each call does.</summary>
    private sealed class FakeProcesses : PiPackageProcesses
    {
        public readonly List<(string Command, string[] Args, string? Cwd)> Runs = [];
        public readonly List<(string Command, string[] Args, string? Cwd)> Captures = [];
        public readonly List<(string Command, string[] Args)> Syncs = [];
        public Func<string, string[], string?, Task>? OnRun;
        public Func<string, string[], string?, string?>? OnCapture;
        public Func<string, string[], string>? OnSync;
        internal override async Task RunAsync(string command, IReadOnlyList<string> args, string? cwd, CancellationToken cancellationToken)
        {
            lock (Runs) Runs.Add((command, [.. args], cwd));
            if (OnRun is not null) await OnRun(command, [.. args], cwd);
        }
        internal override Task<string> CaptureAsync(string command, IReadOnlyList<string> args, string? cwd, int? timeoutMs, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken)
        {
            lock (Captures) Captures.Add((command, [.. args], cwd));
            try { return Task.FromResult(OnCapture?.Invoke(command, [.. args], cwd) ?? throw new PiPackageException("Unexpected capture: " + string.Join(' ', args))); }
            catch (PiPackageException error) { return Task.FromException<string>(error); }
        }
        internal override string RunSync(string command, IReadOnlyList<string> args)
        {
            Syncs.Add((command, [.. args]));
            return OnSync?.Invoke(command, [.. args]) ?? throw new PiPackageException("Unexpected sync: " + string.Join(' ', args));
        }
        public bool Ran(string command, string[] args, string? cwd = null) =>
            Runs.Any(run => run.Command == command && run.Args.SequenceEqual(args) && (cwd is null || run.Cwd == cwd));
    }

    private static JsonObject J(string json) => JsonNode.Parse(json)!.AsObject();
    private static string Jstr(object value) => JsonSerializer.Serialize(value);

    private static PiPackageManager Manager(Sandbox sandbox, JsonObject? global = null, JsonObject? project = null, bool trusted = true,
        PiPackageProcesses? processes = null, Dictionary<string, string?>? environment = null, string? cwd = null, IEnumerable<string>? builtins = null) =>
        new(cwd ?? sandbox.Cwd, sandbox.AgentDir, sandbox.Home, PiSettings.FromObjects(global ?? [], project ?? [], trusted),
            name => environment?.GetValueOrDefault(name), processes ?? new FakeProcesses(), builtins);

    private static string Norm(string path) => path.Replace('\\', '/');
    private static bool Enabled(ImmutableArray<PiResolvedResource> resources, string suffix) => resources.Any(r => Norm(r.Path).EndsWith(Norm(suffix), StringComparison.Ordinal) && r.Enabled);
    private static bool Disabled(ImmutableArray<PiResolvedResource> resources, string suffix) => resources.Any(r => Norm(r.Path).EndsWith(Norm(suffix), StringComparison.Ordinal) && !r.Enabled);
    private static bool Has(ImmutableArray<PiResolvedResource> resources, string fragment) => resources.Any(r => Norm(r.Path).Contains(Norm(fragment), StringComparison.Ordinal));

    private static void Skill(Sandbox sandbox, string path, string name, string description = "Test skill") =>
        sandbox.Write(path, $"---\nname: {name}\ndescription: {description}\n---\nContent");

    private static async Task<(int Code, string Out, string Err)> RunPackages(Sandbox sandbox, Func<TextWriter, TextWriter, PiPackageProcesses>? processes, params string[] args)
    {
        using var stdout = new StringWriter { NewLine = "\n" }; using var stderr = new StringWriter { NewLine = "\n" };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(200));
        var host = sandbox.Host(stdout, stderr, null) with { PackageProcesses = processes };
        var code = await PiCommand.RunAsync(args, host, deadline.Token);
        return (code, stdout.ToString(), stderr.ToString());
    }

    /// <summary>A directory link: a symbolic link where allowed, else (Windows without the privilege) a junction.</summary>
    private static bool Link(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (!OperatingSystem.IsWindows()) return false;
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{link}\" \"{target}\"")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
            process.StandardOutput.ReadToEnd(); process.StandardError.ReadToEnd(); process.WaitForExit();
            return process.ExitCode == 0 && Directory.Exists(link);
        }
    }

    // ---- helper semantics ----------------------------------------------------------------------------------------------------

    private static void PackageSemver()
    {
        // node-semver 7: validRange normalization (null for tags and file specs).
        (string Range, string? Valid)[] ranges =
        [
            ("^1.2.3", ">=1.2.3 <2.0.0-0"), ("~1.2.3", ">=1.2.3 <1.3.0-0"), ("1.x", ">=1.0.0 <2.0.0-0"), (">=1.2.0 <2", ">=1.2.0 <2.0.0-0"),
            ("1.2.3 - 2.3", ">=1.2.3 <2.4.0-0"), ("*", "*"), ("latest", null), ("file:../x.tgz", null), ("^0.2.3", ">=0.2.3 <0.3.0-0"),
            ("^0.0.3", ">=0.0.3 <0.0.4-0"), ("~1", ">=1.0.0 <2.0.0-0"), ("<=1.2", "<1.3.0-0"), (">1", ">=2.0.0"), ("1.2.3", "1.2.3"), ("=1.2.3", "1.2.3"),
            ("v1.2.3", "1.2.3"), ("1.2.3 || ^3", "1.2.3||>=3.0.0 <4.0.0-0"), ("^1.2.3-beta.1", ">=1.2.3-beta.1 <2.0.0-0"), ("", "*"), (" >= 1.0.0", ">=1.0.0"),
            ("next", null), ("x", "*"), (">1.2", ">=1.3.0")
        ];
        foreach (var (range, valid) in ranges) Equal(valid, PiSemver.ValidRange(range), "validRange " + range);
        (string Version, string Range, bool Expected)[] satisfies =
        [
            ("1.5.0", "^1.2.3", true), ("2.0.0", "^1.2.3", false), ("1.3.0-beta", "^1.2.3", false), ("1.2.4-beta", "^1.2.3-beta.1", false),
            ("1.2.3-beta.2", "^1.2.3-beta.1", true), ("0.2.9", "^0.2.3", true), ("0.3.0", "^0.2.3", false), ("1.2.9", "~1.2.3", true), ("1.3.0", "~1.2.3", false),
            ("2.3.9", "1.2.3 - 2.3", true), ("2.4.0", "1.2.3 - 2.3", false), ("3.1.0", "1.2.3 || ^3", true), ("1.2.0", "<=1.2", true), ("1.3.0", "<=1.2", false),
            ("2.0.0", ">1", true), ("1.9.9", ">1", false), ("1.3.0", ">1.2", true), ("1.2.5", ">1.2", false)
        ];
        foreach (var (version, range, expected) in satisfies) Equal(expected, PiSemver.Satisfies(version, range), $"satisfies {version} {range}");
        Equal("1.1.0", PiSemver.MaxSatisfying(["1.0.0", "1.1.0", "2.0.0", "1.2.0-rc.1"], "^1.0.0"), "maxSatisfying");
        Equal("1.2.3", PiSemver.Valid("v1.2.3"), "valid v"); Equal(null, PiSemver.Valid("1.2"), "valid partial"); Equal(null, PiSemver.Valid("01.2.3"), "valid leading zero");
        Equal("1.2.3", PiSemver.Valid(" 1.2.3 "), "valid trims");
        Names(["1.0.0", "1.0.0-rc.10", "1.0.0-rc.2", "1.0.0-rc.1", "1.0.0-alpha", "0.9.0"],
            new[] { "1.0.0", "1.0.0-rc.1", "1.0.0-rc.10", "1.0.0-rc.2", "1.0.0-alpha", "0.9.0" }.Order(Comparer<string>.Create(PiSemver.RCompare)), "rcompare");
        Check(PiSemver.Gt("1.0.1", "1.0.0") && PiSemver.Gt("1.0.0", "1.0.0-rc.1") && !PiSemver.Gt("1.0.0", "1.0.0"), "gt");
        Throws<ArgumentException>(() => PiSemver.Gt("latest", "1.0.0"), "gt rejects invalid versions");
    }

    private static void PackageMinimatch()
    {
        // minimatch 10 with default options (dot false), as matchesAnyPattern calls it.
        (string Path, string Pattern, bool Expected)[] cases =
        [
            ("a", "a/**", false), ("a/", "a/**", true), ("a/b", "a/**", true), (".x", "*", false), ("a/.x/b", "a/**/b", false), ("a/x/y/b", "a/**/b", true),
            ("a/b", "a/**/b", true), ("a", "a/", false), ("a/", "a", true), ("a/b", "**", true), (".a/b", "**", false), ("C:/x/y.ts", "C:/x/*.ts", true),
            ("skill-b", "**/*", true), ("a.b", "{a,b}.b", true), ("b.b", "{a,b}.b", true), ("c.b", "{a,b}.b", false), ("f3", "f{1..3}", true),
            ("extensions/x.ts", "extensions/*.ts", true), ("extensions/sub/x.ts", "extensions/*.ts", false), ("a//b", "a/b", true), ("./a", "a", false),
            ("#a", "#a", false), ("ab", "a?", true), ("a", "[a-c]", true), ("d", "[!a-c]", true), (".a", "[.]a", true), (".a", "?a", false), (".a", ".a", true),
            (".a", ".*", true), ("x/.a", "x/.*", true), ("remove.ts", "**/remove.ts", true), ("extensions/remove.ts", "**/remove.ts", true),
            ("bad-skill", "**/bad-skill", true), ("skills/bad-skill", "**/bad-skill", true), ("a{b", "a{b", true), ("a{b}", "a{b}", true),
            ("x.TS", "*.ts", false), ("ab", "!ab", false), ("ac", "!ab", true), ("", "", true)
        ];
        foreach (var (path, pattern, expected) in cases) Equal(expected, PiMinimatch.Match(path, pattern), $"minimatch({Jstr(path)}, {Jstr(pattern)})");
    }

    private static void PackageIgnore()
    {
        // The ignore package as addIgnoreRules feeds it: directories carry a trailing slash, children of ignored directories are ignored.
        var ig = new PiIgnore();
        ig.Add(["venv", "__pycache__", "*.log", "!keep.log", "build/", "sub/x.md", "docs/**", "# comment", ""]);
        (string Path, bool Expected)[] cases =
        [
            ("venv/", true), ("a/venv/", true), ("venv/bad/SKILL.md", true), ("good/SKILL.md", false), ("a.log", true), ("keep.log", false), ("dir/b.log", true),
            ("build/", true), ("build", false), ("x/build/", true), ("sub/x.md", true), ("a/sub/x.md", false), ("docs/a/b.md", true), ("docs/", false), ("# comment", false)
        ];
        foreach (var (path, expected) in cases) Equal(expected, ig.Ignores(path), "ignores " + path);
        var empty = new PiIgnore();
        Check(!empty.Ignores("anything/"), "no rules ignore nothing");
    }

    private static void PackageGitUrl()
    {
        void Is(string source, string host, string path, string? repo = null, string? reference = null)
        {
            var parsed = PiGitUrl.Parse(source) ?? throw new InvalidOperationException("parse " + source);
            Equal(host, parsed.Host, source + " host"); Equal(path, parsed.Path, source + " path"); Equal(reference, parsed.Ref, source + " ref");
            Equal(reference is not null, parsed.Pinned, source + " pinned");
            if (repo is not null) Equal(repo, parsed.Repo, source + " repo");
        }
        // git-ssh-url.test.ts
        Is("https://github.com/user/repo", "github.com", "user/repo", "https://github.com/user/repo");
        Is("ssh://git@github.com/user/repo", "github.com", "user/repo", "ssh://git@github.com/user/repo");
        Is("https://github.com/user/repo@v1.0.0", "github.com", "user/repo", "https://github.com/user/repo", "v1.0.0");
        Is("git:git@github.com:user/repo", "github.com", "user/repo", "git@github.com:user/repo");
        Is("git:github.com/user/repo", "github.com", "user/repo", "https://github.com/user/repo");
        Is("git:git@github.com:user/repo@v1.0.0", "github.com", "user/repo", "git@github.com:user/repo", "v1.0.0");
        foreach (var unsafeSource in new[] { "git:git@evil.example:../../victim/repo", "https://evil.example/..%2F..%2Fvictim/repo", "https://evil.example/..%2F..%2Fvictim/repo%",
            "git:git@evil.example:/absolute/repo", "git:git@evil.example:user\\repo/name", "git:git@evil.example:user/repo\0name" })
            Equal(null, PiGitUrl.Parse(unsafeSource), "unsafe " + Jstr(unsafeSource));
        Equal(null, PiGitUrl.Parse("git@github.com:user/repo"), "scp without git:");
        Equal(null, PiGitUrl.Parse("github.com/user/repo"), "host/path without git:");
        Equal(null, PiGitUrl.Parse("user/repo"), "user/repo");
        // package-manager.test.ts "HTTPS git URL parsing (old behavior)"
        Is("git:https://github.com/user/repo", "github.com", "user/repo");
        Is("https://github.com/user/repo.git", "github.com", "user/repo");
        Is("https://gitlab.com/user/repo", "gitlab.com", "user/repo");
        Is("https://bitbucket.org/user/repo", "bitbucket.org", "user/repo");
        Is("https://codeberg.org/user/repo", "codeberg.org", "user/repo");
        Is("https://github.com/user/repo@main", "github.com", "user/repo", null, "main");
        Is("https://github.com/user/repo@feature/branch", "github.com", "user/repo", null, "feature/branch");
        Is("git:gitlab.com/group/sub/repo", "gitlab.com", "group/sub/repo", "https://gitlab.com/group/sub/repo");
        Is("git:example.test/owner/repo@v2", "example.test", "owner/repo", "https://example.test/owner/repo", "v2");
        Is("git:localhost/owner/repo", "localhost", "owner/repo", "https://localhost/owner/repo");
        Equal(null, PiGitUrl.Parse("git:intranet/owner/repo"), "shorthand host needs a dot or localhost");
    }

    private static void PackageSourceParsing()
    {
        using var sandbox = new Sandbox("pkg-parse");
        var manager = Manager(sandbox);
        PiNpmSource Npm(string source) => PiPackageSource.Parse(source) as PiNpmSource ?? throw new InvalidOperationException("npm " + source);
        Check(Npm("npm:@scope/pkg@1.2.3").Pinned, "exact version pinned");
        Check(!Npm("npm:@scope/pkg@^1.2.3").Pinned, "range unpinned");
        Check(!Npm("npm:pkg").Pinned, "bare unpinned");
        Equal("@scope/pkg", Npm("npm:@scope/pkg@^1.2.3").Name, "scoped name"); Equal("^1.2.3", Npm("npm:@scope/pkg@^1.2.3").Version, "version");
        Equal(">=1.2.3 <2.0.0-0", Npm("npm:@scope/pkg@^1.2.3").Range, "range");
        Equal(null, Npm("npm:pkg@latest").Range, "tags are not ranges");
        Equal("fixture@file:C:/x/y.tgz", Npm("npm: fixture@file:C:/x/y.tgz ").Spec, "spec trimmed");
        Equal("file:C:/x/y.tgz", Npm("npm:fixture@file:C:/x/y.tgz").Version, "file spec version");
        foreach (var git in new[] { "git:github.com/user/repo@v1", "https://github.com/user/repo@v1", "git:git@github.com:user/repo@v1", "ssh://git@github.com/user/repo@v1" })
            Check(PiPackageSource.Parse(git) is PiGitSource, "git " + git);
        foreach (var local in new[] { "/absolute/path/to/package", "./relative/path/to/package", "../relative/path/to/package", "git@github.com:user/repo", "github.com/user/repo" })
            Check(PiPackageSource.Parse(local) is PiLocalSource, "local " + local);
        Equal("./packages/agent-timers", ((PiLocalSource)PiPackageSource.Parse("./packages/agent-timers")).Path, "dot-relative kept");
        Equal("../packages/agent-timers", ((PiLocalSource)PiPackageSource.Parse("../packages/agent-timers")).Path, "dot-dot-relative kept");
        // Identity: version and ref ignored; SSH, HTTPS and git: forms of one repository match.
        foreach (var source in new[] { "https://github.com/user/repo", "https://github.com/user/repo@v1.0.0", "git:github.com/user/repo", "https://github.com/user/repo.git",
            "git:git@github.com:user/repo", "ssh://git@github.com/user/repo" })
            Equal("git:github.com/user/repo", manager.GetPackageIdentity(source), "identity " + source);
        Equal("npm:@scope/pkg", manager.GetPackageIdentity("npm:@scope/pkg@1.0.0"), "npm identity");
        Equal("local:" + Path.Join(sandbox.Cwd, "pkg"), manager.GetPackageIdentity("./pkg"), "local identity from cwd");
        Equal("local:" + Path.Join(sandbox.AgentDir, "pkg"), manager.GetPackageIdentity("./pkg", "user"), "local identity in user scope");
        Equal("local:" + Path.Join(sandbox.Cwd, ".pi", "pkg"), manager.GetPackageIdentity("./pkg", "project"), "local identity in project scope");
        Check(manager.GetPackageIdentity("git:github.com/user/repo") != manager.GetPackageIdentity("git:github.com/user/other"), "different repositories");
    }

    // ---- resolve -------------------------------------------------------------------------------------------------------------

    private static async Task PackageResolveBasics()
    {
        using var sandbox = new Sandbox("pkg-resolve");
        var empty = await Manager(sandbox).ResolveAsync();
        Check(empty.Extensions.IsEmpty && empty.Prompts.IsEmpty && empty.Themes.IsEmpty, "no sources, no resources");
        Check(empty.Skills.All(r => r.Metadata.Source == "auto" && r.Metadata.Origin == "top-level"), "only auto skills");

        var extPath = sandbox.Write(Path.Combine(sandbox.AgentDir, "extensions", "my-extension.ts"), "export default function() {}");
        var result = await Manager(sandbox, J("""{"extensions":["extensions/my-extension.ts"]}""")).ResolveAsync();
        Check(result.Extensions.Any(r => r.Path == extPath && r.Enabled), "settings extension relative to the agent dir");
        Equal(1, result.Extensions.Count(r => r.Path == extPath), "settings entry and auto-discovery deduplicated");
        Equal("local", result.Extensions.First(r => r.Path == extPath).Metadata.Source, "settings entry ranks before auto-discovery");

        // Built-in extensions: user exclusions, project overrides.
        async Task<string[]> Builtins(JsonObject global, JsonObject project) => [.. (await Manager(sandbox, global, project, builtins: ["mcp", "llama.cpp"]).ResolveAsync())
            .Extensions.Where(r => r.Path.StartsWith("builtin:", StringComparison.Ordinal)).Select(r => $"{r.Path},{r.Enabled},{r.Metadata.Source},{r.Metadata.Scope}")];
        Names(["builtin:mcp,True,builtin,user", "builtin:llama.cpp,True,builtin,user"], await Builtins([], []), "builtins default");
        Names(["builtin:mcp,True,builtin,project", "builtin:llama.cpp,False,builtin,project"],
            await Builtins(J("""{"extensions":["-builtin:mcp"]}"""), J("""{"extensions":["+builtin:mcp","-builtin:llama.cpp"]}""")), "builtins project overrides");
        Names(["builtin:mcp,False,builtin,user", "builtin:llama.cpp,True,builtin,user"], await Builtins(J("""{"extensions":["-builtin:mcp"]}"""), J("""{"extensions":[]}""")), "builtins user exclusion");

        var skillFile = Path.Combine(sandbox.AgentDir, "skills", "my-skill", "SKILL.md");
        Skill(sandbox, skillFile, "test-skill");
        Check((await Manager(sandbox, J("""{"skills":["skills"]}""")).ResolveAsync()).Skills.Any(r => r.Path == skillFile && r.Enabled), "settings skill dir");
        var rootSkill = Path.Combine(sandbox.AgentDir, "skills", "single-file.md");
        Skill(sandbox, rootSkill, "single-file");
        Check((await Manager(sandbox).ResolveAsync()).Skills.Any(r => r.Path == rootSkill && r.Enabled), "root markdown skill auto-discovered");

        var projectExt = sandbox.Write(Path.Combine(sandbox.Cwd, ".pi", "extensions", "project-ext.ts"), "export default function() {}");
        Check((await Manager(sandbox, project: J("""{"extensions":["extensions/project-ext.ts"]}""")).ResolveAsync()).Extensions.Any(r => r.Path == projectExt && r.Enabled),
            "project entries relative to .pi");
        var userPrompt = sandbox.Write(Path.Combine(sandbox.AgentDir, "prompts", "auto.md"), "Auto prompt");
        Check((await Manager(sandbox, J("""{"prompts":["!prompts/auto.md"]}""")).ResolveAsync()).Prompts.Any(r => r.Path == userPrompt && !r.Enabled), "user prompt override");
        var projectPrompt = sandbox.Write(Path.Combine(sandbox.Cwd, ".pi", "prompts", "is.md"), "Is prompt");
        Check((await Manager(sandbox, project: J("""{"prompts":["!prompts/is.md"]}""")).ResolveAsync()).Prompts.Any(r => r.Path == projectPrompt && !r.Enabled), "project prompt override");

        var pkgDir = Path.Combine(sandbox.Root, "my-extensions-pkg");
        sandbox.Write(Path.Combine(pkgDir, "package.json"), Jstr(new { name = "my-extensions-pkg", pi = new { extensions = new[] { "./extensions/clip.ts", "./extensions/cost.ts" } } }));
        foreach (var name in new[] { "clip.ts", "cost.ts", "helper.ts" }) sandbox.Write(Path.Combine(pkgDir, "extensions", name), "export default function() {}");
        var manifest = (await Manager(sandbox, new JsonObject { ["extensions"] = new JsonArray(pkgDir) }).ResolveAsync()).Extensions;
        Check(manifest.Any(r => r.Path == Path.Combine(pkgDir, "extensions", "clip.ts") && r.Enabled) && manifest.Any(r => r.Path == Path.Combine(pkgDir, "extensions", "cost.ts") && r.Enabled),
            "pi.extensions of a settings directory");
        Check(!Has(manifest, "helper.ts"), "helpers outside the manifest are not loaded");

        // Untrusted: project entries and project auto-discovery are skipped.
        var untrusted = await Manager(sandbox, project: J("""{"extensions":["extensions/project-ext.ts"]}"""), trusted: false).ResolveAsync();
        Check(!untrusted.Extensions.Any(r => r.Path == projectExt) && !untrusted.Prompts.Any(r => r.Path == projectPrompt), "untrusted project resources skipped");
        Check(untrusted.Prompts.Any(r => r.Path == userPrompt), "user resources stay");
    }

    private static async Task PackageResolveSkillMetadata()
    {
        using var sandbox = new Sandbox("pkg-skillmeta");
        var userSkill = Path.Combine(sandbox.AgentDir, "skills", "user-pi", "SKILL.md"); Skill(sandbox, userSkill, "user-pi");
        var projectBase = Path.Combine(sandbox.Cwd, ".pi");
        var projectSkill = Path.Combine(projectBase, "skills", "project-pi", "SKILL.md"); Skill(sandbox, projectSkill, "project-pi");
        var agentsBase = Path.Combine(sandbox.Home, ".agents");
        var agentsSkill = Path.Combine(agentsBase, "skills", "user-agents", "SKILL.md"); Skill(sandbox, agentsSkill, "user-agents");
        var skills = (await Manager(sandbox).ResolveAsync()).Skills;
        void Meta(string path, string scope, string baseDir)
        {
            var skill = skills.FirstOrDefault(r => r.Path == path) ?? throw new InvalidOperationException("missing " + path);
            Equal("auto", skill.Metadata.Source, path + " source"); Equal(scope, skill.Metadata.Scope, path + " scope"); Equal(baseDir, skill.Metadata.BaseDir, path + " baseDir");
        }
        Meta(userSkill, "user", sandbox.AgentDir); Meta(projectSkill, "project", projectBase); Meta(agentsSkill, "user", agentsBase);

        var repoRoot = Path.Combine(sandbox.Cwd, "repo"); var nested = Path.Combine(repoRoot, "packages", "feature");
        Directory.CreateDirectory(nested); Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));
        var repoAgents = Path.Combine(repoRoot, ".agents"); var repoSkill = Path.Combine(repoAgents, "skills", "repo", "SKILL.md"); Skill(sandbox, repoSkill, "repo");
        var packageAgents = Path.Combine(repoRoot, "packages", ".agents"); var packageSkill = Path.Combine(packageAgents, "skills", "package", "SKILL.md"); Skill(sandbox, packageSkill, "package");
        skills = (await Manager(sandbox, cwd: nested).ResolveAsync()).Skills;
        Meta(repoSkill, "project", repoAgents); Meta(packageSkill, "project", packageAgents);
    }

    private static async Task PackageResolveAgentsSkills()
    {
        using var sandbox = new Sandbox("pkg-agents");
        var repoRoot = Path.Combine(sandbox.Cwd, "repo"); var nested = Path.Combine(repoRoot, "packages", "feature");
        Directory.CreateDirectory(nested); Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));
        var above = Path.Combine(sandbox.Cwd, ".agents", "skills", "above-repo", "SKILL.md"); Skill(sandbox, above, "above-repo");
        var repoSkill = Path.Combine(repoRoot, ".agents", "skills", "repo-root", "SKILL.md"); Skill(sandbox, repoSkill, "repo-root");
        var nestedSkill = Path.Combine(repoRoot, "packages", ".agents", "skills", "nested", "SKILL.md"); Skill(sandbox, nestedSkill, "nested");
        var skills = (await Manager(sandbox, cwd: nested).ResolveAsync()).Skills;
        Check(skills.Any(r => r.Path == repoSkill && r.Enabled) && skills.Any(r => r.Path == nestedSkill && r.Enabled), "cwd up to the git root");
        Check(!skills.Any(r => r.Path == above), "nothing above the git root");

        // Root markdown files of .agents/skills are ignored; nested markdown files are skills.
        var agentsDir = Path.Combine(sandbox.Cwd, "work", ".agents", "skills");
        var rootFile = Path.Combine(agentsDir, "root-file.md"); Skill(sandbox, rootFile, "root-file");
        var nestedSkillMd = Path.Combine(agentsDir, "nested-skill", "SKILL.md"); Skill(sandbox, nestedSkillMd, "nested-skill");
        var child = Path.Combine(agentsDir, "third-party", "child-skill.md"); Skill(sandbox, child, "child-skill");
        var deep = Path.Combine(agentsDir, "third-party", "vendor", "pack", "deep-skill.md"); Skill(sandbox, deep, "deep-skill");
        skills = (await Manager(sandbox, cwd: Path.Combine(sandbox.Cwd, "work")).ResolveAsync()).Skills;
        Check(!skills.Any(r => r.Path == rootFile), "root markdown ignored");
        Check(skills.Any(r => r.Path == nestedSkillMd && r.Enabled) && skills.Any(r => r.Path == child && r.Enabled) && skills.Any(r => r.Path == deep && r.Enabled), "nested markdown skills");

        // ~/.agents/skills stays user-scoped (and appears once) when the cwd is under home.
        var homeSkill = Path.Combine(sandbox.Home, ".agents", "skills", "home-skill", "SKILL.md"); Skill(sandbox, homeSkill, "home-skill");
        var scratch = Path.Combine(sandbox.Home, "scratch", "nested"); Directory.CreateDirectory(scratch);
        var matching = (await Manager(sandbox, cwd: scratch).ResolveAsync()).Skills.Where(r => r.Path == homeSkill).ToList();
        Equal(1, matching.Count, "home skill once");
        Check(matching[0].Enabled && matching[0].Metadata.Scope == "user" && matching[0].Metadata.Source == "auto", "home skill user-scoped");

        // ~/.pi/agent/skills linked to ~/.agents/skills resolves each skill once.
        using var linked = new Sandbox("pkg-agents-link");
        var shared = Path.Combine(linked.Home, ".agents", "skills"); Directory.CreateDirectory(shared);
        Skill(linked, Path.Combine(shared, "foo", "SKILL.md"), "foo");
        if (Link(Path.Combine(linked.AgentDir, "skills"), shared))
            Equal(1, (await Manager(linked).ResolveAsync()).Skills.Count(r => Norm(r.Path).EndsWith("foo/SKILL.md", StringComparison.Ordinal)), "linked skill dirs deduplicated");
    }

    private static async Task PackageResolveIgnoreFiles()
    {
        using var sandbox = new Sandbox("pkg-ignore");
        var skillsDir = Path.Combine(sandbox.AgentDir, "skills");
        sandbox.Write(Path.Combine(skillsDir, ".gitignore"), "venv\n__pycache__\n");
        Skill(sandbox, Path.Combine(skillsDir, "good-skill", "SKILL.md"), "good-skill");
        Skill(sandbox, Path.Combine(skillsDir, "venv", "bad-skill", "SKILL.md"), "bad-skill");
        var skills = (await Manager(sandbox, J("""{"skills":["skills"]}""")).ResolveAsync()).Skills;
        Check(skills.Any(r => r.Path.Contains("good-skill") && r.Enabled), "good skill");
        Check(!skills.Any(r => r.Path.Contains("venv") && r.Enabled), ".gitignore respected");

        sandbox.Write(Path.Combine(sandbox.Cwd, ".gitignore"), ".pi\n");
        var autoSkill = Path.Combine(sandbox.Cwd, ".pi", "skills", "auto-skill", "SKILL.md"); Skill(sandbox, autoSkill, "auto-skill");
        Check((await Manager(sandbox).ResolveAsync()).Skills.Any(r => r.Path == autoSkill && r.Enabled), "parent .gitignore not applied to .pi");

        var prompts = Path.Combine(sandbox.AgentDir, "prompts");
        sandbox.Write(Path.Combine(prompts, ".ignore"), "draft-*.md\n");
        sandbox.Write(Path.Combine(prompts, "draft-one.md"), "x"); sandbox.Write(Path.Combine(prompts, "final.md"), "x");
        var resolvedPrompts = (await Manager(sandbox).ResolveAsync()).Prompts;
        Check(Enabled(resolvedPrompts, "final.md") && !Has(resolvedPrompts, "draft-one.md"), ".ignore in auto prompts");
    }

    private static async Task PackageResolveExtensionSources()
    {
        using var sandbox = new Sandbox("pkg-extsrc");
        var progress = new List<PiPackageProgressEvent>();
        var manager = Manager(sandbox);
        manager.SetProgressCallback(progress.Add);
        var extPath = sandbox.Write(Path.Combine(sandbox.Root, "ext.ts"), "export default function() {}");
        var result = await manager.ResolveExtensionSourcesAsync([extPath]);
        Check(result.Extensions.Any(r => r.Path == extPath && r.Enabled), "local file");
        Equal(0, progress.Count, "local sources emit no progress");
        Equal("package", result.Extensions[0].Metadata.Origin, "origin"); Equal("user", result.Extensions[0].Metadata.Scope, "default scope");
        Equal(Path.GetDirectoryName(extPath), result.Extensions[0].Metadata.BaseDir, "file baseDir");
        Equal("temporary", (await manager.ResolveExtensionSourcesAsync([extPath], temporary: true)).Extensions[0].Metadata.Scope, "temporary scope");
        var builtin = await manager.ResolveExtensionSourcesAsync(["builtin:mcp"]);
        Check(builtin.Extensions.Single() is { Path: "builtin:mcp", Enabled: true, Metadata.Source: "builtin" }, "-e builtin:<name>");

        var pkgDir = Path.Combine(sandbox.Root, "my-package");
        sandbox.Write(Path.Combine(pkgDir, "package.json"), Jstr(new { name = "my-package", pi = new { extensions = new[] { "./src/index.ts" }, skills = new[] { "./skills" } } }));
        sandbox.Write(Path.Combine(pkgDir, "src", "index.ts"), "export default function() {}");
        Skill(sandbox, Path.Combine(pkgDir, "skills", "my-skill", "SKILL.md"), "my-skill");
        result = await manager.ResolveExtensionSourcesAsync([pkgDir]);
        Check(result.Extensions.Any(r => r.Path == Path.Combine(pkgDir, "src", "index.ts") && r.Enabled), "manifest extension");
        Check(result.Skills.Any(r => r.Path == Path.Combine(pkgDir, "skills", "my-skill", "SKILL.md") && r.Enabled), "manifest skill");
        Equal(pkgDir, result.Skills[0].Metadata.PackageRoot, "packageRoot");

        var tilde = Path.Combine(sandbox.Root, "tilde-manifest-package");
        var direct = sandbox.Write(Path.Combine(tilde, "~extensions", "main.ts"), "export default function() {}");
        var slash = sandbox.Write(Path.Combine(tilde, "~", "extensions", "alt.ts"), "export default function() {}");
        var directSkill = Path.Combine(tilde, "~skills", "direct-skill", "SKILL.md"); Skill(sandbox, directSkill, "direct-skill");
        var slashSkill = Path.Combine(tilde, "~", "skills", "slash-skill", "SKILL.md"); Skill(sandbox, slashSkill, "slash-skill");
        sandbox.Write(Path.Combine(tilde, "package.json"), Jstr(new { name = "tilde", pi = new { extensions = new[] { "~extensions/main.ts", "~/extensions/alt.ts" }, skills = new[] { "~skills", "~/skills" } } }));
        result = await manager.ResolveExtensionSourcesAsync([tilde]);
        Check(result.Extensions.Any(r => r.Path == direct) && result.Extensions.Any(r => r.Path == slash), "tilde manifest extensions stay package-relative");
        Check(result.Skills.Any(r => r.Path == directSkill) && result.Skills.Any(r => r.Path == slashSkill), "tilde manifest skills stay package-relative");

        var auto = Path.Combine(sandbox.Root, "auto-pkg");
        sandbox.Write(Path.Combine(auto, "extensions", "main.ts"), "export default function() {}");
        sandbox.Write(Path.Combine(auto, "themes", "dark.json"), "{}");
        result = await manager.ResolveExtensionSourcesAsync([auto]);
        Check(Enabled(result.Extensions, "main.ts") && Enabled(result.Themes, "dark.json"), "auto-discovery layout");

        var skillRoot = Path.Combine(sandbox.Root, "skill-root-pkg");
        var rootSkill = Path.Combine(skillRoot, "skills", "root-skill", "SKILL.md"); Skill(sandbox, rootSkill, "root-skill");
        var nestedSkill = Path.Combine(skillRoot, "skills", "root-skill", "nested-skill", "SKILL.md"); Skill(sandbox, nestedSkill, "nested-skill");
        result = await manager.ResolveExtensionSourcesAsync([skillRoot]);
        Check(result.Skills.Any(r => r.Path == rootSkill) && !result.Skills.Any(r => r.Path == nestedSkill), "SKILL.md stops recursion");

        var bare = Path.Combine(sandbox.Root, "bare-dir"); Directory.CreateDirectory(bare);
        result = await manager.ResolveExtensionSourcesAsync([bare]);
        Check(result.Extensions.Single().Path == bare, "a directory without resources is itself the extension entry");
        Check((await manager.ResolveExtensionSourcesAsync([Path.Combine(sandbox.Root, "missing")])).Extensions.IsEmpty, "missing local source skipped");
    }

    private static async Task PackageResolveManifestGlobs()
    {
        using var sandbox = new Sandbox("pkg-globs");
        var manager = Manager(sandbox);
        var pkg = Path.Combine(sandbox.Root, "manifest-pkg");
        sandbox.Write(Path.Combine(pkg, "extensions", "local.ts"), "x");
        sandbox.Write(Path.Combine(pkg, "node_modules", "dep", "extensions", "remote.ts"), "x");
        sandbox.Write(Path.Combine(pkg, "node_modules", "dep", "extensions", "skip.ts"), "x");
        sandbox.Write(Path.Combine(pkg, "package.json"), Jstr(new { name = "manifest-pkg", pi = new { extensions = new[] { "extensions", "node_modules/dep/extensions", "!**/skip.ts" } } }));
        var result = await manager.ResolveExtensionSourcesAsync([pkg]);
        Check(Enabled(result.Extensions, "local.ts") && Enabled(result.Extensions, "remote.ts") && !Has(result.Extensions, "skip.ts"), "manifest exclusion pattern");

        var skillPkg = Path.Combine(sandbox.Root, "skill-manifest-pkg");
        Skill(sandbox, Path.Combine(skillPkg, "skills", "good-skill", "SKILL.md"), "good-skill");
        Skill(sandbox, Path.Combine(skillPkg, "skills", "bad-skill", "SKILL.md"), "bad-skill");
        sandbox.Write(Path.Combine(skillPkg, "package.json"), Jstr(new { name = "s", pi = new { skills = new[] { "skills", "!**/bad-skill" } } }));
        result = await manager.ResolveExtensionSourcesAsync([skillPkg]);
        Check(result.Skills.Any(r => r.Path.Contains("good-skill") && r.Enabled) && !Has(result.Skills, "bad-skill"), "manifest skill exclusion by directory");

        var globPkg = Path.Combine(sandbox.Root, "skill-manifest-glob-pkg");
        Skill(sandbox, Path.Combine(globPkg, "plugins", "pdf-to-markdown", "skills", "pdf-to-markdown", "SKILL.md"), "pdf-to-markdown");
        Skill(sandbox, Path.Combine(globPkg, "plugins", "nutrient-dws", "skills", "document-processor-api", "SKILL.md"), "document-processor-api");
        sandbox.Write(Path.Combine(globPkg, "package.json"), Jstr(new { name = "g", pi = new { skills = new[] { "./plugins/*/skills" } } }));
        result = await manager.ResolveExtensionSourcesAsync([globPkg]);
        Check(result.Skills.Any(r => r.Path.Contains("pdf-to-markdown") && r.Enabled) && result.Skills.Any(r => r.Path.Contains("document-processor-api") && r.Enabled), "positive glob entries");

        var semantics = Path.Combine(sandbox.Root, "manifest-glob-semantics-pkg");
        sandbox.Write(Path.Combine(semantics, "extension-files", "z.ts"), "x"); sandbox.Write(Path.Combine(semantics, "extension-files", "a.ts"), "x");
        sandbox.Write(Path.Combine(semantics, "extension-files", ".ignored.ts"), "x"); sandbox.Write(Path.Combine(semantics, "extension-files", "nested", ".hidden.ts"), "x");
        sandbox.Write(Path.Combine(semantics, "extension-groups", "group", "index.ts"), "x");
        Skill(sandbox, Path.Combine(semantics, "plugins", "local", "skills", "local-skill", "SKILL.md"), "local-skill");
        var linkedSource = Path.Combine(semantics, "linked-plugin-source");
        Skill(sandbox, Path.Combine(linkedSource, "skills", "linked-skill", "SKILL.md"), "linked-skill");
        var linked = Link(Path.Combine(semantics, "plugins", "linked"), linkedSource);
        sandbox.Write(Path.Combine(semantics, "package.json"), Jstr(new
        {
            name = "manifest-glob-semantics-pkg",
            pi = new
            {
                extensions = new[] { "./extension-files/*.ts", "./extension-files/**/.ignored.ts", "./extension-files/nested/.hidden.ts", "./extension-groups/*/" },
                skills = new[] { "./plugins/*/skills", "./plugins/linked/skills" }
            }
        }));
        result = await manager.ResolveExtensionSourcesAsync([semantics]);
        Names([Path.Join("extension-files", "a.ts"), Path.Join("extension-files", "z.ts"), Path.Join("extension-files", "nested", ".hidden.ts"), Path.Join("extension-groups", "group", "index.ts")],
            result.Extensions.Select(r => Path.GetRelativePath(semantics, r.Path)), "sorted glob matches; exact entries reach dot paths");
        Check(result.Skills.Any(r => Norm(r.Path).EndsWith("local-skill/SKILL.md", StringComparison.Ordinal)), "local plugin skill");
        if (linked) Check(result.Skills.Any(r => Norm(r.Path).EndsWith("linked-skill/SKILL.md", StringComparison.Ordinal)), "exact entry through a link");
    }

    private static async Task PackageResolveTopLevelPatterns()
    {
        using var sandbox = new Sandbox("pkg-toplevel");
        var ext = Path.Combine(sandbox.AgentDir, "extensions");
        foreach (var name in new[] { "keep.ts", "remove.ts" }) sandbox.Write(Path.Combine(ext, name), "x");
        var result = await Manager(sandbox, J("""{"extensions":["extensions","!**/remove.ts"]}""")).ResolveAsync();
        Check(Enabled(result.Extensions, "keep.ts") && Disabled(result.Extensions, "remove.ts"), "! exclusion");

        var themes = Path.Combine(sandbox.AgentDir, "themes");
        foreach (var name in new[] { "dark.json", "light.json", "funky.json" }) sandbox.Write(Path.Combine(themes, name), "{}");
        result = await Manager(sandbox, J("""{"themes":["themes","!funky.json"]}""")).ResolveAsync();
        Check(Enabled(result.Themes, "dark.json") && Enabled(result.Themes, "light.json") && Disabled(result.Themes, "funky.json"), "theme name exclusion");

        var prompts = Path.Combine(sandbox.AgentDir, "prompts");
        foreach (var name in new[] { "review.md", "explain.md", "debug.md" }) sandbox.Write(Path.Combine(prompts, name), "x");
        result = await Manager(sandbox, J("""{"prompts":["prompts","!explain.md"]}""")).ResolveAsync();
        Check(Enabled(result.Prompts, "review.md") && Disabled(result.Prompts, "explain.md"), "prompt exclusion");

        var skills = Path.Combine(sandbox.AgentDir, "skills");
        Skill(sandbox, Path.Combine(skills, "good-skill", "SKILL.md"), "good-skill"); Skill(sandbox, Path.Combine(skills, "bad-skill", "SKILL.md"), "bad-skill");
        result = await Manager(sandbox, J("""{"skills":["skills","!**/bad-skill"]}""")).ResolveAsync();
        Check(result.Skills.Any(r => r.Path.Contains("good-skill") && r.Enabled) && result.Skills.Any(r => r.Path.Contains("bad-skill") && !r.Enabled), "skill exclusion");

        sandbox.Write(Path.Combine(ext, "excluded.ts"), "x"); sandbox.Write(Path.Combine(ext, "force-back.ts"), "x");
        result = await Manager(sandbox, J("""{"extensions":["extensions","!extensions/*.ts","+extensions/force-back.ts"]}""")).ResolveAsync();
        Check(Disabled(result.Extensions, "keep.ts") && Disabled(result.Extensions, "excluded.ts") && Enabled(result.Extensions, "force-back.ts"), "+ force-include");
        result = await Manager(sandbox, J("""{"extensions":["extensions","!extensions/remove.ts","+extensions/remove.ts"]}""")).ResolveAsync();
        Check(Enabled(result.Extensions, "keep.ts") && Enabled(result.Extensions, "remove.ts"), "force-include after specific exclusion");
        result = await Manager(sandbox, J("""{"themes":["themes","!themes/*.json","+themes/funky.json"]}""")).ResolveAsync();
        Check(Disabled(result.Themes, "dark.json") && Enabled(result.Themes, "funky.json"), "force-include theme");
        result = await Manager(sandbox, J("""{"prompts":["prompts","!prompts/*.md","+prompts/debug.md"]}""")).ResolveAsync();
        Check(Disabled(result.Prompts, "review.md") && Enabled(result.Prompts, "debug.md"), "force-include prompt");
        result = await Manager(sandbox, J("""{"extensions":["extensions","+extensions/keep.ts","-extensions/keep.ts"]}""")).ResolveAsync();
        Check(Disabled(result.Extensions, "keep.ts") && Enabled(result.Extensions, "remove.ts"), "- force-exclude wins");
        result = await Manager(sandbox, J("""{"extensions":["extensions/keep.ts"]}""")).ResolveAsync();
        Check(Enabled(result.Extensions, "keep.ts"), "plain entries");
        Equal("local", result.Extensions.First(r => r.Path.EndsWith("keep.ts", StringComparison.Ordinal)).Metadata.Source, "settings entry wins over auto");
    }

    private static async Task PackageResolvePackageFilters()
    {
        using var sandbox = new Sandbox("pkg-filters");
        JsonObject Packages(object entry) => new() { ["packages"] = JsonNode.Parse(Jstr(new[] { entry })) };
        string Pkg(string name, params string[] files) { var dir = Path.Combine(sandbox.Root, name); foreach (var file in files) sandbox.Write(Path.Combine(dir, file), file.EndsWith(".json", StringComparison.Ordinal) ? "{}" : "x"); return dir; }

        var layered = Pkg("layered-pkg", "extensions/foo.ts", "extensions/bar.ts", "extensions/baz.ts");
        sandbox.Write(Path.Combine(layered, "package.json"), Jstr(new { name = "layered-pkg", pi = new { extensions = new[] { "extensions", "!**/baz.ts" } } }));
        var result = await Manager(sandbox, Packages(new { source = layered, extensions = new[] { "!**/bar.ts" }, skills = Array.Empty<string>(), prompts = Array.Empty<string>(), themes = Array.Empty<string>() })).ResolveAsync();
        Check(Enabled(result.Extensions, "foo.ts") && Disabled(result.Extensions, "bar.ts") && !Has(result.Extensions, "baz.ts"), "user filters layer over manifest filters");
        Check(result.Extensions.All(r => r.Metadata.Origin == "package" && r.Metadata.Source == layered && r.Metadata.PackageRoot == layered), "package metadata");

        var pattern = Pkg("pattern-pkg", "extensions/foo.ts", "extensions/bar.ts", "extensions/baz.ts");
        result = await Manager(sandbox, Packages(new { source = pattern, extensions = new[] { "!**/baz.ts" } })).ResolveAsync();
        Check(Enabled(result.Extensions, "foo.ts") && Enabled(result.Extensions, "bar.ts") && Disabled(result.Extensions, "baz.ts"), "! in package filter");

        var theme = Pkg("theme-pkg", "themes/nice.json", "themes/ugly.json");
        result = await Manager(sandbox, Packages(new { source = theme, extensions = Array.Empty<string>(), themes = new[] { "!ugly.json" } })).ResolveAsync();
        Check(Enabled(result.Themes, "nice.json") && Disabled(result.Themes, "ugly.json"), "theme filter");

        var combo = Pkg("combo-pkg", "extensions/alpha.ts", "extensions/beta.ts", "extensions/gamma.ts");
        result = await Manager(sandbox, Packages(new { source = combo, extensions = new[] { "**/alpha.ts", "**/beta.ts", "!**/beta.ts" } })).ResolveAsync();
        Check(Enabled(result.Extensions, "alpha.ts") && Disabled(result.Extensions, "beta.ts") && Disabled(result.Extensions, "gamma.ts"), "include and exclude");

        var directPkg = Pkg("direct-pkg", "extensions/one.ts", "extensions/two.ts", "skills/s/SKILL.md");
        result = await Manager(sandbox, Packages(new { source = directPkg, extensions = new[] { "extensions/one.ts" }, skills = Array.Empty<string>() })).ResolveAsync();
        Check(Enabled(result.Extensions, "one.ts") && Disabled(result.Extensions, "two.ts"), "direct paths");
        Check(result.Skills.Any(r => r.Path.Contains("direct-pkg") && !r.Enabled), "empty array disables the type");

        var force = Pkg("force-pkg", "extensions/alpha.ts", "extensions/beta.ts", "extensions/gamma.ts");
        result = await Manager(sandbox, Packages(new { source = force, extensions = new[] { "!**/*.ts", "+extensions/beta.ts" } })).ResolveAsync();
        Check(Disabled(result.Extensions, "alpha.ts") && Enabled(result.Extensions, "beta.ts") && Disabled(result.Extensions, "gamma.ts"), "force-include in package filter");
        var multi = Pkg("multi-force-pkg", "skills/skill-a/SKILL.md", "skills/skill-b/SKILL.md", "skills/skill-c/SKILL.md");
        result = await Manager(sandbox, Packages(new { source = multi, skills = new[] { "!**/*", "+skills/skill-a", "+skills/skill-c" } })).ResolveAsync();
        Check(result.Skills.Any(r => r.Path.Contains("skill-a") && r.Enabled) && result.Skills.Any(r => r.Path.Contains("skill-b") && !r.Enabled) &&
            result.Skills.Any(r => r.Path.Contains("skill-c") && r.Enabled), "force-include skills by directory");
        var manifestForce = Pkg("manifest-force-pkg", "extensions/one.ts", "extensions/two.ts", "extensions/three.ts");
        sandbox.Write(Path.Combine(manifestForce, "package.json"), Jstr(new { name = "m", pi = new { extensions = new[] { "extensions", "!**/two.ts", "+extensions/two.ts" } } }));
        result = await Manager(sandbox).ResolveExtensionSourcesAsync([manifestForce]);
        Check(Enabled(result.Extensions, "one.ts") && Enabled(result.Extensions, "two.ts") && Enabled(result.Extensions, "three.ts"), "force-include in manifest");
        var forceExclude = Pkg("force-exclude-pkg", "extensions/alpha.ts", "extensions/beta.ts");
        result = await Manager(sandbox, Packages(new { source = forceExclude, extensions = new[] { "extensions/*.ts", "+extensions/alpha.ts", "-extensions/alpha.ts" } })).ResolveAsync();
        Check(Disabled(result.Extensions, "alpha.ts") && Enabled(result.Extensions, "beta.ts"), "force-exclude in package filter");

        // autoload: false in the project is a delta over the user package (no install attempted: the managed npm copy exists).
        var npmPkg = Path.Combine(sandbox.AgentDir, "npm", "node_modules", "pi-tools");
        sandbox.Write(Path.Combine(npmPkg, "package.json"), Jstr(new { name = "pi-tools", version = "1.0.0" }));
        sandbox.Write(Path.Combine(npmPkg, "extensions", "foo.ts"), "x"); sandbox.Write(Path.Combine(npmPkg, "extensions", "bar.ts"), "x");
        var processes = new FakeProcesses { OnRun = (_, _, _) => throw new PiPackageException("unexpected install") };
        result = await Manager(sandbox, J("""{"packages":["npm:pi-tools"]}"""), J("""{"packages":[{"source":"npm:pi-tools","autoload":false,"extensions":["-extensions/foo.ts"]}]}"""),
            processes: processes).ResolveAsync();
        Equal(0, processes.Runs.Count, "no install");
        var foo = result.Extensions.Single(r => r.Path == Path.Combine(npmPkg, "extensions", "foo.ts"));
        var bar = result.Extensions.Single(r => r.Path == Path.Combine(npmPkg, "extensions", "bar.ts"));
        Check(!foo.Enabled && foo.Metadata.Scope == "project", "delta disables foo in the project"); Check(bar.Enabled && bar.Metadata.Scope == "user", "bar from the user package");

        var positive = Pkg("positive-only-pkg", "extensions/foo.ts", "extensions/bar.ts", "skills/foo/SKILL.md");
        result = await Manager(sandbox, project: new JsonObject
        {
            ["packages"] = new JsonArray(new JsonObject { ["source"] = Path.GetRelativePath(Path.Combine(sandbox.Cwd, ".pi"), positive), ["autoload"] = false, ["extensions"] = new JsonArray("+extensions/foo.ts") })
        }).ResolveAsync();
        Names([Path.Combine(positive, "extensions", "foo.ts")], result.Extensions.Select(r => r.Path), "autoload false without a user package is positive-only");
        Check(!result.Skills.Any(r => r.Path.Contains("positive-only-pkg")), "no skills from the positive-only package");
    }

    private static async Task PackageResolveDedupe()
    {
        using var sandbox = new Sandbox("pkg-dedupe");
        var shared = Path.Combine(sandbox.Root, "shared-pkg"); sandbox.Write(Path.Combine(shared, "extensions", "shared.ts"), "x");
        var both = new JsonObject { ["packages"] = new JsonArray(shared) };
        var result = await Manager(sandbox, both, (JsonObject)both.DeepClone()).ResolveAsync();
        var sharedPaths = result.Extensions.Where(r => r.Path.Contains("shared-pkg")).ToList();
        Equal(1, sharedPaths.Count, "same local package once"); Equal("project", sharedPaths[0].Metadata.Scope, "project wins");
        var pkg1 = Path.Combine(sandbox.Root, "pkg1"); sandbox.Write(Path.Combine(pkg1, "extensions", "from-pkg1.ts"), "x");
        var pkg2 = Path.Combine(sandbox.Root, "pkg2"); sandbox.Write(Path.Combine(pkg2, "extensions", "from-pkg2.ts"), "x");
        result = await Manager(sandbox, new JsonObject { ["packages"] = new JsonArray(pkg1) }, new JsonObject { ["packages"] = new JsonArray(pkg2) }).ResolveAsync();
        Check(Has(result.Extensions, "pkg1") && Has(result.Extensions, "pkg2"), "different packages kept");
        var manager = Manager(sandbox);
        Equal(manager.GetPackageIdentity("https://github.com/user/repo"), manager.GetPackageIdentity("git:git@github.com:user/repo"), "SSH and HTTPS");
        Equal(manager.GetPackageIdentity("https://github.com/user/repo@v1"), manager.GetPackageIdentity("git:git@github.com:user/repo@v2"), "refs ignored");
        Equal(manager.GetPackageIdentity("ssh://git@github.com/user/repo"), manager.GetPackageIdentity("git:git@github.com:user/repo"), "ssh:// and scp");
        Check(manager.GetPackageIdentity("https://github.com/user/repo1") != manager.GetPackageIdentity("git:git@github.com:user/repo2"), "different repositories");

        // Precedence: project settings entry, project auto, user settings entry, user auto, package.
        var ranks = new[] { ("local", "project", "top-level"), ("auto", "project", "top-level"), ("local", "user", "top-level"), ("auto", "user", "top-level"),
            ("npm:x", "user", "package"), ("builtin", "user", "top-level") }
            .Select(item => PiPackageManager.ResourcePrecedenceRank(new() { Source = item.Item1, Scope = item.Item2, Origin = item.Item3 }));
        Names(["0", "1", "2", "3", "4", "5"], ranks.Select(rank => rank.ToString(System.Globalization.CultureInfo.InvariantCulture)), "resourcePrecedenceRank");
    }

    private static async Task PackageResolveMultiFileExtensions()
    {
        using var sandbox = new Sandbox("pkg-multifile");
        var ext = Path.Combine(sandbox.AgentDir, "extensions");
        sandbox.Write(Path.Combine(ext, "subagent", "index.ts"), "x"); sandbox.Write(Path.Combine(ext, "subagent", "agents.ts"), "x");
        sandbox.Write(Path.Combine(ext, "manifested", "package.json"), Jstr(new { name = "manifested", pi = new { extensions = new[] { "./src/main.ts" } } }));
        sandbox.Write(Path.Combine(ext, "manifested", "src", "main.ts"), "x"); sandbox.Write(Path.Combine(ext, "manifested", "src", "helper.ts"), "x");
        sandbox.Write(Path.Combine(ext, "standalone.ts"), "x"); sandbox.Write(Path.Combine(ext, "with-js", "index.js"), "x");
        sandbox.Write(Path.Combine(ext, "no-entry", "util.ts"), "x");
        var paths = (await Manager(sandbox).ResolveAsync()).Extensions.Select(r => Norm(Path.GetRelativePath(ext, r.Path))).Order(StringComparer.Ordinal);
        Names(["manifested/src/main.ts", "standalone.ts", "subagent/index.ts", "with-js/index.js"], paths, "one level of extension directories");
        // A directory that is itself a package (index.ts at the root) yields only that entry.
        sandbox.Write(Path.Combine(ext, "index.ts"), "x");
        Names([Path.Combine(ext, "index.ts")], (await Manager(sandbox).ResolveAsync()).Extensions.Select(r => r.Path), "root index wins");
    }

    private static async Task PackageMalformedManifest()
    {
        using var sandbox = new Sandbox("pkg-7187");
        var packageDir = Path.Combine(sandbox.AgentDir, "npm", "node_modules", "bad-package");
        var skillPath = Path.Combine(packageDir, "skills", "bad", "SKILL.md"); Skill(sandbox, skillPath, "bad", "Must not load");
        var promptPath = sandbox.Write(Path.Combine(packageDir, "prompts", "valid.md"), "Valid prompt\n");
        sandbox.Write(Path.Combine(packageDir, "package.json"), Jstr(new { name = "bad-package", version = "1.0.0", pi = new { skills = "./skills", prompts = new[] { "./prompts" } } }));
        var resources = await Manager(sandbox, J("""{"packages":["npm:bad-package"]}""")).ResolveAsync();
        Check(!resources.Skills.Any(r => r.Path == skillPath), "invalid field ignored");
        Check(resources.Prompts.Any(r => r.Path == promptPath), "valid field kept");
        Check(PiManifest.Read(Path.Combine(sandbox.Root, "missing.json")) is null, "missing manifest");
        sandbox.Write("not-json.json", "{"); Check(PiManifest.Read(Path.Combine(sandbox.Root, "not-json.json")) is null, "unparsable manifest");
        sandbox.Write("no-pi.json", """{"name":"x","pi":[1]}"""); Check(PiManifest.Read(Path.Combine(sandbox.Root, "no-pi.json")) is null, "pi must be an object");
        sandbox.Write("mixed.json", """{"pi":{"extensions":["a",1],"themes":[]}}""");
        var mixed = PiManifest.Read(Path.Combine(sandbox.Root, "mixed.json"))!;
        Check(mixed.Extensions is null && mixed.Themes is { Length: 0 }, "a field must be all strings; an empty array is valid");
    }

    // ---- settings ------------------------------------------------------------------------------------------------------------

    private static void PackageSettingsNormalization()
    {
        using var sandbox = new Sandbox("pkg-settings");
        var settings = PiSettings.FromObjects([], [], true);
        var manager = new PiPackageManager(sandbox.Cwd, sandbox.AgentDir, sandbox.Home, settings, _ => null, new FakeProcesses());
        string? First(string scope) => PiPackageEntry.List(scope == "project" ? settings.Project : settings.Global).FirstOrDefault()?.Source;
        var pkgDir = Path.Combine(sandbox.Cwd, "packages", "local-global-pkg"); sandbox.Write(Path.Combine(pkgDir, "extensions", "index.ts"), "x");
        Check(manager.AddSourceToSettings("./packages/local-global-pkg"), "added");
        Equal(Path.GetRelativePath(sandbox.AgentDir, pkgDir), First("user"), "global local packages relative to the agent dir");
        var projectPkg = Path.Combine(sandbox.Cwd, "project-local-pkg"); sandbox.Write(Path.Combine(projectPkg, "extensions", "index.ts"), "x");
        Check(manager.AddSourceToSettings("./project-local-pkg", local: true), "added to project");
        Equal(Path.GetRelativePath(Path.Combine(sandbox.Cwd, ".pi"), projectPkg), First("project"), "project local packages relative to .pi");
        Check(manager.RemoveSourceFromSettings(pkgDir + "/"), "removed with an equivalent path form");
        Equal(0, PiPackageEntry.List(settings.Global).Count, "global packages empty");
        Check(manager.AddSourceToSettings("git:github.com/user/repo@v1"), "first add");
        Check(!manager.AddSourceToSettings("git:github.com/user/repo@v1"), "same source and ref");
        Check(manager.AddSourceToSettings("git:github.com/user/repo@v2"), "ref replaced");
        Equal("""["git:github.com/user/repo@v2"]""", settings.Global["packages"]!.ToJsonString(), "one entry with the new ref");
        settings.SetField("global", "packages", JsonNode.Parse("""[{"source":"git:github.com/user/repo@v1","extensions":["extensions/main.ts"],"skills":[],"prompts":["prompts/review.md"],"themes":["themes/dark.json"]}]"""));
        Check(manager.AddSourceToSettings("git:github.com/user/repo@v2"), "filtered entry replaced");
        Equal("""[{"source":"git:github.com/user/repo@v2","extensions":["extensions/main.ts"],"skills":[],"prompts":["prompts/review.md"],"themes":["themes/dark.json"]}]""",
            settings.Global["packages"]!.ToJsonString(), "filters preserved");
        Check(manager.AddSourceToSettings("."), "cwd as a package");
        Equal(Path.GetRelativePath(sandbox.AgentDir, sandbox.Cwd), PiPackageEntry.List(settings.Global)[1].Source, "relative cwd");
        Check(!manager.RemoveSourceFromSettings("npm:absent"), "nothing to remove");
        var untrusted = new PiPackageManager(sandbox.Cwd, sandbox.AgentDir, sandbox.Home, PiSettings.FromObjects([], [], false), _ => null, new FakeProcesses());
        Equal("Project is not trusted; refusing to access project package storage", Throws<PiPackageException>(() => untrusted.AddSourceToSettings("./x", local: true), "untrusted").Message, "untrusted project scope");
    }

    private static void PackageSettingsPersistence()
    {
        using var sandbox = new Sandbox("pkg-persist");
        var globalPath = sandbox.Write(Path.Combine(sandbox.AgentDir, "settings.json"), "{\n  \"theme\": \"dark\",\n  \"queueMode\": \"all\",\n  \"packages\": []\n}\n");
        var settings = PiSettings.Load(sandbox.Cwd, sandbox.AgentDir, projectTrusted: true);
        var manager = new PiPackageManager(sandbox.Cwd, sandbox.AgentDir, sandbox.Home, settings, _ => null, new FakeProcesses());
        manager.AddSourceToSettings("npm:pi-tools");
        Equal("{\n  \"theme\": \"dark\",\n  \"packages\": [\n    \"npm:pi-tools\"\n  ],\n  \"steeringMode\": \"all\"\n}", File.ReadAllText(globalPath),
            "the file keeps other keys, is migrated and written as JSON.stringify(…, null, 2)");
        Check(!Directory.Exists(globalPath + ".lock"), "lock released");
        manager.AddSourceToSettings("npm:other", local: true);
        Equal("{\n  \"packages\": [\n    \"npm:other\"\n  ]\n}", File.ReadAllText(Path.Combine(sandbox.Cwd, ".pi", "settings.json")), "fresh project settings");
        manager.RemoveSourceFromSettings("npm:pi-tools");
        Equal("[]", JsonNode.Parse(File.ReadAllText(globalPath))!["packages"]!.ToJsonString(), "removed");
        // A settings file that failed to load is never overwritten.
        var broken = sandbox.Write(Path.Combine(sandbox.AgentDir, "settings.json"), "{ not json");
        var brokenSettings = PiSettings.Load(sandbox.Cwd, sandbox.AgentDir, projectTrusted: true);
        new PiPackageManager(sandbox.Cwd, sandbox.AgentDir, sandbox.Home, brokenSettings, _ => null, new FakeProcesses()).AddSourceToSettings("npm:pi-tools");
        Equal("{ not json", File.ReadAllText(broken), "unloadable file kept");
    }

    // ---- commands with recorded processes -------------------------------------------------------------------------------------

    private static async Task PackageNpmArgv()
    {
        using var sandbox = new Sandbox("pkg-npm-argv");
        var npmRoot = Path.Combine(sandbox.AgentDir, "npm");
        var processes = new FakeProcesses();
        await Manager(sandbox, J("""{"npmCommand":["mise","exec","node@20","--","npm"]}"""), processes: processes).InstallAsync("npm:@scope/pkg");
        Check(processes.Ran("mise", ["exec", "node@20", "--", "npm", "install", "@scope/pkg", "--prefix", npmRoot, "--legacy-peer-deps"]), "npmCommand argv install");
        Equal("{\n  \"name\": \"pi-extensions\",\n  \"private\": true\n}", File.ReadAllText(Path.Combine(npmRoot, "package.json")), "managed npm project");
        Equal("*\n!.gitignore\n", File.ReadAllText(Path.Combine(npmRoot, ".gitignore")), "managed npm root ignored by git");

        processes = new FakeProcesses();
        await Manager(sandbox, processes: processes).RemoveAsync("npm:@scope/pkg");
        Check(processes.Ran("npm", ["uninstall", "@scope/pkg", "--prefix", npmRoot, "--legacy-peer-deps"]), "uninstall with legacy peer deps");
        processes = new FakeProcesses();
        await Manager(sandbox, J("""{"npmCommand":["mise","exec","bun@1","--","bun"]}"""), processes: processes).InstallAsync("npm:@scope/pkg");
        Check(processes.Ran("mise", ["exec", "bun@1", "--", "bun", "install", "@scope/pkg", "--cwd", npmRoot, "--omit=peer"]), "bun --cwd");
        processes = new FakeProcesses();
        await Manager(sandbox, J("""{"npmCommand":["pnpm"]}"""), processes: processes).InstallAsync("npm:x");
        Check(processes.Ran("pnpm", ["install", "x", "--prefix", npmRoot, "--config.auto-install-peers=false", "--config.strict-peer-dependencies=false", "--config.strict-dep-builds=false"]), "pnpm");

        var wrapped = Manager(sandbox, J("""{"npmCommand":["npm","exec","--","pnpm"]}"""));
        Equal("pnpm", wrapped.GetPackageManagerName(), "manager after the separator");
        Names(["install", "--prod", "--config.auto-install-peers=false", "--config.strict-peer-dependencies=false", "--config.strict-dep-builds=false"], wrapped.GetGitDependencyInstallArgs(), "pnpm git deps");
        Equal("pnpm", Manager(sandbox, J("""{"npmCommand":["corepack","pnpm"]}""")).GetPackageManagerName(), "corepack wrapper");
        // basename(command) without .cmd/.exe; a platform path to the shim (node:path basename splits only native separators).
        Equal("npm", Manager(sandbox, new JsonObject { ["npmCommand"] = new JsonArray(Path.Combine(sandbox.Root, "tools", "npm.cmd")) }).GetPackageManagerName(), "extension stripped");
        Equal("Ambiguous npmCommand package managers: pnpm, bun", Throws<PiPackageException>(() => Manager(sandbox, J("""{"npmCommand":["wrap","pnpm","bun"]}""")).GetPackageManagerName(), "ambiguous").Message, "ambiguous");
        Equal("Invalid npmCommand: first array entry must be a non-empty command", Throws<PiPackageException>(() => Manager(sandbox, J("""{"npmCommand":[""]}""")).GetNpmCommand(), "empty").Message, "empty command");

        // npm root -g lookup through npmCommand, cached per command.
        var root20 = Path.Combine(sandbox.Root, "node20", "lib", "node_modules"); Directory.CreateDirectory(Path.Combine(root20, "@scope", "pkg"));
        var syncs = new FakeProcesses { OnSync = (command, args) => command == "mise" && args[1] == "node@20" ? root20 : Path.Combine(sandbox.Root, "node22") };
        var legacy = Manager(sandbox, J("""{"npmCommand":["mise","exec","node@20","--","npm"]}"""), processes: syncs);
        Equal(Path.Combine(root20, "@scope", "pkg"), legacy.GetInstalledPath("npm:@scope/pkg", "user"), "legacy global install");
        Equal(Path.Combine(root20, "@scope", "pkg"), legacy.GetInstalledPath("npm:@scope/pkg", "user"), "cached");
        Equal(1, syncs.Syncs.Count, "npm root -g once"); Names(["exec", "node@20", "--", "npm", "root", "-g"], syncs.Syncs[0].Args, "root argv");

        // pnpm global packages from pnpm list output; malformed output is ignored.
        var pnpmPath = Path.Combine(sandbox.Root, "pnpm", "global", "v11", "20-hash", "node_modules", "pnpm-pkg");
        sandbox.Write(Path.Combine(pnpmPath, "package.json"), Jstr(new { name = "pnpm-pkg", version = "1.0.0" }));
        sandbox.Write(Path.Combine(pnpmPath, "extensions", "index.ts"), "x");
        var pnpm = new FakeProcesses
        {
            OnSync = (command, args) => string.Join(' ', args) == "list -g --depth 0 --json" ? Jstr(new[] { new { path = "x", dependencies = new Dictionary<string, object> { ["pnpm-pkg"] = new { version = "1.0.0", path = pnpmPath } } } }) : throw new PiPackageException("unexpected")
        };
        var pnpmManager = Manager(sandbox, J("""{"npmCommand":["pnpm"],"packages":["npm:pnpm-pkg"]}"""), processes: pnpm);
        Check(Enabled((await pnpmManager.ResolveAsync()).Extensions, "pnpm-pkg/extensions/index.ts"), "legacy pnpm package loaded");
        Equal(0, pnpm.Runs.Count, "nothing installed"); Equal(pnpmPath, pnpmManager.GetInstalledPath("npm:pnpm-pkg", "user"), "pnpm path");
        Equal(null, Manager(sandbox, J("""{"npmCommand":["pnpm"]}"""), processes: new FakeProcesses { OnSync = (_, _) => "not json" }).GetInstalledPath("npm:other-pkg", "user"), "malformed list ignored");

        // Missing user packages install into the managed root once.
        var managed = Path.Combine(npmRoot, "node_modules", "fresh-pkg");
        var installs = new FakeProcesses
        {
            OnSync = (_, _) => throw new PiPackageException("legacy lookup unavailable"),
            OnRun = (_, _, _) => { sandbox.Write(Path.Combine(managed, "package.json"), Jstr(new { name = "fresh-pkg", version = "1.0.0" })); sandbox.Write(Path.Combine(managed, "extensions", "index.ts"), "x"); return Task.CompletedTask; }
        };
        var fresh = Manager(sandbox, J("""{"packages":["npm:fresh-pkg"]}"""), processes: installs);
        Check(Enabled((await fresh.ResolveAsync()).Extensions, "fresh-pkg/extensions/index.ts") && Enabled((await fresh.ResolveAsync()).Extensions, "fresh-pkg/extensions/index.ts"), "installed and loaded");
        Equal(1, installs.Runs.Count, "installed once"); Equal(managed, fresh.GetInstalledPath("npm:fresh-pkg", "user"), "managed path");

        // Progress events around a failing install.
        var progress = new List<PiPackageProgressEvent>();
        var failing = Manager(sandbox, processes: new FakeProcesses { OnRun = (_, _, _) => throw new PiPackageException("simulated npm install failure") });
        failing.SetProgressCallback(progress.Add);
        Equal("simulated npm install failure", (await ThrowsAsync<PiPackageException>(() => failing.InstallAsync("npm:nonexistent-package@1.0.0"))).Message, "failure surfaces");
        Names(["start:install:Installing npm:nonexistent-package@1.0.0...", "error:install:simulated npm install failure"], progress.Select(e => $"{e.Type}:{e.Action}:{e.Message}"), "progress");
    }

    private static async Task<T> ThrowsAsync<T>(Func<Task> run) where T : Exception
    {
        try { await run(); } catch (T error) { return error; }
        throw new InvalidOperationException("expected " + typeof(T).Name);
    }

    private static async Task PackageGitArgv()
    {
        using var sandbox = new Sandbox("pkg-git-argv");
        const string source = "git:github.com/user/repo";
        var target = Path.Combine(sandbox.AgentDir, "git", "github.com", "user", "repo");
        FakeProcesses Cloning(Action? afterClone = null, Func<string, string[], Task>? other = null) => new()
        {
            OnRun = async (command, args, _) =>
            {
                if (command == "git" && args[0] == "clone") { Directory.CreateDirectory(target); sandbox.Write(Path.Combine(target, "package.json"), Jstr(new { name = "repo", version = "1.0.0" })); afterClone?.Invoke(); return; }
                if (other is not null) await other(command, args);
            }
        };
        var processes = Cloning();
        await Manager(sandbox, processes: processes).InstallAsync(source);
        Check(processes.Ran("git", ["clone", "https://github.com/user/repo", target]), "clone");
        Check(processes.Ran("npm", ["install", "--omit=dev", "--legacy-peer-deps"], target), "git dependencies without peers");
        Equal("*\n!.gitignore\n", File.ReadAllText(Path.Combine(sandbox.AgentDir, "git", ".gitignore")), "git root ignored");
        PiPackageManager.DeleteTree(Path.Combine(sandbox.AgentDir, "git"));

        processes = Cloning();
        await Manager(sandbox, J("""{"npmCommand":["corepack","pnpm"]}"""), processes: processes).InstallAsync(source);
        Check(processes.Ran("corepack", ["pnpm", "install", "--prod", "--config.auto-install-peers=false", "--config.strict-peer-dependencies=false", "--config.strict-dep-builds=false"], target), "corepack pnpm deps");
        PiPackageManager.DeleteTree(Path.Combine(sandbox.AgentDir, "git"));
        processes = Cloning();
        await Manager(sandbox, J("""{"npmCommand":["bun"]}"""), processes: processes).InstallAsync(source);
        Check(processes.Ran("bun", ["install", "--omit=dev", "--omit=peer"], target), "bun deps");
        PiPackageManager.DeleteTree(Path.Combine(sandbox.AgentDir, "git"));

        var cloneFails = new FakeProcesses { OnRun = (command, args, _) => { if (args[0] == "clone") { Directory.CreateDirectory(target); throw new PiPackageException("simulated git clone failure"); } return Task.CompletedTask; } };
        Equal("simulated git clone failure", (await ThrowsAsync<PiPackageException>(() => Manager(sandbox, processes: cloneFails).InstallAsync(source))).Message, "clone failure");
        Check(!Directory.Exists(target) && !Directory.Exists(Path.Combine(sandbox.AgentDir, "git", "github.com")), "failed checkout and empty parents removed");
        var depsFail = Cloning(other: (command, _) => command == "npm" ? throw new PiPackageException("simulated dependency install failure") : Task.CompletedTask);
        Equal("simulated dependency install failure", (await ThrowsAsync<PiPackageException>(() => Manager(sandbox, processes: depsFail).InstallAsync(source))).Message, "deps failure");
        Check(!Directory.Exists(target), "checkout removed after dependency failure");

        // An existing checkout is reconciled to a pinned ref.
        sandbox.Write(Path.Combine(target, "package.json"), Jstr(new { name = "repo", version = "1.0.0" }));
        processes = new FakeProcesses { OnCapture = (_, args, _) => args is ["rev-parse", "HEAD"] ? "old-head" : args is ["rev-parse", "FETCH_HEAD^{commit}"] ? "new-head" : null };
        await Manager(sandbox, processes: processes).InstallAsync(source + "@v2");
        Check(processes.Ran("git", ["fetch", "origin", "v2"], target) && processes.Ran("git", ["reset", "--hard", "FETCH_HEAD^{commit}"], target) &&
            processes.Ran("git", ["clean", "-fdx"], target) && processes.Ran("npm", ["install", "--omit=dev", "--legacy-peer-deps"], target), "pinned ref reconciled");
        Check(!File.Exists(Path.Combine(sandbox.AgentDir, "git", "github.com", "user", ".repo.pi-update-incomplete")), "update marker cleared");

        // Without a ref the update target is the upstream branch.
        string? UpstreamCapture(string[] args, string local, string remote) => args switch
        {
            ["rev-parse", "--abbrev-ref", "@{upstream}"] => "origin/main", ["rev-parse", "@{upstream}"] => remote, ["rev-parse", "@{upstream}^{commit}"] => remote,
            ["rev-parse", "HEAD"] => local, _ => null
        };
        processes = new FakeProcesses { OnCapture = (_, args, _) => UpstreamCapture(args, "old-head", "new-head") };
        await Manager(sandbox, J("""{"packages":["git:github.com/user/repo"]}"""), processes: processes).UpdateAsync(source);
        Check(processes.Ran("git", ["fetch", "--prune", "--no-tags", "origin", "+refs/heads/main:refs/remotes/origin/main"], target) &&
            processes.Ran("git", ["reset", "--hard", "@{upstream}^{commit}"], target) && processes.Ran("git", ["clean", "-fdx"], target), "upstream branch update");
        // origin/HEAD fallback when there is no upstream.
        processes = new FakeProcesses
        {
            OnCapture = (_, args, _) => args switch
            {
                ["rev-parse", "--abbrev-ref", "@{upstream}"] => throw new PiPackageException("no upstream"), ["rev-parse", "origin/HEAD"] => "new-head",
                ["symbolic-ref", "refs/remotes/origin/HEAD"] => "refs/remotes/origin/trunk", ["rev-parse", "HEAD"] => "old-head", ["rev-parse", "origin/HEAD^{commit}"] => "new-head", _ => null
            }
        };
        await Manager(sandbox, processes: processes).InstallAsync(source);
        Check(processes.Ran("git", ["remote", "set-head", "origin", "-a"], target) && processes.Ran("git", ["fetch", "--prune", "--no-tags", "origin", "+refs/heads/trunk:refs/remotes/origin/trunk"], target) &&
            processes.Ran("git", ["reset", "--hard", "origin/HEAD^{commit}"], target), "origin/HEAD fallback");

        // Current checkout: missing dependencies are repaired without cleaning.
        sandbox.Write(Path.Combine(target, "package.json"), Jstr(new { name = "repo", version = "1.0.0", dependencies = new { dependency = "1.0.0" } }));
        processes = new FakeProcesses { OnCapture = (_, args, _) => UpstreamCapture(args, "current-head", "current-head") };
        await Manager(sandbox, J("""{"packages":["git:github.com/user/repo"]}"""), processes: processes).UpdateAsync(source);
        Check(processes.Ran("npm", ["install", "--omit=dev", "--legacy-peer-deps"], target) && !processes.Ran("git", ["clean", "-fdx"], target), "repair without clean");
        processes = new FakeProcesses { OnCapture = (_, args, _) => UpstreamCapture(args, "old-head", "new-head"), OnRun = (_, args, _) => args[0] == "clean" ? throw new PiPackageException("simulated clean failure") : Task.CompletedTask };
        Equal("simulated clean failure", (await ThrowsAsync<PiPackageException>(() => Manager(sandbox, J("""{"packages":["git:github.com/user/repo"]}"""), processes: processes).UpdateAsync(source))).Message, "clean failure");
        Check(processes.Ran("npm", ["install", "--omit=dev", "--legacy-peer-deps"], target), "dependencies repaired after a failed clean");

        // Project scope with a wrapped pnpm.
        var projectTarget = Path.Combine(sandbox.Cwd, ".pi", "git", "github.com", "user", "repo");
        sandbox.Write(Path.Combine(projectTarget, "package.json"), Jstr(new { name = "repo", version = "1.0.0" }));
        processes = new FakeProcesses { OnCapture = (_, args, _) => UpstreamCapture(args, "local-head", "remote-head") };
        await Manager(sandbox, J("""{"npmCommand":["mise","exec","node@20","--","pnpm"]}"""), J("""{"packages":["git:github.com/user/repo"]}"""), processes: processes).UpdateAsync(source);
        Check(processes.Ran("mise", ["exec", "node@20", "--", "pnpm", "install", "--prod", "--config.auto-install-peers=false", "--config.strict-peer-dependencies=false", "--config.strict-dep-builds=false"], projectTarget), "project git deps through wrapped pnpm");

        // Removing deletes the checkout and prunes empty parents up to the git root.
        await Manager(sandbox, processes: new FakeProcesses()).RemoveAsync(source);
        Check(!Directory.Exists(Path.Combine(sandbox.AgentDir, "git", "github.com")) && Directory.Exists(Path.Combine(sandbox.AgentDir, "git")), "git removal prunes parents");

        // A protocol URL without git: is a git source.
        processes = new FakeProcesses { OnRun = (_, _, _) => throw new PiPackageException("simulated git clone failure") };
        await ThrowsAsync<PiPackageException>(() => Manager(sandbox, processes: processes).InstallAsync("https://github.com/nonexistent/repo"));
        Check(processes.Runs[0].Command == "git" && processes.Runs[0].Args[0] == "clone" && processes.Runs[0].Args[1] == "https://github.com/nonexistent/repo", "https clone");
    }

    private static void PackageInstallPaths()
    {
        using var sandbox = new Sandbox("pkg-paths");
        var manager = Manager(sandbox);
        var temp = manager.GetNpmInstallPath((PiNpmSource)PiPackageSource.Parse("npm:left-pad"), "temporary");
        var tempRoot = Path.Combine(sandbox.AgentDir, "tmp", "extensions");
        Check(Norm(temp).EndsWith("node_modules/left-pad", StringComparison.Ordinal) && temp.StartsWith(tempRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal), "temporary npm under the agent temp folder");
        if (!OperatingSystem.IsWindows()) Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(tempRoot), "private temp folder");
        var traversal = new PiGitSource("git@evil.example:../../victim/repo", "evil.example", "../../victim/repo", null);
        foreach (var scope in new[] { "user", "project", "temporary" })
            Check(Throws<PiPackageException>(() => manager.GetGitInstallPath(traversal, scope), scope).Message.Contains("outside package install root", StringComparison.Ordinal), "traversal refused " + scope);
        var git = (PiGitSource)PiPackageSource.Parse("git:github.com/user/repo@v1");
        var pinned = manager.GetGitInstallPath(git, "temporary"); var other = manager.GetGitInstallPath(git with { Ref = "v2" }, "temporary");
        Check(pinned != other && Norm(pinned).Contains("/git-github.com/", StringComparison.Ordinal) && Norm(pinned).EndsWith("/user/repo", StringComparison.Ordinal), "temporary git checkouts per ref");
        Equal(Path.Combine(sandbox.AgentDir, "git", "github.com", "user", "repo"), manager.GetGitInstallPath(git, "user"), "user git path");
        Equal(Path.Combine(sandbox.Cwd, ".pi", "git", "github.com", "user", "repo"), manager.GetGitInstallPath(git, "project"), "project git path");
        Equal(Path.Combine(sandbox.Cwd, ".pi", "npm", "node_modules", "@a", "b"), manager.GetNpmInstallPath((PiNpmSource)PiPackageSource.Parse("npm:@a/b@1.0.0"), "project"), "project npm path");
        var untrusted = Manager(sandbox, trusted: false);
        Equal("Project is not trusted; refusing to access project package storage", Throws<PiPackageException>(() => untrusted.GetGitInstallPath(git, "project"), "untrusted").Message, "untrusted project storage");
    }

    private static async Task PackageOfflineAndUpdates()
    {
        using var sandbox = new Sandbox("pkg-updates");
        var offline = new Dictionary<string, string?> { ["PI_OFFLINE"] = "1" };
        var processes = new FakeProcesses { OnSync = (_, _) => throw new PiPackageException("no legacy root") };
        var resolved = await Manager(sandbox, J("""{"packages":["npm:missing-pkg","git:github.com/user/missing"]}"""), processes: processes, environment: offline).ResolveAsync();
        Check(processes.Runs.Count == 0 && resolved.Extensions.IsEmpty, "offline: missing packages are not installed");
        await Manager(sandbox, J("""{"packages":["npm:missing-pkg","git:github.com/user/missing"]}"""), processes: processes, environment: new() { ["PI_OFFLINE"] = "TRUE" }).UpdateAsync();
        Equal(0, processes.Runs.Count + processes.Captures.Count, "offline update does nothing");
        Check((await Manager(sandbox, J("""{"packages":["npm:x"]}"""), processes: processes, environment: offline).CheckForAvailableUpdatesAsync()).IsEmpty, "offline update check");

        Equal("No matching package found for pi-formatter. Did you mean npm:pi-formatter?",
            (await ThrowsAsync<PiPackageException>(() => Manager(sandbox, J("""{"packages":["npm:pi-formatter"]}""")).UpdateAsync("pi-formatter"))).Message, "npm suggestion");
        Equal("No matching package found for github.com/user/repo@v1. Did you mean git:github.com/user/repo@v1?",
            (await ThrowsAsync<PiPackageException>(() => Manager(sandbox, J("""{"packages":["git:github.com/user/repo@v1"]}""")).UpdateAsync("github.com/user/repo@v1"))).Message, "git suggestion");
        Equal("No matching package found for npm:absent", (await ThrowsAsync<PiPackageException>(() => Manager(sandbox).UpdateAsync("npm:absent"))).Message, "no suggestion");

        void Installed(string root, string name, string version) => sandbox.Write(Path.Combine(root, "node_modules", name, "package.json"), Jstr(new { name, version }));
        var userRoot = Path.Combine(sandbox.AgentDir, "npm"); var projectRoot = Path.Combine(sandbox.Cwd, ".pi", "npm");
        Installed(userRoot, "range-pkg", "1.0.0");
        processes = new FakeProcesses { OnCapture = (_, args, _) => args[1] == "range-pkg@^1.0.0" ? """["1.0.0","1.2.0","2.0.0"]""" : null };
        await Manager(sandbox, J("""{"packages":["npm:range-pkg@^1.0.0"]}"""), processes: processes).UpdateAsync();
        Names(["view", "range-pkg@^1.0.0", "version", "--json"], processes.Captures[0].Args, "npm view with the configured spec");
        Check(processes.Ran("npm", ["install", "range-pkg@^1.0.0", "--prefix", userRoot, "--legacy-peer-deps"]), "update installs the configured spec");

        Installed(projectRoot, "current-pkg", "1.0.0");
        processes = new FakeProcesses { OnCapture = (_, _, _) => "\"1.0.0\"" };
        await Manager(sandbox, project: J("""{"packages":["npm:current-pkg"]}"""), processes: processes).UpdateAsync();
        Equal(0, processes.Runs.Count, "current project package skipped");
        processes = new FakeProcesses { OnCapture = (_, _, _) => "\"0.9.0\"" };
        await Manager(sandbox, project: J("""{"packages":["npm:current-pkg"]}"""), processes: processes).UpdateAsync();
        Equal(0, processes.Runs.Count, "newer installed version skipped");

        // Batched per scope, pinned npm skipped, git updated in parallel.
        Installed(userRoot, "a", "1.0.0"); Installed(userRoot, "b", "1.0.0"); Installed(projectRoot, "c", "1.0.0"); Installed(userRoot, "pinned", "1.0.0");
        var gitTarget = Path.Combine(sandbox.AgentDir, "git", "github.com", "user", "tool"); Directory.CreateDirectory(gitTarget);
        var progress = new List<PiPackageProgressEvent>();
        processes = new FakeProcesses
        {
            OnCapture = (_, args, _) => args[0] == "view" ? "\"2.0.0\"" : args switch
            {
                ["rev-parse", "--abbrev-ref", "@{upstream}"] => "origin/main", ["rev-parse", "HEAD"] => "same", _ => "same"
            }
        };
        var batch = Manager(sandbox, J("""{"packages":["npm:a","npm:b","npm:pinned@1.0.0","git:github.com/user/tool"]}"""), J("""{"packages":["npm:c"]}"""), processes: processes);
        batch.SetProgressCallback(progress.Add);
        await batch.UpdateAsync();
        Check(processes.Ran("npm", ["install", "a@latest", "b@latest", "--prefix", userRoot, "--legacy-peer-deps"]), "user batch");
        Check(processes.Ran("npm", ["install", "c@latest", "--prefix", projectRoot, "--legacy-peer-deps"]), "project batch");
        Check(!processes.Runs.Any(run => run.Args.Contains("pinned@1.0.0")) && !processes.Captures.Any(run => run.Args.Contains("pinned")), "pinned npm skipped");
        Check(processes.Ran("git", ["fetch", "--prune", "--no-tags", "origin", "+refs/heads/main:refs/remotes/origin/main"], gitTarget), "git fetched");
        Check(progress.Any(e => e is { Type: "start", Action: "update", Source: "user npm packages", Message: "Updating user npm packages..." }) &&
            progress.Any(e => e is { Type: "start", Source: "npm:c", Message: "Updating npm:c..." }) &&
            progress.Any(e => e is { Type: "complete", Source: "git:github.com/user/tool" }), "update progress");

        // resolve(): installed unpinned packages are used as they are; a pinned version mismatch reinstalls.
        Installed(userRoot, "used-pkg", "1.0.0");
        processes = new FakeProcesses();
        await Manager(sandbox, J("""{"packages":["npm:used-pkg"]}"""), processes: processes).ResolveAsync();
        Equal(0, processes.Captures.Count + processes.Runs.Count, "no npm view during resolve");
        processes = new FakeProcesses();
        await Manager(sandbox, J("""{"packages":["npm:used-pkg@2.0.0"]}"""), processes: processes).ResolveAsync();
        Check(processes.Ran("npm", ["install", "used-pkg@2.0.0", "--prefix", userRoot, "--legacy-peer-deps"]), "pinned mismatch reinstalled");
        var skipped = new List<string>();
        processes = new FakeProcesses();
        await Manager(sandbox, J("""{"packages":["npm:used-pkg@2.0.0"]}"""), processes: processes).ResolveAsync(source => { skipped.Add(source); return Task.FromResult(PiMissingSourceAction.Skip); });
        Check(processes.Runs.Count == 0 && skipped.SequenceEqual(["npm:used-pkg@2.0.0"]), "onMissing skip");
        Equal("Missing source: npm:used-pkg@2.0.0", (await ThrowsAsync<PiPackageException>(() => Manager(sandbox, J("""{"packages":["npm:used-pkg@2.0.0"]}""")).ResolveAsync(_ => Task.FromResult(PiMissingSourceAction.Error)))).Message, "onMissing error");

        // Update checks.
        processes = new FakeProcesses { OnCapture = (_, args, _) => args[0] == "view" ? "\"1.5.0\"" : null };
        var updates = await Manager(sandbox, J("""{"packages":["npm:used-pkg","npm:a@1.0.0"]}"""), processes: processes).CheckForAvailableUpdatesAsync();
        Check(updates.Single() is { Source: "npm:used-pkg", DisplayName: "used-pkg", Type: "npm", Scope: "user" }, "available npm update; pinned skipped");
        processes = new FakeProcesses { OnCapture = (_, args, _) => args[0] == "view" ? "\"0.5.0\"" : null };
        Check((await Manager(sandbox, J("""{"packages":["npm:used-pkg"]}"""), processes: processes).CheckForAvailableUpdatesAsync()).IsEmpty, "older registry version is no update");
        processes = new FakeProcesses { OnCapture = (command, args, _) => command == "mise" && args[4] == "view" ? "\"9.0.0\"" : null };
        Check((await Manager(sandbox, J("""{"npmCommand":["mise","exec","node@20","--","npm"],"packages":["npm:used-pkg"]}"""), processes: processes).CheckForAvailableUpdatesAsync()).Length == 1, "npmCommand argv for update checks");
        processes = new FakeProcesses
        {
            OnCapture = (_, args, _) => args switch
            {
                ["rev-parse", "HEAD"] => "1111111111111111111111111111111111111111", ["rev-parse", "--abbrev-ref", "@{upstream}"] => "origin/main",
                ["ls-remote", "origin", "refs/heads/main"] => "2222222222222222222222222222222222222222\trefs/heads/main", _ => null
            }
        };
        updates = await Manager(sandbox, J("""{"packages":["git:github.com/user/tool"]}"""), processes: processes).CheckForAvailableUpdatesAsync();
        Check(updates.Single() is { DisplayName: "github.com/user/tool", Type: "git" }, "git update available");
    }

    // ---- CLI -----------------------------------------------------------------------------------------------------------------

    private static async Task PackageCliParseAndHelp()
    {
        using var sandbox = new Sandbox("pkg-cli-help");
        var (code, stdout, stderr) = await sandbox.Run("install", "--help");
        Equal(0, code, "help exit");
        Equal("""
            Usage:
              pisharp install <source> [-l] [--approve|--no-approve]

            Install a package and add it to settings.

            Options:
              -l, --local       Install project-locally (.pi/settings.json)
              -a, --approve     Trust project-local files for this command
              -na, --no-approve Ignore project-local files for this command

            Examples:
              pisharp install npm:@foo/bar
              pisharp install git:github.com/user/repo
              pisharp install git:git@github.com:user/repo
              pisharp install https://github.com/user/repo
              pisharp install ssh://git@github.com/user/repo
              pisharp install ./local/path


            """, stdout, "install help"); Equal("", stderr, "no stderr");
        Check((await sandbox.Run("uninstall", "-h")).Out.Contains("Alias: pisharp uninstall <source> [-l]\n", StringComparison.Ordinal), "remove help");
        Check((await sandbox.Run("update", "--help")).Out.Contains("  pisharp update pi             Update pi only (self works as alias to pi)\n", StringComparison.Ordinal), "update help");
        Check((await sandbox.Run("list", "--help")).Out.StartsWith("Usage:\n  pisharp list [--approve|--no-approve]\n\nList installed packages from user and project settings.\n", StringComparison.Ordinal), "list help");
        Check((await sandbox.Run("config", "--help")).Out.Contains("Without -l, starts in global settings (~/.pi/agent/settings.json).\n", StringComparison.Ordinal), "config help");

        (code, stdout, stderr) = await sandbox.Run("install", "--unknown");
        Equal(1, code, "unknown exit"); Equal("Unknown option --unknown for \"install\".\nUse \"pisharp --help\" or \"pisharp install <source> [-l] [--approve|--no-approve]\".\n", stderr, "unknown option");
        (code, _, stderr) = await sandbox.Run("install");
        Equal(1, code, "missing exit"); Equal("Missing install source.\nUsage: pisharp install <source> [-l] [--approve|--no-approve]\n", stderr, "missing source");
        (code, _, stderr) = await sandbox.Run("update", "--models", "--self");
        Equal(1, code, "conflict exit"); Check(stderr.StartsWith("--models cannot be combined with --self, --extensions, --all, or --extension\nUsage: pisharp update ", StringComparison.Ordinal), "models conflict");
        (_, _, stderr) = await sandbox.Run("update", "--extension");
        Check(stderr.StartsWith("Missing value for --extension.\n", StringComparison.Ordinal), "missing --extension value");
        (_, _, stderr) = await sandbox.Run("remove", "a", "b");
        Check(stderr.StartsWith("Unexpected argument b.\nUsage: pisharp remove", StringComparison.Ordinal), "unexpected argument");
        (_, _, stderr) = await sandbox.Run("list", "-l");
        Check(stderr.StartsWith("Unknown option -l for \"list\".", StringComparison.Ordinal), "-l only for install/remove");
        (_, _, stderr) = await sandbox.Run("config", "extra");
        Equal("Unexpected argument extra.\nUsage: pisharp config [-l] [--approve|--no-approve]\n", stderr, "config argument");

        PiPackageCommands.Options Parse(params string[] args) => PiPackageCommands.Parse(args)!;
        Equal(("self", (string?)null), Parse("update").UpdateTarget, "default self"); Check(Parse("update").ShowExtensionsSkippedNote, "skipped note");
        Equal(("all", (string?)null), Parse("update", "--all").UpdateTarget, "--all"); Equal(("all", (string?)null), Parse("update", "--self", "--extensions").UpdateTarget, "self+extensions");
        Equal(("all", (string?)null), Parse("update", "pi", "--extensions").UpdateTarget, "pi --extensions"); Equal(("self", (string?)null), Parse("update", "self").UpdateTarget, "self alias");
        Equal(("extensions", (string?)"npm:x"), Parse("update", "npm:x").UpdateTarget, "positional source");
        Equal(("extensions", (string?)"npm:y"), Parse("update", "--extension", "npm:y").UpdateTarget, "--extension");
        Equal("--extension can only be provided once", Parse("update", "--extension", "a", "--extension", "b").ConflictingOptions, "twice");
        Equal("--all cannot be combined with a positional source", Parse("update", "--all", "npm:x").ConflictingOptions, "all+source");
        Equal("positional update targets cannot be combined with --self, --extensions, or --all", Parse("update", "npm:x", "--self").ConflictingOptions, "source+self");
        Equal("remove", Parse("uninstall", "x").Command, "uninstall alias"); Check(Parse("install", "x", "-a").ProjectTrustOverride == true && Parse("list", "-na").ProjectTrustOverride == false, "trust flags");
        Equal(null, PiPackageCommands.Parse(["auth"]), "not a package command");
    }

    private static async Task PackageCliTrust()
    {
        async Task<string> List(Sandbox sandbox, params string[] extra) { var (code, stdout, stderr) = await sandbox.Run(["list", .. extra]); Equal(0, code, "list exit; " + stderr); return stdout; }
        void ProjectPackages(Sandbox sandbox) => sandbox.Write(Path.Combine(sandbox.Cwd, ".pi", "settings.json"), """{"packages":["npm:@project/pkg"]}""");

        using (var untrusted = new Sandbox("pkg-trust-a", trusted: false))
        {
            ProjectPackages(untrusted);
            Equal("No packages installed.\n", await List(untrusted), "untrusted project skipped");
            Equal("Project packages:\n  npm:@project/pkg\n", await List(untrusted, "--approve"), "--approve trusts for the command");
            new ProjectTrustStore(untrusted.AgentDir, untrusted.Home).Set(untrusted.Cwd, true);
            Equal("Project packages:\n  npm:@project/pkg\n", await List(untrusted), "remembered trust");
            Equal("No packages installed.\n", await List(untrusted, "--no-approve"), "--no-approve overrides remembered trust");
            new ProjectTrustStore(untrusted.AgentDir, untrusted.Home).Set(untrusted.Cwd, false);
            untrusted.Write(Path.Combine(untrusted.AgentDir, "settings.json"), """{"defaultProjectTrust":"always"}""");
            Equal("No packages installed.\n", await List(untrusted), "trust.json overrides defaultProjectTrust");
            new ProjectTrustStore(untrusted.AgentDir, untrusted.Home).Set(untrusted.Cwd, null);
            Equal("Project packages:\n  npm:@project/pkg\n", await List(untrusted), "defaultProjectTrust always");
        }
        using (var blocked = new Sandbox("pkg-trust-b", trusted: false))
        {
            blocked.Write(Path.Combine(blocked.Cwd, ".pi", "settings.json"), "{}");
            Directory.CreateDirectory(Path.Combine(blocked.Root, "local-package"));
            var (code, _, stderr) = await blocked.Run("install", "-l", "../local-package");
            Equal(1, code, "blocked exit"); Equal("Project is not trusted. Use --approve to modify local package config.\n", stderr, "blocked message");
            (code, _, stderr) = await blocked.Run("config", "-l");
            Equal(1, code, "config -l blocked"); Equal("Project is not trusted. Use --approve to modify local resource config.\n", stderr, "config blocked message");
        }
        using (var fresh = new Sandbox("pkg-trust-c"))
        {
            // A trusted project without .pi yet: -l initializes .pi/settings.json.
            var packageDir = Path.Combine(fresh.Root, "local-package"); Directory.CreateDirectory(packageDir);
            var (code, stdout, stderr) = await fresh.Run("install", "-l", packageDir);
            Equal(0, code, "fresh install; " + stderr); Equal($"Installing {packageDir}...\nInstalled {packageDir}\n", stdout, "install output");
            var stored = JsonNode.Parse(File.ReadAllText(Path.Combine(fresh.Cwd, ".pi", "settings.json")))!["packages"]![0]!.GetValue<string>();
            Equal(Path.GetFullPath(packageDir), Path.GetFullPath(Path.Combine(fresh.Cwd, ".pi", stored)), "stored relative to .pi");
        }
    }

    private static async Task PackageCliLocalDirectory()
    {
        using var sandbox = new Sandbox("pkg-cli-local");
        var relativeDir = Path.Combine(sandbox.Cwd, "packages", "local-package");
        sandbox.Write(Path.Combine(relativeDir, "extensions", "index.ts"), "export default function() {}");
        var (code, stdout, stderr) = await sandbox.Run("install", "./packages/local-package");
        Equal(0, code, "install; " + stderr); Equal("Installing ./packages/local-package...\nInstalled ./packages/local-package\n", stdout, "install output");
        var settingsPath = Path.Combine(sandbox.AgentDir, "settings.json");
        var stored = JsonNode.Parse(File.ReadAllText(settingsPath))!["packages"]![0]!.GetValue<string>();
        Equal(Path.GetRelativePath(sandbox.AgentDir, relativeDir), stored, "stored relative to settings.json");
        Equal(relativeDir, Path.GetFullPath(Path.Combine(sandbox.AgentDir, stored)), "resolves back");
        (code, stdout, _) = await sandbox.Run("list");
        Equal($"User packages:\n  {stored}\n    {relativeDir}\n", stdout, "list output");

        var other = Path.Combine(sandbox.Root, "local-package"); Directory.CreateDirectory(other);
        Equal(0, (await sandbox.Run("install", other + "/")).Code, "install with trailing slash");
        (code, stdout, _) = await sandbox.Run("remove", other + "/");
        Equal(0, code, "remove"); Equal($"Removing {other}/...\nRemoved {other}/\n", stdout, "remove output");
        Equal(1, JsonNode.Parse(File.ReadAllText(settingsPath))!["packages"]!.AsArray().Count, "one package left");
        (code, _, stderr) = await sandbox.Run("remove", other);
        Equal(1, code, "remove missing"); Equal($"No matching package found for {other}\n", stderr, "remove missing message");
        (code, _, stderr) = await sandbox.Run("install", Path.Combine(sandbox.Root, "absent"));
        Equal(1, code, "install missing path"); Equal($"Error: Path does not exist: {Path.Combine(sandbox.Root, "absent")}\n", stderr, "missing path");

        sandbox.Write(settingsPath, Jstr(new { packages = new[] { "npm:pi-formatter" } }));
        (code, stdout, stderr) = await sandbox.Run("update", "pi-formatter");
        Equal(1, code, "update suggestion exit"); Equal("Error: No matching package found for pi-formatter. Did you mean npm:pi-formatter?\n", stderr, "suggestion");
        Check(!stdout.Contains("Updated", StringComparison.Ordinal), "nothing updated");
        Equal(Jstr(new { packages = new[] { "npm:pi-formatter" } }), File.ReadAllText(settingsPath), "settings untouched");

        sandbox.Vars["PI_OFFLINE"] = "1";
        (code, stdout, stderr) = await sandbox.Run("update");
        Equal(1, code, "self update exit");
        Equal("Extensions are skipped. Run pisharp update --extensions to update extensions.\n", stdout, "skipped note");
        Equal("error: pisharp cannot self-update this installation.\nUpdate pisharp using the package manager, wrapper, or source checkout that provides this installation.\n", stderr, "self update unavailable");
        (code, stdout, _) = await sandbox.Run("update", "--extensions");
        Equal(0, code, "offline extension update"); Equal("Updated packages\n", stdout, "nothing to do offline");
    }

    private static async Task PackageCliUpdateTrust()
    {
        using var sandbox = new Sandbox("pkg-cli-update", trusted: false);
        sandbox.Write(Path.Combine(sandbox.AgentDir, "settings.json"), """{"defaultProjectTrust":"always"}""");
        sandbox.Write(Path.Combine(sandbox.Cwd, ".pi", "settings.json"), """{"packages":["npm:fake-package"],"npmCommand":["fake-npm","--flag"]}""");
        var processes = new FakeProcesses { OnCapture = (_, _, _) => "\"1.0.0\"" };
        var (code, stdout, stderr) = await RunPackages(sandbox, (_, _) => processes, "update", "--extensions");
        Equal(0, code, "update; " + stderr); Equal("Updated packages\n", stdout, "nothing updated");
        Equal(0, processes.Runs.Count + processes.Captures.Count, "update ignores defaultProjectTrust (saved trust only)");
        new ProjectTrustStore(sandbox.AgentDir, sandbox.Home).Set(sandbox.Cwd, true);
        (code, stdout, stderr) = await RunPackages(sandbox, (_, _) => processes, "update", "--extensions");
        Equal(0, code, "trusted update; " + stderr);
        Check(processes.Ran("fake-npm", ["--flag", "install", "fake-package@latest", "--prefix", Path.Combine(sandbox.Cwd, ".pi", "npm"), "--legacy-peer-deps"]), "project npmCommand used");
        Equal("Updating npm:fake-package...\nUpdated packages\n", stdout, "progress line");
        (code, _, stderr) = await RunPackages(sandbox, (_, _) => new FakeProcesses { OnRun = (_, _, _) => throw new PiPackageException("npm install fake-package@latest failed with code 1") }, "update", "npm:fake-package");
        Equal(1, code, "failure exit"); Equal("Error: npm install fake-package@latest failed with code 1\n", stderr, "failure message");
    }

    private static async Task PackageCliConfig()
    {
        using var sandbox = new Sandbox("pkg-cli-config");
        var (code, _, stderr) = await sandbox.Run("config");
        Equal(1, code, "no selector"); Check(stderr.Contains("requires the interactive terminal UI", StringComparison.Ordinal), "needs the UI");
        sandbox.Write(Path.Combine(sandbox.AgentDir, "extensions", "user.ts"), "x");
        sandbox.Write(Path.Combine(sandbox.Cwd, ".pi", "extensions", "project.ts"), "x");
        PiPackageConfigRequest? seen = null;
        using var stdout = new StringWriter(); using var stderrWriter = new StringWriter();
        var host = sandbox.Host(stdout, stderrWriter, null) with { ConfigSelector = (request, _) => { seen = request; request.Settings.SetField("global", "extensions", new JsonArray("-extensions/user.ts")); return Task.CompletedTask; } };
        Equal(0, await PiCommand.RunAsync(["config", "-l"], host, CancellationToken.None), "config exit; " + stderrWriter);
        Check(seen is { WriteScope: "project", ProjectModeAvailable: true }, "request scope");
        Check(seen!.Global.Extensions.Any(r => r.Path.EndsWith("user.ts", StringComparison.Ordinal)) && !seen.Global.Extensions.Any(r => r.Path.EndsWith("project.ts", StringComparison.Ordinal)), "global view without the project");
        Check(seen.Project.Extensions.Any(r => r.Path.EndsWith("project.ts", StringComparison.Ordinal)), "project view");
        // handleConfigCommand passes the built-in extension names: pi config lists them (extensions/index.ts order).
        Names(["builtin:llama.cpp", "builtin:codemode", "builtin:tool-search", "builtin:mcp"],
            seen.Global.Extensions.Where(r => r.Metadata.Source == "builtin").Select(r => r.Path), "built-in extensions listed");
        Equal("{\n  \"extensions\": [\n    \"-extensions/user.ts\"\n  ]\n}", File.ReadAllText(Path.Combine(sandbox.AgentDir, "settings.json")), "selector writes through the settings");
    }

    private static async Task PackageSessionResources()
    {
        // A trusted project's local package contributes its skill to the run.
        using (var sandbox = new Sandbox("pkg-session"))
        {
            var pkg = Path.Combine(sandbox.Root, "skill-pkg");
            Skill(sandbox, Path.Combine(pkg, "skills", "pkg-skill", "SKILL.md"), "pkg-skill", "Skill from a package");
            sandbox.Write(Path.Combine(pkg, "prompts", "pkg-prompt.md"), "Prompt from a package");
            sandbox.Write(Path.Combine(sandbox.Cwd, ".pi", "settings.json"), Jstr(new { packages = new[] { "../../skill-pkg" } }));
            var (code, _, stderr) = await sandbox.Run("-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "hi");
            Equal(0, code, "run; " + stderr);
            var system = sandbox.Requests[0].Json.GetProperty("system")[0].GetProperty("text").GetString()!;
            Check(system.Contains("pkg-skill", StringComparison.Ordinal) && system.Contains("Skill from a package", StringComparison.Ordinal), "package skill in the system prompt");
            var resources = PiResources.Discover(new(sandbox.Cwd, sandbox.AgentDir, sandbox.Home, PiSettings.Load(sandbox.Cwd, sandbox.AgentDir, true), true)
            { Packages = await new PiPackageManager(sandbox.Cwd, sandbox.AgentDir, sandbox.Home, PiSettings.Load(sandbox.Cwd, sandbox.AgentDir, true), _ => null).ResolveAsync(), NoContextFiles = true });
            Check(resources.PromptPaths.Any(p => p.Path.EndsWith("pkg-prompt.md", StringComparison.Ordinal) && p.Scope == "project" && p.Source == "../../skill-pkg"), "package prompt with its metadata");
            Check(resources.SkillPaths.Last().Path.EndsWith("SKILL.md", StringComparison.Ordinal), "package skills after the top-level ones");
            var none = PiResources.Discover(new(sandbox.Cwd, sandbox.AgentDir, sandbox.Home, PiSettings.Load(sandbox.Cwd, sandbox.AgentDir, true), true)
            { Packages = await new PiPackageManager(sandbox.Cwd, sandbox.AgentDir, sandbox.Home, PiSettings.Load(sandbox.Cwd, sandbox.AgentDir, true), _ => null).ResolveAsync(), NoPromptTemplates = true, NoContextFiles = true });
            Check(!none.PromptPaths.Any(), "--no-prompt-templates drops package prompts");
        }
        // An untrusted project's packages are neither installed nor loaded.
        using (var untrusted = new Sandbox("pkg-session-untrusted", trusted: false))
        {
            untrusted.Write(Path.Combine(untrusted.Cwd, ".pi", "settings.json"), Jstr(new { packages = new[] { "git:github.com/user/never-cloned", "npm:never-installed" } }));
            var processes = new FakeProcesses();
            using var stdout = new StringWriter(); using var stderr = new StringWriter();
            var host = untrusted.Host(stdout, stderr, null) with { PackageProcesses = (_, _) => processes };
            Equal(0, await PiCommand.RunAsync(["-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "hi"], host, CancellationToken.None), "untrusted run; " + stderr);
            Equal(0, processes.Runs.Count + processes.Syncs.Count, "no installs for an untrusted project");
            Check(!Directory.Exists(Path.Combine(untrusted.Cwd, ".pi", "git")) && !Directory.Exists(Path.Combine(untrusted.Cwd, ".pi", "npm")), "no project package storage");
        }
        // A trusted project's missing git package is installed for the run, with child output on stderr.
        using (var trusted = new Sandbox("pkg-session-install"))
        {
            trusted.Write(Path.Combine(trusted.Cwd, ".pi", "settings.json"), Jstr(new { packages = new[] { "git:github.com/user/cloned" } }));
            var target = Path.Combine(trusted.Cwd, ".pi", "git", "github.com", "user", "cloned");
            var processes = new FakeProcesses { OnRun = (_, args, _) => { if (args[0] == "clone") Skill(trusted, Path.Combine(target, "skills", "cloned-skill", "SKILL.md"), "cloned-skill"); return Task.CompletedTask; } };
            using var stdout = new StringWriter(); using var stderr = new StringWriter();
            TextWriter? output = null;
            var host = trusted.Host(stdout, stderr, null) with { PackageProcesses = (o, _) => { output = o; return processes; } };
            Equal(0, await PiCommand.RunAsync(["-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "hi"], host, CancellationToken.None), "install run; " + stderr);
            Check(processes.Ran("git", ["clone", "https://github.com/user/cloned", target]), "missing package cloned");
            Check(ReferenceEquals(output, stderr), "print mode keeps stdout clean");
            Check(trusted.Requests[0].Json.GetProperty("system")[0].GetProperty("text").GetString()!.Contains("cloned-skill", StringComparison.Ordinal), "cloned package skill used");
        }
    }
}
