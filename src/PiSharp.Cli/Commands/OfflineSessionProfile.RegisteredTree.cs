using System.Collections.Immutable;
using PiSharp.CodingAgent;
using PiSharp.Cli.Extensions;
using PiSharp.Extensions;
using PiSharp.Contracts;
using PiSharp.Sessions.Compaction;

namespace PiSharp.Cli.Commands;

internal sealed partial class OfflineSessionProfile
{
    private void BindProfileTreeRunner(NativeExtensionActivation activation,ProfileRuntimeView bindingView)
    {
        activation.ConfigureProfileTreeRunner(async (owner,attachment,request,validate,token)=>
        {
            if(!ReferenceEquals(Sessions,owner)||!ReferenceEquals(owner.Current,attachment)||
                !ReferenceEquals(CaptureRuntimeView(attachment),bindingView))throw new InvalidOperationException("Foreign tree profile/view.");
            // A user hold keeps the view alive while allowing retirement to signal
            // activation cancellation before waiting for the user's original task.
            using var hold=bindingView.Lifetime.Enter();
            var actual=owner.CaptureTree(attachment,token);
            var execution=new SessionTreeNavigationExecution(SummaryGenerator,
                BeforeTree:async (preview,options,cancellation)=>
                {
                    await validate(new(actual.SessionId,attachment.Generation,preview.Context.LeafId,
                        preview.Context.Ancestry.Select(entry=>entry.WireBody).ToImmutableArray())
                        {Persistence=NativeTreePersistence(attachment)},cancellation).ConfigureAwait(false);
                    return await activation.DispatchBeforeTreeAsync(preview,options,cancellation).ConfigureAwait(false);
                },
                AfterTree:(receipt,cancellation)=>activation.DispatchSessionTreeAsync(owner,receipt,actual.LeafId),
                ValidateProspective:(projection,cancellation)=>validate(new(actual.SessionId,attachment.Generation,projection.LeafId,
                    projection.Ancestry.Select(entry=>entry.WireBody).ToImmutableArray())
                    {Persistence=NativeTreePersistence(attachment)},cancellation))
            {CaptureSummaryBudget=()=>
            {
                owner.ValidateAttachment(attachment);
                if(!ReferenceEquals(CaptureRuntimeView(attachment),bindingView))throw new InvalidOperationException("Summary budget crossed actual profile view.");
                return CaptureBranchSummaryBudget(SelectedModelDefinition.Raw,CaptureEffectiveSettings(attachment)?.Values);
            }};
            return await owner.NavigateTreeAsync(attachment,new(request.TargetId,actual.Revision)
                {Options=new(request.Summarize,request.CustomInstructions,request.ReplaceInstructions,request.Label),Execution=execution},token).ConfigureAwait(false);
        });
    }
    internal static SessionTreeSummaryBudget CaptureBranchSummaryBudget(JsonData actualModel,JsonData? actualSettings)
    {
        var raw=actualModel.Value;
        if(!raw.TryGetProperty("contextWindow",out var window)||!window.TryGetDouble(out var contextWindow)||
            !double.IsFinite(contextWindow)||contextWindow<=0)throw new InvalidOperationException("Actual model has no finite tree-summary window.");
        // Pinned settings-manager.ts974-977: this is independent of compaction settings.
        var reserve=16384d;
        if(actualSettings is not null&&actualSettings.Value.TryGetProperty("branchSummary",out var branch))
        {
            if(branch.ValueKind==System.Text.Json.JsonValueKind.Null)return new(contextWindow,reserve);
            if(branch.ValueKind!=System.Text.Json.JsonValueKind.Object)throw new InvalidOperationException("Actual branch-summary settings are invalid.");
            if(branch.TryGetProperty("reserveTokens",out var supplied)&&supplied.ValueKind!=System.Text.Json.JsonValueKind.Null&&
                (!supplied.TryGetDouble(out reserve)||!double.IsFinite(reserve)||reserve<0))
                throw new InvalidOperationException("Actual branch-summary reserve is invalid.");
        }
        return new(contextWindow,reserve);
    }
    private static ExtensionSessionPersistence NativeTreePersistence(AgentSessionAttachment attachment)
        =>attachment.Session.Snapshot.Log.StorageDurability switch
        {
            PiSharp.Sessions.Storage.SessionLogStorageDurability.LocalFileFlush=>ExtensionSessionPersistence.DurableLocalFile,
            PiSharp.Sessions.Storage.SessionLogStorageDurability.VolatileMemory=>ExtensionSessionPersistence.VolatileMemory,
            PiSharp.Sessions.Storage.SessionLogStorageDurability.DeferredLocalFile=>ExtensionSessionPersistence.DeferredLocalFile,
            _=>throw new InvalidOperationException("Unsupported tree storage.")
        };
}
