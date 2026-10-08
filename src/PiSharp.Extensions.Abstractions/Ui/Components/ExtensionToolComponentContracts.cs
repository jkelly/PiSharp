namespace PiSharp.Extensions;

/// <summary>A persistent, generation-owned presentation. Its actual host marker survives the
/// creating callback's UI lease. Invalidate acknowledges paint admission, never its own render.</summary>
public interface IExtensionToolComponentPresentation : IAsyncDisposable
{
    ExtensionCustomComponentIdentity Identity { get; }
    Task ShowAsync(CancellationToken cancellationToken = default);
    Task InvalidateAsync(CancellationToken cancellationToken = default);
    Task JoinPaintsAsync();
}

/// <summary>Optional actual terminal row host. Attachment reserves a persistent owner; ShowAsync joins initial render and physical write;
/// the returned presentation owns subsequent render/write/disposal and session retirement.</summary>
public interface IExtensionToolComponentUi
{
    IExtensionToolComponentPresentation AttachToolComponent(ExtensionCustomComponentCallbacks source);
}
