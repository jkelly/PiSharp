// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/agent/src/agent-loop.ts.
using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading.Channels;
using PiSharp.Contracts;

namespace PiSharp.Agent;

/// <summary>
/// Runs one finalized assistant tool batch. This is not a complete agent loop, session coordinator,
/// policy broker, or host tool implementation. No state lock is held across user code.
/// </summary>
public sealed class ToolBatchScheduler
{
    private readonly ImmutableDictionary<string, ToolDefinition> tools;
    private readonly IToolHooks? hooks;
    private readonly ToolExecutionMode executionMode;
    private readonly ToolProgressDeliveryOptions progressOptions;
    private readonly ToolResultValueOptions resultOptions;
    private static readonly AsyncLocal<object?> ProgressDelivery = new();
    /// <summary>Opt-in monotonic clock for tool execution durations (durationMs); null records none. Prepared
    /// invocations carry it to finalized executors, which time the tool's own execution.</summary>
    public TimeProvider? TimeProvider { get; init; }

    public ToolBatchScheduler(IEnumerable<ToolDefinition> tools, IToolHooks? hooks = null,
        ToolExecutionMode executionMode = ToolExecutionMode.Parallel, ToolProgressDeliveryOptions? progressOptions = null,
        ToolResultValueOptions? resultOptions = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        if (!Enum.IsDefined(executionMode)) throw new ArgumentOutOfRangeException(nameof(executionMode));
        var builder = ImmutableDictionary.CreateBuilder<string, ToolDefinition>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            ArgumentNullException.ThrowIfNull(tool);
            ArgumentException.ThrowIfNullOrWhiteSpace(tool.Name);
            ArgumentNullException.ThrowIfNull(tool.Executor);
            if (!Enum.IsDefined(tool.ExecutionMode)) throw new ArgumentOutOfRangeException(nameof(tools));
            if (!builder.TryAdd(tool.Name, tool)) throw new ArgumentException($"Duplicate tool: {tool.Name}", nameof(tools));
        }
        this.tools = builder.ToImmutable();
        this.hooks = hooks;
        this.executionMode = executionMode;
        this.progressOptions = progressOptions ?? new();
        this.progressOptions.Validate();
        this.resultOptions = ToolResultValueCodec.ValidateLimits(resultOptions ?? ToolResultValueOptions.ExecutionBoundary);
    }

    public async Task<ToolBatchResult> RunAsync(AssistantMessage message, IAgentEventSink sink,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(sink);
        if (message.StopReason is StopReason.Pending or StopReason.Deferred)
            throw new ArgumentException("A finalized assistant message is required.", nameof(message));
        if (message.Content.IsDefault) throw new ArgumentException("Content must be initialized.", nameof(message));

        // Failed snapshots can retain unfinished tool identities as nonexecutable history.
        // Their authoritative assistant commit is still awaited; executable preflight is not admitted.
        var failed = message.StopReason is StopReason.Error or StopReason.Aborted;
        var invocations = failed ? [] : Invocations(message, sink);
        // The caller can commit the assistant here. Preflight cannot run until that work settles.
        var ended = new AssistantMessageEnded(message);
        await sink.EmitAsync(ended, cancellationToken).ConfigureAwait(false);
        // Source: message_end handlers mutate the finalized message in place before the loop reads its stop reason and tool calls.
        AssistantMessage? replaced = null;
        if (AgentMessageReplacement.Get(ended) is { } replacement)
        {
            message = replaced = PiWireJson.ReadMessage(replacement.WireBody.Value);
            failed = message.StopReason is StopReason.Error or StopReason.Aborted or StopReason.Pending or StopReason.Deferred;
            invocations = failed ? [] : Invocations(message, sink);
        }
        if (failed) return new([], [], message.StopReason == StopReason.Aborted || cancellationToken.IsCancellationRequested) { Assistant = replaced };
        if (cancellationToken.IsCancellationRequested) return new([], [], true) { Assistant = replaced };
        return await RunInvocationsAsync(invocations, sink, cancellationToken).ConfigureAwait(false) with { Assistant = replaced };
    }

    private static ImmutableArray<ToolInvocation> Invocations(AssistantMessage message, IAgentEventSink sink)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var invocations = message.Content.Select((content, index) => (content, index))
            .Where(entry => entry.content is ToolCallContent)
            .Select(entry => new ToolInvocation(message, (ToolCallContent)entry.content, entry.index) { EventSink = sink }).ToImmutableArray();
        foreach (var invocation in invocations)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(invocation.Call.Id);
            ArgumentException.ThrowIfNullOrWhiteSpace(invocation.Call.Name);
            ArgumentNullException.ThrowIfNull(invocation.Call.Arguments);
            if (!ids.Add(invocation.Call.Id))
                throw new ArgumentException($"Duplicate tool call ID: {invocation.Call.Id}", nameof(message));
        }
        return invocations;
    }

    private async Task<ToolBatchResult> RunInvocationsAsync(ImmutableArray<ToolInvocation> invocations, IAgentEventSink sink,
        CancellationToken cancellationToken)
    {

        var sequential = executionMode == ToolExecutionMode.Sequential || invocations.Any(invocation =>
            tools.TryGetValue(invocation.Call.Name, out var tool) && tool.ExecutionMode == ToolExecutionMode.Sequential);
        var deliveryOwner = new object();
        var budget = progressOptions.Mode == ToolProgressDeliveryMode.SourceCompatible ? new ToolProgressBudget(progressOptions, resultOptions) : null;
        using var batchLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delivery = new BatchEventSink(sink, deliveryOwner, batchLifetime);
        invocations = invocations.Select(value => value with { EventSink = delivery, Clock = TimeProvider }).ToImmutableArray();
        try
        {
            var result = sequential
                ? await RunSequentialAsync(invocations, delivery, deliveryOwner, budget, batchLifetime.Token).ConfigureAwait(false)
                : budget is not null ? await RunSourceParallelAsync(invocations, delivery, deliveryOwner, budget, batchLifetime.Token).ConfigureAwait(false)
                : await RunParallelAsync(invocations, delivery, deliveryOwner, batchLifetime.Token).ConfigureAwait(false);
            delivery.ThrowIfFailed();
            return result;
        }
        catch
        {
            // Tool executors may convert exceptions to error outcomes. Listener failure remains a batch failure.
            // Every scheduler branch has joined admitted execution/progress before reaching this boundary.
            delivery.ThrowIfFailed();
            throw;
        }
    }

    private sealed class BatchEventSink(IAgentEventSink inner, object owner, CancellationTokenSource lifetime) : IAgentEventSink
    {
        private Exception? failure;
        internal void ThrowIfFailed()
        {
            if (Volatile.Read(ref failure) is { } error) ExceptionDispatchInfo.Capture(error).Throw();
        }
        public async ValueTask EmitAsync(AgentEvent observation, CancellationToken token)
        {
            ThrowIfFailed();
            try { await EmitProgressAsync(inner, observation, owner, token).ConfigureAwait(false); }
            catch (Exception error)
            {
                Interlocked.CompareExchange(ref failure, error, null);
                // Cancel actual execution immediately, including source-style reporters waiting on cancellation.
                try { await lifetime.CancelAsync().ConfigureAwait(false); }
                catch (Exception) { /* The first listener failure remains authoritative. */ }
                ThrowIfFailed();
                throw;
            }
        }
    }

    private async Task<ToolBatchResult> RunSequentialAsync(ImmutableArray<ToolInvocation> invocations,
        IAgentEventSink sink, object deliveryOwner, ToolProgressBudget? budget, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var outcomes = ImmutableArray.CreateBuilder<ToolOutcome>();
        var messages = ImmutableArray.CreateBuilder<ToolResultMessage>();
        foreach (var invocation in invocations)
        {
            if (cancellationToken.IsCancellationRequested) break;
            await sink.EmitAsync(new ToolExecutionStarted(invocation), cancellationToken).ConfigureAwait(false);
            var prepared = await PrepareAsync(invocation, cancellationToken).ConfigureAwait(false);
            Exception? sinkFailure = null;
            var outcome = prepared.Immediate ?? await ExecuteAsync(invocation, prepared.Tool!, async (partial, token) =>
            {
                if (ReferenceEquals(ProgressDelivery.Value, deliveryOwner)) throw ProgressSelfWait();
                try { await EmitProgressAsync(sink, new ToolExecutionUpdated(invocation, partial), deliveryOwner, token).ConfigureAwait(false); }
                catch (Exception error)
                {
                    sinkFailure = error;
                    try { await lifetime.CancelAsync().ConfigureAwait(false); }
                    catch (Exception cancelError) { sinkFailure = new AggregateException(error, cancelError); }
                    throw;
                }
            }, lifetime.Token, budget).ConfigureAwait(false);
            if (sinkFailure is not null) ExceptionDispatchInfo.Capture(sinkFailure).Throw();
            await sink.EmitAsync(new ToolExecutionEnded(outcome), cancellationToken).ConfigureAwait(false);
            outcomes.Add(outcome);
            messages.Add(await EmitMessageAsync(outcome, sink, cancellationToken).ConfigureAwait(false));
            if (cancellationToken.IsCancellationRequested) break;
        }
        return CreateBatch(outcomes.ToImmutable(), messages.ToImmutable(), cancellationToken);
    }

    private async Task<ToolBatchResult> RunSourceParallelAsync(ImmutableArray<ToolInvocation> invocations,
        IAgentEventSink sink, object deliveryOwner, ToolProgressBudget budget, CancellationToken cancellationToken)
    {
        var ordered = new List<(ToolInvocation Invocation, Preparation Preparation)>();
        foreach (var invocation in invocations)
        {
            if (cancellationToken.IsCancellationRequested) break;
            await sink.EmitAsync(new ToolExecutionStarted(invocation), cancellationToken).ConfigureAwait(false);
            var preparation = await PrepareAsync(invocation, cancellationToken).ConfigureAwait(false);
            ordered.Add((invocation, preparation));
            if (preparation.Immediate is { } immediate)
                await sink.EmitAsync(new ToolExecutionEnded(immediate), cancellationToken).ConfigureAwait(false);
        }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var failureGate = new object();
        Exception? sinkFailure = null;
        async ValueTask FailedAsync(Exception error)
        {
            lock (failureGate) sinkFailure ??= error;
            try { await lifetime.CancelAsync().ConfigureAwait(false); }
            catch (Exception cancelError) { lock (failureGate) sinkFailure = new AggregateException(sinkFailure!, cancelError); }
        }
        async Task<ToolOutcome> ExecuteAndEndAsync(ToolInvocation invocation, Preparation prepared)
        {
            if (prepared.Immediate is { } immediate) return immediate;
            ToolOutcome outcome;
            try { outcome = await ExecuteAsync(invocation, prepared.Tool!, async (partial, token) =>
            {
                if (ReferenceEquals(ProgressDelivery.Value, deliveryOwner)) throw ProgressSelfWait();
                try { await EmitProgressAsync(sink, new ToolExecutionUpdated(invocation, partial), deliveryOwner, token).ConfigureAwait(false); }
                catch (Exception error) { await FailedAsync(error).ConfigureAwait(false); throw; }
            }, lifetime.Token, budget).ConfigureAwait(false); }
            catch (Exception error) { await FailedAsync(error).ConfigureAwait(false); throw; }
            lock (failureGate) if (sinkFailure is not null) return outcome;
            try { await EmitProgressAsync(sink, new ToolExecutionEnded(outcome), deliveryOwner, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) { await FailedAsync(error).ConfigureAwait(false); }
            return outcome;
        }
        // Each tool closes and joins its own admitted progress before its after hook/end.
        // There is no shared event consumer that could hold an unrelated tool behind a listener.
        var executions = ordered.Select(entry => ExecuteAndEndAsync(entry.Invocation, entry.Preparation)).ToArray();
        ToolOutcome[] completed;
        try { completed = await Task.WhenAll(executions).ConfigureAwait(false); }
        catch
        {
            if (sinkFailure is not null) ExceptionDispatchInfo.Capture(sinkFailure).Throw();
            throw;
        }
        var outcomes = completed.ToImmutableArray();
        if (sinkFailure is not null) ExceptionDispatchInfo.Capture(sinkFailure).Throw();
        var messages = ImmutableArray.CreateBuilder<ToolResultMessage>();
        foreach (var outcome in outcomes)
            messages.Add(await EmitMessageAsync(outcome, sink, cancellationToken).ConfigureAwait(false));
        return CreateBatch(outcomes, messages.ToImmutable(), cancellationToken);
    }

    private async Task<ToolBatchResult> RunParallelAsync(ImmutableArray<ToolInvocation> invocations,
        IAgentEventSink sink, object deliveryOwner, CancellationToken cancellationToken)
    {
        var ordered = new List<(ToolInvocation Invocation, Preparation Preparation)>();
        foreach (var invocation in invocations)
        {
            if (cancellationToken.IsCancellationRequested) break;
            await sink.EmitAsync(new ToolExecutionStarted(invocation), cancellationToken).ConfigureAwait(false);
            var preparation = await PrepareAsync(invocation, cancellationToken).ConfigureAwait(false);
            ordered.Add((invocation, preparation));
            if (preparation.Immediate is { } immediate)
                await sink.EmitAsync(new ToolExecutionEnded(immediate), cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) break;
        }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // One queue slot, one pending update per invocation, and one execution task per admitted call.
        // Producers await delivery acknowledgement; updates and final ends share this single consumer.
        var publications = Channel.CreateBounded<Publication>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        var executions = ordered.Where(entry => entry.Preparation.Immediate is null)
            .Select(entry => ExecuteAndPublishAsync(entry.Invocation, entry.Preparation.Tool!, publications.Writer, deliveryOwner, lifetime.Token))
            .ToArray();
        var finalized = new Dictionary<int, ToolOutcome>();
        foreach (var entry in ordered)
            if (entry.Preparation.Immediate is { } immediate) finalized.Add(entry.Invocation.SourceIndex, immediate);

        try
        {
            var remaining = executions.Length;
            while (remaining > 0)
            {
                var publication = await publications.Reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
                if (publication.Update is { } update)
                {
                    try
                    {
                        await EmitProgressAsync(sink, update, deliveryOwner, cancellationToken).ConfigureAwait(false);
                        publication.Delivered!.TrySetResult();
                    }
                    catch (Exception error) { publication.Delivered!.TrySetException(error); throw; }
                    continue;
                }
                var outcome = publication.Outcome!;
                finalized.Add(outcome.Invocation.SourceIndex, outcome);
                // A single consumer dispatches observations in observed finalization order, even with a slow sink.
                await EmitProgressAsync(sink, new ToolExecutionEnded(outcome), deliveryOwner, cancellationToken).ConfigureAwait(false);
                remaining--;
            }
            await Task.WhenAll(executions).ConfigureAwait(false);
        }
        catch (Exception sinkError)
        {
            // Release queued acknowledgements and blocked writers before joining tools that await them.
            publications.Writer.TryComplete(sinkError);
            while (publications.Reader.TryRead(out var pending)) pending.Delivered?.TrySetException(sinkError);
            // Sink failure must settle effects already admitted before reporting failure to the caller.
            Exception? cancellationError = null;
            try { await lifetime.CancelAsync().ConfigureAwait(false); }
            catch (Exception error) { cancellationError = error; }
            await Task.WhenAll(executions).ConfigureAwait(false);
            if (cancellationError is not null) throw new AggregateException(sinkError, cancellationError);
            throw;
        }
        finally { publications.Writer.TryComplete(); }

        var outcomes = ordered.Select(entry => finalized[entry.Invocation.SourceIndex]).ToImmutableArray();
        var messages = ImmutableArray.CreateBuilder<ToolResultMessage>();
        foreach (var outcome in outcomes)
            messages.Add(await EmitMessageAsync(outcome, sink, cancellationToken).ConfigureAwait(false));
        return CreateBatch(outcomes, messages.ToImmutable(), cancellationToken);
    }

    private async Task ExecuteAndPublishAsync(ToolInvocation invocation, ToolDefinition tool,
        ChannelWriter<Publication> publications, object deliveryOwner, CancellationToken cancellationToken)
    {
        var outcome = await ExecuteAsync(invocation, tool, async (partial, token) =>
        {
            if (ReferenceEquals(ProgressDelivery.Value, deliveryOwner)) throw ProgressSelfWait();
            var publication = new Publication(new ToolExecutionUpdated(invocation, partial));
            await publications.WriteAsync(publication, token).ConfigureAwait(false);
            // Once enqueued, this observation must settle before the invocation can emit its final end.
            await publication.Delivered!.Task.ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        try { await publications.WriteAsync(new Publication(outcome), CancellationToken.None).ConfigureAwait(false); }
        catch (ChannelClosedException) { /* The consumer fault path owns cancellation and joins this execution. */ }
    }

    // This is the sole preflight path for both execution modes. Arguments of any JSON kind reach the executor, whose
    // validateToolArguments gives upstream's result for a non-object.
    private async ValueTask<Preparation> PrepareAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        if (invocation.AssistantMessage.StopReason == StopReason.Length)
            return Immediate(ToolFailureKind.Truncated, "Tool call came from an output-length-truncated assistant message.");
        if (!tools.TryGetValue(invocation.Call.Name, out var tool))
            return Immediate(ToolFailureKind.UnknownTool, $"Tool {invocation.Call.Name} not found");
        if (cancellationToken.IsCancellationRequested) return Immediate(ToolFailureKind.Canceled, "Operation aborted");
        try
        {
            var decision = hooks is null ? ToolPreflightDecision.Allow :
                await hooks.BeforeExecutionAsync(invocation, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) return Immediate(ToolFailureKind.Canceled, "Operation aborted");
            if (decision.Block)
                return new(null, new(invocation, ToolResult.Error(ToolFailureKind.Blocked,
                    decision.Reason ?? "Tool execution was blocked", decision.Terminate)));
            return new(tool, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return Immediate(ToolFailureKind.Canceled, "Operation aborted"); }
        catch (Exception error) { return Immediate(ToolFailureKind.HookError, error.Message); }

        Preparation Immediate(ToolFailureKind kind, string text) => new(null, new(invocation, ToolResult.Error(kind, text)));
    }

    private async ValueTask<ToolOutcome> ExecuteAsync(ToolInvocation invocation, ToolDefinition tool,
        ToolProgressCallback onProgress, CancellationToken cancellationToken, ToolProgressBudget? budget = null)
    {
        if (cancellationToken.IsCancellationRequested)
            return new(invocation, ToolResult.Error(ToolFailureKind.Canceled, "Operation aborted"));
        ToolResult result;
        bool? finalizedIsError = null;
        JsonData? nestedCalls = null, nestedUsage = null;
        long? durationMs = null;
        var progress = new ToolProgressScope((partial, token) => onProgress(Normalize(partial), token), cancellationToken, budget);
        // A plain executor is the tool's execute(): its time is measured before admitted progress is joined.
        // A finalized executor owns its pipeline and reports the time of the tool's own execution, if it ran.
        var finalizedExecutor = tool.Executor as IFinalizedToolExecutor;
        var clock = invocation.Clock;
        var started = clock?.GetTimestamp() ?? 0;
        try
        {
            try
            {
                if (finalizedExecutor is not null)
                {
                    var execution = await finalizedExecutor.ExecuteFinalizedAsync(invocation, progress.ReportAsync, cancellationToken).ConfigureAwait(false);
                    durationMs = execution.DurationMs;
                    result = Normalize(execution.Result);
                    finalizedIsError = execution.IsError;
                    nestedCalls = execution.NestedCalls;
                    nestedUsage = execution.NestedUsage;
                }
                else result = Normalize(await tool.Executor.ExecuteAsync(invocation, progress.ReportAsync, cancellationToken).ConfigureAwait(false));
            }
            finally
            {
                if (finalizedExecutor is null && clock is not null) durationMs = ElapsedMilliseconds(clock, started);
                await progress.CloseAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { result = ToolResult.Error(ToolFailureKind.Canceled, "Operation aborted"); finalizedIsError = null; }
        catch (Exception error) { result = ToolResult.Error(ToolFailureKind.ExecutionError, error.Message); finalizedIsError = null; }

        if (budget is not null && progress.DeliveryFailure is { } failure)
            ExceptionDispatchInfo.Capture(failure).Throw();

        var outcome = new ToolOutcome(invocation, result) { IsError = finalizedIsError ?? result.IsError, NestedCalls = nestedCalls,
            NestedUsage = nestedUsage, DurationMs = durationMs };
        if (hooks is not null)
        {
            // After-hook replacements and failures keep the execution's measured duration.
            try
            {
                if (hooks is ISourceToolHooks sourceHooks)
                    outcome = SourceToolAfterHook.Apply(outcome,
                        await sourceHooks.AfterToolCallAsync(invocation, result, outcome.IsError, cancellationToken).ConfigureAwait(false), resultOptions);
                else
                {
                    result = Normalize(await hooks.AfterExecutionAsync(invocation, result, cancellationToken).ConfigureAwait(false));
                    outcome = new(invocation, result) { NestedCalls = nestedCalls, NestedUsage = nestedUsage, DurationMs = durationMs };
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { outcome = new(invocation, ToolResult.Error(ToolFailureKind.Canceled, "Operation aborted")) { NestedCalls = nestedCalls, NestedUsage = nestedUsage, DurationMs = durationMs }; }
            catch (Exception error) { outcome = new(invocation, ToolResult.Error(ToolFailureKind.HookError, error.Message)) { NestedCalls = nestedCalls, NestedUsage = nestedUsage, DurationMs = durationMs }; }
        }
        return outcome;
    }

    /// <summary>Whole milliseconds of monotonic time since a timestamp of the same provider.</summary>
    internal static long ElapsedMilliseconds(TimeProvider time, long started) =>
        Math.Max(0, (long)Math.Round(time.GetElapsedTime(started).TotalMilliseconds, MidpointRounding.AwayFromZero));

    private ToolResult Normalize(ToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(result.Details);
        ToolResultValueCodec.Validate(result, resultOptions);
        return result;
    }

    private static ToolBatchResult CreateBatch(ImmutableArray<ToolOutcome> outcomes,
        ImmutableArray<ToolResultMessage> messages, CancellationToken cancellationToken) =>
        new(outcomes, messages, cancellationToken.IsCancellationRequested ||
            outcomes.Any(outcome => outcome.Result.Failure?.Kind == ToolFailureKind.Canceled));

    private async ValueTask<ToolResultMessage> EmitMessageAsync(ToolOutcome outcome,
        IAgentEventSink sink, CancellationToken cancellationToken)
    {
        var message = ToolResultMessageMaterializer.Create(outcome, resultOptions);
        await sink.EmitAsync(new ToolResultMessageStarted(message), cancellationToken).ConfigureAwait(false);
        await sink.EmitAsync(new ToolResultMessageEnded(message), cancellationToken).ConfigureAwait(false);
        return message;
    }

    private sealed record Preparation(ToolDefinition? Tool, ToolOutcome? Immediate);

    private sealed class Publication
    {
        public ToolExecutionUpdated? Update { get; }
        public ToolOutcome? Outcome { get; }
        public TaskCompletionSource? Delivered { get; }
        public Publication(ToolExecutionUpdated update)
        { Update = update; Delivered = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        public Publication(ToolOutcome outcome) => Outcome = outcome;
    }

    private static InvalidOperationException ProgressSelfWait() =>
        new("A tool event callback cannot publish progress to its own batch.");

    private static async ValueTask EmitProgressAsync(IAgentEventSink sink, AgentEvent observation, object owner, CancellationToken token)
    {
        var previous = ProgressDelivery.Value; ProgressDelivery.Value = owner;
        try { await sink.EmitAsync(observation, token).ConfigureAwait(false); }
        finally { ProgressDelivery.Value = previous; }
    }
}
