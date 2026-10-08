using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Cli.Prompts;
using PiSharp.CodingAgent;
using PiSharp.Contracts;

// Upstream: packages/coding-agent/src/core/system-prompt.ts and core/skills.ts:formatSkillsForPrompt at
// abe508e1b89912adde45528136c3221eb69acdd7. Expected sections are typed from that source text.
internal static partial class Program
{
    private const string Preamble = "You are an expert coding assistant operating inside pi, a coding agent harness. You help users by reading files, executing commands, editing code, and writing new files.";
    private const string ToolsTail = "\n\nIn addition to the tools above, you may have access to other custom tools depending on the project.";
    private const string Docs = "Pi documentation (read only when the user asks about pi itself, its SDK, extensions, themes, skills, or TUI):\n" +
        "- Main documentation: /pi/README.md\n- Additional docs: /pi/docs\n- Examples: /pi/examples (extensions, custom tools, SDK)\n" +
        "- When reading pi docs or examples, resolve docs/... under Additional docs and examples/... under Examples, not the current working directory\n" +
        "- When asked about: extensions (docs/extensions.md, examples/extensions/), themes (docs/themes.md), skills (docs/skills.md), prompt templates (docs/prompt-templates.md), TUI components (docs/tui.md), keybindings (docs/keybindings.md), SDK integrations (docs/sdk.md), custom providers (docs/custom-provider.md), adding models (docs/models.md), pi packages (docs/packages.md), environment variables (docs/environment-variables.md), MCP servers (docs/mcp.md), codemode scripts and non-LLM models such as classifiers and image models (docs/codemode.md)\n" +
        "- When working on pi topics, read the docs and examples, and follow .md cross-references before implementing\n" +
        "- Always read pi .md files completely and follow links to related docs (e.g., tui.md for TUI API details)";
    private const string SkillsBody = "When a skill file references a relative path, resolve it against the skill directory (parent of SKILL.md / dirname of the path) and use that absolute path in tool commands.\n\n" +
        "<available_skills>\n  <skill>\n    <name>lint &amp; fix</name>\n    <description>Lint &lt;code&gt;</description>\n    <location>/skills/lint/SKILL.md</location>\n  </skill>\n</available_skills>";

