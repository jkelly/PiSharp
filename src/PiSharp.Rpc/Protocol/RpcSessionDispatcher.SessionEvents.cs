// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/modes/rpc/rpc-mode.ts (session.subscribe:
// output(toJsonEvent(event)); onError: output({type:"extension_error", ...})).
using PiSharp.CodingAgent;

namespace PiSharp.Rpc.Protocol;

public sealed partial class RpcSessionDispatcher
{
    /// <summary>Session events added for Pi v1.1.0 parity: entry_appended, thinking_level_changed and summarization_retry_*.
    /// Returns false for observations handled elsewhere.</summary>
    private async ValueTask<bool> TryObserveSessionEventAsync(SessionOperationEvent observation)
    {
        if (observation is SessionModelSelected) return true; // Extension-only source event; no wire record.
        if (observation is not (SessionEntryAppended or SessionThinkingLevelChanged or SessionSummarizationRetryScheduled or
            SessionSummarizationRetryAttemptStarted or SessionSummarizationRetryFinished)) return false;
        var previous = _inCallback.Value; _inCallback.Value = true;
        try { await WriteAsync(RpcSessionEventProjector.Project(observation, _options)!).ConfigureAwait(false); }
        catch (Exception error)
        { SignalFatal(error is RpcDispatchException dispatch ? dispatch.Failure : RpcDispatchFailure.SessionRunFailed, error); throw; }
        finally { _inCallback.Value = previous; }
        return true;
    }

    /// <summary>Source RPC onError: one <c>extension_error</c> record per reported extension failure. A dispatcher that is
    /// closing drops the record rather than failing the reporting extension path.</summary>
    public async ValueTask PublishExtensionErrorAsync(string extensionPath, string eventName, string error)
    {
        var record = RpcSessionEventProjector.ExtensionError(extensionPath, eventName, error, _options);
        lock (_gate) if (_closed || _fatal is not null) return;
        var previous = _inCallback.Value; _inCallback.Value = true;
        try { await WriteAsync(record).ConfigureAwait(false); }
        finally { _inCallback.Value = previous; }
    }
}
