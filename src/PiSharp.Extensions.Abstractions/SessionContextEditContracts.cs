using PiSharp.Contracts;

namespace PiSharp.Extensions;

/// <summary>Optional captured-attachment action. Null omits; replacement is an exact owned content object.</summary>
public interface IExtensionSessionContextEditContext : IExtensionSessionActionsContext
{
    ValueTask<ExtensionSessionContextEditAcknowledgment> AppendContextEditAsync(string targetId,
        JsonData? replacement, CancellationToken cancellationToken = default);
}
/// <summary>The checkpoint and an immutable post-edit read view. The view grants no mutation authority.</summary>
public sealed record ExtensionSessionContextEditAcknowledgment(ExtensionSessionEntryAcknowledgment Checkpoint,
    ExtensionSessionSnapshot Snapshot);
public interface IExtensionSessionContextEditProvider : IExtensionSessionActionProvider { }
public interface IExtensionSessionContextEditScope : IExtensionSessionActionScope
{
    /// <summary>The actual prospective branch must pass the eventual SDK policy before append effects.</summary>
    ValueTask<ExtensionSessionContextEditAcknowledgment> AppendContextEditAsync(string targetId, JsonData? replacement,
        Func<ExtensionSessionSnapshot, CancellationToken, ValueTask> validateProspectiveSnapshot,
        CancellationToken cancellationToken);
}
public sealed class ExtensionSessionContextEditCommittedException(ExtensionSessionEntryAcknowledgment checkpoint, Exception inner)
    : Exception("The host acknowledged a context edit, but its returned view could not be admitted; inspect the checkpoint before retrying.", inner)
{
    public ExtensionSessionEntryAcknowledgment Checkpoint { get; } = checkpoint;
}
