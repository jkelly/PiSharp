using PiSharp.CodingAgent;
using PiSharp.Cli.Extensions;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Facade.Context;
using PiSharp.Extensions.Runtime;
using PiSharp.Extensions.Runtime.Facade.Context;
using PiSharp.Sessions.Compaction;

internal static class RegisteredSessionBehaviorCompositionTests
{
    public static (string Name,Func<Task> Run)[] Cases()=>[
        ("command facade dropped with-session original holds settlement and fresh context",()=>Run(false,0)),
        ("registration actions dropped with-session uses originating lease and fresh context",()=>Run(true,0)),
        ("command facade compaction joins checkpoint then completion callback originals",()=>Run(false,1)),
        ("registration actions compaction joins checkpoint then completion callback originals",()=>Run(true,1)),
        ("command facade actual thinking clamp and captured idle abort",()=>Run(false,2)),
        ("registration actions actual thinking clamp and captured idle abort",()=>Run(true,2)),
        ("command facade full supplier empty-aggregate and callback fault inventory",()=>Run(false,3)),
        ("registration actions full supplier empty-aggregate and callback fault inventory",()=>Run(true,3)),
        ("command dropped replacement crosses body settlement then owns fresh callback",()=>Run(false,4)),
        ("registration dropped replacement crosses body settlement then owns fresh callback",()=>Run(true,4))];
    private static async Task Run(bool registration,int mode)
    {
        var f=await SessionBoundaryFixture.Open();Exception? primary=null;ExtensionRegistry? registry=null;IAsyncDisposable? plugin=null;
        ExtensionProviderRegistrationHost? providers=null;
        var callbackEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackRelease=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var generator=new Generator(f);var oldSession=f.Session.Snapshot.Log.Header.Id;long freshGeneration=0;var completed=0;
        Exception[] expected=[new IOException("actual supplier failure"),new AggregateException("actual empty supplier aggregate"),new IOException("actual error callback failure")];
        var failedSupplier=new TaskCompletionSource<SessionSummaryCheckpointReceipt?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if(mode==3){failedSupplier.SetException(expected.Take(2));_=f.Keep(failedSupplier.Task);}
        var replacementEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementRelease=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ExecutionContext? sourceFrame=null;ExecutionContext? freshFrame=null;Func<string>? sourceRead=null;
        IExtensionCommandFacade? escaped=null;Task? bodyOriginal=null;
        try
        {
            var views=new NativeSessionSnapshotProvider();views.Attach(f.Owner);
            var reads=new NativeExtensionContextFacadeHost();
            reads.ConfigureSessionBehaviorCompaction((attachment,instructions,token)=>mode==3?failedSupplier.Task:f.Keep(f.Owner.CompactAsync(attachment,
                new(new SessionCompactionSettings(ReserveTokens:0,KeepRecentTokens:0),ContextWindow:100000,
                    SummaryOptions:new(CustomInstructions:instructions)),generator,token)));
            reads.Attach(f.Owner);
            var host=new NativeExtensionRegistrationFacadeHost(reads,new NoActions());
            registry=new(null,null,views,host);providers=new(registry,[],new NoConfiguration());
            async Task<bool> BeforeCreation()
            {replacementEntered.TrySetResult();await f.Keep(replacementRelease.Task);return true;}
            if(mode==4)f.Owner.BeforeCreation=(attachment,request,token)=>new(f.Keep(BeforeCreation()));
            async Task Fresh(IExtensionCommandFacade fresh)
            {
                SessionBoundaryFixture.Check(fresh.SessionId!=oldSession&&fresh.SessionId==f.Session.Snapshot.Log.Header.Id&&
                    fresh.Cwd==f.Session.Snapshot.Log.Header.WireBody.Value.GetProperty("cwd").GetString(),"With-session used retired source context.");
                freshGeneration=f.Owner.Current.Generation;
                SessionBoundaryFixture.Check(((IExtensionSettingsThinkingReadFacade)fresh).GetThinkingLevel()==f.Session.Snapshot.Context.ThinkingLevel,
                    "Fresh context did not retain actual native read binding.");
                if(mode==4)
                {
                    await f.Keep(((IExtensionSessionBehaviorFacade)fresh).SetThinkingLevelAsync("high").AsTask());
                    SessionBoundaryFixture.Check(((IExtensionSettingsThinkingReadFacade)fresh).GetThinkingLevel()==f.Session.Snapshot.Context.ThinkingLevel,
                        "Late callback could not act/read through its own actual fresh admission.");
                    escaped=fresh;freshFrame=ExecutionContext.Capture();
                }
                callbackEntered.TrySetResult();await f.Keep(callbackRelease.Task);
            }
            async Task Complete(ExtensionBehaviorCompactionResult result)
            {
                completed++;SessionBoundaryFixture.Check(result.AcknowledgedEntry.Value.GetProperty("type").GetString()=="compaction","No actual compaction receipt.");
                callbackEntered.TrySetResult();await f.Keep(callbackRelease.Task);
            }
            async Task Error(Exception actual)
            {
                SessionBoundaryFixture.Check(ReferenceEquals(actual,expected[0]),"Error callback did not receive actual supplier direct failure.");
                callbackEntered.TrySetResult();await f.Keep(callbackRelease.Task);throw expected[2];
            }
            async Task Body(IExtensionSessionBehaviorFacade behaviors,IExtensionSettingsThinkingReadFacade getter)
            {
                if(mode==0||mode==4)
                {
                    var operation=f.Keep(behaviors.NewSessionWithSessionAsync((fresh,token)=>new(f.Keep(Fresh(fresh)))).AsTask());
                    // Race the real replacement terminal and finite diagnostic before intentionally
                    // dropping completion; a failure before callback entry must reach finally cleanup.
                    if(mode==0)await f.Gate(callbackEntered.Task,operation);
                    else sourceFrame=ExecutionContext.Capture();
                }
                else if(mode==1)
                    _=f.Keep(behaviors.CompactWithCallbacksAsync("registered instructions",new(OnComplete:(result,token)=>new(f.Keep(Complete(result))))).AsTask());
                else if(mode==3)
                    _=f.Keep(behaviors.CompactWithCallbacksAsync(callbacks:new(OnError:(actual,token)=>new(f.Keep(Error(actual))))).AsTask());
                else
                {
                    await f.Keep(behaviors.SetThinkingLevelAsync("high").AsTask());
                    var supported=f.Session.GetSupportedThinkingLevels();
                    SessionBoundaryFixture.Check(supported.Contains(getter.GetThinkingLevel())&&getter.GetThinkingLevel()==f.Session.Snapshot.Context.ThinkingLevel,
                        "Actual ConfigureAsync clamp was not visible through the same capture.");
                    behaviors.Abort();SessionBoundaryFixture.Check(!f.Session.Snapshot.IsDisposed,"Idle captured Abort disposed the session.");
                }
            }
            var command=registration?ExtensionRegistrationCommands.Create("behavior","behavior","behavior",providers,host,
                (arguments,actions,context,token)=>{sourceRead=()=>((IExtensionSettingsThinkingReadFacade)actions).GetThinkingLevel();
                    return new(bodyOriginal=f.Keep(Body((IExtensionSessionBehaviorFacade)actions,(IExtensionSettingsThinkingReadFacade)actions)));}):
                ExtensionCommandFacade.CreateCommand("behavior","behavior","behavior",
                    (arguments,context,token)=>{sourceRead=()=>context.Cwd;
                        return new(bodyOriginal=f.Keep(Body((IExtensionSessionBehaviorFacade)context,(IExtensionSettingsThinkingReadFacade)context)));});
            plugin=await f.Keep(registry.ActivateAsync("behavior-owner",new Plugin(command)));
            f.HoldStorage=mode==1;
            var invoke=f.Keep(registry.InvokeCommandAsync(registry.CaptureSnapshot(),"behavior",JsonData.EmptyObject).AsTask());
            if(mode==4)
            {
                await f.Gate(replacementEntered.Task,invoke);
                SessionBoundaryFixture.Check(bodyOriginal?.IsCompletedSuccessfully==true&&sourceFrame is not null&&
                    f.Owner.Current.Generation==1&&!invoke.IsCompleted,"Replacement did not cross actual originating body return.");
                var refused=false;ExecutionContext.Run(sourceFrame!,state=>
                {try{_=sourceRead!();}catch(InvalidOperationException){refused=true;}},null);
                SessionBoundaryFixture.Check(refused,"Ordinary originating admission reopened during late callback custody.");
                replacementRelease.TrySetResult();
            }
            if(mode==1)
            {
                await f.Gate(f.StorageEntered.Task,invoke);
                SessionBoundaryFixture.Check(!invoke.IsCompleted&&completed==0,"Compaction checkpoint original detached.");
                f.StorageRelease.TrySetResult();
            }
            if(mode!=2)
            {
                await f.Gate(callbackEntered.Task,invoke);
                SessionBoundaryFixture.Check(!invoke.IsCompleted,"Dropped behavior callback original detached from actual command admission.");
                if(mode==0||mode==4)SessionBoundaryFixture.Check(freshGeneration==2,"No actual session replacement occurred.");
                else if(mode==1)SessionBoundaryFixture.Check(completed==1&&generator.Calls>0,"Actual engine/callback pipeline not reached.");
                callbackRelease.TrySetResult();
            }
            if(mode==3)
            {
                var raw=await f.Observe(invoke);SessionBoundaryFixture.Check(invoke.IsFaulted&&raw.Fault is not null,"Supplier/callback faults erased.");
                var carriers=Carriers(raw.Fault!).ToArray();SessionBoundaryFixture.Check(carriers.Length==1,"Kernel settlement evidence missing/duplicated.");
                var supplier=carriers[0].Originals.Single(row=>ReferenceEquals(row.Original,failedSupplier.Task));
                SessionBoundaryFixture.Check(supplier.Aggregate?.InnerExceptions.Count==2&&
                    supplier.Aggregate.InnerExceptions.Zip(expected.Take(2)).All(pair=>ReferenceEquals(pair.First,pair.Second))&&
                    ReferenceEquals(supplier.Observed,expected[0])&&OnlyExpected(raw.Fault!,expected)&&
                    expected.All(error=>Contains(raw.Fault!,error)),"Full actual supplier/callback raw graph lost.");
                foreach(var row in carriers[0].Originals)f.Retain(new(row.Phase,row.Original,row.Aggregate,row.Observed));
                f.Retain(new("behavior-close",carriers[0].Original,carriers[0].Fault,carriers[0].Direct));
                foreach(var row in f.CapturedOriginals.Where(row=>row.Original.IsFaulted))
                {var observed=await f.Observe(row.Original);f.AckFault(row.Original,cached=>OnlyExpected(cached.Fault!,expected)&&observed.Direct is not null);}
            }
            else await invoke;
            if(mode==4)
            {
                var refused=false;SessionBoundaryFixture.Check(escaped is not null&&freshFrame is not null,"Actual fresh callback scope was not retained.");
                ExecutionContext.Run(freshFrame!,state=>{try{_=escaped!.Cwd;}catch(InvalidOperationException){refused=true;}},null);
                SessionBoundaryFixture.Check(refused,"Escaped callback View retained post-callback admission.");
            }
        }
        catch(Exception error){primary=error;}
        finally
        {
            replacementRelease.TrySetResult();callbackRelease.TrySetResult();f.StorageRelease.TrySetResult();
            if(plugin is not null)f.StartCleanup(()=>plugin.DisposeAsync());
            if(registry is not null)f.StartCleanup(()=>registry.DisposeAsync());
            if(providers is not null){try{providers.Dispose();}catch(Exception error){f.SynchronousCleanupFailures.Add(error);}}
            await f.Finish(primary);
        }
    }
    private static IEnumerable<ExtensionFacadeBehaviorSettlementException> Carriers(Exception error)
    {
        if(error is ExtensionFacadeBehaviorSettlementException carrier){yield return carrier;yield break;}
        if(error is AggregateException aggregate)foreach(var child in aggregate.InnerExceptions)foreach(var found in Carriers(child))yield return found;
        else if(error.InnerException is { } inner)foreach(var found in Carriers(inner))yield return found;
    }
    private static bool OnlyExpected(Exception error,Exception[] expected)
    {
        if(expected.Any(actual=>ReferenceEquals(actual,error)))return true;
        if(error is AggregateException {InnerExceptions.Count:>0} aggregate)return aggregate.InnerExceptions.All(child=>OnlyExpected(child,expected));
        if(error is ExtensionFacadeBehaviorSettlementException carrier)return carrier.Fault is not null&&OnlyExpected(carrier.Fault,expected)&&
            carrier.Originals.All(row=>row.Aggregate is not null?OnlyExpected(row.Aggregate,expected):row.Observed is null||OnlyExpected(row.Observed,expected));
        return false;
    }
    private static bool Contains(Exception graph,Exception exact)=>ReferenceEquals(graph,exact)||
        graph is AggregateException aggregate&&aggregate.InnerExceptions.Any(child=>Contains(child,exact))||
        graph.InnerException is { } inner&&Contains(inner,exact);
    private sealed class Plugin(ExtensionCommandDescriptor command):IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry,CancellationToken token)
        {registry.RegisterCommand(command);return ValueTask.CompletedTask;}
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private sealed class Generator(SessionBoundaryFixture fixture):ISessionSummaryGenerator
    {
        internal int Calls;
        public ValueTask<SessionGeneratedSummary> GenerateAsync(SessionSummaryRequest request,CancellationToken cancellationToken=default)
        {Calls++;return new(fixture.Keep(Task.FromResult(new SessionGeneratedSummary("actual synthetic compaction summary",TokenUsage.Zero))));}
    }
    private sealed class NoConfiguration:IExtensionProviderConfigurationAdapter
    {public ExtensionProviderDefinition Resolve(string name,JsonData configuration)=>throw new NotSupportedException();}
    private sealed class NoActions:IExtensionRegistrationActionHost
    {
        public ValueTask<bool> SetModelAsync(ModelDescriptor model,CancellationToken token)=>throw new NotSupportedException();
        public ValueTask SendMessageAsync(ExtensionCustomMessage message,ExtensionMessageOptions? options,CancellationToken token)=>throw new NotSupportedException();
        public ValueTask SendUserMessageAsync(JsonData content,ExtensionUserMessageOptions? options,CancellationToken token)=>throw new NotSupportedException();
    }
}
