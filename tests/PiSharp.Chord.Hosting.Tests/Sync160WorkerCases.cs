// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/node/workers/workers.ts, packages/chord/src/node/workers/process.ts,
// packages/chord/src/tui/scope.ts.
// Authored native regressions from the Pi 1.6.0 tests packages/chord/test/node/workers/workers.test.ts ("passes admit to its route", the
// three cases of "a worker's exit", "a worker's incarnation"), node/workers/process.test.ts ("closes its link at its exit") and
// tui/scope.test.ts ("a scope's event subscriptions"); not an upstream execution capture. The fixtures
// test/node/fixtures/workers/{child,control,worker}.ts are RunChildAsync here (this test assembly run as a real worker process):
// Control's report, reportAndExit and crash, and the start data's reportAtStart. Upstream's holdNextCommit (internal here) is a store
// export holding the commit queue, with the queue's tail (SettledAsync) telling when a commit was queued behind it.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Documents;
using PiSharp.Chord.Host;
using PiSharp.Chord.Hosting.App;
using PiSharp.Chord.Hosting.Plugins;
using PiSharp.Chord.Hosting.Terminal;
using PiSharp.Chord.Hosting.Workers;
using PiSharp.Chord.Link;
using PiSharp.Chord.State;
using PiSharp.Chord.Store;
using static PiSharp.Chord.Contracts.Schemas;

internal static class Sync160WorkerCases
{
    private static readonly ChordContext Context = ChordContext.Background;
    /// <summary>The host kind of the fixture workers.</summary>
    private static readonly HostKind Session = new("w160.session", new() { ["id"] = Str() });
    /// <summary>The server's own kind.</summary>
    private static readonly HostKind Server = new("w160.server");
    /// <summary>What a worker reports to the server.</summary>
    private static readonly Contract Reports = Contract.Remote("w160.reports", new()
    {
        Version = 1, Params = [], HostAddress = _ => Server.Address(),
        Variants = new() { ["main"] = new() { Methods = new() { ["report"] = Method(Str()) } } },
    });
    /// <summary>What the fixture worker does on request, at its own address.</summary>
    private static readonly Contract Control = Contract.Remote("w160.control", new()
    {
        Version = 1, Params = new() { ["id"] = Str() }, HostAddress = p => Session.Address(p),
        Variants = new()
        {
            ["main"] = new()
            {
                Methods = new()
                {
                    ["pid"] = Method(null, Int()),
                    // Exits with this code shortly after answering.
                    ["crash"] = Method(Int()),
                    // Sends the server a report and returns without waiting for its answer.
                    ["report"] = Method(Str()),
                    // Sends the server a report and exits (code 7) in the same turn.
                    ["reportAndExit"] = Method(Str()),
                },
            },
        },
    });
    /// <summary>The server's journal of its workers' reports.</summary>
    private static readonly Contract Journal = Contract.Local("w160.journal", new()
    { Version = 1, Params = [], Members = new() { ["lines"] = Arr(Str()), ["append"] = Method(Str()) } });
    private static readonly StateDefinition Lines = StateDefinition.Define("w160.lines", new()
    { Schema = Obj(("lines", Arr(Str()))), Version = 1, Initial = () => new JsonObject { ["lines"] = new JsonArray() } });
    private static readonly StateDefinition StoredLines = StateDefinition.Define("w160.stored-lines", new()
    {
        Schema = Obj(("lines", Arr(Str()))), Version = 1, Initial = () => new JsonObject { ["lines"] = new JsonArray() },
        Persist = true, CheckpointWhen = (_, _, _) => false,
    });
    /// <summary>The scope case's notes: a text, a write and the event it emits.</summary>
    private static readonly Contract Notes = Contract.Local("w160.notes", new()
    { Version = 1, Params = [], Members = new() { ["text"] = Str(), ["write"] = Method(Str()), ["written"] = Event(Str()) } });

