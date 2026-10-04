using System.Collections.Immutable;
using PiSharp.Contracts;
using PiSharp.Extensions.Events;

namespace PiSharp.Extensions.Runtime.Dispatch;

/// <summary>Typed reducers over actual transactional registrations and owner callback leases.
/// The registry is borrowed; this dispatcher neither activates nor disposes extensions.</summary>
public sealed class RegisteredExtensionEventDispatcher
{
    private readonly ExtensionRegistry registry;
    private readonly ExtensionEventDispatcher reducers;
    private readonly int maximumHandlersPerDispatch;

    public RegisteredExtensionEventDispatcher(ExtensionRegistry registry, Action<JsonData> admitToolResult,
        ExtensionEventDispatchOptions? options = null, int maximumHandlersPerDispatch = 256,
        Action<ImmutableArray<TranscriptEntry>>? admitContextMessages = null,
        Func<ImmutableArray<TranscriptEntry>, CancellationToken, TranscriptEntry?>? restoreSystemMessage = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (maximumHandlersPerDispatch is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(maximumHandlersPerDispatch));
        this.registry = registry;
        this.maximumHandlersPerDispatch = maximumHandlersPerDispatch;
        // One long-lived reducer engine preserves its concurrent and recursive dispatch accounting.
        reducers = new(admitToolResult, options, admitContextMessages, restoreSystemMessage);
    }

    public ValueTask<ExtensionBeforeAgentStartReduction> DispatchBeforeAgentStartAsync(ExtensionBeforeAgentStartEvent input,
        CancellationToken operationCancellationToken = default, CancellationToken sessionCancellationToken = default) =>
        DispatchAsync<ExtensionBeforeAgentStartEvent, ExtensionBeforeAgentStartPatch, ExtensionBeforeAgentStartReduction>(registry.CaptureSnapshot(),
            RegistrationKind.BeforeAgentStartHandler, "before_agent_start", input, reducers.BeforeAgentStartHandlers,
            entry => ((ExtensionBeforeAgentStartHandlerDescriptor)entry.Descriptor).HandleAsync,
            reducers.DispatchBeforeAgentStartAsync, operationCancellationToken, sessionCancellationToken, useCurrentSnapshot: true);

    public ValueTask<ExtensionContextReduction> DispatchContextAsync(ExtensionContextEvent input,
        CancellationToken operationCancellationToken = default, CancellationToken sessionCancellationToken = default) =>
        DispatchAsync<ExtensionContextEvent, ExtensionContextMessagesPatch, ExtensionContextReduction>(registry.CaptureSnapshot(),
            RegistrationKind.ContextHandler, "context", input, reducers.ContextHandlers,
            entry => ((ExtensionContextHandlerDescriptor)entry.Descriptor).HandleAsync,
            reducers.DispatchContextAsync, operationCancellationToken, sessionCancellationToken, useCurrentSnapshot: true);

    /// <summary>Capture and lease the live handler list atomically for one model request.</summary>
    public ValueTask<ExtensionContextReduction> DispatchContextWithSystemAsync(
        ExtensionContextWithSystemEvent input, CancellationToken operationCancellationToken = default,
        CancellationToken sessionCancellationToken = default) =>
        DispatchAsync<ExtensionContextWithSystemEvent, ExtensionContextMessagesPatch, ExtensionContextReduction>(registry.CaptureSnapshot(),
            RegistrationKind.ContextWithSystemHandler, "context_with_system", input, reducers.ContextWithSystemHandlers,
            entry => ((ExtensionContextWithSystemHandlerDescriptor)entry.Descriptor).HandleAsync,
            reducers.DispatchContextWithSystemAsync, operationCancellationToken, sessionCancellationToken, useCurrentSnapshot: true);

    public ValueTask<ExtensionContextReduction> DispatchContextWithSystemAsync(ExtensionRegistrySnapshot captured,
        ExtensionContextWithSystemEvent input, CancellationToken operationCancellationToken = default,
        CancellationToken sessionCancellationToken = default) =>
        DispatchAsync<ExtensionContextWithSystemEvent, ExtensionContextMessagesPatch, ExtensionContextReduction>(captured,
            RegistrationKind.ContextWithSystemHandler, "context_with_system", input, reducers.ContextWithSystemHandlers,
            entry => ((ExtensionContextWithSystemHandlerDescriptor)entry.Descriptor).HandleAsync,
            reducers.DispatchContextWithSystemAsync, operationCancellationToken, sessionCancellationToken);

