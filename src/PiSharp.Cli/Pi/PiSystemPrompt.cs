// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/agent-session.ts (_rebuildSystemPrompt: tool snippets
// and guidelines of the registered definitions, the loader's system and append prompts, skills and context files),
// packages/coding-agent/src/core/system-prompt.ts and packages/coding-agent/src/config.ts (getReadmePath, getDocsPath, getExamplesPath).
using System.Collections.Immutable;
using PiSharp.Cli.Prompts;
using PiSharp.CodingAgent.Resources.Skills;

namespace PiSharp.Cli.Pi;

/// <summary>Builds the Pi system prompt inputs for a run. The sections themselves are rendered by
/// <see cref="OriginalSystemPromptBuilder"/>; the session adds the <c>mcp_servers</c> section at each prompt start.</summary>
internal static class PiSystemPrompt
{
    /// <summary>Source promptSnippet of every tool PiSharp registers natively: the built-in tools (tools/*.ts), codemode
    /// (extensions/codemode/tool.ts) and tool_search (extensions/tool-search/tool.ts).</summary>
    internal static ImmutableArray<KeyValuePair<string, string>> ToolSnippets { get; } =
    [
        .. PiSharp.Tools.BuiltinToolPrompts.Snippets.OrderBy(pair => pair.Key, StringComparer.Ordinal),
        KeyValuePair.Create(PiSharp.Codemode.CodemodeToolDefinition.Name, PiSharp.Codemode.CodemodeToolDefinition.PromptSnippet),
        KeyValuePair.Create("tool_search", PiSharp.Extensions.Mcp.Discovery.ToolSearch.PromptSnippet)
    ];

    /// <summary>Source promptGuidelines of the same tools. PiSharp's bash exposes the PI_* session variables, so its guideline applies.</summary>
    internal static ImmutableArray<KeyValuePair<string, ImmutableArray<string>>> ToolGuidelines { get; } =
    [
        .. PiSharp.Tools.BuiltinToolPrompts.Guidelines.Where(pair => pair.Value.Length > 0).OrderBy(pair => pair.Key, StringComparer.Ordinal),
        KeyValuePair.Create(PiSharp.Codemode.CodemodeToolDefinition.Name, PiSharp.Codemode.CodemodeToolDefinition.PromptGuidelines)
    ];

    /// <summary>Source getReadmePath/getDocsPath/getExamplesPath: PiSharp's documentation next to the application.</summary>
    internal static OriginalPromptDocumentation Documentation(string? applicationDirectory = null)
    {
        var root = applicationDirectory ?? AppContext.BaseDirectory;
        return new(Path.Join(root, "README.md"), Path.Join(root, "docs"), Path.Join(root, "examples"));
    }

    /// <summary>The admission for a run: custom or default prompt, the appended text joined by blank lines, context files and the
    /// loaded skills. Tool names are filled in by the session from its active tools.</summary>
    internal static OriginalSystemPromptAdmission Admission(PiResources resources, SkillResourceSet? skills, string? applicationDirectory = null) => new()
    {
        CustomPrompt = string.IsNullOrEmpty(resources.SystemPrompt) ? null : resources.SystemPrompt,
        SelectedTools = [],
        ToolSnippets = ToolSnippets,
        ToolGuidelines = ToolGuidelines,
        AppendSystemPrompt = resources.AppendSystemPrompt.Length > 0 ? string.Join("\n\n", resources.AppendSystemPrompt) : "",
        ContextFiles = [.. resources.ContextFiles.Select(file => new OriginalPromptContextFile(file.Path, file.Content))],
        Skills = skills is null ? [] : [.. skills.Skills.Select(Skill)],
        Documentation = Documentation(applicationDirectory)
    };

    private static OriginalPromptSkill Skill(SkillResource skill) => new(skill.Name, skill.Description, skill.FilePath, skill.BaseDir,
        PiSharp.Contracts.JsonData.Parse(System.Text.Json.JsonSerializer.Serialize(new
        {
            path = skill.SourceInfo.Path, source = skill.SourceInfo.Source,
            scope = skill.SourceInfo.Scope.ToString().ToLowerInvariant(),
            origin = skill.SourceInfo.Origin == PiSharp.CodingAgent.Resources.PromptTemplateSourceOrigin.TopLevel ? "top-level" : "package",
            baseDir = skill.SourceInfo.BaseDir
        })), skill.DisableModelInvocation);
}
