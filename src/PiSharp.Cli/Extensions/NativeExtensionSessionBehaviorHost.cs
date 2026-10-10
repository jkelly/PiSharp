using PiSharp.AI;
using PiSharp.CodingAgent;
using PiSharp.Extensions;
using PiSharp.Extensions.Facade.Context;

namespace PiSharp.Cli.Extensions;

/// <summary>Actual command-idle thinking configuration and signal-only cancellation.
/// No provider/generator acquisition. Profile supplies its existing typed compaction original.</summary>
internal sealed class NativeExtensionSessionBehaviorHost : IExtensionDirectSessionBehaviorHost
{
    private readonly ReplaceableAgentSession owner;
    private readonly NativeExtensionContextFacadeHost reads;
    private readonly Func<AgentSessionAttachment, string?, CancellationToken, Task<SessionSummaryCheckpointReceipt?>>? compact;
    internal NativeExtensionSessionBehaviorHost(ReplaceableAgentSession owner, NativeExtensionContextFacadeHost reads,
        Func<AgentSessionAttachment, string?, CancellationToken, Task<SessionSummaryCheckpointReceipt?>>? compact = null)
    {
        ArgumentNullException.ThrowIfNull(owner); ArgumentNullException.ThrowIfNull(reads);
        if (compact?.GetInvocationList().Length > 1) throw new ArgumentException("One captured compaction owner required.");
        this.owner = owner; this.reads = reads; this.compact = compact;
    }
    private AgentSessionAttachment Capture(IExtensionContext context)
    {
        _ = reads.GetCwd(context); // Existing actual host validates the captured context and all lifetime tokens.
        var captured = (context as IExtensionSessionContext)?.SessionSnapshot ?? throw new InvalidOperationException("Actual session context required.");
        var attached = reads.CaptureRegistrationActionAttachment(context.OperationCancellationToken);
        if (!ReferenceEquals(attached, owner.Current)) throw new InvalidOperationException("Read host belongs to a different actual session owner.");
        owner.ValidateAttachment(attached);
        if (captured.Generation != attached.Generation || captured.SessionId != attached.Session.Snapshot.Log.Header.Id)
            throw new InvalidOperationException("Session behavior capture is stale.");
        return attached;
    }
    public Task SetThinkingLevel(IExtensionCommandContext context, string level, CancellationToken token)
    {
        var attached = Capture(context); token.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(level);
        // agent-session.ts setThinkingLevel: a level the model lacks, or one that is no level at all, is clamped (models.ts
        // clampThinkingLevel: an unknown name selects the model's first supported level), never refused.
        var selected = ThinkingLevels.Clamp(attached.Session.GetSupportedThinkingLevels(), level);
        owner.ValidateAttachment(attached);
        // Return this exact engine original. It intentionally refuses an active session operation.
        return attached.Session.ConfigureAsync(new(ThinkingLevel: selected), token);
    }
    public ExtensionBehaviorCompactionWork Compact(IExtensionCommandContext context, string? instructions, CancellationToken token)
    {
        var attached = Capture(context); token.ThrowIfCancellationRequested();
        var original = (compact ?? throw new NotSupportedException("No actual admitted compaction owner."))(attached, instructions, token)
            ?? throw new InvalidOperationException("No compaction original.");
        return new(original, () => original.GetAwaiter().GetResult() is { } receipt ? new(receipt.Entry.WireBody) : null);
    }
    public void Abort(IExtensionContext context) => Capture(context).Session.Abort();
}
