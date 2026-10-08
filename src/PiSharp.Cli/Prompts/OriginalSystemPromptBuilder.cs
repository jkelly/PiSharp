using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using PiSharp.Contracts;

namespace PiSharp.Cli.Prompts;

internal static class OriginalSystemPromptBuilder
{
    private static readonly Regex SectionName = new("^[a-z][a-z0-9_-]*$", RegexOptions.CultureInvariant);
    internal static OriginalSystemPromptSnapshot Capture(OriginalSystemPromptAdmission input, string cwd,
        ImmutableArray<string> selected, bool literal = false)
    {
        ArgumentNullException.ThrowIfNull(input); ArgumentException.ThrowIfNullOrEmpty(cwd);
        if (!System.IO.Path.IsPathFullyQualified(cwd) || selected.IsDefault)
            throw new ArgumentException("Prompt construction requires actual absolute cwd and admitted tool names.");
        static ImmutableArray<T> Copy<T>(ImmutableArray<T> values) => values.IsDefault ? [] : values.ToArray().ToImmutableArray();
        var frozen = input with
        {
            SelectedTools = Copy(selected), ToolSnippets = Copy(input.ToolSnippets),
            ToolGuidelines = Copy(input.ToolGuidelines).Select(row => KeyValuePair.Create(row.Key, Copy(row.Value))).ToImmutableArray(),
            PromptGuidelines = Copy(input.PromptGuidelines), Sections = Copy(input.Sections),
            ContextFiles = Copy(input.ContextFiles).Select(row => row with { }).ToImmutableArray(),
            Skills = Copy(input.Skills).Select(row => row with { SourceInfo = JsonData.FromElement(row.SourceInfo.Value) }).ToImmutableArray()
        };
        static void Unique(IEnumerable<string> names)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in names) if (name is null || !seen.Add(name)) throw new ArgumentException("Duplicate or null prompt map key.");
        }
        Unique(frozen.ToolSnippets.Select(row => row.Key)); Unique(frozen.ToolGuidelines.Select(row => row.Key));
        Unique(frozen.Sections.Select(row => row.Key));
        foreach (var row in frozen.Sections)
            if (!SectionName.IsMatch(row.Key) || row.Key == "preamble" || row.Value is null)
                throw new ArgumentException("Invalid system prompt section name or content.");
        if (frozen.AppendSystemPrompt is null || frozen.PromptGuidelines.Any(value => value is null) ||
            frozen.ToolSnippets.Any(row => row.Value is null) || frozen.ToolGuidelines.Any(row => row.Value.Any(value => value is null)) ||
            frozen.ContextFiles.Any(row => row.Path is null || row.Content is null) ||
            frozen.Skills.Any(row => row.Name is null || row.Description is null || row.FilePath is null || row.BaseDir is null))
            throw new ArgumentException("Invalid admitted prompt value.");
        if (!literal && string.IsNullOrEmpty(frozen.CustomPrompt) && frozen.Documentation is null)
            throw new ArgumentException("Original default documentation paths must be explicitly admitted.");
        return new(frozen, cwd, Copy(selected), literal);
    }

    internal static JsonData Options(OriginalSystemPromptSnapshot snapshot)
    {
        var input = snapshot.Input;
        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["selectedTools"] = snapshot.SelectedTools, ["toolSnippets"] = input.ToolSnippets.ToDictionary(row => row.Key, row => row.Value),
            ["toolGuidelines"] = input.ToolGuidelines.ToDictionary(row => row.Key, row => row.Value),
            ["promptGuidelines"] = input.PromptGuidelines, ["appendSystemPrompt"] = input.AppendSystemPrompt,
            ["sections"] = input.Sections.ToDictionary(row => row.Key, row => row.Value), ["cwd"] = snapshot.Cwd,
            ["contextFiles"] = input.ContextFiles.Select(row => new { path = row.Path, content = row.Content }),
            ["skills"] = input.Skills.Select(row => new { name = row.Name, description = row.Description, filePath = row.FilePath,
                baseDir = row.BaseDir, sourceInfo = row.SourceInfo.Value, disableModelInvocation = row.DisableModelInvocation })
        };
        if (input.CustomPrompt is not null) values["customPrompt"] = input.CustomPrompt;
        if (input.ForceSystemPrompt is not null) values["forceSystemPrompt"] = input.ForceSystemPrompt;
        return JsonData.Parse(JsonSerializer.Serialize(values));
    }

    internal static ImmutableArray<KeyValuePair<string, string>> Sections(OriginalSystemPromptSnapshot snapshot)
    {
        var input = snapshot.Input; var sections = new List<KeyValuePair<string, string>>();
        void Put(string name, string value)
        {
            var index = sections.FindIndex(row => row.Key == name);
            if (index < 0) sections.Add(KeyValuePair.Create(name, value)); else sections[index] = KeyValuePair.Create(name, value);
        }
        if (!string.IsNullOrEmpty(input.CustomPrompt)) Put("preamble", input.CustomPrompt);
        else
        {
            Put("preamble", "You are an expert coding assistant operating inside pi, a coding agent harness. You help users by reading files, executing commands, editing code, and writing new files.");
            var snippets = input.ToolSnippets.ToDictionary(row => row.Key, row => row.Value, StringComparer.Ordinal);
            var visible = snapshot.SelectedTools.Where(name => snippets.TryGetValue(name, out var text) && text.Length != 0).ToArray();
            Put("tools", (visible.Length == 0 ? "(none)" : string.Join("\n", visible.Select(name => $"- {name}: {snippets[name]}"))) +
                "\n\nIn addition to the tools above, you may have access to other custom tools depending on the project.");
            var rules = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
            void Rule(string value) { var trimmed = value.Trim(); if (trimmed.Length != 0 && seen.Add(trimmed)) rules.Add(trimmed); }
            var bash = snapshot.SelectedTools.Contains("bash"); var ps = snapshot.SelectedTools.Contains("powershell");
            if ((bash || ps) && !snapshot.SelectedTools.Any(name => name is "grep" or "find" or "ls"))
                Rule(bash && ps ? "Use bash or PowerShell for file operations like listing, searching, and finding files" :
                    ps ? "Use PowerShell for file operations like listing, searching, and finding files" : "Use bash for file operations like ls, rg, find");
            var guidelines = input.ToolGuidelines.ToDictionary(row => row.Key, row => row.Value, StringComparer.Ordinal);
            foreach (var name in snapshot.SelectedTools) if (guidelines.TryGetValue(name, out var list)) foreach (var rule in list) Rule(rule);
            foreach (var rule in input.PromptGuidelines) Rule(rule);
            Rule("Be concise in your responses"); Rule("Show file paths clearly when working with files");
            Put("rules", string.Join("\n", rules.Select(value => "- " + value)));
            var docs = input.Documentation ?? throw new InvalidOperationException("Admitted documentation paths absent.");
            Put("docs", $"Pi documentation (read only when the user asks about pi itself, its SDK, extensions, themes, skills, or TUI):\n- Main documentation: {docs.Readme}\n- Additional docs: {docs.Docs}\n- Examples: {docs.Examples} (extensions, custom tools, SDK)\n- When reading pi docs or examples, resolve docs/... under Additional docs and examples/... under Examples, not the current working directory\n- When asked about: extensions (docs/extensions.md, examples/extensions/), themes (docs/themes.md), skills (docs/skills.md), prompt templates (docs/prompt-templates.md), TUI components (docs/tui.md), keybindings (docs/keybindings.md), SDK integrations (docs/sdk.md), custom providers (docs/custom-provider.md), adding models (docs/models.md), pi packages (docs/packages.md), environment variables (docs/environment-variables.md), MCP servers (docs/mcp.md)\n- When working on pi topics, read the docs and examples, and follow .md cross-references before implementing\n- Always read pi .md files completely and follow links to related docs (e.g., tui.md for TUI API details)");
        }
        if (input.AppendSystemPrompt.Length != 0) Put("addendum", input.AppendSystemPrompt);
        if (input.ContextFiles.Length != 0) Put("project_context", "Project-specific instructions and guidelines:\n\n" +
            string.Join("\n\n", input.ContextFiles.Select(row => $"<project_instructions path=\"{row.Path}\">\n{row.Content}\n</project_instructions>")));
        var read = new[] { "read", "bash" }.FirstOrDefault(name => snapshot.SelectedTools.Contains(name));
        if (read is not null)
        {
            var skills = FormatSkills(input.Skills, read).Trim(); if (skills.Length != 0) Put("skills", skills);
        }
        Put("cwd", snapshot.Cwd.Replace('\\', '/'));
        foreach (var row in input.Sections) if (row.Value.Length != 0) Put(row.Key, row.Value);
        return sections.Select(row => KeyValuePair.Create(row.Key, row.Key == "preamble" ? row.Value : $"<{row.Key}>\n{row.Value}\n</{row.Key}>")).ToImmutableArray();
    }

    private static string FormatSkills(ImmutableArray<OriginalPromptSkill> input, string read)
    {
        var visible = input.Where(row => !row.DisableModelInvocation).ToArray(); if (visible.Length == 0) return "";
        // Exact source wording is kept separate from metadata; no skill file is read here.
        var lines = new List<string>
        {
            "The following skills provide specialized instructions for specific tasks.",
            read == "bash" ? "Use bash to load a skill's file when the task matches its description." :
                "Use the read tool to load a skill's file when the task matches its description.",
            "When a skill file references a relative path, resolve it against the skill directory (parent of SKILL.md / dirname of the path) and use that absolute path in tool commands.",
            "", "<available_skills>"
        };
        foreach (var skill in visible) lines.Add($"  <skill>\n    <name>{Xml(skill.Name)}</name>\n    <description>{Xml(skill.Description)}</description>\n    <location>{Xml(skill.FilePath)}</location>\n  </skill>");
        lines.Add("</available_skills>"); return string.Join("\n", lines);
    }
    private static string Xml(string value) => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");
    internal static JsonData Message(OriginalSystemPromptSnapshot snapshot, long timestamp, bool allowForce = true)
    {
        if (snapshot.NativeLiteralBaseline || allowForce && snapshot.Input.ForceSystemPrompt is not null)
            return JsonData.Parse(JsonSerializer.Serialize(new { role = "system", content = snapshot.NativeLiteralBaseline ? snapshot.Input.CustomPrompt : snapshot.Input.ForceSystemPrompt, timestamp }));
        return JsonData.Parse(JsonSerializer.Serialize(new { role = "system", content = "", timestamp,
            sections = Sections(snapshot).ToDictionary(row => row.Key, row => row.Value, StringComparer.Ordinal) }));
    }
}
