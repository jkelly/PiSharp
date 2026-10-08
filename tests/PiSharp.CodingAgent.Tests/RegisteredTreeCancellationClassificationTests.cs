using System.Collections.Immutable;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;

internal static class RegisteredTreeCancellationClassificationTests
{
    public static (string Name,Func<Task> Run)[] Cases()=>[
        ("registered tree exact admitted cancellation joins context cleanup",()=>Run(0)),
        ("registered tree canceled callback and cleanup fault remain faulted",()=>Run(1)),
        ("registered tree foreign callback cancellation remains faulted",()=>Run(2))];
    private static async Task Run(int mode)
    {
        var f=await SessionBoundaryFixture.Open();Exception? primary=null;ExtensionRegistry? registry=null;IAsyncDisposable? plugin=null;
        using var operation=new CancellationTokenSource();using var foreign=new CancellationTokenSource();foreign.Cancel();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callback=new TaskCompletionSource<ExtensionBeforeTreeResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupFailure=new IOException("actual before-tree UI cleanup sibling");CancellationToken admitted=default;
        Task? dispatch=null;
        async Task Close()
        {cleanupEntered.TrySetResult();await release.Task;if(mode==1)throw cleanupFailure;}
        Task? close=null;
        try
        {
            var views=new PiSharp.Cli.Extensions.NativeSessionSnapshotProvider();views.Attach(f.Owner);
            registry=new(null,new UiProvider(()=>close??=f.Keep(Close())),views);
            plugin=await f.Keep(registry.ActivateAsync("tree-cancellation",new Plugin((proposal,context,token)=>
            {admitted=token;entered.TrySetResult();return new(f.Keep(callback.Task));})));
            dispatch=f.Keep(registry.BeforeSessionTreeAsync(registry.CaptureSnapshot(),new("source","right","left","left",new("left",true)),operation.Token).AsTask());
            await f.Gate(entered.Task,dispatch);operation.Cancel();
            callback.TrySetCanceled(mode==2?foreign.Token:admitted);
            await f.Gate(cleanupEntered.Task,dispatch);
            SessionBoundaryFixture.Check(!dispatch.IsCompleted,"Canceled callback detached its held context cleanup.");
            release.TrySetResult();var result=await f.Observe(dispatch);
            var evidence=result.Direct is ExtensionTreeCanceledDispatchException cancellation?cancellation.Evidence:
                result.Direct as ExtensionTreeDispatchException??throw new IOException("Missing raw registered dispatch evidence.");
            foreach(var row in evidence.Originals)f.Retain(new("registered-cancellation",row.Original,row.Fault,row.Direct));
            var callbackRow=await f.Observe(callback.Task);f.AckCanceled(callback.Task,mode==2?foreign.Token:admitted);
            if(mode==0)
            {
                SessionBoundaryFixture.Check(result.Direct is ExtensionTreeCanceledDispatchException canceled&&
                    canceled.CancellationToken==operation.Token&&dispatch.IsCanceled,"Genuine admitted cancellation not retained.");
                f.AckCanceled(dispatch,operation.Token);
            }
            else
            {
                SessionBoundaryFixture.Check(dispatch.IsFaulted&&result.Direct is ExtensionTreeDispatchException,"Mixed/foreign cancellation erased a fault.");
                SessionBoundaryFixture.Check(ReferenceEquals(evidence.InnerException,callbackRow.Direct)||
                    mode==1&&evidence.InnerException is AggregateException aggregate&&aggregate.InnerExceptions.Count==2&&
                    ReferenceEquals(aggregate.InnerExceptions[0],callbackRow.Direct)&&OnlyCleanup(aggregate.InnerExceptions[1],cleanupFailure),
                    "Actual callback/cleanup primary graph changed.");
                f.AckFault(dispatch,row=>row.Fault!.InnerExceptions.Count==1&&ReferenceEquals(row.Fault.InnerExceptions[0],evidence)&&ReferenceEquals(row.Direct,evidence));
            }
            foreach(var row in evidence.Originals)
            {
                if(row.Original.IsCanceled)f.AckCanceled(row.Original,mode==2?foreign.Token:admitted);
                else if(row.Original.IsFaulted)f.AckFault(row.Original,raw=>mode==1&&OnlyCleanup(raw.Fault!,cleanupFailure)&&OnlyCleanup(raw.Direct!,cleanupFailure));
            }
            SessionBoundaryFixture.Check(close is not null,"Actual context cleanup never started.");
            var cleanupRow=await f.Observe(close!);
            if(mode==1)f.AckFault(close!,row=>ReferenceEquals(row.Direct,cleanupFailure)&&row.Fault!.InnerExceptions.Count==1&&ReferenceEquals(row.Fault.InnerExceptions[0],cleanupFailure));
            else SessionBoundaryFixture.Check(cleanupRow.Direct is null,"Unexpected context cleanup failure.");
        }
        catch(Exception error){primary=error;}
        finally
        {
            callback.TrySetResult(null);release.TrySetResult();
            if(plugin is not null)f.StartCleanup(()=>plugin.DisposeAsync());
            if(registry is not null)f.StartCleanup(()=>registry.DisposeAsync());
            await f.Finish(primary);
        }
    }
    private static bool OnlyCleanup(Exception error,Exception exact)
        =>ReferenceEquals(error,exact)||error is AggregateException aggregate&&aggregate.InnerExceptions.Count>0&&aggregate.InnerExceptions.All(child=>OnlyCleanup(child,exact));
    private sealed class Plugin(ExtensionBeforeTreeCallback callback):IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry,CancellationToken token)
        {((IExtensionSessionTreeLifecycleRegistry)registry).RegisterSessionBeforeTreeHandler(new("before",callback));return ValueTask.CompletedTask;}
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private sealed class UiProvider(Func<Task> close):IExtensionUiProvider
    {public IExtensionUiScope OpenScope(IExtensionContext context)=>new UiScope(close);}
    private sealed class UiScope(Func<Task> close):IExtensionUiScope
    {
        public ExtensionUiCapabilities Capabilities=>ExtensionUiCapabilities.NoUi;
        public ValueTask DisposeAsync()=>new(close());
        public ValueTask<ExtensionUiOutcome<string>> SelectAsync(string title,ImmutableArray<string> choices,ExtensionUiDialogOptions? options=null,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
        public ValueTask<ExtensionUiOutcome<bool>> ConfirmAsync(string title,string message,ExtensionUiDialogOptions? options=null,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
        public ValueTask<ExtensionUiOutcome<string>> InputAsync(string title,string? placeholder=null,ExtensionUiDialogOptions? options=null,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
        public ValueTask<ExtensionUiOutcome<string>> EditorAsync(string title,string? prefill=null,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
        public ValueTask<ExtensionUiOutcome<ExtensionUiPublication>> PublishAsync(ExtensionUiNotification notification,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
    }
}
