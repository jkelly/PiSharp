using System.Collections.Immutable;
using PiSharp.Contracts;

namespace PiSharp.CodingAgent;

/// <summary>Pure explicitly admitted base-prompt preparation; no forced request projection.</summary>
public sealed record SessionPromptSectionRequest(ImmutableArray<string> SelectedTools, JsonData? PriorSystem);
public sealed record SessionPromptSectionPreparation(object Revision,
    ImmutableArray<KeyValuePair<string, string>> Sections, Action ValidateSource);

public sealed partial class PersistentAgentSession
{
    private object? _acknowledgedPromptRevision;
}
