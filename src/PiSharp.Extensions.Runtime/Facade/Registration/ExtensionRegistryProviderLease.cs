using System.Collections.Immutable;

namespace PiSharp.Extensions.Runtime;

public sealed partial class ExtensionRegistry
{
    /// <summary>Reuses the real native admission counters and snapshot/generation checks. The metadata marker
    /// is not the stream callback; its exact entry lease keeps actual owner cleanup joined to enumeration.</summary>
    internal ImmutableArray<(RegistrationScope Scope, RegistrationEntry Entry)> AdmitProviderMarker(
        RegistrationScope scope, IExtensionRegistration marker, string topic, CancellationToken token)
    {
        var admitted = AdmitCurrent(RegistrationKind.Observation, topic, "provider-stream", token, default, 1, out _);
        if (admitted.Length == 1 && ReferenceEquals(admitted[0].Scope, scope) &&
            admitted[0].Entry.RegistrationId == marker.RegistrationId) return admitted;
        ReleaseAdmission(admitted);
        throw Failure(ExtensionRegistrationFailure.StaleSnapshot, scope.OwnerId, "provider-stream");
    }
}
