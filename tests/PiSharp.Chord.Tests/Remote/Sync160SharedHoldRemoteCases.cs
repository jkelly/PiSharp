// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/test/remote/remote-object.test.ts ("references sharing one wire
// hold"), consumer.test.ts ("cancelling one reference's calls on a shared hold"), hold.test.ts (the two shared-hold cases),
// path.test.ts (requested, suspended), replica.test.ts (generation, fork). Upstream 47e9d74f9 and 90ed3e3b1. Ported by reading, against
// real hosts and raw protocol peers; `await delivered()` is S6Settled. Cases named 160-* are authored for the native port.
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Host;
using PiSharp.Chord.Link;
using PiSharp.Chord.Remote;
using PiSharp.Chord.State;
using PiSharp.Telemetry;
using static PiSharp.Chord.Contracts.Schemas;
using static ChordSupport;
using StateEntry = PiSharp.Chord.Link.StateEntry;

internal static partial class Program
{
    private static JsonObject H6Session(string session = "S1") => new() { ["session"] = session };

    /// <summary>A client and a raw provider peer on its parent link (upstream <c>rawProvider</c>).</summary>
    private static async Task<(ChordHost Client, S6Peer Peer)> H6RawProvider(PartLimits? limits = null, InMemoryTelemetryContext? telemetry = null,
        long? calls = 256, Func<JsonObject, IEnumerable<JsonNode?>>? revokedBy = null)
    {
        var client = Hosts.Create(new()
        {
            Principal = S6User, Telemetry = telemetry, OnError = _ => { }, ReadyTimeout = TimeSpan.FromSeconds(3),
            CloseTimeout = TimeSpan.FromMilliseconds(200), RevokedBy = revokedBy,
        });
        await client.LoadAsync([], S6Ctx);
        return (client, H6RawParent(client, limits, calls));
    }

    /// <summary>A new raw peer the client connects as its parent (upstream <c>rawParent</c>).</summary>
    private static S6Peer H6RawParent(ChordHost client, PartLimits? limits = null, long? calls = 256)
    {
        var pair = MemoryLinkPair.Create();
        client.Connect(pair.First, new() { Principal = S6ServerPrincipal, Limits = limits }, S6Ctx);
        return new S6Peer(pair.Second, calls);
    }

    /// <summary>Binds <paramref name="hold"/> of the notes as an editor with <paramref name="notes"/> (their count as the count).</summary>
    private static void H6BindHold(S6Peer peer, long hold, string notes = "[]")
    {
        var names = WireNames(Notes, "editor");
        peer.Send(new BoundMessage([new BoundEntry(hold, names.Variant, [new(names.State["notes"], new Part.Text(notes)),
            new(names.State["count"], new Part.Text(JsonNode.Parse(notes)!.AsArray().Count.ToString()))], [])]));
    }
    private static async Task<long> H6BindNext(S6Peer peer, string notes = "[]")
    { var hold = (await peer.Next<HoldMessage>()).Hold; H6BindHold(peer, hold, notes); return hold; }
    private static StateMessage H6Count(long hold, int count) =>
        new([new StateEntry(hold, [new StateMember(WireNames(Notes, "editor").State["count"], new Part.Text("[[\"r\"," + count + "]]"))], [], [])]);
    private static int H6Int(IReference reference, string member = "count") => reference.Member(member).Value!.GetValue<int>();
    /// <summary>How many messages of kind <typeparamref name="T"/> wait untaken once what was sent arrived (upstream <c>taken</c>).</summary>
    private static async Task<int> H6Taken<T>(S6Peer peer, ChordHost client) where T : Message
    { await S6Settled(client); return peer.All.OfType<T>().Count(); }

    /// <summary>A remote panel whose variants differ in their state members: wide has x, y and poke, narrow only x.</summary>
    private static readonly Contract H6Panel = Contract.Remote("app.panel", new()
    {
        Version = 1, Params = new(), HostAddress = _ => Server.Address(),
        Variants = new()
        {
            ["wide"] = new() { State = { ["x"] = Int(), ["y"] = Int() }, Methods = { ["poke"] = Method(Str(), Int()) } },
            ["narrow"] = new() { State = { ["x"] = Int() } },
        },
    });
    private static WireError H6Plugin(string message) => WireError.FromException(new PluginError("derive", message));
    /// <summary>Binds <paramref name="hold"/> of the panel in <paramref name="variant"/>; a member without a value is failed.</summary>
    private static void H6BindPanel(S6Peer peer, long hold, string variant, int? x = null, int? y = null, string failure = "member threw")
    {
        var names = WireNames(H6Panel, variant); var members = new List<BoundMember>(); var failed = new List<FailedMember>();
        foreach (var (member, index) in names.State)
        {
            var value = member == "x" ? x : y;
            if (value is null) failed.Add(new(index, H6Plugin(failure))); else members.Add(new(index, new Part.Text(value.Value.ToString())));
        }
        peer.Send(new BoundMessage([new BoundEntry(hold, names.Variant, members, failed)]));
    }
    /// <summary>Sends a state message setting x of the panel's hold, or failing it (null).</summary>
    private static void H6SetX(S6Peer peer, long hold, int? x)
    {
        var member = WireNames(H6Panel, "wide").State["x"];
        peer.Send(x is null ? new StateMessage([new StateEntry(hold, [], [new FailedMember(member, H6Plugin("x threw"))], [])])
            : new StateMessage([new StateEntry(hold, [new StateMember(member, new Part.Text("[[\"r\"," + x + "]]"))], [], [])]));
    }
    /// <summary>What a watch opened now on <paramref name="member"/> gives: its value (the last good one when failed), or the rejection
    /// (upstream <c>opened</c>).</summary>
    private static async Task<string> H6Opened(IWatchable<JsonNode?> member)
    {
        try
        {
            var watch = await member.WatchAsync(S6Ctx); var value = watch.Value; await watch.StopAsync();
            return "opened " + (value?.ToJsonString() ?? "null");
        }
        catch (Exception error) { return "rejected " + error.Message; }
    }
    private static async Task<Exception> H6Rejects(Task<object?> call) { try { await call; } catch (Exception error) { return error; } throw new InvalidOperationException("the call was answered"); }
    private static string H6Code(Exception error) => error is ChordError chord ? chord.Code : error.GetType().Name;

    /// <summary>The match bound as a player over a raw provider: one reference to it.</summary>
    private static async Task<(ChordHost Client, S6Peer Peer, long Hold, IReference A)> H6Match()
    {
        var (client, peer) = await H6RawProvider();
        var a = client.Hold(S6Match, new() { ["id"] = 1 }, S6Ctx);
        var hold = (await peer.Next<HoldMessage>()).Hold; E6BindMatch(peer, hold); await S6Live(a);
        return (client, peer, hold, a);
    }
    private static void H6Paddle(S6Peer peer, long hold, params double[] ys) => peer.Send(new EventMessage([.. ys.Select(y => E6PaddleEvent(hold, y))]));

    /// <summary>A waiter placed on a session's worker: wait(tag) records its tag and runs until its context aborts.</summary>
    private static readonly Contract H6Waiter = Contract.Remote("app.tag-waiter", new()
    {
        Version = 1, Params = new() { ["session"] = Str() }, HostAddress = values => Session.Address(("id", values["session"])),
        Variants = new() { ["v"] = new() { State = { ["n"] = Int() }, Methods = { ["wait"] = Method(Str()) } } },
    });
    private static void H6ServeWaiter(IFacetHost h, Contract type, List<string> seen) => h.Serve(type, (_, _, _) => new(new Dictionary<string, object?>
    {
        ["n"] = Watchables.Constant(JsonValue.Create(0)),
        ["wait"] = (HostMethod)(async (tag, ctx) =>
        {
            lock (seen) seen.Add(((JsonNode)tag!).GetValue<string>());
            try { await Task.Delay(Timeout.Infinite, ctx.AbortSignal); } catch (OperationCanceledException) { }
            return null;
        }),
    }, "v"));
    private static string[] H6Tags(List<string> seen) { lock (seen) return [.. seen]; }

