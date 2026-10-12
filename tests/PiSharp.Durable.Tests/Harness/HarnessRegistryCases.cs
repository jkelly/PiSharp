// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/test/harness-registry.test.ts. Verbatim apart from the adaptations in
// HarnessSupport.cs and these: a host tool type with an extra field is a derived ToolRegistration record (AppTool), and `{ ...tool, x }` is
// `tool with { X = ... }`; stored agent states are pi.agent JSON; hook handler maps are the built-in hook classes; toBe is
// ReferenceEquals.
using System.Collections.Immutable;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using static Program;
using static SessionTests;

internal static partial class HarnessTests
{
    /// <summary>A host tool type with an extra field (upstream <c>AppTool</c>).</summary>
    private sealed record AppTool : ToolRegistration
    {
        public string? Snippet { get; init; }
    }

    private static AppTool RegistryTool(string name, string? description = null, string? snippet = null) => new()
    {
        Name = name,
        Description = description ?? $"{name} tool",
        Snippet = snippet,
        Execute = (_, _, _) => Task.FromResult(new ToolExecutionResult { Content = JsonArrayValue.Empty }),
    };

    private static DurableTask RegistryTask(string name, int version = 1) => Tasks.DefineTask(new TaskDefinition
    {
        Name = name,
        Version = version,
        Initial = _ => O("""{"phase":"run"}"""),
        Phases = new Dictionary<string, TaskPhaseHandler> { ["run"] = (_, _, _) => Task.CompletedTask },
        Abort = (_, _, _) => Task.CompletedTask,
    });

    private static List<string> Names(IEnumerable<Extension> items) => [.. items.Select(item => item.Name)];
    private static List<string> Names(IEnumerable<ToolRegistration> items) => [.. items.Select(item => item.Name)];

    /// <summary>Resolves <paramref name="state"/> against <paramref name="snapshot"/> and <paramref name="settings"/>, collecting reports.</summary>
    private static Agent ResolveFor(string? state, IRegistrySnapshot snapshot, HarnessSettings? settings = null, List<Exception>? reports = null) =>
        Agents.ResolveAgent(state is null ? null : O(state), snapshot, Agents.ResolveSettings(settings ?? new HarnessSettings()), error => reports?.Add(error));

    private static async Task<List<(string Key, string? Text)>> Rendered(Agent agent)
    {
        var input = new PromptInput { ConversationId = new ConversationId(1), Agent = agent, Env = null, Shown = ImmutableDictionary<string, string>.Empty, Read = NullReader.Instance };
        var rendered = new List<(string, string?)>();
        foreach (var item in agent.Sections) rendered.Add((item.Key, await item.Render(input, Context)));
        return rendered;
    }

