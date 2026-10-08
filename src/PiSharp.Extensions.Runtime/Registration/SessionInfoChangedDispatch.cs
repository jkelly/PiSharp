using System.Collections.Immutable;
using PiSharp.Contracts;
using PiSharp.Extensions.Events;

namespace PiSharp.Extensions.Runtime;

public sealed partial class ExtensionRegistry
{
    /// <summary>Postcommit metadata observation, no event abort signal; original captured callbacks and context cleanup are joined.</summary>
    public async ValueTask DispatchSessionInfoChangedAsync(ExtensionRegistrySnapshot captured, JsonData observation,
        Func<ExtensionEventDiagnostic, CancellationToken, ValueTask>? reportDiagnostic = null)
    {
        ValidateDispatchData(observation, "dispatch-session-info-changed");
        if (observation.Value.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !observation.Value.TryGetProperty("type", out var type) || type.ValueKind != System.Text.Json.JsonValueKind.String || type.GetString() != "session_info_changed" ||
            observation.Value.TryGetProperty("name", out var name) && name.ValueKind != System.Text.Json.JsonValueKind.String)
            throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, "registry", "dispatch-session-info-changed");
        var admission = Admit(captured, RegistrationKind.Observation, "session_info_changed", "dispatch-session-info-changed",
            CancellationToken.None, CancellationToken.None);
        try
        {
            using var dispatchFrame = new CallbackFrame(admission.Select(item => item.Scope).Distinct().ToImmutableArray());
            foreach (var (scope, entry) in admission)
            {
                try
                {
                    using var frame = new CallbackFrame(scope);
                    await using var context = await CreateUiContextAsync(scope, CancellationToken.None, CancellationToken.None).ConfigureAwait(false);
                    await ((ExtensionObservationDescriptor)entry.Descriptor).ObserveAsync(observation, context.Context,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Topic-specific failure continuation, including OCE and original context cleanup failure.
                    // A throwing source-style reporter skips later handlers; the postcommit host binding owns refusal.
                    if (reportDiagnostic is not null)
                        await reportDiagnostic(new("session_info_changed", scope.OwnerId, scope.OwnerGeneration,
                            entry.RegistrationId, ExtensionEventFailure.HandlerFailed), CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        finally { ReleaseAdmission(admission); }
    }
}
