using PiSharp.Extensions.Facade.Context;

namespace PiSharp.Extensions.Runtime.Facade.Context;

public static partial class ExtensionCommandFacade
{
    internal static IExtensionCommandFacade BorrowSessionBehaviorView(IExtensionCommandContext context,IExtensionContextReadHost host,
        Action structuralCheck,Action<Func<Task<List<Exception>>>> enrollClose)
    {
        structuralCheck();var lease=new Lease(structuralCheck);
        enrollClose(lease.CloseAsync);return new View(context,host,lease);
    }
    private sealed partial class View
    {
        private void CheckBehavior()
        {
            Check();
            if(context is ExtensionCommandContext native)native.ValidateFacadeInvocation();
        }
        private ExtensionSessionBehaviorOperationOwner BehaviorOwner=>lease.BehaviorOwner(this,context,CheckBehavior);
        private IExtensionDirectSessionBehaviorHost DirectBehavior=>host as IExtensionDirectSessionBehaviorHost??
            throw new NotSupportedException("The actual admitted host has no direct session behavior binding.");
        public ValueTask SetThinkingLevelAsync(string level)
        {CheckBehavior();var actual=BehaviorOwner;return lease.StartTask(()=>actual.SetThinkingLevelAsync(DirectBehavior,level));}
        public void Abort()
        {CheckBehavior();BehaviorOwner.Abort(DirectBehavior);}
        public ValueTask CompactWithCallbacksAsync(string? customInstructions=null,ExtensionBehaviorCompactionCallbacks? callbacks=null)
        {
            CheckBehavior();var actual=BehaviorOwner;
            return lease.StartTask(()=>actual.CompactAsync(()=>DirectBehavior.Compact(context,customInstructions,context.OperationCancellationToken),callbacks));
        }
        public ValueTask<IExtensionCommandFacade?> NewSessionWithSessionAsync(ExtensionFacadeWithSessionCallback callback,
            string? parentSession=null,CancellationToken cancellationToken=default)
            =>CreateWithSessionAsync(new(ExtensionSessionCreationKind.New,ParentSession:parentSession),callback,cancellationToken);
        public ValueTask<IExtensionCommandFacade?> ForkWithSessionAsync(string entryId,ExtensionFacadeWithSessionCallback callback,
            bool before=true,CancellationToken cancellationToken=default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
            return CreateWithSessionAsync(new(before?ExtensionSessionCreationKind.ForkBefore:ExtensionSessionCreationKind.ForkAt,EntryId:entryId),callback,cancellationToken);
        }
        private ValueTask<IExtensionCommandFacade?> CreateWithSessionAsync(ExtensionSessionCreationRequest request,
            ExtensionFacadeWithSessionCallback callback,CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(callback);CheckBehavior();
            if(callback.GetInvocationList().Length!=1)throw new ArgumentException("One with-session callback required.");
            if(context is not IExtensionSessionCreationCommandContext creation)throw new NotSupportedException("No actual session creation broker.");
            var actual=BehaviorOwner;
            return lease.Start<ExtensionSessionCreationResult?,IExtensionCommandFacade?>(()=>new(WithOwnedCallback(actual,
                ()=>creation.CreateSessionAsync(request,token).AsTask(),result=>result.Context,callback)),
                result=>result is null?null:new View(result.Context,host,lease));
        }
        public ValueTask<IExtensionCommandFacade?> SwitchSessionWithSessionAsync(string absolutePath,ExtensionFacadeWithSessionCallback callback,
            CancellationToken cancellationToken=default)
        {
            ArgumentNullException.ThrowIfNull(callback);CheckBehavior();
            if(callback.GetInvocationList().Length!=1)throw new ArgumentException("One with-session callback required.");
            if(context is not IExtensionSessionCommandContext sessions)throw new NotSupportedException("No actual session switch broker.");
            var actual=BehaviorOwner;
            return lease.Start<IExtensionSessionCommandContext?,IExtensionCommandFacade?>(()=>new(WithOwnedCallback(actual,
                ()=>sessions.SwitchSessionAsync(absolutePath,cancellationToken:cancellationToken).AsTask(),result=>result,callback)),
                result=>result is null?null:new View(result,host,lease));
        }
        private Task<T?> WithOwnedCallback<T>(ExtensionSessionBehaviorOperationOwner actual,Func<Task<T?>> acquire,
            Func<T,IExtensionCommandContext> nativeContext,ExtensionFacadeWithSessionCallback callback) where T:class
        {
            Lease? callbackLease=null;
            return actual.WithSessionAsync(acquire,nativeContext,(fresh,token)=>
            {
                // Only this already owned callback gets a scope; the originating/root lease remains fenced.
                callbackLease=new Lease();var previous=Lease.Current.Value;Lease.Current.Value=callbackLease;
                try{return callback(new View(fresh,host,callbackLease),token);}
                finally{Lease.Current.Value=previous;}
            },()=>
            {
                var admitted=callbackLease??throw new InvalidOperationException("No admitted fresh callback scope.");
                var original=admitted.CloseAsync();
                return new(original,()=>original.GetAwaiter().GetResult());
            });
        }
    }
}
