using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Host;
using PiSharp.Chord.Link;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> RemoteRouteCases() =>
    [
        Case("remote.route.resolve-outlives-initial-reference", async () =>
        {
            var type = R1Type(); await using var server = R1Host(R1Server.Address()); await using var client = R1Host();
            await server.LoadAsync([R1Plugin(type)], ChordContext.Background);
            var resolving = new TaskCompletionSource<ResolvedLink>(TaskCreationOptions.RunContinuationsAsynchronously); var started = 0;
            client.Route(R1Server, new() { Authorize = (_, _) => true, Resolve = (_, _) => { started++; return resolving.Task; } }, ChordContext.Background);
            await client.LoadAsync([], ChordContext.Background);
            var first = client.Hold(type, new(), ChordContext.Background); await R1Until(() => started == 1); first.Release();
            var pair = MemoryLinkPair.Create(); server.Connect(pair.Second, new() { Principal = R1User }, ChordContext.Background);
            resolving.SetResult(new(pair.First, new() { Principal = R1User }));
            using var second = await client.GetAsync(type, new(), ChordContext.Background);
            Equal(1, started, "late resolved link admitted for next reference");
            await second.CallAsync("inc", null, ChordContext.Background);
        }),
        Case("remote.route.retry-suspension-and-per-hold-authorization", async () =>
        {
            var type = R1Type(); await using var worker = R1Host(R1Server.Address()); await using var client = R1Host();
            await worker.LoadAsync([R1Plugin(type)], ChordContext.Background); var resolutions = 0; var authorizations = 0;
            var route = client.Route(R1Server, new()
            {
                Authorize = (_, _) => { authorizations++; return true; },
                Resolve = (_, _) =>
                {
                    if (++resolutions == 1) throw new InvalidOperationException("temporarily offline");
                    var pair = MemoryLinkPair.Create(); worker.Connect(pair.Second, new() { Principal = R1User }, ChordContext.Background);
                    return Task.FromResult(new ResolvedLink(pair.First, new() { Principal = R1User }));
                },
            }, ChordContext.Background);
            await client.LoadAsync([], ChordContext.Background); using var first = client.Hold(type, new(), ChordContext.Background);
            await R1Until(() => first.State.Value.Kind == "failed"); Equal(1, resolutions, "no reconnect timer after failed resolve");
            route.Suspend(R1Server.Address()); route.Retry(R1Server.Address());
            await R1Until(() => first.State.Value.Kind == "live"); using var second = await client.GetAsync(type, new(), ChordContext.Background);
            Equal(2, resolutions, "one explicit retry resolves"); Equal(2, authorizations, "authorization for every hold");
        }),
        Case("remote.route.handshake-drop-retries-once-then-waits", async () =>
        {
            var type = R1Type(); await using var client = R1Host(); var resolutions = 0; R1Peer? firstPeer = null;
            client.Route(R1Server, new()
            {
                Authorize = (_, _) => true,
                Resolve = (_, _) =>
                {
                    resolutions++; var pair = MemoryLinkPair.Create();
                    if (resolutions == 1) firstPeer = new R1Peer(pair.Second); else pair.Second.Close();
                    return Task.FromResult(new ResolvedLink(pair.First, new() { Principal = R1User }));
                },
            }, ChordContext.Background);
            await client.LoadAsync([], ChordContext.Background); using var reference = client.Hold(type, new(), ChordContext.Background);
            await R1Until(() => firstPeer is not null); await firstPeer!.Next<HoldMessage>(); firstPeer.Dispose();
            await R1Until(() => resolutions == 2 && reference.State.Value.Kind == "failed");
            Equal(2, resolutions, "no hot retry loop for a link closed before handshake");
        }),
        Case("remote.route.relay-origin-isolates-slow-downstream", async () =>
        {
            var type = R1Type(); await using var worker = R1Host(R1Server.Address()); await using var router = R1Host();
            await worker.LoadAsync([R1Plugin(type)], ChordContext.Background);
            router.Route(R1Server, new()
            {
                Authorize = (_, _) => true,
                Resolve = (_, _) =>
                {
                    var pair = MemoryLinkPair.Create(); worker.Connect(pair.Second, new() { Principal = R1User }, ChordContext.Background);
                    return Task.FromResult(new ResolvedLink(pair.First, new() { Principal = R1User }));
                },
            }, ChordContext.Background);
            await router.LoadAsync([], ChordContext.Background);
            var a = MemoryLinkPair.Create(); var b = MemoryLinkPair.Create(); using var slow = new R1Peer(a.Second); using var fast = new R1Peer(b.Second);
            router.Accept(a.First, new() { Principal = R1User }, ChordContext.Background); router.Accept(b.First, new() { Principal = R1User }, ChordContext.Background);
            slow.Send(R1Hold(type)); fast.Send(R1Hold(type)); await slow.Next<BoundMessage>(); await fast.Next<BoundMessage>(); fast.Send(new AckMessage());
            for (var i = 0; i < 6; i++)
            {
                fast.Send(new CallMessage(i, 0, 0)); await fast.Next<ResultMessage>(); await fast.Next<StateMessage>(); fast.Send(new AckMessage());
            }
            Equal(1, slow.All.OfType<StateMessage>().Count(), "slow origin has only its second outstanding frame");
            slow.Send(new AckMessage()); await slow.Next<StateMessage>(); var folded = await slow.Next<StateMessage>();
            Check(folded.Holds[0].Members.Count > 0, "slow origin catches up only when it acknowledges");
        }),
        Case("remote.route.closed-origin-once-for-multiple-holds", async () =>
        {
            var type = R1Type(); await using var router = R1Host(); R1Peer? upstream = null;
            router.Route(R1Server, new()
            {
                Authorize = (_, _) => true,
                Resolve = (_, _) =>
                {
                    var pair = MemoryLinkPair.Create(); upstream = new R1Peer(pair.Second);
                    return Task.FromResult(new ResolvedLink(pair.First, new() { Principal = R1User }));
                },
            }, ChordContext.Background);
            await router.LoadAsync([], ChordContext.Background); var pair = MemoryLinkPair.Create(); var child = new R1Peer(pair.Second);
            router.Accept(pair.First, new() { Principal = R1User }, ChordContext.Background); child.Send(R1Hold(type, 1)); child.Send(R1Hold(type, 2));
            await R1Until(() => upstream is not null); var first = await upstream!.Next<HoldMessage>(); var second = await upstream.Next<HoldMessage>();
            Equal(first.Origin, second.Origin, "one downstream origin"); child.Dispose(); var closed = await upstream.Next<ClosedMessage>();
            Equal(first.Origin, closed.Origin, "origin attributed correctly"); Check(!upstream.All.OfType<ReleaseMessage>().Any(), "no release replay per hold");
            upstream.Dispose();
        }),
    ];
}
