// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/coding-agent/src/core/tools/{read,bash,edit,write,grep,find,ls}.ts
// (*ToolSystemPromptContribution).
using System.Collections.Immutable;

namespace PiSharp.Tools;

/// <summary>Source prompt snippets and guidelines of the built-in tools, for the system prompt's tool list and rules.</summary>
public static class BuiltinToolPrompts
{
    /// <summary>Source promptSnippet per tool name.</summary>
    public static ImmutableDictionary<string, string> Snippets { get; } = new Dictionary<string, string>
    {
        ["read"] = "Read file contents",
        ["bash"] = "Execute bash commands (ls, grep, find, etc.)",
        ["edit"] = "Make precise file edits with exact text replacement, including multiple disjoint edits in one call",
        ["write"] = "Create or overwrite files",
        ["grep"] = "Search file contents for patterns (respects .gitignore)",
        ["find"] = "Find files by glob pattern (respects .gitignore)",
        ["ls"] = "List directory contents",
    }.ToImmutableDictionary(StringComparer.Ordinal);

    /// <summary>Source promptGuidelines per tool name. Bash's guideline applies only while session variables are exposed.</summary>
    public static ImmutableDictionary<string, ImmutableArray<string>> Guidelines { get; } = new Dictionary<string, ImmutableArray<string>>
    {
        ["read"] = ["Use read to examine files instead of cat or sed."],
        ["bash"] = BashGuidelines(exposeSessionEnvironment: true),
        ["edit"] =
        [
            "Use edit for targeted changes. Batch changes to one file in a single call.",
            "Each edits[].oldText must exactly and uniquely match the original file. Keep matches short and non-overlapping; merge nearby changes without including large unchanged regions."
        ],
        ["write"] = ["Use write only for new files or complete rewrites."],
        ["grep"] = [],
        ["find"] = [],
        ["ls"] = [],
    }.ToImmutableDictionary(StringComparer.Ordinal);

    /// <summary>Source createShellToolDefinition: <c>exposeSessionEnvironment &amp;&amp; config.promptGuidelines</c>.</summary>
    public static ImmutableArray<string> BashGuidelines(bool exposeSessionEnvironment) => exposeSessionEnvironment
        ? ["You can inspect PI_* environment variables for current model and session details."] : [];

    /// <summary>Source DEFAULT_TOOL_NAMES: tools enabled at startup when defaultTools does not change them.</summary>
    public static ImmutableArray<string> DefaultToolNames { get; } = ["read", "bash", "edit", "write"];
}
