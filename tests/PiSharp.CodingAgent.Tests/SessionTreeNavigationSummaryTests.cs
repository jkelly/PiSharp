using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Sessions.Compaction;
using PiSharp.Sessions.Serialization;

internal static class SessionTreeNavigationSummaryTests
{
    public static (string Name,Func<Task> Run)[] Cases()=>[
        ("tree summary no-op precedes policy and generator",NoOp),
        ("tree provided summary and label share target-parent checkpoint",Provided),
        ("tree held generator abort joins original before idle",Abort),
        ("tree summary label checkpoint held before selected context publication",HeldCheckpoint),
        ("tree natural generator fault retains raw siblings empty aggregate and faulted OCE",NaturalFault)];
    private static async Task NaturalFault()
    {
        var f=await SessionBoundaryFixture.Open();Exception? primary=null;
        Exception[] expected=[new IOException("generator original"),new AggregateException("actual empty generator fault"),
            new OperationCanceledException("faulted generator cancellation")];
        var source=new TaskCompletionSource<SessionGeneratedSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = f.Keep(source.Task);
        try
        {
            var old=f.Owner.Current;var view=f.Owner.CaptureTree(old);source.SetException(expected);
            var operation=f.Keep(f.Owner.NavigateTreeAsync(old,new("left",view.Revision)
                {Options=new(Summarize:true),Execution=new(new FaultGenerator(source.Task))}));
            var observed=await f.Observe(operation);
            var retained=SessionCreationSetupTests.FindOriginal(observed.Fault!,source.Task)??throw new IOException("Generator source carrier absent.");
            f.Retain(new("generator",source.Task,retained.CachedFault,retained.InnerException));
            var raw=await f.Observe(source.Task);
            SessionBoundaryFixture.Check(operation.IsFaulted&&source.Task.IsFaulted&&ReferenceEquals(raw.Fault,retained.CachedFault)&&
                ReferenceEquals(raw.Direct,retained.InnerException)&&raw.Fault!.InnerExceptions.Count==3&&
                raw.Fault.InnerExceptions.Zip(expected).All(pair=>ReferenceEquals(pair.First,pair.Second))&&
                SessionCreationSetupTests.OnlyExpectedOriginal(observed.Fault!,expected)&&f.Session.Snapshot.Context.LeafId=="right",
                "Natural generator fault lost full cached raw graph or changed state.");
            f.AckFault(operation,row=>SessionCreationSetupTests.OnlyExpectedOriginal(row.Fault!,expected));
            f.AckFault(source.Task,row=>row.Fault!.InnerExceptions.Count==3&&row.Fault.InnerExceptions.Zip(expected).All(pair=>ReferenceEquals(pair.First,pair.Second)));
        }
        catch(Exception error){primary=error;}finally{await f.Finish(primary);}
    }
    private sealed class FaultGenerator(Task<SessionGeneratedSummary> original):ISessionSummaryGenerator
    {
        public ValueTask<SessionGeneratedSummary> GenerateAsync(SessionSummaryRequest request,CancellationToken token=default)=>new(original);
    }
    private static async Task NoOp()
    {
        var f=await SessionBoundaryFixture.Open();Exception? primary=null;
        try
        {
            var old=f.Owner.Current;var view=f.Owner.CaptureTree(old);var generator=new Generator(f);var calls=0;
            var execution=new SessionTreeNavigationExecution(generator,BeforeTree:(preview,options,token)=>{calls++;return ValueTask.FromResult<SessionTreePreparationResult?>(new(Cancel:true));});
            var result=await f.Keep(f.Owner.NavigateTreeAsync(old,new("right",view.Revision){Options=new(Summarize:true),Execution=execution}));
            SessionBoundaryFixture.Check(result.NoOp&&calls==0&&generator.Calls==0,"No-op invoked policy or summary.");
        }
        catch(Exception error){primary=error;}finally{await f.Finish(primary);}
    }
    private static async Task Provided()
    {
        var f=await SessionBoundaryFixture.Open();Exception? primary=null;
        try
        {
            var old=f.Owner.Current;var view=f.Owner.CaptureTree(old);var generator=new Generator(f);
            var execution=new SessionTreeNavigationExecution(generator,BeforeTree:(preview,options,token)=>ValueTask.FromResult<SessionTreePreparationResult?>(
                new(Summary:new("provided",TokenUsage.Zero),Label:new("labelled"))));
            var result=await f.Keep(f.Owner.NavigateTreeAsync(old,new("left",view.Revision){Options=new(Summarize:true),Execution=execution}));
            foreach(var row in result.Originals)f.Retain(row);var append=result.Checkpoint!.Append;var summary=append.Entries.Single(entry=>entry.Kind==SessionEntryKind.BranchSummary);
            var label=append.Entries.Single(entry=>entry.Kind==SessionEntryKind.Label);
            SessionBoundaryFixture.Check(generator.Calls==0&&summary.ParentId=="model"&&label.WireBody.Value.GetProperty("targetId").GetString()==summary.Id&&
                append.CheckpointAcknowledged&&result.Context.Ancestry.Any(entry=>entry.Id==summary.Id),"Provided summary/target label not acknowledged together.");
        }
        catch(Exception error){primary=error;}finally{await f.Finish(primary);}
    }
    private static async Task Abort()
    {
        var f=await SessionBoundaryFixture.Open();Exception? primary=null;var generator=new Generator(f) {Hold=true};
        try
        {
            var old=f.Owner.Current;var view=f.Owner.CaptureTree(old);
            var operation=f.Keep(f.Owner.NavigateTreeAsync(old,new("left",view.Revision){Options=new(Summarize:true),Execution=new(generator)}));
            await f.Gate(generator.Entered.Task,operation);_ = f.Keep(generator.Original!);
            f.Session.Abort();SessionBoundaryFixture.Check(!operation.IsCompleted,"Abort detached held generator original.");
            generator.Release.TrySetResult();var result=await operation;foreach(var row in result.Originals)f.Retain(row);
            SessionBoundaryFixture.Check(result.Aborted&&result.Checkpoint is null&&f.Session.Snapshot.Context.LeafId=="right"&&generator.Original!.IsCanceled,
                "Owned Abort published or lost generator cancellation.");
            var canceled=await f.Observe(generator.Original!);
            SessionBoundaryFixture.Check(canceled.Direct is OperationCanceledException cancel&&cancel.CancellationToken==generator.Token,
                "Generator Abort token differs from the actual supplied token.");
            f.AckCanceled(generator.Original!,generator.Token);
            SessionBoundaryFixture.Check(result.Originals.Any(row=>ReferenceEquals(row.Original,generator.Original)&&row.Original.IsCompleted),"Raw generator original absent.");
        }
        catch(Exception error){primary=error;}finally{generator.Release.TrySetResult();await f.Finish(primary);}
    }
    private static async Task HeldCheckpoint()
    {
        var f=await SessionBoundaryFixture.Open();Exception? primary=null;
        try
        {
            var old=f.Owner.Current;var view=f.Owner.CaptureTree(old);f.HoldStorage=true;
            var operation=f.Keep(f.Owner.NavigateTreeAsync(old,new("left",view.Revision){Options=new(Summarize:true,Label:"atomic"),Execution=new(new Generator(f))}));
            await f.Gate(f.StorageEntered.Task,operation);
            SessionBoundaryFixture.Check(!operation.IsCompleted&&f.Session.Snapshot.Context.LeafId=="right","Context published before checkpoint ACK.");
            f.StorageRelease.TrySetResult();var result=await operation;foreach(var row in result.Originals)f.Retain(row);
            SessionBoundaryFixture.Check(result.Checkpoint?.Append.Entries.Length==2&&result.Checkpoint.Append.CheckpointAcknowledged,"Summary/label batch missing.");
        }
        catch(Exception error){primary=error;}finally{f.StorageRelease.TrySetResult();await f.Finish(primary);}
    }
    private sealed class Generator(SessionBoundaryFixture fixture) : ISessionSummaryGenerator
    {
        internal int Calls;internal bool Hold;internal Task<SessionGeneratedSummary>? Original;internal CancellationToken Token;
        internal readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<SessionGeneratedSummary> GenerateAsync(SessionSummaryRequest request,CancellationToken token=default)
        {Calls++;Token=token;Original=fixture.Keep(Run(token));return new(Original);}
        private async Task<SessionGeneratedSummary> Run(CancellationToken token)
        {Entered.TrySetResult();if(Hold)await Release.Task;token.ThrowIfCancellationRequested();return new("actual synthetic summary",TokenUsage.Zero);}
    }
}
