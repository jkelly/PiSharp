// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/test/host/events.test.ts, packages/chord/test/host/reference-events.test.ts,
// packages/chord/test/host/members.test.ts (event members), packages/chord/test/host/staging.test.ts (the inert event source).
// Ported by reading; the upstream "turn" is one dispatcher action here (S6Turn) and `await flushed()` is S6Flushed (see
// Sync160RemoteSupport.cs). Cases named 160-* are authored for the native port.
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Host;
using PiSharp.Chord.Link;
using PiSharp.Chord.State;
using PiSharp.Telemetry;
using static PiSharp.Chord.Contracts.Schemas;
using static ChordSupport;

internal static partial class Program
{
    private static readonly ChordContext S6Ctx = ChordContext.Background;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] S6EventGarbage(ChordHost host, S6TickerProbe probe)
    {
        var ticker = host.GetAsync(S6Ticker, new(), S6Ctx).GetAwaiter().GetResult();
        var token = new object();
        Action<ChordEvent> listener = _ => GC.KeepAlive(token);
        var subscription = ticker.Event("ticks").OnAsync(listener, S6Ctx).GetAwaiter().GetResult();
        var other = host.GetAsync(S6Ticker, new(), S6Ctx).GetAwaiter().GetResult();
        var kept = other.Event("ticks").OnAsync(_ => { }, S6Ctx).GetAwaiter().GetResult();
        probe.Source.Emit(S6Y(1));
        var value = S6Y(2);
        probe.Source.Emit(value);
        S6Flushed(host).GetAwaiter().GetResult();
        subscription.Stop(); other.Release();
        S6Flushed(host).GetAwaiter().GetResult();
        GC.KeepAlive(ticker);
        return [new(listener), new(subscription), new(kept), new(value), new(other)];
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference S6StoppedSubscription(ChordHost host, IReference ticker, ChordContext context)
    {
        var subscription = ticker.Event("ticks").OnAsync(_ => { }, context).GetAwaiter().GetResult();
        Check(subscription is Subscription, "a reference's subscription is the host's");
        subscription.Stop();
        S6Flushed(host).GetAwaiter().GetResult();
        return new(subscription);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (IEventSubscription[] Kept, WeakReference[] Refs) S6KeptSubscriptions(ChordHost host, S6TickerProbe probe)
    {
        var named = host.GetAsync(S6NamedTicker, new() { ["name"] = "gc" }, S6Ctx).GetAwaiter().GetResult();
        var source = probe.Named[^1];
        var own = source.OnAsync(_ => { }, S6Ctx).GetAwaiter().GetResult();
        own.Stop();
        using var cancel = new CancellationTokenSource();
        var fromReference = named.Event("ticks").OnAsync(_ => { }, ChordContext.WithAbortSignal(cancel.Token, S6Ctx)).GetAwaiter().GetResult();
        fromReference.Stop();
        named.Release();
        S6Flushed(host).GetAwaiter().GetResult();
        lock (probe) { probe.Named.Clear(); probe.NamedSelves.Clear(); }
        return ([own, fromReference], [new(source), new(named)]);
    }

    private static IEnumerable<(string, Func<Task>)> HostSync160EventsCases() =>
    [
        // --- the event flush ---
        Case("host.events.delivers-a-turns-emits-in-one-flush-after-the-turn-in-order-stamped-with-the-emits-time", async () =>
        {
            var (host, probe, _) = await S6Ticking(); await using var closing = host;
            using var ticker = await host.GetAsync(S6Ticker, new(), S6Ctx);
            var seen = new List<ChordEvent>();
            await ticker.Event("ticks").OnAsync(e => { lock (seen) seen.Add(e); }, S6Ctx);
            var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            S6Turn(host, () =>
            {
                probe.Source.Emit(S6Y(1)); probe.Source.Emit(S6Y(2));
                Equal(0, seen.Count, "nothing is delivered inside the turn that emitted");
            });
            await S6Flushed(host);
            Sequence([1.0, 2.0], seen.Select(e => e.Value!["y"]!.GetValue<double>()), "both, in order");
            Check(seen.All(e => e.At >= before), "stamped with the emit's Unix milliseconds");
        }),
        Case("host.events.puts-what-a-listener-emits-into-the-next-flush", async () =>
        {
            var (host, probe, _) = await S6Ticking(); await using var closing = host;
            using var ticker = await host.GetAsync(S6Ticker, new(), S6Ctx);
            var seen = new List<double>();
            await ticker.Event("ticks").OnAsync(e =>
            {
                var y = e.Value!["y"]!.GetValue<double>(); seen.Add(y);
                if (y < 3) probe.Source.Emit(S6Y(y + 1));
            }, S6Ctx);
            probe.Source.Emit(S6Y(1));
            await S6Flushed(host); await S6Flushed(host);
            Sequence([1.0, 2.0, 3.0], seen, "one flush per emitting listener call");
        }),
        Case("host.events.calls-listeners-in-entry-order-then-subscription-order-across-references", async () =>
        {
            var (host, probe, _) = await S6Ticking(); await using var closing = host;
            using var one = await host.GetAsync(S6Ticker, new(), S6Ctx); using var two = await host.GetAsync(S6Ticker, new(), S6Ctx);
            var seen = new List<string>();
            await two.Event("ticks").OnAsync(S6Recorder(seen, "a"), S6Ctx);
            await one.Event("ticks").OnAsync(S6Recorder(seen, "b"), S6Ctx);
            await two.Event("ticks").OnAsync(S6Recorder(seen, "c"), S6Ctx);
            S6Turn(host, () => { probe.Source.Emit(S6Y(1)); probe.Source.Emit(S6Y(2)); });
            await S6Flushed(host);
            Sequence(["a1", "b1", "c1", "a2", "b2", "c2"], S6Seen(seen), "entry order, then subscription order");
        }),
        Case("host.events.lets-a-listener-stop-release-subscribe-start-a-transaction-and-emit-a-call-due-when-it-stopped-is-skipped", async () =>
        {
            var (host, probe, _) = await S6Ticking(); await using var closing = host;
            using var one = await host.GetAsync(S6Ticker, new(), S6Ctx); var two = await host.GetAsync(S6Ticker, new(), S6Ctx);
            var seen = new List<string>(); Task<IEventSubscription>? late = null; IEventSubscription? second = null;
            var first = await one.Event("ticks").OnAsync(e =>
            {
                lock (seen) seen.Add("first" + e.Value!["y"]);
                second!.Stop(); two.Release();
                late = one.Event("ticks").OnAsync(S6Recorder(seen, "late"), S6Ctx);
                _ = probe.Selves[^1].TxAsync<object?>(_ => Task.FromResult<object?>(null), S6Ctx);
            }, S6Ctx);
            second = await one.Event("ticks").OnAsync(S6Recorder(seen, "second"), S6Ctx);
            await two.Event("ticks").OnAsync(S6Recorder(seen, "released"), S6Ctx);
            probe.Source.Emit(S6Y(1));
            await S6Flushed(host);
            first.Stop(); await late!;
            probe.Source.Emit(S6Y(2));
            await S6Flushed(host);
            Sequence(["first1", "late2"], S6Seen(seen), "the stopped and the released one get nothing, the late one the next emit");
        }),
        Case("host.events.reports-a-throwing-listener-and-ends-only-its-subscription", async () =>
        {
            var (host, probe, errors) = await S6Ticking(); await using var closing = host;
            using var ticker = await host.GetAsync(S6Ticker, new(), S6Ctx);
            var seen = new List<string>();
            await ticker.Event("ticks").OnAsync(_ => throw new InvalidOperationException("listener"), S6Ctx);
            await ticker.Event("ticks").OnAsync(S6Recorder(seen), S6Ctx);
            S6Turn(host, () => { probe.Source.Emit(S6Y(1)); probe.Source.Emit(S6Y(2)); });
            await S6Flushed(host);
            Sequence(["1", "2"], S6Seen(seen), "the other listener gets both");
            HostError[] reported; lock (errors) reported = [.. errors];
            Equal(1, reported.Length, "reported once: its subscription ended at the first throw");
            Equal("listener_error", reported[0].Kind, "the report's kind"); Equal("listener", reported[0].Error!.Message, "the listener's error");
        }),
        Case("host.events.throws-released-for-an-emit-after-its-attempts-seal", async () =>
        {
            var (host, probe, _) = await S6Ticking(); await using var closing = host;
            var reference = await host.GetAsync(S6NamedTicker, new() { ["name"] = "a" }, S6Ctx);
            var source = probe.Named[^1];
            reference.Release();
            await S6Until(() => host.Dispatcher.Invoke(() => host.Objects.Count == 1 && host.EndingObjects.Count == 0), "the named ticker ending");
            Equal("released", Throws<ChordError>(() => source.Emit(S6Y(1)), "an emit after the seal").Code, "released");
        }),
        Case("host.events.delivers-an-emit-made-inside-a-transaction-that-fails-emit-is-no-transaction-work", async () =>
        {
            var (host, probe, _) = await S6Ticking(); await using var closing = host;
            using var ticker = await host.GetAsync(S6Ticker, new(), S6Ctx);
            var seen = new List<string>();
            await ticker.Event("ticks").OnAsync(S6Recorder(seen), S6Ctx);
            var error = await S6Rejection(ticker.CallAsync("failing", S6Y(7), S6Ctx));
            Check(error.Message.Contains("refused", StringComparison.Ordinal), "the transaction's failure reaches the caller: " + error.Message);
            await S6Flushed(host);
            Sequence(["7"], S6Seen(seen), "the emit was delivered");
            Equal(0, ticker.Member("count").Value!.GetValue<int>(), "the transaction's change was not");
        }),
        Case("host.events.lets-the-owner-listen-to-its-own-source-from-its-subscription-on", async () =>
        {
            var (host, probe, _) = await S6Ticking(); await using var closing = host;
            var seen = new List<string>(); IEventSubscription? own = null;
            S6Turn(host, () =>
            {
                probe.Source.Emit(S6Y(1));
                own = probe.Source.OnAsync(S6Recorder(seen), S6Ctx).GetAwaiter().GetResult();
                probe.Source.Emit(S6Y(2));
            });
            await S6Flushed(host);
            own!.Stop();
            probe.Source.Emit(S6Y(3));
            await S6Flushed(host);
            Sequence(["2"], S6Seen(seen), "only what was emitted while it was subscribed");
        }),

        // --- who gets an event ---
        Case("host.events.gets-nothing-for-a-reference-bound-after-the-emit-also-before-the-flush", async () =>
        {
            var (host, probe, _) = await S6Ticking(); await using var closing = host;
            var seen = new List<string>(); IReference? late = null;
            S6Turn(host, () =>
            {
                probe.Source.Emit(S6Y(1));
                late = host.Hold(S6Ticker, new(), S6Ctx);
                late.Event("ticks").OnAsync(S6Recorder(seen), S6Ctx).GetAwaiter().GetResult();
            });
            await S6Flushed(host);
            probe.Source.Emit(S6Y(2));
            await S6Flushed(host);
            Sequence(["2"], S6Seen(seen), "bound after the first emit"); late!.Release();
        }),
        Case("host.events.gets-an-event-for-a-reference-bound-before-the-emit-and-subscribed-after-it-before-the-flush", async () =>
        {
            var (host, probe, _) = await S6Ticking(); await using var closing = host;
            using var ticker = await host.GetAsync(S6Ticker, new(), S6Ctx);
            var seen = new List<string>();
            S6Turn(host, () =>
            {
                probe.Source.Emit(S6Y(1));
                ticker.Event("ticks").OnAsync(S6Recorder(seen), S6Ctx).GetAwaiter().GetResult();
            });
            await S6Flushed(host);
            Sequence(["1"], S6Seen(seen), "the binding was made before the emit");
        }),
        Case("host.events.gets-nothing-for-a-binding-made-at-the-emits-counter-value-a-same-host-reference-and-a-re-exposing-view", async () =>
        {
            var (host, probe, _) = await S6Ticking(); await using var closing = host;
            using var named = await host.GetAsync(S6NamedTicker, new() { ["name"] = "a" }, S6Ctx);
            var source = probe.Named[^1]; var seen = new List<string>(); IReference? same = null, view = null;
            S6Turn(host, () =>
            {
                source.Emit(S6Y(1));
                // Both bind after the emit, in the same turn: the counter has the emit's value.
                same = host.Hold(S6NamedTicker, new() { ["name"] = "a" }, S6Ctx);
                view = host.Hold(S6TickerView, new() { ["name"] = "a" }, S6Ctx);
                same.Event("ticks").OnAsync(S6Recorder(seen, "same"), S6Ctx).GetAwaiter().GetResult();
                view.Event("ticks").OnAsync(S6Recorder(seen, "view"), S6Ctx).GetAwaiter().GetResult();
            });
            await S6Live(view!); await S6Flushed(host);
            source.Emit(S6Y(2));
            await S6Flushed(host);
            Sequence(["same2", "view2"], S6Seen(seen), "neither got the emit made before it bound");
            same!.Release(); view!.Release();
        }),
        Case("host.events.gets-nothing-for-a-reference-released-before-the-flush", async () =>
        {
            var (host, probe, _) = await S6Ticking(); await using var closing = host;
            var ticker = await host.GetAsync(S6Ticker, new(), S6Ctx);
            var seen = new List<string>();
            await ticker.Event("ticks").OnAsync(S6Recorder(seen), S6Ctx);
            S6Turn(host, () => { probe.Source.Emit(S6Y(1)); ticker.Release(); });
            await S6Flushed(host);
            Equal(0, S6Seen(seen).Length, "released before the flush");
        }),
        Case("host.events.gets-nothing-once-its-object-failed-before-the-flush-and-again-once-a-retry-is-live", async () =>
        {
            var (host, probe, _) = await S6Ticking(); await using var closing = host;
            using var ticker = await host.GetAsync(S6Ticker, new(), S6Ctx);
            var seen = new List<string>();
            await ticker.Event("ticks").OnAsync(S6Recorder(seen), S6Ctx);
            var failing = probe.Selves[^1];
            // A live failure of the owner: its references show it failed at once.
            S6Turn(host, () => { probe.Source.Emit(S6Y(1)); S6FailLive(host, failing); });
            await S6Flushed(host);
            Equal("failed", ticker.State.Value.Kind, "the reference shows the failure");
            ticker.Retry(); await S6Live(ticker);
            probe.Source.Emit(S6Y(2));
            await S6Flushed(host);
            Sequence(["2"], S6Seen(seen), "nothing of the failed instance, the retry's emit");
        }),
        Case("host.events.gets-nothing-through-a-re-exposing-view-once-the-source-failed-before-the-flush", async () =>
        {
            var (host, probe, _) = await S6Ticking(); await using var closing = host;
            using var view = await host.GetAsync(S6TickerView, new() { ["name"] = "a" }, S6Ctx);
            var seen = new List<string>();
            await view.Event("ticks").OnAsync(S6Recorder(seen), S6Ctx);
            S6Turn(host, () => { probe.Named[^1].Emit(S6Y(1)); S6FailLive(host, probe.NamedSelves[^1]); });
            await S6Flushed(host);
            Equal(0, S6Seen(seen).Length, "the view's reference no longer shows its source live");
        }),

        // --- GC ---
        Case("host.events.gc-stopped-subscriptions-released-references-and-a-flushed-list-keep-nothing", async () =>
        {
            var (host, probe, _) = await S6Ticking(); await using var closing = host;
            var refs = S6EventGarbage(host, probe);
            await ChordSupport.ExpectCollected(refs);
        }),
        Case("host.events.gc-a-subscription-keeps-no-abort-registration-once-stopped", async () =>
        {
            var (host, _, _) = await S6Ticking(); await using var closing = host;
            using var ticker = await host.GetAsync(S6Ticker, new(), S6Ctx);
            using var cancel = new CancellationTokenSource();
            var weak = S6StoppedSubscription(host, ticker, ChordContext.WithAbortSignal(cancel.Token, S6Ctx));
            await ChordSupport.ExpectCollected(weak);
            GC.KeepAlive(cancel);
        }),
        Case("host.events.gc-a-stopped-subscription-kept-by-its-holder-keeps-neither-its-source-nor-its-reference", async () =>
        {
            var (host, probe, _) = await S6Ticking(); await using var closing = host;
            var (kept, refs) = S6KeptSubscriptions(host, probe);
            await S6Until(() => host.Dispatcher.Invoke(() => host.Objects.Count == 1 && host.EndingObjects.Count == 0), "the named ticker ending");
            await ChordSupport.ExpectCollected(refs);
            Equal(2, kept.Length, "the stopped subscriptions are still held by the test");
            GC.KeepAlive(kept);
        }),

        // --- cost ---
        Case("host.events.cost-a-contract-without-event-members-keeps-no-event-bookkeeping-per-binding", async () =>
        {
            // Upstream spies on the counter's getter (never read for such a contract). Here: such a binding has no event member to hand
            // out, the host's counter never moves, and the variants of a contract without events share one empty list of event members.
            var type = Contract.Remote("app.plain", new()
            { Version = 1, Params = new(), HostAddress = _ => Server.Address(), Variants = new() { ["v"] = new() { State = { ["value"] = Int() } } } });
            var (server, client) = await S6Linked(h => h.Serve(type, (_, _, _) =>
                new(new Dictionary<string, object?> { ["value"] = Watchables.Constant(JsonValue.Create(1)) }, "v")));
            await using var closeServer = server; await using var closeClient = client;
            using var reference = await client.GetAsync(type, new(), S6Ctx);
            Equal("live", reference.State.Value.Kind, "bound over the link");
            Throws<ChordError>(() => reference.Event("value"), "a state member is no event member");
            Equal(0L, server.Dispatcher.Invoke(() => server.Events.Counter), "no event was counted on the server");
            Equal(0L, client.Dispatcher.Invoke(() => client.Events.Counter), "nor on the client");
            var variants = ContractsInternal.CurrentVariants(Notes);
            Check(ReferenceEquals(ContractsInternal.VariantNamesOf(variants["editor"]).Events, ContractsInternal.VariantNamesOf(variants["reader"]).Events),
                "their names share one empty list of event members");
        }),
        Case("host.events.cost-an-emit-with-no-holder-encodes-nothing-walks-and-copies-nothing-it-is-counted-emitted", async () =>
        {
            var telemetry = new InMemoryTelemetryContext();
            var (host, probe, _) = await S6Ticking(telemetry); await using var closing = host;
            probe.Source.Emit(S6Y(1));
            await S6Flushed(host);
            Equal(1.0, S6Count(telemetry, "chord.events.emitted"), "counted emitted");
            Equal(0.0, S6Count(telemetry, "chord.events.encodes"), "nothing encoded");
            Equal(0.0, S6Count(telemetry, "chord.data.walks"), "nothing walked");
        }),

        // --- reference-events.test.ts: subscriptions ---
        Case("host.reference-events.subscriptions-are-installed-at-once-while-loading-and-receive-once-their-reference-is-live", async () =>
        {
            var (host, probe, _) = await S6Ticking(); await using var closing = host;
            var seen = new List<string>(); IReference? named = null; var loading = "";
            S6Turn(host, () =>
            {
                named = host.Hold(S6NamedTicker, new() { ["name"] = "a" }, S6Ctx);
                named.Event("ticks").OnAsync(S6Recorder(seen), S6Ctx).GetAwaiter().GetResult();
                loading = named.State.Value.Kind;
            });
            Equal("loading", loading, "subscribed while loading");
            await S6Live(named!);
            probe.Named[^1].Emit(S6Y(1));
            await S6Flushed(host);
            Sequence(["1"], S6Seen(seen), "received once live"); named!.Release();
        }),
        Case("host.reference-events.subscriptions-reject-an-aborted-contexts-cancellation-and-a-released-references-released-installing-nothing", async () =>
        {
            var (host, _, _) = await S6Ticking(); await using var closing = host;
            var ticker = await host.GetAsync(S6Ticker, new(), S6Ctx);
            var member = ticker.Event("ticks");
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            Check(await S6Rejection(member.OnAsync(_ => { }, ChordContext.WithAbortSignal(cancel.Token, S6Ctx))) is OperationCanceledException,
                "an aborted context's cancellation");
            ticker.Release();
            Equal("released", ((ChordError)await S6Rejection(member.OnAsync(_ => { }, S6Ctx))).Code, "a released reference's released");
        }),
        Case("host.reference-events.subscriptions-end-with-their-contexts-abort", async () =>
        {
            var (host, probe, _) = await S6Ticking(); await using var closing = host;
            using var ticker = await host.GetAsync(S6Ticker, new(), S6Ctx);
            var seen = new List<string>(); using var cancel = new CancellationTokenSource();
            await ticker.Event("ticks").OnAsync(S6Recorder(seen), ChordContext.WithAbortSignal(cancel.Token, S6Ctx));
            S6Turn(host, () => { probe.Source.Emit(S6Y(1)); cancel.Cancel(); });
            await S6Flushed(host);
            Equal(0, S6Seen(seen).Length, "a call due when its context aborted is skipped");
        }),
        Case("host.reference-events.subscriptions-move-with-their-reference-to-a-restarts-new-instance-nothing-missed-is-replayed", async () =>
        {
            var probe = new S6TickerProbe(); await using var host = S6Host();
            PluginFacet Plugin() => new("@test/app", Facet.Define((h, _) => S6ServeTickers(h, probe)));
            await host.LoadAsync([Plugin()], S6Ctx);
            using var ticker = await host.GetAsync(S6Ticker, new(), S6Ctx);
            var seen = new List<string>();
            await ticker.Event("ticks").OnAsync(S6Recorder(seen), S6Ctx);
            var first = probe.Source; first.Emit(S6Y(1));
            await S6Flushed(host);
            await host.ReloadAsync([Plugin()], S6Ctx); await host.DrainReloadsAsync(S6Ctx);
            await S6Until(() => { lock (probe) return probe.Sources[^1] is OwnEventSource && !ReferenceEquals(probe.Sources[^1], first); }, "the successor's source");
            await S6Live(ticker); await S6Flushed(host);
            probe.Source.Emit(S6Y(2));
            await S6Flushed(host);
            Sequence(["1", "2"], S6Seen(seen), "the old instance's emit, then the new one's");
        }),
        Case("host.reference-events.subscriptions-keep-getting-an-old-attempts-emits-while-a-reload-drains-it-then-the-successors", async () =>
        {
            var pulse = Contract.Local("app.pulse", new() { Version = 1, Params = new(), Members = new() { ["beats"] = Event(S6Sample), ["slow"] = Method() } });
            var sources = new List<IEventSource>(); var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            PluginFacet Plugin() => new("@test/app", Facet.Define((h, _) => h.Serve(pulse, (_, _, self) =>
            {
                var beats = self.Events(); lock (sources) sources.Add(beats);
                return new(new Dictionary<string, object?>
                {
                    ["beats"] = beats,
                    ["slow"] = (HostMethod)(async (_, _) => { started.TrySetResult(); await open.Task; beats.Emit(S6Y(1)); return null; }),
                });
            })));
            await using var host = S6Host(); await host.LoadAsync([Plugin()], S6Ctx);
            using var reference = await host.GetAsync(pulse, new(), S6Ctx);
            var seen = new List<string>();
            await reference.Event("beats").OnAsync(S6Recorder(seen), S6Ctx);
            var slow = reference.CallAsync("slow", null, S6Ctx); await started.Task;
            var reloaded = host.ReloadAsync([Plugin()], S6Ctx);
            await S6Flushed(host);
            Equal("live", reference.State.Value.Kind, "the old attempt still serves its reference while it drains");
            open.TrySetResult(); await slow;
            await S6Flushed(host);
            Sequence(["1"], S6Seen(seen), "the draining attempt's emit");
            await reloaded; await host.DrainReloadsAsync(S6Ctx); await S6Live(reference);
            await S6Until(() => { lock (sources) return sources[^1] is OwnEventSource && !ReferenceEquals(sources[^1], sources[0]); }, "the successor's source");
            await S6Flushed(host);
            IEventSource last; lock (sources) last = sources[^1];
            last.Emit(S6Y(2));
            await S6Flushed(host);
            Sequence(["1", "2"], S6Seen(seen), "then the successor's");
        }),

        // --- re-exposed members ---
        Case("host.reference-events.re-exposed-members-forward-each-event-with-its-value-and-stamp-unchanged", async () =>
        {
            var (host, probe, _) = await S6Ticking(); await using var closing = host;
            using var view = await host.GetAsync(S6TickerView, new() { ["name"] = "a" }, S6Ctx);
            var seen = new List<ChordEvent>(); var own = new List<ChordEvent>();
            await view.Event("ticks").OnAsync(e => { lock (seen) seen.Add(e); }, S6Ctx);
            var source = probe.Named[^1];
            await source.OnAsync(e => { lock (own) own.Add(e); }, S6Ctx);
            source.Emit(S6Y(5));
            await S6Flushed(host);
            Equal(1, seen.Count, "one event through the view"); Equal(1, own.Count, "one at the source");
            Check(ReferenceEquals(seen[0].Value, own[0].Value) && seen[0].At == own[0].At, "the value and stamp are the source's");
        }),
        Case("host.reference-events.re-exposed-members-follow-their-sources-new-instance-without-the-view-restarting", async () =>
        {
            var (host, probe, _) = await S6Ticking(); await using var closing = host;
            using var view = await host.GetAsync(S6TickerView, new() { ["name"] = "a" }, S6Ctx);
            var seen = new List<string>();
            await view.Event("ticks").OnAsync(S6Recorder(seen), S6Ctx);
            using var named = await host.GetAsync(S6NamedTicker, new() { ["name"] = "a" }, S6Ctx);
            S6FailLive(host, probe.NamedSelves[^1]);
            named.Retry();
            await S6Until(() => { lock (probe) return probe.Named.Count == 2; }, "the retry's attempt");
            await S6Live(named); await S6Live(view); await S6Flushed(host);
            probe.Named[^1].Emit(S6Y(3));
            await S6Flushed(host);
            Sequence(["3"], S6Seen(seen), "the view's subscription follows the new instance");
        }),

        // --- subscriptions through failures and reloads ---
        Case("host.reference-events.subscriptions-open-while-the-object-is-failed-and-receive-once-a-retry-is-live", async () =>
        {
            var (host, probe, _) = await S6Ticking(); await using var closing = host;
            using var named = await host.GetAsync(S6NamedTicker, new() { ["name"] = "a" }, S6Ctx);
            S6FailLive(host, probe.NamedSelves[^1]);
            await S6Until(() => named.State.Value.Kind == "failed", "the failure");
            var seen = new List<string>();
            await named.Event("ticks").OnAsync(S6Recorder(seen), S6Ctx);
            named.Retry();
            await S6Until(() => { lock (probe) return probe.Named.Count == 2; }, "the retry's attempt");
            await S6Live(named); await S6Flushed(host);
            probe.Named[^1].Emit(S6Y(7));
            await S6Flushed(host);
            Sequence(["7"], S6Seen(seen), "received once the retry is live");
        }),
        Case("host.reference-events.160-a-reload-moves-own-re-exposed-and-mapped-members-to-the-new-instance", async () =>
        {
            // Upstream's "keep no node of an instance a reload replaced" also asserts the replaced nodes are collected; the delivery half
            // is ported here (the collection half needs every EventNode ever made, which upstream gathers by patching a prototype).
            var timed = Obj(("y", Num()), ("t", Num()));
            var sourceV1 = Contract.Local("app.source", new() { Version = 1, Params = new(), Members = new() { ["ticks"] = Event(S6Sample) } });
            var sourceV2 = Contract.Evolve(sourceV1, e => e.Event("ticks", timed));
            var viewer = Contract.Local("app.viewer", new() { Version = 1, Params = new(), Members = new() { ["ticks"] = Event(timed) } });
            var sources = new List<IEventSource>();
            PluginFacet Plugin() => new("@test/app", Facet.Define((h, _) =>
            {
                h.Serve(sourceV2, (_, _, self) =>
                {
                    var ticks = self.Events(); lock (sources) sources.Add(ticks);
                    return new(new Dictionary<string, object?> { ["ticks"] = ticks });
                }, new(new AcceptsTable { [1] = new() { Events = { ["ticks"] = new StateMapping(value => new JsonObject { ["y"] = ((JsonNode)value!)["y"]!.DeepClone() }) } } }));
                h.Serve(viewer, (_, _, self) => new(new Dictionary<string, object?> { ["ticks"] = self.Get(sourceV2, new()).Event("ticks") }));
            }));
            await using var host = S6Host(); await host.LoadAsync([Plugin()], S6Ctx);
            using var older = await host.GetAsync(sourceV1, new(), S6Ctx); using var view = await host.GetAsync(viewer, new(), S6Ctx);
            var seen = new List<string>();
            await older.Event("ticks").OnAsync(S6Recorder(seen, "old "), S6Ctx);
            await view.Event("ticks").OnAsync(S6Recorder(seen, "view "), S6Ctx);
            IEventSource first; lock (sources) first = sources[^1];
            first.Emit(new JsonObject { ["y"] = 1, ["t"] = 0 });
            await S6Flushed(host);
            await host.ReloadAsync([Plugin()], S6Ctx); await host.DrainReloadsAsync(S6Ctx);
            await S6Until(() => { lock (sources) return sources[^1] is OwnEventSource && !ReferenceEquals(sources[^1], first); }, "the successor's source");
            await S6Live(older); await S6Live(view); await S6Flushed(host);
            IEventSource last; lock (sources) last = sources[^1];
            last.Emit(new JsonObject { ["y"] = 2, ["t"] = 0 });
            await S6Flushed(host);
            Sequence(["old 1", "old 2", "view 1", "view 2"], S6Seen(seen).Order(StringComparer.Ordinal), "both follow the new instance");
        }),

        // --- members.test.ts: event members ---
        Case("host.members.event-members-fail-the-object-broken-for-an-implementation-that-is-neither-its-own-source-nor-a-references-event-member", async () =>
        {
            var ticks = Contract.Local("app.ticks", new() { Version = 1, Params = new() { ["n"] = Int() }, Members = new() { ["ticks"] = Event(Obj(("y", Int()))) } });
            var others = new List<IEventSource>();
            await using var host = S6Host();
            await host.LoadAsync([new("@test/app", Facet.Define((h, _) => h.Serve(ticks, (parameters, _, self) =>
            {
                var own = self.Events(); lock (others) others.Add(own);
                // 1: a foreign implementation of the interface; 2: another attempt's source; 3: a state member.
                object? member = parameters["n"]!.GetValue<int>() switch
                {
                    1 => new S6ForeignEvents(), 2 => others[0], 3 => Watchables.Constant(JsonValue.Create(1)), _ => own,
                };
                return new(new Dictionary<string, object?> { ["ticks"] = member });
            })))], S6Ctx);
            using var good = await host.GetAsync(ticks, new() { ["n"] = 0 }, S6Ctx);
            Check(good.Event("ticks") is not null, "its own source binds");
            foreach (var n in new[] { 1, 2, 3 })
            {
                var error = await S6Rejection(host.GetAsync(ticks, new() { ["n"] = n }, S6Ctx));
                Check(error.Message.Contains("ticks: an event member is this object's own self.Events() or a reference's event member", StringComparison.Ordinal),
                    $"n={n}: {error.Message}");
            }
        }),
        Case("host.members.event-members-bind-an-event-only-contract-live-no-state-to-hydrate-at-once-over-a-link-too", async () =>
        {
            var only = Contract.Remote("app.only", new()
            {
                Version = 1, Params = new(), HostAddress = _ => Server.Address(),
                Variants = new() { ["v"] = new() { Events = { ["ticks"] = Obj(("y", Int())) } } },
            });
            var (server, client) = await S6Linked(h => h.Serve(only, (_, _, self) => new(new Dictionary<string, object?> { ["ticks"] = self.Events() }, "v")));
            await using var closeServer = server; await using var closeClient = client;
            using var reference = await client.GetAsync(only, new(), S6Ctx);
            Equal("live", reference.State.Value.Kind, "live without any state member");
        }),

        // --- staging.test.ts ---
        Case("host.staging.hands-out-an-inert-event-source-an-emit-in-staging-delivers-nothing", async () =>
        {
            var ticks = Contract.Local("app.ticks", new() { Version = 1, Params = new(), Members = new() { ["ticks"] = Event(Obj(("y", Int()))) } });
            await using var host = S6Host(); var factoryRuns = 0;
            PluginFacet Plugin(string label) => new("@test/app", Facet.Define((h, _) => h.Serve(ticks, (_, _, self) =>
            {
                var source = self.Events(); Interlocked.Increment(ref factoryRuns);
                // Staging runs this against a recording self: its handle is inert, the emit does nothing.
                source.Emit(new JsonObject { ["y"] = label.Length });
                return new(new Dictionary<string, object?> { ["ticks"] = source });
            })));
            await host.LoadAsync([Plugin("v1")], S6Ctx);
            using var reference = await host.GetAsync(ticks, new(), S6Ctx);
            var seen = new List<string>();
            await reference.Event("ticks").OnAsync(S6Recorder(seen), S6Ctx);
            var before = factoryRuns;
            var report = await host.ReloadAsync([Plugin("v2!")], S6Ctx);
            Equal(0, report.Failed.Count, "the reload staged the factory");
            Check(factoryRuns > before, "the factory ran in staging");
            await S6Flushed(host);
            // The recording's emit delivered nothing; the real attempt's emit in its factory precedes every binding to it.
            Equal(0, S6Seen(seen).Length, "nothing was delivered");
        }),
    ];

    private sealed class S6ForeignEvents : IEventSource
    {
        public void Emit(JsonNode? value) { }
        public Task<IEventSubscription> OnAsync(Action<ChordEvent> listener, ChordContext context) => throw new NotSupportedException();
    }
}
