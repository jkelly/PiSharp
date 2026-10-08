using PiSharp.CodingAgent;
using PiSharp.Extensions;
using PiSharp.Extensions.Facade.Context;

namespace PiSharp.Cli.Extensions;

public sealed partial class NativeExtensionContextFacadeHost : IExtensionDirectSessionBehaviorHost
{
    private readonly object behaviorBindingGate=new();
    private Func<AgentSessionAttachment,string?,CancellationToken,Task<SessionSummaryCheckpointReceipt?>>? behaviorCompact;
    private NativeExtensionSessionBehaviorHost? sessionBehaviors;
    internal void ConfigureSessionBehaviorCompaction(Func<AgentSessionAttachment,string?,CancellationToken,Task<SessionSummaryCheckpointReceipt?>> supplier)
    {
        ArgumentNullException.ThrowIfNull(supplier);
        if(supplier.GetInvocationList().Length!=1)throw new ArgumentException("One admitted typed compaction supplier required.");
        lock(behaviorBindingGate)
        {
            if(behaviorCompact is not null||Volatile.Read(ref owner) is not null)throw new InvalidOperationException("Typed compaction must be bound once before actual attachment.");
            behaviorCompact=supplier;
        }
    }
    private void BindAttachedSessionBehaviors(ReplaceableAgentSession actual)
    {lock(behaviorBindingGate)sessionBehaviors=new(actual,this,behaviorCompact);}
    private NativeExtensionSessionBehaviorHost SessionBehaviors
    {get{lock(behaviorBindingGate)return sessionBehaviors??throw new NotSupportedException("Actual native session behavior owner is not attached.");}}
    public Task SetThinkingLevel(IExtensionCommandContext context,string level,CancellationToken token)
        =>SessionBehaviors.SetThinkingLevel(context,level,token);
    public ExtensionBehaviorCompactionWork Compact(IExtensionCommandContext context,string? instructions,CancellationToken token)
        =>SessionBehaviors.Compact(context,instructions,token);
    public void Abort(IExtensionContext context)=>SessionBehaviors.Abort(context);
}
