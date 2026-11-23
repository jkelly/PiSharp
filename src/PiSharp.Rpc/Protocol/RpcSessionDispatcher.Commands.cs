using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Rpc.Ui;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Lifecycle;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;
using PiSharp.Sessions.Tree;

namespace PiSharp.Rpc.Protocol;

public sealed partial class RpcSessionDispatcher
{
    private TranscriptEntry Input(RpcCommandEnvelope command)
    {
        long timestamp;
        try { timestamp = _clock(); }
        catch (Exception) { throw new RpcCommandException(command.Id, command.Type, "Prompt timestamp is invalid."); }
        if (timestamp is < -9_007_199_254_740_991 or > 9_007_199_254_740_991)
            throw new RpcCommandException(command.Id, command.Type, "Prompt timestamp is outside the native integer profile.");
        return RpcCommandCodec.User(command, timestamp, _options);
    }
    private JsonData CatalogPage(SessionCatalogPage page, AgentSessionAttachment attachment) => RpcCommandCodec.Build(writer =>
    {
        writer.WriteNumber("generation", attachment.Generation);
        if (page.Items.Length > _options.MaximumReturnedEntries) throw new RpcDispatchException(RpcDispatchFailure.ResourceLimit);
        writer.WritePropertyName("sessions"); writer.WriteStartArray();
        foreach (var item in page.Items)
        {
            writer.WriteStartObject(); writer.WriteString("key", item.Key); writer.WriteString("storeId", item.StoreId);
            writer.WriteString("fileName", item.FileName); writer.WriteString("sessionFile", item.Path);
            writer.WriteString("sessionId", item.SessionId); writer.WriteString("createdTimestamp", item.CreatedTimestamp);
            writer.WriteString("cwd", item.WorkingDirectory); writer.WriteString("cwdGroup", item.CwdGroup);
            writer.WriteString("parentSession", item.ParentSessionPath); writer.WriteNumber("fileBytes", item.FileBytes);
            writer.WriteString("modifiedUtc", new DateTime(item.ModifiedUtcTicks, DateTimeKind.Utc).ToString("o", System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteBoolean("isCurrent", string.Equals(item.Path, attachment.Session.Path,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            writer.WriteEndObject();
        }
        writer.WriteEndArray(); writer.WriteString("nextCursor", page.NextCursor);
        writer.WriteNumber("skippedFiles", page.SkippedFiles); writer.WriteNumber("unavailableStores", page.UnavailableStores);
    }, _options.MaximumOutputBytes);
    private JsonData ReplacementResponse(AgentSessionReplacement? replacement) => RpcCommandCodec.Build(writer =>
    {
        writer.WriteBoolean("cancelled", replacement is null);
        if (replacement is null) return;
        writer.WriteString("sessionId", replacement.Current.Session.Snapshot.Log.Header.Id);
        writer.WriteString("sessionFile", replacement.Current.Session.Path); writer.WriteNumber("generation", replacement.Current.Generation);
    }, _options.MaximumOutputBytes);
    private void PreflightReplacement(RpcCommandEnvelope command, PersistentAgentSession target, long generation)
    {
        var data = RpcCommandCodec.Build(writer =>
        {
            writer.WriteBoolean("cancelled", false); writer.WriteString("sessionId", target.Snapshot.Log.Header.Id);
            writer.WriteString("sessionFile", target.Path); writer.WriteNumber("generation", generation);
        }, _options.MaximumOutputBytes);
        _ = RpcCommandCodec.Success(command, data, _options);
        _ = RpcCommandCodec.Event("session_switched", writer =>
        {
            writer.WriteString("sessionFile", target.Path); writer.WriteString("sessionId", target.Snapshot.Log.Header.Id);
            writer.WriteNumber("generation", generation);
        }, _options);
    }
    private JsonData ForkMessages(CancellationToken token)
    {
        var entries = _session.Snapshot.Log.Entries;
        return RpcCommandCodec.Build(writer =>
        {
            writer.WritePropertyName("messages"); writer.WriteStartArray(); var count = 0;
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                if (entry.Kind != SessionEntryKind.Message) continue;
                var message = entry.WireBody.Value.GetProperty("message");
                if (message.GetProperty("role").GetString() != "user") continue;
                var text = RpcCommandCodec.Text(new("user", JsonData.FromElement(message)));
                if (text.Length == 0) continue;
                if (++count > _options.MaximumReturnedMessages) throw new RpcDispatchException(RpcDispatchFailure.ResourceLimit);
                writer.WriteStartObject(); writer.WriteString("entryId", entry.Id); writer.WriteString("text", text); writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }, _options.MaximumOutputBytes);
    }
    private JsonData Tree(RpcCommandEnvelope command, CancellationToken token)
    {
        var state = _session.Snapshot;
        if (state.Log.Entries.Length > _options.MaximumReturnedEntries) throw new RpcDispatchException(RpcDispatchFailure.ResourceLimit);
        // The session's own context bounds (the Pi entry's admit every entry); the returned-entry bound above stays the command's.
        var tree = _session.CreateTreeQueries().Build(state.Log.Entries, token);
        if (!tree.LabelsAvailable) throw new RpcCommandException(command.Id, command.Type, "Session tree metadata is unavailable under the native profile.");
        var data = RpcCommandCodec.Build(writer =>
        {
            writer.WriteString("leafId", state.Context.LeafId);
            writer.WritePropertyName("tree"); writer.WriteStartArray();
            var stack = new Stack<(string? Id, int Depth)>();
            for (var index = tree.RootIds.Length - 1; index >= 0; index--) stack.Push((tree.RootIds[index], 1));
            while (stack.TryPop(out var frame))
            {
                token.ThrowIfCancellationRequested();
                if (frame.Id is null) { writer.WriteEndArray(); writer.WriteEndObject(); continue; }
                if (frame.Depth * 2 + 4 >= _options.MaximumJsonDepth) throw new RpcDispatchException(RpcDispatchFailure.ResourceLimit);
                var node = tree.ById[frame.Id]; writer.WriteStartObject();
                RpcCommandCodec.Raw(writer, "entry", node.Entry.WireBody);
                if (node.ResolvedLabel is { } label) { writer.WriteString("label", label.Label); writer.WriteString("labelTimestamp", label.Timestamp); }
                writer.WritePropertyName("children"); writer.WriteStartArray(); stack.Push((null, frame.Depth));
                var children = tree.GetChronologicalChildren(frame.Id, token);
                if (children.Status != SessionTreeOrderStatus.Completed)
                    throw new RpcCommandException(command.Id, command.Type, "Session tree chronological ordering is unavailable under the native profile.");
                for (var index = children.Entries.Length - 1; index >= 0; index--) stack.Push((children.Entries[index].Id, frame.Depth + 1));
            }
            writer.WriteEndArray();
        }, _options.MaximumOutputBytes);
        try { RpcCommandCodec.Strict(data, _options.MaximumOutputBytes, Math.Max(1, _options.MaximumJsonDepth - 1)); }
        catch (JsonlTransportException) { throw new RpcDispatchException(RpcDispatchFailure.ResourceLimit); }
        return data;
    }
    private async Task RestoreQueueAsync(RpcCommandEnvelope command, CancellationToken token)
    {
        Task interrupted = Task.CompletedTask;
        await _queuePublication.WaitAsync(token).ConfigureAwait(false);
        try
        {
            RpcQueueRestorationPlan plan;
            await _transitions.WaitAsync(token).ConfigureAwait(false);
            try
            {
                CheckOpen();
                var attachment = _sessionOwner?.Current;
                if (attachment is null || attachment.Generation != command.ExpectedGeneration ||
                    !ReferenceEquals(attachment.Session, _session))
                    throw new RpcCommandException(command.Id, command.Type, "Queue restoration session generation is stale or unavailable.");
                var removed = false;
                try
                {
                    plan = RpcQueueRestorationPlan.Capture(_session, command, attachment.Generation, _options, token);
                    if (!plan.TryCommit(_session, out _, token))
                        throw new RpcCommandException(command.Id, command.Type, "Queue changed during restoration; retry against the current queue.");
                    removed = true;
                }
                finally
                {
                    if (command.Type == "pisharp_interrupt" && (removed || !token.IsCancellationRequested))
                    {
                        // Capture only this validated attachment's work. Global owner.Abort()
                        // would also cancel a staged replacement or a newer attachment.
                        RunState? run;
                        lock (_gate) { run = _run; if (run is not null) run.AbortRequested = true; }
                        interrupted = Task.WhenAll(_session.WaitForIdleAsync(), run?.Settled.Task ?? Task.CompletedTask);
                        _session.Abort();
                    }
                }
                // The exact expected snapshot was atomically removed. Both frames were
                // fully budgeted before that commit, including the response envelope.
            }
            finally { _transitions.Release(); }
            // Keep postcommit delivery owned even if the requesting caller cancels.
            // Later queue admissions publish after this clear event; switching also waits.
            await WriteAsync(plan.QueueEvent).ConfigureAwait(false);
            _publishedQueue = plan.Cleared;
            await WriteAsync(plan.Response).ConfigureAwait(false);
        }
        finally
        {
            _queuePublication.Release();
            // Settlement can publish output/queue events. Never join it under either
            // semaphore, and retain the original join after caller cancellation/errors.
            await interrupted.ConfigureAwait(false);
        }
    }

    private async Task QueueAsync(RpcCommandEnvelope command, bool steering, CancellationToken token)
    {
        var input = Input(command);
        _ = RpcCommandCodec.Success(command, Disposition("queued"), _options);
        await _transitions.WaitAsync(token).ConfigureAwait(false);
        try
        {
            CheckOpen(); CheckQueueBudget(input, steering, token);
            if (steering) _session.Steer(input, token); else _session.FollowUp(input, token);
        }
        finally { _transitions.Release(); }
        await PublishQueueAsync(force: true).ConfigureAwait(false);
    }
    /// <summary>Pi accepts a prompt as soon as agent_settled has been published; the run releases its ownership right after
    /// writing it, so a prompt in that window waits for the release instead of being refused as "settling".</summary>
    private async Task WaitForPublishedSettlementAsync(CancellationToken token)
    {
        RunState? published; lock (_gate) published = _run is { SettledPublished: true } run ? run : null;
        if (published is not null) await published.Settled.Task.WaitAsync(token).ConfigureAwait(false);
    }
    /// <summary>A prompt met a run that has finished and is settling (agent_settled not yet published).</summary>
    private sealed class SettlingRunException(RunState run) : Exception("Session is settling.") { public RunState Run { get; } = run; }

    /// <summary>agent-session.ts prompt(): a prompt that arrives while the previous run settles is deferred until it has settled
    /// (_deferredSettledActions), never refused. The settling run is waited for, then the prompt is admitted as usual.</summary>
    private async Task PromptAsync(RpcCommandEnvelope command, CancellationToken token)
    {
        while (true)
        {
            try { await PromptCoreAsync(command, token).ConfigureAwait(false); return; }
            catch (SettlingRunException settling) { await settling.Run.Settled.Task.WaitAsync(token).ConfigureAwait(false); }
        }
    }
    private async Task SubmitInputAsync(RpcCommandEnvelope command, CancellationToken token)
    {
        while (true)
        {
            try { await SubmitInputCoreAsync(command, token).ConfigureAwait(false); return; }
            catch (SettlingRunException settling) { await settling.Run.Settled.Task.WaitAsync(token).ConfigureAwait(false); }
        }
    }
    private async Task PromptCoreAsync(RpcCommandEnvelope command, CancellationToken token)
    {
        var input = Input(command); RunState? run = null; Task<AgentLoopResult>? processing = null; var queued = false; PersistentAgentSession? originating = null;
        var startedResponse = RpcCommandCodec.Success(command, Disposition("started"), _options);
        var queuedResponse = RpcCommandCodec.Success(command, Disposition("queued"), _options);
        await WaitForPublishedSettlementAsync(token).ConfigureAwait(false);
        await _transitions.WaitAsync(token).ConfigureAwait(false);
        try
        {
            CheckOpen(); RunState? active; lock (_gate) active = _run;
            var snapshot = _session.Snapshot;
            if (active is not null)
            {
                if (!snapshot.Agent.IsRunning) throw new SettlingRunException(active);
                if (command.StreamingBehavior is null)
                    throw new RpcCommandException(command.Id, command.Type,
                        "Agent is already processing. Specify streamingBehavior ('steer' or 'followUp') to queue the message.");
                CheckQueueBudget(input, command.StreamingBehavior == "steer", token);
                if (command.StreamingBehavior == "steer") _session.Steer(input, token); else _session.FollowUp(input, token);
                queued = true;
            }
            else
            {
                if (!KnowsModel(snapshot.Agent.Model)) throw new RpcDispatchException(RpcDispatchFailure.InvalidModelDefinition);
                originating = _session;
                run = new(snapshot.Agent.Messages.Length); lock (_gate) _run = run;
                try { processing = _session.PromptAsync(input); }
                catch { lock (_gate) if (ReferenceEquals(_run, run)) _run = null; run.Ready.TrySetResult(); run.Settled.TrySetResult(); throw; }
                if (_session.Snapshot.Agent.Generation == snapshot.Agent.Generation)
                {
                    lock (_gate) if (ReferenceEquals(_run, run)) _run = null;
                    run.Ready.TrySetResult(); run.Settled.TrySetResult(); run = null;
                }
            }
        }
        finally { _transitions.Release(); }
        if (queued)
        {
            await PublishQueueAsync(force: true).ConfigureAwait(false);
            await WriteAsync(queuedResponse).ConfigureAwait(false); return;
        }
        if (run is null) { await processing!.ConfigureAwait(false); throw new RpcDispatchException(RpcDispatchFailure.SessionRunFailed); }
        // The generation transition is actual admission. Gate observer output until this one authoritative response is written.
        lock (_gate) _postInputMovedToRun.Add(command);
        _ = MonitorAsync(run, processing!, originating!);
        try { await WriteAsync(startedResponse).ConfigureAwait(false); }
        finally { run.Ready.TrySetResult(); }
    }
    private sealed class InputAdmission(RpcSessionDispatcher owner, IPromptInputAdmission selected, CancellationToken caller) : IPromptInputAdmission
    {
        public async ValueTask<PromptInputDecision> ReduceAsync(PromptInput input, CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, caller);
            var previous = owner._inInputCallback.Value; owner._inInputCallback.Value = true;
            try
            {
                var result = await selected.ReduceAsync(input, linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested(); return result;
            }
            finally { owner._inInputCallback.Value = previous; }
        }
    }
    private async Task SubmitInputCoreAsync(RpcCommandEnvelope command, CancellationToken token)
    {
        var started = RpcCommandCodec.Success(command, Disposition("started"), _options);
        var queued = RpcCommandCodec.Success(command, Disposition("queued"), _options);
        var handled = RpcCommandCodec.Success(command, Disposition("handled"), _options);
        using var admissionCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _stopInputToken);
        if (command.Type == "prompt") await WaitForPublishedSettlementAsync(admissionCancellation.Token).ConfigureAwait(false);
        await _inputCommands.WaitAsync(admissionCancellation.Token).ConfigureAwait(false);
        var candidate = new RunState(0);
        PersistentAgentSession originating;
        try
        {
            await _transitions.WaitAsync(admissionCancellation.Token).ConfigureAwait(false);
            try
            {
                CheckOpen();
                originating = _session;
                lock (_gate) _admittingSession = originating;
                if (command.Type == "prompt")
                {
                    RunState? active; lock (_gate) active = _run;
                    if (active is not null && !_session.Snapshot.Agent.IsRunning) throw new SettlingRunException(active);
                    if (!KnowsModel(_session.Snapshot.Agent.Model)) throw new RpcDispatchException(RpcDispatchFailure.InvalidModelDefinition);
                    lock (_gate) _startingInput = candidate;
                }
            }
            finally { _transitions.Release(); }
            var mode = command.Type switch
            {
                "steer" => PromptInputStreamingBehavior.Steer,
                "follow_up" => PromptInputStreamingBehavior.FollowUp,
                _ => command.StreamingBehavior switch
                {
                    "steer" => PromptInputStreamingBehavior.Steer,
                    "followUp" => PromptInputStreamingBehavior.FollowUp,
                    _ => (PromptInputStreamingBehavior?)null
                }
            };
            var limits = new PromptInputAdmissionOptions(_options.MaximumPromptCharacters, _options.MaximumImages,
                _options.MaximumCommandBytes, _options.MaximumCommandBytes, _options.MaximumJsonDepth,
                _options.MaximumCommandBytes, _options.MaximumCommandBytes)
            {
                QueueOnly = command.Type != "prompt", KeepsLoneSurrogates = PromptInputAdmissionOptions.Default.KeepsLoneSurrogates,
                BeforeQueueCommit = (message, queue, behavior) =>
                {
                    admissionCancellation.Token.ThrowIfCancellationRequested();
                    CheckQueueBudget(message, behavior == PromptInputStreamingBehavior.Steer, queue);
                }
            };
            var selected = _inputAdmissionSelector?.Invoke(command.Type) ?? _inputAdmission ??
                throw new InvalidOperationException("Input admission is unavailable.");
            var processing = originating.SubmitInputAsync(new(command.Message!, PromptInputSource.Rpc, command.Images, mode),
                new InputAdmission(this, selected, token), limits, _stopInputToken);
            // Started coordinator submissions finish after the run. Its actual first event is the admission witness.
            await Task.WhenAny(candidate.Entered.Task, processing).ConfigureAwait(false);
            // A command's handler can return before the run its sendMessage({ triggerTurn }) started has begun. An input processed
            // before any run started has none of its own, so it stops claiming the next start here and a later one is the session's
            // (ObserveAsync adopts it). Claimed until the finally, a start while the response is written waited for a monitor this
            // input never started, and shutdown waited for that run.
            if (!candidate.Entered.Task.IsCompleted) lock (_gate) if (ReferenceEquals(_startingInput, candidate)) _startingInput = null;
            if (candidate.Entered.Task.IsCompleted && originating.TakeInputTriggeredRun() is { } triggered)
            {
                // agent-session.ts sendCustomMessage: a command's sendMessage({ triggerTurn }) runs its turn while the handler still runs;
                // the turn's events flow at once and the prompt is answered when the handler returns.
                lock (_gate)
                {
                    if (ReferenceEquals(_startingInput, candidate)) _startingInput = null;
                    candidate.CommandTriggered = candidate.InputHeldByCommand = true;
                }
                // _emitAgentSettled: agent_settled precedes the handler's return from waitForIdle (and so the "handled" response).
                originating.HoldCommandIdleUntil(candidate.Settled.Task);
                _ = MonitorAsync(candidate, triggered, originating);
                candidate.Ready.TrySetResult();
            }
            else if (candidate.Entered.Task.IsCompleted)
            {
                lock (_gate) if (ReferenceEquals(_startingInput, candidate)) _startingInput = null;
                lock (_gate) _postInputMovedToRun.Add(command);
                _ = MonitorAsync(candidate, SubmittedRunAsync(processing), originating);
                try { await WriteAsync(started).ConfigureAwait(false); }
                finally { candidate.Ready.TrySetResult(); }
                return;
            }
            var result = await processing.ConfigureAwait(false);
            if (result.Disposition == SubmittedInputDisposition.Queued)
            {
                await PublishQueueAsync(force: true).ConfigureAwait(false);
                await WriteAsync(queued).ConfigureAwait(false);
            }
            else if (result.Disposition == SubmittedInputDisposition.Handled) await WriteAsync(handled).ConfigureAwait(false);
            else throw new RpcDispatchException(RpcDispatchFailure.SessionRunFailed);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_startingInput, candidate)) _startingInput = null;
                _admittingSession = null;
            }
            // A command-triggered run that is deciding its settlement under this admission keeps it until it has decided.
            bool release = true;
            lock (_gate) if (candidate.InputHeldByCommand) { candidate.InputHeldByCommand = false; if (candidate.MonitorBorrowingInput) { candidate.InputReleasePending = true; release = false; } }
            if (release) _inputCommands.Release();
        }
    }
    private static async Task<AgentLoopResult> SubmittedRunAsync(Task<SubmittedInputResult> processing)
    {
        var result = await processing.ConfigureAwait(false);
        return result.Disposition == SubmittedInputDisposition.Started && result.Run is not null
            ? result.Run : throw new RpcDispatchException(RpcDispatchFailure.SessionRunFailed);
    }
    private async Task MonitorAsync(RunState run, Task<AgentLoopResult> processing, PersistentAgentSession originating)
    {
        try
        {
            await run.Ready.Task.ConfigureAwait(false); var continuations = 0;
            while (true)
            {
                AgentLoopResult? result;
                // agent-session.ts abort()/dispose(): a run aborted while it starts or runs settles; a run an extension started (a
                // custom message with triggerTurn) reports that as a cancelled task. Only an abort or shutdown makes that a settlement.
                try { result = await processing.ConfigureAwait(false); }
                catch (OperationCanceledException) when (StopRequested(run)) { result = null; }
                // A command's triggered run settles while the handler (the command's input) still runs.
                await (run.CommandTriggered ? originating.WaitForTriggeredRunIdleAsync() : _session.WaitForIdleAsync()).ConfigureAwait(false);
                var borrowed = false;
                lock (_gate) if (run.InputHeldByCommand) borrowed = run.MonitorBorrowingInput = true;
                if (!borrowed) await _inputCommands.WaitAsync().ConfigureAwait(false);
                await _transitions.WaitAsync().ConfigureAwait(false);
                TaskCompletionSource? ready = null;
                try
                {
                    bool stopping;
                    lock (_gate)
                    {
                        if (!ReferenceEquals(_run, run)) return;
                        stopping = _closed || run.AbortRequested;
                    }
                    var queue = _session.GetPendingInputQueueSnapshot();
                    if (stopping || result is null || result.Reason != AgentLoopStopReason.Completed || queue.SteeringMessages.IsEmpty && queue.FollowUpMessages.IsEmpty) break;
                    if (++continuations > _options.MaximumContinuationRuns) throw new RpcDispatchException(RpcDispatchFailure.ResourceLimit);
                    run.HistoryLength = _session.Snapshot.Agent.Messages.Length;
                    run.Ready = ready = NewGate(); // Keep callbacks out of this short transition, even for a synchronous continuation.
                    processing = borrowed ? originating.ContinueTriggeredRunAsync() : _session.ContinueAsync();
                }
                finally { _transitions.Release(); ReleaseInputTurn(run, borrowed); ready?.TrySetResult(); }
            }
            await PublishQueueAsync(force: false).ConfigureAwait(false);
            bool fatal, aborted; lock (_gate) { fatal = _fatal is not null; aborted = run.AbortRequested; }
            // aborted reports whether this session-level run ended because an abort was requested while it ran.
            lock (_gate) run.SettledPublished = true;
            if (!fatal) await WriteAsync(RpcCommandCodec.Event("agent_settled", writer => writer.WriteBoolean("aborted", aborted), _options)).ConfigureAwait(false);
        }
        catch (Exception error) { SignalFatal(error is RpcDispatchException dispatch ? dispatch.Failure : RpcDispatchFailure.SessionRunFailed, error); }
        finally
        {
            TaskCompletionSource<PostOriginEvidence>? postSlot = null;
            try { postSlot = ReservePostOrigin(_postRunSettlement); }
            catch (Exception error) { SignalFatal(RpcDispatchFailure.ResourceLimit, error); }
            lock (_gate) if (ReferenceEquals(_run, run)) _run = null;
            run.Settled.TrySetResult();
            // Processing/idle and agent_settled writer awaits finished and run ownership is released.
            // A reserved post-run slot remains part of actual dispatcher shutdown custody.
            await JoinPostOrigin("run-settled", _postRunSettlement, originating, postSlot).ConfigureAwait(false);
            ScheduleIdleShutdownCheck();
        }
    }
    private bool StopRequested(RunState run) { lock (_gate) return _closed || run.AbortRequested; }
    private void ReleaseInputTurn(RunState run, bool borrowed)
    {
        if (!borrowed) { _inputCommands.Release(); return; }
        bool release; lock (_gate) { run.MonitorBorrowingInput = false; release = run.InputReleasePending; run.InputReleasePending = false; }
        if (release) _inputCommands.Release();
    }
    private async Task AbortAsync(CancellationToken token)
    {
        ContextEditCommand[] edits;
        lock (_gate)
        {
            edits = _contextEdits.ToArray();
            foreach (var edit in edits) if (edit.CancelUsers++ == 0) edit.CancelIdle = NewGate();
        }
        foreach (var edit in edits)
        {
            try { edit.Abort.Cancel(); }
            catch (Exception error) { SignalFatal(RpcDispatchFailure.CleanupFailed, error); }
            finally { lock (_gate) if (--edit.CancelUsers == 0) edit.CancelIdle!.TrySetResult(); }
        }
        RunState? run; PersistentAgentSession? originating;
        await _transitions.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (_gate) { run = _run; originating = _admittingSession; if (run is not null) run.AbortRequested = true; }
            if (_sessionOwner is not null) _sessionOwner.Abort(); else _session.Abort();
            originating?.Abort();
        }
        finally { _transitions.Release(); }
        if (run is not null) await run.Settled.Task.ConfigureAwait(false);
        if (originating is not null) await originating.WaitForIdleAsync().ConfigureAwait(false);
        await _session.WaitForIdleAsync().ConfigureAwait(false);
    }

    private async ValueTask ObserveAsync(AgentEvent observation)
    {
        var previous = _inCallback.Value; _inCallback.Value = true;
        try
        {
            RunState? run;
            // agent-session.ts sendCustomMessage: an extension's sendMessage({ triggerTurn }) on an idle session runs a turn of its own
            // (a timer, an event handler); the host adopts it, streams its events and settles it as any run.
            bool unowned; lock (_gate) unowned = observation is AgentLoopStarted && _run is null && _startingInput is null;
            if (unowned && _session.TakeInputTriggeredRun() is { } triggered)
            {
                var adopted = new RunState(_session.Snapshot.Agent.Messages.Length);
                bool owns; lock (_gate) { owns = _run is null && !_closed; if (owns) _run = adopted; }
                adopted.Ready.TrySetResult();
                if (owns) _ = MonitorAsync(adopted, triggered, _session);
                else _ = ObserveUnadoptedRunAsync(triggered);
            }
            bool closing;
            lock (_gate)
            {
                if (observation is AgentLoopStarted && _startingInput is { } starting)
                {
                    starting.HistoryLength = _session.Snapshot.Agent.Messages.Length;
                    _run = starting; starting.Entered.TrySetResult();
                }
                run = _run; closing = _closed;
                if (observation is AgentLoopStarted && run is not null)
                    run.HistoryLength = _session.Snapshot.Agent.Messages.Length;
            }
            // print-mode.ts and rpc-mode.ts dispose the session on shutdown: agent.abort() and its listeners are removed. A run an
            // extension started that a closing host did not adopt (a command that returned without waitForIdle) is aborted by the
            // shutdown and settles there; none of its events are written.
            if (run is null && closing) return;
            if (run is null && observation is AgentLoopInputMessageStarted { Message.Role: "custom" } or AgentLoopInputMessageEnded { Message.Role: "custom" })
            {
                // agent-session.ts _appendCustomMessage: an idle session appends an extension's custom message and emits its
                // message_start/message_end at once, outside any run.
                lock (_gate) if (_fatal is not null) throw _fatal;
                await _projectionGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    foreach (var record in _events.Project(observation, _session.Snapshot, _session.Snapshot.Agent.Messages.Length))
                        await WriteAsync(record).ConfigureAwait(false);
                }
                finally { _projectionGate.Release(); }
                return;
            }
            if (run is null) throw new RpcDispatchException(RpcDispatchFailure.SessionRunFailed);
            await run.Ready.Task.ConfigureAwait(false);
            lock (_gate) if (_fatal is not null) throw _fatal;
            if (observation is AgentLoopInputMessageStarted) await PublishQueueAsync(force: false).ConfigureAwait(false);
            // A parallel tool batch reports its tools' progress and ends concurrently; projection and its records stay in order.
            await _projectionGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var projected = _events.Project(observation, _session.Snapshot, run.HistoryLength,
                    observation is AgentLoopEnded ended && _session.WillRetryAfterAgentEnd(ended.Result));
                foreach (var record in projected) await WriteAsync(record).ConfigureAwait(false);
            }
            finally { _projectionGate.Release(); }
        }
        catch (Exception error) { SignalFatal(error is RpcDispatchException dispatch ? dispatch.Failure : RpcDispatchFailure.SessionRunFailed, error); throw; }
        finally { _inCallback.Value = previous; }
    }
    private async ValueTask ObserveOperationAsync(SessionOperationEvent observation)
    {
        if (observation is SessionCompactionStarted or SessionCompactionPrepared or SessionCompactionEnded)
        {
            var prior = _inCallback.Value; _inCallback.Value = true;
            try
            {
                switch (observation)
                {
                    case SessionCompactionStarted started:
                        await WriteAsync(OriginalCompactionEventProjector.Start(started.Reason, _options)).ConfigureAwait(false);
                        break;
                    case SessionCompactionPrepared prepared:
                        // Validate the complete eventual frame before the original append is admitted.
                        _ = OriginalCompactionEventProjector.End(prepared.Reason, prepared.Result, false, prepared.WillRetry, options: _options);
                        break;
                    case SessionCompactionEnded ended:
                        await WriteAsync(OriginalCompactionEventProjector.End(ended.Reason, ended.Result,
                            ended.Aborted, ended.WillRetry, ended.ErrorMessage, _options)).ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception error)
            {
                // A prospective output-budget refusal has no transport effect and must not poison the dispatcher.
                if (observation is not SessionCompactionPrepared)
                    SignalFatal(error is RpcDispatchException dispatch ? dispatch.Failure : RpcDispatchFailure.SessionRunFailed, error);
                throw;
            }
            finally { _inCallback.Value = prior; }
            return;
        }
        if (observation is SessionAutoRetryStarted or SessionAutoRetryEnded)
        { await ObserveRetryAsync(observation).ConfigureAwait(false); return; }
        if (!_session.Snapshot.AutoRecoveryEnabled && !_session.AutomaticRetryConfigured) return;
        var previous = _inCallback.Value; _inCallback.Value = true;
        try
        {
            var kind = observation switch
            {
                SessionRecoveryStarted => "pisharp_recovery_started",
                SessionRecoveryEnded => "pisharp_recovery_ended",
                SessionOperationSettled => "pisharp_operation_settled",
                _ => throw new RpcDispatchException(RpcDispatchFailure.SessionRunFailed)
            };
            await WriteAsync(RpcCommandCodec.Event(kind, writer =>
            {
                writer.WriteNumber("operationGeneration", observation.OperationGeneration);
                if (observation is SessionRecoveryStarted started)
                { writer.WriteString("reason", started.Reason); writer.WriteBoolean("willRetry", started.WillRetry); }
                else if (observation is SessionRecoveryEnded ended)
                { writer.WriteString("status", ended.Status); writer.WriteBoolean("willRetry", ended.WillRetry); if (ended.CheckpointId is not null) writer.WriteString("checkpointId", ended.CheckpointId); }
                else if (observation is SessionOperationSettled settled) writer.WriteString("status", settled.Status);
            }, _options)).ConfigureAwait(false);
        }
        catch (Exception error) { SignalFatal(error is RpcDispatchException dispatch ? dispatch.Failure : RpcDispatchFailure.SessionRunFailed, error); throw; }
        finally { _inCallback.Value = previous; }
    }
    private async Task PublishQueueAsync(bool force)
    {
        await _queuePublication.WaitAsync().ConfigureAwait(false);
        try
        {
            var queue = _session.GetPendingInputQueueSnapshot();
            if (!force && SameQueue(queue, _publishedQueue)) return;
            await WriteAsync(RpcCommandCodec.Event("queue_update", writer => RpcCommandCodec.Queue(writer, queue), _options)).ConfigureAwait(false);
            _publishedQueue = queue;
        }
        finally { _queuePublication.Release(); }
    }
    private static bool SameQueue(AgentPendingInputQueueSnapshot left, AgentPendingInputQueueSnapshot right) =>
        Same(left.SteeringMessages, right.SteeringMessages) && Same(left.FollowUpMessages, right.FollowUpMessages);
    private static bool Same(ImmutableArray<TranscriptEntry> left, ImmutableArray<TranscriptEntry> right) =>
        left.Length == right.Length && left.Zip(right).All(pair => pair.First.WireBody.ToString() == pair.Second.WireBody.ToString());
    private void CheckQueueBudget(TranscriptEntry input, bool steering, CancellationToken token)
    {
        var queue = _session.GetPendingInputQueueSnapshot(token);
        CheckQueueBudget(input, steering, queue);
    }
    private void CheckQueueBudget(TranscriptEntry input, bool steering, AgentPendingInputQueueSnapshot queue)
    {
        var prospective = steering ? queue with { SteeringMessages = queue.SteeringMessages.Add(input) }
            : queue with { FollowUpMessages = queue.FollowUpMessages.Add(input) };
        _ = RpcCommandCodec.Event("queue_update", writer => RpcCommandCodec.Queue(writer, prospective), _options);
    }

    private JsonData State()
    {
        var snapshot = _session.Snapshot; var queue = _session.GetPendingInputQueueSnapshot();
        if (!TryGetModel(snapshot.Agent.Model, out var model)) throw new RpcDispatchException(RpcDispatchFailure.InvalidModelDefinition);
        string? name = null;
        foreach (var entry in snapshot.Log.Entries.Reverse())
            if (entry.Type == "session_info")
            {
                if (entry.WireBody.Value.TryGetProperty("name", out var value) && value.ValueKind == JsonValueKind.String)
                { var text = RpcCommandCodec.TrimSource(value.GetString()!); if (text.Length != 0) name = text; }
                break;
            }
        return RpcCommandCodec.Build(writer =>
        {
            RpcCommandCodec.Raw(writer, "model", model); writer.WriteString("thinkingLevel", snapshot.Context.ThinkingLevel);
            writer.WriteBoolean("isStreaming", snapshot.Agent.IsRunning); writer.WriteBoolean("isCompacting", snapshot.IsCompacting);
            writer.WriteBoolean("autoRetryEnabled", _session.AutoRetryEnabled); writer.WriteBoolean("isRetrying", _session.IsRetrying);
            // agent_settled is published before the monitor clears its original run owner.
            // This is a read-only dispatcher admission fence, not a promise about future commands.
            bool runOwnerSettled; lock (_gate) runOwnerSettled = _run is null;
            writer.WriteBoolean("pisharpRunOwnerSettled", runOwnerSettled);
            if (snapshot.AutoRecoveryEnabled)
            {
                writer.WriteBoolean("pisharpOperationActive", snapshot.IsProcessingOperation);
                writer.WriteString("pisharpOperationPhase", snapshot.OperationPhase.ToString());
                writer.WriteNumber("pisharpOperationGeneration", snapshot.OperationGeneration);
            }
            if (snapshot.IsEditingContext) writer.WriteBoolean("pisharpEditingContext", true);
            writer.WriteString("steeringMode", RpcCommandCodec.Mode(queue.SteeringMode)); writer.WriteString("followUpMode", RpcCommandCodec.Mode(queue.FollowUpMode));
            writer.WriteString("sessionFile", _session.Path); writer.WriteString("sessionId", snapshot.Log.Header.Id);
            if (_sessionOwner is not null) writer.WriteNumber("pisharpGeneration", _sessionOwner.Current.Generation);
            if (snapshot.Log.StorageDurability != PiSharp.Sessions.Storage.SessionLogStorageDurability.LocalFileFlush)
            {
                writer.WriteString("pisharpPersistence", snapshot.Log.StorageDurability.ToString());
                writer.WriteBoolean("pisharpDurableCheckpointAcknowledged", false);
            }
            if (name is not null) writer.WriteString("sessionName", name);
            writer.WriteBoolean("autoCompactionEnabled", snapshot.AutoCompactionEnabled); writer.WriteNumber("messageCount", snapshot.Context.Messages.Length);
            writer.WriteNumber("pendingMessageCount", checked(queue.SteeringMessages.Length + queue.FollowUpMessages.Length));
        }, _options.MaximumOutputBytes);
    }
    private JsonData Entries(RpcCommandEnvelope command)
    {
        var snapshot = _session.Snapshot; var start = 0;
        if (command.Since is not null)
        {
            var found = -1;
            for (var index = 0; index < snapshot.Log.Entries.Length; index++) if (snapshot.Log.Entries[index].Id == command.Since) { found = index; break; }
            if (found < 0) throw new RpcCommandException(command.Id, command.Type, "Entry not found: " + command.Since);
            start = found + 1;
        }
        if (snapshot.Log.Entries.Length - start > _options.MaximumReturnedEntries) throw new RpcDispatchException(RpcDispatchFailure.ResourceLimit);
        return RpcCommandCodec.Build(writer =>
        {
            writer.WritePropertyName("entries"); writer.WriteStartArray();
            for (var index = start; index < snapshot.Log.Entries.Length; index++) writer.WriteRawValue(snapshot.Log.Entries[index].WireBody.ToString(), skipInputValidation: true);
            writer.WriteEndArray(); writer.WriteString("leafId", snapshot.Context.LeafId);
        }, _options.MaximumOutputBytes);
    }
    private JsonData LastAssistantText()
    {
        string? text = null;
        foreach (var message in _session.Snapshot.Context.Messages.Reverse())
        {
            if (message.Role != "assistant") continue;
            var body = message.WireBody.Value;
            var content = RpcCommandCodec.TrimSource(RpcCommandCodec.Text(message));
            // Pi faac585884ae2c013a6b1400f3e9e77f10b34bac agent-session.ts getLastAssistantText (#12119): a message without text from a
            // turn that did not end normally (aborted, failed or only calling tools) is skipped, so the previous answer is returned. A
            // reply that ended normally stays the last reply even without text.
            var stopReason = body.TryGetProperty("stopReason", out var stop) && stop.ValueKind == System.Text.Json.JsonValueKind.String ? stop.GetString() : null;
            if (stopReason != "stop" && content.Length == 0) continue;
            if (content.Length != 0) text = content;
            break;
        }
        // Pinned runtime returns undefined despite the docs/types saying null; JSON.stringify omits the text property.
        return RpcCommandCodec.Build(writer => { if (text is not null) writer.WriteString("text", text); }, _options.MaximumOutputBytes);
    }
    private JsonData Disposition(string disposition) => RpcCommandCodec.Build(writer => writer.WriteString("disposition", disposition), _options.MaximumOutputBytes);

    private async Task WriteAsync(JsonData record)
    {
        // This gate makes only one call pending in the owned writer. Waiters are bounded by admitted commands plus one sequential agent observer/settlement.
        await _wire.WaitAsync().ConfigureAwait(false);
        try { await _output.WriteAsync(record).ConfigureAwait(false); }
        catch (Exception error) { SignalFatal(RpcDispatchFailure.OutputFailed, error); throw new RpcDispatchException(RpcDispatchFailure.OutputFailed, error); }
        finally { _wire.Release(); }
    }
    private async Task WriteUiAsync(JsonData record, CancellationToken admissionToken, CancellationToken connectionToken)
    {
        using var queued = CancellationTokenSource.CreateLinkedTokenSource(admissionToken, connectionToken);
        await _wire.WaitAsync(queued.Token).ConfigureAwait(false);
        try
        {
            queued.Token.ThrowIfCancellationRequested();
            try { await _output.WriteAsync(record, connectionToken).ConfigureAwait(false); }
            catch (Exception error) { SignalFatal(RpcDispatchFailure.OutputFailed, error); throw new RpcDispatchException(RpcDispatchFailure.OutputFailed, error); }
        }
        finally { _wire.Release(); }
    }
    public Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        if (_inCallback.Value) throw new InvalidOperationException("RPC event callbacks cannot await their own settlement.");
        var nativeIdle = _session.WaitForIdleAsync(); Task settled;
        lock (_gate) settled = _run?.Settled.Task ?? Task.CompletedTask;
        var idle = Task.WhenAll(nativeIdle, settled); return cancellationToken.CanBeCanceled ? idle.WaitAsync(cancellationToken) : idle;
    }
    public ValueTask DisposeAsync()
    {
        lock (_gate) if (_activePostOrigins != 0) throw new InvalidOperationException("Dispatcher disposal cannot join an active post-origin callback.");
        if (_inCallback.Value) throw new InvalidOperationException("RPC event callbacks cannot dispose their own dispatcher.");
        _ = _session.WaitForIdleAsync(); // Includes the coordinator/Agent's callback self-wait guards.
        return new(BeginShutdown(publicRequest: true));
    }
    private void SignalFatal(RpcDispatchFailure failure, Exception? cause = null)
    {
        lock (_gate) _fatal ??= new(failure, cause);
        _ = BeginShutdown();
    }
    private Task BeginShutdown(bool publicRequest = false)
    {
        Task pending; RunState? run; JsonlReader? input;
        lock (_gate)
        {
            if (publicRequest && _activePostOrigins != 0) throw new InvalidOperationException("Dispatcher close cannot join an active post-origin callback.");
            if (_disposal is not null) return _disposal;
            _closed = true; _disposal = _completion.Task;
            pending = _pendingIdle?.Task ?? Task.CompletedTask; run = _run; input = _input;
            if (run is not null) run.AbortRequested = true;
            _capacity.TrySetResult();
        }
        _ = ShutdownAsync(pending, run, input); return _completion.Task;
    }
    private async Task ShutdownAsync(Task pending, RunState? run, JsonlReader? input)
    {
        var failures = new List<Exception>();
        // Resolve dialogs before joining the callback/Agent which may be awaiting them. Cancel cooperative UI writes too.
        Task? uiCleanup = null;
        try { uiCleanup = _ui?.BeginDisconnect(); } catch (Exception error) { failures.Add(error); }
        try { _stopInput.Cancel(); } catch (Exception error) { failures.Add(error); }
        // Join the short admission transition before aborting: shutdown can race a prompt's generation publication.
        try
        {
            await _transitions.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (_gate) { run = _run ?? run; if (run is not null) run.AbortRequested = true; }
                if (_sessionOwner is not null) _sessionOwner.Abort(); else _session.Abort();
            }
            finally { _transitions.Release(); }
        }
        catch (Exception error) { failures.Add(error); }
        if (input is not null) try { await input.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        // Internal fatal closure may start inside a callback: wait our run first, before invoking native self-wait guards.
        if (run is not null) try { await run.Settled.Task.ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        try { await pending.ConfigureAwait(false); await _session.WaitForIdleAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        Task? idleShutdownWait;
        lock (_gate) idleShutdownWait = _idleShutdownWait;
        if (idleShutdownWait is not null) await idleShutdownWait.ConfigureAwait(false);
        await JoinPostOriginShutdown(failures).ConfigureAwait(false);
        if (uiCleanup is not null) try { await uiCleanup.ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        try { _subscription.Dispose(); } catch (Exception error) { failures.Add(error); }
        try { _operationSubscription?.Dispose(); } catch (Exception error) { failures.Add(error); }
        if (_startupWork is not null) try { await _startupWork.ConfigureAwait(false); await _startupReady.Task.ConfigureAwait(false); }
            catch (OperationCanceledException error) when (_startupToken.IsCancellationRequested && error.CancellationToken == _startupToken) { }
            catch (Exception error) when (ReferenceEquals(error, _startupFailure)) { /* Already retained as the operation failure. */ }
            catch (Exception error) { failures.Add(error); }
        if (_sessionOwner is not null) _sessionOwner.AttachmentChanged = null;
        _navigation = null;
        if (_ownership == RpcSessionOwnership.Owned)
            try
            {
                if (_sessionOwner is not null) await _sessionOwner.DisposeAsync().ConfigureAwait(false);
                else await _session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception error) { failures.Add(error); }
        try { await _output.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        foreach (var resource in new IDisposable[] { _stopInput, _transitions, _wire, _queuePublication, _inputCommands })
            try { resource.Dispose(); } catch (Exception error) { failures.Add(error); }
        lock (_gate)
        {
            RpcDispatchException? failure = failures.Count == 0 ? _fatal : new(_fatal?.Failure ?? RpcDispatchFailure.CleanupFailed,
                new AggregateException(_fatal is null ? failures : failures.Prepend(_fatal)));
            _shutdownSettlement.TrySetResult(new(_fatal, failures.ToImmutableArray(), failure));
            if (failure is null) _completion.TrySetResult(); else _completion.TrySetException(failure);
        }
    }
    private JsonData TreePublicationFailureResponse(RpcCommandEnvelope command, string sessionId, long generation, string? leafId) =>
        RpcCommandCodec.Build(writer =>
        {
            if (command.Id is not null) writer.WriteString("id", command.Id);
            writer.WriteString("type", "response"); writer.WriteString("command", command.Type);
            writer.WriteBoolean("success", false);
            writer.WriteString("error", "Session tree selection committed; lifecycle publication failed.");
            writer.WriteBoolean("committed", true);
            writer.WriteStartObject("data"); writer.WriteString("disposition", "Selected");
            writer.WriteString("sessionId", sessionId); writer.WriteNumber("generation", generation);
            writer.WriteString("leafId", leafId); writer.WriteEndObject();
        }, _options.MaximumOutputBytes);
    private void AddPending(int bytes = 0) { if (_pending++ == 0) _pendingIdle = NewGate(); _retainedCommandBytes += bytes; }
    private void CheckOpen() { lock (_gate) ThrowOpen(); }
    private void ThrowOpen() { if (_closed) throw _fatal ?? new RpcDispatchException(RpcDispatchFailure.Disposed); }
    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
