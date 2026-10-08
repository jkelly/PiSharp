using PiSharp.Extensions.Facade.Context;
using PiSharp.Extensions.Runtime.Facade.Context;

namespace PiSharp.Extensions.Runtime;

public static partial class ExtensionRegistrationCommands
{
    private sealed partial class Actions
    {
        private readonly object behaviorGate=new();
        private IExtensionSessionBehaviorFacade? behaviors;
        private IExtensionSessionBehaviorFacade Behaviors
        {
            get
            {
                lock(behaviorGate)
                {
                    lease.StructuralCheck();
                    if(behaviors is not null)return behaviors;
                    var actual=(ExtensionCommandContext)lease.Context;actual.ValidateFacadeInvocation();
                    var facade=ExtensionCommandFacade.BorrowSessionBehaviorView(actual,actual.GetFacadeHostForAdapter(),
                        lease.StructuralCheck,lease.EnrollFacadeClose);
                    return behaviors=(IExtensionSessionBehaviorFacade)facade;
                }
            }
        }
        public ValueTask SetThinkingLevelAsync(string level)=>Behaviors.SetThinkingLevelAsync(level);
        public void Abort()=>Behaviors.Abort();
        public ValueTask CompactWithCallbacksAsync(string? customInstructions=null,ExtensionBehaviorCompactionCallbacks? callbacks=null)
            =>Behaviors.CompactWithCallbacksAsync(customInstructions,callbacks);
        public ValueTask<IExtensionCommandFacade?> NewSessionWithSessionAsync(ExtensionFacadeWithSessionCallback callback,
            string? parentSession=null,CancellationToken cancellationToken=default)
            =>Behaviors.NewSessionWithSessionAsync(callback,parentSession,cancellationToken);
        public ValueTask<IExtensionCommandFacade?> ForkWithSessionAsync(string entryId,ExtensionFacadeWithSessionCallback callback,
            bool before=true,CancellationToken cancellationToken=default)
            =>Behaviors.ForkWithSessionAsync(entryId,callback,before,cancellationToken);
        public ValueTask<IExtensionCommandFacade?> SwitchSessionWithSessionAsync(string absolutePath,ExtensionFacadeWithSessionCallback callback,
            CancellationToken cancellationToken=default)=>Behaviors.SwitchSessionWithSessionAsync(absolutePath,callback,cancellationToken);
    }
}
