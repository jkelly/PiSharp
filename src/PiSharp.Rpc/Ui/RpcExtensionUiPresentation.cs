using PiSharp.Contracts;
using PiSharp.Extensions;

namespace PiSharp.Rpc.Ui;

/// <summary>Identity captured by the actual coordinator at admission; never added to legacy RPC JSON.</summary>
public sealed record RpcExtensionUiPresentationIdentity(string RequestId, long ConnectionGeneration,
    long SessionGeneration, string OwnerId, long OwnerGeneration);

/// <summary>A dialog whose complete shared RPC write and flush have successfully settled.</summary>
public sealed record RpcExtensionUiPresentation(RpcExtensionUiPresentationIdentity Identity, JsonData Request);

/// <summary>Exactly one terminal observation for an admitted dialog, after its publication attempt settles.</summary>
public sealed record RpcExtensionUiRetirement(RpcExtensionUiPresentationIdentity Identity,
    ExtensionUiOutcomeKind Outcome, ExtensionUiUnavailableReason? UnavailableReason,
    bool PublicationSucceeded, bool PresentationEntered);

/// <summary>
/// Optional PiSharp native host presentation extension. Calls are sequential per admitted dialog, outside
/// coordinator and writer locks, and joined by its scope/connection cleanup. Implementations must cooperate
/// with connection cancellation and must not await disposal of the scope/connection invoking them.
/// </summary>
public interface IRpcExtensionUiPresentationObserver
{
    ValueTask PublishedAsync(RpcExtensionUiPresentation presentation, CancellationToken cancellationToken);
    ValueTask RetiredAsync(RpcExtensionUiRetirement retirement, CancellationToken cancellationToken);
}
