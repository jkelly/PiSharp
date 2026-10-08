using System.Text.Json;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions.Runtime;

namespace PiSharp.Cli.Extensions;

/// <summary>Explicit host producer for an actual successful no-summary selection receipt.
/// The RPC postselection owner must call and join this after core publication; no default
/// pipeline hook is installed by this leaf. It grants no selection/mutation authority.</summary>
internal sealed class NativeSessionTreeObservationBinding(ExtensionRegistry registry, ExtensionRegistrySnapshot captured)
{
    internal async ValueTask PublishAsync(ReplaceableAgentSession owner, SessionTreeNavigationReceipt receipt, string? oldLeafId)
    {
        ArgumentNullException.ThrowIfNull(owner); ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.Disposition != SessionTreeNavigationDisposition.Selected) return;
        Task? original = null;
        try
        {
            owner.ValidateAttachment(receipt.View.Attachment);
            var state = receipt.View.Attachment.Session.Snapshot;
            if (state.IsDisposed || state.IsRetired || state.Fault is not null ||
                state.Log.Header.Id != receipt.SessionId || state.Context.LeafId != receipt.LeafId)
                throw new InvalidOperationException("Selected lifecycle receipt no longer describes the admitted current attachment.");
            var observation = JsonData.Parse(JsonSerializer.Serialize(new
            { type = "session_tree", newLeafId = receipt.LeafId, oldLeafId }));
            original = registry.DispatchObservationsAsync(captured, "session_tree", observation).AsTask();
            await original.ConfigureAwait(false);
        }
        catch (Exception direct)
        {
            // Publication is postcommit. The carrier retains the actual receipt, so its owner
            // can report delivery failure without replaying selection or pretending it rolled back.
            throw new NativeSessionTreePublicationException(receipt, original,
                original is { IsFaulted: true } ? original.Exception! : direct, direct);
        }
    }
}

internal sealed class NativeSessionTreePublicationException(SessionTreeNavigationReceipt committedReceipt,
    Task? original, Exception evidence, Exception direct)
    : IOException("Selected session tree committed; native lifecycle publication failed.", evidence)
{
    internal SessionTreeNavigationReceipt CommittedReceipt { get; } = committedReceipt;
    internal Task? Original { get; } = original;
    internal Exception Evidence { get; } = evidence;
    internal Exception Direct { get; } = direct;
}
