// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/modes/rpc/rpc-mode.ts (bash, abort_bash)
// and packages/coding-agent/src/core/agent-session.ts (executeBash, bash_execution_update).
using System.Runtime.CompilerServices;
using PiSharp.CodingAgent;
using PiSharp.CodingAgent.Execution;
using PiSharp.Contracts;

namespace PiSharp.Rpc.Protocol;

public sealed partial class RpcSessionDispatcher
{
    private readonly IUserBashExecutor? _userBash;
    private readonly ConditionalWeakTable<PersistentAgentSession, object> _userBashSessions = new();

    /// <summary>Captures the host's user Bash capability on an idle session; a busy session is configured at its first bash command.</summary>
    private bool TryConfigureUserBash(PersistentAgentSession session, bool required)
    {
        if (_userBash is null || _userBashSessions.TryGetValue(session, out _)) return _userBash is not null;
        try { session.ConfigureUserBashExecution(_userBash, ObserveUserBashAsync); }
        catch (InvalidOperationException) when (!required) { return false; }
        _userBashSessions.AddOrUpdate(session, session); return true;
    }

    private Task ObserveUserBashAsync(UserBashExecutionUpdate update) => WriteAsync(RpcCommandCodec.Event("bash_execution_update", writer =>
    {
        if (update.Id is not null) writer.WriteString("id", update.Id);
        writer.WriteString("delta", update.Delta);
    }, _options));

    private async Task<JsonData?> UserBashAsync(RpcCommandEnvelope command, CancellationToken token)
    {
        if (_userBash is null) throw new RpcCommandException(command.Id, command.Type, "User bash execution is unavailable from this host.");
        PersistentAgentSession session;
        await _transitions.WaitAsync(token).ConfigureAwait(false);
        try { CheckOpen(); session = _session; if (command.Type == "bash") TryConfigureUserBash(session, required: true); }
        finally { _transitions.Release(); }
        if (command.Type == "abort_bash")
        {
            // Source abortBash only signals; each bash command still answers with its own (cancelled) result.
            _ = ObserveCommandAsync(session.AbortUserBashAsync());
            return null;
        }
        var excluded = command.Mode switch { "exclude" => true, "include" => (bool?)false, _ => null };
        var result = await session.ExecuteUserBashAsync(command.Message!, excluded, command.Id, token).ConfigureAwait(false);
        return UserBash.RpcData(result);
    }
}
