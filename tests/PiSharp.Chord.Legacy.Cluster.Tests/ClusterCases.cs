using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using PiSharp.Chord.Legacy;
using static PiSharp.Chord.Contracts.Schemas;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> ClusterCases()
    {
        yield return ("cluster.actual-host-routes-and-reloads-existing-reference", async () =>
        {
            var contract = LegacyContract.Remote("cluster.greeter", 1, Obj(), LegacyPlacement.Named("worker"),
                new Dictionary<string, IReadOnlyDictionary<string, LegacyMember>> { ["main"] = new Dictionary<string, LegacyMember> { ["greet"] = LegacyMember.Method(Str(), Str()) } });
            LegacyFacet Facet(string greeting) => new("greeter", api => api.Serve(contract, (_, _, _) =>
                new(new Dictionary<string, object?> { ["greet"] = (LegacyMethod)((args, _) => Task.FromResult<object?>(JsonValue.Create(greeting + args!.GetValue<string>()))) }, "main")));
            var active = Facet("v1 ");
            await using var server = new LegacyHost(new() { Name = "server", Self = JsonValue.Create("server"), ReadyTimeout = TimeSpan.FromSeconds(3) });
            await using var worker = new LegacyHost(new() { Name = "worker", Self = JsonValue.Create("worker"), ReadyTimeout = TimeSpan.FromSeconds(3),
                Loader = (_, _) => Task.FromResult<IReadOnlyList<LegacyFacet>>([active]) });
            await Stage(worker.LoadAsync([active]), "initial worker load");
            var links = LegacyMemoryLink.Pair(); var peer = server.Accept(links.A, new() { Kind = "host", Name = "worker", Principal = JsonValue.Create("worker") });
            worker.Connect(links.B, JsonValue.Create("server")); await Stage(peer.WhenSyncedAsync(), "initial peer synchronization");
            using var reference = await Stage(server.GetAsync(contract, new JsonObject()), "initial routed reference");
            Check((await Stage(reference.CallAsync("greet", JsonValue.Create("A")), "initial routed call") as JsonNode)?.GetValue<string>() == "v1 A", "Real worker did not serve call.");
            active = Facet("v2 ");
            var report = await Stage(server.ReloadAsync([], new() { Remote = new Dictionary<string, IReadOnlyList<string>> { ["work"] = ["greeter"] }, VoteDeadline = TimeSpan.FromSeconds(3), GateDeadline = TimeSpan.FromSeconds(1), RetireDeadline = TimeSpan.FromSeconds(1) }), "cluster reload");
            Check(report.Children?.ContainsKey("worker") == true, "Actual worker's prepare vote missing.");
            Check((await Stage(reference.CallAsync("greet", JsonValue.Create("B")), "post-reload routed call") as JsonNode)?.GetValue<string>() == "v2 B", "Existing reference did not cross real reload.");
            Check(server.CodeVersion == worker.CodeVersion, "Actual cluster code versions diverged.");
            await Stage(server.CloseAsync(), "server close"); await Stage(worker.CloseAsync(), "worker close");
        });
        yield return ("cluster.parent-first-fifo-commit-and-prefix", async () =>
        {
            var trace = new ConcurrentQueue<string>();
            var serverHost = new ReloadHost("server", trace); var childHost = new ReloadHost("session:S1", trace);
            using var server = new LegacyNetwork(serverHost); using var child = new LegacyNetwork(childHost);
            var links = LegacyMemoryLink.Pair(); var down = server.Accept(links.A, new() { Kind = "host", Name = childHost.Name });
            var up = child.Connect(links.B, new JsonObject()); await down.WhenSyncedAsync();
            var called = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            up.Connection.Received += frame => { if (frame.Type == "call") called.TrySetResult(childHost.CodeVersion); };
            serverHost.NewWork = () => down.Connection.Send(LegacyProtocol.Frame("call", ("id", JsonValue.Create(1)), ("hold", JsonValue.Create(1)), ("member", JsonValue.Create("greet"))));
            var report = await server.Cluster.ReloadAsync(["local"], new() { Remote = new Dictionary<string, IReadOnlyList<string>> { ["session:"] = ["broad"], ["session:S"] = ["specific"] } });
            Check(report.CodeVersion == 2 && report.Children?.Count == 1, "Participant vote missing from report.");
            Check(await called.Task == 2, "New-code call overtook child commit.");
            Check(childHost.Modules.SequenceEqual(["specific"]), "Longest prefix did not win.");
            Check(Array.IndexOf(trace.ToArray(), "server:commit") < Array.IndexOf(trace.ToArray(), "session:S1:commit"), "Child committed before parent.");
        });
        yield return ("cluster.vote-failure-rolls-every-host-back", async () =>
        {
            var trace = new ConcurrentQueue<string>(); var a = new ReloadHost("server", trace); var b = new ReloadHost("worker", trace) { FailPrepare = true };
            using var server = new LegacyNetwork(a); using var child = new LegacyNetwork(b);
            var (down, up) = LegacyMemoryLink.Pair(); var peer = server.Accept(down, new() { Kind = "host", Name = "worker" }); child.Connect(up, null); await peer.WhenSyncedAsync();
            var error = await Throws<ChordException>(() => server.Cluster.ReloadAsync([]));
            Check(error.Message.Contains("worker") && error.Code == "unavailable", "Vote error lost host/cause.");
            await Eventually(() => !b.Reloading);
            Check(a.CodeVersion == 1 && b.CodeVersion == 1 && trace.Contains("server:rollback"), "Failed vote committed code.");
        });
        yield return ("cluster.vote-timeout-aborts-loading-child-and-can-retry", async () =>
        {
            var trace = new ConcurrentQueue<string>(); var a = new ReloadHost("server", trace); var b = new ReloadHost("worker", trace);
            var slow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); b.Load = () => slow.Task;
            using var server = new LegacyNetwork(a); using var child = new LegacyNetwork(b);
            var (down, up) = LegacyMemoryLink.Pair(); var peer = server.Accept(down, new() { Kind = "host", Name = "worker" }); child.Connect(up, null); await peer.WhenSyncedAsync();
            var error = await Throws<ChordException>(() => server.Cluster.ReloadAsync([], new() { VoteDeadline = TimeSpan.FromMilliseconds(80) }));
            Check(error.Reason == "vote_deadline", "Missing vote was not timed out."); slow.SetResult();
            await Eventually(() => !b.Reloading); b.Load = null;
            await server.Cluster.ReloadAsync([]); await Eventually(() => b.CodeVersion == 2);
            Check(a.CodeVersion == 2, "Aborted loading left reload slot occupied.");
        });
        yield return ("cluster.late-child-fenced-and-reconciled", async () =>
        {
            var trace = new ConcurrentQueue<string>(); var a = new ReloadHost("server", trace); var b = new ReloadHost("worker", trace);
            using var server = new LegacyNetwork(a); using var child = new LegacyNetwork(b);
            var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); a.Load = () => blocked.Task;
            var loading = server.Cluster.ReloadAsync([], new() { Remote = new Dictionary<string, IReadOnlyList<string>> { ["worker"] = ["new-worker"] } });
            await Eventually(() => a.Reloading);
            var links = LegacyMemoryLink.Pair(); var peer = server.Accept(links.A, new() { Kind = "host", Name = "worker" }); child.Connect(links.B, null);
            await Task.Delay(30); Check(!peer.Synced, "Late peer was routed before reload end.");
            blocked.SetResult(); var report = await loading; await peer.WhenSyncedAsync();
            // Synchronization permits sending: the FIFO commit may still be in transit until
            // the child processes it. Direct inspection of a separate host needs its own barrier.
            await Eventually(() => b.CodeVersion == 2);
            Check(report.Children?.Count == 0 && b.CodeVersion == 2 && b.Modules.SequenceEqual(["new-worker"]), "Late peer was relabeled without reloading modules.");
            Check(b.AllowedRetirement >= 1, "Late peer did not receive retirement watermark.");
        });
        yield return ("cluster.ahead-child-refused-and-client-not-participant", async () =>
        {
            var a = new ReloadHost("server", new()); using var server = new LegacyNetwork(a);
            var links = LegacyMemoryLink.Pair(); var ahead = server.Accept(links.A, new() { Kind = "host", Name = "ahead" });
            using var remote = new LegacyConnection(links.B, new() { Name = "ahead", Role = "upstream", CodeVersion = 7, PeerKind = "host" });
            await Eventually(() => !ahead.Connection.Open);
            var clients = LegacyMemoryLink.Pair(); server.Accept(clients.A, new());
            using var browser = new LegacyConnection(clients.B, new() { Name = "browser", Kind = "client", Role = "upstream", PeerKind = "host" });
            var result = await server.Cluster.ReloadAsync([]); Check(result.Children?.Count == 0, "Browser participated in reload.");
        });
        yield return ("cluster.discovery-reauthorizes-and-admits-expired-request", async () =>
        {
            var a = new ReloadHost("server", new()); var b = new ReloadHost("worker", new());
            using var server = new LegacyNetwork(a); using var worker = new LegacyNetwork(b);
            var allowed = true; var calls = 0; var links = LegacyMemoryLink.Pair(); worker.Connect(links.B, null);
            server.Route("work", new() { Authorize = (_, _) => Task.FromResult(allowed), Principal = _ => new JsonObject { ["trusted"] = true }, Resolve = _ => { calls++; allowed = false; return Task.FromResult<ILegacyLink>(links.A); } });
            await Throws<ChordException>(() => server.ResolveAsync("worker", null));
            Check(calls == 1 && server.Peers.Count == 1, "Spawned peer was abandoned after permission changed.");
            Check(server.Peers[0].Connection.Principal?["trusted"]?.GetValue<bool>() == true, "Configured service principal lost.");
            var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Throws<OperationCanceledException>(() => server.ResolveRelayAsync("worker", null, cancellationToken: cancelled.Token));
            Check(calls == 1, "Cancelled request spawned again.");
        });
        yield return ("cluster.reconnect-replaces-parent-with-isolated-close", async () =>
        {
            var a = new ReloadHost("server", new()); var b = new ReloadHost("worker", new());
            using var server = new LegacyNetwork(a); using var worker = new LegacyNetwork(b);
            var first = LegacyMemoryLink.Pair(); server.Accept(first.A, new() { Kind = "host", Name = "worker" }); var old = worker.Connect(first.B, null);
            var second = LegacyMemoryLink.Pair(); server.Accept(second.A, new() { Kind = "host", Name = "worker" }); var current = worker.Connect(second.B, null);
            await current.WhenSyncedAsync(); await Eventually(() => !old.Connection.Open);
            Check(ReferenceEquals(worker.Upstream, current) && current.Connection.Open, "Old parent close cleared replacement.");
            var strangerLinks = LegacyMemoryLink.Pair(); var stranger = server.Accept(strangerLinks.A, new()); strangerLinks.B.Close();
            await Eventually(() => !stranger.Connection.Open); Check(current.Connection.Open, "Unrelated close killed healthy peer.");
        });
        yield return ("cluster.catchup-merges-modules-and-removal-tombstones", async () =>
        {
            var a = new ReloadHost("server", new()); var b = new ReloadHost("worker", new());
            using var server = new LegacyNetwork(a); using var worker = new LegacyNetwork(b);
            await server.Cluster.ReloadAsync([], new() { Remote = new Dictionary<string, IReadOnlyList<string>> { ["worker"] = ["a"] } });
            await server.Cluster.ReloadAsync([], new() { Remote = new Dictionary<string, IReadOnlyList<string>> { ["worker"] = ["b"] } });
            await server.Cluster.ReloadAsync([], new() { RemoteRemove = new Dictionary<string, IReadOnlyList<string>> { ["worker"] = ["a"] } });
            var links = LegacyMemoryLink.Pair(); var peer = server.Accept(links.A, new() { Kind = "host", Name = "worker" }); worker.Connect(links.B, null);
            await peer.WhenSyncedAsync();
            Check(b.Modules.SequenceEqual(["b"]) && b.LastOptions.Remove.SequenceEqual(["a"]), "Catch-up lost module union or removal tombstone.");
        });
        yield return ("cluster.invalidates-or-restamps-only-matching-peer", async () =>
        {
            using var server = new LegacyNetwork(new ReloadHost("server", new()));
            var a = LegacyMemoryLink.Pair(); var b = LegacyMemoryLink.Pair(); var c = LegacyMemoryLink.Pair();
            var restamped = server.Accept(a.A, new() { Principal = new JsonObject { ["grant"] = 1 }, Restamp = _ => new JsonObject { ["grant"] = 2 } });
            var closed = server.Accept(b.A, new() { Principal = new JsonObject { ["grant"] = 1 } });
            var untouched = server.Accept(c.A, new() { Principal = new JsonObject { ["grant"] = 9 } });
            server.Invalidate(principal => principal?["grant"]?.GetValue<int>() == 1);
            await Eventually(() => restamped.Connection.Principal?["grant"]?.GetValue<int>() == 2 && !closed.Connection.Open);
            Check(restamped.Connection.Open && untouched.Connection.Open, "Invalidation closed a reauthorized or unrelated peer.");
            a.B.Close(); b.B.Close(); c.B.Close();
        });
    }
    private static async Task Stage(Task action, string stage)
    { try { await action.WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException error) { Console.WriteLine("TRACE cluster stage timed out: " + stage); throw new TimeoutException("Cluster stage timed out: " + stage, error); } }
    private static async Task<T> Stage<T>(Task<T> action, string stage)
    { try { return await action.WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException error) { Console.WriteLine("TRACE cluster stage timed out: " + stage); throw new TimeoutException("Cluster stage timed out: " + stage, error); } }
    private sealed class ReloadHost(string name, ConcurrentQueue<string> trace) : ILegacyReloadHost
    {
        public string Name => name;
        public string Kind => "host";
        public int CodeVersion { get; private set; } = 1;
        public bool Reloading { get; private set; }
        public int HighestRetiredCodeVersion { get; private set; }
        public event Action<int>? Retired;
        public int AllowedRetirement { get; private set; }
        public bool FailPrepare;
        public Func<Task>? Load;
        public Action? NewWork;
        public IReadOnlyList<string> Modules = [];
        public LegacyReloadOptions LastOptions = new();
        public async Task<ILegacyPreparedReload> BeginReloadAsync(IReadOnlyList<string> modules, LegacyReloadOptions options, int? codeVersion = null, CancellationToken cancellationToken = default)
        {
            if (Reloading) throw new ChordException("unavailable", "already reloading", "reload_running");
            Reloading = true; Modules = modules; LastOptions = options;
            if (Load is not null) await Load();
            return new Candidate(this, codeVersion ?? CodeVersion + 1, trace);
        }
        public void EndReload() => Reloading = false;
        public void AllowRetirement(int version) => AllowedRetirement = Math.Max(AllowedRetirement, version);
        private sealed class Candidate(ReloadHost host, int version, ConcurrentQueue<string> trace) : ILegacyPreparedReload
        {
            private bool rolledBack;
            public Task WaitForRetirementAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task PrepareAsync(CancellationToken cancellationToken = default)
            { cancellationToken.ThrowIfCancellationRequested(); trace.Enqueue(host.Name + ":prepare"); if (host.FailPrepare) throw new ChordException("unavailable", "factory failed"); return Task.CompletedTask; }
            public LegacyReloadReport Summary() => new() { CodeVersion = version, Previous = version - 1, Changed = host.Modules };
            public LegacyReloadReport Commit(Action? beforeNewWork = null)
            {
                host.CodeVersion = version; trace.Enqueue(host.Name + ":commit"); beforeNewWork?.Invoke(); host.NewWork?.Invoke();
                host.HighestRetiredCodeVersion = version - 1; host.Retired?.Invoke(version - 1); return Summary();
            }
            public Task RollbackAsync() { if (!rolledBack) { rolledBack = true; trace.Enqueue(host.Name + ":rollback"); } return Task.CompletedTask; }
        }
    }
}
