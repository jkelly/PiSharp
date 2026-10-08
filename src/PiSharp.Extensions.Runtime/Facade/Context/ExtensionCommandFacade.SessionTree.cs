using PiSharp.Extensions.Facade.Context;

namespace PiSharp.Extensions.Runtime.Facade.Context;

public static partial class ExtensionCommandFacade
{
    private sealed partial class View
    {
        public ValueTask<IExtensionCommandFacade?> NewSessionAsync(ExtensionNewSessionOptions options,CancellationToken token=default)
        {
            ArgumentNullException.ThrowIfNull(options);
            return CreateAsync(new(ExtensionSessionCreationKind.New,ParentSession:options.ParentSession){Setup=options.Setup},token);
        }
        public ValueTask<ExtensionTreeFacadeResult> NavigateTreeAsync(ExtensionSessionTreeRequest request,CancellationToken token=default)
        {
            Check();ArgumentNullException.ThrowIfNull(request);
            if(context is not IExtensionSessionTreeCommandContext tree)throw new NotSupportedException("No admitted tree context.");
            return lease.Start<ExtensionSessionTreeResult,ExtensionTreeFacadeResult>(()=>tree.NavigateTreeAsync(request,token),
                result=>new(new View(result.Context,host,lease),result.Disposition,result.EditorText,result.Checkpoint){Originals=result.Originals});
        }
    }
}
