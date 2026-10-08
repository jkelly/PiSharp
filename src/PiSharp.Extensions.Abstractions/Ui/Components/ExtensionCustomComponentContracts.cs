using System.Collections.Immutable;

namespace PiSharp.Extensions;

public sealed record ExtensionCustomComponentIdentity(string OwnerId, long OwnerGeneration,
    long SessionGeneration, string ComponentId);
public sealed record ExtensionCustomComponentRows(ImmutableArray<string> Rows, ImmutableArray<int> CellWidths);

/// <summary>Actual worker callbacks supplied by the admitted native transport owner. Source objects,
/// constructors, theme, keybindings and render state remain in their original realm.</summary>
public sealed record ExtensionCustomComponentCallbacks(ExtensionCustomComponentIdentity Identity,
    Func<int, IExtensionContext, CancellationToken, Task<ExtensionCustomComponentRows>> Render,
    Func<string, IExtensionContext, CancellationToken, Task> Input,
    Func<IExtensionContext, Task> Dispose);

/// <summary>Optional actual native broker. Signals acknowledge admission without joining the callback
/// currently publishing them. Open owns full input/render/disposal and physical presentation settlement.</summary>
public interface IExtensionCustomComponentUi
{
    Task OpenCustomComponentAsync(ExtensionCustomComponentCallbacks component, CancellationToken token = default);
    Task SignalCustomComponentDoneAsync(ExtensionCustomComponentIdentity identity, CancellationToken token = default);
    Task InvalidateCustomComponentAsync(ExtensionCustomComponentIdentity identity, CancellationToken token = default);
}
