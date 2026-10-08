// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/agent/src/agent-loop.ts.
using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.Agent;

public enum PreparedToolActionKind { Path, Command, Extension }

/// <summary>Trusted adapter's resolved action. Policy and execution receive this identical immutable value.</summary>
public sealed record PreparedToolAction(
    string ToolName, string Operation, PreparedToolActionKind Kind, string Target, JsonData Arguments,
    ImmutableArray<string> CommandArguments, string? WorkingDirectory, ImmutableDictionary<string, string> Environment);

/// <summary>Trusted code resolves/normalizes arguments, validates action semantics, and faithfully executes the authorized action.</summary>
public interface IPreparedToolAdapter
{
    string Name { get; }
    ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken cancellationToken);
    ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken cancellationToken);
    ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken cancellationToken);
    ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, ToolProgressCallback onProgress,
        CancellationToken cancellationToken) => ExecuteAsync(action, cancellationToken);
}

/// <summary>Optional execution capability receiving the original invocation and identical authorized final action.</summary>
public interface IInvocationPreparedToolAdapter : IPreparedToolAdapter
{
    ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, PreparedToolAction action,
        ToolProgressCallback onProgress, CancellationToken cancellationToken);
}

/// <summary>Trusted, initial-only argument preparation. Hook replacements never invoke this capability.</summary>
public interface IInitialToolArgumentPreparationAdapter : IPreparedToolAdapter
{
    ValueTask<JsonData> PrepareInitialArgumentsAsync(ToolInvocation invocation, CancellationToken cancellationToken);
}

public sealed record ToolActionAuthorization(bool Allow, bool Terminate = false);
public interface IToolActionPolicy
{
    ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction finalAction,
        CancellationToken cancellationToken);
}
public delegate ValueTask<PreparedToolAction> ToolActionTransform(ToolInvocation invocation, PreparedToolAction action,
    CancellationToken cancellationToken);
public delegate ValueTask<ToolResult> ToolResultTransform(ToolInvocation invocation, PreparedToolAction finalAction,
    ToolResult result, CancellationToken cancellationToken);

public sealed record ToolInvokerOptions(int MaximumTools = 128, int MaximumTransforms = 16,
    int MaximumAssistantContentBlocks = 1024, int MaximumArgumentCharacters = 65_536,
    int MaximumActionCharacters = 65_536, int MaximumResultCharacters = 65_536, int MaximumJsonDepth = 32,
    int MaximumActionEntries = 128, int MaximumResultContentBlocks = 128)
{
    /// <summary>Host-captured model reachability. Null preserves the existing all-registered root profile.</summary>
    public ImmutableHashSet<string>? AllowedRootTools { get; init; }
    /// <summary>Host-captured nested reachability. Null preserves the existing all-registered callable profile.</summary>
    public ImmutableHashSet<string>? AllowedNestedTools { get; init; }
    /// <summary>Separate raw JSON cap: a 1 MiB output escaped at six characters per byte plus envelope headroom.</summary>
    public int MaximumStructuredContentCharacters { get; init; } = 6 * 1024 * 1024 + 65_536;
    public int MaximumResultRawCharacters { get; init; } = 12 * 1024 * 1024;
    public int MaximumResultRawBytes { get; init; } = 48 * 1024 * 1024;
}

/// <summary>
/// One trusted-adapter invocation: prepare/validate, transform, revalidate, authorize, execute, finalize.
/// Implements the accepted scheduler executor seam without supplying effects, schemas or a native-code sandbox.
/// </summary>
public sealed class ToolInvoker : IFinalizedToolExecutor
{
    private readonly ImmutableDictionary<string, IPreparedToolAdapter> _tools;
    private readonly ImmutableArray<string> _callableToolNames;
    private readonly ImmutableArray<ToolActionTransform> _transforms;
    private readonly ImmutableArray<ToolResultTransform> _resultTransforms;
    private readonly IToolActionPolicy _policy;
    private readonly ToolInvokerOptions _options;
    private readonly IPreparedToolHooks? _preparedHooks;
    private readonly ToolInvocationScopeOptions? _invocationScopes;
    private readonly object _nestedQueueGate = new();
    private Task _nestedQueueTail = Task.CompletedTask;

