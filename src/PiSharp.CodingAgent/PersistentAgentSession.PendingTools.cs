// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/agent-session.ts (_pendingToolNames).
using System.Collections.Immutable;

namespace PiSharp.CodingAgent;

public sealed partial class PersistentAgentSession
{
    private ImmutableArray<string> _pendingToolNames = [];

    /// <summary>
    /// Source pending tools: names that were active when a reload (or another owned retirement publication) replaced the
    /// catalog but are not registered in it, such as MCP tools whose server connects later, and names of the loadout restored
    /// from the transcript (session open without initial names, tree navigation) that were left out because they are not
    /// registered yet or are now hidden. A later catalog publication that registers (or unhides) one activates it. Running a
    /// prompt drops them, as does a selection that deactivates an active tool; a tool the user disabled is never pending, so
    /// it is not resurrected. In-memory only, like the source.
    /// </summary>
    public ImmutableArray<string> PendingToolNames { get { lock (_gate) return _pendingToolNames; } }

    /// <summary>
    /// Source _restoreToolsFromTranscript: the restored loadout replaces the pending set. Unlike the source, which records the
    /// loadout at the next prompt, the restored loadout (registered names with their current declarations) is recorded before
    /// the session is used, so every later transcript resolution sees only bound declarations.
    /// </summary>
    private async Task RecordRestoredToolsAsync(ImmutableArray<string> active, ImmutableArray<string> pending, CancellationToken token)
    {
        await ConfigureAsync(new() { ActiveToolNames = active, ReplaceDeclarations = true }, token).ConfigureAwait(false);
        lock (_gate) _pendingToolNames = pending;
    }

    /// <summary>The names a catalog publication requests (its own, then pending ones) and the pending candidates it considers.</summary>
    private (ImmutableArray<string> Requested, ImmutableArray<string> Candidates) PendingToolRequestLocked(SessionRuntimeRegistry replacement,
        ImmutableArray<string> activeNames, bool restorePrevious)
    {
        var lifetime = replacement.LifetimeToolSelection;
        var previous = restorePrevious
            ? _pendingActivation?.Names ?? _configuration.Tools.Select(tool => tool.Name).ToImmutableArray()
            : ImmutableArray<string>.Empty;
        // Source _isAllowedTool: --tools/--exclude-tools keep a name from ever becoming pending.
        var candidates = _pendingToolNames.Concat(previous).Where(name => lifetime?.IsAllowed(name) != false)
            .Distinct(StringComparer.Ordinal).ToImmutableArray();
        return (activeNames.IsDefault ? activeNames : activeNames.Concat(candidates).Distinct(StringComparer.Ordinal).ToImmutableArray(), candidates);
    }

    /// <summary>Source _setActiveTools: activated names leave the pending set.</summary>
    private void RetirePendingToolsLocked(ImmutableArray<string> candidates, ImmutableArray<string> selected) =>
        _pendingToolNames = candidates.Where(name => !selected.Contains(name, StringComparer.Ordinal)).ToImmutableArray();

    /// <summary>Source setActiveToolsByName: a selection that deactivates an active tool replaces the restored loadout and
    /// drops pending tools; one that only adds tools keeps them.</summary>
    private void SelectPendingToolsLocked(ImmutableArray<string> previous, ImmutableArray<string> selected)
    {
        if (_pendingToolNames.IsEmpty) return;
        _pendingToolNames = previous.Any(name => !selected.Contains(name, StringComparer.Ordinal)) ? []
            : _pendingToolNames.Where(name => !selected.Contains(name, StringComparer.Ordinal)).ToImmutableArray();
    }
}
