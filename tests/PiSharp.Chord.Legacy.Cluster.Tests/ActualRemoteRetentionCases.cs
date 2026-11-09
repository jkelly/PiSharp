using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using PiSharp.Chord.Legacy;
using static PiSharp.Chord.Contracts.Schemas;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> ActualRemoteRetentionCases()
    {
        yield return ("retention.actual-remote-linger-reacquire-and-lru", async () =>
        {
            var opened = new ConcurrentQueue<int>(); var closed = new ConcurrentQueue<int>();
            var type = LegacyContract.Remote("remote-retention", 1, Obj(("id", Int())), LegacyPlacement.Named("worker"),
                new Dictionary<string, IReadOnlyDictionary<string, LegacyMember>> { ["main"] = new Dictionary<string, LegacyMember> { ["id"] = LegacyMember.State(Int()) } });
            await using var server = new LegacyHost(new() { Name = "server", Self = JsonValue.Create("server"), ReadyTimeout = TimeSpan.FromSeconds(3),
                RemoteLingerTypes = new Dictionary<string, TimeSpan> { [type.Name] = TimeSpan.FromSeconds(5) }, RemoteLingerCap = 2 });
            await using var worker = new LegacyHost(new() { Name = "worker", Self = JsonValue.Create("worker"), ReadyTimeout = TimeSpan.FromSeconds(3) });
            worker.Serve(type, (input, _, self) =>
            {
                var id = input!["id"]!.GetValue<int>();
                var state = self.State("id", Int(), JsonValue.Create(id));
                self.Resource("lifetime", new LegacyResourceDeclaration<int> { Open = _ => { opened.Enqueue(id); return Task.FromResult(id); },
                    Close = value => { closed.Enqueue(value); return Task.CompletedTask; } });
                return new(new Dictionary<string, object?> { ["id"] = state }, "main");
            });
            var links = LegacyMemoryLink.Pair(); var peer = server.Accept(links.A, new() { Kind = "host", Name = "worker", Principal = JsonValue.Create("worker") });
            worker.Connect(links.B, JsonValue.Create("server")); await peer.WhenSyncedAsync();
            var first = await server.GetAsync(type, new JsonObject { ["id"] = 1 }); first.Release();
            var revived = await server.GetAsync(type, new JsonObject { ["id"] = 1 }); revived.Release();
            Check(opened.Count(value => value == 1) == 1 && closed.IsEmpty, "Remote linger reacquire restarted provider.");
            (await server.GetAsync(type, new JsonObject { ["id"] = 2 })).Release();
            (await server.GetAsync(type, new JsonObject { ["id"] = 3 })).Release();
            await Eventually(() => closed.Contains(1));
            Check(!closed.Contains(2) && !closed.Contains(3), "LRU cap released the wrong providers.");
            await server.CloseAsync(); await Eventually(() => closed.Contains(2) && closed.Contains(3));
            Check(closed.Count == 3, "Provider lifetime closed more than once.");
        });
        yield return ("retention.actual-upstream-reconnect-reholds-again", async () =>
        {
            var type = LegacyContract.Remote("reconnect", 1, Obj(), LegacyPlacement.Named("server"),
                new Dictionary<string, IReadOnlyDictionary<string, LegacyMember>> { ["main"] = new Dictionary<string, LegacyMember> { ["read"] = LegacyMember.Method(null, Str()) } });
            await using var server = new LegacyHost(new() { Name = "server", Self = JsonValue.Create("server") });
            await using var client = new LegacyHost(new() { Name = "client", Kind = "client", Self = JsonValue.Create("client"), ReadyTimeout = TimeSpan.FromSeconds(3) });
            server.Serve(type, (_, _, _) => new(new Dictionary<string, object?> { ["read"] = (LegacyMethod)((_, _) => Task.FromResult<object?>(JsonValue.Create("alive"))) }, "main"));
            var first = LegacyMemoryLink.Pair(); server.Accept(first.A, new() { Principal = JsonValue.Create("client") }); client.Connect(first.B, JsonValue.Create("server"));
            using var reference = await client.GetAsync(type, new JsonObject());
            first.B.Close("offline"); await Eventually(() => reference.Status == "loading");
            var second = LegacyMemoryLink.Pair(); var observed = new ObservedLink(second.B);
            server.Accept(second.A, new() { Principal = JsonValue.Create("client") }); client.Connect(observed, JsonValue.Create("server"));
            Check((await reference.CallAsync("read") as JsonNode)?.GetValue<string>() == "alive", "Existing reference did not recover after manual reconnect.");
            Check(observed.Sent.Any(frame => frame.Type == "hold" && frame.Is("again", true)), "Reheld hydrated reference did not send protocol4 again marker.");
        });
    }
    private sealed class ObservedLink(ILegacyLink inner) : ILegacyLink
    {
        internal ConcurrentQueue<LegacyFrame> Sent { get; } = new();
        public bool Closed => inner.Closed;
        public long? Send(LegacyFrame frame) { Sent.Enqueue(frame); return inner.Send(frame); }
        public void Receive(Action<LegacyFrame> handler) => inner.Receive(handler);
        public void OnClose(Action<string> handler) => inner.OnClose(handler);
        public void Close(string reason = "closed") => inner.Close(reason);
    }
}
