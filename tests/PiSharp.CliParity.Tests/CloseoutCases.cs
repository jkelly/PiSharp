using System.Text.Json;
using PiSharp.Cli.Pi;

// The CLI differences 1.1.0.2 still listed (1.1.0.3 close-out): -e package sources contribute their skills, prompts and themes
// (core/resource-loader.ts reload over packageManager.resolveExtensionSources), and `mcp` runs after main.ts's setup (cli/setup.ts
// PI_CODING_AGENT, --offline/PI_OFFLINE, the bootstrap settings' httpProxy). Written from the pinned sources by reading.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> CloseoutCases() =>
    [
        ("closeout.e-package-contributes-skills-prompts-and-themes", ExtensionPackageResources),
        ("closeout.mcp-command-runs-after-the-main-setup", McpAfterMainSetup),
    ];

    // resource-loader.ts reload: cliExtensionPaths = resolveExtensionSources(additionalExtensionPaths, { temporary: true }); its enabled
    // skills, prompts and themes lead the run's own (mergePaths([...cliEnabledSkills, ...enabledSkills], additionalSkillPaths)).
    private static async Task ExtensionPackageResources()
    {
        using var sandbox = new Sandbox("e-package-resources");
        var package = Path.Combine(sandbox.Root, "local-pack");
        sandbox.Write(Path.Combine(package, "package.json"), """{"name":"local-pack","version":"1.0.0","pi":{"prompts":["./prompts"],"skills":["./skills"],"themes":["./themes"]}}""");
        sandbox.Write(Path.Combine(package, "prompts", "pack-hello.md"), "---\ndescription: From the package\n---\nPrompt body from the -e package");
        sandbox.Write(Path.Combine(package, "skills", "pack-skill", "SKILL.md"), "---\nname: pack-skill\ndescription: A skill the -e package ships\n---\nSkill body");
        sandbox.Write(Path.Combine(package, "themes", "pack-theme.json"), JsonSerializer.Serialize(new { name = "pack-theme", vars = new { }, colors = new { } }));
        var (code, _, stderr) = await sandbox.Run("-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "-e", package, "/pack-hello");
        Equal(0, code, "exit; " + stderr);
        var body = sandbox.Requests.Single().Body!;
        Check(body.Contains("Prompt body from the -e package", StringComparison.Ordinal), "the package's prompt template expanded: " + body);
        Check(body.Contains("pack-skill", StringComparison.Ordinal) && body.Contains("A skill the -e package ships", StringComparison.Ordinal),
            "the package's skill is listed in the system prompt");
        var resources = PiResources.Discover(new(sandbox.Cwd, sandbox.AgentDir, sandbox.Home, PiSettings.Load(sandbox.Cwd, sandbox.AgentDir, true), true)
        {
            ExtensionSources = await new PiSharp.Cli.Packages.PiPackageManager(sandbox.Cwd, sandbox.AgentDir, sandbox.Home, PiSettings.Load(sandbox.Cwd, sandbox.AgentDir, true),
                name => sandbox.Vars.GetValueOrDefault(name)).ResolveExtensionSourcesAsync([package], temporary: true),
            NoSkills = true, NoPromptTemplates = true, NoThemes = true, NoContextFiles = true
        });
        // --no-skills/--no-prompt-templates/--no-themes keep the -e sources' resources (mergePaths(cliEnabled..., additional...)).
        Check(resources.PromptPaths.Any(path => path.Path.EndsWith("pack-hello.md", StringComparison.Ordinal)), "prompt path: " + string.Join(";", resources.PromptPaths.Select(path => path.Path)));
        Check(resources.SkillPaths.Any(path => path.Path.Contains("pack-skill", StringComparison.Ordinal)), "skill path: " + string.Join(";", resources.SkillPaths.Select(path => path.Path)));
        Names(["pack-theme"], resources.Themes.Select(theme => theme.Name), "theme from the package");
    }

    // main.ts main(): --offline/PI_OFFLINE set PI_OFFLINE=1 and PI_SKIP_VERSION_CHECK=1, the bootstrap settings (project untrusted) apply
    // the global httpProxy, and only then `mcp` runs (runMcpCommand(args.slice(1), { cwd, agentDir })); cli/setup.ts sets
    // PI_CODING_AGENT for child processes first.
    private static async Task McpAfterMainSetup()
    {
        using var sandbox = new Sandbox("mcp-main-setup");
        sandbox.Write(Path.Combine(sandbox.AgentDir, "settings.json"), """{"httpProxy":" http://proxy.test:3128 "}""");
        sandbox.Write(Path.Combine(sandbox.Cwd, ".pi", "settings.json"), """{"httpProxy":"http://project-proxy.test:1"}""");
        sandbox.Vars["PI_OFFLINE"] = "true";
        var (code, stdout, stderr) = await sandbox.Run("mcp", "--help");
        Equal(0, code, "exit; " + stderr);
        Check(stdout.Contains("mcp add <server>", StringComparison.Ordinal) && stdout.Contains("mcp list [--json]", StringComparison.Ordinal), "mcp help on stdout: " + stdout);
        Equal("true", sandbox.Vars.GetValueOrDefault("PI_CODING_AGENT"), "PI_CODING_AGENT");
        Equal("1", sandbox.Vars.GetValueOrDefault("PI_OFFLINE"), "PI_OFFLINE");
        Equal("1", sandbox.Vars.GetValueOrDefault("PI_SKIP_VERSION_CHECK"), "PI_SKIP_VERSION_CHECK");
        Equal("http://proxy.test:3128", sandbox.Vars.GetValueOrDefault("HTTP_PROXY"), "HTTP_PROXY from the global httpProxy (not the project's)");
        Equal("http://proxy.test:3128", sandbox.Vars.GetValueOrDefault("HTTPS_PROXY"), "HTTPS_PROXY");
        // An existing proxy variable is kept; `mcp list` reads the run's agent directory (an empty mcp.json lists nothing).
        sandbox.Vars.Remove("PI_OFFLINE"); sandbox.Vars.Remove("PI_SKIP_VERSION_CHECK"); sandbox.Vars["HTTPS_PROXY"] = "http://kept.test:9";
        sandbox.Write(Path.Combine(sandbox.AgentDir, "mcp.json"), """{"mcpServers":{}}""");
        (code, stdout, stderr) = await sandbox.Run("mcp", "list", "--json", "--offline");
        Equal("1", sandbox.Vars.GetValueOrDefault("PI_OFFLINE"), "--offline sets PI_OFFLINE before mcp runs");
        Equal("http://kept.test:9", sandbox.Vars.GetValueOrDefault("HTTPS_PROXY"), "an existing HTTPS_PROXY is kept");
        Check(code is 0 or 1 && stdout.Length + stderr.Length > 0, "mcp list ran: " + code + " " + stdout + " " + stderr);
    }
}
