using PiSharp.CodingAgent;
using PiSharp.Cli.Commands;
using PiSharp.Contracts;
using PiSharp.Sessions.Compaction;

internal static class SessionTreeDeferredBudgetTests
{
    public static (string Name,Func<Task> Run)[] Cases()=>[
        ("tree no-op does not consume invalid unused summary budget",()=>Run(0)),
        ("tree veto does not consume invalid unused summary budget",()=>Run(1)),
        ("tree non-summary does not consume invalid unused summary budget",()=>Run(2)),
        ("tree provided summary does not consume invalid unused summary budget",()=>Run(3)),
        ("tree restrictive independent branch-summary budget defeats broad compaction budget",()=>Run(4)),
        ("tree broad independent branch-summary budget ignores restrictive compaction budget",()=>Run(5)),
        ("tree natural synchronous budget failure preserves identity and original fault graph",()=>Run(6))];
    private static async Task Run(int mode)
    {
        var f=await SessionBoundaryFixture.Open();Exception? primary=null;var budgetCalls=0;var generator=new Generator(f);
        var failure=new IOException("actual deferred budget capture failure");
        try
        {
            var attached=f.Owner.Current;var captured=f.Owner.CaptureTree(attached);
            SessionTreeSummaryBudget Budget()
            {
                budgetCalls++;
                if(mode<4||mode==6)throw failure;
                var settings=JsonData.Parse(mode==4?"{\"compaction\":{\"reserveTokens\":1},\"branchSummary\":{\"reserveTokens\":99.9}}":
                    "{\"compaction\":{\"reserveTokens\":99.9},\"branchSummary\":{\"reserveTokens\":1}}");
                var result=OfflineSessionProfile.CaptureBranchSummaryBudget(JsonData.Parse("{\"contextWindow\":100}"),settings);
                SessionBoundaryFixture.Check(result.ReserveTokens==(mode==4?99.9:1),"Independent branch-summary setting lost.");
                var omitted=OfflineSessionProfile.CaptureBranchSummaryBudget(JsonData.Parse("{\"contextWindow\":20000}"),JsonData.Parse("{\"compaction\":{\"reserveTokens\":1}}"));
                SessionBoundaryFixture.Check(omitted.ReserveTokens==16384,"Pinned branch-summary default borrowed compaction settings.");
                return result;
            }
            var execution=new SessionTreeNavigationExecution(generator,BeforeTree:(preview,options,token)=>ValueTask.FromResult<SessionTreePreparationResult?>(
                mode==1?new(Cancel:true):mode==3?new(Summary:new("provided",TokenUsage.Zero)):null)){CaptureSummaryBudget=Budget};
            var original=f.Keep(f.Owner.NavigateTreeAsync(attached,new(mode==0?"right":"left",captured.Revision)
                {Options=new(Summarize:mode!=2),Execution=execution}));
            if(mode==6)
            {
                var row=await f.Observe(original);
                SessionBoundaryFixture.Check(original.IsFaulted&&SessionCreationSetupTests.OnlyExpectedOriginal(row.Fault!,[failure])&&
                    budgetCalls==1&&generator.Calls==0&&f.Session.Snapshot.Context.LeafId=="right","Synchronous budget failure lost original graph or published.");
                f.AckFault(original,raw=>SessionCreationSetupTests.OnlyExpectedOriginal(raw.Fault!,[failure]));
            }
            else
            {
                var receipt=await original;foreach(var row in receipt.Originals)f.Retain(row);
                SessionBoundaryFixture.Check(budgetCalls==(mode<4?0:1)&&generator.Calls==(mode==5?1:0),"Unused budget consumed or independent budget not applied.");
                SessionBoundaryFixture.Check(mode==0?receipt.NoOp:mode==1?receipt.Disposition==SessionTreeNavigationDisposition.Vetoed:
                    receipt.Disposition==SessionTreeNavigationDisposition.Selected,"Actual routing disposition changed.");
            }
        }
        catch(Exception error){primary=error;}finally{await f.Finish(primary);}
    }
    private sealed class Generator(SessionBoundaryFixture f):ISessionSummaryGenerator
    {
        internal int Calls;
        public ValueTask<SessionGeneratedSummary> GenerateAsync(SessionSummaryRequest request,CancellationToken cancellationToken=default)
        {Calls++;return new(f.Keep(Task.FromResult(new SessionGeneratedSummary("actual default branch summary",TokenUsage.Zero))));}
    }
}
