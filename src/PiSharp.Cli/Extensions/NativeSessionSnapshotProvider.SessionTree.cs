using System.Collections.Immutable;
using PiSharp.CodingAgent;
using PiSharp.Extensions;

namespace PiSharp.Cli.Extensions;

internal sealed partial class NativeSessionSnapshotProvider
{
    private Func<ReplaceableAgentSession,AgentSessionAttachment,ExtensionSessionTreeRequest,
        Func<ExtensionSessionSnapshot,CancellationToken,ValueTask>,CancellationToken,Task<SessionTreeNavigationReceipt>>? treeRunner;
    internal void ConfigureTreeRunner(Func<ReplaceableAgentSession,AgentSessionAttachment,ExtensionSessionTreeRequest,
        Func<ExtensionSessionSnapshot,CancellationToken,ValueTask>,CancellationToken,Task<SessionTreeNavigationReceipt>> runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        if(runner.GetInvocationList().Length!=1||Interlocked.CompareExchange(ref treeRunner,runner,null) is not null)
            throw new InvalidOperationException("Tree runner must be captured exactly once by the actual profile.");
    }
    private sealed partial class Scope
    {
        private readonly List<ExtensionTreeOriginal> treeOriginals=[];
        public ImmutableArray<ExtensionTreeOriginal> CaptureOriginals(){lock(gate)return treeOriginals.ToImmutableArray();}
        private async Task<SessionTreeNavigationReceipt> JoinTree(Task<SessionTreeNavigationReceipt> original)
        {
            int index;lock(gate){index=treeOriginals.Count;treeOriginals.Add(new(original,null,null));}
            try{return await original.ConfigureAwait(false);}
            catch(Exception direct)
            {lock(gate)treeOriginals[index]=new(original,original.IsFaulted?original.Exception:null,direct);throw;}
        }
        public ValueTask<ExtensionSessionTreeScopeResult> NavigateAsync(ExtensionSessionTreeRequest request,
            Func<ExtensionSessionSnapshot,CancellationToken,ValueTask> validateProspectiveSnapshot,CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(request);ArgumentNullException.ThrowIfNull(validateProspectiveSnapshot);
            if(string.IsNullOrWhiteSpace(request.TargetId)||request.TargetId.Length>128||!TreeText(request.TargetId)||
                !TreeText(request.CustomInstructions)||!TreeText(request.Label))
                throw new ArgumentException("Invalid bounded tree options.");
            var runner=Volatile.Read(ref provider.treeRunner)??throw new NotSupportedException("No actual profile tree admission.");
            return new(Admit(async ()=>
            {
                using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,context.OperationCancellationToken,
                    context.ExtensionLifetimeCancellationToken,SessionCancellationToken);
                var runnerOriginal=runner(host,attachment,request,validateProspectiveSnapshot,linked.Token);
                var receipt=await JoinTree(runnerOriginal).ConfigureAwait(false);
                // The engine receipt is the acknowledged state. A concurrent later
                // replacement must not erase it by rereading mutable owner.Current.
                var snapshot=new ExtensionSessionSnapshot(receipt.SessionId,receipt.View.Generation,receipt.LeafId,
                    receipt.Context.Ancestry.Select(entry=>entry.WireBody).ToImmutableArray()){Persistence=Snapshot.Persistence};
                ExtensionSessionEntryAcknowledgment? checkpoint=null;
                if(receipt.Checkpoint is { } written)
                {
                    var entry=written.Append.Entries[^1];
                    checkpoint=new(snapshot.SessionId,snapshot.Generation,entry.WireBody,written.Append.Sequence,
                        written.Append.ByteOffset,written.Append.ByteLength,snapshot.SelectedLeafId)
                        {Persistence=Persistence(written.Append.Snapshot.StorageDurability)};
                }
                var fresh=new Scope(provider,host,attachment,context,snapshot,baseSessionToken);
                return new ExtensionSessionTreeScopeResult(fresh,receipt.Disposition.ToString(),receipt.EditorText,checkpoint)
                    {Originals=receipt.Originals.Select(row=>new ExtensionTreeOriginal(row.Original,row.Fault,row.Observed)).ToImmutableArray().AddRange(CaptureOriginals())};
            }));
        }
        private static bool TreeText(string? text)
        {
            if(text is null)return true;if(text.Length>65536)return false;
            for(var index=0;index<text.Length;index++)
                if(char.IsSurrogate(text[index])&&(!char.IsHighSurrogate(text[index])||++index>=text.Length||!char.IsLowSurrogate(text[index])))return false;
            return true;
        }
    }
}
