using PiSharp.Contracts;

namespace PiSharp.Extensions;

/// <summary>Explicit extension-provided summary data. A null boundary retains no earlier entry.</summary>
public sealed record ExtensionSessionCompactionSummary(string? FirstKeptEntryId, string Text,
    TokenUsage? Usage = null, JsonData? Details = null);
public interface IExtensionSessionCompactionContext : IExtensionSessionActionsContext
{
    ValueTask<ExtensionSessionCompactionAcknowledgment?> AppendCompactionSummaryAsync(
        ExtensionSessionCompactionSummary summary, CancellationToken cancellationToken = default);
}
public interface IExtensionSessionCompactionProvider : IExtensionSessionActionProvider { }
public interface IExtensionSessionCompactionScope : IExtensionSessionActionScope
{
    ValueTask<ExtensionSessionCompactionAcknowledgment?> AppendCompactionSummaryAsync(ExtensionSessionCompactionSummary summary,
        Func<ExtensionSessionSnapshot, CancellationToken, ValueTask> validateProspectiveSnapshot, CancellationToken cancellationToken);
}
public sealed record ExtensionSessionCompactionAcknowledgment(ExtensionSessionEntryAcknowledgment Checkpoint,
    ExtensionSessionSnapshot Snapshot);
public sealed class ExtensionSessionCompactionCommittedException(ExtensionSessionEntryAcknowledgment checkpoint, Exception inner)
    : Exception("The host acknowledged a summary, but its returned view could not be admitted; inspect the checkpoint before retrying.", inner)
{
    public ExtensionSessionEntryAcknowledgment Checkpoint { get; } = checkpoint;
}
