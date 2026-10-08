using System.Collections.Immutable;

namespace PiSharp.Extensions.Runtime;

public sealed partial class ExtensionRegistry
{
    internal IExtensionRegistration Register(RegistrationScope scope,ExtensionSessionBeforeTreeHandlerDescriptor descriptor)
    {
        const string op="register-session-before-tree";
        if(descriptor is null||!ValidNames(descriptor.RegistrationId,"session_before_tree")||
            descriptor.HandleAsync is null||descriptor.HandleAsync.GetInvocationList().Length!=1)
            throw Failure(ExtensionRegistrationFailure.InvalidDescriptor,scope.OwnerId,op);
        return Add(scope,descriptor.RegistrationId,"session_before_tree",RegistrationKind.SessionBeforeTreeHandler,
            descriptor,(long)descriptor.RegistrationId.Length+"session_before_tree".Length,op);
    }
    public async ValueTask<ExtensionBeforeTreeResult> BeforeSessionTreeAsync(ExtensionRegistrySnapshot captured,
        ExtensionBeforeTreeEvent proposal,CancellationToken operation=default,CancellationToken session=default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var admitted=Admit(captured,RegistrationKind.SessionBeforeTreeHandler,"session_before_tree","before-tree",operation,session);
        var originals=new List<ExtensionTreeOriginal>();
        var callbackTokens=new Dictionary<Task,CancellationToken>(ReferenceEqualityComparer.Instance);
        bool CancellationOnly(ExtensionTreeDispatchException failure)
        {
            var canceled=new HashSet<Exception>(ReferenceEqualityComparer.Instance);
            foreach(var row in failure.Originals)
            {
                if(row.Original.IsFaulted||row.Fault is not null)return false;
                if(row.Direct is null)continue;
                if(!row.Original.IsCanceled||row.Direct is not OperationCanceledException direct||
                    !callbackTokens.TryGetValue(row.Original,out var exact)||direct.CancellationToken!=exact||
                    !exact.IsCancellationRequested)return false;
                canceled.Add(direct);
            }
            if(canceled.Count==0||failure.InnerException is null)return false;
            var pending=new Stack<Exception>();pending.Push(failure.InnerException);
            var visited=new HashSet<Exception>(ReferenceEqualityComparer.Instance);
            while(pending.TryPop(out var node))
            {
                if(!visited.Add(node))continue;
                if(node is AggregateException aggregate)
                {
                    if(aggregate.InnerExceptions.Count==0)return false;
                    foreach(var child in aggregate.InnerExceptions)pending.Push(child);
                }
                else if(!canceled.Contains(node))return false;
            }
            return true;
        }
        async Task<T> Join<T>(Task<T> task)
        {
            try {var result=await task.ConfigureAwait(false);originals.Add(new(task,null,null));return result;}
            catch(Exception direct){originals.Add(new(task,task.IsFaulted?task.Exception:null,direct));throw;}
        }
        async Task Close(Task task)
        {
            try{await task.ConfigureAwait(false);originals.Add(new(task,null,null));}
            catch(Exception direct){originals.Add(new(task,task.IsFaulted?task.Exception:null,direct));throw;}
        }
        try
        {
            using var frame=new CallbackFrame(admitted.Select(row=>row.Scope).Distinct().ToImmutableArray());
            var reduced=new ExtensionBeforeTreeResult();
            foreach(var (scope,entry) in admitted)
            {
                if(originals.Count>509)throw Failure(ExtensionRegistrationFailure.LimitExceeded,scope.OwnerId,"before-tree-originals");
                using var linked=CancellationTokenSource.CreateLinkedTokenSource(operation,session,scope.ExtensionLifetimeCancellationToken);
                var acquired=await Join(CreateUiContextAsync(scope,operation,session).AsTask()).ConfigureAwait(false);
                Exception? primary=null;ExtensionBeforeTreeResult? patch=null;
                try
                {
                    var actualSnapshot=acquired.Context.SessionSnapshot??throw new InvalidOperationException("Before-tree has no actual session capture.");
                    var inert=OwnSessionSnapshot(new(proposal.SessionId,actualSnapshot.Generation,proposal.OldLeafId,proposal.EntriesToSummarize),scope.OwnerId);
                    var callbackOriginal=((ExtensionSessionBeforeTreeHandlerDescriptor)entry.Descriptor).HandleAsync(
                        proposal with{EntriesToSummarize=inert.BranchEntries},acquired.Context,linked.Token).AsTask();
                    _ = callbackTokens.TryAdd(callbackOriginal,linked.Token);
                    patch=await Join(callbackOriginal).ConfigureAwait(false);
                    linked.Token.ThrowIfCancellationRequested();
                }
                catch(Exception error){primary=error;}
                try{await Close(acquired.DisposeAsync().AsTask()).ConfigureAwait(false);}
                catch(Exception error){primary=primary is null?error:new AggregateException(primary,error);}
                if(primary is not null)throw new ExtensionTreeDispatchException(originals.ToImmutableArray(),primary);
                if(patch is null)continue;
                bool Text(string? text)=>text is null||text.Length<=options.MaximumJsonCharacters&&RegistrationPolicy.Scalars(text);
                if(!Text(patch.Summary?.Text)||!Text(patch.CustomInstructions?.Value)||!Text(patch.Label?.Value)||
                    patch.Summary?.Details is { } details&&!RegistrationPolicy.Json(details,options))
                    throw Failure(ExtensionRegistrationFailure.InvalidDescriptor,scope.OwnerId,"before-tree");
                reduced=new(patch.Cancel,reduced.Summary is null?patch.Summary:patch.Summary??reduced.Summary,
                    patch.CustomInstructions??reduced.CustomInstructions,patch.ReplaceInstructions??reduced.ReplaceInstructions,
                    patch.Label??reduced.Label);
                if(reduced.Cancel)return reduced with{Originals=originals.ToImmutableArray()};
            }
            return reduced with{Originals=originals.ToImmutableArray()};
        }
        catch(ExtensionTreeDispatchException error) when(operation.IsCancellationRequested&&!session.IsCancellationRequested&&
            admitted.All(row=>!row.Scope.ExtensionLifetimeCancellationToken.IsCancellationRequested)&&
            CancellationOnly(error))
        {throw new ExtensionTreeCanceledDispatchException(error,operation);}
        catch(ExtensionTreeDispatchException){throw;}
        catch(Exception error){throw new ExtensionTreeDispatchException(originals.ToImmutableArray(),error);}
        finally{ReleaseAdmission(admitted);}
    }
}
