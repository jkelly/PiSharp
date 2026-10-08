// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/agent-session.ts (AgentSessionEvent) and
// packages/coding-agent/src/modes/json-event.ts (toJsonEvent: session events other than message_update are emitted as is).
using PiSharp.CodingAgent;
using PiSharp.Contracts;

namespace PiSharp.Rpc.Protocol;

/// <summary>Shared RPC and JSON-mode wire records for session-level events (not agent loop events). Null means the
/// observation has no source session event.</summary>
public static class RpcSessionEventProjector
{
    public static JsonData? Project(SessionOperationEvent observation, RpcDispatchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var configured = options ?? new();
        return observation switch
        {
            SessionEntryAppended appended => RpcCommandCodec.SourceEvent("entry_appended", writer =>
                RpcCommandCodec.Raw(writer, "entry", appended.Entry.WireBody), configured),
            SessionThinkingLevelChanged changed => RpcCommandCodec.SourceEvent("thinking_level_changed", writer =>
                writer.WriteString("level", changed.Level), configured),
            SessionInfoChanged info => RpcCommandCodec.SourceEvent("session_info_changed", writer =>
            { if (info.Name is not null) writer.WriteString("name", info.Name); }, configured),
            SessionSummarizationRetryScheduled scheduled => RpcCommandCodec.SourceEvent("summarization_retry_scheduled", writer =>
            {
                writer.WriteNumber("attempt", scheduled.Attempt); writer.WriteNumber("maxAttempts", scheduled.MaxAttempts);
                writer.WriteNumber("delayMs", scheduled.DelayMs); writer.WriteString("errorMessage", scheduled.ErrorMessage);
            }, configured),
            SessionSummarizationRetryAttemptStarted started => RpcCommandCodec.SourceEvent("summarization_retry_attempt_start", writer =>
            {
                writer.WriteString("source", started.Source);
                if (started.Reason is { } reason) writer.WriteString("reason", SessionSummarizationRetryAttemptStarted.ReasonText(reason));
            }, configured),
            SessionSummarizationRetryFinished => RpcCommandCodec.SourceEvent("summarization_retry_finished", null, configured),
            SessionAutoRetryStarted started => RpcCommandCodec.SourceEvent("auto_retry_start", writer =>
            {
                writer.WriteNumber("attempt", started.Attempt); writer.WriteNumber("maxAttempts", started.MaxAttempts);
                writer.WriteNumber("delayMs", started.DelayMs); writer.WriteString("errorMessage", started.ErrorMessage);
            }, configured),
            SessionAutoRetryEnded ended => RpcCommandCodec.SourceEvent("auto_retry_end", writer =>
            {
                writer.WriteBoolean("success", ended.Success); writer.WriteNumber("attempt", ended.Attempt);
                if (ended.FinalError is not null) writer.WriteString("finalError", ended.FinalError);
            }, configured),
            SessionCompactionStarted started => OriginalCompactionEventProjector.Start(started.Reason, configured),
            SessionCompactionEnded ended => OriginalCompactionEventProjector.End(ended.Reason, ended.Result, ended.Aborted, ended.WillRetry,
                ended.ErrorMessage, configured),
            SessionOperationSettled settled => RpcCommandCodec.SourceEvent("agent_settled", writer => writer.WriteBoolean("aborted", settled.Aborted), configured),
            _ => null
        };
    }

    /// <summary>Source extension_error record (RPC onError): <c>{type, extensionPath, event, error}</c>.</summary>
    public static JsonData ExtensionError(string extensionPath, string eventName, string error, RpcDispatchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(extensionPath); ArgumentNullException.ThrowIfNull(eventName); ArgumentNullException.ThrowIfNull(error);
        return RpcCommandCodec.SourceEvent("extension_error", writer =>
        {
            writer.WriteString("extensionPath", extensionPath); writer.WriteString("event", eventName); writer.WriteString("error", error);
        }, options ?? new());
    }
}