    internal static IEnumerable<(string, Func<Task>)> RegistryCases(string prefix)
    {
        // ─── registry ───

        yield return Case(prefix + "registry.installs-replaces-in-place-and-uninstalls-extensions-by-name", () =>
        {
            var registry = Registry.Create();
            var listener = new List<string>();
            registry.Subscribe(() => listener.Add(string.Join(",", Names(registry.Snapshot().Installed()))));
            var a = new Extension { Name = "a", Tools = [RegistryTool("read", snippet: "Read files")] };
            var b = new Extension { Name = "b", Tools = [RegistryTool("read"), RegistryTool("bash")] };
            registry.Install(a);
            registry.Install(b);
            var before = registry.Snapshot();
            // A new object with an installed name replaces it at its position.
            var a2 = new Extension { Name = "a", Tools = [RegistryTool("grep")] };
            registry.Install(a2);
            Seq(["a", "b"], Names(registry.Snapshot().Installed()), "installed");
            Same(a2, registry.Snapshot().GetExtension("a"), "replaced extension");
            Seq(["grep@a", "read@b", "bash@b"], registry.Snapshot().Tools().Select(item => $"{item.Tool.Name}@{item.Extension.Name}"), "tools");
            // Old snapshots stay as they were.
            Same(a, before.GetExtension("a"), "old snapshot");
            Equal("Read files", ((AppTool)before.Tools()[0].Tool).Snippet, "old snapshot tool");
            // Uninstall matches the name, whichever object; a later install appends.
            registry.Uninstall(a);
            registry.Uninstall(a);
            Seq(["b"], Names(registry.Snapshot().Installed()), "after uninstall");
            registry.Install(a);
            Seq(["b", "a"], Names(registry.Snapshot().Installed()), "after reinstall");
            Seq(["a", "a,b", "a,b", "b", "b,a"], listener, "publications");
        });

        yield return Case(prefix + "registry.validates-the-registry-as-it-would-be-after-an-install-and-publishes-nothing-when-invalid", () =>
        {
            var registry = Registry.Create();
            registry.Install(new Extension { Name = "tasks", Tasks = [RegistryTask("app.index")] });
            var published = new List<int>();
            registry.Subscribe(() => published.Add(1));
            var before = registry.Snapshot();
            (Extension, string)[] invalid =
            [
                (new Extension { Name = "x", Tools = [RegistryTool("read"), RegistryTool("read")] }, "two tools named read"),
                (new Extension { Name = "x", Sections = [Define.Section("a", "1"), Define.Section("a", "2")] }, "two sections"),
                (new Extension { Name = "x", Sections = [Define.Section("Bad Key", "")] }, "must match"),
                (new Extension { Name = "x", Sections = [Define.Section("instructions", "")] }, "reserved"),
                (new Extension { Name = "x", Tasks = [RegistryTask("pi.generation")] }, "already installed"),
                (new Extension { Name = "x", Tasks = [RegistryTask("app.index")] }, "already installed"),
            ];
            foreach (var (extension, message) in invalid) Throws(() => registry.Install(extension), message, "invalid install");
            Same(before, registry.Snapshot(), "snapshot unchanged");
            Seq([], published, "publications");
            // Replacing the extension that holds a task name is valid: the check runs on the state after replacement.
            registry.Install(new Extension { Name = "tasks", Tasks = [RegistryTask("app.index", 2)] });
            Equal(2, registry.Snapshot().GetTask("app.index")?.Definition.Version, "replaced task version");
        });

        yield return Case(prefix + "registry.always-holds-the-built-in-tasks-which-are-not-an-extension", () =>
        {
            var registry = Registry.Create();
            Equal(0, registry.Snapshot().Installed().Count, "installed");
            Seq<DurableTask>([HarnessTasks.GenerationTask, HarnessTasks.ToolTask, HarnessTasks.CompactionTask], registry.Snapshot().Tasks(), "built-in tasks");
            var custom = RegistryTask("app.custom");
            registry.Install(new Extension { Name = "custom", Tasks = [custom] });
            Seq<DurableTask>([HarnessTasks.GenerationTask, HarnessTasks.ToolTask, HarnessTasks.CompactionTask, custom], registry.Snapshot().Tasks(), "tasks");
            Same(custom, registry.Snapshot().GetTask("app.custom"), "custom task");
            registry.Uninstall(new Extension { Name = "custom" });
            Check(registry.Snapshot().GetTask("app.custom") is null, "uninstalled task");
        });

        // ─── settings ───

        yield return Case(prefix + "settings.keeps-the-default-of-a-progress-interval-given-as-undefined", () =>
        {
            var settings = Agents.ResolveSettings(new HarnessSettings { Progress = new ProgressSettings { PartialIntervalMs = null, OutputIntervalMs = 250 } });
            Equal(new ProgressPolicy(100, 250), settings.Progress, "progress");
        });

        // ─── agent resolution ───

        var read = RegistryTool("read");
        var bash = RegistryTool("bash");
        var edit = RegistryTool("edit");
        var coding = new Extension
        {
            Name = "coding",
            Tools = [read, bash, edit],
            Sections = [Define.Section("preamble", "You code.", tag: false), Define.Section("cwd", "/repo")],
        };
        var skills = new Extension { Name = "skills", Sections = [Define.Section("skills", "S")] };
        var reviewer = new Extension { Name = "reviewer", Sections = [Define.Section("role", "Review.")] };
        var shared = Registry.Create();
        foreach (var extension in new[] { coding, skills, reviewer }) shared.Install(extension);
        var snapshot = shared.Snapshot();

        yield return Case(prefix + "agent-resolution.selects-the-default-an-array-or-the-default-edited-by-add-and-remove", () =>
        {
            Seq(["coding", "skills", "reviewer"], Names(ResolveFor(null, snapshot).Extensions), "default");
            var settings = new HarnessSettings { Extensions = [coding, skills] };
            Seq(["coding", "skills"], Names(ResolveFor("{}", snapshot, settings).Extensions), "host default");
            Seq(["reviewer", "coding"], Names(ResolveFor("""{"extensions":["reviewer","coding"]}""", snapshot, settings).Extensions), "array");
            // Add appends, remove drops, duplicates keep their first position, uninstalled names are skipped.
            var edited = """{"extensions":{"add":["reviewer","coding","gone"],"remove":["skills"]}}""";
            Seq(["coding", "reviewer"], Names(ResolveFor(edited, snapshot, settings).Extensions), "edited");
            // An old object stands for its name: the installed extension is selected.
            var stale = new HarnessSettings { Extensions = [new Extension { Name = "skills" }, skills] };
            var selected = ResolveFor("{}", snapshot, stale).Extensions;
            Check(selected.Length == 1 && ReferenceEquals(selected[0], skills), "stale object selects the installed extension");
        });

        yield return Case(prefix + "agent-resolution.skips-uninstalled-names-and-resolves-them-again-once-they-are-installed", () =>
        {
            var local = Registry.Create();
            local.Install(coding);
            var state = """{"extensions":["coding","skills"]}""";
            Seq(["coding"], Names(ResolveFor(state, local.Snapshot()).Extensions), "before install");
            local.Install(skills);
            Seq(["coding", "skills"], Names(ResolveFor(state, local.Snapshot()).Extensions), "after install");
        });

        yield return Case(prefix + "agent-resolution.replaces-same-name-tools-in-place-wraps-the-winner-then-applies-the-filter", () =>
        {
            var local = Registry.Create();
            var venvBash = RegistryTool("bash", description: "venv bash");
            var calls = new List<string>();
            var venv = new Extension { Name = "venv", Tools = [venvBash] };
            var timing = new Extension
            {
                Name = "timing",
                Wraps =
                [
                    Define.WrapTool(bash, inner => inner with { Description = $"{inner.Description} (timed)" }),
                    Define.WrapTool(bash, inner => inner with { Description = $"{inner.Description} [2]" }),
                    // No `grep` is selected: the wrapper does nothing and reports nothing.
                    Define.WrapTool(RegistryTool("grep"), _ =>
                    {
                        calls.Add("grep");
                        return RegistryTool("grep");
                    }),
                ],
            };
            foreach (var extension in new[] { coding, venv, timing }) local.Install(extension);
            var reports = new List<Exception>();
            var agent = ResolveFor(null, local.Snapshot(), null, reports);
            Seq(["read:read tool", "bash:venv bash (timed) [2]", "edit:edit tool"], agent.Tools.Select(each => $"{each.Name}:{each.Description}"), "tools");
            Check(agent.Tools[1] is AppTool, "a wrapper keeps the host tool type");
            Seq([], reports.Select(error => error.Message), "reports");
            Seq([], calls, "calls");

            // An array keeps exactly these names in its order, a repeated name at its first position.
            var filtered = ResolveFor("""{"tools":["edit","missing","read","edit"]}""", local.Snapshot());
            Seq(["edit", "read"], Names(filtered.Tools), "array filter");
            Seq(["read", "edit"], Names(ResolveFor("""{"tools":{"remove":["bash"]}}""", local.Snapshot()).Tools), "remove filter");
        });

        yield return Case(prefix + "agent-resolution.drops-a-tool-or-section-whose-wrapper-throws-or-renames-it-and-reports-the-failure", () =>
        {
            var local = Registry.Create();
            var broken = new Extension
            {
                Name = "broken",
                Wraps =
                [
                    Define.WrapTool(read, _ => throw new InvalidOperationException("wrapper failed")),
                    Define.WrapTool(edit, inner => inner with { Name = "renamed" }),
                    Define.WrapSection("cwd", _ => throw new InvalidOperationException("section wrapper failed")),
                ],
            };
            local.Install(coding);
            local.Install(broken);
            var reports = new List<Exception>();
            var agent = ResolveFor(null, local.Snapshot(), null, reports);
            Seq(["bash"], Names(agent.Tools), "tools");
            Seq(["preamble"], agent.Sections.Select(each => each.Key), "sections");
            Seq(["wrapper failed", "Wrapper renamed edit to renamed", "section wrapper failed"], reports.Select(error => error.Message), "reports");
        });

        yield return Case(prefix + "agent-resolution.orders-sections-by-extension-replaces-same-keys-in-place-and-renders-instructions-last-and-unwrapped", async () =>
        {
            var local = Registry.Create();
            var @override = new Extension
            {
                Name = "override",
                Sections = [Define.Section("preamble", "You review.", tag: false)],
                Wraps =
                [
                    Define.WrapSection("cwd", inner => inner with { Render = async (input, ctx) => $"{await inner.Render(input, ctx)}!" }),
                    // Instructions are not wrapped.
                    Define.WrapSection("instructions", _ => throw new InvalidOperationException("never")),
                ],
            };
            foreach (var extension in new[] { coding, skills, @override }) local.Install(extension);
            var reports = new List<Exception>();
            var agent = ResolveFor("""{"instructions":"Be terse."}""", local.Snapshot(), null, reports);
            Seq([("preamble", (string?)"You review."), ("cwd", "/repo!"), ("skills", "S"), ("instructions", "Be terse.")], await Rendered(agent), "rendered");
            Check(agent.Sections[^1].Tag is null, "instructions carry no tag field");
            Seq([], reports.Select(error => error.Message), "reports");
        });

        yield return Case(prefix + "agent-resolution.collects-hooks-of-the-selected-extensions-in-extension-order-and-applies-field-defaults", () =>
        {
            var local = Registry.Create();
            var first = new ToolHooks { BeforeTool = (_, _, _) => Task.FromResult<BeforeToolDecision?>(null) };
            var second = new ToolHooks { BeforeTool = (_, _, _) => Task.FromResult<BeforeToolDecision?>(null) };
            var onYield = new GenerationHooks { OnYield = (_, _, _) => Task.FromResult<JsonValue?>(null) };
            local.Install(new Extension
            {
                Name = "a",
                Hooks = [Define.Hook(HarnessTasks.ToolTask, first), Define.Hook(HarnessTasks.GenerationTask, onYield)],
            });
            local.Install(new Extension { Name = "b", Hooks = [Define.Hook(HarnessTasks.ToolTask, second)] });
            Seq([first, second], Agents.AgentHooks(ResolveFor(null, local.Snapshot()), "pi.tool"), "default order");
            Seq([second, first], Agents.AgentHooks(ResolveFor("""{"extensions":["b","a"]}""", local.Snapshot()), "pi.tool"), "selected order");
            Seq([], Agents.AgentHooks(ResolveFor("""{"extensions":["b"]}""", local.Snapshot()), "pi.generation"), "unselected");

            var defaults = ResolveFor(null, local.Snapshot());
            Check(defaults.Model is null, "model unset");
            Equal("off", defaults.ThinkingLevel, "thinking level");
            Check(defaults.Cwd is null, "cwd unset");
            var configured = ResolveFor("""{"model":{"provider":"p","modelId":"m"},"thinkingLevel":"high","cwd":"/w"}""", local.Snapshot());
            Equal(new ModelRef("p", "m"), configured.Model, "model");
            Equal("high", configured.ThinkingLevel, "configured thinking level");
            Equal("/w", configured.Cwd, "configured cwd");
        });
    }
}
