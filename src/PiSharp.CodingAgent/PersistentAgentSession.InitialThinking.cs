// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/sdk.ts.
using System.Collections.Immutable;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Sessions.Context;

namespace PiSharp.CodingAgent;

public sealed partial class PersistentAgentSession
{
    /// <summary>
    /// Source createAgentSession for an existing session: <c>options.thinkingLevel</c> (the CLI's --thinking, clamped to the model)
    /// becomes agent.state.thinkingLevel in memory and the session file is not written (sdk.ts appends a thinking_level_change only
    /// to a session that has none). The branch's recorded level is tolerated, as after tree navigation, until a change names the
    /// session's; responses record the level they ran with. Requires the idle session, like <see cref="ConfigureAsync"/>.
    /// </summary>
    public async Task ApplyInitialThinkingLevelAsync(string level, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(level);
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
            // sdk.ts createAgentSession: options.thinkingLevel is clamped to the model's capabilities (clampThinkingLevel).
            level = ThinkingLevels.Clamp(registry.GetSupportedThinkingLevels(configuration.Model), level);
            if (level == configuration.ThinkingLevel) return;
            var resolved = WithUnrecordedLoadout(context, configuration.Tools.Select(tool => tool.Name).ToImmutableArray(), work);
            var selection = await PrepareAndDrainLoadoutAsync(() => registry.Resolve(resolved, configuration.Model, work,
                tolerated: _toleratedSelection, thinkingLevel: level), work).ConfigureAwait(false);
            var tolerated = context.ThinkingLevel != selection.Configuration.ThinkingLevel ? context.ThinkingLevel : null;
            ValidateRuntimeContext(context, selection.Configuration, _toleratedSelection, tolerated);
            var messages = SessionContextProjector.AgentMessages(context);
            await using (var probe = new PiSharp.Agent.Agent(selection.Configuration, _clock, new NoopSink(), _agentOptions))
                probe.ConfigureAndReplaceMessages(selection.Configuration, messages);
            work.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!ReferenceEquals(_context, context) || !ReferenceEquals(_configuration, configuration) || !ReferenceEquals(_active, idle))
                    throw new InvalidOperationException("Initial thinking reservation changed.");
                _agent.ConfigureAndReplaceMessages(RecoveryConfiguration(selection.Configuration), messages);
                _configuration = selection.Configuration;
                _toleratedThinking = tolerated;
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
}
