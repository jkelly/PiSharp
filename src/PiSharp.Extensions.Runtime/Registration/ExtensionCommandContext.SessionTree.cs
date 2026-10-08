namespace PiSharp.Extensions.Runtime;

internal sealed partial class ExtensionCommandContext
{
    public async ValueTask<ExtensionSessionTreeResult> NavigateTreeAsync(ExtensionSessionTreeRequest request,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(request);ValidateFacadeAccess();
        if(SessionActions is not IExtensionSessionTreeScope tree||CreateFreshContext is null||ValidateCreatedSnapshot is null)
            throw new NotSupportedException("No admitted native tree broker.");
        var original=tree.NavigateAsync(request,ValidateCreatedSnapshot,token).AsTask();
        ExtensionSessionTreeScopeResult result;
        try{result=await original.ConfigureAwait(false);}
        catch(Exception direct)
        {
            var raw=tree.CaptureOriginals().Add(new(original,original.IsFaulted?original.Exception:null,direct));
            if(original.IsFaulted)throw new ExtensionTreeDispatchException(raw,direct);
            if(direct is OperationCanceledException canceled)throw new ExtensionTreeCanceledOriginalException(original,canceled){Originals=raw};
            throw;
        }
        var originals=result.Originals.Add(new(original,null,null));
        Task<IExtensionSessionCommandContext>? freshOriginal=null;
        try
        {
            freshOriginal=CreateFreshContext(result.Scope).AsTask();
            var fresh=await freshOriginal.ConfigureAwait(false);
            return new(fresh,result.Disposition,result.EditorText,result.Checkpoint){Originals=originals.Add(new(freshOriginal,null,null))};
        }
        catch(Exception error)
        {
            if(freshOriginal is not null)originals=originals.Add(new(freshOriginal,freshOriginal.IsFaulted?freshOriginal.Exception:null,error));
            throw new ExtensionSessionTreeContextException(result with{Originals=originals},error);
        }
    }
}
