using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Delta;
using PiSharp.Chord.Documents;
using PiSharp.Chord.Host;
using PiSharp.Chord.Link;
using PiSharp.Chord.Remote;
using PiSharp.Chord.State;
using static PiSharp.Chord.Contracts.Schemas;
using StateEntry = PiSharp.Chord.Link.StateEntry;

// Real hosts and raw protocol peers; expected behavior read from pinned next/remote and host/route.ts.
internal static partial class Program
{
    private static readonly HostKind R1Server = new("remote-server");
    private static readonly JsonObject R1User = new() { ["kind"] = "user", ["id"] = 7 };
    private static Contract R1Type(string name = "remote.counter") => Contract.Remote(name, new()
    {
        Version = 1, Params = new(), HostAddress = _ => R1Server.Address(), Variants = new()
        {
            ["editor"] = new() { State = new() { ["value"] = Obj(("n", Int())) }, Methods = new() { ["inc"] = Method(), ["who"] = Method(null, JsonValue.Create(true)) } },
        },
    });
    private static ChordHost R1Host(Address? address = null, List<HostError>? errors = null, Func<JsonObject, IEnumerable<JsonNode?>>? revoke = null) => Hosts.Create(new()
    {
        Address = address, Principal = R1User, OnError = e => { lock (errors ?? []) errors?.Add(e); }, ReadyTimeout = TimeSpan.FromSeconds(2),
        CloseTimeout = TimeSpan.FromMilliseconds(200), RevokedBy = revoke,
    });
    private static PluginFacet R1Plugin(Contract type)
    {
        var definition = StateDefinition.Define("remote-count", new() { Version = 1, Schema = Obj(("n", Int())), Initial = () => new() { ["n"] = 0 } });
        return new("counter", Facet.Define((f, _) => f.Serve(type, (_, _, self) =>
        {
            var state = self.State(definition);
            return new(new Dictionary<string, object?>
            {
                ["value"] = state,
                ["inc"] = (HostMethod)(async (_, ctx) => { await state.ChangeAsync(d => d["n"] = d["n"]!.GetValue<int>() + 1, ctx.Context); return null; }),
                ["who"] = (HostMethod)((_, ctx) => Task.FromResult<object?>(new JsonObject { ["principal"] = ctx.Principal?.DeepClone(), ["incarnation"] = ctx.Incarnation })),
            }, "editor");
        })));
    }
    private static async Task R1Until(Func<bool> check)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!check()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException("remote condition did not settle"); await Task.Delay(5); }
    }
    private sealed class R1Peer : IDisposable
    {
        private readonly ILink link;
        private readonly ICodec codec;
        private readonly object gate = new();
        private readonly List<Message> messages = [];
        internal readonly string Incarnation = Guid.NewGuid().ToString();
        internal bool Closed;
        internal R1Peer(ILink link, ICodec? codec = null, long calls = 256)
        {
            this.link = link; this.codec = codec ?? BinaryCodec.Instance;
            link.OnMessage(bytes => { var message = this.codec.Decode(bytes); lock (gate) messages.Add(message); });
            link.OnClose(() => Closed = true); Send(new HelloMessage(1, Incarnation, calls));
        }
        internal void Send(Message message) => link.Send(codec.Encode(message));
        internal Message[] All { get { lock (gate) return messages.ToArray(); } }
        internal async Task<T> Next<T>(Func<T, bool>? predicate = null) where T : Message
        {
            T? found = null;
            await R1Until(() =>
            {
                lock (gate)
                {
                    found = messages.OfType<T>().FirstOrDefault(x => predicate?.Invoke(x) != false);
                    if (found is not null) messages.Remove(found);
                    return found is not null;
                }
            });
            return found!;
        }
        public void Dispose() => link.Close();
    }
    private static HoldMessage R1Hold(Contract type, long id = 0) => new(id, type.Name, type.Version, type.Fingerprint,
        new Part.Text("{}"), new WireAddress(R1Server.Kind, new JsonObject()));
    private static BoundEntry R1Bound(long hold, int value) => new(hold, 0, [new BoundMember(0, new Part.Text("{\"n\":" + value + "}"))], []);
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference R1InstallCapturedFactory(ChordHost host, Contract type)
    {
        var token = new object(); var weak = new WeakReference(token);
        var facet = new PluginFacet("captured", Facet.Define((f, _) => f.Serve(type, (_, _, _) =>
        {
            GC.KeepAlive(token);
            return new(new Dictionary<string, object?>
            {
                ["value"] = Watchables.Constant(new JsonObject { ["n"] = 0 }),
                ["inc"] = (HostMethod)((_, _) => Task.FromResult<object?>(null)),
                ["who"] = (HostMethod)((_, _) => Task.FromResult<object?>(null)),
            }, "editor");
        })));
        host.LoadAsync([facet], ChordContext.Background).GetAwaiter().GetResult();
        return weak;
    }
    private static IEnumerable<(string, Func<Task>)> RemoteRuntimeCases() =>
    [
        Case("remote.runtime.real-host-call-state-and-principal", async () =>
        {
            var type = R1Type(); await using var server = R1Host(R1Server.Address()); await using var client = R1Host();
            await server.LoadAsync([R1Plugin(type)], ChordContext.Background); await client.LoadAsync([], ChordContext.Background);
            var pair = MemoryLinkPair.Create(); server.Accept(pair.First, new() { Principal = R1User }, ChordContext.Background);
            client.Connect(pair.Second, new() { Principal = new() { ["kind"] = "server" } }, ChordContext.Background);
            using var reference = await client.GetAsync(type, new(), ChordContext.Background);
            await R1Until(() => server.References(type).Value.Count == 1);
            Equal(client.Incarnation, server.References(type).Value[0].Incarnation, "provider presence uses incoming holder incarnation");
            Equal(0, reference.Member("value").Value!["n"]!.GetValue<int>(), "initial snapshot");
            await reference.CallAsync("inc", null, ChordContext.Background);
            await R1Until(() => reference.Member("value").Value!["n"]!.GetValue<int>() == 1);
            var who = (JsonObject)(await reference.CallAsync("who", null, ChordContext.Background))!;
            Check(JsonNode.DeepEquals(R1User, who["principal"]), "caller principal reaches the method");
            Equal(client.Incarnation, who["incarnation"]!.GetValue<string>(), "caller incarnation reaches the method");
            reference.Release(); await R1Until(() => server.Activity.Value.References == 0);
        }),
        Case("remote.runtime.prepublication-incoming-hold", async () =>
        {
            var type = R1Type(); await using var host = R1Host(R1Server.Address()); var pair = MemoryLinkPair.Create(); using var raw = new R1Peer(pair.Second);
            host.Accept(pair.First, new() { Principal = R1User }, ChordContext.Background); raw.Send(R1Hold(type));
            await R1Until(() => host.Activity.Value.References == 1); Check(!raw.All.OfType<BoundMessage>().Any(), "hold waits for publication");
            await host.LoadAsync([R1Plugin(type)], ChordContext.Background); await raw.Next<BoundMessage>();
        }),
        Case("remote.runtime.child-cannot-spoof-principal-or-incarnation", async () =>
        {
            var type = R1Type(); await using var host = R1Host(R1Server.Address()); await host.LoadAsync([R1Plugin(type)], ChordContext.Background);
            var pair = MemoryLinkPair.Create(); using var raw = new R1Peer(pair.Second); host.Accept(pair.First, new() { Principal = R1User, Trusted = true }, ChordContext.Background);
            raw.Send(R1Hold(type) with { Principal = new JsonObject { ["kind"] = "admin" }, Incarnation = Guid.NewGuid().ToString() });
            await raw.Next<BoundMessage>(); raw.Send(new CallMessage(0, 0, 1)); var reply = await raw.Next<ResultMessage>();
            var who = Parts.Parse(reply.Value!); Check(JsonNode.DeepEquals(R1User, who!["principal"]), "trusted child identity still stamped");
            Equal(raw.Incarnation, who["incarnation"]!.GetValue<string>(), "peer handshake incarnation stamped");
        }),
        Case("remote.runtime.parent-may-forward-principal", async () =>
        {
            var type = R1Type(); await using var host = R1Host(R1Server.Address()); await host.LoadAsync([R1Plugin(type)], ChordContext.Background);
            var pair = MemoryLinkPair.Create(); using var raw = new R1Peer(pair.Second); host.Connect(pair.First, new() { Principal = R1User }, ChordContext.Background);
            var forwarded = new JsonObject { ["kind"] = "user", ["id"] = 18 }; var incarnation = Guid.NewGuid().ToString();
            raw.Send(R1Hold(type) with { Principal = forwarded, Incarnation = incarnation }); await raw.Next<BoundMessage>(); raw.Send(new CallMessage(0, 0, 1));
            var who = Parts.Parse((await raw.Next<ResultMessage>()).Value!); Check(JsonNode.DeepEquals(forwarded, who!["principal"]), "parent forwards caller");
            Equal(incarnation, who["incarnation"]!.GetValue<string>(), "parent forwards incarnation");
        }),
        Case("remote.runtime.full-window-folds-and-control-keeps-flowing", async () =>
        {
            var type = R1Type(); await using var host = R1Host(R1Server.Address()); await host.LoadAsync([R1Plugin(type)], ChordContext.Background);
            var pair = MemoryLinkPair.Create(); using var raw = new R1Peer(pair.Second); host.Accept(pair.First, new() { Principal = R1User }, ChordContext.Background);
            raw.Send(R1Hold(type)); await raw.Next<BoundMessage>();
            raw.Send(new CallMessage(0, 0, 0)); await raw.Next<ResultMessage>();
            var decoder = new WireDeltaDecoder();
            JsonNode? state = new JsonObject { ["n"] = 0 };
            void Apply(StateMessage message)
            {
                var ops = decoder.Decode(Parts.Parse(message.Holds[0].Members[0].Ops));
                state = DeltaApply.Apply(state, new JsonArray(ops.Select(x => (JsonNode?)x.ToJson()).ToArray()));
            }
            Apply(await raw.Next<StateMessage>());
            for (var i = 1; i <= 12; i++)
            {
                raw.Send(new CallMessage(i, 0, 0)); await raw.Next<ResultMessage>();
                Apply(await raw.Next<StateMessage>());
            }
            Check(!raw.All.OfType<StateMessage>().Any(), "each answer flushed exactly one waiting state past K=2");
            Equal(13, state!["n"]!.GetValue<int>(), "answer includes final state");
            for (var i = 0; i < 14; i++) raw.Send(new AckMessage());
        }),
        Case("remote.runtime.frame-validation-is-all-or-none", async () =>
        {
            var type = R1Type(); var errors = new List<HostError>(); await using var host = R1Host(errors: errors); await host.LoadAsync([], ChordContext.Background);
            var pair = MemoryLinkPair.Create(); using var raw = new R1Peer(pair.Second); host.Connect(pair.First, new() { Principal = R1User }, ChordContext.Background);
            using var a = host.Hold(type, new(), ChordContext.Background); using var b = host.Hold(type, new(), ChordContext.Background);
            var ah = await raw.Next<HoldMessage>(); var bh = await raw.Next<HoldMessage>();
            raw.Send(new BoundMessage([R1Bound(ah.Hold, 1), R1Bound(bh.Hold, 2)])); await R1Until(() => a.State.Value.Kind == "live" && b.State.Value.Kind == "live");
            var av = a.Member("value"); var bv = b.Member("value");
            raw.Send(new StateMessage([
                new StateEntry(ah.Hold, [new StateMember(0, new Part.Text("[[\"s\",[\"n\"],99]]"))], [], []),
                new StateEntry(bh.Hold, [new StateMember(9, new Part.Text("[[\"r\",{}]]"))], [], []),
            ]));
            await R1Until(() => raw.Closed); Equal(1, av.Value!["n"]!.GetValue<int>(), "first entry never adopted"); Equal(2, bv.Value!["n"]!.GetValue<int>(), "second unchanged");
        }),
        Case("remote.runtime.oversized-owned-part-isolates-hold", async () =>
        {
            var type = R1Type(); await using var host = R1Host(); await host.LoadAsync([], ChordContext.Background);
            var pair = MemoryLinkPair.Create(); using var raw = new R1Peer(pair.Second); host.Connect(pair.First, new() { Principal = R1User, Limits = new(100, 100) }, ChordContext.Background);
            using var a = host.Hold(type, new(), ChordContext.Background); using var b = host.Hold(type, new(), ChordContext.Background);
            var ah = await raw.Next<HoldMessage>(); var bh = await raw.Next<HoldMessage>();
            raw.Send(new BoundMessage([new(ah.Hold, 0, [new(0, new Part.Text("\"" + new string('x', 110) + "\""))], []), R1Bound(bh.Hold, 2)]));
            await R1Until(() => a.State.Value.Kind == "failed" && b.State.Value.Kind == "live");
            Equal("too_large", a.State.Value.Cause!.Error.Code, "only large hold fails"); Check(!raw.Closed, "link remains live");
            Equal(2, b.Member("value").Value!["n"]!.GetValue<int>(), "other entry adopted");
        }),
        Case("remote.runtime.reconnect-reholds-but-never-replays-call", async () =>
        {
            var type = R1Type(); await using var host = R1Host(); await host.LoadAsync([], ChordContext.Background);
            var pair = MemoryLinkPair.Create(); var raw = new R1Peer(pair.Second); host.Connect(pair.First, new() { Principal = R1User }, ChordContext.Background);
            using var reference = host.Hold(type, new(), ChordContext.Background); var hold = await raw.Next<HoldMessage>(); raw.Send(new BoundMessage([R1Bound(hold.Hold, 1)]));
            await R1Until(() => reference.State.Value.Kind == "live"); var call = reference.CallAsync("who", null, ChordContext.Background); await raw.Next<CallMessage>();
            raw.Dispose(); await R1Until(() => reference.State.Value.Kind == "loading");
            try { await call; throw new Exception("call survived drop"); } catch (ChordError error) { Equal("unavailable", error.Code, "inflight failed"); }
            var again = MemoryLinkPair.Create(); using var next = new R1Peer(again.Second); host.Connect(again.First, new() { Principal = R1User }, ChordContext.Background);
            var newHold = await next.Next<HoldMessage>(); next.Send(new BoundMessage([R1Bound(newHold.Hold, 9)])); await R1Until(() => reference.State.Value.Kind == "live");
            Check(!next.All.OfType<CallMessage>().Any(), "no call replay"); Equal(9, reference.Member("value").Value!["n"]!.GetValue<int>(), "snapshot replaces old replica");
        }),
        Case("remote.runtime.unknown-call-is-ignored-and-live-id-reuse-closes", async () =>
        {
            var type = R1Type(); await using var host = R1Host(R1Server.Address()); await host.LoadAsync([R1Plugin(type)], ChordContext.Background);
            var pair = MemoryLinkPair.Create(); using var raw = new R1Peer(pair.Second); host.Accept(pair.First, new() { Principal = R1User }, ChordContext.Background);
            for (var i = 0; i < 300; i++) raw.Send(new CallMessage(i, 999, 0));
            raw.Send(R1Hold(type)); await raw.Next<BoundMessage>(); Check(!raw.Closed, "unknown calls neither count nor close");
            raw.Send(R1Hold(type)); await R1Until(() => raw.Closed);
        }),
        Case("remote.runtime.compressed-child-args-and-origin-are-protocol-errors", async () =>
        {
            foreach (var origin in new[] { false, true })
            {
                var type = R1Type(); await using var host = R1Host(R1Server.Address()); await host.LoadAsync([R1Plugin(type)], ChordContext.Background);
                var pair = MemoryLinkPair.Create(); using var raw = new R1Peer(pair.Second); host.Accept(pair.First, new() { Principal = R1User }, ChordContext.Background);
                raw.Send(R1Hold(type)); await raw.Next<BoundMessage>();
                if (origin) raw.Send(new AckMessage { Origin = 9 });
                else raw.Send(new CallMessage(0, 0, 0, new Part.Deflated(NativeCompression.Instance.Deflate(System.Text.Encoding.UTF8.GetBytes("null")))));
                await R1Until(() => raw.Closed);
            }
        }),
        Case("remote.runtime.revocation-protects-own-principal-and-parent", async () =>
        {
            var type = R1Type(); var selectors = 0; await using var host = R1Host(R1Server.Address(), revoke: p => { selectors++; return [p["id"]]; }); await host.LoadAsync([R1Plugin(type)], ChordContext.Background);
            var pair = MemoryLinkPair.Create(); using var raw = new R1Peer(pair.Second); host.Connect(pair.First, new() { Principal = new() { ["kind"] = "user", ["id"] = 12 } }, ChordContext.Background);
            raw.Send(R1Hold(type)); await raw.Next<BoundMessage>();
            host.Revoke(JsonValue.Create(13), ChordContext.Background);
            host.Revoke(JsonValue.Create(12), ChordContext.Background); await raw.Next<GoneMessage>(); Check(!raw.Closed, "parent link remains open");
            Equal(1, selectors, "revocation selector cached per principal identity");
            using var own = await host.GetAsync(type, new(), ChordContext.Background); host.Revoke(JsonValue.Create(7), ChordContext.Background);
            Equal("live", own.State.Value.Kind, "own principal is never implicitly revoked");
        }),
        Case("remote.delta.staged-decoder-and-all-ops", () =>
        {
            var encoder = new WireDeltaEncoder(); var decoder = new WireDeltaDecoder();
            ImmutableArray<Op> ops = [new SetOp(["s"], JsonValue.Create("ab")), new AppendOp(["s"], "cd"), new TruncateOp(["s"], 1), new SpliceOp(["a"], 1, 1, [JsonValue.Create(9)]), new MoveOp(["a"], [1, 0]), new DeleteOp(["unused"])];
            var wire = encoder.Encode(ops); var staged = decoder.Clone(); var decoded = staged.Decode(wire);
            var value = DeltaApply.Apply(new JsonObject { ["s"] = "", ["a"] = new JsonArray(1, 2), ["unused"] = 5 }, new JsonArray(decoded.Select(x => (JsonNode?)x.ToJson()).ToArray()));
            Equal("bcd", value!["s"]!.GetValue<string>(), "append truncate"); Equal("[9,1]", value["a"]!.ToJsonString(), "splice move");
            var other = decoder.Clone(); Throws<ArgumentException>(() => other.Decode(JsonNode.Parse("[[\"s\",0,1]]")), "unadopted dictionary not visible");
        }),
        Case("remote.runtime.call-window-queued-cancel-frees-no-extra-slot", async () =>
        {
            var type = R1Type(); await using var host = R1Host(); await host.LoadAsync([], ChordContext.Background);
            var pair = MemoryLinkPair.Create(); using var raw = new R1Peer(pair.Second, calls: 1); host.Connect(pair.First, new() { Principal = R1User }, ChordContext.Background);
            using var reference = host.Hold(type, new(), ChordContext.Background); var held = await raw.Next<HoldMessage>(); raw.Send(new BoundMessage([R1Bound(held.Hold, 0)]));
            await R1Until(() => reference.State.Value.Kind == "live");
            var first = reference.CallAsync("who", null, ChordContext.Background); var sent = await raw.Next<CallMessage>();
            using var cancellation = new CancellationTokenSource(); var second = reference.CallAsync("who", null, ChordContext.WithAbortSignal(cancellation.Token, ChordContext.Background));
            cancellation.Cancel(); try { await second; throw new Exception("queued cancellation succeeded"); } catch (Exception error) when (error is ChordError or OperationCanceledException) { }
            var third = reference.CallAsync("who", null, ChordContext.Background);
            Check(!raw.All.OfType<CallMessage>().Any(), "window still full after queued cancellation");
            raw.Send(new ResultMessage(sent.Call)); await first; var next = await raw.Next<CallMessage>(); Equal(sent.Call + 1, next.Call, "queued canceled call never allocated an id");
            raw.Send(new ResultMessage(next.Call)); await third;
        }),
        Case("remote.runtime.byte-leaves-round-trip-real-host", async () =>
        {
            var type = Contract.Remote("remote.bytes", new()
            {
                Version = 1, Params = new(), HostAddress = _ => R1Server.Address(),
                Variants = new() { ["v"] = new() { Methods = new() { ["echo"] = Method(Bytes(), Bytes()) } } },
            });
            await using var server = R1Host(R1Server.Address()); await using var client = R1Host();
            await server.LoadAsync([new("echo", Facet.Define((h, _) => h.Serve(type, (_, _, _) => new(new Dictionary<string, object?>
                { ["echo"] = (HostMethod)((args, _) => Task.FromResult(args)) }, "v"))))], ChordContext.Background);
            await client.LoadAsync([], ChordContext.Background); var pair = MemoryLinkPair.Create();
            server.Accept(pair.First, new() { Principal = R1User }, ChordContext.Background); client.Connect(pair.Second, new() { Principal = R1User }, ChordContext.Background);
            using var reference = await client.GetAsync(type, new(), ChordContext.Background); var bytes = new byte[] { 0, 1, 127, 255 };
            var reply = (ReadOnlyMemory<byte>)(await reference.CallAsync("echo", JsonValue.Create(bytes), ChordContext.Background))!;
            Sequence(bytes, reply.ToArray(), "raw byte segments round trip through validated host args");
        }),
        Case("remote.runtime.variant-member-disappears-and-recovers", async () =>
        {
            var type = Contract.Remote("remote.variants", new()
            {
                Version = 1, Params = new(), HostAddress = _ => R1Server.Address(),
                Variants = new() { ["editor"] = new() { State = new() { ["value"] = Obj(("n", Int())) } }, ["viewer"] = new() },
            });
            await using var host = R1Host(); await host.LoadAsync([], ChordContext.Background); var pair = MemoryLinkPair.Create(); using var raw = new R1Peer(pair.Second);
            host.Connect(pair.First, new() { Principal = R1User }, ChordContext.Background); using var reference = host.Hold(type, new(), ChordContext.Background);
            var held = await raw.Next<HoldMessage>(); raw.Send(new BoundMessage([R1Bound(held.Hold, 1)])); await R1Until(() => reference.State.Value.Kind == "live");
            var member = reference.Member("value"); Equal(1, member.Value!["n"]!.GetValue<int>(), "initial member");
            raw.Send(new BoundMessage([new BoundEntry(held.Hold, 1, [], [])])); await R1Until(() => reference.Variant == "viewer");
            Throws<ChordError>(() => _ = member.Value, "removed member read fails");
            raw.Send(new BoundMessage([R1Bound(held.Hold, 3)])); await R1Until(() => reference.Variant == "editor");
            Equal(3, member.Value!["n"]!.GetValue<int>(), "reappearing member adopts replacement");
        }),
        Case("remote.runtime.swap-keeps-old-call-while-bound-waits", async () =>
        {
            var type = R1Type(); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var definition = StateDefinition.Define("swap-count", new() { Version = 1, Schema = Obj(("n", Int())), Initial = () => new() { ["n"] = 0 } });
            PluginFacet Plugin(string version) => new("counter", Facet.Define((f, _) => f.Serve(type, (_, _, self) =>
            {
                var state = self.State(definition);
                return new(new Dictionary<string, object?>
                {
                    ["value"] = state,
                    ["inc"] = (HostMethod)(async (_, ctx) => { await state.ChangeAsync(d => d["n"] = d["n"]!.GetValue<int>() + 1, ctx.Context); return null; }),
                    ["who"] = (HostMethod)(async (_, _) => { if (version == "old") { started.TrySetResult(); await finish.Task; } return JsonValue.Create(version); }),
                }, "editor");
            })));
            await using var host = R1Host(R1Server.Address()); await host.LoadAsync([Plugin("old")], ChordContext.Background);
            var pair = MemoryLinkPair.Create(); using var raw = new R1Peer(pair.Second); host.Accept(pair.First, new() { Principal = R1User }, ChordContext.Background);
            raw.Send(R1Hold(type)); await raw.Next<BoundMessage>(); raw.Send(new CallMessage(0, 0, 0)); await raw.Next<ResultMessage>(); await raw.Next<StateMessage>();
            raw.Send(new CallMessage(1, 0, 1)); await started.Task;
            // Pi 1.5.2 reports readiness while the admitted old call still drains.
            await host.ReloadAsync([Plugin("new")], ChordContext.Background);
            Check(!raw.All.OfType<ResultMessage>().Any(x => x.Call == 1), "readiness preserves the pending old call");
            Check(!raw.All.OfType<BoundMessage>().Any(), "new bound waits for slot at readiness");
            finish.TrySetResult(); Equal("old", Parts.Parse((await raw.Next<ResultMessage>(x => x.Call == 1)).Value!)!.GetValue<string>(), "old call drains");
            Check(!raw.All.OfType<BoundMessage>().Any(), "new bound waits for slot");
            raw.Send(new CallMessage(2, 0, 1)); Equal("new", Parts.Parse((await raw.Next<ResultMessage>(x => x.Call == 2)).Value!)!.GetValue<string>(), "old indexed names dispatch new call");
            raw.Send(new AckMessage()); await raw.Next<BoundMessage>();
        }),
        Case("remote.runtime.missing-own-address-provider-never-forwards", async () =>
        {
            var type = R1Type(); await using var host = R1Host(R1Server.Address()); await host.LoadAsync([], ChordContext.Background);
            var pair = MemoryLinkPair.Create(); using var raw = new R1Peer(pair.Second); host.Connect(pair.First, new() { Principal = R1User }, ChordContext.Background);
            using var reference = host.Hold(type, new(), ChordContext.Background); await R1Until(() => reference.State.Value.Kind == "failed");
            Check(!raw.All.OfType<HoldMessage>().Any(), "own address cannot escape to parent");
        }),
        Case("remote.runtime.malformed-owned-params-closes-even-if-type-unknown", async () =>
        {
            var errors = new List<HostError>(); var type = R1Type(); await using var host = R1Host(R1Server.Address(), errors); await host.LoadAsync([], ChordContext.Background);
            var pair = MemoryLinkPair.Create(); using var raw = new R1Peer(pair.Second); host.Accept(pair.First, new() { Principal = R1User }, ChordContext.Background);
            raw.Send(R1Hold(type) with { Params = new Part.Text("{") }); await R1Until(() => raw.Closed);
            Check(errors.Any(x => x.Kind == "protocol_error"), "malformed owned JSON is a protocol error");
        }),
        Case("remote.runtime.withdrawn-hold-releases-old-factory-capture", async () =>
        {
            var type = R1Type(); await using var host = R1Host(R1Server.Address()); var capture = R1InstallCapturedFactory(host, type);
            var pair = MemoryLinkPair.Create(); using var raw = new R1Peer(pair.Second); host.Accept(pair.First, new() { Principal = R1User }, ChordContext.Background);
            raw.Send(R1Hold(type)); await raw.Next<BoundMessage>();
            await host.ReloadAsync([], ChordContext.Background); await raw.Next<FailedMessage>();
            Check(!raw.Closed, "withdrawn request remains waiting on its link");
            await ChordSupport.ExpectCollected(capture);
        }),
        Case("remote.runtime.parked-swap-forwards-old-admitted-state", async () =>
        {
            var type = R1Type();
            static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = Signal(); var commit = Signal(); var committed = Signal(); var finish = Signal(); var attaching = Signal(); var attached = Signal();
            var definition = StateDefinition.Define("parked-count", new() { Version = 1, Schema = Obj(("n", Int())), Initial = () => new() { ["n"] = 0 } });
            PluginFacet Plugin(bool replacement) => new("counter", Facet.Define((f, _) => f.Serve(type, (_, _, self) =>
            {
                var state = self.State(definition);
                if (replacement) self.Follow(async (_, _) => { attaching.TrySetResult(); await attached.Task; return new FollowAttachment(null, () => { }); });
                return new(new Dictionary<string, object?>
                {
                    ["value"] = state,
                    ["inc"] = (HostMethod)(async (_, context) =>
                    {
                        if (!replacement)
                        {
                            started.TrySetResult(); await commit.Task;
                            await state.ChangeAsync(d => d["n"] = 1, context.Context);
                            committed.TrySetResult(); await finish.Task;
                        }
                        return null;
                    }),
                    ["who"] = (HostMethod)((_, _) => Task.FromResult<object?>(null)),
                }, "editor");
            })));
            await using var host = R1Host(R1Server.Address()); await host.LoadAsync([Plugin(false)], ChordContext.Background);
            var pair = MemoryLinkPair.Create(); using var raw = new R1Peer(pair.Second); host.Accept(pair.First, new() { Principal = R1User }, ChordContext.Background);
            try
            {
                raw.Send(R1Hold(type)); await raw.Next<BoundMessage>(); raw.Send(new AckMessage());
                raw.Send(new CallMessage(0, 0, 0)); await started.Task;
                var reload = host.ReloadAsync([Plugin(true)], ChordContext.Background);
                Check(!attaching.Task.IsCompleted, "successor waits for old call drain");
                commit.TrySetResult(); await committed.Task;
                var changed = await raw.Next<StateMessage>();
                var ops = new WireDeltaDecoder().Decode(Parts.Parse(changed.Holds.Single().Members.Single().Ops));
                Equal(1, DeltaApply.Apply(new JsonObject { ["n"] = 0 }, new JsonArray(ops.Select(op => (JsonNode?)op.ToJson()).ToArray()))!["n"]!.GetValue<int>(), "old admitted commit crosses reload drain");
                Check(!raw.All.OfType<LoadingMessage>().Any(), "old bound instance remains attached while its admitted call runs");
                finish.TrySetResult(); await attaching.Task;
                var report = await reload;
                Equal("restarted", report.Objects.Single().Fate, "reload reports restart before successor becomes live");
                Check(!attached.Task.IsCompleted, "successor remains parked on its attachment after the report");
                attached.TrySetResult(); await raw.Next<ResultMessage>(); await raw.Next<BoundMessage>();
            }
            finally { commit.TrySetResult(); finish.TrySetResult(); attached.TrySetResult(); }
        }),
    ];
}
