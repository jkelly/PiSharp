using PiSharp.Cli.Pi;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;

// core/extensions/runner.ts emitProjectTrustEvent and emitResourcesDiscover over a native ExtensionRegistry, directly and through the
// Pi entry (project-trust.ts resolveProjectTrusted; agent-session.ts extendResourcesFromExtensions).
internal static partial class Program
{
    private sealed class Plugin(Action<IExtensionRegistry> initialize) : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) { initialize(registry); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task<ExtensionRegistry> Extensions(params (string Owner, Action<IExtensionEventHandlerRegistry> Register)[] owners)
    {
        var registry = new ExtensionRegistry();
        foreach (var (owner, register) in owners) await registry.ActivateAsync(owner, new Plugin(api => register((IExtensionEventHandlerRegistry)api)));
        return registry;
    }

    private static ValueTask<JsonData?> Result(string json) => ValueTask.FromResult<JsonData?>(JsonData.Parse(json));

    private static IEnumerable<(string, Func<Task>)> ExtensionEventCases() =>
    [
        ("extension-events.project-trust-first-decision-wins-errors-reported-and-remembered", async () =>
        {
            var seen = new List<string>();
            await using var registry = await Extensions(
                ("undecided-ext", api => api.RegisterEventHandler(new("a", "project_trust", (value, _, _) => { seen.Add("a:" + value); return Result("""{"trusted":"undecided"}"""); }))),
                ("failing-ext", api => api.RegisterEventHandler(new("b", "project_trust", (_, _, _) => throw new InvalidOperationException("trust lookup failed")))),
                ("deciding-ext", api => api.RegisterEventHandler(new("c", "project_trust", (value, _, _) => { seen.Add("c:" + value); return Result("""{"trusted":"no","remember":true}"""); }))),
                ("late-ext", api => api.RegisterEventHandler(new("d", "project_trust", (_, _, _) => { seen.Add("d"); return Result("""{"trusted":"yes"}"""); }))));
            var errors = new List<string>();
            var decision = await PiExtensionEvents.ProjectTrustAsync(registry, registry.CaptureSnapshot(), "/work/project",
                message => { errors.Add(message); return ValueTask.CompletedTask; }, CancellationToken.None);
            Equal(new PiProjectTrustDecision(false, true), decision, "first decisive handler");
            Equal("a:{\"type\":\"project_trust\",\"cwd\":\"/work/project\"}", seen[0], "event payload");
            Equal(2, seen.Count, "handlers after the first decision do not run (seen: " + string.Join(", ", seen) + ")");
            Names(["Extension \"failing-ext\" project_trust error: trust lookup failed"], errors, "error text");
            await using var none = await Extensions();
            Equal(null, await PiExtensionEvents.ProjectTrustAsync(none, none.CaptureSnapshot(), "/x", null, CancellationToken.None), "no handlers");
            // Through the entry: the extension decides before the store, and remember:true saves it.
            using var sandbox = new Sandbox("ext-trust", trusted: false);
            sandbox.Write(Path.Combine(sandbox.Cwd, ".pi", "SYSTEM.md"), "EXTENSION TRUSTED PROMPT");
            await using var yes = await Extensions(("trusting-ext", api => api.RegisterEventHandler(new("t", "project_trust", (_, _, _) => Result("""{"trusted":"yes","remember":true}""")))));
            using var stdout = new StringWriter(); using var stderr = new StringWriter();
            var host = sandbox.Host(stdout, stderr, null) with { LoadExtensions = (_, _) => Task.FromResult<PiLoadedExtensions?>(new(yes, yes.CaptureSnapshot())) };
            Equal(0, await PiCommand.RunAsync(["-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "hi"], host, CancellationToken.None), "exit; " + stderr);
            Equal("EXTENSION TRUSTED PROMPT", sandbox.Requests[0].Json.GetProperty("system")[0].GetProperty("text").GetString()!.Split("\n\n")[0], "the project is trusted");
            Equal(true, new ProjectTrustStore(sandbox.AgentDir, sandbox.Home).Get(sandbox.Cwd), "remembered in trust.json");
        }),
        ("extension-events.resources-discover-collects-attributed-paths-and-reports-errors", async () =>
        {
            using var sandbox = new Sandbox("ext-resources");
            var skill = sandbox.Write(Path.Combine(sandbox.Root, "ext-skills", "lint", "SKILL.md"), "---\nname: lint\ndescription: Lint the code\n---\nRun the linter.");
            var payloads = new List<string>();
            await using var registry = await Extensions(
                ("skills-ext", api => api.RegisterEventHandler(new("s", "resources_discover", (value, _, _) =>
                { payloads.Add(value.ToString()); return Result("{\"skillPaths\":[" + System.Text.Json.JsonSerializer.Serialize(Path.Combine(sandbox.Root, "ext-skills")) + "],\"promptPaths\":[\"prompts\"]}"); }))),
                ("broken-ext", api => api.RegisterEventHandler(new("b", "resources_discover", (_, _, _) => throw new InvalidOperationException("discovery failed")))),
                ("themes-ext", api => api.RegisterEventHandler(new("t", "resources_discover", (_, _, _) => Result("""{"themePaths":["themes/a.json"],"skillPaths":"not-an-array"}""")))));
            var errors = new List<string>();
            var found = await PiExtensionEvents.ResourcesDiscoverAsync(registry, registry.CaptureSnapshot(), sandbox.Cwd, "startup",
                (path, message) => { errors.Add(path + ": " + message); return ValueTask.CompletedTask; }, CancellationToken.None);
            Names([Path.Combine(sandbox.Root, "ext-skills")], found.SkillPaths.Select(entry => entry.Path), "skill paths");
            Names(["prompts"], found.PromptPaths.Select(entry => entry.Path), "prompt paths"); Names(["themes/a.json"], found.ThemePaths.Select(entry => entry.Path), "theme paths");
            Names(["skills-ext", "skills-ext", "themes-ext"], [.. found.SkillPaths.Concat(found.PromptPaths).Concat(found.ThemePaths).Select(entry => entry.ExtensionPath)], "each path keeps the extension that returned it");
            // agent-session.ts getExtensionSourceLabel: file name without .ts/.js, or the synthetic path without angle brackets.
            Names(["extension:skills-ext", "extension:lint", "extension:tool.mjs", "extension:inline:1"],
                [.. new[] { "skills-ext", "/exts/lint.ts", "/exts/tool.mjs", "<inline:1>" }.Select(owner => new PiDiscoveredPath("x", owner).SourceLabel)], "source labels");
            var merged = PiResources.WithDiscovered(new PiResources(), found, sandbox.Cwd, sandbox.Home);
            Names(["temporary:extension:skills-ext"], merged.SkillPaths.Select(item => item.Scope + ":" + item.Source), "discovered skill path scope and source");
            Equal($"{{\"type\":\"resources_discover\",\"cwd\":{System.Text.Json.JsonSerializer.Serialize(sandbox.Cwd)},\"reason\":\"startup\"}}", payloads.Single(), "event payload");
            Names(["broken-ext: discovery failed"], errors, "errors");
            Throws<ArgumentException>(() => PiExtensionEvents.ResourcesDiscoverAsync(registry, registry.CaptureSnapshot(), sandbox.Cwd, "later", null, CancellationToken.None).GetAwaiter().GetResult(), "reason");
            // Through the entry: the discovered skill reaches the prompt, and print mode reports the handler error on stderr.
            using var stdout = new StringWriter(); using var stderr = new StringWriter();
            var host = sandbox.Host(stdout, stderr, null) with { LoadExtensions = (_, _) => Task.FromResult<PiLoadedExtensions?>(new(registry, registry.CaptureSnapshot())) };
            Equal(0, await PiCommand.RunAsync(["-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "hi"], host, CancellationToken.None), "exit; " + stderr);
            var system = sandbox.Requests[0].Json.GetProperty("system")[0].GetProperty("text").GetString()!;
            Check(system.Contains("<name>lint</name>", StringComparison.Ordinal) && system.Contains(skill, StringComparison.Ordinal), "discovered skill in the prompt");
            Equal("Extension error (broken-ext): discovery failed\n", stderr.ToString(), "print-mode extension error line");
        }),
    ];
}
