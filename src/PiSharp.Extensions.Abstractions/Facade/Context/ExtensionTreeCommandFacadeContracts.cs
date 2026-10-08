namespace PiSharp.Extensions.Facade.Context;

public interface IExtensionTreeCommandFacade : IExtensionCommandFacade
{
    ValueTask<ExtensionTreeFacadeResult> NavigateTreeAsync(ExtensionSessionTreeRequest request,CancellationToken token = default);
}
public sealed record ExtensionTreeFacadeResult(IExtensionCommandFacade Context,string Disposition,string? EditorText,
    ExtensionSessionEntryAcknowledgment? Checkpoint)
{ public System.Collections.Immutable.ImmutableArray<ExtensionTreeOriginal> Originals {get;init;}=[]; }