    private static IEnumerable<(string, Func<Task>)> RemoteSync160SharedHoldCases() =>
    [
        // --- sharing ---
        Case("remote.shared-hold.two-references-from-one-host-with-one-key-share-one-hold-another-key-is-another-hold", async () =>
        {
            var (client, peer) = await H6RawProvider(); await using var closing = client;
            using var first = client.Hold(Notes, H6Session(), S6Ctx); using var second = client.Hold(Notes, H6Session(), S6Ctx);
            using var other = client.Hold(Notes, H6Session("S2"), S6Ctx);
            await S6Until(() => peer.Count<HoldMessage>() == 2, "two holds"); await S6Settled(client);
            Equal(2, peer.Count<HoldMessage>(), "one hold per key");
        }),
        Case("remote.shared-hold.sharing-two-holds-of-one-remote-object-one-hold-one-snapshot-the-second-is-live-at-once-a-commit-reaches-both", async () =>
        {
            var (client, peer) = await H6RawProvider(); await using var closing = client;
            using var first = client.Hold(Notes, H6Session(), S6Ctx);
            var hold = await H6BindNext(peer, "[{\"text\":\"a\"}]"); await S6Live(first);
            using var second = client.Hold(Notes, H6Session(), S6Ctx);
            Equal("live", second.State.Value.Kind, "the second is live at once"); Equal(1, H6Int(second), "with the replica's value");
            Equal(0, await H6Taken<HoldMessage>(peer, client), "no second hold");
            var frames = new[] { FramesOf(await first.Member("count").WatchAsync(S6Ctx)), FramesOf(await second.Member("count").WatchAsync(S6Ctx)) };
            peer.Send(H6Count(hold, 2));
            await S6Until(() => frames.All(each => each.Count == 1), "the commit reaching both");
            Sequence(["[2,[[\"r\",2]]]", "[2,[[\"r\",2]]]"], frames.Select(each => FrameJson(each[0])), "one frame each");
            // Accepted difference 6: shared values are identical.
            Check(ReferenceEquals(first.Member("notes").Value, second.Member("notes").Value), "shared values are the same node");
        }),
        Case("remote.shared-hold.sharing-joins-show-the-objects-state-loading-failed-transient-live-failed-waiting-over-the-link-and-broken-hold-anew", async () =>
        {
            var (client, peer) = await H6RawProvider(); await using var closing = client;
            using var first = client.Hold(Notes, H6Session(), S6Ctx);
            var hold = (await peer.Next<HoldMessage>()).Hold;
            Equal("loading", client.Hold(Notes, H6Session(), S6Ctx).State.Value.Kind, "a join while loading");
            var error = WireError.FromException(new PluginError("down", "down"));
            peer.Send(new FailedMessage(hold, new("transient", error), 60_000));
            await S6Until(() => first.State.Value.Kind == "failed", "the failure");
            var transient = client.Hold(Notes, H6Session(), S6Ctx).State.Value;
            Check(transient is { Kind: "failed", Cause.Kind: FailureKind.Transient }, "a join to a failed transient object shows it");
            Check(transient.RetryAt > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "with its retry time");
            peer.Send(new LoadingMessage(hold)); H6BindHold(peer, hold);
            await S6Live(first);
            Equal("live", client.Hold(Notes, H6Session(), S6Ctx).State.Value.Kind, "a join to a live object");
            Equal(0, await H6Taken<HoldMessage>(peer, client), "no hold for any of the joins");
            // A refusal over the link: a new reference is a new request (a relay's failed route resolves again for it).
            peer.Send(new FailedMessage(hold, new("waiting", error)));
            await S6Until(() => first.State.Value.Kind == "failed", "the refusal");
            client.Hold(Notes, H6Session(), S6Ctx);
            Equal(1L, (await peer.Next<HoldMessage>()).Hold, "a hold of its own");
            // A broken object leaves the table for good.
            peer.Send(new FailedMessage(1, new("broken", error))); peer.Send(new FailedMessage(hold, new("broken", error)));
            await S6Until(() => first.State.Value.Cause?.Kind == FailureKind.Broken, "the broken failure"); await S6Settled(client);
            client.Hold(Notes, H6Session(), S6Ctx);
            Equal(2L, (await peer.Next<HoldMessage>()).Hold, "a new reference holds anew");
        }),
        Case("remote.shared-hold.sharing-releasing-one-sends-nothing-and-the-other-keeps-receiving-the-last-sends-one-release-a-reference-after-it-holds-anew", async () =>
        {
            var (client, peer) = await H6RawProvider(); await using var closing = client;
            var first = client.Hold(Notes, H6Session(), S6Ctx); var second = client.Hold(Notes, H6Session(), S6Ctx);
            var hold = await H6BindNext(peer); await S6Live(second);
            first.Release();
            Equal(0, await H6Taken<ReleaseMessage>(peer, client), "a non-final release sends nothing");
            peer.Send(H6Count(hold, 5));
            await S6Until(() => H6Int(second) == 5, "the other keeps receiving");
            IReference? third = null;
            S6Turn(client, () => { second.Release(); third = client.Hold(Notes, H6Session(), S6Ctx); });
            await S6Until(() => peer.Count<HoldMessage>() == 2, "the new hold"); await S6Settled(client);
            Sequence(["hold", "release", "hold"], peer.Log.Where(message => message is ReleaseMessage or HoldMessage).Select(message => message.Kind), "one release, then a new hold");
            Equal("loading", third!.State.Value.Kind, "the reference after it holds anew, also in the same turn");
        }),
        Case("remote.shared-hold.sharing-a-getter-timing-out-or-aborted-before-and-after-the-objects-first-binding-releases-only-its-own-binding", async () =>
        {
            var (client, peer) = await H6RawProvider(); await using var closing = client;
            using var kept = client.Hold(Notes, H6Session(), S6Ctx);
            var hold = (await peer.Next<HoldMessage>()).Hold;
            var late = client.GetAsync(Notes, H6Session(), S6Ctx, new(TimeSpan.FromMilliseconds(20)));
            using var cancel = new CancellationTokenSource();
            var aborted = client.GetAsync(Notes, H6Session(), ChordContext.WithAbortSignal(cancel.Token, S6Ctx));
            cancel.Cancel();
            Check(await S6Rejection(aborted) is OperationCanceledException, "the aborted getter");
            Equal("unavailable", H6Code(await S6Rejection(late)), "the getter timing out");
            H6BindHold(peer, hold); await S6Live(kept);
            // After the first binding: a getter of a failed object times out.
            peer.Send(new FailedMessage(hold, new("transient", WireError.FromException(new PluginError("down", "down"))), 60_000));
            await S6Until(() => kept.State.Value.Kind == "failed", "the failure");
            await S6Rejection(client.GetAsync(Notes, H6Session(), S6Ctx, new(TimeSpan.FromMilliseconds(20))));
            Equal(0, await H6Taken<ReleaseMessage>(peer, client), "no release: only the getters' own bindings went");
            Equal("failed", kept.State.Value.Kind, "the kept reference is untouched");
        }),
        Case("remote.shared-hold.sharing-calls-through-two-references-are-both-answered-calls-parked-per-reference-go-in-order-once-live", async () =>
        {
            var (client, peer) = await H6RawProvider(); await using var closing = client;
            using var first = client.Hold(Notes, H6Session(), S6Ctx); using var second = client.Hold(Notes, H6Session(), S6Ctx);
            var a = first.CallAsync("add", new JsonObject { ["text"] = "a" }, S6Ctx);
            await S6Settled(client);
            var b = second.CallAsync("add", new JsonObject { ["text"] = "b" }, S6Ctx);
            await S6Settled(client);
            var hold = await H6BindNext(peer);
            var calls = new[] { await peer.Next<CallMessage>(), await peer.Next<CallMessage>() };
            Sequence([hold + " {\"text\":\"a\"}", hold + " {\"text\":\"b\"}"], calls.Select(call => call.Hold + " " + Parts.Parse(call.Args!)!.ToJsonString()), "both on the one hold, in order");
            for (var index = 0; index < calls.Length; index++) peer.Send(new ResultMessage(calls[index].Call, new Part.Text((index + 1).ToString())));
            Equal(1, ((JsonNode)(await a)!).GetValue<int>(), "the first reference's answer"); Equal(2, ((JsonNode)(await b)!).GetValue<int>(), "the second's");
        }),

        // --- state changes ---
        Case("remote.shared-hold.state-a-rebind-to-another-variant-moves-every-binding-in-one-batch-a-member-the-new-variant-lacks-fails-for-all", async () =>
        {
            var (client, peer) = await H6RawProvider(); await using var closing = client;
            using var first = client.Hold(H6Panel, new(), S6Ctx); using var second = client.Hold(H6Panel, new(), S6Ctx);
            var hold = (await peer.Next<HoldMessage>()).Hold; H6BindPanel(peer, hold, "wide", 1, 2); await S6Live(second);
            var calls = new[] { ChangesOf(await first.Member("y").WatchAsync(S6Ctx)), ChangesOf(await second.Member("y").WatchAsync(S6Ctx)) };
            peer.Send(new LoadingMessage(hold)); H6BindPanel(peer, hold, "narrow", 3);
            await S6Until(() => first.Variant == "narrow" && second.Variant == "narrow" && calls.All(each => each.Count == 1), "both rebound");
            Check(calls.All(each => each.Single() is WatchFailedCall<JsonNode?>), "the member the new variant lacks fails for both");
            Throws<ChordError>(() => _ = second.Member("y").Value, "a read of the lacking member");
        }),
        Case("remote.shared-hold.state-gone-ends-every-binding-exactly-once-watches-and-calls-too-a-later-reference-holds-anew", async () =>
        {
            var (client, peer) = await H6RawProvider(); await using var closing = client;
            var first = client.Hold(Notes, H6Session(), S6Ctx); var second = client.Hold(Notes, H6Session(), S6Ctx);
            var hold = await H6BindNext(peer); await S6Live(second);
            var watches = new[] { await first.Member("count").WatchAsync(S6Ctx), await second.Member("count").WatchAsync(S6Ctx) };
            var call = H6Rejects(second.CallAsync("add", new JsonObject { ["text"] = "a" }, S6Ctx));
            await peer.Next<CallMessage>();
            peer.Send(new GoneMessage(hold, WireError.FromException(new ChordError("released", "app.notes(S1): ended", "ended"))));
            await S6Until(() => first.State.Value.Kind == "ended" && second.State.Value.Kind == "ended", "both ended");
            Sequence([WatchEnd.Ended, WatchEnd.Ended], await Task.WhenAll(watches.Select(watch => watch.Closed)), "their watches ended");
            var failed = (ChordError)await call; Equal("released", failed.Code, "the running call's error"); Equal("ended", failed.Reason, "with the reason");
            client.Hold(Notes, H6Session(), S6Ctx);
            Equal(1L, (await peer.Next<HoldMessage>()).Hold, "a later reference holds anew");
        }),
        Case("remote.shared-hold.state-the-hosts-close-ends-every-binding-of-a-shared-object", async () =>
        {
            var (client, peer) = await H6RawProvider();
            var third = client.Hold(Notes, H6Session("S2"), S6Ctx); var fourth = client.Hold(Notes, H6Session("S2"), S6Ctx);
            await peer.Next<HoldMessage>();
            await client.CloseAsync(S6Ctx);
            Sequence(["ended", "ended"], new[] { third.State.Value.Kind, fourth.State.Value.Kind }, "both ended with the host");
            Equal("released", third.State.Value.Error!.Code, "released");
        }),
        Case("remote.shared-hold.state-a-link-drop-one-re-hold-every-binding-live-again", async () =>
        {
            var (client, peer) = await H6RawProvider(); await using var closing = client;
            using var first = client.Hold(Notes, H6Session(), S6Ctx); using var second = client.Hold(Notes, H6Session(), S6Ctx);
            await H6BindNext(peer); await S6Live(second);
            peer.Dispose();
            await S6Until(() => first.State.Value.Kind == "loading" && second.State.Value.Kind == "loading", "both loading");
            var next = H6RawParent(client);
            await H6BindNext(next, "[{\"text\":\"b\"}]");
            await S6Live(first); await S6Live(second);
            Equal(0, await H6Taken<HoldMessage>(next, client), "one re-hold");
            Equal(1, H6Int(second), "the new snapshot reaches both");
        }),
        Case("remote.shared-hold.state-failure-and-loading-fan-out-to-every-binding-in-order-a-last-release-while-the-first-binding-is-pending", async () =>
        {
            var (client, peer) = await H6RawProvider(); await using var closing = client;
            var first = client.Hold(Notes, H6Session(), S6Ctx); var second = client.Hold(Notes, H6Session(), S6Ctx);
            var hold = (await peer.Next<HoldMessage>()).Hold;
            first.Release(); second.Release();
            await S6Until(() => peer.Count<ReleaseMessage>() == 1, "one release"); await S6Settled(client);
            Equal(1, peer.Count<ReleaseMessage>(), "the last release while the first binding was pending");
            using var third = client.Hold(Notes, H6Session(), S6Ctx); using var fourth = client.Hold(Notes, H6Session(), S6Ctx);
            var next = await H6BindNext(peer); Equal(hold + 1, next, "a new hold");
            await S6Live(fourth);
            // Upstream reads the order from watch callbacks; a native watch's listener is never invoked inline, so two watches' callbacks
            // have no order between them. The order the object tells its bindings is read where it is synchronous (Binding.Changed).
            var order = new List<string>(); var watched = new List<string>();
            foreach (var (name, reference) in new[] { ("third", third), ("fourth", fourth) })
            {
                var binding = (Binding)reference;
                client.Dispatcher.Invoke(() => binding.Changed += () => { lock (order) order.Add(name + " " + binding.RefState.Kind); });
                (await reference.State.WatchAsync(S6Ctx)).Start((call, _) =>
                { if (call is NativeWatchValueCall<RefState> value) lock (watched) watched.Add(name + " " + value.Value.Kind); return Task.CompletedTask; });
            }
            peer.Send(new FailedMessage(next, new("transient", WireError.FromException(new PluginError("down", "down"))), 60_000));
            await S6Until(() => { lock (watched) return watched.Count == 2; }, "both told the failure");
            peer.Send(new LoadingMessage(next));
            await S6Until(() => { lock (watched) return watched.Count == 4; }, "both told the loading");
            lock (order) Sequence(["third failed", "fourth failed", "third loading", "fourth loading"], order, "in the order they joined");
            lock (watched) foreach (var name in new[] { "third", "fourth" })
                Sequence([name + " failed", name + " loading"], watched.Where(entry => entry.StartsWith(name, StringComparison.Ordinal)), "each watch: failed, then loading");
        }),
        Case("remote.shared-hold.state-a-broken-object-that-recovers-stays-out-of-the-table-ending-it-leaves-the-newer-entry-alone", async () =>
        {
            var (client, peer) = await H6RawProvider(); await using var closing = client;
            var a = client.Hold(Notes, H6Session(), S6Ctx);
            var hold = (await peer.Next<HoldMessage>()).Hold;
            peer.Send(new FailedMessage(hold, new("broken", WireError.FromException(new PluginError("down", "down")))));
            await S6Until(() => a.State.Value.Kind == "failed", "the broken failure");
            using var c = client.Hold(Notes, H6Session(), S6Ctx);
            var newer = (await peer.Next<HoldMessage>()).Hold;
            a.Retry(); await peer.Next<RetryMessage>();
            peer.Send(new LoadingMessage(hold)); H6BindHold(peer, hold); H6BindHold(peer, newer);
            await S6Live(a); await S6Live(c);
            a.Release();
            Equal(hold, (await peer.Next<ReleaseMessage>()).Hold, "the recovered object's own release");
            // D joins C's object: the recovered one never came back, and its end took nothing of C's.
            using var d = client.Hold(Notes, H6Session(), S6Ctx);
            Equal("live", d.State.Value.Kind, "the newer entry is still shared"); Equal(0, await H6Taken<HoldMessage>(peer, client), "with no hold");
        }),
        Case("remote.shared-hold.state-a-refused-holds-retry-restarts-it-for-every-reference-sharing-it", async () =>
        {
            var (client, peer) = await H6RawProvider(); await using var closing = client;
            using var first = client.Hold(Notes, H6Session(), S6Ctx); using var second = client.Hold(Notes, H6Session(), S6Ctx);
            var hold = (await peer.Next<HoldMessage>()).Hold;
            var error = WireError.FromException(new ChordError("unavailable", "not served"));
            peer.Send(new FailedMessage(hold, new("waiting", error)));
            await S6Until(() => second.State.Value.Kind == "failed", "the refusal");
            var states = new[] { new List<string>(), new List<string>() };
            foreach (var (reference, seen) in new[] { (first, states[0]), (second, states[1]) })
                (await reference.State.WatchAsync(S6Ctx)).Start((call, _) =>
                {
                    if (call is NativeWatchValueCall<RefState> value) lock (seen) seen.Add(value.Value.Kind == "failed" ? "failed: " + value.Value.Cause!.Error.Message : value.Value.Kind);
                    return Task.CompletedTask;
                });
            first.Retry();
            Equal(hold, (await peer.Next<RetryMessage>()).Hold, "one retry on the shared hold");
            peer.Send(new LoadingMessage(hold)); peer.Send(new FailedMessage(hold, new("waiting", error)));
            await S6Until(() => states.All(seen => { lock (seen) return seen.Count == 2; }), "both told");
            foreach (var seen in states) lock (seen) Check(seen[0] == "loading" && seen[1].StartsWith("failed", StringComparison.Ordinal) && seen[1].Contains("not served", StringComparison.Ordinal), string.Join(", ", seen));
        }),

        // --- too_large/part and the retry alone ---
        Case("remote.shared-hold.too-large-fails-every-binding-broken-a-new-reference-holds-anew-a-retry-moves-only-its-binding-keeping-its-last-value", async () =>
        {
            var placed = Contract.Remote("app.placed", new()
            { Version = 1, Params = new(), HostAddress = _ => Server.Address(), Variants = new() { ["v"] = new() { State = { ["text"] = Str() } } } });
            void BindText(S6Peer to, long id, string text) => to.Send(new BoundMessage([new BoundEntry(id, 0, [new(0, new Part.Text(ChordJson.Stringify(JsonValue.Create(text))))], [])]));
            var (client, peer) = await H6RawProvider(new(64)); await using var closing = client;
            var a1 = client.Hold(placed, new(), S6Ctx); var a2 = client.Hold(placed, new(), S6Ctx);
            var hold = (await peer.Next<HoldMessage>()).Hold; BindText(peer, hold, "short"); await S6Live(a2);
            var watch = ChangesOf(await a1.Member("text").WatchAsync(S6Ctx));
            // A state part past the link's limit: the shared object fails, every binding with it.
            peer.Send(new StateMessage([new StateEntry(hold, [new StateMember(0, new Part.Text("[[\"r\",\"" + new string('x', 100) + "\"]]"))], [], [])]));
            Equal(hold, (await peer.Next<ReleaseMessage>()).Hold, "released at the provider");
            await S6Until(() => a1.State.Value.Kind == "failed" && a2.State.Value.Kind == "failed", "both failed");
            Check(new[] { a1, a2 }.All(reference => reference.State.Value is { Cause: { Kind: FailureKind.Broken, Error.Code: "too_large" } }), "broken, too_large, for both");
            // C makes a new hold, owning the new entry.
            var c = client.Hold(placed, new(), S6Ctx); var held = await peer.Next<HoldMessage>();
            // A1 alone moves to a private hold, its watch and last value its own.
            a1.Retry();
            var retried = await peer.Next<HoldMessage>(); Check(retried.Hold != held.Hold, "a hold of its own");
            Sequence(["loading", "failed", "\"short\""], new[] { a1.State.Value.Kind, a2.State.Value.Kind, a1.Member("text").Value!.ToJsonString() }, "only A1 moved, keeping its last value");
            BindText(peer, retried.Hold, "again"); await S6Live(a1);
            await S6Until(() => watch.Count == 2, "the private hold's value");
            Check(watch[0] is WatchFailedCall<JsonNode?> && watch[1] is WatchValueCall<JsonNode?> { Value: var again } && again!.GetValue<string>() == "again", "its watch: the failure, then the new value");
            // D joins C's entry, not A1's private object.
            BindText(peer, held.Hold, "c"); await S6Live(c);
            var d = client.Hold(placed, new(), S6Ctx);
            Sequence(["live", "\"c\""], new[] { d.State.Value.Kind, d.Member("text").Value!.ToJsonString() }, "D shares C's object");
            Equal(0, await H6Taken<HoldMessage>(peer, client), "with no hold");
            // The old object's last binding leaves: nothing more is sent for it.
            a2.Release(); c.Release();
            Equal(0, await H6Taken<ReleaseMessage>(peer, client), "nothing for the old object, nor for a non-final release");
            d.Release(); a1.Release();
            await S6Until(() => peer.All.OfType<ReleaseMessage>().Count() == 2, "the two last releases");
            Sequence(new[] { held.Hold, retried.Hold }.Order(), peer.All.OfType<ReleaseMessage>().Select(message => message.Hold).Order(), "C's entry and A1's private hold");
        }),
        Case("remote.shared-hold.too-large-a-retry-whose-placement-throws-fails-only-that-binding-the-old-objects-last-binding-retrying-ends-it", async () =>
        {
            var throws = false;
            var placed = Contract.Remote("app.placed-throwing", new()
            {
                Version = 1, Params = new(), HostAddress = _ => throws ? throw new InvalidOperationException("no address") : Server.Address(),
                Variants = new() { ["v"] = new() { State = { ["text"] = Str() } } },
            });
            void BindText(S6Peer to, long id, string text) => to.Send(new BoundMessage([new BoundEntry(id, 0, [new(0, new Part.Text(ChordJson.Stringify(JsonValue.Create(text))))], [])]));
            var (client, peer) = await H6RawProvider(new(64)); await using var closing = client;
            using var a1 = client.Hold(placed, new(), S6Ctx); using var a2 = client.Hold(placed, new(), S6Ctx);
            var hold = (await peer.Next<HoldMessage>()).Hold; BindText(peer, hold, new string('x', 100));
            await peer.Next<ReleaseMessage>(); await S6Until(() => a2.State.Value.Kind == "failed", "the too_large failure");
            throws = true;
            a1.Retry();
            Check(a1.State.Value is { Kind: "failed", Cause.Kind: FailureKind.Broken } && a1.State.Value.Cause!.Error.Message.Contains("no address", StringComparison.Ordinal),
                "the retrying binding fails broken with the placement's error: " + a1.State.Value.Cause?.Error.Message);
            Equal("too_large", a2.State.Value.Cause!.Error.Code, "the other is untouched");
            throws = false;
            // A2, the old object's last binding, retries: the old object ends, A2 holds on its own.
            a2.Retry();
            var again = (await peer.Next<HoldMessage>()).Hold; BindText(peer, again, "ok"); await S6Live(a2);
            a1.Retry();
            Equal(again + 1, (await peer.Next<HoldMessage>()).Hold, "A1's own hold");
        }),

        // --- seen members ---
        Case("remote.shared-hold.seen-a-join-to-a-live-object-with-a-failed-member-its-watch-rejects-with-the-error-no-last-value-until-a-value-arrives", async () =>
        {
            var (client, peer) = await H6RawProvider(); await using var closing = client;
            using var a = client.Hold(H6Panel, new(), S6Ctx);
            var hold = (await peer.Next<HoldMessage>()).Hold; H6BindPanel(peer, hold, "wide", 7, 1); await S6Live(a);
            var ax = a.Member("x"); Equal(7, ax.Value!.GetValue<int>(), "A read x while it was good");
            H6SetX(peer, hold, null); await S6Settled(client);
            using var b = client.Hold(H6Panel, new(), S6Ctx);
            Sequence(["opened 7", "rejected x threw"], new[] { await H6Opened(ax), await H6Opened(b.Member("x")) }, "A keeps its last value, the joiner has none");
            H6SetX(peer, hold, 8); await S6Settled(client);
            Equal("opened 8", await H6Opened(b.Member("x")), "the joiner sees the value that arrived");
        }),
        Case("remote.shared-hold.seen-a-member-missing-from-the-bound-variant-at-the-join-likewise-also-across-variants", async () =>
        {
            var (client, peer) = await H6RawProvider(); await using var closing = client;
            using var a = client.Hold(H6Panel, new(), S6Ctx);
            var hold = (await peer.Next<HoldMessage>()).Hold; H6BindPanel(peer, hold, "wide", 1, 2); await S6Live(a);
            var ay = a.Member("y"); Equal(2, ay.Value!.GetValue<int>(), "A read y while present");
            peer.Send(new LoadingMessage(hold)); H6BindPanel(peer, hold, "narrow", 3);
            await S6Until(() => a.Variant == "narrow", "the narrow binding");
            using var b = client.Hold(H6Panel, new(), S6Ctx); var by = b.Member("y");
            Equal("opened 2", await H6Opened(ay), "A's own last value");
            Check((await H6Opened(by)).StartsWith("rejected", StringComparison.Ordinal), "the joiner has none: " + await H6Opened(by));
            peer.Send(new LoadingMessage(hold)); H6BindPanel(peer, hold, "wide", 4, 5);
            await S6Until(() => a.Variant == "wide" && a.State.Value.Kind == "live", "wide again");
            peer.Send(new LoadingMessage(hold)); H6BindPanel(peer, hold, "narrow", 6);
            await S6Until(() => a.Variant == "narrow" && a.State.Value.Kind == "live", "narrow again");
            // Seen while present: its own last value now.
            Equal("opened 5", await H6Opened(by), "seen while present");
        }),
        Case("remote.shared-hold.seen-a-join-while-loading-sees-nothing-of-before-the-reconnects-snapshot-with-x-failed-rejects-its-watch-the-old-one-starts-at-7", async () =>
        {
            var (client, peer) = await H6RawProvider(); await using var closing = client;
            using var a = client.Hold(H6Panel, new(), S6Ctx);
            H6BindPanel(peer, (await peer.Next<HoldMessage>()).Hold, "wide", 7, 1); await S6Live(a);
            var ax = a.Member("x"); Equal(7, ax.Value!.GetValue<int>(), "A's value");
            peer.Dispose(); await S6Until(() => a.State.Value.Kind == "loading", "the drop");
            using var b = client.Hold(H6Panel, new(), S6Ctx);
            var next = H6RawParent(client);
            H6BindPanel(next, (await next.Next<HoldMessage>()).Hold, "wide", null, 2); await S6Live(b);
            Sequence(["opened 7", "rejected member threw"], new[] { await H6Opened(ax), await H6Opened(b.Member("x")) }, "the old one starts at 7, the joiner sees nothing of before");
        }),
        Case("remote.shared-hold.seen-failed-then-good-then-failed-again-before-the-joiners-first-read-the-joiner-keeps-that-value-as-its-own", async () =>
        {
            var (client, peer) = await H6RawProvider(); await using var closing = client;
            using var a = client.Hold(H6Panel, new(), S6Ctx);
            var hold = (await peer.Next<HoldMessage>()).Hold; H6BindPanel(peer, hold, "wide", 7, 1); await S6Live(a);
            H6SetX(peer, hold, null); await S6Settled(client);
            using var b = client.Hold(H6Panel, new(), S6Ctx);
            H6SetX(peer, hold, 9); H6SetX(peer, hold, null); await S6Settled(client);
            Equal("opened 9", await H6Opened(b.Member("x")), "the value that arrived after its join is its own last value");
        }),
        Case("remote.shared-hold.seen-unseen-members-and-own-last-values-survive-the-retry-alone-after-too-large-part", async () =>
        {
            var (client, peer) = await H6RawProvider(new(64)); await using var closing = client;
            using var a = client.Hold(H6Panel, new(), S6Ctx);
            var hold = (await peer.Next<HoldMessage>()).Hold; H6BindPanel(peer, hold, "wide", 7, 1); await S6Live(a);
            H6SetX(peer, hold, null); await S6Settled(client);
            using var b = client.Hold(H6Panel, new(), S6Ctx);
            Equal("opened 1", await H6Opened(b.Member("y")), "y is good at the join");
            peer.Send(new StateMessage([new StateEntry(hold, [new StateMember(WireNames(H6Panel, "wide").State["y"], new Part.Text("[[\"r\",\"" + new string('y', 100) + "\"]]"))], [], [])]));
            await peer.Next<ReleaseMessage>(); await S6Until(() => b.State.Value.Kind == "failed", "the too_large failure");
            b.Retry();
            var own = (await peer.Next<HoldMessage>()).Hold; H6BindPanel(peer, own, "wide", null, 2); await S6Live(b);
            // x: still unseen (failed at the join, no value since); y: its own.
            Equal("rejected member threw", await H6Opened(b.Member("x")), "x is still unseen");
            H6SetX(peer, own, 3); H6SetX(peer, own, null); await S6Settled(client);
            Equal("opened 3", await H6Opened(b.Member("x")), "seen once a value arrived on the private hold");
        }),

        // --- the accepted differences ---
        Case("remote.shared-hold.accepted-presence-and-activity-at-the-provider-one-entry-and-count-per-hold-a-non-final-release-changes-neither", async () =>
        {
            var probe = new S6MatchProbe();
            var (server, client) = await S6Linked(h => S6ServeMatch(h, probe)); await using var closeServer = server; await using var closeClient = client;
            int Listed() => server.References(S6Match).Value.Count;
            var a = await client.GetAsync(S6Match, new() { ["id"] = 1 }, S6Ctx); var b = await client.GetAsync(S6Match, new() { ["id"] = 1 }, S6Ctx);
            var other = await client.GetAsync(S6Match, new() { ["id"] = 2 }, S6Ctx);
            await S6Until(() => Listed() == 2 && server.Activity.Value.References == 2, "two holds at the provider");
            a.Release(); await S6Settled(server, client);
            Sequence([2, 2], new[] { Listed(), server.Activity.Value.References }, "a non-final release changes neither");
            b.Release(); other.Release();
            await S6Until(() => Listed() == 0 && server.Activity.Value.References == 0, "all released");
        }),
        Case("remote.shared-hold.accepted-a-commit-in-transit-a-joiner-first-reads-the-replica-then-gets-the-commit", async () =>
        {
            var (client, peer) = await H6RawProvider(); await using var closing = client;
            using var a = client.Hold(Notes, H6Session(), S6Ctx);
            var hold = await H6BindNext(peer, "[{\"text\":\"a\"}]"); await S6Live(a);
            IReference? b = null; var read = 0;
            // The commit is in transit while B joins: B joins in the turn before the state message is handled.
            S6Turn(client, () => { peer.Send(H6Count(hold, 2)); b = client.Hold(Notes, H6Session(), S6Ctx); read = H6Int(b); });
            Equal(1, read, "the joiner first reads the replica");
            await S6Until(() => H6Int(b!) == 2, "then gets the commit"); b!.Release();
        }),
        Case("remote.shared-hold.accepted-part-limits-apply-to-received-parts-a-value-grown-past-the-limit-by-deltas-does-not-fail-a-joiner", async () =>
        {
            var (client, peer) = await H6RawProvider(new(64)); await using var closing = client;
            using var a = client.Hold(Notes, H6Session(), S6Ctx);
            var hold = await H6BindNext(peer); await S6Live(a);
            var notes = WireNames(Notes, "editor").State["notes"];
            for (var n = 0; n < 6; n++)
                peer.Send(new StateMessage([new StateEntry(hold, [new StateMember(notes, new Part.Text("[[\"p\",[]," + n + ",0,[{\"text\":\"0123456789\"}]]]"))], [], [])]));
            await S6Until(() => a.Member("notes").Value!.ToJsonString().Length > 64, "the value grown past the limit");
            using var b = client.Hold(Notes, H6Session(), S6Ctx);
            Equal("live", b.State.Value.Kind, "the joiner is live");
            var calling = H6Rejects(b.CallAsync("add", new JsonObject { ["text"] = "x" }, S6Ctx));
            var call = await peer.Next<CallMessage>();
            peer.Send(new ResultMessage(call.Call, new Part.Text("\"" + new string('y', 100) + "\"")));
            Equal("too_large", H6Code(await calling), "an oversized call result fails only its call");
            Sequence(["live", "live"], new[] { a.State.Value.Kind, b.State.Value.Kind }, "both stay live");
        }),
        Case("remote.shared-hold.accepted-watch-callbacks-of-one-shared-commit-run-in-subscription-order-not-hold-order", async () =>
        {
            var (client, peer) = await H6RawProvider(); await using var closing = client;
            using var a = client.Hold(Notes, H6Session(), S6Ctx); using var b = client.Hold(Notes, H6Session(), S6Ctx);
            var hold = await H6BindNext(peer); await S6Live(b);
            var order = new List<string>();
            // B subscribes first, though A holds first.
            (await b.Member("count").WatchAsync(S6Ctx)).Start((_, _) => { lock (order) order.Add("b"); return Task.CompletedTask; });
            (await a.Member("count").WatchAsync(S6Ctx)).Start((_, _) => { lock (order) order.Add("a"); return Task.CompletedTask; });
            peer.Send(H6Count(hold, 4));
            await S6Until(() => { lock (order) return order.Count == 2; }, "both callbacks");
            lock (order) Sequence(["b", "a"], order, "subscription order");
        }),
        Case("remote.shared-hold.accepted-a-joining-getter-makes-no-hold-and-no-window-turn-it-resolves-with-the-replica-at-once", async () =>
        {
            var (client, peer) = await H6RawProvider(); await using var closing = client;
            var getting = client.GetAsync(Notes, H6Session(), S6Ctx);
            await H6BindNext(peer, "[{\"text\":\"a\"}]");
            using var a = await getting;
            using var b = await client.GetAsync(Notes, H6Session(), S6Ctx);
            Equal(1, H6Int(b), "the replica's value"); Equal(0, await H6Taken<HoldMessage>(peer, client), "no hold");
            Check(ReferenceEquals(a.Member("notes").Value, b.Member("notes").Value), "shared values are the same node");
        }),

        // --- events ---
        Case("remote.shared-hold.events-a-subscription-survives-its-references-retry-alone-after-too-large-part-the-private-holds-events-reach-it", async () =>
        {
            var (client, peer) = await H6RawProvider(new(64)); await using var closing = client;
            using var a1 = client.Hold(S6Match, new() { ["id"] = 1 }, S6Ctx); using var a2 = client.Hold(S6Match, new() { ["id"] = 1 }, S6Ctx);
            var hold = (await peer.Next<HoldMessage>()).Hold; E6BindMatch(peer, hold); await S6Live(a1);
            var heard = new List<string>();
            await a1.Event("paddle").OnAsync(S6Recorder(heard, "a1:"), S6Ctx); await a2.Event("paddle").OnAsync(S6Recorder(heard, "a2:"), S6Ctx);
            var score = ContractsInternal.VariantNamesOf(ContractsInternal.CurrentVariants(S6Match)["player"]).State.ToList().IndexOf("score");
            peer.Send(new StateMessage([new StateEntry(hold, [new StateMember(score, new Part.Text("[[\"r\",\"" + new string('x', 100) + "\"]]"))], [], [])]));
            await peer.Next<ReleaseMessage>(); await S6Until(() => a1.State.Value.Kind == "failed", "the too_large failure");
            a1.Retry();
            var own = (await peer.Next<HoldMessage>()).Hold; E6BindMatch(peer, own); await S6Live(a1);
            H6Paddle(peer, own, 1); H6Paddle(peer, hold, 2);
            await S6Until(() => S6Seen(heard).Length == 1, "the private hold's event"); await S6Settled(client);
            Sequence(["a1:1"], S6Seen(heard), "only the moved reference hears, and only its own hold"); Equal("failed", a2.State.Value.Kind, "the sibling stays failed");
        }),
        .. new[] { "gone", "the hosts close", "revocation" }.Select(terminal =>
            Case($"remote.shared-hold.events-{terminal.Replace(' ', '-')}-ends-every-reference-sharing-the-hold-once-its-watches-event-subscriptions-and-running-calls", async () =>
            {
                var (client, peer) = await H6RawProvider(revokedBy: principal => ChordJson.AsString(principal["kind"]) == "guest" ? [principal["session"]?.DeepClone()] : []);
                await using var closing = client;
                // Two references of a guest's scope (revocation ends a revoked principal's, never the host's own).
                var refs = new List<IReference>(); var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var scope = client.RootAsync<object?>(async ctx =>
                {
                    var both = new[] { ctx.GetAsync(S6Match, new() { ["id"] = 1 }), ctx.GetAsync(S6Match, new() { ["id"] = 1 }) };
                    foreach (var each in both) refs.Add(await each);
                    ready.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ctx.AbortSignal);
                    return null;
                }, S6Ctx, new() { Principal = new() { ["kind"] = "guest", ["session"] = "S1" } }).ContinueWith(_ => { });
                var hold = (await peer.Next<HoldMessage>()).Hold; E6BindMatch(peer, hold);
                await ready.Task;
                var states = new[] { new List<string>(), new List<string>() };
                for (var index = 0; index < 2; index++)
                {
                    var seen = states[index];
                    (await refs[index].State.WatchAsync(S6Ctx)).Start((call, _) =>
                    { if (call is NativeWatchValueCall<RefState> value) lock (seen) seen.Add(value.Value.Kind); return Task.CompletedTask; });
                }
                var watches = new[] { await refs[0].Member("score").WatchAsync(S6Ctx), await refs[1].Member("score").WatchAsync(S6Ctx) };
                var heard = new List<string>(); var paddle = refs[0].Event("paddle");
                for (var index = 0; index < 2; index++) await refs[index].Event("paddle").OnAsync(S6Recorder(heard, index + ":"), S6Ctx);
                var calls = refs.Select(reference => H6Rejects(reference.CallAsync("bump", null, S6Ctx))).ToArray();
                await peer.Next<CallMessage>(); await peer.Next<CallMessage>();
                H6Paddle(peer, hold, 1);
                await S6Until(() => S6Seen(heard).Length == 2, "the event reaching both");
                Sequence(["0:1", "1:1"], S6Seen(heard), "each once");
                if (terminal == "gone") peer.Send(new GoneMessage(hold, WireError.FromException(new ChordError("released", "ended", "ended"))));
                else if (terminal == "revocation") client.Revoke(JsonValue.Create("S1"), S6Ctx);
                else await client.CloseAsync(S6Ctx);
                await S6Until(() => refs.All(reference => reference.State.Value.Kind == "ended"), "both ended");
                var code = terminal == "revocation" ? "forbidden" : "released";
                // Ended once each: no other state on the way.
                foreach (var seen in states) lock (seen) Check(seen.All(kind => kind == "ended") && seen.Count <= 1, "no other state on the way: " + string.Join(",", seen));
                Sequence([WatchEnd.Ended, WatchEnd.Ended], await Task.WhenAll(watches.Select(watch => watch.Closed)), "their watches ended");
                // The host's close closes its links first: running calls see the link drop.
                var called = terminal == "the hosts close" ? "unavailable" : code;
                Sequence([called, called], (await Task.WhenAll(calls)).Select(H6Code), "their running calls");
                // Their subscriptions ended: a new one is refused, and nothing more reaches the old listeners.
                Equal(code, H6Code(await S6Rejection(paddle.OnAsync(_ => { }, S6Ctx))), "a new subscription is refused");
                Equal(code, Throws<ChordError>(() => refs[0].Event("paddle"), "the member of an ended reference").Code, "and the reference hands out no member any more");
                if (!peer.Closed && terminal != "the hosts close") { H6Paddle(peer, hold, 2); await S6Settled(client); }
                Sequence(["0:1", "1:1"], S6Seen(heard), "nothing more reaches the old listeners");
                await client.CloseAsync(S6Ctx); await scope;
            })),
        Case("remote.shared-hold.events-two-references-subscribed-to-one-member-each-get-every-event-once-stopping-one-leaves-the-others", async () =>
        {
            var (client, peer, hold, a) = await H6Match(); await using var closing = client;
            using var b = client.Hold(S6Match, new() { ["id"] = 1 }, S6Ctx);
            var seen = new List<string>();
            var stopA = await a.Event("paddle").OnAsync(S6Recorder(seen, "a"), S6Ctx);
            await b.Event("paddle").OnAsync(S6Recorder(seen, "b"), S6Ctx);
            H6Paddle(peer, hold, 1);
            await S6Until(() => S6Seen(seen).Length == 2, "both hear the first");
            stopA.Stop();
            H6Paddle(peer, hold, 2);
            await S6Until(() => S6Seen(seen).Length == 3, "the other hears the second"); await S6Settled(client);
            Sequence(["a1", "b1", "b2"], S6Seen(seen), "each once; stopping one leaves the other's");
        }),
        Case("remote.shared-hold.events-an-event-in-transit-when-a-reference-joins-reaches-it-one-queued-for-the-hosts-flush-before-it-bound-does-not", async () =>
        {
            var (client, peer, hold, a) = await H6Match(); await using var closing = client;
            var seen = new List<string>(); IReference? b = null;
            // In transit: sent before B joins, delivered after.
            S6Turn(client, () =>
            {
                H6Paddle(peer, hold, 1);
                b = client.Hold(S6Match, new() { ["id"] = 1 }, S6Ctx);
                b.Event("paddle").OnAsync(S6Recorder(seen, "b"), S6Ctx).GetAwaiter().GetResult();
            });
            await S6Until(() => S6Seen(seen).Length == 1, "the event in transit");
            Sequence(["b1"], S6Seen(seen), "it reaches the joiner");
            // Queued for the flush: C joins in A's listener of the first of two entries; it gets neither.
            IReference? c = null;
            await a.Event("paddle").OnAsync(e =>
            {
                if (e.Value!["y"]!.ToJsonString() != "2" || c is not null) return;
                c = client.Hold(S6Match, new() { ["id"] = 1 }, S6Ctx);
                _ = c.Event("paddle").OnAsync(S6Recorder(seen, "c"), S6Ctx);
            }, S6Ctx);
            H6Paddle(peer, hold, 2, 3);
            await S6Until(() => S6Seen(seen).Length == 3, "B hears both"); await S6Settled(client);
            H6Paddle(peer, hold, 4);
            await S6Until(() => S6Seen(seen).Length == 5, "the next event");
            Sequence(["b1", "b2", "b3", "b4", "c4"], S6Seen(seen), "C gets neither of the entries queued before it bound");
            b!.Release(); c!.Release();
        }),
        Case("remote.shared-hold.events-a-bindings-event-cutoff-stays-when-another-binding-joins", async () =>
        {
            var (client, peer, hold, a) = await H6Match(); await using var closing = client;
            var seen = new List<string>();
            await a.Event("paddle").OnAsync(S6Recorder(seen), S6Ctx);
            S6Turn(client, () => { H6Paddle(peer, hold, 1); client.Hold(S6Match, new() { ["id"] = 1 }, S6Ctx); });
            await S6Until(() => S6Seen(seen).Length == 1, "the event"); await S6Settled(client);
            Sequence(["1"], S6Seen(seen), "delivered once to the first binding");
        }),