    private static readonly string Cwd = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "pisharp-cli-sync", "cwd"));

    private static OriginalSystemPromptAdmission Admission(params string[] hidden) => new()
    {
        HiddenTools = [.. hidden],
        ToolSnippets = [KeyValuePair.Create("read", "Read file contents"), KeyValuePair.Create("bash", "Execute bash commands"),
            KeyValuePair.Create("edit", "Make precise file edits"), KeyValuePair.Create("write", "Create or overwrite files"),
            KeyValuePair.Create("codemode", "Run a script that calls tools")],
        ToolGuidelines = [KeyValuePair.Create("read", ImmutableArray.Create("Use read to examine files instead of cat or sed.")),
            KeyValuePair.Create("bash", ImmutableArray.Create("Prefer bash for builds.")),
            KeyValuePair.Create("codemode", ImmutableArray.Create("Prefer codemode for multi-step tool work."))],
        Skills = [new("lint & fix", "Lint <code>", "/skills/lint/SKILL.md", "/skills/lint", JsonData.EmptyObject, false),
            new("manual", "Only by command", "/skills/manual/SKILL.md", "/skills/manual", JsonData.EmptyObject, true)],
        Documentation = new("/pi/README.md", "/pi/docs", "/pi/examples")
    };

    private static ImmutableArray<KeyValuePair<string, string>> Sections(OriginalSystemPromptAdmission input, params string[] selected) =>
        OriginalSystemPromptBuilder.Sections(OriginalSystemPromptBuilder.Capture(input, Cwd, [.. selected]));

    private static void SectionsEqual(IEnumerable<(string Key, string Value)> expected, ImmutableArray<KeyValuePair<string, string>> actual, string what)
    {
        var rows = expected.ToArray();
        Names(rows.Select(row => row.Key), actual.Select(row => row.Key), what + " section order");
        foreach (var (row, index) in rows.Select((row, index) => (row, index)))
            Equal(row.Value, actual[index].Value, $"{what} section {row.Key}");
    }

    private static string Wrap(string name, string body) => $"<{name}>\n{body}\n</{name}>";

    private static void HiddenPrompt()
    {
        // codemode hides the direct declarations of read and bash: they stay selected but leave tools, rules and the skills hint.
        SectionsEqual([
            ("preamble", Preamble),
            ("tools", Wrap("tools", "- edit: Make precise file edits\n- write: Create or overwrite files\n- codemode: Run a script that calls tools" + ToolsTail)),
            ("rules", Wrap("rules", "- Prefer codemode for multi-step tool work.\n- Be concise in your responses\n- Show file paths clearly when working with files")),
            ("docs", Wrap("docs", Docs)),
            ("skills", Wrap("skills", "The following skills provide specialized instructions for specific tasks.\nLoad a skill's file when the task matches its description.\n" + SkillsBody)),
            ("cwd", Wrap("cwd", Cwd.Replace('\\', '/')))
        ], Sections(Admission("read", "bash", "unselected"), "read", "bash", "edit", "write", "codemode"), "hidden readers");
        // Without hidden declarations, v0.99.1 rules are unchanged apart from the docs line.
        SectionsEqual([
            ("preamble", Preamble),
            ("tools", Wrap("tools", "- read: Read file contents\n- bash: Execute bash commands\n- edit: Make precise file edits\n- write: Create or overwrite files\n- codemode: Run a script that calls tools" + ToolsTail)),
            ("rules", Wrap("rules", "- Use bash for file operations like ls, rg, find\n- Use read to examine files instead of cat or sed.\n- Prefer bash for builds.\n- Prefer codemode for multi-step tool work.\n- Be concise in your responses\n- Show file paths clearly when working with files")),
            ("docs", Wrap("docs", Docs)),
            ("skills", Wrap("skills", "The following skills provide specialized instructions for specific tasks.\nUse the read tool to load a skill's file when the task matches its description.\n" + SkillsBody)),
            ("cwd", Wrap("cwd", Cwd.Replace('\\', '/')))
        ], Sections(Admission(), "read", "bash", "edit", "write", "codemode"), "no hidden tools");
    }

    private static void SkillsHint()
    {
        var bash = Sections(Admission("read"), "read", "bash");
        Equal(Wrap("skills", "The following skills provide specialized instructions for specific tasks.\nUse bash to load a skill's file when the task matches its description.\n" + SkillsBody),
            bash.Single(row => row.Key == "skills").Value, "hidden read falls back to declared bash");
        Equal(Wrap("rules", "- Use bash for file operations like ls, rg, find\n- Prefer bash for builds.\n- Be concise in your responses\n- Show file paths clearly when working with files"),
            bash.Single(row => row.Key == "rules").Value, "hidden read guideline dropped");
        Check(!Sections(Admission(), "edit").Any(row => row.Key == "skills"), "Skills without any reader.");
        Check(!Sections(Admission("edit"), "edit").Any(row => row.Key == "skills"), "Hidden non-reader produced a skills hint.");
        var custom = OriginalSystemPromptBuilder.Sections(OriginalSystemPromptBuilder.Capture(Admission("read") with { CustomPrompt = "Custom." }, Cwd, ["read"]));
        Names(["preamble", "skills", "cwd"], custom.Select(row => row.Key), "custom prompt sections");
        Check(custom[1].Value.Contains("\nLoad a skill's file when the task matches its description.\n", StringComparison.Ordinal), "Custom prompt skills hint not indirect.");
    }

    private static void DocsAndOptions()
    {
        var snapshot = OriginalSystemPromptBuilder.Capture(Admission("bash"), Cwd, ["read", "bash"]);
        var options = snapshot.Options.Value;
        Names(["bash"], options.GetProperty("hiddenTools").EnumerateArray().Select(value => value.GetString()!), "options hiddenTools");
        Names(["selectedTools", "hiddenTools", "toolSnippets"], options.EnumerateObject().Take(3).Select(property => property.Name), "options order");
        var unhidden = OriginalSystemPromptBuilder.Capture(Admission(), Cwd, ["read"]).Options.Value;
        Equal(0, unhidden.GetProperty("hiddenTools").GetArrayLength(), "default hiddenTools");
        Throws<ArgumentException>(() => OriginalSystemPromptBuilder.Capture(Admission() with { HiddenTools = [null!] }, Cwd, ["read"]), "null hidden tool");
    }

    private static void RegistryHidden()
    {
        SessionPromptSectionRequest? captured = null; var prepared = 0;
        var registry = Registry([Tool("read"), Tool("bash"), Tool("edit"), Tool("codemode", prepare: loadout =>
        {
            prepared++;
            return new() { HiddenDeclarations = ["bash", "read"] };
        })], new()
        {
            PreparePromptSections = request => { captured = request; return null; },
            ReportLoadoutDiagnostic = (_, error) => throw new InvalidOperationException("Prompt evaluation reported a loadout diagnostic.", error)
        });
        _ = registry.PreparePromptSectionMessage(["read", "bash", "edit", "codemode"], [], null, 0, default);
        Names(["read", "bash", "edit", "codemode"], captured!.SelectedTools, "selected");
        Names(["read", "bash"], captured.HiddenTools, "hidden in registry order");
        Equal(1, prepared, "loadout preparations");
        _ = registry.PreparePromptSectionMessage(["read", "edit"], [], null, 0, default);
        Names([], captured.HiddenTools, "inactive preparer hides nothing");
        var presentation = registry.PrepareActiveLoadout(["read", "bash", "edit", "codemode"], default)!; Equal(2, prepared, "explicit preparation");
        _ = registry.PreparePromptSectionMessage(["read", "bash", "edit", "codemode"], [], null, 0, default, presentation);
        Equal(2, prepared, "a request's prepared loadout is reused"); Names(["read", "bash"], captured.HiddenTools, "reused hidden declarations");
        var failing = Registry([Tool("read"), Tool("codemode", prepare: _ => throw new InvalidOperationException("boom"))], new()
        {
            PreparePromptSections = request => { captured = request; return null; },
            ReportLoadoutDiagnostic = (_, error) => throw new InvalidOperationException("Prompt evaluation reported a loadout diagnostic.", error)
        });
        _ = failing.PreparePromptSectionMessage(["read", "codemode"], [], null, 0, default);
        Names([], captured.HiddenTools, "failed preparer hides nothing and reports nothing twice");
    }
}
