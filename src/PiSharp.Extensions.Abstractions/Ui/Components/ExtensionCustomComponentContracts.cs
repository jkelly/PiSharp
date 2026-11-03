using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace PiSharp.Extensions;

public sealed record ExtensionCustomComponentIdentity(string OwnerId, long OwnerGeneration,
    long SessionGeneration, string ComponentId);
public sealed record ExtensionCustomComponentRows(ImmutableArray<string> Rows, ImmutableArray<int> CellWidths);

/// <summary>Pi 1.5.0 ctx.ui.custom() <c>blocked</c> option: what pi waits for while the component is visible, reported through
/// OSC 7501 program status. <paramref name="Kind"/> is "permission", "question" or "auth".</summary>
[Experimental("PISHARP_EXTUI001")]
public sealed record ExtensionCustomComponentBlocked(string Kind, string Message);

/// <summary>Actual worker callbacks supplied by the admitted native transport owner. Source objects,
/// constructors, theme, keybindings and render state remain in their original realm.</summary>
public sealed record ExtensionCustomComponentCallbacks(ExtensionCustomComponentIdentity Identity,
    Func<int, IExtensionContext, CancellationToken, Task<ExtensionCustomComponentRows>> Render,
    Func<string, IExtensionContext, CancellationToken, Task> Input,
    Func<IExtensionContext, Task> Dispose)
{
    /// <summary>Pi 1.5.0: report pi as blocked while this inline component is visible; null reports nothing. Overlays ignore it.</summary>
    [Experimental("PISHARP_EXTUI001")]
    public ExtensionCustomComponentBlocked? Blocked { get; init; }
}

/// <summary>Optional actual native broker. Signals acknowledge admission without joining the callback
/// currently publishing them. Open owns full input/render/disposal and physical presentation settlement.</summary>
public interface IExtensionCustomComponentUi
{
    Task OpenCustomComponentAsync(ExtensionCustomComponentCallbacks component, CancellationToken token = default);
    Task SignalCustomComponentDoneAsync(ExtensionCustomComponentIdentity identity, CancellationToken token = default);
    Task InvalidateCustomComponentAsync(ExtensionCustomComponentIdentity identity, CancellationToken token = default);
}
