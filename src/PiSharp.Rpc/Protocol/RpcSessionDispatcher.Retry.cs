using PiSharp.CodingAgent;
using PiSharp.Contracts;

namespace PiSharp.Rpc.Protocol;

public sealed partial class RpcSessionDispatcher
{
    private async Task<JsonData?> RetryCommandAsync(RpcCommandEnvelope command, AgentSessionAttachment? attachment, CancellationToken token)
    {
        PersistentAgentSession session;
        await _transitions.WaitAsync(token).ConfigureAwait(false);
        try
        {
            CheckOpen(); token.ThrowIfCancellationRequested();
            if (attachment is not null && !ReferenceEquals(_sessionOwner!.Current, attachment))
                throw new RpcCommandException(command.Id, command.Type, "Retry command belongs to a retired attachment.");
            session = attachment?.Session ?? _session;
            _ = RpcCommandCodec.Success(command, null, _options);
        }
        finally { _transitions.Release(); }
        if (command.Type == "abort_retry") await session.AbortRetryAsync().ConfigureAwait(false);
        else if (attachment is null) await session.SetAutoRetryEnabledAsync(command.Mode == "enabled", token).ConfigureAwait(false);
        else await _sessionOwner!.SetAutoRetryEnabledAsync(attachment, command.Mode == "enabled", token).ConfigureAwait(false);
        return null;
    }
    private async ValueTask ObserveRetryAsync(SessionOperationEvent observation)
    {
        var previous = _inCallback.Value; _inCallback.Value = true;
        try
        {
            var started = observation as SessionAutoRetryStarted;
            var ended = observation as SessionAutoRetryEnded;
            if (started is null && ended is null) throw new InvalidOperationException("Unsupported retry event.");
            await WriteAsync(RpcCommandCodec.Event(started is null ? "auto_retry_end" : "auto_retry_start", writer =>
            {
                if (started is not null)
                {
                    writer.WriteNumber("attempt", started.Attempt); writer.WriteNumber("maxAttempts", started.MaxAttempts);
                    writer.WriteNumber("delayMs", started.DelayMs); writer.WriteString("errorMessage", started.ErrorMessage);
                }
                else
                {
                    writer.WriteBoolean("success", ended!.Success); writer.WriteNumber("attempt", ended.Attempt);
                    if (ended.FinalError is not null) writer.WriteString("finalError", ended.FinalError);
                }
            }, _options)).ConfigureAwait(false);
        }
        catch (Exception error) { SignalFatal(error is RpcDispatchException dispatch ? dispatch.Failure : RpcDispatchFailure.SessionRunFailed, error); throw; }
        finally { _inCallback.Value = previous; }
    }
}
