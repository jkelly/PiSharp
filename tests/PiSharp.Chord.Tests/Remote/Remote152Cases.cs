// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/remote/consumer.ts, packages/chord/src/remote/provider.ts, packages/chord/src/remote/relay.ts.
// Authored native regression; not an upstream execution capture.
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Host;
using PiSharp.Chord.Link;
using PiSharp.Chord.Documents;
using PiSharp.Telemetry;
using static PiSharp.Chord.Contracts.Schemas;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Remote152Cases()
    {
        yield return Case("remote.152.loading-retains-running-rejects-queued-calls", async () =>
        {
            var type = R1Type(); await using var host = R1Host(); await host.LoadAsync([], ChordContext.Background);
            var pair = MemoryLinkPair.Create(); using var raw = new R1Peer(pair.Second, calls: 1);
            host.Connect(pair.First, new() { Principal = R1User }, ChordContext.Background);
            using var reference = host.Hold(type, new(), ChordContext.Background);
            var hold = await raw.Next<HoldMessage>(); raw.Send(new BoundMessage([R1Bound(hold.Hold, 0)]));
            await R1Until(() => reference.State.Value.Kind == "live");
            var first = reference.CallAsync("who", null, ChordContext.Background); var sent = await raw.Next<CallMessage>();
            var second = reference.CallAsync("who", null, ChordContext.Background);
            raw.Send(new LoadingMessage(hold.Hold)); await R1Until(() => reference.State.Value.Kind == "loading");
            Check(!first.IsCompleted, "loading retains the already sent call");
            try { await second; throw new Exception("queued call survived loading"); }
            catch (ChordError error) { Equal("unavailable", error.Code, "queued old-index call rejects"); }
            raw.Send(new ResultMessage(sent.Call)); await first;
            Check(!raw.All.OfType<CallMessage>().Any(), "queued call never sent after slot freed");
            raw.Send(new BoundMessage([R1Bound(hold.Hold, 1)])); await R1Until(() => reference.State.Value.Kind == "live");
            var failing = reference.CallAsync("who", null, ChordContext.Background); await raw.Next<CallMessage>();
            raw.Send(new FailedMessage(hold.Hold, new("broken", WireError.FromException(new ChordError("unavailable", "failed")))));
            try { await failing; throw new Exception("failed did not reject call"); }
            catch (ChordError error) { Equal("unavailable", error.Code, "failed ends calls"); }
        });
        yield return Case("remote.152.old-work-only-trusted-incoming", async () =>
        {
            foreach (var trusted in new[] { false, true })
            {
                var type = R1Type(); await using var host = R1Host(R1Server.Address());
                await host.LoadAsync([new("old", Facet.Define((f, _) => f.Serve(type, (_, _, self) => new(new Dictionary<string, object?>
                {
                    ["value"] = PiSharp.Chord.State.Watchables.Constant(new JsonObject { ["n"] = 0 }),
                    ["inc"] = (HostMethod)((_, _) => Task.FromResult<object?>(null)),
                    ["who"] = (HostMethod)((_, ctx) => Task.FromResult<object?>(JsonValue.Create(host.OldWork.Of(ctx.Context)))),
                }, "editor"))))], ChordContext.Background);
                var pair = MemoryLinkPair.Create(); using var raw = new R1Peer(pair.Second);
                host.Accept(pair.First, new() { Principal = R1User, Trusted = trusted }, ChordContext.Background);
                raw.Send(R1Hold(type)); await raw.Next<BoundMessage>(); raw.Send(new CallMessage(0, 0, 1, Old: true));
                Equal(trusted, Parts.Parse((await raw.Next<ResultMessage>()).Value!)!.GetValue<bool>(), "incoming trusted " + trusted);
            }
        });
        yield return Case("remote.152.loading-cannot-send-queued-index-after-reordered-bound", async () =>
        {
            var type = Contract.Remote("remote.queued-index", new()
            {
                Version = 1, Params = new(), HostAddress = _ => R1Server.Address(), Variants = new()
                {
                    ["a"] = new() { Methods = new() { ["alpha"] = Method(), ["zeta"] = Method() } },
                    ["b"] = new() { Methods = new() { ["zeta"] = Method() } },
                },
            });
            await using var host = R1Host(); await host.LoadAsync([], ChordContext.Background);
            var pair = MemoryLinkPair.Create(); using var raw = new R1Peer(pair.Second, calls: 1);
            host.Connect(pair.First, new() { Principal = R1User }, ChordContext.Background);
            using var reference = host.Hold(type, new(), ChordContext.Background);
            var hold = await raw.Next<HoldMessage>();
            raw.Send(new BoundMessage([new BoundEntry(hold.Hold, 0, [], [])]));
            await R1Until(() => reference.Variant == "a"); await raw.Next<AckMessage>();
            var running = reference.CallAsync("alpha", null, ChordContext.Background);
            var first = await raw.Next<CallMessage>(); Equal(0L, first.Method, "first old variant index");
            var queued = reference.CallAsync("zeta", null, ChordContext.Background);
            raw.Send(new LoadingMessage(hold.Hold));
            try { await queued; throw new Exception("stale queued call survived rebind"); }
            catch (ChordError error) { Equal("unavailable", error.Code, "stale queued call rejected"); }
            Check(!running.IsCompleted, "sent call still waits for its old provider result");
            raw.Send(new BoundMessage([new BoundEntry(hold.Hold, 1, [], [])]));
            await R1Until(() => reference.Variant == "b"); await raw.Next<AckMessage>();
            raw.Send(new ResultMessage(first.Call)); await running;
            Check(!raw.All.OfType<CallMessage>().Any(), "free slot never sends old numeric index");
            var fresh = reference.CallAsync("zeta", null, ChordContext.Background);
            var next = await raw.Next<CallMessage>(); Equal(0L, next.Method, "fresh call uses new variant index");
            Equal(first.Call + 1, next.Call, "rejected queue allocates no wire call id");
            raw.Send(new ResultMessage(next.Call)); await fresh;
        });
        yield return Case("remote.152.relay-strips-untrusted-old-without-telemetry", async () =>
        {
            foreach (var trusted in new[] { false, true })
            {
                var type = R1Type(); await using var relay = R1Host(); await relay.LoadAsync([], ChordContext.Background);
                var up = MemoryLinkPair.Create(); using var upstream = new R1Peer(up.Second);
                relay.Connect(up.First, new() { Principal = R1User }, ChordContext.Background);
                var down = MemoryLinkPair.Create(); using var downstream = new R1Peer(down.Second);
                relay.Accept(down.First, new() { Principal = R1User, Trusted = trusted }, ChordContext.Background);
                downstream.Send(R1Hold(type)); var hold = await upstream.Next<HoldMessage>();
                upstream.Send(new BoundMessage([R1Bound(0, 0)]) { Origin = hold.Origin }); await downstream.Next<BoundMessage>();
                downstream.Send(new CallMessage(0, 0, 1, Old: true)); var call = await upstream.Next<CallMessage>();
                Equal(trusted ? true : (bool?)null, call.Old, "relay old trust");
                upstream.Send(new ResultMessage(call.Call) { Origin = call.Origin }); await downstream.Next<ResultMessage>();
            }
        });
        yield return Case("remote.152.state-before-result-past-full-window", async () =>
        {
            var type = R1Type(); await using var host = R1Host(R1Server.Address()); await host.LoadAsync([R1Plugin(type)], ChordContext.Background);
            var pair = MemoryLinkPair.Create(); using var raw = new R1Peer(pair.Second);
            host.Accept(pair.First, new() { Principal = R1User }, ChordContext.Background);
            raw.Send(R1Hold(type)); await raw.Next<BoundMessage>();
            for (var id = 0; id < 5; id++)
            {
                raw.Send(new CallMessage(id, 0, 0));
                await R1Until(() => raw.All.OfType<ResultMessage>().Any(x => x.Call == id));
                var all = raw.All;
                Check(Array.FindIndex(all, x => x is StateMessage) < Array.FindIndex(all, x => x is ResultMessage), "state precedes result " + id);
                await raw.Next<StateMessage>(); await raw.Next<ResultMessage>();
            }
            for (var i = 0; i < 6; i++) raw.Send(new AckMessage());
            raw.Send(new CallMessage(6, 0, 1)); await raw.Next<ResultMessage>();
            Check(!raw.All.OfType<StateMessage>().Any(), "no empty state frame before unchanged answer");
            Check(!raw.Closed, "all excess state ACKs balance");
        });
        yield return Case("remote.152.method-index-keeps-old-binding-until-own-ack", async () =>
        {
            var type = Contract.Remote("remote.index-order", new()
            {
                Version = 1, Params = new(), HostAddress = _ => R1Server.Address(), Variants = new()
                {
                    ["a"] = new() { Methods = new() { ["alpha"] = Method(null, Str()), ["zeta"] = Method(null, Str()) } },
                    ["b"] = new() { Methods = new() { ["zeta"] = Method(null, Str()) } },
                },
            });
            PluginFacet Plugin(string variant) => new("index", Facet.Define((f, _) => f.Serve(type, (_, _, _) =>
                new(variant == "a" ? new Dictionary<string, object?>
                {
                    ["alpha"] = (HostMethod)((_, _) => Task.FromResult<object?>(JsonValue.Create("alpha"))),
                    ["zeta"] = (HostMethod)((_, _) => Task.FromResult<object?>(JsonValue.Create("old-zeta"))),
                } : new Dictionary<string, object?>
                {
                    ["zeta"] = (HostMethod)((_, _) => Task.FromResult<object?>(JsonValue.Create("new-zeta"))),
                }, variant))));
            await using var host = R1Host(R1Server.Address()); await host.LoadAsync([Plugin("a")], ChordContext.Background);
            var pair = MemoryLinkPair.Create(); using var raw = new R1Peer(pair.Second);
            host.Accept(pair.First, new() { Principal = R1User }, ChordContext.Background);
            raw.Send(R1Hold(type)); await raw.Next<BoundMessage>(); raw.Send(new AckMessage());
            await host.ReloadAsync([Plugin("b")], ChordContext.Background); await raw.Next<BoundMessage>();
            raw.Send(new CallMessage(0, 0, 1));
            Equal("new-zeta", Parts.Parse((await raw.Next<ResultMessage>()).Value!)!.GetValue<string>(), "old index before ACK");
            raw.Send(new AckMessage()); raw.Send(new CallMessage(1, 0, 0));
            Equal("new-zeta", Parts.Parse((await raw.Next<ResultMessage>()).Value!)!.GetValue<string>(), "new index after own ACK");
            Check(!raw.Closed, "old index accepted while fresh bound unacknowledged");
        });
        yield return Case("remote.152.trace-crosses-consumer-relay-provider", async () =>
        {
            var memory = new InMemoryTelemetryContext();
            var type = R1Type();
            await using var server = Hosts.Create(new() { Address = R1Server.Address(), Principal = R1User, Telemetry = memory, OnError = _ => { } });
            await using var relay = Hosts.Create(new() { Principal = R1User, Telemetry = memory, OnError = _ => { } });
            await using var client = Hosts.Create(new() { Principal = R1User, Telemetry = memory, OnError = _ => { } });
            Equal(0, (await server.LoadAsync([R1Plugin(type)], ChordContext.Background)).Failed.Count, "provider loaded");
            relay.Route(R1Server, new()
            {
                Authorize = (_, _) => true,
                Resolve = (_, _) =>
                {
                    var up = MemoryLinkPair.Create();
                    // A routed provider sees its router as a parent. Child links
                    // deliberately reject relayed origins, even when trusted.
                    server.Connect(up.Second, new() { Principal = R1User, Trusted = true }, ChordContext.Background);
                    return Task.FromResult(new ResolvedLink(up.First, new() { Principal = R1User, Trusted = true }));
                },
            }, ChordContext.Background);
            await relay.LoadAsync([], ChordContext.Background); await client.LoadAsync([], ChordContext.Background);
            var down = MemoryLinkPair.Create();
            relay.Accept(down.First, new() { Principal = R1User }, ChordContext.Background);
            client.Connect(down.Second, new() { Principal = R1User }, ChordContext.Background);
            await memory.StartSpanAsync("trigger", async span =>
            {
                var context = ChordContext.WithTelemetry(span, ChordContext.Background);
                using var reference = await client.GetAsync(type, new(), context);
                await reference.CallAsync("inc", null, context);
                Equal(1, reference.Member("value").Value!["n"]!.GetValue<int>(), "state adopted before traced answer");
            });
            var spans = memory.GetSpans();
            var call = spans.Single(s => s.Name == "chord.call");
            var hop = spans.Single(s => s.Name == "chord.relay" && s.Attributes["member"]?.GetString() == "#0");
            var method = spans.Single(s => s.Name == "chord.method");
            Equal(call.Id, hop.ParentId!.Value, "relay child of consumer span");
            Equal(hop.Id, method.ParentId!.Value, "method child of relay span");
            Equal(R1Server.Address().Key, method.Attributes["chord.host"]!.GetString(), "provider host stamped");
            Check(call.Settled && hop.Settled && method.Settled, "all method and relay spans settled");
        });
        yield return Case("remote.152.relay-answer-flushes-local-state-and-attributes-acks", async () =>
        {
            var local = R1Type("remote.local"); var remoteKind = new HostKind("remote-other");
            var remote = Contract.Remote("remote.other", new()
            {
                Version = 1, Params = new(), HostAddress = _ => remoteKind.Address(),
                Variants = new() { ["v"] = new() { Methods = new() { ["run"] = Method() } } },
            });
            ISingletonHandle? state = null;
            var definition = StateDefinition.Define("remote-order", new() { Version = 1, Schema = Obj(("n", Int())), Initial = () => new() { ["n"] = 0 } });
            await using var relay = R1Host(R1Server.Address());
            await relay.LoadAsync([new("local", Facet.Define((f, _) => f.Serve(local, (_, _, self) =>
            {
                state = self.State(definition);
                return new(new Dictionary<string, object?>
                {
                    ["value"] = state,
                    ["inc"] = (HostMethod)((_, _) => Task.FromResult<object?>(null)),
                    ["who"] = (HostMethod)((_, _) => Task.FromResult<object?>(null)),
                }, "editor");
            })))], ChordContext.Background);
            var up = MemoryLinkPair.Create(); using var upstream = new R1Peer(up.Second);
            relay.Connect(up.First, new() { Principal = R1User }, ChordContext.Background);
            var down = MemoryLinkPair.Create(); using var downstream = new R1Peer(down.Second);
            relay.Accept(down.First, new() { Principal = R1User }, ChordContext.Background);
            downstream.Send(R1Hold(local)); await downstream.Next<BoundMessage>();
            await state!.ChangeAsync(d => d["n"] = 1, ChordContext.Background); await downstream.Next<StateMessage>();
            await state.ChangeAsync(d => d["n"] = 2, ChordContext.Background);
            Check(!downstream.All.OfType<StateMessage>().Any(), "local state waits at full window");
            downstream.Send(new HoldMessage(1, remote.Name, 1, remote.Fingerprint, new Part.Text("{}"), new(remoteKind.Kind, new JsonObject())));
            var forwardedHold = await upstream.Next<HoldMessage>();
            upstream.Send(new BoundMessage([new BoundEntry(1, 0, [], [])]) { Origin = forwardedHold.Origin }); await downstream.Next<BoundMessage>();
            downstream.Send(new CallMessage(4, 1, 0)); var call = await upstream.Next<CallMessage>();
            upstream.Send(new ErrorMessage(call.Call, WireError.FromException(new ChordError("unavailable", "test"))) { Origin = call.Origin });
            await R1Until(() => downstream.All.OfType<ErrorMessage>().Any());
            var messages = downstream.All;
            Check(Array.FindIndex(messages, m => m is StateMessage) < Array.FindIndex(messages, m => m is ErrorMessage), "local pending state precedes relayed error");
            await downstream.Next<StateMessage>(); await downstream.Next<ErrorMessage>();
            downstream.Send(new AckMessage()); downstream.Send(new AckMessage());
            Check(!upstream.All.OfType<AckMessage>().Any(), "own state ACKs do not reach upstream");
            downstream.Send(new AckMessage());
            Equal(forwardedHold.Origin, (await upstream.Next<AckMessage>()).Origin, "third ACK belongs to relay");
            downstream.Send(new AckMessage());
            Check(!downstream.Closed && !upstream.Closed, "mixed own and relayed windows remain balanced");
        });
    }
}
