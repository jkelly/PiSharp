// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): next/remote/path.ts.
using PiSharp.Chord.Data;
using PiSharp.Telemetry;

namespace PiSharp.Chord.Remote;

internal interface IUpstream
{
    ITelemetrySpan? Span => null;
    void Attach(HostLink link);
    void Dropped(ChordError error);
    void Unreachable(ChordError error);
}

internal sealed class RemotePath(Action<string, IUpstream?> changed)
{
    private readonly HashSet<IUpstream> holders = [];
    internal HostLink? Link { get; private set; }
    internal bool Linked { get; private set; }
    internal bool Held => holders.Count != 0;
    internal IUpstream[] Holders => [.. holders];
    internal void Attach(IUpstream holder)
    {
        holders.Add(holder);
        if (Link is { } link) holder.Attach(link); else changed("attached", holder);
    }
    internal void Detach(IUpstream holder)
    {
        if (holders.Remove(holder) && holders.Count == 0) changed("released", null);
    }
    internal void Connected(HostLink link)
    {
        Link = link; Linked = true;
        foreach (var holder in Holders) holder.Attach(link);
    }
    internal void Disconnected(HostLink link)
    {
        if (!ReferenceEquals(Link, link)) return;
        Link = null;
        var error = new ChordError("unavailable", "the link closed");
        foreach (var holder in Holders) holder.Dropped(error);
        changed("dropped", null);
    }
    internal void Unreachable(ChordError error) { foreach (var holder in Holders) holder.Unreachable(error); }
}
