// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/remote/host-link.ts.
// 1.6.0: the call window is announced by trust (none on a link given as trusted, else the host's CallWindow), `event` messages go to the
// consumer side and event acks to their origin's provider side, a route's `opened` is told the peer's incarnation at the handshake, and
// Fence runs something once everything the peer sent before a ping has arrived.
using System.Text.Json.Nodes;
using PiSharp.Chord.Data;
using PiSharp.Chord.Host;
using PiSharp.Chord.Link;

namespace PiSharp.Chord.Remote;

internal enum LinkRole { Parent, Child, Route }
internal sealed class HostLink : IChannelHandler
{
    internal readonly ChordHost Host;
    internal readonly LinkRole Role;
    internal readonly JsonObject Principal;
    internal readonly bool Trusted;
    internal readonly Limits Limits;
    internal readonly ChordContext Context;
    internal readonly ConsumerSide Consumer;
    private readonly Channel channel;
    private readonly Dictionary<long, ProviderSide> providers = [];
    private readonly Action<string>? opened;
    // How many concurrent calls of one origin this side accepts, announced in hello: none on a link it was given as trusted, else the
    // host's CallWindow (decision "Call windows only for untrusted links").
    private readonly long? calls;
    internal HostLink(ChordHost host, ILink link, LinkOptions options, LinkRole role, ChordContext context, Action<string>? opened = null)
    {
        Host = host; Role = role; Principal = options.Principal; Trusted = options.Trusted;
        Limits = Limits.ForLink(options.Trusted, options.Limits); Context = ChordContext.WithoutAbortSignal(context);
        Consumer = new ConsumerSide(this); this.opened = opened; calls = Trusted ? null : host.Options.CallWindow;
        channel = new Channel(link, this, new ChannelOptions(options.Codec == LinkCodec.Text ? TextCodec.Instance : BinaryCodec.Instance,
            host.Incarnation, calls, Trusted, error => host.Report(new HostError("protocol_error", error)), host.Options.TimeProvider, Context, host.Telemetry.Metrics));
        ChordTelemetry.Metrics(host.Telemetry.Metrics).UpDownCounter("chord.links.open").Add(1);
    }
    internal void Start() => channel.Start();
    /// <summary>How many origins have a provider side on this link now: a side is dropped once it holds nothing (no holds, no calls, no
    /// state and no event messages in flight).</summary>
    internal int Providers => providers.Count;
    internal bool Open => !channel.Closed;
    internal bool Greeted => channel.Opened;
    internal string Peer => channel.Peer;
    internal void Send(Message message) => channel.Send(message);
    internal void Close() => channel.Close();
    /// <summary>Runs <paramref name="then"/> on the host's dispatcher once everything the peer sent before it saw a ping sent now has
    /// arrived (see <c>Channel.Fence</c>); returns what cancels it.</summary>
    internal Action Fence(Action then) => channel.Fence(() => Host.Dispatcher.Post(() => { if (Open && !Host.Closed) then(); }));
    internal void Tick(long now, long paused = 0) => channel.Tick(now, paused, (long)Host.Options.HeartbeatPing.TotalMilliseconds, (long)Host.Options.HeartbeatClose.TotalMilliseconds);
    internal Part Part(string json) => Parts.Origin(json, Host.Options.Compression, Context);
    internal Part OpenPart(Part part, Limits limits) => Parts.Open(part, limits, Host.Options.Compression, Context);
    internal async Task InflateOwned(Part? part, Limits limits)
    {
        if (Host.Options.Compression is null && part is Part.Deflated { Inflated: null } zipped && Parts.Oversized(part, limits.Part) is null)
            await Parts.InflateAsync(zipped, limits.Decoded, channel.ClosedToken, Context);
    }
    public void Opened() => Host.Dispatcher.Invoke(() =>
    {
        if (!Open || Host.Closed) return;
        // First: whoever asked knows the peer before anything else it sent is handled.
        opened?.Invoke(channel.Peer);
        Consumer.Opened(channel.PeerCalls); Host.LinkOpened(this);
    });
    public ValueTask MessageAsync(Message message) => new(Host.Dispatcher.RunAsync(async () =>
    {
        if (!Open || Host.Closed) return;
        if (message.Origin is not null && Role == LinkRole.Child) throw new ArgumentException("origin received from a child");
        switch (message)
        {
            case HoldMessage hold:
            {
                var origin = hold.Origin ?? 0;
                if (!providers.TryGetValue(origin, out var provider)) providers.Add(origin, provider = new ProviderSide(this, origin, calls));
                await provider.MessageAsync(message); break;
            }
            case ReleaseMessage or CallMessage or AbortMessage or RetryMessage or AckMessage:
                if (providers.TryGetValue(message.Origin ?? 0, out var found)) await found.MessageAsync(message);
                break;
            case ClosedMessage:
                if (message.Origin is { } id && providers.Remove(id, out var closed)) closed.Close();
                break;
            case BoundMessage or StateMessage or FailedMessage or LoadingMessage or ResultMessage or ErrorMessage or GoneMessage or EventMessage:
                await Consumer.MessageAsync(message); break;
            case RevokeMessage revoke when Role == LinkRole.Parent: Host.Revoke(revoke.Revocation, Context); break;
        }
    }));
    public void Closed() => Host.Dispatcher.Invoke(() =>
    {
        ChordTelemetry.Metrics(Host.Telemetry.Metrics).UpDownCounter("chord.links.open").Add(-1);
        foreach (var provider in providers.Values.ToArray()) provider.Close(); providers.Clear();
        Consumer.Close(); Host.Unlink(this);
    });
    internal void Drop(ProviderSide side) { if (providers.GetValueOrDefault(side.Origin) == side) { providers.Remove(side.Origin); side.Close(); } }
    internal void RevokeUnbound(Func<JsonObject, bool> predicate, ChordError error)
    { foreach (var provider in providers.Values.ToArray()) provider.RevokeUnbound(predicate, error); }
}
