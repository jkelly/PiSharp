// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/extensions/runner.ts
// (emitCacheWarmingDecision, emitBeforeProviderRequest, emitBeforeProviderHeaders).
using System.Collections.Immutable;
using PiSharp.Contracts;
using PiSharp.Extensions.Events;

namespace PiSharp.Extensions.Runtime;

/// <summary>Folds one handler result into the event the next handler sees. Null keeps the event unchanged.</summary>
public delegate JsonData? ExtensionEventFold(JsonData currentEvent, JsonData result);

public sealed partial class ExtensionRegistry
{
    internal IExtensionRegistration Register(RegistrationScope scope, ExtensionEventHandlerDescriptor descriptor)
    {
        const string op = "register-event-handler";
        if (descriptor is null || !ValidNames(descriptor.RegistrationId, descriptor.Topic) ||
            descriptor.HandleAsync is null || descriptor.HandleAsync.GetInvocationList().Length != 1)
            throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, scope.OwnerId, op);
        return Add(scope, descriptor.RegistrationId, descriptor.Topic, RegistrationKind.EventHandler, descriptor,
            (long)descriptor.RegistrationId.Length + descriptor.Topic.Length, op);
    }

    /// <summary>Source hasHandlers for a result-returning event topic.</summary>
    public bool HasEventHandlers(ExtensionRegistrySnapshot captured, string topic)
    {
        ArgumentNullException.ThrowIfNull(captured);
        return captured.Entries.Any(entry => entry.Kind == RegistrationKind.EventHandler && entry.Name == topic);
    }

    /// <summary>Runs the topic's handlers in registration order with the same event, passing each result with its owner (the
    /// extension path upstream reports) to <paramref name="observe"/>; when it returns true no later handler runs (runner.ts handlers
    /// whose first decisive result wins, such as project_trust). A failure is reported and the next handler runs.</summary>
    public async ValueTask DispatchUntilAsync(ExtensionRegistrySnapshot captured, string topic, JsonData @event,
        Func<string, JsonData, bool> observe, Func<ExtensionEventDiagnostic, CancellationToken, ValueTask>? report,
        CancellationToken operationToken = default, CancellationToken sessionToken = default)
    {
        ArgumentNullException.ThrowIfNull(@event); ArgumentNullException.ThrowIfNull(observe);
        var admission = Admit(captured, RegistrationKind.EventHandler, topic, "dispatch-until", operationToken, sessionToken);
        try
        {
            using var dispatchFrame = new CallbackFrame(admission.Select(item => item.Scope).Distinct().ToImmutableArray());
            foreach (var (scope, entry) in admission)
            {
                JsonData? result;
                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(operationToken, sessionToken, scope.ExtensionLifetimeCancellationToken);
                    using var frame = new CallbackFrame(scope);
                    await using var context = await CreateUiContextAsync(scope, operationToken, sessionToken).ConfigureAwait(false);
                    result = await ((ExtensionEventHandlerDescriptor)entry.Descriptor).HandleAsync(@event, context.Context, linked.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (!operationToken.IsCancellationRequested && !sessionToken.IsCancellationRequested)
                {
                    await ReportAsync(report, new(topic, scope.OwnerId, scope.OwnerGeneration, entry.RegistrationId, ExtensionEventFailure.HandlerFailed) { Message = error.Message }).ConfigureAwait(false);
                    continue;
                }
                if (result is not null && observe(scope.OwnerId, result)) return;
            }
        }
        finally { ReleaseAdmission(admission); }
    }

    /// <summary>Runs the topic's handlers in registration order. Each sees the current event; a returned result is folded into
    /// it; a failure is reported with its message and the next handler still runs. Returns the final event.</summary>
    public async ValueTask<JsonData> ReduceEventAsync(ExtensionRegistrySnapshot captured, string topic, JsonData initial,
        ExtensionEventFold fold, Func<ExtensionEventDiagnostic, CancellationToken, ValueTask>? report,
        CancellationToken operationToken = default, CancellationToken sessionToken = default)
    {
        ArgumentNullException.ThrowIfNull(initial); ArgumentNullException.ThrowIfNull(fold);
        var admission = Admit(captured, RegistrationKind.EventHandler, topic, "reduce-event", operationToken, sessionToken);
        var current = initial;
        try
        {
            using var dispatchFrame = new CallbackFrame(admission.Select(item => item.Scope).Distinct().ToImmutableArray());
            foreach (var (scope, entry) in admission)
            {
                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(operationToken, sessionToken, scope.ExtensionLifetimeCancellationToken);
                    using var frame = new CallbackFrame(scope);
                    await using var context = await CreateUiContextAsync(scope, operationToken, sessionToken).ConfigureAwait(false);
                    var result = await ((ExtensionEventHandlerDescriptor)entry.Descriptor).HandleAsync(current, context.Context, linked.Token).ConfigureAwait(false);
                    if (result is not null && fold(current, result) is { } next) current = next;
                }
                catch (Exception error) when (!operationToken.IsCancellationRequested && !sessionToken.IsCancellationRequested)
                { await ReportAsync(report, new(topic, scope.OwnerId, scope.OwnerGeneration, entry.RegistrationId, ExtensionEventFailure.HandlerFailed) { Message = error.Message }).ConfigureAwait(false); }
            }
            return current;
        }
        finally { ReleaseAdmission(admission); }
    }
}