    public ValueTask<ExtensionInputReduction> DispatchInputAsync(ExtensionRegistrySnapshot captured,
        ExtensionInputEvent input, CancellationToken operationCancellationToken = default,
        CancellationToken sessionCancellationToken = default) =>
        DispatchAsync<ExtensionInputEvent, ExtensionInputPatch, ExtensionInputReduction>(captured,
            RegistrationKind.InputHandler, "input", input, reducers.InputHandlers,
            entry => ((ExtensionInputHandlerDescriptor)entry.Descriptor).HandleAsync,
            reducers.DispatchInputAsync, operationCancellationToken, sessionCancellationToken);

    public ValueTask<ExtensionToolCallReduction> DispatchToolCallAsync(ExtensionRegistrySnapshot captured,
        ExtensionToolCallEvent input, CancellationToken operationCancellationToken = default,
        CancellationToken sessionCancellationToken = default) =>
        DispatchAsync<ExtensionToolCallEvent, ExtensionToolCallPatch, ExtensionToolCallReduction>(captured,
            RegistrationKind.ToolCallHandler, "tool_call", input, reducers.ToolCallHandlers,
            entry => ((ExtensionToolCallHandlerDescriptor)entry.Descriptor).HandleAsync,
            reducers.DispatchToolCallAsync, operationCancellationToken, sessionCancellationToken);

    public ValueTask<ExtensionToolResultReduction> DispatchToolResultAsync(ExtensionRegistrySnapshot captured,
        ExtensionToolResultEvent input, CancellationToken operationCancellationToken = default,
        CancellationToken sessionCancellationToken = default) =>
        DispatchAsync<ExtensionToolResultEvent, ExtensionToolResultPatch, ExtensionToolResultReduction>(captured,
            RegistrationKind.ToolResultHandler, "tool_result", input, reducers.ToolResultHandlers,
            entry => ((ExtensionToolResultHandlerDescriptor)entry.Descriptor).HandleAsync,
            reducers.DispatchToolResultAsync, operationCancellationToken, sessionCancellationToken);

    private async ValueTask<TResult> DispatchAsync<TEvent, TPatch, TResult>(ExtensionRegistrySnapshot captured,
        RegistrationKind kind, string eventName, TEvent input, ExtensionReducerHandlerSet<TEvent, TPatch> identity,
        Func<RegistrationEntry, ExtensionReducerCallback<TEvent, TPatch>> callback,
        Func<ExtensionReducerSnapshot<TEvent, TPatch>, TEvent, CancellationToken, CancellationToken, ValueTask<TResult>> reduce,
        CancellationToken operation, CancellationToken session, bool useCurrentSnapshot = false) where TPatch : class
    {
        var admission = useCurrentSnapshot
            ? registry.AdmitCurrent(kind, eventName, "dispatch-" + eventName, operation, session, maximumHandlersPerDispatch, out captured)
            : registry.Admit(captured, kind, eventName, "dispatch-" + eventName, operation, session, maximumHandlersPerDispatch);
        try
        {
            // All selected owner generations are leased before any callback. This also prevents a
            // callback from awaiting disposal of a later participant whose lease it is holding.
            using var frame = new CallbackFrame(admission.Select(row => row.Scope).Distinct().ToImmutableArray());
            var entries = ImmutableArray.CreateBuilder<ReducerRegistration<TEvent, TPatch>>(admission.Length);
            for (var index = 0; index < admission.Length; index++)
            {
                var (scope, entry) = admission[index];
                async ValueTask<TPatch?> InvokeWithUi(TEvent value, IExtensionContext ignored, CancellationToken token)
                {
                    await using var context = await registry.CreateUiContextAsync(scope, operation, session).ConfigureAwait(false);
                    return await callback(entry)(value, context.Context, token).ConfigureAwait(false);
                }
                entries.Add(new(entry.OwnerId, entry.OwnerGeneration, entry.RegistrationId, InvokeWithUi,
                    scope.ExtensionLifetimeCancellationToken, entry.OwnerGeneration, entry.OwnerGeneration, index));
            }
            // This is an ephemeral projection of the leased registry entries, not another registration
            // store. Its identity only lets the existing reducer validate this internal projection.
            var projected = new ExtensionReducerSnapshot<TEvent, TPatch>(identity, captured.Revision, entries.MoveToImmutable());
            return await reduce(projected, input, operation, session).ConfigureAwait(false);
        }
        finally { registry.ReleaseAdmission(admission); }
    }
}
