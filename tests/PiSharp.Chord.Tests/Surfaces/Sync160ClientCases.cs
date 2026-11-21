// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/surfaces/client.ts, packages/chord/src/surfaces/connection.ts,
// packages/chord/src/surfaces/variant.ts.
// Authored native regressions from the Pi 1.6.0 tests packages/chord/test/surfaces/client.test.ts, connection.test.ts and
// variant.test.ts; not an upstream execution capture. Helpers: Sync160EdgeSupport.cs.
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Host;
using PiSharp.Chord.Link;
using PiSharp.Chord.Surfaces;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> SurfacesSync160ClientCases() =>
    [
        // client.test.ts "starts with its initial scripts while the server does not answer".
        Case("surfaces.client.160.starts-offline-with-its-initial-scripts", async () =>
        {
            await using var world = await Sync160ClientWorld.StartAsync([Sync160Script("notes", "d1", "one")], more: new() { Offline = true })
                .WaitAsync(TimeSpan.FromSeconds(5));
            Check(world.Sockets.Count > 0, "the client tried to connect");
            Equal("one", await world.GreetingAsync(), "the initial script runs while the server does not answer");
            Check(!world.Client.Followed.IsCompleted, "the client waits for the scripts object in the background");
        }),
        // Native: the scripts object is held, not got, so a client that started offline follows it once the server answers, however
        // long that took (a get would have given up at the host's ready timeout).
        Case("surfaces.client.160.follows-the-scripts-object-once-the-server-answers", async () =>
        {
            var one = Sync160Script("notes", "d1", "one"); var two = Sync160Script("notes", "d2", "two");
            await using var world = await Sync160ClientWorld.StartAsync([one], [two], new() { Offline = true, Shown = [two] })
                .WaitAsync(TimeSpan.FromSeconds(5));
            Equal("one", await world.GreetingAsync(), "the initial script runs offline");
            world.Offline = false;
            // The first socket never opened: closing it is the drop the loop retries after.
            world.Sockets[0].Link.Close();
            await world.Client.Followed.WaitAsync(TimeSpan.FromSeconds(10));
            Sequence(new[] { "/notes.d2.js" }, world.Fetched, "the set the server shows was fetched once it answered");
            Equal("two", await world.GreetingAsync(), "the set the server shows was applied once it answered");
        }),
        // client.test.ts "applies the set the server shows when it starts following, though no frame brought it".
        Case("surfaces.client.160.applies-the-set-shown-when-it-starts-following", async () =>
        {
            var a1 = Sync160Script("test.a", "d1", "hello"); var a2 = Sync160Script("test.a", "d2", "hello again");
            await using var world = await Sync160ClientWorld.StartAsync([a1], [a2], new() { Shown = [a2] });
            await world.Client.Followed.WaitAsync(TimeSpan.FromSeconds(10));
            Equal("hello again", await world.GreetingAsync(), "the shown set was applied though no frame brought it");
            Sequence(new[] { "/test.a.d2.js" }, world.Fetched, "only the changed script was fetched");
        }),
        // client.test.ts "asks the server's base at a reconnect: another one stops the loop, then calls outdated". Upstream changed this
        // case: 1.5.2 asked before the reconnect and expected one socket; 1.6.0 asks beside the next attempt.
        Case("surfaces.client.160.another-base-at-a-reconnect-stops-the-loop-then-calls-outdated", async () =>
        {
            await using var world = await Sync160ClientWorld.StartAsync([]);
            await world.Client.Followed.WaitAsync(TimeSpan.FromSeconds(10));
            world.ServerBase = "B2";
            world.Sockets[0].Link.Close();
            await Sync160Until(() => world.Outdated.Count == 1, "the client was told it is outdated");
            await Task.Delay(400);
            Check(world.Sockets.Count <= 2, "an outdated client kept reconnecting: " + world.Sockets.Count + " sockets");
            Sequence(new[] { "outdated (Waiting)" }, world.Outdated, "the loop had stopped before outdated was called");
        }),
        // client.test.ts "a slow base answer after the next link opened: that link ends before outdated is called".
        Case("surfaces.client.160.slow-base-answer-ends-the-next-link-before-outdated", async () =>
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var world = await Sync160ClientWorld.StartAsync([], more: new() { BaseAnswers = gate.Task });
            await world.Client.Followed.WaitAsync(TimeSpan.FromSeconds(10));
            world.ServerBase = "B2";
            world.Sockets[0].Link.Close();
            // The next attempt goes ahead while the base is still being asked.
            await Sync160Until(() => world.Sockets.Count == 2, "the next link opened beside the question");
            await Task.Delay(50);
            Equal(0, world.Outdated.Count, "outdated was called before the base was answered");
            gate.SetResult();
            await Sync160Until(() => world.Outdated.Count == 1, "the client was told it is outdated");
            Sequence(new[] { "outdated (Waiting)" }, world.Outdated, "the next link had ended before outdated was called");
        }),
        // client.test.ts "takes parts from the server within the limits it was given, and none past them".
        Case("surfaces.client.160.part-limits-bound-what-arrives-from-the-server", async () =>
        {
            var a1 = Sync160Script("test.a", "d1", "hello"); var a2 = Sync160Script("test.a", "d2", "hello again");
            await using (var within = await Sync160ClientWorld.StartAsync([a1], [a2], new() { Shown = [a2], Limits = new(Part: 1024 * 1024) }))
            {
                await within.Client.Followed.WaitAsync(TimeSpan.FromSeconds(10));
                Equal("hello again", await within.GreetingAsync(), "a set within the limits was applied");
            }
            // The scripts object's set is past 32 bytes: the client never gets it, so it keeps the code it started with.
            await using var past = await Sync160ClientWorld.StartAsync([a1], [a2], new() { Shown = [a2], Limits = new(Part: 32) });
            await Task.Delay(200);
            Equal(0, past.Fetched.Count, "a set past the limits was fetched");
            Equal("hello", await past.GreetingAsync(), "a set past the limits was applied");
        }),
        // client.test.ts "applies no new set and is never outdated by one" (a static client: follow false).
        Case("surfaces.client.160.static-client-applies-no-set-and-is-never-outdated", async () =>
        {
            var one = Sync160Script("notes", "d1", "one"); var two = Sync160Script("notes", "d2", "two");
            await using var world = await Sync160ClientWorld.StartAsync([one], [two], new() { Follow = false });
            Check(world.Client.Followed.IsCompletedSuccessfully, "a static client follows nothing");
            await world.PublishAsync(Sync160SetOf("B1", [two]));
            await world.PublishAsync(Sync160SetOf("B2", []));
            await Task.Delay(150);
            Equal(0, world.Fetched.Count, "a static client fetched a new set");
            Equal(0, world.Outdated.Count, "a static client was outdated by a new base");
            Equal("one", await world.GreetingAsync(), "a static client keeps the code it started with");
        }),
        // client.test.ts "reconnects without asking the server's base".
        Case("surfaces.client.160.static-client-reconnects-without-asking-the-base", async () =>
        {
            await using var world = await Sync160ClientWorld.StartAsync([], more: new() { Follow = false });
            world.ServerBase = "B2";
            world.Sockets[0].Link.Close();
            await Sync160Until(() => world.Sockets.Count == 2, "the static client reconnected");
            await Task.Delay(50);
            Equal(0, world.Asked.Count, "a static client asked the server's base");
            Equal(0, world.Outdated.Count, "a static client was outdated");
        }),
        // A following client still applies a set published later, now that its start no longer waits for the first one.
        Case("surfaces.client.160.applies-a-set-published-after-it-started", async () =>
        {
            var one = Sync160Script("notes", "d1", "one"); var two = Sync160Script("notes", "d2", "two");
            await using var world = await Sync160ClientWorld.StartAsync([one], [two]);
            await world.Client.Followed.WaitAsync(TimeSpan.FromSeconds(10));
            Equal(0, world.Fetched.Count, "the set shown at start is the one it runs");
            await world.PublishAsync(Sync160SetOf("B1", [two]));
            await Sync160Until(() => world.Fetched.Count == 1, "the published set was fetched");
            using var greeting = await world.Client.Host.GetAsync(Sync160Greeting, new(), ChordContext.Background);
            await Sync160Until(() => greeting.Member("text").Value?.GetValue<string>() == "two", "the published set was applied");
            Equal(0, world.Errors.Count, "nothing failed");
        }),
    ];

    private static IEnumerable<(string, Func<Task>)> SurfacesSync160ConnectionCases() =>
    [
        // connection.test.ts "does not wait for canRetry: the next attempt goes ahead; a late no after it opened closes it, reported
        // waiting". It replaces 1.5.2's "retries at once when woken while canRetry is deciding".
        Case("surfaces.connection.160.next-attempt-goes-ahead-and-a-late-no-closes-it", async () =>
        {
            var environment = new SurfaceClock(); var links = new List<SurfaceLink>(); var states = new List<ConnectionState>();
            ConnectionState Last() { lock (states) return states[^1]; }
            var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var loop = ConnectionLoop.KeepConnected(new()
            {
                Environment = environment, CanRetry = () => answer.Task, OnState = state => { lock (states) states.Add(state); },
                Open = events => { var link = new SurfaceLink(events); links.Add(link); return link; },
            });
            links[^1].Close();
            environment.Advance(250);
            Equal(2, links.Count, "the next attempt did not wait for the answer");
            links[^1].Events.Opened();
            Equal(ConnectionState.Connected, Last(), "the next attempt opened");
            // The answer is taken on another thread.
            answer.SetResult(false);
            await Sync160Until(() => links[^1].Closed && Last() == ConnectionState.Waiting, "the late no closed the open link, reported waiting");
            environment.Advance(60_000);
            Equal(2, links.Count, "a stopped loop opened another link");
            Equal(ConnectionState.Waiting, Last(), "the attempt the stop ended is reported waiting");
        }),
        // connection.test.ts "asks canRetry one question at a time: closes while it is open ask nothing more; the next close asks again".
        Case("surfaces.connection.160.asks-can-retry-one-question-at-a-time", async () =>
        {
            var environment = new SurfaceClock(); var links = new List<SurfaceLink>();
            var answers = new List<TaskCompletionSource<bool>>();
            using var loop = ConnectionLoop.KeepConnected(new()
            {
                Environment = environment, OnState = _ => { },
                CanRetry = () => { var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); lock (answers) answers.Add(answer); return answer.Task; },
                Open = events => { var link = new SurfaceLink(events); links.Add(link); return link; },
            });
            for (var attempt = 0; attempt < 8; attempt++) { links[^1].Close(); environment.Advance(5000); }
            Equal(9, links.Count, "every close was retried while the question was open");
            Equal(1, answers.Count, "closes while a question is open ask nothing more");
            answers[0].SetResult(true);
            // The answer is taken on another thread: the next close asks again once it was.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (answers.Count < 2 && DateTime.UtcNow < deadline) { links[^1].Close(); environment.Advance(5000); await Task.Delay(5); }
            Equal(2, answers.Count, "the next close asks again");
        }),
        // connection.test.ts "retries at once a drop soon after a wake (the socket that was away), once per wake".
        Case("surfaces.connection.160.drop-soon-after-a-wake-retries-at-once-once-per-wake", () =>
        {
            var environment = new SurfaceClock(); var links = new List<SurfaceLink>(); var opens = new List<double>();
            using var loop = ConnectionLoop.KeepConnected(new()
            {
                Environment = environment, CanRetry = () => Task.FromResult(true), OnState = _ => { },
                Open = events => { var link = new SurfaceLink(events); links.Add(link); opens.Add(environment.Now()); return link; },
            });
            links[^1].Events.Opened();
            environment.Advance(ConnectionLoop.StableMilliseconds);
            // Back from the background: the wake comes first, then the close the frozen page had not handled yet.
            environment.Wake();
            links[^1].Close();
            environment.Advance(0);
            Equal(2, links.Count, "a drop soon after a wake was not retried at once");
            // The server is down: the next drop waits the growing pause, no tight loop.
            var closedAt = environment.Now();
            links[^1].Close();
            environment.Advance(5000);
            Equal(500d, opens[^1] - closedAt, "the wake was used for more than one retry");
        }),
        // connection.test.ts "is replaced on a wake once its next ping is overdue; one with recent frames stays" (its 1.6.0 addition: the
        // replacement goes at once).
        Case("surfaces.connection.160.silent-socket-replacement-goes-at-once", () =>
        {
            var environment = new SurfaceClock(); var links = new List<SurfaceLink>(); var opens = new List<double>();
            using var loop = ConnectionLoop.KeepConnected(new()
            {
                Environment = environment, CanRetry = () => Task.FromResult(true), OnState = _ => { },
                Open = events => { var link = new SurfaceLink(events); links.Add(link); opens.Add(environment.Now()); return link; },
            });
            links[^1].Events.Opened();
            environment.Advance(10_000);
            links[^1].Events.Received();
            environment.Wake();
            environment.Advance(ConnectionLoop.StaleMilliseconds);
            Check(!links[0].Closed, "a socket with recent frames was replaced");
            environment.Wake();
            environment.Advance(ConnectionLoop.StaleMilliseconds + 1000);
            Check(links[0].Closed, "a silent socket was not replaced");
            // Its replacement goes at once (at the second wake's check, 1 s after it), not after a pause.
            Equal(10_000 + ConnectionLoop.StaleMilliseconds + 1000, opens[1], "the replacement waited a pause");
        }),
        // connection.test.ts "closes the link at stop and opens no other"; 1.6.0: the attempt a stop ended is reported waiting.
        Case("surfaces.connection.160.stop-reports-the-attempt-it-ended-waiting", () =>
        {
            var environment = new SurfaceClock(); var links = new List<SurfaceLink>(); var states = new List<ConnectionState>();
            var loop = ConnectionLoop.KeepConnected(new()
            {
                Environment = environment, CanRetry = () => Task.FromResult(true), OnState = states.Add,
                Open = events => { var link = new SurfaceLink(events); links.Add(link); return link; },
            });
            links[^1].Events.Opened();
            loop.Stop();
            Check(links[^1].Closed, "stop did not close the link");
            Sequence(new[] { ConnectionState.Connecting, ConnectionState.Connected, ConnectionState.Waiting }, states, "the states a stopped loop reported");
            loop.Stop();
            environment.Advance(60_000);
            Equal(1, links.Count, "a stopped loop opened another link"); Equal(3, states.Count, "a second stop reported a state");
        }),
    ];

    private static IEnumerable<(string, Func<Task>)> SurfacesSync160VariantCases() =>
    [
        // variant.test.ts "is the reference while it is live in the variant, nothing before it binds or for no reference".
        Case("surfaces.variant.160.live-in-is-the-reference-while-live-in-the-variant", async () =>
        {
            await using var desk = await Sync160DeskWorld.StartAsync();
            using var held = desk.Client.Hold(Sync160Desk, new(), ChordContext.Background);
            Equal<IReference?>(null, Variants.LiveIn(held, "reader"), "a reference that is not bound yet");
            Equal<IReference?>(null, Variants.LiveIn<IReference>(null, "reader"), "no reference");
            desk.Link("guest");
            await Sync160Until(() => held.State.Value.Kind == "live", "the reference is live");
            Check(ReferenceEquals(held, Variants.LiveIn(held, "reader")), "the reference is read in the variant it is live in");
            Equal<IReference?>(null, Variants.LiveIn(held, "editor"), "another variant");
            Throws<ArgumentNullException>(() => Variants.LiveIn(held, null!), "a variant is required");
        }),
        // variant.test.ts "follows the variant an admission rebinds: nothing while it loads between two, denied or released". Upstream
        // rebinds through an admission and denies through it; admission is the chord-admission package's, so here another principal's
        // link rebinds the hold, and a lost link is what leaves it not live. The denied state and the per-state sequence upstream
        // records from the state's watch (a native watch is delivered on another thread, so what is read there is not the state it
        // was called for) are not covered here; "loading still says the last variant" is terminal.160.scope-variant-one-watch-per-state
        // in the Hosting suite.
        Case("surfaces.variant.160.live-in-follows-a-rebind-to-another-variant", async () =>
        {
            await using var desk = await Sync160DeskWorld.StartAsync();
            var held = desk.Client.Hold(Sync160Desk, new(), ChordContext.Background);
            desk.Link("guest");
            await Sync160Until(() => ReferenceEquals(held, Variants.LiveIn(held, "reader")), "the reference is live in the reader variant");
            desk.Link("owner");
            await Sync160Until(() => ReferenceEquals(held, Variants.LiveIn(held, "editor")), "the reference is live in the editor variant");
            Equal<IReference?>(null, Variants.LiveIn(held, "reader"), "an editor is not read as a reader");
            await Variants.LiveIn(held, "editor")!.CallAsync("write", JsonValue.Create("edited"), ChordContext.Background);
            desk.Unlink();
            await Sync160Until(() => held.State.Value.Kind != "live", "the reference lost its link");
            Equal<IReference?>(null, Variants.LiveIn(held, "editor"), "a reference that is not live");
            desk.Link("guest");
            await Sync160Until(() => ReferenceEquals(held, Variants.LiveIn(held, "reader")), "the reference is live in the reader variant again");
            Equal<IReference?>(null, Variants.LiveIn(held, "editor"), "a reader is not read as an editor");
            held.Release();
            Equal<IReference?>(null, Variants.LiveIn(held, "reader"), "a released reference");
        }),
    ];
}
