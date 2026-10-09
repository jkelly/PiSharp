using PiSharp.Cli.Pi;

// core/resource-loader.ts (context files, SYSTEM.md/APPEND_SYSTEM.md, resolvePromptInput) and core/package-manager.ts (default skill,
// prompt and theme locations and their precedence), ported from test/resource-loader.test.ts where a case exists.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> ResourceCases() =>
    [
        ("resources.context-files-agent-dir-then-root-down-with-override", Sync(() =>
        {
            using var sandbox = new Sandbox("context-files");
            var nested = Path.Combine(sandbox.Cwd, "packages", "app");
            Directory.CreateDirectory(nested);
            var global = sandbox.Write(Path.Combine(sandbox.AgentDir, "AGENTS.md"), "global rules");
            var rootClaude = sandbox.Write(Path.Combine(sandbox.Root, "CLAUDE.md"), "root claude");
            var project = sandbox.Write(Path.Combine(sandbox.Cwd, "AGENTS.md"), "project agents");
            sandbox.Write(Path.Combine(sandbox.Cwd, "CLAUDE.md"), "shadowed by AGENTS.md");
            var nestedOverride = sandbox.Write(Path.Combine(nested, "AGENTS.override.md"), "nested override");
            sandbox.Write(Path.Combine(nested, "AGENTS.md"), "not used");
            Directory.CreateDirectory(Path.Combine(sandbox.Cwd, "packages", "AGENTS.md")); // A directory candidate is skipped.
            var files = PiResources.LoadProjectContextFiles(nested, sandbox.AgentDir);
            var ours = files.Where(file => file.Path.StartsWith(sandbox.Root, StringComparison.OrdinalIgnoreCase)).ToArray();
            Names([global, rootClaude, project, nestedOverride], ours.Select(file => file.Path), "order");
            Names(["global rules", "root claude", "project agents", "nested override"], ours.Select(file => file.Content), "contents");
        })),
        ("resources.worktree-shadow-skips-main-repo-duplicate", Sync(() =>
        {
            using var sandbox = new Sandbox("worktree-shadow");
            var main = Path.Combine(sandbox.Root, "main"); var worktree = Path.Combine(main, ".worktrees", "feature");
            Directory.CreateDirectory(Path.Combine(main, ".git", "worktrees", "feature"));
            File.WriteAllText(Path.Combine(main, ".git", "HEAD"), "ref: refs/heads/main\n");
            File.WriteAllText(Path.Combine(main, ".git", "worktrees", "feature", "HEAD"), "ref: refs/heads/feature\n");
            File.WriteAllText(Path.Combine(main, ".git", "worktrees", "feature", "commondir"), "../..\n");
            Directory.CreateDirectory(worktree);
            File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: " + Path.Combine(main, ".git", "worktrees", "feature") + "\n");
            var mainAgents = sandbox.Write(Path.Combine(main, "AGENTS.md"), "main");
            var worktreeAgents = sandbox.Write(Path.Combine(worktree, "AGENTS.md"), "worktree");
            var files = PiResources.LoadProjectContextFiles(worktree, sandbox.AgentDir).Select(file => file.Path).ToArray();
            Check(files.Contains(worktreeAgents) && !files.Contains(mainAgents), "the worktree's copy shadows the main repo's: " + string.Join(";", files));
            File.Delete(worktreeAgents);
            files = PiResources.LoadProjectContextFiles(worktree, sandbox.AgentDir).Select(file => file.Path).ToArray();
            Check(files.Contains(mainAgents), "without its own copy the worktree inherits the main repo's");
        })),
        ("resources.system-and-append-prompt-discovery-trust-and-cli", Sync(() =>
        {
            using var sandbox = new Sandbox("system-md");
            sandbox.Write(Path.Combine(sandbox.AgentDir, "SYSTEM.md"), "global system");
            sandbox.Write(Path.Combine(sandbox.Cwd, ".pi", "SYSTEM.md"), "project system");
            sandbox.Write(Path.Combine(sandbox.AgentDir, "APPEND_SYSTEM.md"), "global append");
            var settings = PiSettings.Load(sandbox.Cwd, sandbox.AgentDir, true);
            PiResources Discover(bool trusted, string? system = null, string[]? append = null) => PiResources.Discover(new(sandbox.Cwd, sandbox.AgentDir, sandbox.Home, settings, trusted)
            { SystemPrompt = system, AppendSystemPrompt = append is null ? default : [.. append], NoContextFiles = true });
            Equal("project system", Discover(true).SystemPrompt, "trusted project SYSTEM.md");
            Equal("global system", Discover(false).SystemPrompt, "untrusted project falls back to the global file");
            Names(["global append"], Discover(true).AppendSystemPrompt, "APPEND_SYSTEM.md");
            var file = sandbox.Write("prompt.txt", "from a file");
            Equal("from a file", Discover(true, file).SystemPrompt, "--system-prompt naming a file reads it");
            Equal("literal text", Discover(true, "literal text").SystemPrompt, "--system-prompt text");
            Names(["A", "from a file"], Discover(true, null, ["A", file]).AppendSystemPrompt, "--append-system-prompt replaces discovery");
        })),
        ("resources.skill-prompt-theme-locations-and-precedence", Sync(() =>
        {
            using var sandbox = new Sandbox("resource-paths");
            sandbox.Write(Path.Combine(sandbox.AgentDir, "settings.json"), """{"skills":["~/extra-skills","+ignored"],"prompts":["/abs/prompts"]}""");
            sandbox.Write(Path.Combine(sandbox.Cwd, ".pi", "settings.json"), """{"skills":["local-skills"]}""");
            sandbox.Write(Path.Combine(sandbox.Cwd, ".pi", "prompts", "review.md"), "Review $1");
            sandbox.Write(Path.Combine(sandbox.Cwd, ".pi", "prompts", ".hidden.md"), "hidden");
            sandbox.Write(Path.Combine(sandbox.AgentDir, "prompts", "fix.md"), "Fix it");
            sandbox.Write(Path.Combine(sandbox.AgentDir, "prompts", "notes.txt"), "not a prompt");
            sandbox.Write(Path.Combine(sandbox.AgentDir, "themes", "ocean.json"), """{"name":"ocean"}""");
            sandbox.Write(Path.Combine(sandbox.Cwd, ".pi", "themes", "ocean.json"), """{"name":"ocean"}""");
            sandbox.Write(Path.Combine(sandbox.Cwd, ".pi", "themes", "dark.json"), """{"name":"dark"}""");
            var settings = PiSettings.Load(sandbox.Cwd, sandbox.AgentDir, true);
            var resources = PiResources.Discover(new(sandbox.Cwd, sandbox.AgentDir, sandbox.Home, settings, true) { CliSkills = ["cli-skill"], NoContextFiles = true });
            Names([
                Path.Combine(sandbox.Cwd, ".pi", "local-skills"), Path.Combine(sandbox.Home, "extra-skills"),
                Path.Combine(sandbox.Cwd, ".pi", "skills"), Path.Combine(sandbox.Cwd, ".agents", "skills"), Path.Combine(sandbox.Root, ".agents", "skills"),
                Path.Combine(sandbox.AgentDir, "skills"), Path.Combine(sandbox.Home, ".agents", "skills"), Path.Combine(sandbox.Cwd, "cli-skill")
            ], resources.SkillPaths.Select(path => path.Path), "skill locations in source order (stopping at the git root)");
            Names([Path.GetFullPath("/abs/prompts"), Path.Combine(sandbox.Cwd, ".pi", "prompts", "review.md"), Path.Combine(sandbox.AgentDir, "prompts", "fix.md")],
                resources.PromptPaths.Select(path => path.Path), "prompt locations");
            Names(["ocean:" + Path.Combine(sandbox.Cwd, ".pi", "themes", "ocean.json")], resources.Themes.Select(theme => theme.Name + ":" + theme.Path), "project theme wins, built-in names are reserved");
            Check(resources.Diagnostics.Any(diagnostic => diagnostic.Message.Contains("\"dark\" collision", StringComparison.Ordinal)), "built-in collision reported");
            Check(resources.Diagnostics.Any(diagnostic => diagnostic.Message == "Skill path does not exist: " + Path.Combine(sandbox.Cwd, "cli-skill")), "missing --skill reported");
            var none = PiResources.Discover(new(sandbox.Cwd, sandbox.AgentDir, sandbox.Home, settings, true) { NoSkills = true, NoPromptTemplates = true, CliSkills = ["only"], NoContextFiles = true });
            Names([Path.Combine(sandbox.Cwd, "only")], none.SkillPaths.Select(path => path.Path), "--no-skills keeps explicit --skill paths");
            Equal(0, none.PromptPaths.Length, "--no-prompt-templates");
            var untrusted = PiResources.Discover(new(sandbox.Cwd, sandbox.AgentDir, sandbox.Home, PiSettings.Load(sandbox.Cwd, sandbox.AgentDir, false), false) { NoContextFiles = true });
            Check(!untrusted.SkillPaths.Any(path => path.Path.StartsWith(sandbox.Cwd, StringComparison.OrdinalIgnoreCase)) &&
                !untrusted.PromptPaths.Any(path => path.Path.StartsWith(sandbox.Cwd, StringComparison.OrdinalIgnoreCase)), "an untrusted project contributes nothing");
        })),
        ("resources.prompt-carries-context-append-and-skills-through-the-cli", async () =>
        {
            using var sandbox = new Sandbox("prompt-resources");
            sandbox.Write(Path.Combine(sandbox.Cwd, "AGENTS.md"), "Use tabs.");
            var skill = sandbox.Write(Path.Combine(sandbox.AgentDir, "skills", "deploy", "SKILL.md"), "---\nname: deploy\ndescription: Deploy the app <safely>\n---\nSteps.");
            var (code, _, stderr) = await sandbox.Run("-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "--append-system-prompt", "Extra rule.", "hi");
            Equal(0, code, "exit; " + stderr);
            var body = sandbox.Requests[0].Json;
            var system = string.Join("\n\n", body.GetProperty("system").EnumerateArray().Select(block => block.GetProperty("text").GetString()));
            var context = $"Project-specific instructions and guidelines:\n\n<project_instructions path=\"{Path.Combine(sandbox.Cwd, "AGENTS.md")}\">\nUse tabs.\n</project_instructions>";
            var expected = ExpectedDefaultPrompt(sandbox, DefaultTools, context, "Extra rule.");
            var skills = "<skills>\nThe following skills provide specialized instructions for specific tasks.\nUse the read tool to load a skill's file when the task matches its description.\n" +
                "When a skill file references a relative path, resolve it against the skill directory (parent of SKILL.md / dirname of the path) and use that absolute path in tool commands.\n\n" +
                $"<available_skills>\n  <skill>\n    <name>deploy</name>\n    <description>Deploy the app &lt;safely&gt;</description>\n    <location>{skill}</location>\n  </skill>\n</available_skills>\n</skills>";
            expected = expected.Replace("\n\n<cwd>\n", "\n\n" + skills + "\n\n<cwd>\n", StringComparison.Ordinal);
            Equal(expected, system, "prompt with context, addendum and skills");
        }),
    ];
}
