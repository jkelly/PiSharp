using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace PiSharp.Extensions;

public sealed record ExtensionCustomComponentIdentity(string OwnerId, long OwnerGeneration,
    long SessionGeneration, string ComponentId);
public sealed record ExtensionCustomComponentRows(ImmutableArray<string> Rows, ImmutableArray<int> CellWidths);

/// <summary>Pi 1.5.0 ctx.ui.custom() <c>blocked</c> option: what pi waits for while the component is visible, reported through
/// OSC 7501 program status. <paramref name="Kind"/> is "permission", "question" or "auth".</summary>
[Experimental("PISHARP_EXTUI001")]
public sealed record ExtensionCustomComponentBlocked(string Kind, string Message);

/// <summary>Pi 1.3.0 ctx.ui.custom(factory, { overlay: true, overlayOptions }): the component opens as an overlay on top of the screen
/// instead of in the editor slot, and its done() hides the overlay it opened (not the topmost one). <paramref name="Options"/> is the
/// overlayOptions object as JSON (width, minWidth, maxHeight, anchor, offsetX, offsetY, row, col, margin, nonCapturing); null uses the
/// overlay defaults.</summary>
[Experimental("PISHARP_EXTUI001")]
public sealed record ExtensionCustomComponentOverlay(JsonElement? Options = null);

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

    /// <summary>Pi 1.3.0: show the component as an overlay rather than in the editor slot; null shows it inline.</summary>
    [Experimental("PISHARP_EXTUI001")]
    public ExtensionCustomComponentOverlay? Overlay { get; init; }

    /// <summary>Pi 1.5.0 Component.capturesViewportInput of the extension's component: while it reports true, the alternate-screen
    /// viewport leaves its scroll keys to the focused component. Null reports false.</summary>
    [Experimental("PISHARP_EXTUI001")]
    public Func<bool>? CapturesViewportInput { get; init; }
}

/// <summary>Optional actual native broker. Signals acknowledge admission without joining the callback
/// currently publishing them. Open owns full input/render/disposal and physical presentation settlement.</summary>
public interface IExtensionCustomComponentUi
{
    Task OpenCustomComponentAsync(ExtensionCustomComponentCallbacks component, CancellationToken token = default);
    Task SignalCustomComponentDoneAsync(ExtensionCustomComponentIdentity identity, CancellationToken token = default);
    Task InvalidateCustomComponentAsync(ExtensionCustomComponentIdentity identity, CancellationToken token = default);
}
