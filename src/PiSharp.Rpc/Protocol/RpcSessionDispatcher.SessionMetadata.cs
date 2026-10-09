using System.Text;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Sessions.Compaction;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;

namespace PiSharp.Rpc.Protocol;

public sealed partial class RpcSessionDispatcher
{
    private async Task<JsonData?> SessionMetadataAsync(RpcCommandEnvelope command,
        AgentSessionAttachment? attachment, CancellationToken token)
    {
        PersistentAgentSession session; string sessionId; string name;
        await _transitions.WaitAsync(token).ConfigureAwait(false);
        try
        {
            CheckOpen(); token.ThrowIfCancellationRequested();
            if (attachment is not null && !ReferenceEquals(_sessionOwner!.Current, attachment))
                throw new RpcCommandException(command.Id, command.Type, "Session metadata command belongs to a retired attachment.");
            // Current can publish before AttachmentChanged rebinds the dispatcher; the validated attachment owns this read.
            session = attachment?.Session ?? _session;
            var snapshot = session.Snapshot;
            if (command.Type == "get_session_stats") return SessionStats(command, snapshot, session.Path, token);
            lock (_gate) if (_run is not null)
                throw new RpcCommandException(command.Id, command.Type, "Session is processing or settling; naming requires idle admission.");
            name = SanitizeSessionName(command.Message!);
            if (name.Length == 0) throw new RpcCommandException(command.Id, command.Type, "Session name cannot be empty");
            _ = RpcCommandCodec.Success(command, null, _options); // Budget the complete acknowledgement before durable effects.
            _ = RpcCommandCodec.Event("session_info_changed", writer => writer.WriteString("name", name), _options);
            sessionId = snapshot.Log.Header.Id;
        }
        finally { _transitions.Release(); }
        // Never hold dispatcher transitions while waiting for the host mutation gate: replacement notifications acquire transitions.
        if (attachment is null) await session.SetSessionNameAsync(sessionId, name, token).ConfigureAwait(false);
        else await _sessionOwner!.SetSessionNameAsync(attachment, name, token).ConfigureAwait(false);
        return null;
    }

    private static string SanitizeSessionName(string text)
    {
        var value = RpcCommandCodec.TrimSource(text); var result = new StringBuilder(value.Length); var newline = false;
        foreach (var character in value)
        {
            if (character is '\r' or '\n') { if (!newline) result.Append(' '); newline = true; }
            else { result.Append(character); newline = false; }
        }
        return RpcCommandCodec.TrimSource(result.ToString());
    }

    private JsonData SessionStats(RpcCommandEnvelope command, PersistentAgentSessionSnapshot snapshot, string sessionPath, CancellationToken token)
    {
        var history = new SessionHistoryProjector().Project(snapshot.Log.Entries, snapshot.Context.LeafId, token);
        var stats = history.SessionStatistics;
        if (stats.Status != SessionAccountingStatus.Available || stats.Totals is null)
            throw new RpcCommandException(command.Id, command.Type, "Session usage is outside the supported finite native accounting profile.");
        var totals = stats.Totals;
        double? window = null, contextTokens = null;
        if (TryGetModel(snapshot.Agent.Model, out var model) && model.Value.TryGetProperty("contextWindow", out var rawWindow) &&
            rawWindow.TryGetDouble(out var number) && double.IsFinite(number) && number > 0)
        {
            window = number;
            var latestCompaction = -1;
            for (var index = 0; index < snapshot.Context.Ancestry.Length; index++)
            { token.ThrowIfCancellationRequested(); if (snapshot.Context.Ancestry[index].Kind == SessionEntryKind.Compaction) latestCompaction = index; }
            var postCompactionUsage = latestCompaction < 0;
            foreach (var contribution in snapshot.Context.ContextEntries)
            {
                token.ThrowIfCancellationRequested();
                if (latestCompaction >= 0 && snapshot.Context.Ancestry.IndexOf(contribution.SourceEntry) <= latestCompaction) continue;
                if (contribution.Messages.Any(message => message.Role == "assistant" &&
                    message.WireBody.Value.TryGetProperty("stopReason", out var stop) && stop.GetString() is not ("error" or "aborted") &&
                    SessionCompactionTokenEstimator.EstimateContextTokens([message]).UsageTokens > 0)) postCompactionUsage = true;
            }
            if (postCompactionUsage) contextTokens = SessionCompactionTokenEstimator.EstimateProjectedContextTokens(snapshot.Context).Tokens;
        }
        if (contextTokens is { } usedTokens && (!double.IsFinite(usedTokens) || !double.IsFinite(usedTokens / window!.Value * 100)))
            throw new RpcCommandException(command.Id, command.Type, "Context usage is outside the supported finite native accounting profile.");
        token.ThrowIfCancellationRequested();
        return RpcCommandCodec.Build(writer =>
        {
            // JavaScript omits undefined sessionFile/contextUsage. Volatile native sessions have no file.
            if (snapshot.Log.StorageDurability != PiSharp.Sessions.Storage.SessionLogStorageDurability.VolatileMemory)
                writer.WriteString("sessionFile", sessionPath);
            writer.WriteString("sessionId", snapshot.Log.Header.Id);
            writer.WriteNumber("userMessages", stats.UserMessages); writer.WriteNumber("assistantMessages", stats.AssistantMessages);
            writer.WriteNumber("toolCalls", stats.ToolCalls); writer.WriteNumber("toolResults", stats.ToolResults);
            writer.WriteNumber("totalMessages", stats.TotalMessages);
            writer.WritePropertyName("tokens"); writer.WriteStartObject();
            writer.WriteNumber("input", totals.Input); writer.WriteNumber("output", totals.Output);
            writer.WriteNumber("cacheRead", totals.CacheRead); writer.WriteNumber("cacheWrite", totals.CacheWrite);
            writer.WriteNumber("total", totals.Total); writer.WriteEndObject(); writer.WriteNumber("cost", totals.Cost);
            if (window is { } contextWindow)
            {
                writer.WritePropertyName("contextUsage"); writer.WriteStartObject();
                if (contextTokens is { } count) writer.WriteNumber("tokens", count); else writer.WriteNull("tokens");
                writer.WriteNumber("contextWindow", contextWindow);
                if (contextTokens is { } used) writer.WriteNumber("percent", used / contextWindow * 100); else writer.WriteNull("percent");
                writer.WriteEndObject();
            }
        }, _options.MaximumOutputBytes);
    }
}