        // --- cancellation ---
        Case("remote.shared-hold.cancellation-a-non-final-release-the-caller-gets-released-the-providers-handler-aborted-a-siblings-call-survives", async () =>
        {
            var probe = new W6WaitProbe();
            var (server, client) = await S6Linked(h => W6ServeWaits(h, probe)); await using var closeServer = server; await using var closeClient = client;
            var a = await client.GetAsync(W6Waits, new(), S6Ctx); var b = await client.GetAsync(W6Waits, new(), S6Ctx);
            var aCall = H6Rejects(a.CallAsync("wait", null, S6Ctx)); var bCall = H6Rejects(b.CallAsync("wait", null, S6Ctx));
            await S6Until(() => probe.Waits == 2, "both calls running");
            a.Release();
            Equal("released", H6Code(await aCall), "the released reference's call");
            await S6Until(() => probe.Aborted == 1, "its handler aborted"); await S6Settled(server, client);
            Equal(1, probe.Aborted, "the sibling's call survives"); Check(!bCall.IsCompleted, "and still runs");
            b.Release();
            Equal("released", H6Code(await bCall), "the last release");
            await S6Until(() => probe.Aborted == 2, "the last handler aborted");
        }),
        Case("remote.shared-hold.cancellation-through-a-relay-with-an-upstream-window-of-1-the-worker-sees-a1-and-b1-never-a2", async () =>
        {
            var seen = new List<string>();
            var (tab, server, workers, _) = await E6Relayed(workerCallWindow: 1, worker: h => H6ServeWaiter(h, H6Waiter, seen));
            await using var closeTab = tab; await using var closeServer = server;
            var a = await tab.GetAsync(H6Waiter, H6Session(), S6Ctx); var b = await tab.GetAsync(H6Waiter, H6Session(), S6Ctx);
            var first = H6Rejects(a.CallAsync("wait", JsonValue.Create("A1"), S6Ctx));
            await S6Until(() => H6Tags(seen).Length == 1, "A1 running");
            var second = H6Rejects(a.CallAsync("wait", JsonValue.Create("A2"), S6Ctx)); await S6Settled(tab, server);
            var sibling = H6Rejects(b.CallAsync("wait", JsonValue.Create("B1"), S6Ctx)); await S6Settled(tab, server);
            Sequence(["A1"], H6Tags(seen), "A2 and B1 wait at the relay");
            a.Release();
            await first; await second;
            await S6Until(() => H6Tags(seen).Length == 2, "the sibling's call running"); await S6Settled(tab, server);
            Sequence(["A1", "B1"], H6Tags(seen), "the worker never sees A2");
            b.Release();
            Equal("released", H6Code(await sibling), "the sibling's call at its release");
            foreach (var worker in workers.ToArray()) await worker.CloseAsync(S6Ctx);
        }),
        Case("remote.shared-hold.cancellation-through-two-relays-with-upstream-windows-2-and-1-the-provider-never-sees-the-released-owners-queued-call", async () =>
        {
            var deep = new HostKind("deep");
            var deepWaiter = Contract.Remote("app.deep-waiter", new()
            {
                Version = 1, Params = new(), HostAddress = _ => deep.Address(),
                Variants = new() { ["v"] = new() { State = { ["n"] = Int() }, Methods = { ["wait"] = Method(Str()) } } },
            });
            var seen = new List<string>();
            var provider = S6Host(deep.Address(), callWindow: 1);
            await provider.LoadAsync([new("@test/deep", Facet.Define((h, _) => H6ServeWaiter(h, deepWaiter, seen)))], S6Ctx);
            // A relay host routing the deep kind to its upstream, announcing callWindow on the links it accepts.
            async Task<ChordHost> Relay(ChordHost upstream, int callWindow)
            {
                var host = S6Host(Server.Address(), callWindow: callWindow);
                host.Route(deep, new()
                {
                    Authorize = (_, _) => true,
                    Resolve = (_, _) =>
                    {
                        var pair = MemoryLinkPair.Create();
                        // The upstream takes the relay as its parent: a child link carries no origins.
                        upstream.Connect(pair.Second, new() { Principal = S6ServerPrincipal }, S6Ctx);
                        return Task.FromResult(new ResolvedLink(pair.First, new() { Principal = S6ServerPrincipal }));
                    },
                }, S6Ctx);
                await host.LoadAsync([], S6Ctx);
                return host;
            }
            var inner = await Relay(provider, 2); var outer = await Relay(inner, 2);
            var tab = S6Host(principal: new() { ["kind"] = "owner" }); await tab.LoadAsync([], S6Ctx);
            S6Link(outer, tab, new() { ["kind"] = "owner" });
            var a = await tab.GetAsync(deepWaiter, new(), S6Ctx); var b = await tab.GetAsync(deepWaiter, new(), S6Ctx);
            // The tab's window (2) sends A1 and A2, the outer relay forwards both (2), the inner one runs A1 (1) and queues A2; B1 and B2
            // wait in the tab.
            var calls = new List<Task<Exception>> { H6Rejects(a.CallAsync("wait", JsonValue.Create("A1"), S6Ctx)) };
            await S6Until(() => H6Tags(seen).Length == 1, "A1 running");
            foreach (var (reference, tag) in new[] { (a, "A2"), (b, "B1"), (b, "B2") })
            { calls.Add(H6Rejects(reference.CallAsync("wait", JsonValue.Create(tag), S6Ctx))); await S6Settled(tab, outer, inner, provider); }
            Sequence(["A1"], H6Tags(seen), "one call at the provider");
            // abort(A2) first drops A2 at the inner relay; abort(A1) then frees the provider's slot for B1. Oldest first, that slot would
            // send A2.
            a.Release();
            await S6Until(() => H6Tags(seen).Length == 2, "the next call at the provider"); await S6Settled(tab, outer, inner, provider);
            Sequence(["A1", "B1"], H6Tags(seen), "never A2");
            b.Release();
            Sequence(["released", "released", "released", "released"], (await Task.WhenAll(calls)).Select(H6Code), "every call ends released");
            await S6Settled(tab, outer, inner, provider);
            Sequence(["A1", "B1"], H6Tags(seen), "nothing more ran");
            // Downstream first: a relay closing before its consumer would resolve its route again for it.
            foreach (var host in new[] { tab, outer, inner, provider }) await host.CloseAsync(S6Ctx);
        }),

