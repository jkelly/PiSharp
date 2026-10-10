// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/extensions/runner.ts (emit: every handler
// in order, an error is reported through emitError and the next handler still runs; emitUserBash; hasHandlers).
using System.Collections.Immutable;
using PiSharp.Contracts;
using PiSharp.Extensions.Events;

namespace PiSharp.Extensions.Runtime;

public sealed partial class ExtensionRegistry
{
    /// <summary>Source hasHandlers for an observation topic in one captured registration revision.</summary>
    public bool HasObservers(ExtensionRegistrySnapshot captured, string topic)
    {
        ArgumentNullException.ThrowIfNull(captured);
        return Current(captured).Entries.Any(entry => entry.Kind == RegistrationKind.Observation && entry.Name == topic);
    }

    /// <summary>Source runner.emit for observation-only events. Each handler failure is reported with its message and the
    /// remaining handlers still run; host cancellation ends the dispatch.</summary>
    public async ValueTask DispatchObservationsReportingAsync(ExtensionRegistrySnapshot captured, string topic, JsonData observation,
        Func<ExtensionEventDiagnostic, CancellationToken, ValueTask>? report, CancellationToken operationToken = default,
        CancellationToken sessionToken = default)
    {
        if (!RegistrationPolicy.Json(observation, options with { MaximumJsonCharacters = options.MaximumObservationCharacters, MaximumJsonDepth = PiSharp.Contracts.JsonData.MaximumDepth }))
            throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, "registry", "dispatch-observation");
        var admission = Admit(captured, RegistrationKind.Observation, topic, "dispatch-observation", operationToken, sessionToken);
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
                    await ((ExtensionObservationDescriptor)entry.Descriptor).ObserveAsync(observation, context.Context, linked.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (!operationToken.IsCancellationRequested && !sessionToken.IsCancellationRequested)
                { await ReportAsync(report, new(topic, scope.OwnerId, scope.OwnerGeneration, entry.RegistrationId, ExtensionEventFailure.HandlerFailed) { Message = error.Message }).ConfigureAwait(false); }
            }
        }
        finally { ReleaseAdmission(admission); }
    }

    internal IExtensionRegistration Register(RegistrationScope scope, ExtensionUserBashHandlerDescriptor descriptor)
    {
        const string op = "register-user-bash";
        if (descriptor is null || !ValidNames(descriptor.RegistrationId, "user_bash") ||
            descriptor.HandleAsync is null || descriptor.HandleAsync.GetInvocationList().Length != 1)
            throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, scope.OwnerId, op);
        return Add(scope, descriptor.RegistrationId, "user_bash", RegistrationKind.UserBashHandler, descriptor,
            (long)descriptor.RegistrationId.Length + "user_bash".Length, op);
    }

    /// <summary>True when the captured revision has user_bash handlers.</summary>
    public bool HasUserBashHandlers(ExtensionRegistrySnapshot captured)
    {
        ArgumentNullException.ThrowIfNull(captured);
        return Current(captured).Entries.Any(entry => entry.Kind == RegistrationKind.UserBashHandler);
    }

    /// <summary>Source emitUserBash: handlers run in order and the first non-null patch wins. A patch without exactly one of
    /// operations or result is invalid. A handler failure is reported and rethrown: the command does not fall back to local
    /// execution.</summary>
    public async ValueTask<ExtensionUserBashPatch?> DispatchUserBashAsync(ExtensionRegistrySnapshot captured, ExtensionUserBashEvent userBashEvent,
        Func<ExtensionEventDiagnostic, CancellationToken, ValueTask>? report, CancellationToken operationToken = default,
        CancellationToken sessionToken = default)
    {
        ArgumentNullException.ThrowIfNull(userBashEvent);
        var admission = Admit(captured, RegistrationKind.UserBashHandler, "user_bash", "dispatch-user-bash", operationToken, sessionToken);
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
                    var patch = await ((ExtensionUserBashHandlerDescriptor)entry.Descriptor).HandleAsync(userBashEvent, context.Context, linked.Token).ConfigureAwait(false);
                    if (patch is null) continue;
                    if ((patch.Operations is null) == (patch.Result is null))
                        throw new InvalidOperationException("Invalid user_bash handler result: return undefined for local execution or exactly one valid { operations } or { result } object");
                    return patch;
                }
                catch (Exception error) when (!operationToken.IsCancellationRequested && !sessionToken.IsCancellationRequested)
                {
                    await ReportAsync(report, new("user_bash", scope.OwnerId, scope.OwnerGeneration, entry.RegistrationId,
                        ExtensionEventFailure.HandlerFailed) { Message = error.Message }).ConfigureAwait(false);
                    throw;
                }
            }
            return null;
        }
        finally { ReleaseAdmission(admission); }
    }

    private static async ValueTask ReportAsync(Func<ExtensionEventDiagnostic, CancellationToken, ValueTask>? report, ExtensionEventDiagnostic diagnostic)
    {
        // A reporter failure has no authority over the event or the remaining handlers.
        if (report is null) return;
        try { await report(diagnostic, CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
    }
}
