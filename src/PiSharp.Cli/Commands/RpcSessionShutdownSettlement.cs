using System.Collections.Immutable;
using PiSharp.CodingAgent;

namespace PiSharp.Cli.Commands;

/// <summary>Owned phase-one settlement. Terminal caller must join drain/read/render/leave originals
/// before returning its acknowledgment. Do not await RuntimeCleanup from inside that callback.</summary>
internal sealed class RpcSessionShutdownSettlement(ImmutableArray<Exception> failures, PersistentAgentSessionSnapshot? session = null,
    ImmutableArray<OwnedProcessCleanupReceipt> childCleanup = default)
{
    private readonly object gate = new();
    private RpcTerminalStoppedAcknowledgment? acknowledgment;
    private readonly TaskCompletionSource<ImmutableArray<Exception>> runtimeCleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<ImmutableArray<Exception>> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal ImmutableArray<Exception> Failures { get; } = failures;
    internal PersistentAgentSessionSnapshot? Session { get; } = session;
    internal ImmutableArray<OwnedProcessCleanupReceipt> ChildCleanup { get; } = childCleanup.IsDefault ? [] : childCleanup;
    internal Task<ImmutableArray<Exception>> RuntimeCleanup => runtimeCleanup.Task;
    internal Task<ImmutableArray<Exception>> Completion => completion.Task;
    internal ImmutableArray<Exception> CaptureAcceptedTerminalFailures()
    { lock (gate) return acknowledgment?.Failures ?? []; }
    internal RpcTerminalStoppedAcknowledgment AcknowledgeTerminalStopped(ImmutableArray<Exception> cleanupFailures = default)
    {
        if (cleanupFailures.IsDefault) cleanupFailures = [];
        if (cleanupFailures.Length > 128 || cleanupFailures.Any(error => error is null))
            throw new ArgumentException("Invalid terminal cleanup failure facts.", nameof(cleanupFailures));
        lock (gate)
        {
            if (acknowledgment is not null && !acknowledgment.Failures.SequenceEqual(cleanupFailures))
                throw new InvalidOperationException("Terminal acknowledgment cannot discard or replace cleanup failure facts.", new AggregateException(cleanupFailures));
            return acknowledgment ??= new(this, cleanupFailures);
        }
    }
    internal ImmutableArray<Exception> Validate(RpcTerminalStoppedAcknowledgment receipt)
    {
        lock (gate)
            if (receipt is null || !ReferenceEquals(receipt.Owner, this) || !ReferenceEquals(receipt, acknowledgment))
                throw new InvalidOperationException("Terminal stopped acknowledgment belongs to a different shutdown settlement.");
        return receipt.Failures;
    }
    internal void CompleteRuntimeCleanup(ImmutableArray<Exception> cleanupFailures) => runtimeCleanup.TrySetResult(cleanupFailures);
    internal void Complete(ImmutableArray<Exception> failures) => completion.TrySetResult(failures);
}

internal sealed class RpcTerminalStoppedAcknowledgment(RpcSessionShutdownSettlement owner, ImmutableArray<Exception> failures)
{
    internal RpcSessionShutdownSettlement Owner { get; } = owner;
    internal ImmutableArray<Exception> Failures { get; } = failures;
}