        // --- admission (accepted difference 2), cost, telemetry ---
        Case("remote.shared-hold.admission-a-relays-authorize-turned-false-after-a-is-held-b-joins-with-no-hold-upstream-and-no-authorization", async () =>
        {
            var allowed = true; var asked = 0; var probe = new W6WaitProbe();
            var relay = S6Host(Server.Address()); await using var closeRelay = relay; var workers = new List<ChordHost>();
            relay.Route(Session, new()
            {
                Authorize = (_, _) => { Interlocked.Increment(ref asked); return allowed; },
                Resolve = async (address, _) =>
                {
                    var made = S6Host(address, principal: new() { ["kind"] = "worker" });
                    await made.LoadAsync([new("@test/worker", Facet.Define((h, _) => H6ServeWaiter(h, H6Waiter, [])))], S6Ctx);
                    var pair = MemoryLinkPair.Create(); made.Connect(pair.Second, new() { Principal = S6ServerPrincipal }, S6Ctx);
                    lock (workers) workers.Add(made);
                    return new ResolvedLink(pair.First, new() { Principal = new() { ["kind"] = "worker" } });
                },
            }, S6Ctx);
            await relay.LoadAsync([], S6Ctx);
            var tab = S6Host(principal: new() { ["kind"] = "owner" }); await using var closeTab = tab; await tab.LoadAsync([], S6Ctx);
            S6Link(relay, tab, new() { ["kind"] = "owner" });
            using var a = await tab.GetAsync(H6Waiter, H6Session(), S6Ctx);
            await S6Settled(tab, relay);
            var holds = relay.Activity.Value.References; allowed = false; var before = asked;
            using var b = await tab.GetAsync(H6Waiter, H6Session(), S6Ctx);
            await S6Settled(tab, relay);
            Equal(0, asked - before, "no authorization for a join"); Equal("live", b.State.Value.Kind, "B joined live");
            Equal(holds, relay.Activity.Value.References, "no hold upstream"); Equal("live", a.State.Value.Kind, "A stays live");
            GC.KeepAlive(probe);
            foreach (var worker in workers.ToArray()) await worker.CloseAsync(S6Ctx);
        }),
        Case("remote.shared-hold.cost-two-references-one-hold-one-encode-per-commit", async () =>
        {
            // chord.link.encodes is counted through the link's context: the server accepts its link under a metered one.
            var (telemetry, metered, _) = await Metered(); var probe = new S6MatchProbe();
            await using var server = S6Host(Server.Address()); await using var client = S6Host();
            await server.LoadAsync([new("@test/served", Facet.Define((h, _) => S6ServeMatch(h, probe)))], S6Ctx); await client.LoadAsync([], S6Ctx);
            var pair = MemoryLinkPair.Create();
            server.Accept(pair.First, new() { Principal = S6User }, metered); client.Connect(pair.Second, new() { Principal = S6ServerPrincipal }, S6Ctx);
            using var a = await client.GetAsync(S6Match, new() { ["id"] = 1 }, S6Ctx); using var b = await client.GetAsync(S6Match, new() { ["id"] = 1 }, S6Ctx);
            await S6Settled(server, client);
            var before = S6Count(telemetry, "chord.link.encodes");
            probe.Score.Set(1); await S6Until(() => H6Int(a, "score") == 1, "the first commit");
            probe.Score.Set(2); await S6Until(() => H6Int(b, "score") == 2, "the second commit");
            await S6Settled(server, client);
            Equal(2.0, S6Count(telemetry, "chord.link.encodes") - before, "one encode per commit");
            Sequence([2, 2], new[] { H6Int(a, "score"), H6Int(b, "score") }, "both see both commits");
        }),
        Case("remote.shared-hold.telemetry-the-shared-hold-is-a-root-linked-to-its-first-getter-a-joining-getter-links-to-it-while-open", async () =>
        {
            var telemetry = new InMemoryTelemetryContext();
            var (client, peer) = await H6RawProvider(telemetry: telemetry); await using var closing = client;
            await telemetry.StartSpanAsync("first", span => { client.Hold(Notes, H6Session(), ChordContext.WithTelemetry(span, S6Ctx)); return Task.FromResult(true); });
            await telemetry.StartSpanAsync("second", span => { client.Hold(Notes, H6Session(), ChordContext.WithTelemetry(span, S6Ctx)); return Task.FromResult(true); });
            client.Hold(Notes, H6Session("S2"), S6Ctx);
            await telemetry.StartSpanAsync("third", span => { client.Hold(Notes, H6Session("S2"), ChordContext.WithTelemetry(span, S6Ctx)); return Task.FromResult(true); });
            await H6BindNext(peer); await H6BindNext(peer);
            await S6Until(() => telemetry.GetSpans().Count(span => span.Name == "chord.hold" && span.Settled) == 2, "both holds bound");
            var spans = telemetry.GetSpans();
            RecordedTelemetrySpan Named(string name) => spans.First(span => span.Name == name);
            var holds = spans.Where(span => span.Name == "chord.hold").ToArray();
            static string Key(IReadOnlyDictionary<string, string> carrier) => string.Join(";", carrier.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value));
            Check(holds.All(span => span.ParentId is null), "both holds are roots");
            Sequence([Key(Named("first").Context)], holds[0].Links.Select(link => Key(link.Context)), "the first hold links to its first getter");
            Equal(0, holds[1].Links.Count, "an untraced first getter gives a root without a link");
            Sequence([Key(holds[0].Context)], Named("second").Links.Select(link => Key(link.Context)), "a joining getter links to the hold");
            Sequence([Key(holds[1].Context)], Named("third").Links.Select(link => Key(link.Context)), "also to one whose first getter was untraced");
        }),
        Case("remote.shared-hold.telemetry-a-re-hold-is-a-fresh-root-linked-to-the-first-holds-the-noop-adapter-records-none", async () =>
        {
            var telemetry = new InMemoryTelemetryContext();
            var (client, peer) = await H6RawProvider(telemetry: telemetry); await using var closing = client;
            using var held = client.Hold(Notes, H6Session(), S6Ctx);
            var firstHold = await peer.Next<HoldMessage>(); H6BindHold(peer, firstHold.Hold); await S6Live(held);
            Check(firstHold.Trace is not null, "the first hold carries its span's carrier");
            peer.Dispose(); await S6Until(() => held.State.Value.Kind == "loading", "the drop");
            await H6BindNext(H6RawParent(client)); await S6Live(held);
            await S6Until(() => telemetry.GetSpans().Count(span => span.Name == "chord.hold") == 2, "the re-hold's span");
            var holds = telemetry.GetSpans().Where(span => span.Name == "chord.hold").ToArray();
            static string Key(IReadOnlyDictionary<string, string> carrier) => string.Join(";", carrier.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value));
            Check(holds[0].ParentId is null && holds[1].ParentId is null, "both are roots");
            Sequence([Key(holds[0].Context)], holds[1].Links.Select(link => Key(link.Context)), "the re-hold links to the first hold");
            // Without an adapter (NOOP) no hold span is made, whatever the getter's context: its hold carries no carrier.
            var untraced = await H6RawProvider(); await using var closeUntraced = untraced.Client;
            await telemetry.StartSpanAsync("getter", span => { untraced.Client.Hold(Notes, H6Session(), ChordContext.WithTelemetry(span, S6Ctx)); return Task.FromResult(true); });
            Equal(null, (await untraced.Peer.Next<HoldMessage>()).Trace, "no carrier without an adapter");
        }),

        // --- what upstream asserts by garbage collection, read directly ---
        Case("remote.shared-hold.gc-no-remote-object-stays-after-its-last-release-while-the-path-lives-a-route-paths-table-goes-with-it", async () =>
        {
            var (client, peer) = await H6RawProvider(); await using var closing = client;
            RemotePath Parent() => client.Dispatcher.Invoke(() => client.PathOf(new WireAddress("server", new JsonObject()), new Caller(S6User, client.Incarnation), null)!);
            var a = client.Hold(Notes, H6Session(), S6Ctx); var b = client.Hold(Notes, H6Session(), S6Ctx);
            await H6BindNext(peer); await S6Live(b);
            Equal(1, client.Dispatcher.Invoke(() => Parent().Shared!.Count), "one shared object for both");
            Equal(1, client.Dispatcher.Invoke(() => Parent().Holders.Length), "one holder on the path");
            a.Release(); b.Release();
            await peer.Next<ReleaseMessage>();
            Equal(0, client.Dispatcher.Invoke(() => Parent().Shared!.Count), "the table is empty after the last release");
            Equal(0, client.Dispatcher.Invoke(() => Parent().Holders.Length), "and the path holds nothing");
        }),
        Case("remote.shared-hold.gc-a-released-joiner-with-queued-calls-is-kept-by-no-queue-or-call-record", async () =>
        {
            var (client, peer) = await H6RawProvider(calls: 1); await using var closing = client;
            using var b = client.Hold(H6Panel, new(), S6Ctx);
            var hold = (await peer.Next<HoldMessage>()).Hold; H6BindPanel(peer, hold, "wide", 7, 1); await S6Live(b);
            // B's call takes the window's one slot: A's calls wait in its queue.
            var sibling = b.CallAsync("poke", JsonValue.Create("B1"), S6Ctx);
            var first = await peer.Next<CallMessage>();
            var a = client.Hold(H6Panel, new(), S6Ctx);
            var queued = new[] { H6Rejects(a.CallAsync("poke", JsonValue.Create("A1"), S6Ctx)), H6Rejects(a.CallAsync("poke", JsonValue.Create("A2"), S6Ctx)) };
            await S6Settled(client);
            a.Release();
            Sequence(["released", "released"], (await Task.WhenAll(queued)).Select(H6Code), "its queued calls reject released");
            peer.Send(new ResultMessage(first.Call, new Part.Text("1")));
            Equal(1, ((JsonNode)(await sibling)!).GetValue<int>(), "the sibling's call is answered");
            Equal("live", b.State.Value.Kind, "the sibling stays live");
            Equal(0, await H6Taken<CallMessage>(peer, client), "the released reference's queued calls never went");
            Equal(0, peer.Count<AbortMessage>(), "and nothing was aborted: they were never sent");
        }),

        // --- consumer.test.ts: cancelling one reference's calls on a shared hold ---
        Case("remote.consumer.shared-a-finite-window-the-released-ones-queued-call-leaves-unsent-its-sent-one-is-aborted-the-others-queued-call-goes-out", async () =>
        {
            var (client, peer) = await H6RawProvider(calls: 1); await using var closing = client;
            var a = client.Hold(Notes, H6Session(), S6Ctx); using var b = client.Hold(Notes, H6Session(), S6Ctx);
            await H6BindNext(peer); await S6Live(b);
            var a1 = H6Rejects(a.CallAsync("add", new JsonObject { ["text"] = "A1" }, S6Ctx)); var sent = await peer.Next<CallMessage>();
            var a2 = H6Rejects(a.CallAsync("add", new JsonObject { ["text"] = "A2" }, S6Ctx)); await S6Settled(client);
            var sibling = b.CallAsync("add", new JsonObject { ["text"] = "B1" }, S6Ctx); await S6Settled(client);
            Equal(0, peer.All.OfType<CallMessage>().Count(), "the window of 1 holds A2 and B1 back");
            a.Release();
            Sequence(["released", "released"], new[] { H6Code(await a1), H6Code(await a2) }, "the released reference's calls");
            var next = await peer.Next<CallMessage>();
            Equal("{\"text\":\"B1\"}", Parts.Parse(next.Args!)!.ToJsonString(), "the other's queued call goes out; A2 never does");
            var aborts = peer.Log.OfType<AbortMessage>().ToArray();
            Equal(1, aborts.Length, "one abort"); Equal(sent.Call, aborts[0].Call, "of the sent call");
            Equal(0, peer.Count<ReleaseMessage>(), "no release: the hold is still shared");
            peer.Send(new ResultMessage(next.Call, new Part.Text("1"))); await sibling;
        }),
        Case("remote.consumer.shared-several-sent-calls-are-aborted-newest-first-a-reply-crossing-the-release-is-ignored-the-last-release-sends-release", async () =>
        {
            var (client, peer) = await H6RawProvider(calls: 8); await using var closing = client;
            var a = client.Hold(Notes, H6Session(), S6Ctx); var b = client.Hold(Notes, H6Session(), S6Ctx);
            await H6BindNext(peer); await S6Live(b);
            var calls = new List<Task<Exception>>(); var ids = new List<long>();
            foreach (var text in new[] { "A1", "A2", "A3" })
            { calls.Add(H6Rejects(a.CallAsync("add", new JsonObject { ["text"] = text }, S6Ctx))); ids.Add((await peer.Next<CallMessage>()).Call); }
            a.Release();
            await Task.WhenAll(calls);
            await S6Until(() => peer.Count<AbortMessage>() == 3, "three aborts");
            Sequence(ids.AsEnumerable().Reverse(), peer.Log.OfType<AbortMessage>().Select(abort => abort.Call), "newest first");
            // A reply crossing the release names a call this side gave up: ignored.
            peer.Send(new ResultMessage(ids[0], new Part.Text("1")));
            await S6Settled(client); Check(!peer.Closed, "the crossing reply is ignored");
            Equal(0, peer.Count<ReleaseMessage>(), "no release while the hold is shared");
            b.Release();
            await peer.Next<ReleaseMessage>();
        }),

        // --- path.test.ts, replica.test.ts ---
        Case("remote.path.counts-a-join-to-a-held-holder-as-a-request-without-a-link-none-with-one", () =>
        {
            var events = new List<string>(); var path = new RemotePath((change, _) => events.Add(change));
            var holder = new H6Upstream();
            path.Attach(holder); path.Requested(holder);
            Sequence(["attached", "attached"], events, "a join without a link is a new request");
            return Task.CompletedTask;
        }),
        Case("remote.path.tells-whether-its-route-suspended-it-a-path-without-a-route-never-is", () =>
        {
            var suspended = false; var routed = new RemotePath((_, _) => { }, () => suspended);
            Check(!routed.Suspended, "not suspended"); suspended = true; Check(routed.Suspended, "suspended once its route says so");
            Check(!new RemotePath((_, _) => { }).Suspended, "a path without a route never is");
            return Task.CompletedTask;
        }),
        Case("remote.replica.counts-the-values-it-takes-a-snapshots-value-each-adoption-and-recovery-failures-and-vanishings-count-none", () =>
        {
            var batch = new Batch(_ => { }, null);
            var replica = new Replica(batch, new MemberSnapshot(JsonValue.Create(1)));
            Equal(1L, replica.Generation, "a snapshot's value");
            Equal(0L, new Replica(batch, new MemberSnapshot(null, new InvalidOperationException("failed"))).Generation, "a failed snapshot counts none");
            batch.Commit(() => { var staged = replica.Prepare(JsonNode.Parse("[[\"r\",2]]")); replica.Adopt(staged.Value, staged.Ops, staged.Decoder, false); }, S6Ctx);
            Equal(2L, replica.Generation, "an adoption");
            batch.Commit(() => replica.Fail(new InvalidOperationException("failed")), S6Ctx);
            Equal(2L, replica.Generation, "a failure counts none");
            batch.Commit(() => replica.Reset(new MemberSnapshot(JsonValue.Create(3))), S6Ctx);
            Equal(3L, replica.Generation, "a recovery");
            return Task.CompletedTask;
        }),
        Case("remote.replica.forks-with-its-state-last-good-value-and-count-on-another-batch-with-a-fresh-decoder", () =>
        {
            var batch = new Batch(_ => { }, null); var other = new Batch(_ => { }, null);
            var replica = new Replica(batch, new MemberSnapshot(JsonValue.Create(7)));
            batch.Commit(() => replica.Fail(new InvalidOperationException("x threw")), S6Ctx);
            var fork = replica.Fork(other);
            Equal(replica.Generation, fork.Generation, "its count");
            var start = fork.Start();
            Check(start is { Failed: true, HasLast: true } && start.Value!.GetValue<int>() == 7 && start.Error!.Message == "x threw", "its state and last good value");
            // A fresh decoder: the fork takes a root replacement as its next snapshot does, independent of its original.
            other.Commit(() => fork.Reset(new MemberSnapshot(JsonValue.Create(8))), S6Ctx);
            Equal(8, fork.Value!.GetValue<int>(), "the fork's own value"); Check(replica.Start().Failed, "the original is untouched");
            return Task.CompletedTask;
        }),
    ];

    private sealed class H6Upstream : IUpstream
    {
        public void Attach(HostLink link) { }
        public void Dropped(ChordError error) { }
        public void Unreachable(ChordError error) { }
    }
}
