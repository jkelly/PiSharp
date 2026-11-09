using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Extensions.Execution;
using PiSharp.Cli.Packages;
using PiSharp.Cli.Pi;
using PiSharp.Extensions;
using PiSharp.Extensions.Facade.Execution;
using PiSharp.Extensions.Runtime;
using PiSharp.Tools.Processes;

// Pi 1.5.0 (authored from the v1.5.0 sources, f9a26365568127461c47a07a37dcb42c140a3ac5): core/exec.ts (execCommand: maxBuffer and
// truncated, SIGKILL after SIGTERM, UTF-8 decoding across chunks, spawn errors in stderr) through the Node bridge's pi.exec and native
// pi.exec (NativeExtensionExecHost over the platform's separated process runner);
// core/package-manager.ts (getNpmInstallArgs and uninstallNpm with a real --prefix, resolvePackageSources: install failures,
// temporary npm and git refresh; cleanAndInstallGitDependencies after a failed clean; reportUnchangedNpmUpdates), core/resource-loader.ts
// (describePackageInstallFailure, formatPackageInstallWarning), main.ts (--help and --list-models report the runtime diagnostics) and
// package-manager-cli.ts (progress events, pi config warnings); the minimatch 11.0.3 upgrade (b4c2e92ac: brace-expansion 5 limits and
// nonrecursive-glob-match) as PiMinimatch ports its protection, from the brace-expansion 5.0.12 and minimatch 11.0.3 sources.
// Expectations are written from the sources by reading, never captured from an upstream run.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync150PackageCases() =>
    [
        ("gap.package-update-summary-offline-empty-and-git", PackageUpdateSummary),
        ("sync150.bridge-exec-max-buffer-utf8-and-spawn-errors", BridgeExecLimits),
        ("sync150.native-exec-max-buffer-truncates-stops-and-spawn-errors", NativeExecLimits),
        ("sync150.package-npm-prefix-resolves-a-linked-agent-dir", PackageNpmRealPrefix),
        ("sync150.package-temporary-npm-refreshes-to-latest", PackageTemporaryNpmRefresh),
        ("sync150.package-temporary-git-ref-refreshes-unless-the-commit-is-checked-out", PackageTemporaryGitRefresh),
        ("sync150.package-git-clean-failure-installs-and-keeps-the-marker", PackageGitCleanFailure),
        ("sync150.package-update-warns-when-npm-leaves-the-version-unchanged", PackageUnchangedNpmUpdate),
        ("sync150.package-install-failure-is-a-startup-warning", PackageInstallFailureAtStartup),
        ("sync150.package-install-failure-warnings-follow-reload", PackageInstallFailureOnReload),
        ("sync150.glob-brace-expansion-is-bounded", Sync(GlobBraceAmplification)),
        ("sync150.glob-amplified-wildcards-and-globstars-answer-at-once", Sync(GlobWildcardAmplification)),
        ("sync150.trust-saved-decision-reaches-extensions", SavedTrustReachesExtensions),
        ("sync150.package-local-identity-ignores-case-on-windows", PackageLocalIdentityCasing),
        ("sync150.bridge-skills-match-other-casing-on-windows", BridgeSkillsCasing),
    ];

    // package-manager.ts getSourceMatchKeyForInput, getSourceMatchKeyForSettings and getPackageIdentity (1be312b46): a local package
    // matches with other casing on Windows (remove c:\pkg removes the configured C:\pkg) and stays case-sensitive elsewhere; resolved
    // resources are deduplicated by getResourcePathKey.
    private static async Task PackageLocalIdentityCasing()
    {
        using var sandbox = new Sandbox("pkg-casing");
        var windows = OperatingSystem.IsWindows();
        var package = Path.Combine(sandbox.Root, "LocalPkg");
        sandbox.Write(Path.Combine(package, "extensions", "index.ts"), "export default function () {}");
        var settingsPath = sandbox.Write(Path.Combine(sandbox.AgentDir, "settings.json"), Jstr(new { packages = new[] { package } }));
        var manager = Manager(sandbox, J(File.ReadAllText(settingsPath)));
        Equal(windows, manager.GetPackageIdentity(package, "user") == manager.GetPackageIdentity(Path.Combine(sandbox.Root, "localpkg"), "user"), "identity");
        var resolved = await Manager(sandbox, J(Jstr(new { packages = new[] { package }, extensions = new[] { Path.Combine(sandbox.Root, "localpkg", "extensions", "index.ts") } }))).ResolveAsync();
        // Elsewhere the result depends on the filesystem's own case sensitivity.
        if (windows) Equal(1, resolved.Extensions.Length, "one extension file under two casings: " + string.Join(", ", resolved.Extensions.Select(e => e.Path)));
        var (code, _, stderr) = await sandbox.Run("remove", Path.Combine(sandbox.Root, "localpkg"));
        Equal(windows ? 0 : 1, code, "remove with other casing; " + stderr);
        Equal(windows ? 0 : 1, JsonNode.Parse(File.ReadAllText(settingsPath))!["packages"]!.AsArray().Count, "the configured package is removed on Windows only");
    }

    // skills.ts loadSkills with source-info.ts getResourcePathKey and utils/paths.ts isPathWithin (1be312b46), through the bridge's
    // pi-coding-agent module: with defaults off, a skill directory under the agent's skills folder written with other casing is still
    // the user's on Windows, and the same skill listed under two casings loads once, without a name collision.
    private static async Task BridgeSkillsCasing()
    {
        using var sandbox = NodeSandbox("skills-casing");
        // The built-in compatibility modules (the bridge's ESM copies); the installed Pi runtime is the Pi 1.2.0 release.
        sandbox.Vars["PI_OFFLINE"] = "1";
        sandbox.Vars["PISHARP_PI_RUNTIME_DIR"] = Path.Combine(sandbox.Root, "no-runtime");
        var skill = Path.Combine(sandbox.AgentDir, "skills", "probe");
        sandbox.Write(Path.Combine(skill, "SKILL.md"), "---\nname: probe\ndescription: Casing probe.\n---\nBody\n");
        var other = Path.Combine(sandbox.AgentDir.ToUpperInvariant(), "SKILLS", "probe");
        var extension = sandbox.Write("skills-probe.ts", $$"""
            import { appendFileSync } from "node:fs";
            import { loadSkills } from "@earendil-works/pi-coding-agent";
            export default function (pi: any) {
              pi.on("session_start", () => {
                const result = loadSkills({ cwd: process.cwd(), agentDir: {{Jstr(sandbox.AgentDir)}}, skillPaths: [{{Jstr(other)}}, {{Jstr(skill)}}], includeDefaults: false });
                const collisions = result.diagnostics.filter((d: any) => d.type === "collision").length;
                appendFileSync(process.cwd() + "/probe.log", JSON.stringify(["skills", result.skills.length, result.skills[0]?.sourceInfo?.scope, collisions]) + "\n");
              });
            }
            """);
        var (code, _, stderr) = await sandbox.Run([.. new[] { "-p", "--no-session", "-e", extension }, .. Model, "hi"]);
        Equal(0, code, "exit; " + stderr);
        var record = LogRecords(sandbox, "skills").Single().ToJsonString();
        // Elsewhere the result depends on the filesystem's own case sensitivity.
        if (OperatingSystem.IsWindows()) Equal("""["skills",1,"user",0]""", record, "one user skill under two casings");
    }

    // main.ts createRuntime and project-trust.ts (31ade0e0d, test/project-trust-saved-decision.test.ts): for a project without
    // trust-requiring resources, ctx.isProjectTrusted() reports a saved "Do not trust", and auto-trust when nothing is saved.
    private static async Task SavedTrustReachesExtensions()
    {
        using var sandbox = NodeSandbox("trust-saved", trusted: false);
        sandbox.Write(Path.Combine(sandbox.Cwd, "AGENTS.md"), "# untrusted\n");
        if (ProjectTrustStore.HasTrustRequiringProjectResources(sandbox.Cwd, sandbox.Home))
            throw new SkipException("ANCESTOR_SKILLS", "An ancestor of the temporary directory holds .agents/skills, so the project requires trust here.");
        var extension = sandbox.Write("trust-probe.ts", """
            import { appendFileSync } from "node:fs";
            export default function (pi: any) {
              pi.on("session_start", (_event: any, ctx: any) => appendFileSync(process.cwd() + "/probe.log", JSON.stringify(["trusted", ctx.isProjectTrusted()]) + "\n"));
            }
            """);
        async Task<bool> Probe()
        {
            File.Delete(Path.Combine(sandbox.Cwd, "probe.log"));
            var (code, _, stderr) = await sandbox.Run([.. new[] { "-p", "--no-session", "-e", extension }, .. Model, "hi"]);
            Equal(0, code, "exit; " + stderr);
            return LogRecords(sandbox, "trusted").Single()[1]!.GetValue<bool>();
        }
        Equal(true, await Probe(), "no decision: auto-trusted");
        new ProjectTrustStore(sandbox.AgentDir, sandbox.Home).Set(sandbox.Cwd, false);
        Equal(false, await Probe(), "a saved Do not trust reaches the extension");
    }

    // brace-expansion 5 (minimatch 11): an amplified brace pattern is truncated instead of expanded without bound: at most 100,000
    // results and 4,000,000 characters, chained sets combined without recursion, nesting followed 1,000 levels deep.
    private static void GlobBraceAmplification()
    {
        var chained = PiMinimatch.BraceExpand(string.Concat(Enumerable.Repeat("{a,b}", 20)));
        Equal(100_000, chained.Count, "2^20 expansions capped");
        Equal(new string('a', 20), chained[0], "first expansion");
        Equal(100_000, chained.Distinct().Count(), "distinct expansions");
        var lengthy = PiMinimatch.BraceExpand(string.Concat(Enumerable.Repeat("{a,b}", 1500)));
        Check(lengthy.Count > 0 && lengthy.Sum(item => (long)item.Length) <= 4_000_000 && lengthy.All(item => item.Length == 1500), "character cap: " + lengthy.Count);
        var sequence = PiMinimatch.BraceExpand("{1..2000000}");
        Equal(100_000, sequence.Count, "sequence capped"); Equal("100000", sequence[^1], "last sequence item");
        Equal(1, PiMinimatch.BraceExpand("{1..3..99999999999}").Count, "a step wider than the range");
        var nested = PiMinimatch.BraceExpand(string.Concat(Enumerable.Repeat("{a,", 5000)) + "b" + new string('}', 5000));
        Check(nested.Count <= 1_002 && nested[0] == "a", "nesting past 1,000 levels stays literal: " + nested.Count);
        // Ordinary expansions are unchanged.
        Names(["a1x", "a1y", "a2x", "a2y", "bx", "by"], PiMinimatch.BraceExpand("{a{1,2},b}{x,y}"), "nested and chained sets");
        Names(["x{y}z1", "x{y}z2"], PiMinimatch.BraceExpand("x{y}z{1,2}"), "a literal set, then a list");
        Names(["f01", "f03", "f05"], PiMinimatch.BraceExpand("f{01..05..2}"), "padded sequence with a step");
        Check(PiMinimatch.Match(new string('a', 20), string.Concat(Enumerable.Repeat("{a,b}", 20))), "a capped pattern matches its first expansion");
    }

    // nonrecursive-glob-match and minimatch 11's globstar walk: a portion with many stars is matched without backtracking and a pattern
    // with many ** without a recursive walk per split, so pathological patterns answer at once; ordinary results are unchanged.
    private static void GlobWildcardAmplification()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var stars = string.Concat(Enumerable.Repeat("*a", 40));
        Check(!PiMinimatch.Match(new string('a', 400), stars + "*b"), "many stars, no final literal");
        Check(PiMinimatch.Match(new string('a', 400) + "b", stars + "*b"), "many stars and the final literal");
        Check(!PiMinimatch.Match(new string('a', 400), stars + "*B", ignoreCase: true), "many stars, ignoring case");
        var path = string.Join('/', Enumerable.Repeat("a", 60));
        var globstars = string.Concat(Enumerable.Repeat("**/a/", 30));
        Check(!PiMinimatch.Match(path, globstars + "b"), "many globstars, no final segment");
        Check(PiMinimatch.Match(path + "/b", globstars + "b"), "many globstars and the final segment");
        Check(!PiMinimatch.Match(path + "/.x/b", globstars + "b"), "a ** never swallows a dot segment");
        Check(watch.Elapsed < TimeSpan.FromSeconds(20), "pathological patterns answer at once: " + watch.Elapsed);
        Check(!PiMinimatch.Match(".a", "*") && PiMinimatch.Match(".a", ".*") && PiMinimatch.Match(".a", "[.]a") && !PiMinimatch.Match(".a", "?a"), "dot rule");
        Check(PiMinimatch.Match("X.TS", "*.[t]s", ignoreCase: true) && !PiMinimatch.Match("X.TS", "*.[t]s") && PiMinimatch.Match("ab1", "a?[[:digit:]]"), "classes");
        Check(PiMinimatch.Match("a/b/", "a/**/*") && PiMinimatch.Match("x/y/z.ts", "**/*.ts") && !PiMinimatch.Match("x/.y/z.ts", "**/*.ts"), "globstar results");
    }

    private const string ExecExtension = """
        import { appendFileSync } from "node:fs";
        const log = (...items: unknown[]) => appendFileSync(process.cwd() + "/probe.log", JSON.stringify(items) + "\n");
        export default function (pi: any) {
          pi.registerTool({
            name: "execs", label: "Execs", description: "Runs pi.exec", parameters: { type: "object", properties: {} },
            async execute() {
              const run = (script: string, options?: any) => pi.exec(process.execPath, ["-e", script], options);
              const rejected: string[] = [];
              for (const maxBuffer of [-1, 0.5, Number.NaN, Number.POSITIVE_INFINITY]) {
                try { await pi.exec("pi-sync150-missing-command", [], { maxBuffer }); rejected.push("resolved"); }
                catch (error: any) { rejected.push(error?.name ?? String(error)); }
              }
              const records: any = {
                rejected,
                missing: await pi.exec("pi-sync150-missing-command", []),
                silent: await run("process.exit(1)"),
                zero: await run("process.stdout.write('x')", { maxBuffer: 0 }),
                exact: await run("process.stdout.write('a'.repeat(1000))", { maxBuffer: 1000 }),
                cut: await run("process.stdout.write('a€🙂')", { maxBuffer: 5 }),
                cutStderr: await run("process.stderr.write('€')", { maxBuffer: 1 }),
                split: await run("process.stdout.write(Buffer.from([0xe2, 0x82])); setTimeout(() => process.stdout.write(Buffer.from([0xac])), 200)"),
                partialAtEof: await run("process.stdout.write(Buffer.from([0xe2]))"),
              };
              const flood = await run("process.stderr.write('e'.repeat(600)); process.stdout.write('o'.repeat(600)); setInterval(() => process.stdout.write('x'.repeat(1000)), 10)", { maxBuffer: 1000 });
              records.flood = { bytes: Buffer.byteLength(flood.stdout) + Buffer.byteLength(flood.stderr), code: flood.code, killed: flood.killed, truncated: flood.truncated };
              if (process.platform !== "win32") {
                const began = Date.now();
                const stubborn = await run("process.on('SIGTERM', () => {}); process.stdout.write('xx'); setInterval(() => {}, 1000)", { maxBuffer: 1 });
                records.stubborn = { ...stubborn, elapsed: Date.now() - began };
              }
              log("exec", records);
              return { content: [{ type: "text", text: "execs ok" }] };
            },
          });
        }
        """;

    // exec.ts execCommand through the bridge's pi.exec: an invalid maxBuffer rejects with a RangeError before anything runs; a command
    // that cannot start resolves with its error in stderr and code 1; the combined stdout/stderr cap stops the command and reports
    // truncated, killed and a nonzero code; a cut drops an incomplete UTF-8 suffix, while a character split across chunks decodes whole
    // and a partial character at a normal EOF flushes as U+FFFD; a child that ignores SIGTERM is killed with SIGKILL after five seconds.
    private static async Task BridgeExecLimits()
    {
        using var sandbox = NodeSandbox("exec-limits");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "execs.ts"), ExecExtension);
        sandbox.Respond = (_, index) => index == 0 ? AnthropicToolCall("execs", new { }) : AnthropicText("done");
        var (code, stdout, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", extension, "run the execs"]);
        Equal(0, code, "exit; " + stderr);
        Equal("done", stdout.Trim(), "final text; stderr " + stderr + "; log " + string.Join(" | ", LogLines(sandbox)));
        var records = LogRecords(sandbox, "exec").Single()[1]!.AsObject();
        Equal("""["RangeError","RangeError","RangeError","RangeError"]""", records["rejected"]!.ToJsonString(), "invalid maxBuffer rejects");
        var missing = records["missing"]!;
        Check(missing["stderr"]!.GetValue<string>().Contains("ENOENT", StringComparison.Ordinal), "spawn error in stderr: " + missing.ToJsonString());
        Equal("""{"stdout":"","code":1,"killed":false,"truncated":false}""", Without(missing, "stderr"), "spawn error result");
        Equal("""{"stdout":"","stderr":"","code":1,"killed":false,"truncated":false}""", records["silent"]!.ToJsonString(), "a silent failure keeps stderr empty");
        Equal("""{"stdout":"","stderr":"","code":1,"killed":true,"truncated":true}""", records["zero"]!.ToJsonString(), "maxBuffer 0 truncates everything");
        Equal("{\"stdout\":\"" + new string('a', 1000) + "\",\"stderr\":\"\",\"code\":0,\"killed\":false,\"truncated\":false}",
            records["exact"]!.ToJsonString(), "output up to maxBuffer is unchanged");
        Equal("a€", records["cut"]!["stdout"]!.GetValue<string>(), "a cut keeps whole characters only");
        Equal("""{"code":1,"killed":true,"truncated":true}""", Without(records["cut"]!, "stdout", "stderr"), "cut result");
        Equal("""{"stdout":"","stderr":"","code":1,"killed":true,"truncated":true}""", records["cutStderr"]!.ToJsonString(), "a stderr cut inside a character");
        Equal("€", records["split"]!["stdout"]!.GetValue<string>(), "a character split across chunks");
        Equal(false, records["split"]!["truncated"]!.GetValue<bool>(), "split is not truncated");
        Equal("�", records["partialAtEof"]!["stdout"]!.GetValue<string>(), "a partial character at a normal EOF");
        Equal("""{"bytes":1000,"code":1,"killed":true,"truncated":true}""", records["flood"]!.ToJsonString(), "the combined cap stops a writing command");
        if (!OperatingSystem.IsWindows())
        {
            var stubborn = records["stubborn"]!;
            Equal("""{"stdout":"x","stderr":"","killed":true,"truncated":true}""", Without(stubborn, "code", "elapsed"), "SIGTERM-ignoring child");
            Check(stubborn["code"]!.GetValue<int>() != 0 && stubborn["elapsed"]!.GetValue<double>() >= 4500, "SIGKILL after the delay: " + stubborn.ToJsonString());
        }
    }

    private sealed class ExecOwner : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // exec.ts execCommand through native pi.exec: maxBuffer is validated before admission, and the default is 256 MiB; output over the
    // limit stops the real child and reads truncated, killed and nonzero, keeping whole UTF-8 characters; an executable that cannot start
    // resolves with "spawn <command> ENOENT" in stderr and code 1 instead of throwing.
    private static async Task NativeExecLimits()
    {
        RequireNode();
        var node = Tool("node", "-p process.execPath") ?? throw new SkipException("NO_NODE", "Node.js is not on PATH.");
        using var sandbox = new Sandbox("native-exec");
        var spill = Path.Combine(sandbox.Root, "spill"); Directory.CreateDirectory(spill);
        var environment = OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("SystemRoot") is { } root
            ? ImmutableDictionary<string, string>.Empty.Add("SystemRoot", root) : ImmutableDictionary<string, string>.Empty;
        var requests = new List<ProcessRequest>();
        var registry = new ExtensionRegistry(); IExtensionExecFacade? facade = null;
        var install = new NativeExtensionExecInstallation(registry, new LimitObservingRunner(OfflineSessionProfile.PiProcessRunner(), requests),
            (invocation, _) => ValueTask.FromResult(new ProcessRequest(invocation.Command == "node" ? node : Path.Combine(sandbox.Root, "missing-" + invocation.Command),
                invocation.Arguments, invocation.WorkingDirectory, environment, Path.Combine(spill, Guid.NewGuid().ToString("N") + ".spill"), invocation.TimeoutSeconds)),
            sandbox.Cwd, (_, value) => facade = value);
        try
        {
            await registry.ActivateAsync("sync150-native-exec", install.Decorate(registry, new ExecOwner()));
            Task<ExtensionExecResult> Run(string script, long? maxBuffer = null) =>
                facade!.ExecAsync("node", ["-e", script], new() { MaxBuffer = maxBuffer, TimeoutMilliseconds = 60_000 });
            foreach (var invalid in new long[] { -1, 0x3FFFFFDF + 1L })
            {
                var refused = await Rejects<ArgumentOutOfRangeException>(() => Run("", invalid), "maxBuffer " + invalid);
                Check(refused.Message.StartsWith("maxBuffer must be an integer between 0 and 1073741791 bytes", StringComparison.Ordinal), refused.Message);
            }
            Equal(0, requests.Count, "an invalid maxBuffer never reaches the runner");
            var plain = await Run("process.stdout.write('a'.repeat(1000))");
            Equal(256L * 1024 * 1024, requests[^1].CaptureLimitBytes, "default maxBuffer");
            Equal((new string('a', 1000), "", 0, false, false), (plain.Stdout, plain.Stderr, plain.Code, plain.Killed, plain.Truncated), "output within the default");
            var exact = await Run("process.stdout.write('a'.repeat(1000))", 1000);
            Equal((1000, false, false, 0), (exact.Stdout.Length, exact.Truncated, exact.Killed, exact.Code), "output up to maxBuffer is unchanged");
            var flood = await Run("process.stderr.write('e'.repeat(600)); process.stdout.write('o'.repeat(600)); setInterval(() => process.stdout.write('x'.repeat(1000)), 10)", 1000);
            Equal((1000, true, true), (flood.Stdout.Length + flood.Stderr.Length, flood.Truncated, flood.Killed), "the combined cap stops a writing command");
            Check(flood.Code != 0, "a truncated result is never a success: " + flood.Code);
            var cut = await Run("process.stdout.write('a€🙂')", 5);
            Equal(("a€", true, true), (cut.Stdout, cut.Truncated, cut.Killed), "a cut keeps whole characters only");
            Check(cut.Code != 0, "cut code " + cut.Code);
            var zero = await Run("process.stdout.write('x')", 0);
            Equal(("", "", true, true), (zero.Stdout, zero.Stderr, zero.Truncated, zero.Killed), "maxBuffer 0 truncates everything");
            Check(zero.Code != 0, "zero code " + zero.Code);
            var missing = await facade!.ExecAsync("pi-sync150-missing", []);
            Equal(("", "spawn pi-sync150-missing ENOENT", 1, false, false), (missing.Stdout, missing.Stderr, missing.Code, missing.Killed, missing.Truncated),
                "an executable that cannot start");
        }
        finally { await registry.DisposeAsync(); }
    }

    private static async Task<T> Rejects<T>(Func<Task> run, string what) where T : Exception
    {
        try { await run(); } catch (T error) { return error; }
        throw new InvalidOperationException(what + ": expected " + typeof(T).Name + ".");
    }

    private sealed class LimitObservingRunner(ISeparatedProcessRunner inner, List<ProcessRequest> requests) : ISeparatedProcessRunner
    {
        public ValueTask<SeparatedProcessRunResult> RunSeparatedAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        { lock (requests) requests.Add(request); return inner.RunSeparatedAsync(request, cancellationToken); }
        public ValueTask<ProcessRunResult> RunAsync(ProcessRequest request, ProcessOutputCallback? onUpdate = null, CancellationToken cancellationToken = default)
            => inner.RunAsync(request, onUpdate, cancellationToken);
    }

    private static string Without(JsonNode node, params string[] names)
    {
        var copy = node.DeepClone().AsObject();
        foreach (var name in names) copy.Remove(name);
        return copy.ToJsonString();
    }

    // getNpmInstallArgs and uninstallNpm: npm writes broken nested package-lock.json paths through a symlinked --prefix, so an agent
    // directory reached through a link installs and uninstalls with the resolved root.
    private static async Task PackageNpmRealPrefix()
    {
        using var sandbox = new Sandbox("pkg-real-prefix");
        var linked = Path.Combine(sandbox.Root, "linked-agent");
        if (!Link(linked, sandbox.AgentDir)) throw new SkipException("NO_LINK", "A directory link cannot be created here.");
        var realRoot = Path.Combine(sandbox.AgentDir, "npm");
        PiPackageManager Linked(FakeProcesses processes) =>
            new(sandbox.Cwd, linked, sandbox.Home, PiSettings.FromObjects([], [], true), _ => null, processes);
        var processes = new FakeProcesses();
        await Linked(processes).InstallAsync("npm:real-pkg");
        Check(processes.Ran("npm", ["install", "real-pkg", "--prefix", realRoot, "--legacy-peer-deps", "--no-audit", "--no-fund"]), "install prefix: " + string.Join(" | ", processes.Runs.Select(run => string.Join(' ', run.Args))));
        processes = new FakeProcesses();
        await Linked(processes).RemoveAsync("npm:real-pkg");
        Check(processes.Ran("npm", ["uninstall", "real-pkg", "--prefix", realRoot, "--legacy-peer-deps", "--no-audit", "--no-fund"]), "uninstall prefix: " + string.Join(" | ", processes.Runs.Select(run => string.Join(' ', run.Args))));
        // A root that does not exist yet is passed as it is.
        Names(["install", "x", "--prefix", Path.Combine(linked, "absent"), "--legacy-peer-deps", "--no-audit", "--no-fund"], Linked(new()).GetNpmInstallArgs(["x"], Path.Combine(linked, "absent")), "missing root");
    }

    // resolvePackageSources: an installed unpinned temporary (-e) npm source with a newer registry version installs name@latest (a bare
    // name keeps the saved range); a failed refresh keeps the cached install; pinned sources and offline mode never look the version up.
    private static async Task PackageTemporaryNpmRefresh()
    {
        using var sandbox = new Sandbox("pkg-temp-npm");
        var installed = Manager(sandbox).GetNpmInstallPath((PiNpmSource)PiPackageSource.Parse("npm:tmp-pkg"), "temporary");
        var root = Path.GetDirectoryName(Path.GetDirectoryName(installed))!;
        sandbox.Write(Path.Combine(installed, "package.json"), Jstr(new { name = "tmp-pkg", version = "1.0.0" }));
        sandbox.Write(Path.Combine(installed, "extensions", "index.ts"), "export default function () {}");
        var processes = new FakeProcesses { OnCapture = (_, args, _) => args[0] == "view" ? "\"2.0.0\"" : null };
        var resolved = await Manager(sandbox, processes: processes).ResolveExtensionSourcesAsync(["npm:tmp-pkg"], temporary: true);
        Names(["view", "tmp-pkg", "version", "--json"], processes.Captures.Single().Args, "registry lookup");
        Check(processes.Ran("npm", ["install", "tmp-pkg@latest", "--prefix", root, "--legacy-peer-deps", "--no-audit", "--no-fund"]), "refresh to latest: " + string.Join(" | ", processes.Runs.Select(run => string.Join(' ', run.Args))));
        Check(Enabled(resolved.Extensions, "tmp-pkg/extensions/index.ts"), "refreshed extension resolved");

        processes = new FakeProcesses { OnCapture = (_, _, _) => "\"2.0.0\"", OnRun = (_, _, _) => throw new PiPackageException("registry down") };
        resolved = await Manager(sandbox, processes: processes).ResolveExtensionSourcesAsync(["npm:tmp-pkg"], temporary: true);
        Check(Enabled(resolved.Extensions, "tmp-pkg/extensions/index.ts") && resolved.InstallFailures.IsEmpty, "a failed refresh keeps the cached install");

        processes = new FakeProcesses { OnCapture = (_, _, _) => "\"1.0.0\"" };
        await Manager(sandbox, processes: processes).ResolveExtensionSourcesAsync(["npm:tmp-pkg"], temporary: true);
        Equal(0, processes.Runs.Count, "current temporary source not reinstalled");
        processes = new FakeProcesses { OnCapture = (_, _, _) => "\"2.0.0\"" };
        await Manager(sandbox, processes: processes, environment: new() { ["PI_OFFLINE"] = "1" }).ResolveExtensionSourcesAsync(["npm:tmp-pkg"], temporary: true);
        Equal(0, processes.Runs.Count + processes.Captures.Count, "offline: no refresh");
        // A user-scope source is never refreshed during resolve (pi update does that).
        var user = Path.Combine(sandbox.AgentDir, "npm", "node_modules", "tmp-pkg");
        sandbox.Write(Path.Combine(user, "package.json"), Jstr(new { name = "tmp-pkg", version = "1.0.0" }));
        processes = new FakeProcesses { OnCapture = (_, _, _) => "\"2.0.0\"" };
        await Manager(sandbox, J("""{"packages":["npm:tmp-pkg"]}"""), processes: processes).ResolveAsync();
        Equal(0, processes.Runs.Count + processes.Captures.Count, "user scope not refreshed on resolve");
    }

    // refreshTemporaryGitSource: a temporary git source with a ref is refreshed too (a ref can name a moving branch); only a commit-hash
    // ref that is already checked out skips the fetch, compared without case.
    private static async Task PackageTemporaryGitRefresh()
    {
        using var sandbox = new Sandbox("pkg-temp-git");
        async Task<FakeProcesses> Resolve(string source, string head)
        {
            var git = (PiGitSource)PiPackageSource.Parse(source);
            Directory.CreateDirectory(Manager(sandbox).GetGitInstallPath(git, "temporary"));
            var processes = new FakeProcesses { OnCapture = (_, args, _) => args is ["rev-parse", "HEAD"] ? head + "\n" : "same" };
            await Manager(sandbox, processes: processes).ResolveExtensionSourcesAsync([source], temporary: true);
            return processes;
        }
        bool Fetched(FakeProcesses processes, string reference) => processes.Runs.Any(run => run.Command == "git" && run.Args.SequenceEqual(["fetch", "origin", reference]));
        Check(Fetched(await Resolve("git:github.com/user/branchy@main", "0123456789abcdef0123456789abcdef01234567"), "main"), "a branch ref refreshes");
        var current = await Resolve("git:github.com/user/hashed@ABC1234", "abc1234def5678abc1234def5678abc1234def56");
        Check(!Fetched(current, "ABC1234") && current.Captures.Count == 1, "a checked-out commit hash skips the fetch");
        Check(Fetched(await Resolve("git:github.com/user/moved@abc1234", "fedcba9876543210fedcba9876543210fedcba98"), "abc1234"), "another checkout fetches the hash");
    }

    // cleanAndInstallGitDependencies: a failed git clean (files in use by another session) reports a progress event, still installs the
    // new commit's dependencies and keeps the update marker so the next update retries the clean.
    private static async Task PackageGitCleanFailure()
    {
        using var sandbox = new Sandbox("pkg-git-clean");
        var target = Path.Combine(sandbox.AgentDir, "git", "github.com", "user", "repo");
        var marker = Path.Combine(sandbox.AgentDir, "git", "github.com", "user", ".repo.pi-update-incomplete");
        sandbox.Write(Path.Combine(target, "package.json"), Jstr(new { name = "repo", version = "1.0.0" }));
        var processes = new FakeProcesses
        {
            OnCapture = (_, args, _) => args switch
            {
                ["rev-parse", "--abbrev-ref", "@{upstream}"] => "origin/main", ["rev-parse", "HEAD"] => "old-head", _ => "new-head"
            },
            OnRun = (_, args, _) => args[0] == "clean" ? throw new PiPackageException("simulated clean failure") : Task.CompletedTask
        };
        var progress = new List<PiPackageProgressEvent>();
        var manager = Manager(sandbox, J("""{"packages":["git:github.com/user/repo"]}"""), processes: processes);
        manager.SetProgressCallback(progress.Add);
        await manager.UpdateAsync("git:github.com/user/repo");
        Check(processes.Ran("git", ["reset", "--hard", "@{upstream}^{commit}"], target) && processes.Ran("npm", ["install", "--omit=dev", "--legacy-peer-deps", "--no-audit", "--no-fund"], target),
            "dependencies installed after a failed clean");
        Check(File.Exists(marker), "the update marker stays for the next update");
        var warning = progress.Single(e => e.Type == "progress");
        Equal(("update", target, $"Could not remove all untracked files in {target} (simulated clean failure). Files may be in use by another pi session; close it and run the update again."),
            (warning.Action, warning.Source, warning.Message), "clean failure progress");
        Check(progress.Any(e => e is { Type: "complete", Source: "git:github.com/user/repo" }), "the update completes");
        // The next update at the same commit retries the clean (the marker) and then clears the marker.
        processes = new FakeProcesses { OnCapture = (_, args, _) => args switch { ["rev-parse", "--abbrev-ref", "@{upstream}"] => "origin/main", _ => "new-head" } };
        await Manager(sandbox, J("""{"packages":["git:github.com/user/repo"]}"""), processes: processes).UpdateAsync("git:github.com/user/repo");
        Check(processes.Ran("git", ["clean", "-fdx"], target) && !File.Exists(marker), "the retried clean clears the marker");
    }

    // reportUnchangedNpmUpdates: an announced update that leaves the installed version unchanged (stale registry metadata) is reported as
    // a progress event, which the update command prints; a version lookup that failed or an update that took effect is not reported.
    private static async Task PackageUnchangedNpmUpdate()
    {
        using var sandbox = new Sandbox("pkg-unchanged");
        var userRoot = Path.Combine(sandbox.AgentDir, "npm");
        void Installed(string name, string version) => sandbox.Write(Path.Combine(userRoot, "node_modules", name, "package.json"), Jstr(new { name, version }));
        Installed("stale", "1.0.0"); Installed("fresh", "1.0.0"); Installed("unknown", "1.0.0");
        FakeProcesses Processes() => new()
        {
            OnCapture = (_, args, _) => args[1] == "unknown" ? throw new PiPackageException("view failed") : "\"2.0.0\"",
            OnRun = (_, args, _) => { if (args.Contains("fresh@latest")) Installed("fresh", "2.0.0"); return Task.CompletedTask; }
        };
        var processes = Processes();
        var progress = new List<PiPackageProgressEvent>();
        var manager = Manager(sandbox, J("""{"packages":["npm:stale","npm:fresh","npm:unknown"]}"""), processes: processes);
        manager.SetProgressCallback(progress.Add);
        var summary = await manager.UpdateAsync();
        Equal(PiPackageUpdateStatus.Warning, summary.Packages.Single(p => p.Source == "npm:stale").Status, "stale registry is a typed warning");
        Equal(PiPackageUpdateStatus.Changed, summary.Packages.Single(p => p.Source == "npm:fresh").Status, "updated version is changed");
        Equal(("1.0.0", "2.0.0"), (summary.Packages.Single(p => p.Source == "npm:fresh").Before!, summary.Packages.Single(p => p.Source == "npm:fresh").After!), "version transition");
        Equal(PiPackageUpdateStatus.Warning, summary.Packages.Single(p => p.Source == "npm:unknown").Status, "failed metadata lookup is surfaced");
        Check(processes.Ran("npm", ["install", "stale@latest", "fresh@latest", "unknown@latest", "--prefix", userRoot, "--legacy-peer-deps", "--no-audit", "--no-fund"]), "one batch");
        var expected = "npm:stale is still 1.0.0 (registry reports 2.0.0); npm registry metadata may be stale, try again in a few minutes";
        Names([expected], progress.Where(e => e.Type == "progress").Select(e => e.Message!), "unchanged warnings");
        Check(progress.Single(e => e.Type == "progress") is { Action: "update", Source: "npm:stale" }, "warning source");

        Installed("fresh", "1.0.0");
        sandbox.Write(Path.Combine(sandbox.AgentDir, "settings.json"), """{"packages":["npm:stale","npm:fresh","npm:unknown"]}""");
        var (code, stdout, stderr) = await RunPackages(sandbox, (_, _) => Processes(), "update", "--extensions");
        Equal(0, code, "update exit; " + stderr);
        Check(stdout.Split('\n').Any(line => line.Contains(expected, StringComparison.Ordinal)), "the update command prints the warning: " + stdout);
        Check(!stdout.Split('\n').Contains("Updated packages"), "no unconditional success summary");
    }

    private static async Task PackageUpdateSummary()
    {
        using var sandbox = new Sandbox("update-summary");
        var processes = new FakeProcesses();
        var summary = await Manager(sandbox, J("""{"packages":["npm:some-package"]}"""), processes: processes,
            environment: new() { ["PI_OFFLINE"] = "1" }).UpdateAsync();
        Equal(PiPackageUpdateStatus.Offline, summary.Packages.Single().Status, "offline status");
        Equal(0, processes.Runs.Count + processes.Captures.Count, "offline performs no processes");
        Check(summary.Lines().Single().Contains("offline mode", StringComparison.Ordinal), "offline output");
        summary = await Manager(sandbox).UpdateAsync();
        Check(summary.Packages.IsEmpty && summary.Lines().Single() == "No packages configured for update.", "empty summary");

        const string source = "git:github.com/test/summary";
        var head = new string('a', 40); var remote = new string('b', 40);
        processes = new FakeProcesses
        {
            OnCapture = (_, args, _) => args switch
            {
                ["rev-parse", "HEAD"] => head,
                ["rev-parse", "--abbrev-ref", "@{upstream}"] => "origin/main",
                ["rev-parse", "@{upstream}"] or ["rev-parse", "@{upstream}^{commit}"] => remote,
                _ => throw new PiPackageException("unexpected capture")
            },
            OnRun = (_, args, _) => { if (args[0] == "reset") head = remote; return Task.CompletedTask; }
        };
        var manager = Manager(sandbox, J("""{"packages":["git:github.com/test/summary"]}"""), processes: processes);
        Directory.CreateDirectory(manager.GetGitInstallPath((PiGitSource)PiPackageSource.Parse(source), "user"));
        summary = await manager.UpdateAsync();
        Equal(PiPackageUpdateStatus.Changed, summary.Packages.Single().Status, "git changed");
        Equal(new string('a', 40), summary.Packages.Single().Before!, "old commit");
        Equal(remote, summary.Packages.Single().After!, "new commit");
        summary = await manager.UpdateAsync();
        Equal(PiPackageUpdateStatus.Unchanged, summary.Packages.Single().Status, "git no change");
        remote = new string('c', 40);
        processes.OnRun = (_, args, _) => { if (args[0] == "reset") head = remote; if (args[0] == "clean") throw new PiPackageException("locked file"); return Task.CompletedTask; };
        summary = await manager.UpdateAsync();
        Equal(PiPackageUpdateStatus.Warning, summary.Packages.Single().Status, "git incomplete cleanup warning");
    }

    // resolvePackageSources: a configured package that cannot be installed is skipped and recorded (source, scope, message) instead of
    // aborting the resolve; describePackageInstallFailure gives a scope-specific hint; the warning reaches a print run's diagnostics,
    // --help, --list-models and pi config, and the next start tries the install again.
    private static async Task PackageInstallFailureAtStartup()
    {
        using var sandbox = new Sandbox("pkg-install-failure");
        FakeProcesses Failing() => new() { OnRun = (_, args, _) => args[0] == "install" ? throw new PiPackageException("registry unreachable") : Task.CompletedTask };
        var resolved = await Manager(sandbox, J("""{"packages":["npm:broken-user"]}"""), J("""{"packages":["npm:broken-project"]}"""), processes: Failing()).ResolveAsync();
        Equal("""[["npm:broken-project","project","registry unreachable"],["npm:broken-user","user","registry unreachable"]]""",
            Jstr(resolved.InstallFailures.Select(failure => new[] { failure.Source, failure.Scope, failure.Message })), "install failures");
        var temporary = await Manager(sandbox, processes: Failing()).ResolveExtensionSourcesAsync(["npm:broken-temp"], temporary: true);
        Equal("temporary", temporary.InstallFailures.Single().Scope, "temporary failure");
        Equal("Missing source: npm:broken-user", (await ThrowsAsync<PiPackageException>(() => Manager(sandbox, J("""{"packages":["npm:broken-user"]}"""), processes: Failing())
            .ResolveAsync(_ => Task.FromResult(PiMissingSourceAction.Error)))).Message, "onMissing error still throws");

        var user = "Extension package \"npm:broken-user\": failed to install (registry unreachable). Run \"pisharp remove npm:broken-user\" to remove it from settings.json.";
        var project = "Extension package \"npm:broken-project\": failed to install (registry unreachable). Run \"pisharp remove npm:broken-project --local\" to remove it from settings.json.";
        Equal(user, PiSharp.Cli.Extensions.Pi.PiExtensionRun.FormatPackageInstallWarning(resolved.InstallFailures[1]), "user hint");
        Equal(project, PiSharp.Cli.Extensions.Pi.PiExtensionRun.FormatPackageInstallWarning(resolved.InstallFailures[0]), "project hint");
        Equal("Extension package \"npm:broken-temp\": failed to install (registry unreachable). Check the source and try again.",
            PiSharp.Cli.Extensions.Pi.PiExtensionRun.FormatPackageInstallWarning(temporary.InstallFailures[0]), "temporary hint");

        sandbox.Write(Path.Combine(sandbox.AgentDir, "settings.json"), """{"packages":["npm:broken-user"]}""");
        var installs = 0;
        Func<TextWriter, TextWriter, PiPackageProcesses> processes = (_, _) => new FakeProcesses
        { OnRun = (_, args, _) => { if (args[0] == "install") { Interlocked.Increment(ref installs); throw new PiPackageException("registry unreachable"); } return Task.CompletedTask; } };
        var (code, stdout, stderr) = await RunPackages(sandbox, processes, [.. new[] { "-p" }, .. Model, "hello"]);
        Equal(0, code, "a print run starts; " + stderr);
        Equal("ok", stdout.Trim(), "print output");
        Equal(1, stderr.Split('\n').Count(line => line == "Warning: " + user), "one warning: " + stderr);
        Check(installs > 0, "the install was attempted");
        (code, stdout, stderr) = await RunPackages(sandbox, processes, "--help");
        Check(code == 0 && stderr.Split('\n').Contains("Warning: " + user), "--help reports the runtime diagnostics: " + stderr);
        (code, _, stderr) = await RunPackages(sandbox, processes, "--list-models");
        Check(code == 0 && stderr.Split('\n').Contains("Warning: " + user), "--list-models reports the runtime diagnostics: " + stderr);
        (_, _, stderr) = await RunPackages(sandbox, processes, "config");
        Equal(1, stderr.Split('\n').Count(line => line == "Warning: " + user), "pi config prints the warning once: " + stderr);
    }

    // resource-loader.ts reload: the install warnings are collected again on every load, so /reload reports a package that still fails
    // and drops one that installed in the meantime.
    private static async Task PackageInstallFailureOnReload()
    {
        using var sandbox = NodeSandbox("pkg-install-reload");
        sandbox.Write(Path.Combine(sandbox.AgentDir, "settings.json"), """{"packages":["npm:broken-user"]}""");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "ext.ts"), "export default function () {}");
        var installRoot = Path.Combine(sandbox.AgentDir, "npm");
        var succeed = false;
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        var host = sandbox.Host(stdout, stderr, null) with
        {
            PackageProcesses = (_, _) => new FakeProcesses
            {
                OnRun = (_, args, _) =>
                {
                    if (args[0] != "install") return Task.CompletedTask;
                    if (!succeed) throw new PiPackageException("registry unreachable");
                    sandbox.Write(Path.Combine(installRoot, "node_modules", "broken-user", "package.json"), Jstr(new { name = "broken-user", version = "1.0.0" }));
                    return Task.CompletedTask;
                }
            }
        };
        await using var run = await PiSharp.Cli.Extensions.Pi.PiExtensionRun.LoadAsync(host, PiArgs.Parse(["-e", extension]), sandbox.Cwd, sandbox.AgentDir, sandbox.Home,
            true, "print", false, null, CancellationToken.None);
        var describe = "failed to install (registry unreachable). Run \"pisharp remove npm:broken-user\" to remove it from settings.json.";
        Check(run.Diagnostics.Any(d => d.Type == "warning" && d.Message == "Extension package \"npm:broken-user\": " + describe), "load warning: " + string.Join(" | ", run.Diagnostics.Select(d => d.Message)));
        Equal(("npm:broken-user", describe), run.Host!.PackageWarnings.Single(), "host warnings after load");
        await run.Host.ResolveReloadPaths!(CancellationToken.None);
        Check(run.Host.PackageWarnings.Length == 1, "a reload that still fails reports it again");
        succeed = true;
        await run.Host.ResolveReloadPaths!(CancellationToken.None);
        Check(run.Host.PackageWarnings.IsEmpty, "a reload that installed the package drops the warning");
    }
}
