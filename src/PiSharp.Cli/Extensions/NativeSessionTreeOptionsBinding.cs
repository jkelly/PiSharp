using PiSharp.CodingAgent;
using PiSharp.Extensions;

namespace PiSharp.Cli.Extensions;

/// <summary>No acquisition. Actual profile supplies its already admitted summary/settings binding.</summary>
internal sealed class NativeSessionTreeOptionsBinding(ReplaceableAgentSession owner, NativeExtensionContextFacadeHost reads,
    SessionTreeNavigationExecution admittedExecution)
{
    internal Task<SessionTreeNavigationReceipt> NavigateAsync(IExtensionCommandContext context, string targetId,
        SessionTreeNavigationOptions options, CancellationToken token)
    {
        _ = reads.GetCwd(context);
        var attachment = reads.CaptureRegistrationActionAttachment(context.OperationCancellationToken);
        if (!ReferenceEquals(attachment, owner.Current)) throw new InvalidOperationException("Foreign tree owner.");
        var view = owner.CaptureTree(attachment, token);
        return owner.NavigateTreeAsync(attachment, new(targetId, view.Revision) { Options = options, Execution = admittedExecution }, token);
    }
}