    /// <summary>Explicit host-owned broker configuration. Nested calls reenter this identical final-action pipeline.</summary>
    public static ToolInvoker WithNestedCalls(IEnumerable<IPreparedToolAdapter> tools, IToolActionPolicy policy,
        ToolInvocationScopeOptions invocationScopes, IPreparedToolHooks? preparedHooks = null,
        IEnumerable<ToolActionTransform>? transforms = null, IEnumerable<ToolResultTransform>? resultTransforms = null,
        ToolInvokerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(invocationScopes);
        invocationScopes.Validate();
        return new(tools, policy, transforms, resultTransforms, options, preparedHooks, invocationScopes);
    }

    public ToolInvoker(IEnumerable<IPreparedToolAdapter> tools, IToolActionPolicy policy,
        IEnumerable<ToolActionTransform>? transforms = null, IEnumerable<ToolResultTransform>? resultTransforms = null,
        ToolInvokerOptions? options = null)
        : this(tools, policy, transforms, resultTransforms, options, null) { }

    /// <summary>Explicit named opt-in preserves the existing constructor and target-typed calls.</summary>
    public static ToolInvoker WithPreparedHooks(IEnumerable<IPreparedToolAdapter> tools, IToolActionPolicy policy,
        IPreparedToolHooks preparedHooks, IEnumerable<ToolActionTransform>? transforms = null,
        IEnumerable<ToolResultTransform>? resultTransforms = null, ToolInvokerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(preparedHooks);
        return new(tools, policy, transforms, resultTransforms, options, preparedHooks);
    }

    private ToolInvoker(IEnumerable<IPreparedToolAdapter> tools, IToolActionPolicy policy,
        IEnumerable<ToolActionTransform>? transforms, IEnumerable<ToolResultTransform>? resultTransforms,
        ToolInvokerOptions? options, IPreparedToolHooks? preparedHooks, ToolInvocationScopeOptions? invocationScopes = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(policy);
        _options = (options ?? new ToolInvokerOptions()) with {
            AllowedRootTools = options?.AllowedRootTools?.ToImmutableHashSet(StringComparer.Ordinal),
            AllowedNestedTools = options?.AllowedNestedTools?.ToImmutableHashSet(StringComparer.Ordinal) };
        if (_options.MaximumTools <= 0 || _options.MaximumTransforms < 0 || _options.MaximumAssistantContentBlocks <= 0 ||
            _options.MaximumArgumentCharacters <= 0 || _options.MaximumActionCharacters <= 0 ||
            _options.MaximumResultCharacters <= 0 || _options.MaximumJsonDepth is < 1 or > 64 ||
            _options.MaximumActionEntries <= 0 || _options.MaximumResultContentBlocks <= 0 ||
            _options.MaximumStructuredContentCharacters <= 0 || _options.MaximumResultRawCharacters <= 0 || _options.MaximumResultRawBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Invalid tool invoker limits.");
        var registered = ImmutableDictionary.CreateBuilder<string, IPreparedToolAdapter>(StringComparer.Ordinal);
        var names = ImmutableArray.CreateBuilder<string>();
        foreach (var tool in tools)
        {
            var name = tool?.Name;
            if (tool is null || name is null || name.Length > _options.MaximumActionCharacters ||
                !ValidText(name, nonempty: true) || registered.Count >= _options.MaximumTools ||
                !registered.TryAdd(name, tool)) throw new ArgumentException("Invalid or duplicate prepared tool registration.", nameof(tools));
            names.Add(name);
        }
        _tools = registered.ToImmutable();
        foreach (var allowed in new[] { _options.AllowedRootTools, _options.AllowedNestedTools })
            if (allowed is not null && (allowed.Count > _options.MaximumTools ||
                allowed.Any(name => name is null || !registered.ContainsKey(name))))
                throw new ArgumentException("Tool reachability must name registered adapters only.", nameof(options));
        _callableToolNames = names.Where(name => _options.AllowedNestedTools?.Contains(name) ?? true).ToImmutableArray();
        _policy = policy;
        _transforms = Copy(transforms);
        _resultTransforms = Copy(resultTransforms);
        _preparedHooks = preparedHooks;
        _invocationScopes = invocationScopes;
    }

    public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken) =>
        ExecuteCompatibilityAsync(invocation, static (_, _) => ValueTask.CompletedTask, cancellationToken);

