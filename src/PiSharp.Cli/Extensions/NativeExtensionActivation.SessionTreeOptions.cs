using System.Collections.Immutable;
using PiSharp.CodingAgent;
using PiSharp.Extensions;

namespace PiSharp.Cli.Extensions;

internal sealed partial class NativeExtensionActivation
{
    internal void ConfigureProfileTreeRunner(Func<ReplaceableAgentSession,AgentSessionAttachment,ExtensionSessionTreeRequest,
        Func<ExtensionSessionSnapshot,CancellationToken,ValueTask>,CancellationToken,Task<SessionTreeNavigationReceipt>> runner)
        => _sessionViews.ConfigureTreeRunner(runner);
    internal async ValueTask<SessionTreePreparationResult?> DispatchBeforeTreeAsync(SessionTreeNavigationPreview preview,
        SessionTreeNavigationOptions options,CancellationToken token)
    {
        var patch=await _registry.BeforeSessionTreeAsync(Binding.Snapshot,new(preview.SessionId,preview.OldLeafId,
            preview.TargetId!,preview.NewLeafId,new(preview.TargetId!,options.Summarize,options.CustomInstructions,
                options.ReplaceInstructions,options.Label))
            {CommonAncestorId=preview.CommonAncestorId,EntriesToSummarize=preview.AbandonedEntries.Select(entry=>entry.WireBody).ToImmutableArray()},token).ConfigureAwait(false);
        return new SessionTreePreparationResult(patch.Cancel,patch.Summary is { } summary?new(summary.Text,summary.Usage,summary.Details):null,
            patch.CustomInstructions is { } custom?new(custom.Value):null,
            patch.ReplaceInstructions is { } replace?new(replace.Value):null,patch.Label is { } label?new(label.Value):null)
            {Originals=patch.Originals.Select(row=>new SessionBoundaryOriginalEvidence("registered-before-tree",row.Original,row.Fault,row.Direct)).ToImmutableArray()};
    }
}
