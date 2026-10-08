using System.Collections.Immutable;
using PiSharp.Contracts;

namespace PiSharp.Extensions;

public sealed record ExtensionSessionTreeRequest(string TargetId, bool Summarize = false,
    string? CustomInstructions = null, bool ReplaceInstructions = false, string? Label = null);
public sealed record ExtensionTreeOverride<T>(T Value);
public sealed record ExtensionTreeSummary(string Text, TokenUsage Usage, JsonData? Details = null);
public sealed record ExtensionBeforeTreeEvent(string SessionId, string? OldLeafId, string TargetId,
    string? NewLeafId, ExtensionSessionTreeRequest Options)
{
    public string? CommonAncestorId {get;init;}
    public ImmutableArray<JsonData> EntriesToSummarize {get;init;}=[];
}
public sealed record ExtensionBeforeTreeResult(bool Cancel = false, ExtensionTreeSummary? Summary = null,
    ExtensionTreeOverride<string?>? CustomInstructions = null, ExtensionTreeOverride<bool>? ReplaceInstructions = null,
    ExtensionTreeOverride<string?>? Label = null)
{ public ImmutableArray<ExtensionTreeOriginal> Originals {get;init;}=[]; }
public delegate ValueTask<ExtensionBeforeTreeResult?> ExtensionBeforeTreeCallback(ExtensionBeforeTreeEvent proposal,
    IExtensionContext context, CancellationToken token);
public sealed record ExtensionSessionBeforeTreeHandlerDescriptor(string RegistrationId, ExtensionBeforeTreeCallback HandleAsync);
public interface IExtensionSessionTreeLifecycleRegistry : IExtensionRegistry
{ IExtensionRegistration RegisterSessionBeforeTreeHandler(ExtensionSessionBeforeTreeHandlerDescriptor descriptor); }
public interface IExtensionSessionTreeProvider : IExtensionSessionActionProvider { }
public interface IExtensionSessionTreeScope : IExtensionSessionActionScope
{
    ImmutableArray<ExtensionTreeOriginal> CaptureOriginals();
    ValueTask<ExtensionSessionTreeScopeResult> NavigateAsync(ExtensionSessionTreeRequest request,
        Func<ExtensionSessionSnapshot,CancellationToken,ValueTask> validateProspectiveSnapshot, CancellationToken token);
}
public sealed record ExtensionSessionTreeScopeResult(IExtensionSessionActionScope Scope, string Disposition, string? EditorText,
    ExtensionSessionEntryAcknowledgment? Checkpoint)
{ public ImmutableArray<ExtensionTreeOriginal> Originals {get;init;}=[]; }
public interface IExtensionSessionTreeCommandContext : IExtensionCommandContext
{ ValueTask<ExtensionSessionTreeResult> NavigateTreeAsync(ExtensionSessionTreeRequest request,CancellationToken token = default); }
public sealed record ExtensionSessionTreeResult(IExtensionCommandContext Context, string Disposition, string? EditorText,
    ExtensionSessionEntryAcknowledgment? Checkpoint)
{ public ImmutableArray<ExtensionTreeOriginal> Originals {get;init;}=[]; }
public sealed class ExtensionSessionTreeContextException(ExtensionSessionTreeScopeResult result,Exception error)
    : Exception("Tree outcome acknowledged; its fresh callback context could not be admitted.",error)
{
    public ExtensionSessionTreeScopeResult Result {get;}=result;
    public bool SelectionAcknowledged => Result.Disposition=="Selected";
}
public sealed record ExtensionTreeOriginal(Task Original,AggregateException? Fault,Exception? Direct);
public sealed class ExtensionTreeDispatchException(ImmutableArray<ExtensionTreeOriginal> originals,Exception error)
    : Exception("Registered before-tree delivery failed; actual originals retained.",error)
{ public ImmutableArray<ExtensionTreeOriginal> Originals {get;}=originals; }
public sealed class ExtensionTreeCanceledOriginalException(Task original,OperationCanceledException direct)
    : OperationCanceledException("Tree scope original canceled; actual source retained.",direct,direct.CancellationToken)
{
    public Task Original {get;}=original; public OperationCanceledException Direct {get;}=direct;
    public ImmutableArray<ExtensionTreeOriginal> Originals {get;init;}=[];
}
public sealed class ExtensionTreeCanceledDispatchException(ExtensionTreeDispatchException evidence,CancellationToken token)
    : OperationCanceledException("Actual registered tree operation cancellation; raw dispatch retained.",evidence,token)
{ public ExtensionTreeDispatchException Evidence {get;}=evidence; }
