// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): next/remote/host-link.ts.
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
    internal HostLink(ChordHost host, ILink link, LinkOptions options, LinkRole role, ChordContext context)
    {
        Host = host; Role = role; Principal = options.Principal; Trusted = options.Trusted;
        Limits = Limits.ForLink(options.Trusted, options.Limits); Context = ChordContext.WithoutAbortSignal(context);
        Consumer = new ConsumerSide(this);
        channel = new Channel(link, this, new ChannelOptions(options.Codec == LinkCodec.Text ? TextCodec.Instance : BinaryCodec.Instance,
            host.Incarnation, 256, Trusted, error => host.Report(new HostError("protocol_error", error)), host.Options.TimeProvider, Context));
    }
    internal void Start() => channel.Start();
    internal bool Open => !channel.Closed;
    internal bool Greeted => channel.Opened;
    internal string Peer => channel.Peer;
    internal void Send(Message message) => channel.Send(message);
    internal void Close() => channel.Close();
    internal void Tick(long now) => channel.Tick(now, (long)Host.Options.HeartbeatPing.TotalMilliseconds, (long)Host.Options.HeartbeatClose.TotalMilliseconds);
    internal Part Part(string json) => Parts.Origin(json, Host.Options.Compression, Context);
    internal Part OpenPart(Part part, Limits limits) => Parts.Open(part, limits, Host.Options.Compression, Context);
    internal async Task InflateOwned(Part? part, Limits limits)
    {
        if (Host.Options.Compression is null && part is Part.Deflated { Inflated: null } zipped && Parts.Oversized(part, limits.Part) is null)
            await Parts.InflateAsync(zipped, limits.Decoded, channel.ClosedToken, Context);
    }
    public void Opened() => Host.Dispatcher.Invoke(() => { if (Open && !Host.Closed) { Consumer.Opened(channel.PeerCalls); Host.LinkOpened(this); } });
    public ValueTask MessageAsync(Message message) => new(Host.Dispatcher.RunAsync(async () =>
    {
        if (!Open || Host.Closed) return;
        if (message.Origin is not null && Role == LinkRole.Child) throw new ArgumentException("origin received from a child");
        switch (message)
        {
            case HoldMessage hold:
            {
                var origin = hold.Origin ?? 0;
                if (!providers.TryGetValue(origin, out var provider)) providers.Add(origin, provider = new ProviderSide(this, origin));
                await provider.MessageAsync(message); break;
            }
            case ReleaseMessage or CallMessage or AbortMessage or RetryMessage or AckMessage:
                if (providers.TryGetValue(message.Origin ?? 0, out var found)) await found.MessageAsync(message);
                break;
            case ClosedMessage:
                if (message.Origin is { } id && providers.Remove(id, out var closed)) closed.Close();
                break;
            case BoundMessage or StateMessage or FailedMessage or LoadingMessage or ResultMessage or ErrorMessage or GoneMessage:
                await Consumer.MessageAsync(message); break;
            case RevokeMessage revoke when Role == LinkRole.Parent: Host.Revoke(revoke.Revocation, Context); break;
        }
    }));
    public void Closed() => Host.Dispatcher.Invoke(() =>
    {
        foreach (var provider in providers.Values.ToArray()) provider.Close(); providers.Clear();
        Consumer.Close(); Host.Unlink(this);
    });
    internal void Drop(ProviderSide side) { if (providers.GetValueOrDefault(side.Origin) == side) { providers.Remove(side.Origin); side.Close(); } }
    internal void RevokeUnbound(Func<JsonObject, bool> predicate, ChordError error)
    { foreach (var provider in providers.Values.ToArray()) provider.RevokeUnbound(predicate, error); }
}
