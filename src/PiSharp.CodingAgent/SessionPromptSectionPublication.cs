using System.Collections.Immutable;
using PiSharp.Contracts;

namespace PiSharp.CodingAgent;

/// <summary>Pure explicitly admitted base-prompt preparation; no forced request projection.</summary>
public sealed record SessionPromptSectionRequest(ImmutableArray<string> SelectedTools, JsonData? PriorSystem)
{
    /// <summary>Registered tools whose declarations the active loadout hides (prepareLoadout hidden declarations), in registry order.</summary>
    public ImmutableArray<string> HiddenTools { get; init; } = [];
}
public sealed record SessionPromptSectionPreparation(object Revision,
    ImmutableArray<KeyValuePair<string, string>> Sections, Action ValidateSource);

public sealed partial class PersistentAgentSession
{
    private object? _acknowledgedPromptRevision;
}
