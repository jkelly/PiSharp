using System.Collections.Immutable;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Cli.Extensions;
using PiSharp.Extensions;
using PiSharp.Extensions.Facade.Context;
using PiSharp.Extensions.Runtime;
using PiSharp.Extensions.Runtime.Facade.Context;
using PiSharp.Sessions.Compaction;

internal static class RegisteredSessionTreeRouteTests
{
    public static (string Name,Func<Task> Run)[] Cases()=>[
        ("registered tree provided summary label and actual fresh facade",()=>Run(0)),
        ("registered tree veto preserves state and has no after observation",()=>Run(1)),
        ("registered tree joins held before checkpoint and after originals independently",()=>Run(2)),
        ("registered tree full replacement instructions reach actual summary request",()=>Run(3))];
    private static async Task Run(int mode)
    {
        var f=await SessionBoundaryFixture.Open();Exception? primary=null;ExtensionRegistry? registry=null;IAsyncDisposable? plugin=null;
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var afterEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var afterRelease=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var views=new NativeSessionSnapshotProvider();views.Attach(f.Owner);
            var reads=new NativeExtensionContextFacadeHost();reads.Attach(f.Owner);
            registry=new(null,null,views,reads);var actualRegistry=registry;var generator=new Generator(f);
            ExtensionTreeFacadeResult? returned=null;var beforeCalls=0;var afterCalls=0;
            async Task<ExtensionBeforeTreeResult?> Before(ExtensionBeforeTreeEvent proposal)
            {
                beforeCalls++;SessionBoundaryFixture.Check(proposal.Options.Summarize&&proposal.TargetId=="left"&&proposal.EntriesToSummarize.Any(entry=>entry.Value.GetProperty("id").GetString()=="right"),"Registered before-tree options/actual abandoned entries lost.");
                entered.TrySetResult();if(mode==2)await release.Task;
                return new(Cancel:mode==1,Summary:mode==3?null:new("provided registered",TokenUsage.Zero),
                    CustomInstructions:new("replacement instructions"),ReplaceInstructions:new(true),Label:new("registered label"));
            }
            async Task After()
            {afterCalls++;afterEntered.TrySetResult();if(mode==2)await afterRelease.Task;}
            async Task Command(IExtensionCommandFacade context,CancellationToken token)
            {
                var original=f.Keep(((IExtensionTreeCommandFacade)context).NavigateTreeAsync(new("left",Summarize:true,
                    CustomInstructions:"original instructions",ReplaceInstructions:false),token).AsTask());
                returned=await original;
            }
            var command=ExtensionCommandFacade.CreateCommand("tree","tree","tree",reads,
                (arguments,context,token)=>new(f.Keep(Command(context,token))));
            views.ConfigureTreeRunner(async (owner,attachment,request,validate,token)=>
            {
                var actual=owner.CaptureTree(attachment,token);
                var execution=new SessionTreeNavigationExecution(generator,BeforeTree:async (preview,options,cancellation)=>
                {
                    var patch=await actualRegistry.BeforeSessionTreeAsync(actualRegistry.CaptureSnapshot(),new(preview.SessionId,
                        preview.OldLeafId,preview.TargetId!,preview.NewLeafId,request)
                        {CommonAncestorId=preview.CommonAncestorId,EntriesToSummarize=preview.AbandonedEntries.Select(entry=>entry.WireBody).ToImmutableArray()},cancellation);
                    return new SessionTreePreparationResult(patch.Cancel,patch.Summary is { } summary?new(summary.Text,summary.Usage,summary.Details):null,
                        patch.CustomInstructions is { } instructions?new(instructions.Value):null,
                        patch.ReplaceInstructions is { } replace?new(replace.Value):null,patch.Label is { } label?new(label.Value):null)
                        {Originals=patch.Originals.Select(row=>new SessionBoundaryOriginalEvidence("registered-before",row.Original,row.Fault,row.Direct)).ToImmutableArray()};
                },AfterTree:(receipt,cancellation)=>new NativeSessionTreeObservationBinding(actualRegistry,actualRegistry.CaptureSnapshot())
                    .PublishAsync(owner,receipt,actual.LeafId),
                    ValidateProspective:(projection,cancellation)=>validate(new(actual.SessionId,attachment.Generation,projection.LeafId,
                        projection.Ancestry.Select(entry=>entry.WireBody).ToImmutableArray())
                        {Persistence=ExtensionSessionPersistence.VolatileMemory},cancellation));
                return await f.Keep(owner.NavigateTreeAsync(attachment,new(request.TargetId,actual.Revision)
                    {Options=new(request.Summarize,request.CustomInstructions,request.ReplaceInstructions,request.Label),Execution=execution},token));
            });
            plugin=await f.Keep(registry.ActivateAsync("tree-owner",new Plugin(command,
                (proposal,context,token)=>new(f.Keep(Before(proposal))),
                (observation,context,token)=>new(f.Keep(After())))));
            var old=f.Session.Snapshot.Context.LeafId;f.HoldStorage=mode==2;
            var invoke=f.Keep(registry.InvokeCommandAsync(registry.CaptureSnapshot(),"tree",JsonData.EmptyObject).AsTask());
            if(mode==2)
            {
                await f.Gate(entered.Task,invoke);SessionBoundaryFixture.Check(!invoke.IsCompleted,"Before callback detached.");
                release.TrySetResult();await f.Gate(f.StorageEntered.Task,invoke);
                SessionBoundaryFixture.Check(!invoke.IsCompleted&&f.Session.Snapshot.Context.LeafId==old,"Checkpoint published before ACK.");
                f.StorageRelease.TrySetResult();await f.Gate(afterEntered.Task,invoke);
                SessionBoundaryFixture.Check(!invoke.IsCompleted&&f.Session.Snapshot.Context.LeafId!=old,"After original detached or precommit.");
                afterRelease.TrySetResult();
            }
            await invoke;
            SessionBoundaryFixture.Check(returned is not null&&beforeCalls==1,"Registered tree facade not actually invoked.");
            if(mode==1)SessionBoundaryFixture.Check(returned!.Disposition=="Vetoed"&&afterCalls==0&&generator.Calls==0&&f.Session.Snapshot.Context.LeafId==old,"Veto had effects.");
            else
            {
                SessionBoundaryFixture.Check(returned!.Disposition=="Selected"&&afterCalls==1&&returned.Checkpoint is not null&&
                    returned.Context.LeafId==f.Session.Snapshot.Context.LeafId,"Registered result is not actual acknowledged current context.");
                foreach(var row in returned.Originals)f.Retain(new("tree-route",row.Original,row.Fault,row.Direct));
                if(mode==3)SessionBoundaryFixture.Check(generator.Calls==1&&generator.Request!.Prompt.EndsWith("replacement instructions",StringComparison.Ordinal),"Replace/custom instructions lost before actual generator.");
                else SessionBoundaryFixture.Check(generator.Calls==0,"Provided summary invoked generator.");
            }
        }
        catch(Exception error){primary=error;}
        finally
        {
            release.TrySetResult();afterRelease.TrySetResult();f.StorageRelease.TrySetResult();
            if(plugin is not null)f.StartCleanup(()=>plugin.DisposeAsync());
            if(registry is not null)f.StartCleanup(()=>registry.DisposeAsync());
            await f.Finish(primary);
        }
    }
    private sealed class Plugin(ExtensionCommandDescriptor command,ExtensionBeforeTreeCallback before,
        ExtensionObservationCallback after):IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry,CancellationToken token)
        {
            registry.RegisterCommand(command);((IExtensionSessionTreeLifecycleRegistry)registry).RegisterSessionBeforeTreeHandler(new("before-tree",before));
            registry.Observe(new("after-tree","session_tree",after));return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private sealed class Generator(SessionBoundaryFixture fixture):ISessionSummaryGenerator
    {
        internal int Calls;internal SessionSummaryRequest? Request;
        public ValueTask<SessionGeneratedSummary> GenerateAsync(SessionSummaryRequest request,CancellationToken token=default)
        {Calls++;Request=request;return new(fixture.Keep(Task.FromResult(new SessionGeneratedSummary("genuine synthetic generator",TokenUsage.Zero))));}
    }
}
