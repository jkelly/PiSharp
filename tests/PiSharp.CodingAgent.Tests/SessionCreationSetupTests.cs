using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Cli.Extensions;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;
using PiSharp.Extensions.Facade.Context;
using PiSharp.Extensions.Runtime.Facade.Context;

internal static class SessionCreationSetupTests
{
    public static (string Name,Func<Task> Run)[] Cases()=>[
        ("session setup held dropped append and callback gate replacement independently",Held),
        ("session setup veto has no callback and escaped manager loses admission",VetoAndEscape),
        ("session setup genuine registered command portable manager and final fresh context",Registered),
        ("session setup callback fault still joins dropped checkpoint before rollback",FaultAndDrain)];
    private static async Task Held()
    {
        var f=await SessionBoundaryFixture.Open(); Exception? primary=null;
        var callbackEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackRelease=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SessionCreationSetupWriter? manager=null; Task<SessionSetupAppendReceipt>? append=null;
        try
        {
            var old=f.Owner.Current;
            var operation=f.Keep(f.Owner.CreateWithSetupAsync(old,new(AgentSessionCreationKind.New),async (writer,token)=>
            {
                manager=writer;f.HoldStorage=true;
                append=f.Keep(writer.AppendMessageAsync(JsonData.Parse("{\"role\":\"user\",\"timestamp\":0,\"content\":\"setup\"}"),token));
                callbackEntered.TrySetResult(); await callbackRelease.Task;
            }));
            await f.Gate(callbackEntered.Task,operation);await f.Gate(f.StorageEntered.Task,operation);
            SessionBoundaryFixture.Check(!operation.IsCompleted&&ReferenceEquals(f.Owner.Current,old),"Setup exposed unacknowledged target.");
            f.StorageRelease.TrySetResult(); await append!;
            SessionBoundaryFixture.Check(!operation.IsCompleted,"Storage release detached callback original.");
            callbackRelease.TrySetResult();var result=await operation;foreach(var row in manager!.CaptureOriginals())f.Retain(row);
            SessionBoundaryFixture.Check(result is not null&&result.Current.Session.Snapshot.Context.Messages.Any(message=>message.Role=="user"),"Fresh context lacks setup message.");
            try { manager!.ResetLeaf();throw new IOException("Escaped manager accepted mutation."); } catch(InvalidOperationException) { }
            SessionBoundaryFixture.Check(manager!.CaptureOriginals().All(row=>row.Original.IsCompleted),"Setup left an original pending.");
        }
        catch(Exception error){primary=error;}
        finally {callbackRelease.TrySetResult();f.StorageRelease.TrySetResult();await f.Finish(primary);}
    }
    private static async Task VetoAndEscape()
    {
        var f=await SessionBoundaryFixture.Open();Exception? primary=null;
        try
        {
            var calls=0;var old=f.Owner.Current;
            f.Owner.BeforeCreation=(_,_,_)=>ValueTask.FromResult(false);
            var result=await f.Keep(f.Owner.CreateWithSetupAsync(old,new(AgentSessionCreationKind.New),(manager,token)=>{calls++;return ValueTask.CompletedTask;}));
            SessionBoundaryFixture.Check(result is null&&calls==0&&ReferenceEquals(f.Owner.Current,old),"Veto performed setup/acquisition.");
        }
        catch(Exception error){primary=error;}finally{await f.Finish(primary);}
    }
    private static async Task Registered()
    {
        var f=await SessionBoundaryFixture.Open();Exception? primary=null;ExtensionRegistry? registry=null;
        IAsyncDisposable? plugin=null;
        try
        {
            var views=new NativeSessionSnapshotProvider();views.Attach(f.Owner);
            var reads=new NativeExtensionContextFacadeHost();reads.Attach(f.Owner);
            registry=new ExtensionRegistry(null,null,views,reads);var calls=0;
            async Task RunSetup(IExtensionSessionSetupManager manager,CancellationToken setupToken)
            {
                calls++;SessionBoundaryFixture.Check(manager.GetSessionId()!="source","Setup wrote the old session.");
                await f.Keep(manager.AppendMessageAsync(JsonData.Parse("{\"role\":\"user\",\"timestamp\":0,\"content\":\"registered setup\"}"),setupToken).AsTask());
            }
            async Task RunCommand(IExtensionCommandFacade context,CancellationToken token)
            {
                var creation=(IExtensionSessionSetupCommandFacade)context;
                var returned=await f.Keep(creation.NewSessionAsync(new(Setup:
                    (manager,setupToken)=>new(f.Keep(RunSetup(manager,setupToken)))),token).AsTask());
                SessionBoundaryFixture.Check(returned is not null&&returned.SessionId==f.Session.Snapshot.Log.Header.Id&&
                    returned.GetBranch().Any(entry=>entry.Value.GetProperty("type").GetString()=="message"),"Registered facade lost fresh acknowledged setup context.");
            }
            var command=ExtensionCommandFacade.CreateCommand("setup","setup","setup",reads,
                (arguments,context,token)=>new(f.Keep(RunCommand(context,token))));
            var activation=f.Keep(registry.ActivateAsync("setup-owner",new Plugin(command)));plugin=await activation;
            await f.Keep(registry.InvokeCommandAsync(registry.CaptureSnapshot(),"setup",JsonData.EmptyObject).AsTask());
            SessionBoundaryFixture.Check(calls==1,"Portable setup callback not actually invoked.");
        }
        catch(Exception error){primary=error;}
        finally
        {
            if(plugin is not null)f.StartCleanup(()=>plugin.DisposeAsync());
            if(registry is not null)f.StartCleanup(()=>registry.DisposeAsync());
            await f.Finish(primary);
        }
    }
    private sealed class Plugin(ExtensionCommandDescriptor command):IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry,CancellationToken token)
        {registry.RegisterCommand(command);return ValueTask.CompletedTask;}
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private static async Task FaultAndDrain()
    {
        var f=await SessionBoundaryFixture.Open();Exception? primary=null;var fault=new IOException("setup callback original fault");
        var empty=new AggregateException("actual empty callback aggregate");
        var faultedCancellation=new OperationCanceledException("faulted callback OCE");
        Exception[] expected=[fault,empty,faultedCancellation];
        Task<SessionSetupAppendReceipt>? append=null;Task? callback=null;
        try
        {
            var old=f.Owner.Current;
            var operation=f.Keep(f.Owner.CreateWithSetupAsync(old,new(AgentSessionCreationKind.New),(writer,token)=>
            {
                f.HoldStorage=true;append=f.Keep(writer.AppendCustomEntryAsync("setup",token:token));
                var source=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                source.SetException(expected);callback=f.Keep(source.Task);return new(callback);
            }));
            await f.Gate(f.StorageEntered.Task,operation);
            SessionBoundaryFixture.Check(!operation.IsCompleted,"Callback fault detached pending checkpoint.");
            f.StorageRelease.TrySetResult();await append!;
            var observed=await f.Observe(operation);
            var sourceCarrier=FindOriginal(observed.Fault!,callback!)??throw new IOException("Missing callback source carrier.");
            f.Retain(new("callback",callback!,sourceCarrier.CachedFault,sourceCarrier.InnerException));
            var callbackRow=await f.Observe(callback!);
            SessionBoundaryFixture.Check(operation.IsFaulted&&observed.Direct is not null&&Contains(observed.Direct,fault)&&OnlyExpectedOriginal(observed.Fault!,expected)&&callback!.IsFaulted&&
                callbackRow.Fault!.InnerExceptions.Count==3&&callbackRow.Fault.InnerExceptions.Zip(expected).All(pair=>ReferenceEquals(pair.First,pair.Second))&&ReferenceEquals(old,f.Owner.Current),"Setup rollback lost callback original identity/source.");
            SessionBoundaryFixture.Check(FindOriginal(observed.Fault!,callback!) is { } retained&&ReferenceEquals(retained.CachedFault,callbackRow.Fault)&&ReferenceEquals(retained.InnerException,callbackRow.Direct),
                "Natural setup failure lost first full callback aggregate/direct custody.");
            f.AckFault(operation,row=>OnlyExpectedOriginal(row.Fault!,expected));
            f.AckFault(callback!,row=>row.Fault!.InnerExceptions.Count==3&&row.Fault.InnerExceptions.Zip(expected).All(pair=>ReferenceEquals(pair.First,pair.Second)));
        }
        catch(Exception error){primary=error;}finally{f.StorageRelease.TrySetResult();await f.Finish(primary);}
    }
    private static bool Contains(Exception root,Exception target)
    {
        var visited=new HashSet<Exception>(ReferenceEqualityComparer.Instance);var pending=new Stack<Exception>();pending.Push(root);
        while(pending.TryPop(out var current))
        {if(!visited.Add(current))continue;if(ReferenceEquals(current,target))return true;
         if(current is AggregateException aggregate)foreach(var child in aggregate.InnerExceptions)pending.Push(child);
         else if(current.InnerException is { } inner)pending.Push(inner);}
        return false;
    }
    internal static SessionBoundaryOriginalFaultException? FindOriginal(Exception root,Task task)
    {
        if(root is SessionBoundaryOriginalFaultException original&&ReferenceEquals(original.Original,task))return original;
        if(root is AggregateException aggregate)foreach(var child in aggregate.InnerExceptions){var found=FindOriginal(child,task);if(found is not null)return found;}
        return root.InnerException is { } inner?FindOriginal(inner,task):null;
    }
    internal static bool OnlyExpectedOriginal(Exception root,params Exception[] expected)
    {
        if(expected.Any(item=>ReferenceEquals(root,item)))return true;
        if(root is AggregateException {InnerExceptions.Count:>0} aggregate)return aggregate.InnerExceptions.All(child=>OnlyExpectedOriginal(child,expected));
        if(root is SessionBoundaryOriginalFaultException original)return OnlyExpectedOriginal(original.CachedFault,expected);
        return false;
    }
}
