using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Sessions.Compaction;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;
using NativeAgent = PiSharp.Agent.Agent;

namespace PiSharp.CodingAgent;

public sealed partial class PersistentAgentSession
{
    private sealed record AutomaticCompaction(SessionCompactionRequest Request, ISessionSummaryGenerator Generator);
    private AutomaticCompaction? _automaticCompaction;
    private Func<SessionCompactionObservation, ValueTask>? _compactionObservation;
    /// <summary>Trusted host observation binding, captured once per original compaction reservation. No abort signal or veto.</summary>
    public void ConfigureCompactionObservation(Func<SessionCompactionObservation, ValueTask>? observer)
    {
        lock (_gate)
        {
            ThrowAvailable(); ThrowInputMutation(); ThrowConfigurationSelfWait();
            if (_active is not null || _inputSubmission is not null) throw new InvalidOperationException("Compaction observation binding requires an idle session.");
            _compactionObservation = observer;
        }
    }
    internal void ConfigureCompactionObservationForBinding(ReplaceableAgentSession owner, AgentSessionAttachment attachment,
        ReplacementReservation? reservation,
        Func<SessionCompactionObservation, ValueTask>? observer)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(attachment.Session, this)) throw new InvalidOperationException("Observation session differs from the attachment.");
            owner.ValidateRuntimeObservationBinding(attachment, reservation);
            if (reservation is null) ThrowAvailable(); else reservation.ValidateCatalogAuthority(this);
            ThrowInputMutation(); ThrowConfigurationSelfWait();
            if (_active is not null || _inputSubmission is not null) throw new InvalidOperationException("Compaction observation binding requires an idle session.");
            _compactionObservation = observer;
        }
    }
    private SessionAutomaticCompactionStatus? _lastAutomaticCompaction;
    public void ConfigureAutomaticCompaction(ISessionSummaryGenerator? generator, SessionCompactionSettings? settings = null,
        double contextWindow = 128_000, SessionSummaryRequestOptions? summaryOptions = null,
        double? recoveryDesiredMaxOutput = null)
    {
        settings ??= new(); _ = SessionCompactionTokenEstimator.ShouldCompact(0, contextWindow, settings);
        if (recoveryDesiredMaxOutput is { } desired && (!double.IsFinite(desired) || desired <= 0))
            throw new ArgumentOutOfRangeException(nameof(recoveryDesiredMaxOutput));
        lock (_gate)
        {
            ThrowAvailable(); ThrowInputMutation();
            if (_active is not null || _inputSubmission is not null) throw new InvalidOperationException("Automatic compaction configuration requires an idle session.");
            _automaticCompaction = generator is null || !settings.Enabled ? null : new(new(settings, Automatic: true,
                ContextWindow: contextWindow, SummaryOptions: summaryOptions) { Reason = SessionCompactionReason.Threshold, WillRetry = false }, generator);
            if (recoveryDesiredMaxOutput is not null)
            {
                _recoveryDesiredOutput = recoveryDesiredMaxOutput;
                _agent.ConfigureAndReplaceMessages(RecoveryConfiguration(_configuration), SessionContextProjector.AgentMessages(_context));
            }
            _lastAutomaticCompaction = null;
        }
    }
    private async Task RunConfiguredAutomaticCompactionAsync(TaskCompletionSource idle, CancellationToken token)
    {
        AutomaticCompaction? configured; ContextEditCancellation abort;
        lock (_gate)
        {
            configured = _automaticCompaction;
            var queue = _agent.GetPendingInputQueueSnapshot();
            if (configured is null || !queue.SteeringMessages.IsEmpty || !queue.FollowUpMessages.IsEmpty || _fault is not null || _disposed || _retired) return;
            if (!_context.Messages.Any(message => message.Role == "assistant" && message.WireBody.Value.GetProperty("stopReason").GetString() is not ("error" or "aborted"))) return;
            abort = new(); _contextEditCancellation = abort; _compacting = true;
        }
        try
        {
            var receipt = await SummaryCoreAsync(configured.Request, null, configured.Generator, token, idle, abort,
                default, null, releaseReservation: false).ConfigureAwait(false);
            lock (_gate) _lastAutomaticCompaction = new(receipt is null ? "skipped" : "committed", receipt?.Entry.Id);
        }
        catch (SessionCompactionException error) { lock (_gate) _lastAutomaticCompaction = new("failed", Failure: error.Failure); }
        catch (OperationCanceledException) when (token.IsCancellationRequested || abort.Abort.IsCancellationRequested)
        { lock (_gate) _lastAutomaticCompaction = new("cancelled"); }
    }
    public Task<SessionSummaryCheckpointReceipt?> CompactAsync(string expectedSessionId, SessionCompactionRequest request,
        ISessionSummaryGenerator generator, CancellationToken cancellationToken = default,
        Func<SessionSummaryCheckpointPreview, CancellationToken, ValueTask>? preflight = null,
        Func<CancellationToken, ValueTask>? onStarted = null)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(generator);
        if (!Enum.IsDefined(request.Reason) || request.Reason != SessionCompactionReason.Overflow && request.WillRetry)
            throw new ArgumentException("Unsupported compaction observation origin/retry metadata.", nameof(request));
        var reservation = ReserveSummary(expectedSessionId, cancellationToken);
        return SummaryCoreAsync(request, null, generator, cancellationToken, reservation.Idle, reservation.Abort, reservation.InputAbort, preflight, onStarted: onStarted);
    }
    public async Task<SessionSummaryCheckpointReceipt> SummarizeBranchAsync(string expectedSessionId,
        SessionBranchSummaryRequest request, ISessionSummaryGenerator generator, CancellationToken cancellationToken = default,
        Func<SessionSummaryCheckpointPreview, CancellationToken, ValueTask>? preflight = null)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(generator);
        var reservation = ReserveSummary(expectedSessionId, cancellationToken);
        return (await SummaryCoreAsync(null, request, generator, cancellationToken, reservation.Idle, reservation.Abort, reservation.InputAbort, preflight).ConfigureAwait(false))!;
    }
    private (TaskCompletionSource Idle, ContextEditCancellation Abort, CancellationToken InputAbort) ReserveSummary(string expectedSessionId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowAvailable(); ThrowInputMutation();
            if (expectedSessionId != _acknowledgedLog.Header.Id) throw Error(PersistentAgentSessionFailure.StaleSession);
            var callback = _inputCallback.Value;
            if (_active is not null || callback is not null && (!ReferenceEquals(callback, _inputSubmission) || callback.Releasing) ||
                _inputSubmission is not null && !ReferenceEquals(callback, _inputSubmission))
                throw new InvalidOperationException("Session is already processing.");
            var snapshot = _agent.Snapshot;
            if (!snapshot.PendingInputs.IsEmpty || snapshot.SteeringCount != 0 || snapshot.FollowUpCount != 0)
                throw Error(PersistentAgentSessionFailure.InvalidConfiguration);
            var inputAbort = callback?.Abort.Token ?? default; inputAbort.ThrowIfCancellationRequested();
            var abort = new ContextEditCancellation(); var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _contextEditCancellation = abort; _active = idle; _compacting = true;
            return (idle, abort, inputAbort);
        }
    }
    private async Task<SessionSummaryCheckpointReceipt?> SummaryCoreAsync(SessionCompactionRequest? request,
        SessionBranchSummaryRequest? branchRequest, ISessionSummaryGenerator generator, CancellationToken token,
        TaskCompletionSource idle, ContextEditCancellation abort, CancellationToken inputAbort,
        Func<SessionSummaryCheckpointPreview, CancellationToken, ValueTask>? preflight, bool releaseReservation = true,
        Func<CancellationToken, ValueTask>? onStarted = null)
    {
        var commitHeld = false; var writeAdmitted = false;
        SessionSummaryCheckpointReceipt? completed = null; SessionCompactionObservation? observation = null;
        var failures = new List<Exception>(); Exception? bodyFailure = null;
        var lifecycle = request is not null && !(request.OverrideRetainedBoundary && request.FirstKeptEntryId is null);
        var started = false; var aborted = false; JsonData? originalResult = null; string? noPlanMessage = null;
        ImmutableArray<OperationSubscription> lifecycleSubscriptions; long operation;
        lock (_gate) { lifecycleSubscriptions = _operationSubscriptions; operation = _operationGeneration; }
        Func<SessionCompactionObservation, ValueTask>? observer; SessionBeforeCompactHandler? beforeCompaction;
        lock (_gate) { observer = _compactionObservation; beforeCompaction = _beforeCompaction; }
        var cancelledByExtension = false; var fromExtension = false;
        var priorCallback = _configurationCallback.Value; _configurationCallback.Value = idle;
        try
        {
            try
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _closing.Token, inputAbort, abort.Abort.Token);
                var work = cancellation.Token; await _commits.WaitAsync(work).ConfigureAwait(false); commitHeld = true;
                if (lifecycle && request!.Reason == SessionCompactionReason.Manual)
                {
                    started = true;
                    await EmitCompactionAsync(lifecycleSubscriptions, new SessionCompactionStarted(operation, request.Reason)).ConfigureAwait(false);
                }
                // Trusted host start delivery observes the actual reserved compaction, before planning/inference or durable effects.
                if (onStarted is not null) await onStarted(work).ConfigureAwait(false);
                work.ThrowIfCancellationRequested();
                SessionContextProjection previous; SessionLogStoreSnapshot log; AgentConfiguration configuration;
                lock (_gate) { previous = _context; log = _acknowledgedLog; configuration = _configuration; }
                SessionCompactionPlan? plan = null; SessionBranchSummaryPlan? branchPlan = null;
                SessionProvidedSummary? provided; string? parent; string? firstKept; double tokensBefore = 0;
                SessionGeneratedSummary? generated = null; SessionFileOperations files;
                if (request is not null)
                {
                    var settings = request.Settings ?? new();
                    if (request.Automatic && !SessionCompactionTokenEstimator.ShouldCompact(
                        SessionCompactionTokenEstimator.EstimateProjectedContextTokens(previous).Tokens, request.ContextWindow, settings))
                    {
                        noPlanMessage = previous.Ancestry.LastOrDefault()?.Kind == SessionEntryKind.Compaction
                            ? "Already compacted" : "Nothing to compact (session too small)";
                        return null;
                    }
                    plan = request.OverrideRetainedBoundary ? new SessionCompactionPlanner().PrepareWithBoundary(previous, request.FirstKeptEntryId, settings, work)
                        : new SessionCompactionPlanner().Prepare(previous, settings, work);
                    if (plan is null)
                    {
                        noPlanMessage = previous.Ancestry.LastOrDefault()?.Kind == SessionEntryKind.Compaction
                            ? "Already compacted" : "Nothing to compact (session too small)";
                        return null;
                    }
                    if (lifecycle && !started)
                    {
                        started = true;
                        await EmitCompactionAsync(lifecycleSubscriptions, new SessionCompactionStarted(operation, request.Reason)).ConfigureAwait(false);
                        work.ThrowIfCancellationRequested();
                    }
                    parent = previous.LeafId; firstKept = plan.FirstKeptEntryId; tokensBefore = plan.TokensBefore;
                    provided = request.ExtensionSummary; files = plan.FileOps;
                    // Source session_before_compact: after compaction_start and before the default summary request.
                    if (lifecycle && provided is null && beforeCompaction is not null)
                    {
                        var decision = await beforeCompaction(new(PreparationJson(plan, plan.FirstKeptEntryId), previous.Ancestry,
                            request.SummaryOptions?.CustomInstructions, request.Reason, request.WillRetry), work).ConfigureAwait(false);
                        if (decision?.Cancel == true) { cancelledByExtension = true; throw new SessionCompactionException(SessionCompactionFailure.Cancelled); }
                        if (decision?.Compaction is { } compaction)
                        {
                            provided = new(compaction.Summary, compaction.Usage, compaction.Details);
                            firstKept = compaction.FirstKeptEntryId; tokensBefore = compaction.TokensBefore;
                        }
                        work.ThrowIfCancellationRequested();
                    }
                    fromExtension = provided is not null;
                    // Construct/bound every prospective provider request before starting the first transport.
                    // agent-session.ts _runDefaultCompaction: compact() gets the session's thinking level (createSummarizationOptions sends
                    // it as reasoning unless "off"; the summary route applies model.reasoning) and `undefined, // sessionId`, so each
                    // completeSummarization call routes with its own fresh uuidv7 (kept across that call's retries).
                    var summaryOptions = request.SummaryOptions ?? new();
                    if (summaryOptions.ThinkingLevel is null)
                        summaryOptions = summaryOptions with { ThinkingLevel = configuration.ThinkingLevel, ModelSupportsReasoning = true };
                    var history = SessionSummaryRequestBuilder.History(plan, configuration.Model, Guid.CreateVersion7().ToString(), summaryOptions);
                    var prefix = plan.IsSplitTurn && !plan.TurnPrefixMessages.IsEmpty
                        ? SessionSummaryRequestBuilder.TurnPrefix(plan, configuration.Model, Guid.CreateVersion7().ToString(), summaryOptions) : null;
                    if (provided is null)
                    {
                        if (prefix is null) generated = await GenerateRetryingSummaryAsync(generator, history, work, "compaction", request.Reason).ConfigureAwait(false);
                        else
                        {
                            var priorText = plan.PreviousSummary ?? "No prior history."; TokenUsage? priorUsage = null;
                            if (!plan.MessagesToSummarize.IsEmpty)
                            {
                                var result = await GenerateRetryingSummaryAsync(generator, history, work, "compaction", request.Reason).ConfigureAwait(false);
                                ValidateGenerated(result); work.ThrowIfCancellationRequested(); priorText = result.Text; priorUsage = result.Usage;
                            }
                            var resultPrefix = await GenerateRetryingSummaryAsync(generator, prefix, work, "compaction", request.Reason).ConfigureAwait(false); ValidateGenerated(resultPrefix);
                            generated = new(priorText + "\n\n---\n\n**Turn Context (split turn):**\n\n" + resultPrefix.Text,
                                priorUsage is null ? resultPrefix.Usage : CombineSummaryUsage(priorUsage, resultPrefix.Usage));
                        }
                    }
                }
                else
                {
                    if (!double.IsFinite(branchRequest!.ContextWindow) || branchRequest.ContextWindow <= 0 ||
                        !double.IsFinite(branchRequest.ReserveTokens) || branchRequest.ReserveTokens < 0 ||
                        branchRequest.TargetId is not null && !log.ById.ContainsKey(branchRequest.TargetId))
                        throw new SessionCompactionException(SessionCompactionFailure.InvalidBoundary);
                    var planner = new SessionBranchSummaryPlanner();
                    var collection = branchRequest.TargetId is null ? new SessionBranchSummaryCollection(previous.Ancestry, null)
                        : planner.Collect(log.Entries, previous.LeafId, branchRequest.TargetId, work);
                    branchPlan = planner.Prepare(collection.Entries, branchRequest.ContextWindow - branchRequest.ReserveTokens, collection.CommonAncestorId, work);
                    parent = branchRequest.TargetId; firstKept = null; provided = branchRequest.ExtensionSummary; files = branchPlan.FileOps;
                    fromExtension = provided is not null;
                    if (provided is null)
                    {
                        if (branchPlan.Messages.IsEmpty) generated = new("No content to summarize", TokenUsage.Zero);
                        else
                        {
                            var result = await GenerateRetryingSummaryAsync(generator, SessionSummaryRequestBuilder.Branch(branchPlan, configuration.Model,
                                Guid.CreateVersion7().ToString(), branchRequest.SummaryOptions), work, "branchSummary", null).ConfigureAwait(false);
                            ValidateGenerated(result); generated = result with { Text = SessionSummaryRequestBuilder.BranchPreamble + result.Text };
                        }
                    }
                }
                work.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    ThrowAvailable();
                    if (!ReferenceEquals(_context, previous) || !ReferenceEquals(_acknowledgedLog, log) ||
                        !ReferenceEquals(_active, idle) || _context.LeafId != previous.LeafId)
                        throw new SessionCompactionException(SessionCompactionFailure.StaleSelection);
                }
                string text; TokenUsage? usage; JsonData? details;
                if (provided is not null) { text = provided.Text; usage = provided.Usage; details = provided.Details; }
                else
                {
                    ValidateGenerated(generated!); var (read, modified) = files.ComputeFileLists();
                    text = generated!.Text + (branchPlan is { Messages.IsEmpty: true } ? "" : SessionFileOperations.FormatFileOperations(read, modified));
                    usage = branchPlan is { Messages.IsEmpty: true } ? null : generated.Usage;
                    details = branchPlan is { Messages.IsEmpty: true } ? null : JsonData.Parse(JsonSerializer.Serialize(new { readFiles = read, modifiedFiles = modified }));
                }
                // Inert shape/bounds/usage/system checkpoint validation occurs before trusted author callbacks.
                _ = SummaryRecord(request is not null, "summary-validation", parent, firstKept, tokensBefore, text, usage, details,
                    provided is not null, previous, () => 0);
                work.ThrowIfCancellationRequested(); var id = Identity(_nextEntryId, log.Header.Id, log.Entries); work.ThrowIfCancellationRequested();
                var entry = SummaryRecord(request is not null, id, parent, firstKept, tokensBefore, text, usage, details,
                    provided is not null, previous, _clock);
                var prospective = _projector.Project(log.Entries.Add(entry), entry.Id, work);
                ValidateRuntimeContext(prospective, configuration, _toleratedSelection, _toleratedThinking);
                await using (var probe = new NativeAgent(configuration, _clock, new NoopSink(), _agentOptions))
                    probe.ConfigureAndReplaceMessages(configuration, SessionContextProjector.AgentMessages(prospective));
                if (preflight is not null) await preflight(new(entry, prospective, log), work).ConfigureAwait(false);
                if (lifecycle)
                {
                    var result = CompactionResult(entry, prospective);
                    await EmitCompactionAsync(lifecycleSubscriptions, new SessionCompactionPrepared(operation, request!.Reason,
                        result, request.WillRetry)).ConfigureAwait(false);
                }
                work.ThrowIfCancellationRequested(); writeAdmitted = true;
                var acknowledged = await _store.AppendAsync([entry], work).ConfigureAwait(false);
                if (!acknowledged.CheckpointAcknowledged) throw Error(PersistentAgentSessionFailure.InvalidCommit);
                lock (_gate)
                {
                    _agent.ConfigureAndReplaceMessages(RecoveryConfiguration(configuration), SessionContextProjector.AgentMessages(prospective));
                    _acknowledgedLog = acknowledged.Snapshot; _context = prospective;
                }
                completed = new(acknowledged.Entries.Single(), acknowledged, prospective, plan, branchPlan);
                if (lifecycle) originalResult = CompactionResult(completed.Entry, completed.Context);
                if (request is not null)
                {
                    // Pi searches all saved entries in storage order, including older identical summaries.
                    var saved = acknowledged.Snapshot.Entries.FirstOrDefault(candidate => candidate.Kind == SessionEntryKind.Compaction &&
                        candidate.WireBody.Value.GetProperty("summary").GetString() == text);
                    if (saved is not null) observation = new(saved, provided is not null, request.Reason, request.WillRetry);
                }
            }
            catch (SessionLogStoreException storage)
            {
                if (storage.Failure == SessionLogStoreFailure.ResourceLimit && !storage.MayHaveWritten)
                    throw new SessionCompactionException(SessionCompactionFailure.ResourceLimit);
                var fault = new PersistentAgentSessionFault(PersistentAgentSessionFailure.AppendFailed, storage.Failure, storage.MayHaveWritten, storage.DurableFlushCompleted);
                if (storage.MayHaveWritten || _store.IsPoisoned) lock (_gate) _fault ??= fault;
                throw new PersistentAgentSessionException(fault);
            }
            catch
            {
                if (writeAdmitted) lock (_gate) _fault ??= new(PersistentAgentSessionFailure.InvalidCommit);
                throw;
            }
            // Outside the write-fault catch, but still inside original commit/reservation/reentrancy ownership.
            if (observation is not null && observer is not null) await observer(observation).ConfigureAwait(false);
            return completed;
        }
        catch (Exception error)
        {
            bodyFailure = error; AddDistinctFailure(failures, error);
            // Cancellation provenance is captured before disposing the original linked sources.
            aborted = cancelledByExtension || token.IsCancellationRequested || _closing.IsCancellationRequested || inputAbort.IsCancellationRequested || abort.Abort.IsCancellationRequested;
            throw;
        }
        finally
        {
            if (commitHeld)
                try { _commits.Release(); } catch (Exception error) { AddDistinctFailure(failures, error); }
            Task cancelIdle;
            lock (_gate)
            {
                if (ReferenceEquals(_contextEditCancellation, abort)) _contextEditCancellation = null;
                cancelIdle = abort.CancelUsers == 0 ? Task.CompletedTask : abort.CancelIdle!.Task;
            }
            try { await cancelIdle.ConfigureAwait(false); } catch (Exception error) { AddDistinctFailure(failures, error); }
            try { abort.Abort.Dispose(); } catch (Exception error) { AddDistinctFailure(failures, error); }
            lock (_gate) if (ReferenceEquals(_active, idle)) { if (releaseReservation) _active = null; _compacting = false; }
            try
            {
                if (started)
                {
                    var failure = bodyFailure ?? failures.FirstOrDefault();
                    var success = failure is null && completed is not null;
                    string? message = null;
                    if (!success && !aborted)
                    {
                        var cause = failure?.Message ?? noPlanMessage ?? "Compaction failed";
                        message = (request!.Reason switch
                        {
                            SessionCompactionReason.Manual => "Compaction failed: ",
                            SessionCompactionReason.Threshold => "Auto-compaction failed: ",
                            SessionCompactionReason.Overflow => "Context overflow recovery failed: ",
                            _ => throw new ArgumentOutOfRangeException(nameof(request))
                        }) + cause;
                    }
                    await EmitCompactionAsync(lifecycleSubscriptions, new SessionCompactionEnded(operation, request!.Reason,
                        success ? originalResult : null, !success && aborted, success && request.WillRetry, message) { FromExtension = fromExtension }).ConfigureAwait(false);
                }
            }
            catch (Exception error) { AddDistinctFailure(failures, error); }
            finally { if (releaseReservation) idle.TrySetResult(); _configurationCallback.Value = priorCallback; }
            if (failures.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures.Count > 1) throw new AggregateException("Compaction body and owned settlement failed.", failures);
        }
    }
    private SessionEntry SummaryRecord(bool compaction, string id, string? parent, string? firstKept,
        double tokensBefore, string text, TokenUsage? usage, JsonData? details, bool fromHook,
        SessionContextProjection previous, Func<long> clock)
    {
        if (text is null || text.Length > 1_048_576 || !double.IsFinite(tokensBefore) || tokensBefore < 0 ||
            details?.ToString().Length > 1_048_576) throw new SessionCompactionException(SessionCompactionFailure.ResourceLimit);
        JsonData? usageWire = null;
        if (usage is not null)
        {
            if (usage.Input < 0 || usage.Output < 0 || usage.CacheRead < 0 || usage.CacheWrite < 0 || usage.TotalTokens < 0)
                throw new SessionCompactionException(SessionCompactionFailure.UnsupportedNumber);
            usageWire = JsonData.FromElement(PiWireJson.WriteMessage(new("summary", "summary", "summary", 0, [], usage, StopReason.Stop)).Value.GetProperty("usage"));
        }
        var timestamp = clock(); var system = compaction ? new SessionSystemReplay().Replay(previous.Messages).CurrentMessage : null;
        return Record(_codec, compaction ? "compaction" : "branch_summary", id, parent, () => timestamp, writer =>
        {
            if (!compaction) writer.WriteString("fromId", previous.LeafId ?? "root");
            writer.WriteString("summary", text);
            if (compaction) { writer.WriteString("firstKeptEntryId", firstKept ?? id); writer.WriteNumber("tokensBefore", tokensBefore); }
            if (details is not null) { writer.WritePropertyName("details"); writer.WriteRawValue(details.ToString(), skipInputValidation: true); }
            if (usageWire is not null) { writer.WritePropertyName("usage"); writer.WriteRawValue(usageWire.ToString(), skipInputValidation: true); }
            writer.WriteBoolean("fromHook", fromHook);
            if (system is not null)
            {
                writer.WritePropertyName("systemMessage"); writer.WriteStartObject();
                foreach (var property in system.WireBody.Value.EnumerateObject())
                    if (property.Name != "timestamp") { writer.WritePropertyName(property.Name); writer.WriteRawValue(property.Value.GetRawText(), skipInputValidation: true); }
                writer.WriteNumber("timestamp", timestamp); writer.WriteEndObject();
            }
        }, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
    }
    private static void ValidateGenerated(SessionGeneratedSummary summary)
    {
        if (summary is null || summary.Text is null || summary.Usage is null) throw new SessionCompactionException(SessionCompactionFailure.SummaryFailed);
        if (summary.Text.Length > 1_048_576) throw new SessionCompactionException(SessionCompactionFailure.ResourceLimit);
        var usage = summary.Usage;
        if (new[] { usage.Input, usage.Output, usage.CacheRead, usage.CacheWrite, usage.TotalTokens }.Any(value => value is < 0 or > 9_007_199_254_740_991))
            throw new SessionCompactionException(SessionCompactionFailure.UnsupportedNumber);
        var wire = PiWireJson.WriteMessage(new("summary", "summary", "summary", 0, [], usage, StopReason.Stop)).Value.GetProperty("usage").GetProperty("cost");
        foreach (var name in new[] { "input", "output", "cacheRead", "cacheWrite", "total" })
            if (!wire.GetProperty(name).TryGetDouble(out var value) || !double.IsFinite(value) || value < 0)
                throw new SessionCompactionException(SessionCompactionFailure.UnsupportedNumber);
    }
    private static async ValueTask<SessionGeneratedSummary> GenerateSummaryAsync(ISessionSummaryGenerator generator,
        SessionSummaryRequest request, CancellationToken token)
    {
        try { var summary = await generator.GenerateAsync(request, token).ConfigureAwait(false); ValidateGenerated(summary); return summary; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (SessionCompactionException) { throw; }
        // agent-session.ts compact()/_runAutoCompaction: a summarizer that throws reports its own error message
        // ("Compaction failed: <message>"); thrown errors are never retried (retryAssistantCall only retries error responses).
        catch (Exception error) { throw new SessionCompactionException(SessionCompactionFailure.SummaryFailed) { FailureText = error.Message }; }
    }
    private static TokenUsage CombineSummaryUsage(TokenUsage left, TokenUsage right)
    {
        // Source combines binary64 costs; retain that exact representation alongside the typed decimal contract.
        var leftWire = PiWireJson.WriteMessage(new("summary", "summary", "summary", 0, [], left, StopReason.Stop)).Value.GetProperty("usage").GetProperty("cost");
        var rightWire = PiWireJson.WriteMessage(new("summary", "summary", "summary", 0, [], right, StopReason.Stop)).Value.GetProperty("usage").GetProperty("cost");
        var costs = new Dictionary<string, double>(); foreach (var name in new[] { "input", "output", "cacheRead", "cacheWrite", "total" })
        {
            var value = leftWire.GetProperty(name).GetDouble() + rightWire.GetProperty(name).GetDouble();
            if (!double.IsFinite(value) || value < 0) throw new SessionCompactionException(SessionCompactionFailure.UnsupportedNumber); costs.Add(name, value);
        }
        var extras = JsonFields.Empty;
        foreach (var name in new[] { "cacheWrite1h", "reasoning" })
        {
            JsonData? first = null, second = null;
            var hasFirst = left.ExtraProperties?.TryGet(name, out first) == true;
            var hasSecond = right.ExtraProperties?.TryGet(name, out second) == true;
            if (!hasFirst && !hasSecond) continue;
            double Number(JsonData? field)
            {
                if (field is null || field.Value.ValueKind == JsonValueKind.Null) return 0;
                if (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetDouble(out var number) ||
                    !double.IsFinite(number) || number < 0 || number > 9_007_199_254_740_991)
                    throw new SessionCompactionException(SessionCompactionFailure.UnsupportedNumber);
                return number;
            }
            var combined = Number(first) + Number(second);
            if (!double.IsFinite(combined) || combined > 9_007_199_254_740_991)
                throw new SessionCompactionException(SessionCompactionFailure.UnsupportedNumber);
            extras = extras.Set(name, JsonData.Parse(JsonSerializer.Serialize(combined)));
        }
        var data = JsonData.Parse(JsonSerializer.Serialize(costs)); decimal D(string name) => data.Value.GetProperty(name).GetDecimal();
        try { return new(checked(left.Input + right.Input), checked(left.Output + right.Output), checked(left.CacheRead + right.CacheRead),
            checked(left.CacheWrite + right.CacheWrite), checked(left.TotalTokens + right.TotalTokens),
            new(D("input"), D("output"), D("cacheRead"), D("cacheWrite"), D("total"), SourceBinary64Cost: data), extras) { ExtrasBeforeTotal = true }; }
        catch (OverflowException) { throw new SessionCompactionException(SessionCompactionFailure.UnsupportedNumber); }
    }
}
