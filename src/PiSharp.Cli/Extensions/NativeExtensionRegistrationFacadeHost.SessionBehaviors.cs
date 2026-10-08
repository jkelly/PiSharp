using PiSharp.Extensions;
using PiSharp.Extensions.Facade.Context;

namespace PiSharp.Cli.Extensions;

public sealed partial class NativeExtensionRegistrationFacadeHost : IExtensionDirectSessionBehaviorHost
{
    public Task SetThinkingLevel(IExtensionCommandContext context,string level,CancellationToken token)
        =>reads.SetThinkingLevel(context,level,token);
    public ExtensionBehaviorCompactionWork Compact(IExtensionCommandContext context,string? instructions,CancellationToken token)
        =>reads.Compact(context,instructions,token);
    public void Abort(IExtensionContext context)=>reads.Abort(context);
}
