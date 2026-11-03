using System.Net;
using System.Text.Json.Nodes;
using PiSharp.Cli.Packages;
using PiSharp.Cli.Pi;

// Pi 1.5.0 (authored from the v1.5.0 sources, f9a26365568127461c47a07a37dcb42c140a3ac5): core/package-manager.ts (getNpmInstallArgs,
// getGitDependencyInstallArgs and uninstallNpm add --no-audit --no-fund wherever npm gets --legacy-peer-deps), config.ts and
// package-manager-cli.ts (the configured self-update command: getConfiguredSelfUpdateArgv, makeConfiguredSelfUpdateCommand,
// getSelfUpdatePlan, runConfiguredSelfUpdate, readVersionOnPath, printSelfUpdateUnavailable), and package-manager.ts with pi-manifest.ts
// and source-info.ts for package changelogs (resolvePackageChangelogPath, hasManifestResourceFields, changelogPath); the bridge's
// editor.configure keybindings (keybinding-hints.ts) and Component.capturesViewportInput for the cli wave-2 bridge items.
// Expectations are written from the sources by reading, never captured from an upstream run.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync150CliCases() =>
    [
        ("sync150.package-npm-skips-audit-and-fund-advice", PackageNpmNoAuditFund),
        ("sync150.self-update-runs-the-configured-command", SelfUpdateConfiguredCommand),
        ("sync150.package-changelog-path-stays-inside-the-package", PackageChangelogPath),
        ("sync150.bridge-key-hints-survive-reload-and-a-late-node-start", BridgeKeyHintsSurviveReloadAndLateStart),
        ("sync150.bridge-component-captures-viewport-input", BridgeComponentCapturesViewportInput),
    ];

    // npm audit fix would run without --legacy-peer-deps and install a second copy of pi's peers into the package directory, so every npm
    // call that disables peer resolution also skips audit and fund advice; bun and pnpm keep their 1.2.0 arguments.
    private static async Task PackageNpmNoAuditFund()
    {
        using var sandbox = new Sandbox("pkg-no-audit-fund");
        var root = Path.Combine(sandbox.Root, "absent-root");
        Names(["install", "a", "b", "--prefix", root, "--legacy-peer-deps", "--no-audit", "--no-fund"], Manager(sandbox).GetNpmInstallArgs(["a", "b"], root), "npm install");
        Names(["install", "--omit=dev", "--legacy-peer-deps", "--no-audit", "--no-fund"], Manager(sandbox).GetGitDependencyInstallArgs(), "npm git dependencies");
        var bun = Manager(sandbox, J("""{"npmCommand":["bun"]}"""));
        Names(["install", "a", "--cwd", root, "--omit=peer"], bun.GetNpmInstallArgs(["a"], root), "bun install");
        Names(["install", "--omit=dev", "--omit=peer"], bun.GetGitDependencyInstallArgs(), "bun git dependencies");
        var pnpm = Manager(sandbox, J("""{"npmCommand":["pnpm"]}"""));
        Names(["install", "a", "--prefix", root, "--config.auto-install-peers=false", "--config.strict-peer-dependencies=false", "--config.strict-dep-builds=false"],
            pnpm.GetNpmInstallArgs(["a"], root), "pnpm install");

        var processes = new FakeProcesses();
        await Manager(sandbox, processes: processes).InstallAsync("npm:audit-pkg");
        var npmRoot = Path.Combine(sandbox.AgentDir, "npm");
        processes = new FakeProcesses();
        await Manager(sandbox, processes: processes).RemoveAsync("npm:audit-pkg");
        Check(processes.Ran("npm", ["uninstall", "audit-pkg", "--prefix", npmRoot, "--legacy-peer-deps", "--no-audit", "--no-fund"]),
            "npm uninstall: " + string.Join(" | ", processes.Runs.Select(run => string.Join(' ', run.Args))));
        processes = new FakeProcesses();
        await Manager(sandbox, J("""{"npmCommand":["pnpm"]}"""), processes: processes).InstallAsync("npm:audit-pkg");
        processes = new FakeProcesses();
        await Manager(sandbox, J("""{"npmCommand":["pnpm"]}"""), processes: processes).RemoveAsync("npm:audit-pkg");
        Check(processes.Ran("pnpm", ["uninstall", "audit-pkg", "--prefix", npmRoot]), "pnpm uninstall: " + string.Join(" | ", processes.Runs.Select(run => string.Join(' ', run.Args))));
    }

    // package-manager.ts resolvePackageChangelogPath and hasManifestResourceFields (#8790), pi-manifest.ts changelogPath, source-info.ts
    // createSourceInfo: pi.changelogPath (a string), else CHANGELOG.md, counts only as an existing file inside the package; an empty
    // value turns it off. A pi object with only metadata keeps directory auto-discovery.
    private static async Task PackageChangelogPath()
    {
        using var sandbox = new Sandbox("pkg-changelog-path");
        string Package(string name, string? manifest, params string[] files)
        {
            var root = Path.Combine(sandbox.Root, "packages", name);
            sandbox.Write(Path.Combine(root, "extensions", "index.ts"), "export default function() {}");
            sandbox.Write(Path.Combine(root, "package.json"), "{\"name\":\"" + name + "\",\"version\":\"1.0.0\"" + (manifest is null ? "" : ",\"pi\":" + manifest) + "}");
            foreach (var file in files) sandbox.Write(Path.Combine(root, file), "## [1.0.0]\n\n- first\n");
            return root;
        }
        var outside = sandbox.Write(Path.Combine(sandbox.Root, "packages", "outside.md"), "## [1.0.0]\n");
        var roots = new Dictionary<string, string>
        {
            ["default"] = Package("default", null, "CHANGELOG.md"),
            ["custom"] = Package("custom", """{"changelogPath":"./docs/CHANGELOG.md"}""", "docs/CHANGELOG.md", "CHANGELOG.md"),
            ["escape"] = Package("escape", """{"changelogPath":"../outside.md"}""", "CHANGELOG.md"),
            ["off"] = Package("off", """{"changelogPath":""}""", "CHANGELOG.md"),
            ["directory"] = Package("directory", """{"changelogPath":"docs"}""", "docs/notes.md"),
            ["missing"] = Package("missing", """{"changelogPath":"missing.md"}""", "CHANGELOG.md"),
            ["number"] = Package("number", """{"changelogPath":7}""", "CHANGELOG.md"),
            ["none"] = Package("none", null),
        };
        var settings = new JsonObject { ["packages"] = new JsonArray([.. roots.Values.Select(root => (JsonNode)root)]) };
        var resolved = await Manager(sandbox, settings).ResolveAsync();
        PiResolvedResource Extension(string name) => resolved.Extensions.Single(resource =>
            resource.Path == Path.Combine(roots[name], "extensions", "index.ts"));
        Equal(Path.Combine(roots["default"], "CHANGELOG.md"), Extension("default").Metadata.ChangelogPath, "CHANGELOG.md by default");
        Equal("package", Extension("default").Metadata.Origin, "package origin");
        Equal(Path.Combine(roots["custom"], "docs", "CHANGELOG.md"), Extension("custom").Metadata.ChangelogPath, "pi.changelogPath");
        Equal(null, Extension("escape").Metadata.ChangelogPath, "outside the package");
        Equal(null, Extension("off").Metadata.ChangelogPath, "empty turns it off");
        Equal(null, Extension("directory").Metadata.ChangelogPath, "a directory");
        Equal(null, Extension("missing").Metadata.ChangelogPath, "a missing file has no CHANGELOG.md fallback");
        Equal(Path.Combine(roots["number"], "CHANGELOG.md"), Extension("number").Metadata.ChangelogPath, "a non-string changelogPath is ignored");
        Equal(null, Extension("none").Metadata.ChangelogPath, "no changelog");
        Check(File.Exists(outside), "the outside file exists");
        // The metadata-only pi object still auto-discovers extensions/ (every package above loaded its index.ts).
        Equal(roots.Count, resolved.Extensions.Count(resource => resource.Path.EndsWith(Path.Combine("extensions", "index.ts"), StringComparison.Ordinal)),
            "auto-discovered extensions");

        var source = new PiSharp.Cli.Extensions.Pi.PiExtensionSource(Extension("custom").Path, "user", roots["custom"], "package", roots["custom"],
            Extension("custom").Metadata.ChangelogPath);
        Equal(Path.Combine(roots["custom"], "docs", "CHANGELOG.md"), (string?)source.SourceInfo()["changelogPath"], "sourceInfo changelogPath");
        Check(!new PiSharp.Cli.Extensions.Pi.PiExtensionSource("x.ts", "user", "local").SourceInfo().ContainsKey("changelogPath"), "no changelogPath key");
    }

    private const string KeyHintRenderer = """
        import { Text } from "@earendil-works/pi-tui";
        const strip = (text: string) => text.replace(/\x1b\[[0-9;]*m/g, "");
        export default function (pi: any) {
          pi.registerMessageRenderer("note", (_message: any, options: any) =>
            new Text(`${options.keyText("app.tools.expand")} | ${strip(options.keyHint("app.tools.expand", "to expand"))}`, 0, 0));
        }
        """;

    private static async Task<string> RenderNote(PiSharp.Cli.Extensions.Pi.PiExtensionHost host) =>
        (await host.RenderMessageAsync("note", new JsonObject { ["role"] = "custom", ["customType"] = "note", ["content"] = "x" }, 60, false,
            CancellationToken.None))!.Value.Single().TrimEnd();

    // keybinding-hints.ts keyText/keyHint read the mode's keybindings: the bridge keeps the editor.configure keybindings across a
    // reload (agent-session.ts reload builds a fresh runtime), and a Node host that starts after the mode configured the editor gets them.
    private static async Task BridgeKeyHintsSurviveReloadAndLateStart()
    {
        using var sandbox = NodeSandbox("sync150-key-hints-reload");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "hints.ts"), KeyHintRenderer);
        var keybindings = new Dictionary<string, IReadOnlyList<string>> { ["app.tools.expand"] = ["ctrl+e"] };
        await using (var host = await StartHost(sandbox, extension))
        {
            await host.ConfigureEditorAsync(keybindings, [], CancellationToken.None);
            Equal("ctrl+e | ctrl+e to expand", await RenderNote(host), "configured");
            await host.ReloadExtensionsAsync(CancellationToken.None);
            Check(host.Errors.IsEmpty, "reload errors: " + string.Join("; ", host.Errors.Select(error => error.Error)));
            Equal("ctrl+e | ctrl+e to expand", await RenderNote(host), "after a reload");
        }
        await using var late = PiSharp.Cli.Extensions.Pi.PiExtensionHost.CreateWithoutNode(new(sandbox.Cwd, sandbox.AgentDir, "tui", true)
            { GetEnvironment = name => sandbox.Vars.GetValueOrDefault(name) });
        await late.ConfigureEditorAsync(keybindings, [], CancellationToken.None);
        await late.LoadAsync([extension], CancellationToken.None);
        Check(late.Errors.IsEmpty, "load errors: " + string.Join("; ", late.Errors.Select(error => error.Error)));
        Equal("ctrl+e | ctrl+e to expand", await RenderNote(late), "a Node host that starts later");
    }

    // Pi 1.5.0 Component.capturesViewportInput of a Node component (here a getter over its state) reaches the host after a render or an
    // input (component.captures), for the alternate-screen viewport keys.
    private static async Task BridgeComponentCapturesViewportInput()
    {
        using var sandbox = NodeSandbox("sync150-captures");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "menu.ts"), """
            export default function (pi: any) {
              pi.registerShortcut("ctrl+shift+w", { description: "Menu", handler: async (ctx: any) => {
                ctx.ui.setWidget("menu", () => ({
                  open: false,
                  get capturesViewportInput() { return this.open; },
                  render(_width: number) { return [this.open ? "menu open" : "menu closed"]; },
                  handleInput(data: string) { this.open = data === "o"; },
                  invalidate() {},
                }));
              } });
            }
            """);
        await using var host = await StartHost(sandbox, extension);
        var publications = new List<(string Op, System.Text.Json.JsonElement Args)>();
        host.InteractiveUi = (op, args) => { lock (publications) publications.Add((op, args.ValueKind == System.Text.Json.JsonValueKind.Undefined ? default : args.Clone())); };
        var (shortcuts, _) = host.ResolveShortcuts(new Dictionary<string, IReadOnlyList<string>>());
        await host.RunShortcutAsync(shortcuts.Single().Extension, shortcuts.Single().Shortcut, CancellationToken.None);
        await WaitUntil(() => { lock (publications) return publications.Any(item => item.Op == "setWidget"); });
        string id;
        lock (publications) id = publications.First(item => item.Op == "setWidget").Args[1].GetProperty("component").GetString()!;
        Equal("menu closed", (await host.RenderComponentAsync(id, 40, CancellationToken.None)).Single().TrimEnd(), "closed menu");
        Check(!host.ComponentCapturesViewportInput(id), "a closed menu leaves the viewport keys");
        await host.InputComponentAsync(id, "o", CancellationToken.None);
        await WaitUntil(() => host.ComponentCapturesViewportInput(id));
        Equal("menu open", (await host.RenderComponentAsync(id, 40, CancellationToken.None)).Single().TrimEnd(), "open menu");
        Check(host.ComponentCapturesViewportInput(id), "an open menu captures the viewport keys");
        await host.InputComponentAsync(id, "x", CancellationToken.None);
        await WaitUntil(() => !host.ComponentCapturesViewportInput(id));
    }

    // getConfiguredSelfUpdateArgv: PI_SELF_UPDATE_COMMAND (split on whitespace) wins over the global selfUpdateCommand argv; a project
    // file never sets it. The release check is NuGet's PiSharp.Cli (decision 0013), so {packageName} is PiSharp.Cli. The command runs only
    // for a newer release or with --force, without a shell, and the version on PATH decides between "Updated" and the yellow warning.
    private static async Task SelfUpdateConfiguredCommand()
    {
        var current = PiConfig.Version;
        using var sandbox = new Sandbox("self-update-command");
        async Task<(int Code, string Out, string Err, SelfUpdateProcesses Processes, NuGet Feed)> Update(SelfUpdateProcesses? processes = null, NuGet? feed = null,
            params string[] args)
        {
            processes ??= new SelfUpdateProcesses(); feed ??= new NuGet(current, "999.0.0");
            using var stdout = new StringWriter { NewLine = "\n" }; using var stderr = new StringWriter { NewLine = "\n" };
            var host = sandbox.Host(stdout, stderr, null) with
            {
                PackageProcesses = (_, _) => processes, VersionCheckHttp = new HttpMessageInvoker(feed), NuGetBaseUrl = "https://nuget.test/v3-flatcontainer",
            };
            var code = await PiCommand.RunAsync(["update", .. (args.Length == 0 ? ["--self"] : args)], host, CancellationToken.None);
            return (code, stdout.ToString(), stderr.ToString(), processes, feed);
        }

        var (code, stdout, stderr, processes, feed) = await Update();
        Equal(1, code, "no command exit");
        Equal("error: pisharp cannot self-update this installation.\nUpdate pisharp using the package manager, wrapper, or source checkout that provides this installation.\n" +
            "To make `pisharp update` run your own update command, set PI_SELF_UPDATE_COMMAND or the selfUpdateCommand setting.\n", stderr, "unavailable");
        Check(feed.Requests == 0 && processes.Runs.Count == 0, "no release check without a command");

        // A project file cannot set the command.
        sandbox.Write(Path.Combine(sandbox.Cwd, ".pi", "settings.json"), """{"selfUpdateCommand":["project-updater"]}""");
        (code, _, stderr, processes, _) = await Update();
        Check(code == 1 && stderr.StartsWith("error: pisharp cannot self-update", StringComparison.Ordinal) && processes.Runs.Count == 0, "project setting ignored: " + stderr);

        sandbox.Vars["PI_SELF_UPDATE_COMMAND"] = "  dotnet tool update\t-g {packageName} --version {version}  ";
        (code, stdout, stderr, processes, feed) = await Update(new SelfUpdateProcesses { VersionOnPath = "999.0.0" });
        Equal(0, code, "update exit; " + stderr);
        Equal("Updating pisharp with dotnet tool update -g PiSharp.Cli --version 999.0.0...\nUpdated pisharp from " + current + " to 999.0.0\n", stdout, "update output");
        Equal("dotnet tool update -g PiSharp.Cli --version 999.0.0", string.Join(" ", processes.Runs.Single()), "argv");
        Equal("pisharp --version|PI_PACKAGE_DIR=", string.Join("|", processes.Captures), "version on PATH without PI_PACKAGE_DIR");
        Equal("GET https://nuget.test/v3-flatcontainer/pisharp.cli/index.json", feed.Last, "NuGet index request");

        (code, stdout, _, processes, _) = await Update(feed: new NuGet(current));
        Check(code == 0 && stdout == $"pisharp is already up to date (v{current})\n" && processes.Runs.Count == 0, "up to date: " + stdout);
        (code, stdout, _, processes, _) = await Update(new SelfUpdateProcesses { VersionOnPath = current }, new NuGet(current), "--self", "--force");
        Check(code == 0 && processes.Runs.Count == 1 && stdout.EndsWith($"Updated pisharp from {current} to {current}\n", StringComparison.Ordinal), "--force: " + stdout);

        // The global argv setting, with an argument that holds a space; the variable still wins while it is set.
        sandbox.Write(Path.Combine(sandbox.AgentDir, "settings.json"), """{"selfUpdateCommand":["my updater","{installSpec}","{packageName}@{version}"]}""");
        (_, _, _, processes, _) = await Update(new SelfUpdateProcesses { VersionOnPath = "999.0.0" });
        Equal("dotnet", processes.Runs.Single()[0], "variable over setting");
        sandbox.Vars.Remove("PI_SELF_UPDATE_COMMAND");
        (code, stdout, _, processes, _) = await Update(new SelfUpdateProcesses { VersionOnPath = "999.0.0" });
        Equal(0, code, "setting exit");
        Equal("my updater|PiSharp.Cli@999.0.0|PiSharp.Cli@999.0.0", string.Join("|", processes.Runs.Single()), "setting argv");
        Check(stdout.StartsWith("Updating pisharp with \"my updater\" PiSharp.Cli@999.0.0 PiSharp.Cli@999.0.0...\n", StringComparison.Ordinal), "quoted display: " + stdout);

        // Exit 0 does not prove an update.
        (code, stdout, _, _, _) = await Update(new SelfUpdateProcesses { VersionOnPath = "1.0.0" });
        Check(code == 0 && stdout.EndsWith("Ran \"my updater\" PiSharp.Cli@999.0.0 PiSharp.Cli@999.0.0, but `pisharp --version` reports 1.0.0, not 999.0.0.\n",
            StringComparison.Ordinal), "stale version: " + stdout);
        (_, stdout, _, _, _) = await Update(new SelfUpdateProcesses());
        Check(stdout.EndsWith("but `pisharp --version` reports no version, not 999.0.0.\n", StringComparison.Ordinal), "no version on PATH: " + stdout);

        // Failures.
        (code, _, stderr, _, _) = await Update(new SelfUpdateProcesses { ExitCode = 3 });
        Equal(1, code, "failed command exit");
        Equal("Error: \"my updater\" PiSharp.Cli@999.0.0 PiSharp.Cli@999.0.0 exited with code 3\n", stderr, "failed command");
        (code, _, stderr, _, _) = await Update(new SelfUpdateProcesses { SpawnFails = true });
        Check(code == 1 && stderr == "Error: spawn my updater ENOENT\n", "spawn failure: " + stderr);
        (code, _, stderr, processes, _) = await Update(feed: new NuGet { Status = HttpStatusCode.NotFound });
        Check(code == 1 && stderr == "Error: Could not determine latest pisharp version.\n" && processes.Runs.Count == 0, "no release: " + stderr);
        (code, _, stderr, _, feed) = await Update(feed: new NuGet { Throws = true });
        Check(code == 1 && stderr == "Error: Could not determine latest pisharp version: boom\n" && feed.Requests == 3, "release check error (retried): " + stderr);
        sandbox.Vars["PI_OFFLINE"] = "1";
        (code, _, stderr, _, feed) = await Update();
        Check(code == 1 && stderr == "Error: Could not determine latest pisharp version.\n" && feed.Requests == 0, "offline: " + stderr);
        sandbox.Vars.Remove("PI_OFFLINE");

        // config.ts helpers.
        Equal(null, PiSelfUpdate.GetConfiguredSelfUpdateArgv(" \t ", new JsonArray()), "blank variable, empty setting");
        Equal(null, PiSelfUpdate.GetConfiguredSelfUpdateArgv(null, new JsonArray(1, "x")), "non-string setting");
        Names(["a", "b"], PiSelfUpdate.GetConfiguredSelfUpdateArgv(null, new JsonArray("a", "b"))!, "setting");
        Equal("{other} PiSharp.Cli", PiSelfUpdate.MakeConfiguredSelfUpdateCommand(["{other}", "{packageName}"], "PiSharp.Cli", "1.0.0").Display, "unknown placeholders stay");
        Equal(new InstallChange("removed"), PiSelfUpdate.DetectInstallChange(Path.Combine(sandbox.Root, "gone.dll"), current), "removed");
        var cli = typeof(PiConfig).Assembly.Location;
        Equal(null, PiSelfUpdate.DetectInstallChange(cli, current), "unchanged installation");
        Equal(new InstallChange("updated", current), PiSelfUpdate.DetectInstallChange(cli, "0.0.1"), "a different version on disk");
        Equal(null, PiSelfUpdate.DetectInstallChange("", current), "single-file build");
    }

    /// <summary>The self-update command's process seam: records argv, exits with <see cref="ExitCode"/>, and answers
    /// <c>pisharp --version</c> with <see cref="VersionOnPath"/> (null: the command fails).</summary>
    private sealed class SelfUpdateProcesses : PiPackageProcesses
    {
        public readonly List<string[]> Runs = [];
        public readonly List<string> Captures = [];
        public int ExitCode { get; init; }
        public bool SpawnFails { get; init; }
        public string? VersionOnPath { get; init; }
        internal override Task<int> RunInheritedAsync(string command, IReadOnlyList<string> args, string? cwd, CancellationToken cancellationToken)
        {
            Runs.Add([command, .. args]);
            return SpawnFails ? Task.FromException<int>(new PiPackageException($"spawn {command} ENOENT")) : Task.FromResult(ExitCode);
        }
        internal override Task<string> CaptureAsync(string command, IReadOnlyList<string> args, string? cwd, int? timeoutMs,
            IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken)
        {
            Captures.Add(command + " " + string.Join(" ", args));
            Captures.Add(string.Join(",", (environment ?? new Dictionary<string, string>()).Select(pair => pair.Key + "=" + pair.Value)));
            return VersionOnPath is null ? Task.FromException<string>(new PiPackageException($"{command} --version failed with code 1"))
                : Task.FromResult(VersionOnPath);
        }
    }

    /// <summary>A NuGet flat container that lists <c>versions</c> for PiSharp.Cli.</summary>
    private sealed class NuGet(params string[] versions) : HttpMessageHandler
    {
        public int Requests;
        public string? Last;
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public bool Throws { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests); Last = request.Method + " " + request.RequestUri!.AbsoluteUri;
            if (Throws) throw new HttpRequestException("boom");
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { versions })) });
        }
    }
}
