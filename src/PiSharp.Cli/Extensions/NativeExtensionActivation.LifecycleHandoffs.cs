using PiSharp.Extensions;
using PiSharp.Cli.Commands;
using PiSharp.CodingAgent;
using PiSharp.Extensions.Facade.Context;

namespace PiSharp.Cli.Extensions;

internal sealed class NativeLifecycleOrigin(NativeExtensionActivation activation, AgentSessionAttachment attachment)
{
    internal NativeExtensionActivation Activation { get; } = activation;
    internal AgentSessionAttachment Attachment { get; } = attachment;
    internal Task? Original;
    internal AggregateException? Aggregate;
    internal Exception? Direct;
    internal bool Finished;
    internal ExtensionLifecycleHandoffKind? Request;
    internal string? OwnerId;
    internal long OwnerGeneration;
    internal bool Started;
}

internal sealed partial class NativeExtensionActivation
{
    private readonly AsyncLocal<NativeLifecycleOrigin?> commandLifecycleOrigin = new();
    private OfflineSessionProfile? lifecycleProfile;
    internal void BindLifecycleHandoffs(OfflineSessionProfile profile)
    {
        var previous = Interlocked.CompareExchange(ref lifecycleProfile, profile, null);
        if (previous is not null && !ReferenceEquals(previous, profile))
            throw new InvalidOperationException("Activation lifecycle owner already bound.");
        _facadeHost.BindLifecycleHandoffs(this);
    }
    private NativeLifecycleOrigin? BeginLifecycleOrigin()
    {
        if (Volatile.Read(ref lifecycleProfile) is null) return null;
        if (commandLifecycleOrigin.Value is not null)
            throw new InvalidOperationException("A command may not recursively originate lifecycle handoffs.");
        var origin = new NativeLifecycleOrigin(this, _facadeHost.CaptureRegistrationActionAttachment(_closing.Token));
        commandLifecycleOrigin.Value = origin;
        return origin;
    }
    private void EndLifecycleOrigin(NativeLifecycleOrigin? origin, Task? original, Exception? direct)
    {
        if (origin is null) return;
        origin.Original = original;
        origin.Aggregate = original?.IsFaulted == true ? original.Exception : null;
        origin.Direct = direct;
        origin.Finished = true;
        commandLifecycleOrigin.Value = null;
    }
    internal void RequestLifecycleHandoff(IExtensionCommandContext context, AgentSessionAttachment attachment,
        ExtensionLifecycleHandoffKind kind, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); _closing.Token.ThrowIfCancellationRequested();
        var origin = commandLifecycleOrigin.Value ??
            throw new NotSupportedException("Only an actual originating native command can request this handoff.");
        if (origin.Finished || !ReferenceEquals(origin.Activation, this) || !ReferenceEquals(origin.Attachment, attachment))
            throw new InvalidOperationException("Lifecycle request has no active exact command origin.");
        (Volatile.Read(ref lifecycleProfile) ?? throw new NotSupportedException("No profile lifecycle owner."))
            .EnqueueLifecycleHandoff(origin, context, kind, token);
    }
}
