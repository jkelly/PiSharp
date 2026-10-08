using System.Collections.Immutable;

namespace PiSharp.Cli.Interactive;

internal sealed record TerminalCustomComponentRows(ImmutableArray<string> Rows, ImmutableArray<int> CellWidths);

/// <summary>An actual native component owner supplies each original render task; the view borrows it.</summary>
internal sealed class TerminalCustomComponentPresentation
{
    internal string OwnerId { get; }
    internal long OwnerGeneration { get; }
    internal long SessionGeneration { get; }
    internal string ComponentId { get; }
    private readonly Func<int, CancellationToken, Task<TerminalCustomComponentRows>> render;
    internal TerminalCustomComponentPresentation(string ownerId, long ownerGeneration, long sessionGeneration,
        string componentId, Func<int, CancellationToken, Task<TerminalCustomComponentRows>> render)
    {
        ArgumentException.ThrowIfNullOrEmpty(ownerId); ArgumentException.ThrowIfNullOrEmpty(componentId);
        ArgumentNullException.ThrowIfNull(render);
        if (ownerGeneration <= 0 || sessionGeneration <= 0) throw new ArgumentOutOfRangeException(nameof(ownerGeneration));
        OwnerId = ownerId; OwnerGeneration = ownerGeneration; SessionGeneration = sessionGeneration;
        ComponentId = componentId; this.render = render;
    }
    internal Task<TerminalCustomComponentRows> RenderAsync(int width, CancellationToken token) => render(width, token) ??
        throw new InvalidOperationException("Native component owner returned no original render task.");
}
