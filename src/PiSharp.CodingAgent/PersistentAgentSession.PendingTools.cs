// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/agent-session.ts (_pendingToolNames).
using System.Collections.Immutable;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;

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
    /// Source _restoreToolsFromTranscript and the constructor's initial names (_buildRuntime) apply the loadout in memory only:
    /// opening a session or navigating the tree writes nothing, and the next run records the loadout (agent-loop
    /// declareToolChanges: a system message before the prompt's messages). While this is set, the agent's loadout
    /// (<see cref="_configuration"/>) is the restored one and the transcript still declares the recorded one, whose tools may be
    /// unregistered, hidden or declared differently now. The next request boundary records the loadout
    /// (<see cref="PrepareActivationRequestAsync"/>), as does any earlier record of the whole loadout (a selection, a catalog
    /// publication); until then every transcript resolution applies that record (<see cref="WithUnrecordedLoadout(ImmutableArray{TranscriptEntry}, ImmutableArray{string}, CancellationToken, int?)"/>).
    /// </summary>
    private bool _unrecordedLoadout;

    /// <summary>Source _restoreToolsFromTranscript at session open: the restored loadout replaces the pending set; it is recorded at
    /// the next request boundary when it differs from the recorded one.</summary>
    private void RestoreUnrecordedTools(bool unrecorded, ImmutableArray<string> pending)
    {
        lock (_gate) { _unrecordedLoadout = unrecorded; _pendingToolNames = pending; }
    }

    /// <summary>
    /// Source createAgentSession for an existing session (sdk.ts initialActiveToolNames: the selected or default tool names) and the
    /// constructor's _buildRuntime: the names become the agent's loadout in memory and the session file is not written; the next
    /// request records the loadout when it differs from the recorded one. Unknown, hidden and capped-out names are ignored, as
    /// <see cref="SetActiveToolsAsync"/> ignores them; a loadout that deactivates a tool drops pending tools, as a selection does.
    /// Requires the idle session, like <see cref="ConfigureAsync"/>.
    /// </summary>
    public async Task ApplyInitialToolsAsync(ImmutableArray<string> names, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource idle;
        lock (_gate)
        {
            ThrowAvailable(); ThrowUserBashMutationLocked();
            if (_registry is null) throw Error(PersistentAgentSessionFailure.InvalidConfiguration);
            if (_active is not null || _inputSubmission is not null) throw new InvalidOperationException("Session is already processing.");
            var snapshot = _agent.Snapshot;
            if (!snapshot.PendingInputs.IsEmpty || snapshot.SteeringCount != 0 || snapshot.FollowUpCount != 0)
                throw Error(PersistentAgentSessionFailure.InvalidConfiguration);
            cancellationToken.ThrowIfCancellationRequested();
            idle = new(TaskCreationOptions.RunContinuationsAsynchronously); _active = idle; _configuring = true;
        }
        var commitHeld = false; var prior = _configurationCallback.Value; _configurationCallback.Value = idle;
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closing.Token);
            var work = cancellation.Token;
            await _commits.WaitAsync(work).ConfigureAwait(false); commitHeld = true;
            await DrainLoadoutDiagnosticsAsync(work).ConfigureAwait(false);
            SessionContextProjection context; AgentConfiguration configuration; SessionRuntimeRegistry registry;
            lock (_gate) { context = _context; configuration = _configuration; registry = _registry!; }
            var selected = registry.NormalizeActiveTools(names, work);
            var current = configuration.Tools.Select(tool => tool.Name).ToImmutableArray();
            if (selected.SequenceEqual(current, StringComparer.Ordinal))
            {
                lock (_gate) { work.ThrowIfCancellationRequested(); if (_pendingActivation is not null) PrepareActivationRestoration(_configuration)(); }
                return;
            }
            // Resolve the loadout as the next request records it: the recorded names removed and the selection declared.
            var resolved = context with { LlmMessages = WithLoadoutRecord(registry, context.LlmMessages, selected, work) };
            var selection = await PrepareAndDrainLoadoutAsync(() => registry.Resolve(resolved, configuration.Model, work, tolerated: _toleratedSelection, thinkingLevel: KeptThinking(configuration)), work).ConfigureAwait(false);
            var messages = SessionContextProjector.AgentMessages(context);
            await using (var probe = new PiSharp.Agent.Agent(selection.Configuration, _clock, new NoopSink(), _agentOptions))
                probe.ConfigureAndReplaceMessages(selection.Configuration, messages);
            work.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!ReferenceEquals(_context, context) || !ReferenceEquals(_configuration, configuration) || !ReferenceEquals(_active, idle))
                    throw new InvalidOperationException("Initial tool reservation changed.");
                var restoreActivation = PrepareActivationRestoration(selection.Configuration);
                _agent.ConfigureAndReplaceMessages(RecoveryConfiguration(selection.Configuration), messages);
                SelectPendingToolsLocked(current, selection.Configuration.Tools.Select(tool => tool.Name).ToImmutableArray());
                _configuration = selection.Configuration;
                _unrecordedLoadout = true;
                restoreActivation();
            }
        }
        finally
        {
            if (commitHeld) _commits.Release();
            lock (_gate) if (ReferenceEquals(_active, idle)) { _active = null; _configuring = false; }
            idle.TrySetResult();
            _configurationCallback.Value = prior;
        }
    }

    /// <summary>The transcript as a registry resolves it while the restored loadout awaits its record: the record the next request
    /// writes (the recorded names removed, <paramref name="names"/> declared with their current bindings) is inserted at
    /// <paramref name="at"/> (the end by default), so the resolution yields the in-memory loadout. Unchanged otherwise.</summary>
    private ImmutableArray<TranscriptEntry> WithUnrecordedLoadout(ImmutableArray<TranscriptEntry> messages, ImmutableArray<string> names,
        CancellationToken token, int? at = null)
    {
        SessionRuntimeRegistry registry;
        lock (_gate) { if (!_unrecordedLoadout || _registry is null) return messages; registry = LoadoutRegistry(); }
        return WithLoadoutRecord(registry, messages, names, token, at);
    }

    private SessionContextProjection WithUnrecordedLoadout(SessionContextProjection context, ImmutableArray<string> names, CancellationToken token)
    {
        var messages = WithUnrecordedLoadout(context.LlmMessages, names, token);
        return messages == context.LlmMessages ? context : context with { LlmMessages = messages };
    }

    private static ImmutableArray<TranscriptEntry> WithLoadoutRecord(SessionRuntimeRegistry registry, ImmutableArray<TranscriptEntry> messages,
        ImmutableArray<string> names, CancellationToken token, int? at = null)
    {
        var index = at ?? messages.Length;
        var recorded = new SessionSystemReplay().Replay(messages[..index], token).Tools
            .Select(tool => tool.Value.GetProperty("name").GetString()!).ToImmutableArray();
        return messages.Insert(index, registry.CreateActivationMessage(names, recorded, 0, token, replaceDeclarations: true)!);
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