    public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, ToolProgressCallback onProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onProgress);
        return ExecuteCompatibilityAsync(invocation, onProgress, cancellationToken);
    }

    public ValueTask<FinalizedToolExecution> ExecuteFinalizedAsync(ToolInvocation invocation, ToolProgressCallback onProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onProgress);
        return ExecuteOwnedAsync(invocation, onProgress, cancellationToken);
    }

    private async ValueTask<ToolResult> ExecuteCompatibilityAsync(ToolInvocation invocation, ToolProgressCallback onProgress,
        CancellationToken cancellationToken) =>
        (await ExecuteOwnedAsync(invocation, onProgress, cancellationToken).ConfigureAwait(false)).Result;

    private ValueTask<FinalizedToolExecution> ExecuteOwnedAsync(ToolInvocation invocation, ToolProgressCallback onProgress,
        CancellationToken token) => _invocationScopes is null
        ? ExecuteCoreAsync(invocation, onProgress, token)
        : ExecuteScopedAsync(invocation, onProgress, token);

    private async ValueTask<FinalizedToolExecution> ExecuteScopedAsync(ToolInvocation invocation, ToolProgressCallback onProgress,
        CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _invocationScopes!.SessionCancellationToken);
        if (invocation?.Call?.Id is null || invocation.AssistantMessage is null)
            return await ExecuteCoreAsync(invocation!, onProgress, linked.Token).ConfigureAwait(false);
        var context = new ToolInvocationContext(invocation, null, _invocationScopes!, linked.Token,
            _callableToolNames, ExecuteNestedAsync,
            progressBudget: (onProgress.Target as ToolProgressScope)?.Budget,
            maximumRecordedArgumentDepth: Math.Max(0, _options.MaximumJsonDepth - 3));
        var owned = invocation with { Context = context };
        using var frame = context.Enter();
        try
        {
            var finalized = await ExecuteCoreAsync(owned, onProgress, linked.Token).ConfigureAwait(false);
            return finalized with { NestedCalls = context.NestedCalls, NestedUsage = context.NestedUsage };
        }
        finally { context.StopAdmission(); await context.CloseAsync().ConfigureAwait(false); }
    }

    private async Task<ToolOutcome> ExecuteNestedAsync(ToolInvocationContext parent, ToolCallContent call,
        ToolProgressCallback onProgress, CancellationToken token)
    {
        var source = parent.Invocation;
        var invocation = new ToolInvocation(source.AssistantMessage with { Content = [call], StopReason = StopReason.ToolUse }, call, 0)
            { EventSink = source.EventSink, Clock = source.Clock };
        var record = parent.Record.Start(call);
        ToolOutcome outcome;
        ToolResult? completedResult = null;
        Task previous = Task.CompletedTask;
        TaskCompletionSource? release = null;
        var exclusive = !parent.HoldsExclusiveQueue && (_invocationScopes!.ExecutionMode == ToolExecutionMode.Sequential ||
            _invocationScopes.SequentialTools.Contains(call.Name));
        if (exclusive)
            lock (_nestedQueueGate)
            {
                previous = _nestedQueueTail;
                release = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _nestedQueueTail = release.Task;
            }
        try
        {
            // This is a host-created call envelope, never a replacement model response or transcript append.
            // The scoped child carries the real parent even before preparation and authorization.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(parent.OperationCancellationToken, token);
            var context = new ToolInvocationContext(invocation, parent, _invocationScopes!, linked.Token,
                parent.Tools, ExecuteNestedAsync, parent.HoldsExclusiveQueue || exclusive);
            invocation = invocation with { Context = context };
            using var frame = context.Enter();
            try
            {
                if (source.EventSink is { } sink) await sink.EmitAsync(new ToolExecutionStarted(invocation), linked.Token).ConfigureAwait(false);
                await previous.ConfigureAwait(false);
                async ValueTask Progress(ToolResult partial, CancellationToken progressToken)
                {
                    await onProgress(partial, progressToken).ConfigureAwait(false);
                    if (source.EventSink is { } events)
                        await events.EmitAsync(new ToolExecutionUpdated(invocation, partial), progressToken).ConfigureAwait(false);
                }
                var finalized = await ExecuteCoreAsync(invocation, Progress, linked.Token).ConfigureAwait(false);
                outcome = new(invocation, finalized.Result) { IsError = finalized.IsError, DurationMs = finalized.DurationMs };
                completedResult = finalized.Result;
            }
            finally { context.StopAdmission(); await context.CloseAsync().ConfigureAwait(false); }
            if (source.EventSink is { } finished)
                await finished.EmitAsync(new ToolExecutionEnded(outcome), CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (parent.OperationCancellationToken.IsCancellationRequested || token.IsCancellationRequested)
        { outcome = new(invocation, Error(ToolFailureKind.Canceled, completedResult)); }
        catch (Exception) { outcome = new(invocation, Error(ToolFailureKind.ExecutionError, completedResult)); }
        finally
        {
            // A failed/cancelled start notification must not unlink a queued call from its predecessor.
            // Keep the FIFO chain intact on every exit, including paths that never reach execution.
            await previous.ConfigureAwait(false);
            release?.TrySetResult();
        }
        parent.Record.Finish(record, outcome);
        return outcome;
    }

    private async ValueTask<FinalizedToolExecution> ExecuteCoreAsync(ToolInvocation invocation, ToolProgressCallback onProgress,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return CompleteResult(Error(ToolFailureKind.Canceled));
        var stage = ToolFailureKind.InvalidArguments;
        ToolResult? completed = null;
        long? duration = null;
        try
        {
            if (invocation is null || invocation.AssistantMessage is null || invocation.Call is null)
                return CompleteResult(Error(ToolFailureKind.InvalidArguments));
            var assistant = invocation.AssistantMessage;
            if (assistant.StopReason == StopReason.Length) return CompleteResult(Error(ToolFailureKind.Truncated));
            if (assistant.StopReason != StopReason.ToolUse || assistant.Content.IsDefault ||
                assistant.Content.Length > _options.MaximumAssistantContentBlocks ||
                assistant.Content.Any(part => part is null) ||
                invocation.SourceIndex < 0 || invocation.SourceIndex >= assistant.Content.Length ||
                !ReferenceEquals(assistant.Content[invocation.SourceIndex], invocation.Call) ||
                invocation.Call.Id is null || invocation.Call.Name is null ||
                invocation.Call.Id.Length > _options.MaximumActionCharacters || invocation.Call.Name.Length > _options.MaximumActionCharacters ||
                !ValidText(invocation.Call.Id, nonempty: true) || !ValidText(invocation.Call.Name, nonempty: true))
                return CompleteResult(Error(ToolFailureKind.InvalidArguments));
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var call in assistant.Content.OfType<ToolCallContent>())
                if (call.Id is null || call.Id.Length > _options.MaximumActionCharacters ||
                    !ValidText(call.Id, nonempty: true) || !ids.Add(call.Id)) return CompleteResult(Error(ToolFailureKind.InvalidArguments));
            if (!_tools.TryGetValue(invocation.Call.Name, out var tool)) return CompleteResult(Error(ToolFailureKind.UnknownTool));
            var reachable = invocation.Context?.CallDepth is > 0 ? _options.AllowedNestedTools : _options.AllowedRootTools;
            if (reachable is not null && !reachable.Contains(invocation.Call.Name))
                return CompleteResult(Error(ToolFailureKind.UnknownTool));
            if (!ValidArguments(invocation.Call.Arguments, cancellationToken)) return CompleteResult(Error(ToolFailureKind.InvalidArguments));
            cancellationToken.ThrowIfCancellationRequested();
            var initialView = invocation;
            if (tool is IInitialToolArgumentPreparationAdapter initial)
            {
                var arguments = await initial.PrepareInitialArgumentsAsync(invocation, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (!ValidArguments(arguments, cancellationToken)) return CompleteResult(Error(ToolFailureKind.InvalidArguments));
                if (!ReferenceEquals(arguments, invocation.Call.Arguments))
                {
                    var call = invocation.Call with { Arguments = arguments };
                    initialView = invocation with { AssistantMessage = assistant with { Content = assistant.Content.SetItem(invocation.SourceIndex, call) }, Call = call };
                }
            }
            var action = await tool.PrepareAsync(initialView, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!ValidAction(action, invocation.Call.Name, cancellationToken))
                return CompleteResult(Error(ToolFailureKind.InvalidArguments));
            var isValid = await tool.ValidateAsync(action, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!isValid) return CompleteResult(Error(ToolFailureKind.InvalidArguments));
            stage = ToolFailureKind.HookError;
            if (_preparedHooks is not null)
            {
                var before = await _preparedHooks.BeforeAsync(invocation, action, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (before is null) return CompleteResult(Error(ToolFailureKind.HookError));
                if (before.Block)
                    return CompleteResult(ToolResult.Error(ToolFailureKind.Blocked, "Tool execution was blocked by a prepared hook.", before.Terminate));
                if (before.Arguments is not null)
                {
                    stage = ToolFailureKind.InvalidArguments;
                    if (!ValidArguments(before.Arguments, cancellationToken)) return CompleteResult(Error(stage));
                    // Re-resolution uses the same trusted adapter. The owned clone is only a prepare
                    // view: committed assistant history, original call identity, and policy context stay exact.
                    var replacedCall = invocation.Call with { Arguments = before.Arguments };
                    var replacedAssistant = assistant with { Content = assistant.Content.SetItem(invocation.SourceIndex, replacedCall) };
                    var prepareView = invocation with { AssistantMessage = replacedAssistant, Call = replacedCall };
                    action = await tool.PrepareAsync(prepareView, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!ValidAction(action, invocation.Call.Name, cancellationToken)) return CompleteResult(Error(stage));
                }
            }
            stage = ToolFailureKind.HookError;
            foreach (var transform in _transforms)
            {
                action = await transform(invocation, action, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (action is null) return CompleteResult(Error(ToolFailureKind.HookError));
            }
            stage = ToolFailureKind.InvalidArguments;
            if (!ValidAction(action, invocation.Call.Name, cancellationToken))
                return CompleteResult(Error(ToolFailureKind.InvalidArguments));
            isValid = await tool.ValidateAsync(action, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!isValid) return CompleteResult(Error(ToolFailureKind.InvalidArguments));
            stage = ToolFailureKind.HookError;
            var authorization = await _policy.AuthorizeAsync(invocation, action, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (authorization is null) return CompleteResult(Error(ToolFailureKind.HookError));
            if (!authorization.Allow) return CompleteResult(ToolResult.Error(ToolFailureKind.Blocked, "Final tool action was denied.", authorization.Terminate));
            stage = ToolFailureKind.ExecutionError;
            invocation.Context?.Activate();
            ToolResult result;
            var progress = invocation.ParentToolCallId is not null && invocation.Context?.ProgressBudget is { } inheritedBudget
                ? new ToolProgressScope((partial, token) => onProgress(Normalize(partial), token), cancellationToken, inheritedBudget)
                : ToolProgressScope.Forward(onProgress, Normalize, cancellationToken);
            var clock = invocation.Clock;
            var started = clock?.GetTimestamp() ?? 0;
            try
            {
                try
                {
                    result = Normalize(await (tool is IInvocationPreparedToolAdapter aware
                        ? aware.ExecuteAsync(invocation, action, progress.ReportAsync, cancellationToken)
                        : tool.ExecuteAsync(action, progress.ReportAsync, cancellationToken)).ConfigureAwait(false));
                }
                finally
                {
                    // execute() settled (or threw); admitted progress and nested calls are joined after timing.
                    if (clock is not null) duration = ToolBatchScheduler.ElapsedMilliseconds(clock, started);
                    invocation.Context?.StopAdmission();
                    try { await progress.CloseAsync().ConfigureAwait(false); }
                    finally { if (invocation.Context is { } context) await context.CloseAsync().ConfigureAwait(false); }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Timed(Error(ToolFailureKind.Canceled)); }
            catch (Exception) { result = Error(ToolFailureKind.ExecutionError); }
            if (progress.IsSourceCompatible && progress.DeliveryFailure is not null) return Timed(Error(ToolFailureKind.ExecutionError));
            completed = result;
            cancellationToken.ThrowIfCancellationRequested();
            stage = ToolFailureKind.HookError;
            foreach (var transform in _resultTransforms)
            {
                result = Normalize(await transform(invocation, action, result, cancellationToken).ConfigureAwait(false));
                completed = result;
                cancellationToken.ThrowIfCancellationRequested();
            }
            var outcome = new ToolOutcome(invocation, result);
            if (_preparedHooks is not null)
            {
                var patch = await _preparedHooks.AfterAsync(invocation, action, result, outcome.IsError, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                outcome = SourceToolAfterHook.Apply(outcome, patch, ResultLimits());
                result = Normalize(outcome.Result);
                completed = result;
                cancellationToken.ThrowIfCancellationRequested();
            }
            return new(result, outcome.IsError) { DurationMs = duration };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return Timed(Error(ToolFailureKind.Canceled, completed)); }
        catch (Exception) { return Timed(Error(stage, completed)); }
        // Failures after execution keep its measured time; earlier failures have none.
        FinalizedToolExecution Timed(ToolResult value) => CompleteResult(value) with { DurationMs = duration };
    }

    private ToolResultValueOptions ResultLimits() => new(_options.MaximumResultCharacters, _options.MaximumStructuredContentCharacters,
        _options.MaximumResultContentBlocks, _options.MaximumJsonDepth, _options.MaximumResultRawCharacters, _options.MaximumResultRawBytes);
    private static FinalizedToolExecution CompleteResult(ToolResult result) => new(result, result.IsError);

    private ImmutableArray<T> Copy<T>(IEnumerable<T>? callbacks) where T : Delegate
    {
        var result = ImmutableArray.CreateBuilder<T>();
        if (callbacks is not null)
            foreach (var callback in callbacks)
            {
                if (callback is null || result.Count >= _options.MaximumTransforms)
                    throw new ArgumentException("Invalid or excessive tool transforms.", nameof(callbacks));
                result.Add(callback);
            }
        return result.ToImmutable();
    }

    private bool ValidArguments(JsonData? arguments, CancellationToken token) =>
        arguments is not null && arguments.Value.ValueKind == JsonValueKind.Object &&
        arguments.ToString().Length <= _options.MaximumArgumentCharacters && ValidJson(arguments.Value, 0, token, allowNulData: true);

    private bool ValidAction(PreparedToolAction? action, string name, CancellationToken token)
    {
        if (action is null || action.ToolName != name || action.Operation is null || action.Target is null ||
            action.Kind is not (PreparedToolActionKind.Path or PreparedToolActionKind.Command or PreparedToolActionKind.Extension) ||
            action.CommandArguments.IsDefault || action.Environment is null ||
            (long)action.CommandArguments.Length + action.Environment.Count > _options.MaximumActionEntries ||
            (action.Kind == PreparedToolActionKind.Path && !action.CommandArguments.IsEmpty) ||
            (action.Kind == PreparedToolActionKind.Command && action.WorkingDirectory is null) ||
            (action.Kind == PreparedToolActionKind.Extension && (!action.CommandArguments.IsEmpty ||
                action.WorkingDirectory is not null || !action.Environment.IsEmpty)) ||
            !ValidArguments(action.Arguments, token)) return false;
        long characters = (long)action.ToolName.Length + action.Operation.Length + action.Target.Length +
            action.Arguments.ToString().Length + (action.WorkingDirectory?.Length ?? 0);
        if (characters > _options.MaximumActionCharacters || !ValidText(action.Operation, true) || !ValidText(action.Target, true) ||
            (action.WorkingDirectory is not null && !ValidText(action.WorkingDirectory, true))) return false;
        foreach (var argument in action.CommandArguments)
        {
            token.ThrowIfCancellationRequested();
            if (argument is null) return false;
            characters += argument.Length;
            if (characters > _options.MaximumActionCharacters || !ValidText(argument, false)) return false;
        }
        foreach (var (key, value) in action.Environment)
        {
            token.ThrowIfCancellationRequested();
            if (value is null) return false;
            characters += (long)key.Length + value.Length;
            if (characters > _options.MaximumActionCharacters || !ValidText(key, true) || key.Contains('=') || !ValidText(value, false)) return false;
        }
        return characters <= _options.MaximumActionCharacters;
    }

    private ToolResult Normalize(ToolResult? result)
    {
        if (result is null || result.Details is null)
            throw new InvalidOperationException("Invalid tool result.");
        ToolResultValueCodec.Validate(result, new(_options.MaximumResultCharacters, _options.MaximumStructuredContentCharacters,
            _options.MaximumResultContentBlocks, _options.MaximumJsonDepth, _options.MaximumResultRawCharacters, _options.MaximumResultRawBytes));
        if (result.StructuredContent is { } structured)
        {
            var raw = structured.ToString();
            if (raw.Length > _options.MaximumStructuredContentCharacters)
                throw new InvalidOperationException("Invalid structured tool result.");
            // FromElement can retain permissively parsed comments/trailing commas. Reparse
            // after charging raw size; keep the original owned value and its valid tokens.
            if (!ValidOutputJson(JsonData.Parse(raw).Value, 0))
                throw new InvalidOperationException("Invalid structured tool result.");
        }
        var detailsRaw = result.Details.ToString();
        long characters = detailsRaw.Length + (long)(result.Failure?.Message.Length ?? 0) + ToolResultValueCodec.AdditionalCharacters(result);
        // Details includes decoded output (for example truncation.content), not action strings.
        // Charge retained syntax before strict reparse; retain the original owned value and tokens.
        if (characters > _options.MaximumResultCharacters || !ValidOutputJson(JsonData.Parse(detailsRaw).Value, 0))
            throw new InvalidOperationException("Invalid tool result.");
        if (result.Failure is { } failure && (!Enum.IsDefined(failure.Kind) || !ValidText(failure.Message, false)))
            throw new InvalidOperationException("Invalid tool result failure.");
        if (result.OwnedContent is ImmutableArray<TextContent> typed)
        {
            foreach (var content in typed)
            {
                if (content is null || content.Text is null) throw new InvalidOperationException("Invalid tool result.");
                characters += content.Text.Length;
                if (characters > _options.MaximumResultCharacters || !ValidText(content.Text, false, allowNul: true)) throw new InvalidOperationException("Tool result exceeds limits.");
                if (content.ExtraProperties is { } extra)
                    foreach (var (key, value) in extra.Values)
                    {
                        characters += (long)key.Length + value.ToString().Length;
                        if (characters > _options.MaximumResultCharacters || !ValidText(key, false) ||
                            !ValidJson(value.Value, 0, CancellationToken.None)) throw new InvalidOperationException("Invalid tool result.");
                    }
            }
        }
        else foreach (var content in result.ContentValue.Value.EnumerateArray())
        {
            var image = content.GetProperty("type").GetString() == "image";
            foreach (var name in image ? new[] { "data", "mimeType" } : new[] { "text" })
            {
                var text = content.GetProperty(name).GetString();
                characters += text!.Length;
                if (characters > _options.MaximumResultCharacters || !ValidText(text, false, allowNul: true)) throw new InvalidOperationException("Tool result exceeds limits.");
            }
            foreach (var property in content.EnumerateObject())
            {
                if (property.Name == "type" || !image && property.Name == "text" || image && property.Name is "data" or "mimeType") continue;
                characters += (long)property.Name.Length + property.Value.GetRawText().Length;
                if (characters > _options.MaximumResultCharacters || !ValidText(property.Name, false) ||
                    !ValidJson(property.Value, 0, CancellationToken.None)) throw new InvalidOperationException("Invalid tool result.");
            }
        }
        if (characters > _options.MaximumResultCharacters) throw new InvalidOperationException("Tool result exceeds limits.");
        return result;
    }

    // Details and programmatic output are JSON data, not action strings. Escaped NUL is valid output;
    // finite numbers, scalar Unicode and bounded container depth are required without rewriting tokens.
    private bool ValidOutputJson(JsonElement value, int parentDepth)
    {
        if (value.ValueKind == JsonValueKind.String) return ValidText(value.GetString(), false, allowNul: true);
        if (value.ValueKind == JsonValueKind.Number) return value.TryGetDouble(out var number) && double.IsFinite(number);
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False) return true;
        if (value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) return false;
        var depth = parentDepth + 1;
        if (depth > _options.MaximumJsonDepth) return false;
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
                if (!names.Add(property.Name) || !ValidText(property.Name, false, allowNul: true) ||
                    !ValidOutputJson(property.Value, depth)) return false;
        }
        else
            foreach (var child in value.EnumerateArray()) if (!ValidOutputJson(child, depth)) return false;
        return true;
    }

    private bool ValidJson(JsonElement value, int parentDepth, CancellationToken token, bool allowNulData = false)
    {
        token.ThrowIfCancellationRequested();
        // Owned JSON string values can contain NUL. Actual executable strings are validated
        // separately by ValidAction; admitting data does not admit NUL targets/argv/cwd/env.
        if (value.ValueKind == JsonValueKind.String) return ValidText(value.GetString(), false, allowNul: allowNulData);
        if (value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) return true;
        var depth = parentDepth + 1;
        if (depth > _options.MaximumJsonDepth) return false;
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
                if (!ValidText(property.Name, false) || !ValidJson(property.Value, depth, token, allowNulData)) return false;
        }
        else
            foreach (var child in value.EnumerateArray()) if (!ValidJson(child, depth, token, allowNulData)) return false;
        return true;
    }
    private static bool ValidText(string? value, bool nonempty, bool allowNul = false)
    {
        if (value is null || (nonempty && string.IsNullOrWhiteSpace(value))) return false;
        for (var index = 0; index < value.Length; index++)
        {
            if (!allowNul && value[index] == '\0') return false;
            if (char.IsHighSurrogate(value[index]))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[++index])) return false;
            }
            else if (char.IsLowSurrogate(value[index])) return false;
        }
        return true;
    }
    private static ToolResult Error(ToolFailureKind kind, ToolResult? completed = null)
    {
        var message = kind switch
        {
            ToolFailureKind.Truncated => "Truncated assistant output cannot authorize a tool action.",
            ToolFailureKind.UnknownTool => "Tool is not registered.",
            ToolFailureKind.InvalidArguments => "Invalid or unsupported final tool action input.",
            ToolFailureKind.HookError => "Tool action hook or policy failed.",
            ToolFailureKind.Canceled => "Tool invocation was cancelled.",
            _ => "Tool action execution failed."
        };
        // Keep the completed result, including structured output, when cancellation or finalization fails.
        return completed is null ? ToolResult.Error(kind, message) : completed with
        {
            Content = completed.Content.Add(new TextContent(message)), IsError = true, Failure = new(kind, message)
        };
    }
}
