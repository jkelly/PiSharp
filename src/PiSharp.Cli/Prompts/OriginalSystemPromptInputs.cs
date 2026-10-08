using System.Collections.Immutable;
using PiSharp.Contracts;

namespace PiSharp.Cli.Prompts;

internal sealed record OriginalPromptContextFile(string Path, string Content);
internal sealed record OriginalPromptSkill(string Name, string Description, string FilePath, string BaseDir,
    JsonData SourceInfo, bool DisableModelInvocation);
internal sealed record OriginalPromptDocumentation(string Readme, string Docs, string Examples);

// Immutable, caller-admitted construction inputs. These values grant no read or tool authority.
internal sealed record OriginalSystemPromptAdmission
{
    internal string? CustomPrompt { get; init; }
    internal string? ForceSystemPrompt { get; init; }
    internal ImmutableArray<string> SelectedTools { get; init; }
    internal ImmutableArray<KeyValuePair<string, string>> ToolSnippets { get; init; } = [];
    internal ImmutableArray<KeyValuePair<string, ImmutableArray<string>>> ToolGuidelines { get; init; } = [];
    internal ImmutableArray<string> PromptGuidelines { get; init; } = [];
    internal string AppendSystemPrompt { get; init; } = "";
    internal ImmutableArray<KeyValuePair<string, string>> Sections { get; init; } = [];
    internal ImmutableArray<OriginalPromptContextFile> ContextFiles { get; init; } = [];
    internal ImmutableArray<OriginalPromptSkill> Skills { get; init; } = [];
    internal OriginalPromptDocumentation? Documentation { get; init; }
}

internal sealed record OriginalSystemPromptSnapshot(OriginalSystemPromptAdmission Input, string Cwd,
    ImmutableArray<string> SelectedTools, bool NativeLiteralBaseline)
{
    internal JsonData Options => OriginalSystemPromptBuilder.Options(this);
}
