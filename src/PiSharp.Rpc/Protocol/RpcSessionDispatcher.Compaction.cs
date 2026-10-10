using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Sessions.Compaction;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;

namespace PiSharp.Rpc.Protocol;

public sealed partial class RpcSessionDispatcher
{
    private async Task<JsonData?> SummaryCommandAsync(RpcCommandEnvelope command,
        AgentSessionAttachment? attachment, CancellationToken token)
    {
        var manualWire = command.Type == "compact";
        var upstreamToggle = command.Type == "set_auto_compaction";
        if (_sessionOwner is null || attachment is null ||
            _summaryGenerator is null && !(upstreamToggle && command.Mode == "disabled"))
            throw new RpcCommandException(command.Id, command.Type, command.Type == "pisharp_set_auto_compaction"
                ? "Automatic summaries require an owning native session host and explicit transport."
                : "Summaries require an owning native session host and explicit summary transport.");
        _sessionOwner.ValidateAttachment(attachment); token.ThrowIfCancellationRequested();
        if (!manualWire && !upstreamToggle && command.ExpectedGeneration != attachment.Generation)
            throw new RpcCommandException(command.Id, command.Type, command.Type == "pisharp_set_auto_compaction"
                ? "Automatic summary configuration generation is stale." : "Summary session generation is stale.");
        // agent-session.ts setAutoCompactionEnabled: the compaction.enabled setting; every check then reads the current model's
        // settings and window (_checkCompaction, _runAutoCompaction), so the toggle reads neither.
        if (upstreamToggle && _compactionSettings is not null)
        {
            _ = RpcCommandCodec.Success(command, null, _options);
            await ConfigureModelBoundAutoCompactionAsync(attachment, command.Mode == "enabled", token).ConfigureAwait(false);
            return null;
        }
        // agent-session.ts compact: settingsManager.getCompactionSettings(model) for the upstream command.
        SessionCompactionSettings? hostSettings = null;
        if (manualWire && _compactionSettings is not null)
        {
            try { hostSettings = _compactionSettings(attachment.Session.Snapshot.Agent.Model); }
            catch (InvalidOperationException error) { throw new RpcCommandException(command.Id, command.Type, "Compaction failed: " + error.Message); }
        }
        if (upstreamToggle || command.Type == "pisharp_set_auto_compaction")
        {
            var request = upstreamToggle ? new SessionCompactionRequest(hostSettings, ContextWindow: SummaryContextWindow(command, attachment), Automatic: true)
                : command.Compaction!;
            _ = RpcCommandCodec.Success(command, null, _options);
            await _sessionOwner.ConfigureAutomaticCompactionAsync(attachment,
                command.Mode == "enabled" ? _summaryGenerator : null, request, token, _recoveryDesiredMaxOutput).ConfigureAwait(false);
            return null;
        }
        // agent-session.ts compact(): `await this.abort()` first, which aborts a running agent loop and waits for idle, so the
        // native transaction is admitted idle. An extension command that awaits ctx.compact runs outside the agent loop (no run
        // to abort). A callback of the running loop itself cannot wait for that loop's settlement, so it is still refused.
        if (manualWire)
        {
            bool running; lock (_gate) running = _run is not null;
            if (running && _inCallback.Value)
                throw new RpcCommandException(command.Id, command.Type, "Session is processing or settling; manual compaction requires idle admission.");
            if (running) await AbortAsync(token).ConfigureAwait(false);
        }
        var operation = new ContextEditCommand(); lock (_gate) { ThrowOpen(); _contextEdits.Add(operation); }
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, operation.Abort.Token);
            JsonData NativeResponse(SessionEntry? entry) => RpcCommandCodec.Build(writer =>
            {
                writer.WriteBoolean("checkpointAcknowledged", entry is not null);
                writer.WriteBoolean("skipped", entry is null); writer.WriteNumber("generation", attachment.Generation);
                writer.WriteString("sessionId", attachment.Session.Snapshot.Log.Header.Id);
                if (entry is not null) { writer.WriteString("entryId", entry.Id); writer.WriteString("leafId", entry.Id); }
            }, _options.MaximumOutputBytes);
            if (!manualWire) _ = RpcCommandCodec.Success(command, NativeResponse(null), _options);
            else
            {
                _ = OriginalCompactionEventProjector.End(SessionCompactionReason.Manual, null, false, false, "Compaction failed: RPC command failed.", _options);
                _ = OriginalCompactionEventProjector.End(SessionCompactionReason.Manual, null, true, false, options: _options);
                _ = OriginalCompactionEventProjector.Start(SessionCompactionReason.Manual, _options);
            }
            try
            {
                ValueTask Validate(SessionSummaryCheckpointPreview prospective, CancellationToken work)
                {
                    work.ThrowIfCancellationRequested();
                    var data = manualWire ? ManualCompactionResult(prospective.Entry, prospective.Context) : NativeResponse(prospective.Entry);
                    _ = RpcCommandCodec.Success(command, data, _options);
                    return ValueTask.CompletedTask;
                }
                var compaction = manualWire && hostSettings is not null && command.Compaction is not null
                    ? command.Compaction with { Settings = hostSettings } : command.Compaction;
                var receipt = compaction is not null
                    ? await _sessionOwner.CompactAsync(attachment, compaction, _summaryGenerator!, cancellation.Token, Validate).ConfigureAwait(false)
                    : await _sessionOwner.SummarizeBranchAsync(attachment, command.BranchSummary!, _summaryGenerator!, cancellation.Token, Validate).ConfigureAwait(false);
                if (!manualWire) return NativeResponse(receipt?.Entry);
                if (receipt is null)
                {
                    var last = attachment.Session.Snapshot.Context.Ancestry.LastOrDefault();
                    throw new RpcCommandException(command.Id, command.Type, last?.Kind == SessionEntryKind.Compaction
                        ? "Already compacted" : "Nothing to compact (session too small)");
                }
                var result = ManualCompactionResult(receipt.Entry, receipt.Context);
                // The session transaction has already delivered its genuine settled terminal before this response.
                return result;
            }
            catch (Exception error) when (manualWire && error is not RpcDispatchException { Failure: RpcDispatchFailure.OutputFailed })
            {
                // rpc-mode.ts: the error session.compact throws answers with its message ("Compaction cancelled" when aborted).
                var message = error is OperationCanceledException ? "Compaction cancelled" : error.Message;
                throw new RpcCommandException(command.Id, command.Type, message);
            }
        }
        finally
        {
            Task cancelIdle;
            lock (_gate) { _contextEdits.Remove(operation); cancelIdle = operation.CancelUsers == 0 ? Task.CompletedTask : operation.CancelIdle!.Task; }
            await cancelIdle.ConfigureAwait(false); operation.Abort.Dispose();
        }
    }

    /// <summary>Pi ExtensionContext.compact(): the current session's manual compaction as the <c>compact</c> command runs it (its
    /// compaction events included), with no response record; upstream's compact() first aborts a run in progress. Returns the
    /// CompactionResult data; failures throw with the command's message.</summary>
    public async Task<JsonData> CompactForExtensionAsync(SessionCompactionRequest request, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        // From a command's own input (ctx.compact awaited by the command) the compaction runs within that input; otherwise it aborts a
        // run and waits for the session to settle.
        if (_sessionOwner?.Current.Session.IsExecutingInputCallback != true)
        {
            lock (_gate) if (_run is not null) _session.Abort();
            await WaitForIdleAsync(token).ConfigureAwait(false);
        }
        var command = new RpcCommandEnvelope(null, "compact", Compaction: request);
        return (await SummaryCommandAsync(command, _sessionOwner?.Current, token).ConfigureAwait(false))!;
    }

    /// <summary>sdk.ts/agent-session.ts: a session's auto-compaction is the compaction.enabled setting (default true) in every mode;
    /// the host applies it when it starts. The configuration carries to replacement sessions, as a setting does.</summary>
    public async Task ConfigureAutoCompactionAsync(bool enabled, CancellationToken token = default)
    {
        if (_sessionOwner is null || _compactionSettings is null)
            throw new InvalidOperationException("Setting-bound auto-compaction requires an owning host with compaction settings.");
        if (enabled && _summaryGenerator is null) return;
        await ConfigureModelBoundAutoCompactionAsync(_sessionOwner.Current, enabled, token).ConfigureAwait(false);
    }
    private Task ConfigureModelBoundAutoCompactionAsync(AgentSessionAttachment attachment, bool enabled, CancellationToken token)
    {
        SessionCompactionRequest? ForModel(ModelDescriptor model)
        {
            // _checkCompaction: contextWindow = this.model.contextWindow ?? 0; no usable window never compacts.
            if (!TryGetModel(model, out var wire) || !wire.Value.TryGetProperty("contextWindow", out var value) ||
                !value.TryGetDouble(out var window) || !double.IsFinite(window) || window <= 0) return null;
            return new(_compactionSettings!(model), Automatic: true, ContextWindow: window);
        }
        return _sessionOwner!.ConfigureAutomaticCompactionAsync(attachment, enabled ? _summaryGenerator : null,
            new SessionCompactionRequest(new(), Automatic: true), token, _recoveryDesiredMaxOutput, ForModel);
    }
    private double SummaryContextWindow(RpcCommandEnvelope command, AgentSessionAttachment attachment)
    {
        if (!TryGetModel(attachment.Session.Snapshot.Agent.Model, out var model) ||
            !model.Value.GetProperty("contextWindow").TryGetDouble(out var window) || !double.IsFinite(window) || window <= 0)
            throw new RpcCommandException(command.Id, command.Type, "Automatic compaction requires a positive finite model context window.");
        return window;
    }
    private JsonData ManualCompactionResult(SessionEntry entry, SessionContextProjection context)
    {
        var body = entry.WireBody.Value; var after = context.Messages.Sum(SessionCompactionTokenEstimator.EstimateTokens);
        if (!double.IsFinite(after)) throw new RpcDispatchException(RpcDispatchFailure.ResourceLimit);
        return OriginalCompactionEventProjector.Result(body.GetProperty("summary").GetString()!,
            body.GetProperty("firstKeptEntryId").GetString()!, body.GetProperty("tokensBefore").GetDouble(), after,
            body.TryGetProperty("usage", out var usage) ? JsonData.FromElement(usage) : null,
            body.TryGetProperty("details", out var details) ? JsonData.FromElement(details) : null, _options);
    }
}