    private static string Sdk => Environment.GetEnvironmentVariable("PISHARP_TEST_DOTNET") ?? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ??
        (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase) ? Environment.ProcessPath! : throw new InvalidOperationException("Run with pinned dotnet host"));

    /// <summary>The fixture worker (upstream fixtures/workers/worker.ts): Control at its address; with <c>reportAtStart</c> in its start
    /// data it reports to the server after its load, before it is ready, and waits for the answer.</summary>
    internal static Task<int> RunChildAsync() => WorkerMain.RunAsync(async worker =>
    {
        var host = Hosts.Create(new() { Address = worker.Address(Session), Principal = worker.Start.Principal, OnError = _ => { } });
        // The server's Reports, held from its first use on: a report goes out in the turn that sends it.
        var gate = new object(); Task<IReference>? held = null;
        Task<IReference> ServerReports() { lock (gate) return held ??= host.GetAsync(Reports, new(), ChordContext.Background); }
        async Task<object?> Report(object? arguments, bool exit)
        {
            var reports = await ServerReports();
            // Its answer is the server's to give or not (the worker may be gone by then).
            _ = reports.CallAsync("report", ((JsonNode?)arguments)?.DeepClone(), ChordContext.Background).ContinueWith(_ => { }, TaskScheduler.Default);
            if (exit) Environment.Exit(7);
            return null;
        }
        var facet = Facet.Define((h, _) => h.Serve(Control, (_, _, _) => new(new Dictionary<string, object?>
        {
            ["pid"] = (HostMethod)((_, _) => Task.FromResult<object?>(JsonValue.Create(Environment.ProcessId))),
            ["crash"] = (HostMethod)((arguments, _call) =>
            {
                var code = ((JsonNode)arguments!).GetValue<int>();
                Task.Run(async () => { await Task.Delay(50); Environment.Exit(code); });
                return Task.FromResult<object?>(null);
            }),
            ["report"] = (HostMethod)((arguments, _) => Report(arguments, exit: false)),
            ["reportAndExit"] = (HostMethod)((arguments, _) => Report(arguments, exit: true)),
        }, "main")));
        await worker.LoadPluginsAsync(host, [new("w160.core", facet)]);
        if (worker.Start.Data?["reportAtStart"]?.GetValue<string>() is { } text)
        {
            using var reports = await host.GetAsync(Reports, new(), worker.Context);
            await reports.CallAsync("report", JsonValue.Create(text), worker.Context);
        }
        return host;
    });

    /// <summary>What the server's Reports took: each report as received, and how its transaction ended.</summary>
    private sealed class Log
    {
        internal readonly ConcurrentQueue<string> Received = new(), Outcomes = new();
    }

    /// <summary>A server facet serving Reports into Journal (persisted when <paramref name="persist"/>): a report is received (with what
    /// <paramref name="seen"/> says of its call), waits for <paramref name="before"/>, then appends its text in a transaction under its
    /// call's context; the log gets how that ended (upstream <c>reporting</c>).</summary>
    private static Facet Reporting(Log log, bool persist = false, Task? before = null, Func<string, string>? seen = null) => Facet.Define((h, _) =>
    {
        h.Serve(Journal, (_, _, self) =>
        {
            var lines = self.State(persist ? StoredLines : Lines);
            return new(new Dictionary<string, object?>
            {
                ["lines"] = self.Derive([lines], values => ((JsonNode)values[0]!)["lines"]!.DeepClone()),
                ["append"] = (HostMethod)(async (arguments, ctx) =>
                {
                    var text = ((JsonNode)arguments!).GetValue<string>();
                    await lines.ChangeAsync(draft => ((JsonArray)draft["lines"]!).Add(text), ctx.Context);
                    return null;
                }),
            });
        });
        h.Serve(Reports, (_, _, self) =>
        {
            var journal = self.Get(Journal, new());
            return new(new Dictionary<string, object?>
            {
                ["report"] = (HostMethod)(async (arguments, ctx) =>
                {
                    var text = ((JsonNode)arguments!).GetValue<string>();
                    log.Received.Enqueue(seen is null ? text : $"{text} {seen(ctx.Incarnation)}");
                    if (before is not null) await before;
                    try { await journal.CallAsync("append", JsonValue.Create(text), ctx.Context); log.Outcomes.Enqueue($"{text} committed"); }
                    catch (Exception error) { log.Outcomes.Enqueue($"{text} {Outcome(error)}"); }
                    return null;
                }),
            }, "main");
        });
    });

    /// <summary>How a refused transaction ended: its Chord error code, else "cancelled" for an aborted context.</summary>
    private static string Outcome(Exception error) => error switch
    {
        ChordError chord => chord.Code,
        OperationCanceledException => "cancelled",
        _ => error.GetType().Name,
    };

    /// <summary>A server host (with <paramref name="store"/>) and a workers component over the fixture worker (upstream <c>fleet</c>).</summary>
    private sealed class Fleet : IAsyncDisposable
    {
        internal required ChordHost Server { get; init; }
        internal required ProcessWorkers Workers { get; init; }
        internal required ConcurrentQueue<string> Starts { get; init; }
        internal required ConcurrentQueue<Exception> Errors { get; init; }
        internal required string Root { get; init; }
        internal ConcurrentDictionary<string, JsonObject> Behaviours { get; } = new();
        public async ValueTask DisposeAsync()
        {
            try { await Workers.CloseAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
            finally
            {
                await Server.CloseAsync(Context);
                try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static async Task<Fleet> FleetAsync(IStore? store = null, RouteAdmit? admit = null, IReadOnlyList<PluginFacet>? facets = null,
        Action<Exception>? onError = null, Action<Fleet>? configure = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "chord-w160-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var server = Hosts.Create(new() { Address = Server.Address(), Principal = new() { ["kind"] = "server" }, OnError = _ => { }, Store = store, ReadyTimeout = TimeSpan.FromSeconds(20) });
        var starts = new ConcurrentQueue<string>(); var errors = new ConcurrentQueue<Exception>();
        Fleet? fleet = null;
        var workers = new ProcessWorkers("w160", server, Session, new()
        {
            Executable = Sdk, Arguments = [Assembly.GetExecutingAssembly().Location, "--worker-160-fixture"],
            BaseDirectory = Path.Combine(root, "base"), LockDirectory = Path.Combine(root, "locks"), Store = store, Admit = admit,
            Authorize = (_, _) => true, Principal = address => new() { ["kind"] = "worker", ["id"] = address.Params["id"]!.DeepClone() },
            Plugins = (_, _) => Task.FromResult(new PluginBuildSet([], [])),
            StartData = (address, _) =>
            {
                starts.Enqueue(address.Key);
                return Task.FromResult<JsonNode?>(fleet!.Behaviours.TryGetValue(address.Params["id"]!.GetValue<string>(), out var behaviour) ? behaviour.DeepClone() : null);
            },
            OnError = (_, error) => { errors.Enqueue(error); onError?.Invoke(error); },
            StartTimeout = TimeSpan.FromSeconds(25), CloseTimeout = TimeSpan.FromSeconds(5),
        }, Context);
        fleet = new() { Server = server, Workers = workers, Starts = starts, Errors = errors, Root = root };
        configure?.Invoke(fleet);
        await server.LoadAsync([new("w160.workers", workers.Facet), .. facets ?? []], Context);
        return fleet;
    }

    private static string Key(string id) => Session.Address(("id", JsonValue.Create(id))).Key;
    private static ProcessWorkerState? StateOf(ProcessWorkers workers, string id) => workers.State.GetValueOrDefault(Key(id));
    private static JsonObject Id(string id) => new() { ["id"] = id };
    private static JsonArray LinesOf(IReference journal) => (JsonArray)journal.Member("lines").Value!;

    // workers.test.ts "passes admit to its route: a denied principal starts no worker, an allowed one does (slice 14 §2.3)".
    public static async Task PassesAdmitToItsRoute()
    {
        await using (var denied = await FleetAsync(admit: (_, _, _) => Watchables.Constant(Admission.Deny("no workers for you"))))
        {
            using var refused = denied.Server.Hold(Control, Id("S1"), Context);
            await Until(() => refused.State.Value.Kind == "failed", "the denied hold failed");
            var state = refused.State.Value;
            Check(state.Cause is { Kind: FailureKind.Denied } cause && cause.Error.Code == "forbidden" && cause.Error.Message == "no workers for you",
                $"the hold failed as denied: {state.Cause?.Kind} {state.Cause?.Error.Code} {state.Cause?.Error.Message}");
            await Task.Delay(200);
            // Nothing forked, nothing even asked to start.
            Check(denied.Starts.IsEmpty && StateOf(denied.Workers, "S1") is null, "a denied principal started a worker");
        }
        await using var allowed = await FleetAsync(admit: (_, _, _) => Watchables.Constant(Admission.Allow()));
        using var control = allowed.Server.Hold(Control, Id("S1"), Context);
        await Until(() => control.State.Value.Kind == "live", "the allowed hold is live");
        Check(allowed.Starts.SequenceEqual([Key("S1")]), "the allowed principal started one worker");
        Check(((JsonNode)(await control.CallAsync("pid", null, Context))!).GetValue<int>() != Environment.ProcessId, "the worker runs in its own process");
    }

    // workers.test.ts "a worker that reports and exits in the same turn: its link is closed before its exit is published, no transaction of
    // its reports commits".
    public static async Task ReportAndExitInTheSameTurn()
    {
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new Log();
        // Each report's transaction runs right after the exit was published (the crash is reported then).
        await using var fleet = await FleetAsync(facets: [new("w160.reports", Reporting(log, before: published.Task))],
            onError: error => { if (error is WorkerProcessException { Kind: "worker_failed" }) published.TrySetResult(); });
        using var journal = await fleet.Server.GetAsync(Journal, new(), Context);
        using var control = await fleet.Server.GetAsync(Control, Id("S1"), Context);
        // One report whose handler waits when the worker exits, one sent in the turn of the exit.
        await control.CallAsync("report", JsonValue.Create("waiting"), Context);
        await Until(() => log.Received.Contains("waiting"), "the first report was received");
        _ = control.CallAsync("reportAndExit", JsonValue.Create("last"), Context).ContinueWith(_ => { }, TaskScheduler.Default);
        await Until(() => StateOf(fleet.Workers, "S1")?.Status == "failed", "the exit was published");
        Check(StateOf(fleet.Workers, "S1")?.Error == "worker exited (code 7)", "the exit's public error: " + StateOf(fleet.Workers, "S1")?.Error);
        await Until(() => !log.Outcomes.IsEmpty, "the waiting report's transaction ended");
        await Task.Delay(200);
        // `last` was handled before the exit and aborted with it, or never handled.
        var outcomes = log.Outcomes.Where(outcome => !outcome.StartsWith("last ", StringComparison.Ordinal) || outcome == "last committed").ToArray();
        Check(outcomes.Length == 1 && outcomes[0].StartsWith("waiting ", StringComparison.Ordinal) && outcomes[0] != "waiting committed",
            "an exited worker's report committed: " + string.Join(", ", log.Outcomes));
        Check(LinesOf(journal).Count == 0, "the journal holds an exited worker's report: " + LinesOf(journal).ToJsonString());
        Check(!fleet.Errors.OfType<WorkerProcessException>().Any(error => error.Kind == "worker_link_lost"), "the exit's link close was told as a lost link");
    }

    // workers.test.ts "a commit a report queued in the store before the exit lands before the exit is published; until then the state shows
    // the worker and nothing starts".
    public static async Task QueuedCommitLandsBeforeTheExitIsPublished()
    {
        var root = Path.Combine(Path.GetTempPath(), "chord-w160-store-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var store = (ChordStore)await Layout.OpenStoreAsync(Path.Combine(root, "store.sqlite"), Context);
        try
        {
            var log = new Log(); IReference? journal = null; JsonArray? atExit = null;
            await using (var fleet = await FleetAsync(store, facets: [new("w160.reports", Reporting(log, persist: true))],
                onError: error => { if (error is WorkerProcessException { Kind: "worker_failed" }) atExit = journal is null ? null : (JsonArray)LinesOf(journal).DeepClone(); }))
            {
                journal = await fleet.Server.GetAsync(Journal, new(), Context);
                using var control = await fleet.Server.GetAsync(Control, Id("S1"), Context);
                await Until(() => StateOf(fleet.Workers, "S1")?.Status == "running", "the worker is running");
                var running = StateOf(fleet.Workers, "S1")!;
                var held = await HoldNextCommitAsync(store);
                try
                {
                    await control.CallAsync("report", JsonValue.Create("queued"), Context);
                    await held.Entered;
                    await control.CallAsync("crash", JsonValue.Create(7), Context);
                    // Its link is closed at its exit: the holder sees that at once.
                    await Until(() => control.State.Value.Kind != "live" && !Alive(running.ProcessId!.Value), "the worker exited and its link closed");
                    await Task.Delay(200);
                    Check(StateOf(fleet.Workers, "S1") == running, $"the exit was published before the queued commit settled: {StateOf(fleet.Workers, "S1")}");
                    Check(fleet.Errors.IsEmpty, "an error was reported before the exit was published: " + string.Join(", ", fleet.Errors.Select(error => error.Message)));
                    Check(fleet.Starts.SequenceEqual([Key("S1")]), "a successor started before the exit was published");
                }
                finally { await held.Release(); }
                await Until(() => StateOf(fleet.Workers, "S1")?.Status == "failed", "the exit was published");
                Check(atExit?.ToJsonString() == "[\"queued\"]", "the queued commit had not landed when the exit was published: " + atExit?.ToJsonString());
                await Task.Delay(200);
                Check(fleet.Starts.SequenceEqual([Key("S1")]), "a crashed address started again");
                // The commit landed (above); its caller's wait was aborted with the worker's call, which upstream does not assert.
                Check(log.Outcomes.Count == 1 && log.Outcomes.Single() is "queued committed" or "queued cancelled", "the queued report: " + string.Join(", ", log.Outcomes));
                journal.Release();
            }
        }
        finally { await store.CloseAsync(Context); try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    // workers.test.ts "a stop resolves once the exit was published, after the commits its worker queued".
    public static async Task StopResolvesAfterTheExitWasPublished()
    {
        var root = Path.Combine(Path.GetTempPath(), "chord-w160-store-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var store = (ChordStore)await Layout.OpenStoreAsync(Path.Combine(root, "store.sqlite"), Context);
        try
        {
            var log = new Log();
            await using var fleet = await FleetAsync(store, facets: [new("w160.reports", Reporting(log, persist: true))]);
            using var journal = await fleet.Server.GetAsync(Journal, new(), Context);
            using var control = await fleet.Server.GetAsync(Control, Id("S1"), Context);
            var pid = ((JsonNode)(await control.CallAsync("pid", null, Context))!).GetValue<int>();
            var held = await HoldNextCommitAsync(store);
            Task stop;
            try
            {
                await control.CallAsync("report", JsonValue.Create("queued"), Context);
                await held.Entered;
                stop = fleet.Workers.StopAsync(Session.Address(("id", JsonValue.Create("S1"))));
                await Until(() => !Alive(pid), "the worker exited");
                await Task.Delay(200);
                Check(!stop.IsCompleted, "the stop resolved before the queued commit settled");
                Check(StateOf(fleet.Workers, "S1")?.Status == "stopping", "the worker's state before its exit was published: " + StateOf(fleet.Workers, "S1"));
            }
            finally { await held.Release(); }
            await stop.WaitAsync(TimeSpan.FromSeconds(20));
            Check(StateOf(fleet.Workers, "S1") is null, "a stopped worker kept its entry");
            await Until(() => LinesOf(journal).Count == 1, "the queued report landed");
            Check(LinesOf(journal).ToJsonString() == "[\"queued\"]", "the journal: " + LinesOf(journal).ToJsonString());
        }
        finally { await store.CloseAsync(Context); try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    // workers.test.ts "a worker's incarnation: is in the state before its first call is handled: a report its startup sends finds it, long
    // before ready". The native State is a polled snapshot (no watchable), so "published at the handshake" is what the startup's handler
    // reads there.
    public static async Task IncarnationBeforeTheFirstCall()
    {
        var log = new Log(); ProcessWorkers? workers = null;
        string Seen(string incarnation)
        {
            var state = workers is null ? null : StateOf(workers, "S1");
            return $"{state?.Status} {(state?.Incarnation == incarnation ? "known" : "unknown")}";
        }
        await using var fleet = await FleetAsync(facets: [new("w160.reports", Reporting(log, seen: Seen))],
            configure: made => { workers = made.Workers; made.Behaviours["S1"] = new JsonObject { ["reportAtStart"] = "startup" }; });
        using var control = await fleet.Server.GetAsync(Control, Id("S1"), Context);
        // The startup's call was answered: the worker went on to ready.
        await Until(() => StateOf(fleet.Workers, "S1")?.Status == "running", "the worker is running");
        await control.CallAsync("report", JsonValue.Create("later"), Context);
        await Until(() => log.Received.Count == 2, "both reports were received");
        Check(log.Received.SequenceEqual(["startup starting known", "later running known"]), "what the handlers saw: " + string.Join(", ", log.Received));
        var first = StateOf(fleet.Workers, "S1")!.Incarnation;
        Check(!string.IsNullOrEmpty(first), "a running worker has an incarnation");
        // Another run of the worker is another incarnation.
        await control.CallAsync("crash", JsonValue.Create(7), Context);
        await Until(() => StateOf(fleet.Workers, "S1")?.Status == "failed", "the crash was published");
        Check(StateOf(fleet.Workers, "S1")!.Incarnation is null, "a failed address has an incarnation");
        fleet.Workers.Retry(Session.Address(("id", JsonValue.Create("S1"))));
        await Until(() => StateOf(fleet.Workers, "S1")?.Status == "running", "the retried worker is running");
        Check(StateOf(fleet.Workers, "S1")!.Incarnation is { Length: > 0 } second && second != first, "a new run kept the old incarnation");
    }

    // process.test.ts "closes its link at its exit, before the exit is told, as neither a lost nor a closed link": the worker link's End.
    public static void LinkEndedAtExitIsToldAsNeitherLostNorClosed()
    {
        var type = typeof(ProcessWorkers).Assembly.GetType("PiSharp.Chord.Hosting.Workers.ObservedLink", throwOnError: true)!;
        ILink Observed(ILink inner) => (ILink)Activator.CreateInstance(type, BindingFlags.NonPublic | BindingFlags.Instance, null, [inner], null)!;
        void Observe(ILink link, Action<bool> observer) => type.GetMethod("Observe", BindingFlags.NonPublic | BindingFlags.Instance, [typeof(Action<bool>)])!.Invoke(link, [observer]);
        void End(ILink link) => type.GetMethod("End", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(link, null);
        Task Ended(ILink link) => (Task)type.GetProperty("Ended", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(link)!;

        var events = new List<string>();
        var (near, _) = MemoryLinkPair.Create();
        var link = Observed(near);
        Observe(link, local => events.Add(local ? "link lost" : "link closed"));
        link.OnClose(() => events.Add("closed"));
        End(link);
        Check(Ended(link).IsCompletedSuccessfully, "the end did not complete with the protocol listener");
        End(link); link.Close();
        Check(events.SequenceEqual(["closed"]), "an ended link was told: " + string.Join(", ", events));

        // A close by this side is told as lost, one by the child's as closed; an end after either changes nothing.
        events.Clear();
        var (lost, _) = MemoryLinkPair.Create(); var lostLink = Observed(lost);
        Observe(lostLink, local => events.Add(local ? "link lost" : "link closed")); lostLink.OnClose(() => events.Add("closed"));
        lostLink.Close(); End(lostLink);
        var (closing, other) = MemoryLinkPair.Create(); var closedLink = Observed(closing);
        Observe(closedLink, local => events.Add(local ? "link lost" : "link closed")); closedLink.OnClose(() => events.Add("closed"));
        // The peer's close arrives on its own turn; once told, an end changes nothing.
        other.Close(); Ended(closedLink).Wait(TimeSpan.FromSeconds(5)); End(closedLink);
        Check(events.SequenceEqual(["link lost", "closed", "link closed", "closed"]) && Ended(lostLink).IsCompleted && Ended(closedLink).IsCompleted,
            "a lost or closed link was told: " + string.Join(", ", events));
    }

    // scope.test.ts "a scope's event subscriptions: call the listener for each event, never changed by themselves, until dispose".
    public static async Task ScopeOnCallsTheListenerUntilDispose()
    {
        await using var host = Hosts.Create(new() { OnError = _ => { } });
        var loaded = await host.LoadAsync([new("w160.notes", Facet.Define((h, _) => h.Serve(Notes, (_, _, self) =>
        {
            var text = self.Writable<JsonNode?>(JsonValue.Create("first"));
            var written = self.Events();
            return new(new Dictionary<string, object?>
            {
                ["text"] = self.Derive([text], values => ((JsonNode?)values[0])?.DeepClone()),
                ["written"] = written,
                ["write"] = (HostMethod)((arguments, _) =>
                {
                    text.Set(((JsonNode)arguments!).DeepClone());
                    written.Emit(((JsonNode)arguments!).DeepClone());
                    return Task.FromResult<object?>(null);
                }),
            });
        })))], Context);
        Check(loaded.Failed.Count == 0, "the notes facet failed to load: " + string.Join(" | ", loaded.Failed.Select(failure => failure.Error.Message)));
        var changes = 0;
        var scope = new TerminalScope(host, () => Interlocked.Increment(ref changes));
        var notes = scope.Hold(Notes, new());
        await Until(() => notes.State.Value.Kind == "live", "the notes are live: " + notes.State.Value + " " + notes.State.Value.Cause?.Error.Message);
        var seen = new ConcurrentQueue<string>();
        scope.On(notes.Event("written"), e => seen.Enqueue(e.Value!.GetValue<string>()));
        await Task.Delay(50);
        var before = Volatile.Read(ref changes);
        await notes.CallAsync("write", JsonValue.Create("a"), Context);
        await Until(() => seen.Count == 1, "the listener got the event");
        await Task.Delay(50);
        // The event called its listener only (the text is not followed here: nothing else changed).
        Check(Volatile.Read(ref changes) == before, "an event called changed");
        scope.Dispose();
        using var again = await host.GetAsync(Notes, new(), Context);
        await again.CallAsync("write", JsonValue.Create("b"), Context);
        await Task.Delay(100);
        Check(seen.SequenceEqual(["a"]), "a disposed scope's listener was called: " + string.Join(", ", seen));
    }

    /// <summary>Holds <paramref name="store"/>'s commit queue: <c>Entered</c> completes once a commit was queued behind the hold,
    /// <c>Release</c> lets it run (the upstream <c>holdNextCommit</c>, which is internal here, by public means).</summary>
    private static async Task<(Task Entered, Func<Task> Release)> HoldNextCommitAsync(ChordStore store)
    {
        var export = store.Export(Context);
        if (!await export.MoveNextAsync()) throw new InvalidOperationException("the export yielded nothing");
        var holding = store.SettledAsync();
        async Task Queued() { while (ReferenceEquals(store.SettledAsync(), holding)) await Task.Delay(10); }
        return (Queued().WaitAsync(TimeSpan.FromSeconds(20)), async () => await export.DisposeAsync());
    }

    private static bool Alive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
    private static void Check(bool test, string error) { if (!test) throw new Exception(error); }
    private static async Task Until(Func<bool> test, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!test()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("timed out waiting: " + what); await Task.Delay(20); }
    }
}
