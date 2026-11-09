// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): next/host/object.ts activationChain.
// H1-approved partial: only the inherited remote activation chain lives here.
using PiSharp.Chord.Link;

namespace PiSharp.Chord.Host;

internal sealed partial class ChordObject
{
    internal bool RemoteSwapInPlace;
    private IReadOnlyList<string> inheritedActivationChain = [];
    internal void InheritActivationChain(IReadOnlyList<string>? chain)
    {
        // Existing objects keep the chain of the request which created them.
        if (Holders.Count == 0 && Current is { Live: false }) inheritedActivationChain = chain?.ToArray() ?? [];
    }
    internal IReadOnlyList<string>? ActivationChain()
    {
        if (Current is not { Live: false } current) return null;
        return [.. inheritedActivationChain.TakeLast(MessageCheck.Chain - 1), current.InstanceId];
    }
    internal bool ChainContains(string? id) => id is not null && inheritedActivationChain.Contains(id);
}
