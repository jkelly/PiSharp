// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/test/host/admission.test.ts, and the admission cases of
// packages/chord/test/host/dependencies.test.ts and reference-source.test.ts (upstream 8270d9af6, slice 14 §2). Ported by reading, against
// real hosts; the fixtures are in Sync160AdmissionSupport.cs. Upstream's `delivered()` is A6Delivered (the dispatcher flushed and the watch
// deliveries given time); a check upstream makes "in the flush of the change" is made right after the awaited call that made the change,
// which returns once its commit (and the flush) ran on the host's dispatcher. Cases named 160-* are authored for the native port.
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Host;
using PiSharp.Chord.Jobs;
using PiSharp.Chord.Link;
using PiSharp.Chord.State;
using PiSharp.Chord.Store;
using static PiSharp.Chord.Contracts.Schemas;
using static ChordSupport;

internal static partial class Program
{
    private static readonly ChordContext A6Ctx = ChordContext.Background;

    /// <summary>A local view of the notes' count (dependencies.test.ts's <c>View</c>).</summary>
    private static readonly Contract A6View = Contract.Local("app.view", new() { Version = 1, Params = new() { ["id"] = Int() }, Members = new() { ["n"] = Int() } });

    /// <summary>A client whose view declares a remote Notes, and the raw parent that answers its hold (upstream <c>viewOverParent</c>).</summary>
    private static async Task<(ChordHost Host, S6Peer Peer, List<HostError> Errors)> A6ViewOverParent(TimeSpan? readyTimeout = null)
    {
        var errors = new List<HostError>();
        var host = Hosts.Create(new()
        {
            Principal = S6User, ReadyTimeout = readyTimeout ?? TimeSpan.FromSeconds(60), CloseTimeout = TimeSpan.FromMilliseconds(200),
            OnError = error => { lock (errors) errors.Add(error); },
        });
        var peer = H6RawParent(host);
        await host.LoadAsync([new("@test/view", Facet.Define((h, _) => h.Serve(A6View, (_, _, self) =>
            new(new Dictionary<string, object?> { ["n"] = self.Get(Notes, new() { ["session"] = "S1" }).Member("count") }))))], A6Ctx);
        return (host, peer, errors);
    }

    private static HostError[] A6Locked(List<HostError> errors) { lock (errors) return [.. errors]; }


    private static readonly TableDefinition A6Items = Tables.DefineTable("items", new TableOptions
    { Version = 1, Columns = new() { ["id"] = Str() }, Key = ["id"] });

    private static readonly Contract A6Shelf = Contract.Local("test.shelf", new()
    {
        Version = 1, Params = new(), Members = new() { ["items"] = Queries(Obj(("id", Str())), ("all", Obj())), ["put"] = Method(Str()) },
    });

    private static readonly Contract A6Reader = Contract.Remote("test.reader", new()
    {
        Version = 1, Params = new(), HostAddress = _ => Server.Address(), Admission = Str(),
        Variants = new() { ["one"] = new() { State = { ["label"] = Str() } } },
    });

    /// <summary>A facet serving <see cref="A6Reader"/>, whose admission reads the shelf's items live.</summary>
    private static Facet A6ReaderFacet() => Facet.Define((f, _) => f.Serve(A6Reader,
        (_, _, _, _) => new(new Dictionary<string, object?> { ["label"] = Watchables.Constant(JsonValue.Create("x")) }, "one"),
        new ServeOptions(Admit: (_, _, self) =>
        {
            var items = self.LiveQuery(self.Get(A6Shelf, new()).QueriesMember("items")["all"], new JsonObject(), 3);
            return self.Derive([items], _ => Admission.Allow(JsonValue.Create("yes")));
        })));

    private static readonly TableDefinition A6Log = Tables.DefineTable("log", new TableOptions
    { Version = 1, Columns = new() { ["line"] = Str() }, Key = ["line"] });

    /// <summary>A persisted log on the host: append commits one row.</summary>
    private static readonly Contract A6Journal = Contract.Local("test.journal", new()
    { Version = 1, Params = new(), Members = new() { ["append"] = Method(Str()), ["has"] = Method(Str(), Int()) } });

    private static PluginFacet A6JournalPlugin() => new("@test/journal", Facet.Define((f, _) => f.Serve(A6Journal, (_, _, self) =>
    {
        var log = ((IStoreSelf)self).Table(A6Log);
        return new(new Dictionary<string, object?>
        {
            ["append"] = Q7Tx(self, (tx, line) => tx.Put(log, new JsonObject { ["line"] = line!.DeepClone() })),
            ["has"] = (HostMethod)(async (line, ctx) =>
                JsonValue.Create(await log.GetAsync(new JsonObject { ["line"] = ((JsonNode)line!).DeepClone() }, ctx.Context) is null ? 0 : 1)),
        });
    })));


    private static readonly JobDefinition A6Work = JobDefinition.Define("test.work", new() { Version = 1, Args = Obj(), Result = Int() });

    /// <summary>The errors of a load of one facet (upstream <c>refused</c>).</summary>
    private static async Task<string> A6Refused(Action<IFacetHost> setup)
    {
        var host = Hosts.Create(new() { Address = Server.Address(), Principal = new() { ["kind"] = "server" }, OnError = _ => { } });
        var loaded = await host.LoadAsync([new("@test/app", Facet.Define((h, _) => setup(h)))], A6Ctx);
        await host.CloseAsync(A6Ctx);
        return string.Join(" | ", loaded.Failed.Select(failure => failure.Error.Message));
    }


    private static IEnumerable<(string, Func<Task>)> HostSync160AdmissionCases() =>
    [
        // --- deny, then allow ---
        Case("host.admission.deny-then-allow.a-direct-hold-shows-failed-denied-with-the-reason-then-binds-the-variant-the-value-selects", async () =>
        {
            var code = new A6DocCode();
            await using var w = await A6Served(code);
            // Held and watched in one turn, as upstream holds and watches before its microtasks run: the admission runs in a later turn.
            var (doc, states) = await A6HoldWatched(w.Host, A6Doc, new() { ["id"] = "a" });
            await A6Delivered(w.Host);
            Equal("failed denied forbidden: no access", A6Cause(doc.State.Value), "denied with the reason");
            Equal<double?>(null, doc.State.Value.RetryAt, "no retry for a denial");
            // The factory has not run: nothing is built for a caller who is denied.
            Equal(0, code.BuiltNow.Length, "nothing built");
            await w.Set("read");
            await A6Delivered(w.Host);
            Equal("live reader \"t\"", $"{doc.State.Value.Kind} {doc.Variant} {A6Read(doc, "text")}", "live in the reader variant");
            Equal("a:read", string.Join(",", code.BuiltNow), "built once, as a reader");
            Equal("failed: no access,loading,live", string.Join(",", A6Now(states)), "its states");
            Equal(0, w.ErrorsNow.Length, "no errors");
        }),
        Case("host.admission.deny-then-allow.a-getter-rejects-at-once-with-the-denial-and-resolves-once-allowed", async () =>
        {
            await using var w = await A6Served(new());
            Equal("forbidden: no access", await A6Rejected(w.Host.GetAsync(A6Doc, new() { ["id"] = "a" }, A6Ctx)), "rejected with the denial");
            await w.Set("write");
            var doc = await w.Host.GetAsync(A6Doc, new() { ["id"] = "a" }, A6Ctx);
            Equal("writer", doc.Variant, "the writer variant");
        }),
        Case("host.admission.deny-then-allow.a-dependents-self-get-shows-the-denial-stays-parked-and-heals-when-access-returns", async () =>
        {
            await using var w = await A6Served(new());
            var panel = w.Host.Hold(A6Panel, new() { ["id"] = "a" }, A6Ctx);
            await A6Delivered(w.Host);
            // Denied, not broken: the dependency's forbidden is no authority refusal that never heals.
            Equal("failed denied forbidden: no access", A6Cause(panel.State.Value), "the dependent shows the denial");
            await w.Set("read");
            await A6Delivered(w.Host);
            Equal("live \"T\"", $"{panel.State.Value.Kind} {A6Read(panel, "shout")}", "healed");
            Equal(0, w.ErrorsNow.Length, "no errors");
        }),

        // --- the watch table (2.2) ---
        Case("host.admission.watch-table.an-equal-value-changes-nothing-no-rebuild-the-running-call-completes", async () =>
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var code = new A6DocCode { Gate = gate.Task };
            await using var w = await A6Served(code, "write");
            var doc = await w.Host.GetAsync(A6Doc, new() { ["id"] = "a" }, A6Ctx);
            var states = await A6States(doc);
            var writing = A6Write(doc, "x");
            // Another person's mode changes the source's value, not this caller's decision.
            await w.Set("read", "guest");
            await w.Set("write");
            await A6Delivered(w.Host);
            gate.SetResult();
            Equal("\"write:x\"", (await writing as JsonNode)?.ToJsonString(), "the running call completes");
            Equal("a:write", string.Join(",", code.BuiltNow), "no rebuild");
            Equal(0, A6Now(states).Length, "no state change");
        }),
        Case("host.admission.watch-table.another-allowed-value-seals-the-attempt-its-calls-reject-unavailable-its-holders-see-loading-then-the-new-variant", async () =>
        {
            var code = new A6DocCode { Gate = new TaskCompletionSource().Task };
            await using var w = await A6Served(code, "write");
            var doc = await w.Host.GetAsync(A6Doc, new() { ["id"] = "a" }, A6Ctx);
            var states = await A6States(doc);
            var frames = await A6Values(doc, "text");
            var writing = A6Rejected(A6Write(doc, "x"));
            await w.Set("read");
            // In the flush of the change: sealed, and loading with the last value readable. The host's dispatcher runs on its own
            // thread and can already have rebuilt the reader when this reads the state, so the loading step is asserted from the
            // recorded states below ("loading,live"), not sampled here. The text is "t" on both instances.
            Equal("\"t\"", A6Read(doc, "text"), "the last value readable");
            Equal("unavailable: test.doc(a): its admission changed", await writing, "the running call");
            await A6Delivered(w.Host);
            Equal("live reader", $"{doc.State.Value.Kind} {doc.Variant}", "the reader variant");
            Equal("a:write,a:read", string.Join(",", code.BuiltNow), "rebuilt as a reader");
            Equal("loading,live", string.Join(",", A6Now(states)), "its states");
            // The same text on the new instance: no frame.
            Equal(0, A6Now(frames).Length, "no frame");
            Equal(0, w.ErrorsNow.Length, "no errors");
        }),
        Case("host.admission.watch-table.a-denial-seals-the-attempt-its-calls-reject-forbidden-revoked-its-holders-see-failed-denied-at-once", async () =>
        {
            var code = new A6DocCode { Gate = new TaskCompletionSource().Task };
            await using var w = await A6Served(code, "write");
            var doc = await w.Host.GetAsync(A6Doc, new() { ["id"] = "a" }, A6Ctx);
            var states = await A6States(doc);
            var writing = A6Rejected(A6Write(doc, "x"));
            await w.Set("none");
            Equal("failed denied forbidden: no access", A6Cause(doc.State.Value), "denied in the flush of the change");
            Equal("forbidden/revoked: test.doc(a): no access", await writing, "the running call");
            await A6Delivered(w.Host);
            // No loading in between: the denial is shown in the flush that made it.
            Equal("failed: no access", string.Join(",", A6Now(states)), "its states");
            Equal("a:write", string.Join(",", code.BuiltNow), "not rebuilt");
        }),
        Case("host.admission.watch-table.a-decision-that-throws-when-read-is-a-failed-source-sealed-calls-unavailable-holders-failed-waiting", async () =>
        {
            var code = new A6DocCode { Gate = new TaskCompletionSource().Task };
            await using var w = await A6Served(code, "write");
            var doc = await w.Host.GetAsync(A6Doc, new() { ["id"] = "a" }, A6Ctx);
            var writing = S6Rejection(A6Write(doc, "x"));
            await w.Set("throw");
            Equal("unavailable", ((ChordError)await writing).Code, "the running call");
            await A6Delivered(w.Host);
            Equal("failed Waiting", $"{doc.State.Value.Kind} {doc.State.Value.Cause?.Kind}", "failed waiting");
            Check(doc.State.Value.Cause!.Error.Message.Contains("the rule threw"), "names what threw: " + doc.State.Value.Cause!.Error.Message);
            // Not denied. Upstream keeps the last value readable while it waits; the native reference member throws the failure of a
            // member its seal failed, as it does for any object failed live (a pre-existing difference of the port, not the admission's):
            // what it shows is the waiting failure, never the denial.
            Check(A6Read(doc, "text").StartsWith("throws unavailable: waiting for test.doc(a): its admission", StringComparison.Ordinal), "not the denial: " + A6Read(doc, "text"));
            await w.Set("write");
            await A6Delivered(w.Host);
            Equal("live", doc.State.Value.Kind, "live again");
            Equal("a:write,a:write", string.Join(",", code.BuiltNow), "rebuilt");
        }),
        Case("host.admission.watch-table.a-source-that-fails-live-seals-its-readers-which-park-on-it-and-rebuild-when-it-is-back", async () =>
        {
            var code = new A6DocCode(); var selves = new List<ISelf>();
            await using var w = await A6Served(code, "read", new A6AccessCode { Selves = selves });
            var doc = await w.Host.GetAsync(A6Doc, new() { ["id"] = "a" }, A6Ctx);
            A6FailLive(w.Host, selves[0], new ChordError("unavailable", "the store broke"));
            Equal("loading", doc.State.Value.Kind, "sealed in the failure's flush");
            await A6Delivered(w.Host);
            Equal("failed Waiting", $"{doc.State.Value.Kind} {doc.State.Value.Cause?.Kind}", "parked on its failed source");
            Equal("waiting for test.access: the store broke", doc.State.Value.Cause!.Error.Message, "names the source");
            // The failed store is retried by its own getter; its modes are its object's documents.
            var access = w.Host.Hold(A6Access, new(), A6Ctx);
            access.Retry();
            await A6Delivered(w.Host);
            await w.Set("read");
            await A6Delivered(w.Host);
            Equal("live", doc.State.Value.Kind, "rebuilt once the source is back");
            Equal("a:read,a:read", string.Join(",", code.BuiltNow), "rebuilt");
            access.Release();
        }),
        Case("host.admission.watch-table.a-call-that-crossed-a-seal-runs-on-the-next-attempt-under-its-value", async () =>
        {
            await using var w = await A6Served(new(), "write");
            var doc = await w.Host.GetAsync(A6Doc, new() { ["id"] = "a" }, A6Ctx);
            await w.Set("read");
            // Made while the new attempt admits (the reference is loading): it waits at the gate, never not_ready, and meets the reader
            // the new value selects, not the writer it was sent to.
            // Upstream's message is 'test.doc(a) (variant reader) has no method "write"'; the native attempt's is its 1.5.2 one.
            Equal("invalid/member: write: not a member of the bound variant", await A6Rejected(A6Write(doc, "y")), "met the reader");
            await w.Set("write");
            Equal("\"write:z\"", (await A6Write(doc, "z") as JsonNode)?.ToJsonString(), "met the writer");
        }),

        // --- what a denial removes ---
        Case("host.admission.denial-removes.references-hold-no-value-a-watch-gets-one-failed-call-and-keeps-what-it-delivered-a-derive-over-a-member-fails-until-the-next-allow", async () =>
        {
            await using var w = await A6Served(new(), "write");
            var doc = await w.Host.GetAsync(A6Doc, new() { ["id"] = "a" }, A6Ctx);
            var panel = await w.Host.GetAsync(A6Panel, new() { ["id"] = "a" }, A6Ctx);
            var watch = await doc.Member("text").WatchAsync(A6Ctx);
            var frames = A6ValuesOf(watch);
            var shouts = await A6Values(panel, "shout");
            await w.Set("none");
            await A6Delivered(w.Host);
            Equal("throws forbidden: no access", A6Read(doc, "text"), "no value held");
            Equal<string?>(null, doc.Variant, "no variant");
            Equal("{failed: no access}", string.Join(",", A6Now(frames)), "one failed call");
            // Data already delivered to application code is not retracted.
            Equal("\"t\"", watch.Value?.ToJsonString(), "the watch keeps what it delivered");
            // The dependent stays live (R18); its derive over the member fails as a member.
            Equal("live", panel.State.Value.Kind, "the dependent stays live");
            Equal("{failed: no access}", string.Join(",", A6Now(shouts)), "its derive failed");
            Equal("throws forbidden: no access", A6Read(panel, "shout"), "its member throws the denial");
            await w.Set("read");
            await A6Delivered(w.Host);
            Equal("{failed: no access},\"t\"", string.Join(",", A6Now(frames)), "the value again");
            Equal("{failed: no access},\"T\"", string.Join(",", A6Now(shouts)), "the derive again");
        }),
        Case("host.admission.denial-removes.shows-the-denial-before-the-sealed-attempts-teardown-finished", async () =>
        {
            var closing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var code = new A6DocCode { Opened = Task.CompletedTask, Closing = closing.Task };
            await using var w = await A6Served(code, "read");
            var doc = await w.Host.GetAsync(A6Doc, new() { ["id"] = "a" }, A6Ctx);
            await w.Set("none");
            Equal("failed denied forbidden: no access", A6Cause(doc.State.Value), "denied at once");
            await A6Delivered(w.Host);
            Equal("open", string.Join(",", code.NativesNow), "the old native still closes");
            // Allowed again while the old native still closes: the factory waits for that teardown, the admission did not.
            await w.Set("read");
            await A6Delivered(w.Host);
            Equal("loading", doc.State.Value.Kind, "loading while the teardown runs");
            closing.SetResult();
            await A6Delivered(w.Host);
            Equal("live", doc.State.Value.Kind, "live after the teardown");
            Equal("open,close,open", string.Join(",", code.NativesNow), "the natives in order");
        }),

        // --- a failed admitted object ---
        Case("host.admission.failed-admitted.that-failed-live-follows-a-denial-failed-denied-no-value-out-of-presence-its-factory-not-run-again", async () =>
        {
            var selves = new List<ISelf>();
            var code = new A6DocCode { Declare = (self, _) => { lock (selves) selves.Add(self); } };
            await using var w = await A6Served(code, "read");
            var present = w.Host.References(A6Doc);
            var doc = await w.Host.GetAsync(A6Doc, new() { ["id"] = "a" }, A6Ctx);
            A6FailLive(w.Host, selves[0], new ChordError("unavailable", "broke live"));
            await A6Delivered(w.Host);
            // Failed: its holder listed. (Upstream keeps its last value readable; the native member throws the live failure, as for any
            // object failed live: a pre-existing difference of the port.)
            Equal("failed throws unavailable: broke live 1", $"{doc.State.Value.Kind} {A6Read(doc, "text")} {present.Value.Count}", "failed live");
            await w.Set("none");
            // In the denial's flush, with no new attempt.
            Equal("failed denied forbidden: no access", A6Cause(doc.State.Value), "denied at once");
            Equal<double?>(null, doc.State.Value.RetryAt, "no retry");
            Equal("throws forbidden: no access", A6Read(doc, "text"), "no value");
            Equal(" 0", $"{doc.Variant} {present.Value.Count}", "no variant, out of presence");
            await A6Delivered(w.Host);
            Equal("a:read", string.Join(",", code.BuiltNow), "its factory not run again");
            // Allowed again: another decision than the denial, so the object is retried.
            await w.Set("read");
            await A6Delivered(w.Host);
            Equal("live \"t\" a:read,a:read", $"{doc.State.Value.Kind} {A6Read(doc, "text")} {string.Join(",", code.BuiltNow)}", "retried");
            // Upstream reports object_failed; the native host reports a live failure as activation_failed (a pre-existing difference).
            Equal("activation_failed", string.Join(",", w.ErrorsNow.Select(error => error.Kind)), "one failure reported");
        }),
        Case("host.admission.failed-admitted.whose-factory-broke-for-one-value-is-retried-by-another-allowed-value-and-by-nothing-else", async () =>
        {
            var code = new A6DocCode { Declare = (_, mode) => { if (mode == "write") throw new InvalidOperationException("the writer is broken"); } };
            await using var w = await A6Served(code, "write");
            var doc = w.Host.Hold(A6Doc, new() { ["id"] = "a" }, A6Ctx);
            await A6Delivered(w.Host);
            Equal("failed Broken", $"{doc.State.Value.Kind} {doc.State.Value.Cause?.Kind}", "broken");
            // An equal decision and a failed source change nothing: the broken factory is not run again.
            await w.Set("read", "guest");
            await w.Set("throw");
            await A6Delivered(w.Host);
            Equal("failed Broken a:write", $"{doc.State.Value.Kind} {doc.State.Value.Cause?.Kind} {string.Join(",", code.BuiltNow)}", "nothing ran again");
            await w.Set("read");
            await A6Delivered(w.Host);
            Equal("live reader a:write,a:read", $"{doc.State.Value.Kind} {doc.Variant} {string.Join(",", code.BuiltNow)}", "retried by the new value");
            doc.Release();
        }),
        // --- an admission's requests ---
        Case("host.admission.requests.are-kept-across-a-rebuild-until-the-successors-bound-a-source-held-only-by-them-never-ends", async () =>
        {
            var shares = new List<string>();
            await using var w = await A6Served(new A6DocCode { Admit = A6AdmitBy(share: true) }, "read", shares: shares);
            var doc = await w.Host.GetAsync(A6Doc, new() { ["id"] = "a" }, A6Ctx);
            await w.Set("write"); await A6Delivered(w.Host);
            await w.Set("none"); await A6Delivered(w.Host);
            await w.Set("read"); await A6Delivered(w.Host);
            Equal("live", doc.State.Value.Kind, "live");
            // One Share(a) through three rebuilds and a denial (it has no linger: a gap would have ended it).
            Equal("a", string.Join(",", A6Now(shares)), "one share");
            doc.Release();
            await A6Delivered(w.Host);
            // Released with the object: the next holder's admission makes a new one.
            (await w.Host.GetAsync(A6Doc, new() { ["id"] = "a" }, A6Ctx)).Release();
            Equal("a,a", string.Join(",", A6Now(shares)), "a new share for the next holder");
            Equal(0, w.ErrorsNow.Length, "no errors");
        }),
        Case("host.admission.requests.stay-with-a-sealed-attempt-while-no-later-admission-bound-a-retry-finds-its-source-still-held", async () =>
        {
            await using var w = await A6Served(new A6DocCode { Admit = A6AdmitBy(share: true) }, "read");
            var doc = await w.Host.GetAsync(A6Doc, new() { ["id"] = "kept" }, A6Ctx);
            var share = A6Object(w.Host, "test.share(kept)");
            Check(share is not null, "the share is held");
            var runs = 0;
            // Staging records the first run; the successor's own run throws before it declares anything.
            Admit flaky = (parameters, principal, self) =>
            {
                if (Interlocked.Increment(ref runs) == 2) throw new InvalidOperationException("admit threw");
                return A6AdmitBy(share: true)(parameters, principal, self);
            };
            Equal(0, (await w.Reload(doc: A6DocFacet(new A6DocCode { Admit = flaky }))).Failed.Count, "the reload committed");
            await A6Delivered(w.Host);
            Equal("failed Broken", $"{doc.State.Value.Kind} {doc.State.Value.Cause?.Kind}", "broken");
            doc.Retry();
            await A6Delivered(w.Host);
            Equal("live", doc.State.Value.Kind, "live after the retry");
            // The share was held by the sealed attempt's admission throughout: it never ended, so no second object. (Upstream counts the
            // attempts started; here the share object is the same one.)
            Check(ReferenceEquals(share, A6Object(w.Host, "test.share(kept)")), "the same share object");
        }),
        Case("host.admission.requests.are-collected-with-their-decisions-once-the-object-ends", async () =>
        {
            await using var w = await A6Served(new(), "read");
            var made = new List<WeakReference?>();
            var doc = await w.Host.GetAsync(A6Doc, new() { ["id"] = "gc" }, A6Ctx);
            made.Add(A6Decision(w.Host, "test.doc(gc)"));
            await w.Set("write"); await A6Delivered(w.Host);
            made.Add(A6Decision(w.Host, "test.doc(gc)"));
            await w.Set("none"); await A6Delivered(w.Host);
            made.Add(A6Decision(w.Host, "test.doc(gc)"));
            Check(made.All(decision => decision?.Target is not null), "a decision per attempt");
            Equal(3, made.Select(decision => decision!.Target).Distinct().Count(), "three decisions");
            doc.Release();
            await A6Delivered(w.Host);
            await ExpectCollected([.. made.Select(decision => decision!)]);
        }),
        Case("host.admission.requests.cost-one-decision-read-per-change-of-a-source-and-no-factory-run-for-an-equal-one", async () =>
        {
            var (telemetry, _, counts) = await Metered();
            var code = new A6DocCode();
            await using var w = await A6Served(code, "read", telemetry: telemetry);
            var doc = await w.Host.GetAsync(A6Doc, new() { ["id"] = "a" }, A6Ctx);
            await A6Delivered(w.Host);
            var before = counts().GetValueOrDefault("chord.derive.computes");
            await w.Set("read", "guest");
            // The store's modes derive and the admission's derive over it: one compute each, nothing else.
            Equal(2d, counts().GetValueOrDefault("chord.derive.computes") - before, "two computes");
            Equal("a:read", string.Join(",", code.BuiltNow), "no factory run");
            doc.Release();
        }),

        // --- presence, linger, parked_long and retry ---
        Case("host.admission.presence.lists-a-holder-from-its-first-allow-until-a-denial", async () =>
        {
            await using var w = await A6Served(new());
            var present = w.Host.References(A6Doc);
            var doc = w.Host.Hold(A6Doc, new() { ["id"] = "a" }, A6Ctx);
            await A6Delivered(w.Host);
            Equal(0, present.Value.Count, "none while denied");
            await w.Set("read");
            await A6Delivered(w.Host);
            Equal("{\"id\":\"a\"}", string.Join(",", present.Value.Select(entry => entry.Parameters.ToJsonString())), "listed once allowed");
            // Another allowed value rebuilds the object: its holder stays listed.
            await w.Set("write");
            Equal(1, present.Value.Count, "still listed through the rebuild");
            await A6Delivered(w.Host);
            await w.Set("none");
            Equal(0, present.Value.Count, "unlisted by the denial");
            // A list made while denied shows none either.
            Equal(0, w.Host.References(A6Doc).Value.Count, "a new list shows none");
            doc.Release();
        }),
        Case("host.admission.presence.unlists-a-holder-when-the-attempt-that-parks-denied-is-not-one-a-seal-started-with-the-denial", async () =>
        {
            await using var w = await A6Served(new(), "read");
            var present = w.Host.References(A6Doc);
            var doc = await w.Host.GetAsync(A6Doc, new() { ["id"] = "a" }, A6Ctx);
            // The source fails: the object waits on it, still listed (no denial yet).
            await w.Set("throw");
            await A6Delivered(w.Host);
            Equal("failed Waiting 1", $"{doc.State.Value.Kind} {doc.State.Value.Cause?.Kind} {present.Value.Count}", "waiting, still listed");
            // The parked attempt reads the denial itself.
            await w.Set("none");
            await A6Delivered(w.Host);
            Equal("failed Denied 0", $"{doc.State.Value.Kind} {doc.State.Value.Cause?.Kind} {present.Value.Count}", "denied, unlisted");
            doc.Release();
        }),
        Case("host.admission.presence.ends-a-lingering-object-without-holders-when-its-decision-changes", async () =>
        {
            var code = new A6DocCode { Linger = TimeSpan.FromSeconds(60) };
            await using var w = await A6Served(code, "read");
            (await w.Host.GetAsync(A6Doc, new() { ["id"] = "a" }, A6Ctx)).Release();
            await A6Delivered(w.Host);
            Equal(1, w.Host.Activity.Value.Lingering, "lingering");
            await w.Set("write");
            await A6Delivered(w.Host);
            // Ended, not rebuilt for nobody.
            Equal(0, w.Host.Activity.Value.Lingering, "ended");
            Equal("a:read", string.Join(",", code.BuiltNow), "not rebuilt");
        }),
        Case("host.admission.presence.reports-no-parked-long-for-a-denial-of-the-object-or-of-a-dependent-parked-on-it", async () =>
        {
            // Upstream fakes the timers; here the ready timeout is short and real time passes.
            await using var w = await A6Served(new(), readyTimeout: TimeSpan.FromMilliseconds(50));
            var doc = w.Host.Hold(A6Doc, new() { ["id"] = "a" }, A6Ctx);
            var panel = w.Host.Hold(A6Panel, new() { ["id"] = "a" }, A6Ctx);
            await A6Delivered(w.Host);
            Equal("Denied Denied", $"{doc.State.Value.Cause?.Kind} {panel.State.Value.Cause?.Kind}", "both denied");
            await Task.Delay(500);
            Equal("", string.Join(",", A6Reports(w.ErrorsNow)), "no reports");
            doc.Release(); panel.Release();
        }),
        Case("host.admission.presence.ref-retry-on-a-denied-object-starts-nothing-on-one-parked-on-a-failed-source-it-passes-on", async () =>
        {
            var code = new A6DocCode(); var broken = new A6Flag(false);
            await using var w = await A6Served(code);
            var doc = w.Host.Hold(A6Doc, new() { ["id"] = "a" }, A6Ctx);
            await A6Delivered(w.Host);
            var attempt = A6Attempt(w.Host, "test.doc(a)");
            doc.Retry();
            await A6Delivered(w.Host);
            Equal("Denied", $"{doc.State.Value.Cause?.Kind}", "still denied");
            Check(ReferenceEquals(attempt, A6Attempt(w.Host, "test.doc(a)")), "no new attempt (upstream: no new decision)");
            // The source fails (its new code's native does not open): the allowed object waits on it instead.
            await w.Set("read");
            await A6Delivered(w.Host);
            broken.Value = true;
            await w.Reload(access: A6AccessFacet(new A6AccessCode { Broken = broken }));
            await A6Delivered(w.Host);
            Equal("failed Waiting", $"{doc.State.Value.Kind} {doc.State.Value.Cause?.Kind}", "waiting on its source");
            broken.Value = false;
            doc.Retry();
            await A6Delivered(w.Host);
            Equal("live", doc.State.Value.Kind, "the retry passed on to the source");
            Equal("a:read,a:read", string.Join(",", code.BuiltNow), "built");
            doc.Release();
        }),

        // --- declaration errors ---
        Case("host.admission.declaration-errors.refuse-an-admission-without-admit-admit-without-an-admission-and-either-on-a-local-type", async () =>
        {
            var plain = Contract.Remote("test.plain", new()
            {
                Version = 1, Params = new(), HostAddress = _ => Server.Address(), Variants = new() { ["only"] = new() { State = { ["n"] = Int() } } },
            });
            HostFactory factory = (_, _, _) => new(new Dictionary<string, object?> { ["n"] = Watchables.Constant(JsonValue.Create(1)) }, "only");
            Admit admit = (_, _, _) => Watchables.Constant(Admission.Allow(new JsonObject { ["mode"] = "read" }));
            Equal("plugin \"@test/app\": test.doc: its contract declares an admission: serve it with admit",
                await A6Refused(h => h.Serve(A6Doc, factory)), "an admission without admit");
            Equal("plugin \"@test/app\": test.plain: admit needs the contract to declare its admission",
                await A6Refused(h => h.Serve(plain, factory, new ServeOptions(Admit: admit))), "admit without an admission");
            Equal("plugin \"@test/app\": test.share: only a remote type takes an admission (admit)",
                await A6Refused(h => h.Serve(A6Share, (_, _, _) => new(new Dictionary<string, object?> { ["open"] = Watchables.Constant(JsonValue.Create("yes")) }),
                    new ServeOptions(Admit: admit))), "admit on a local type");
        }),
        Case("host.admission.declaration-errors.break-the-attempt-a-job-in-an-admitted-factory-a-declaration-outside-admits-four-a-value-that-is-no-decision", async () =>
        {
            await using (var job = await A6Served(new A6DocCode { Declare = (self, _) => self.Job(A6Work, new() { Run = (_, _) => Task.FromResult<object?>(JsonValue.Create(1)) }) }, "read"))
                Check((await A6Rejected(job.Host.GetAsync(A6Doc, new() { ["id"] = "a" }, A6Ctx))).Contains(
                    "test.doc(a): self.job is not declared in an admitted object: work for a person lives in a local store"), "a job is refused");
            // Upstream casts admit's self to a factory's self and calls writable on it ("writable is not a function"); the native
            // admission's self is no ISelf, so the cast throws.
            await using (var outside = await A6Served(new A6DocCode
            {
                Admit = (_, _, self) => { ((ISelf)self).Writable(1); return Watchables.Constant(Admission.Allow(new JsonObject { ["mode"] = "read" })); },
            }))
                Check((await S6Rejection(outside.Host.GetAsync(A6Doc, new() { ["id"] = "a" }, A6Ctx))).Message.Contains("ISelf"), "a declaration outside the four");
            await using var value = await A6Served(new A6DocCode { Admit = (_, _, _) => Watchables.Constant(new JsonObject { ["kind"] = "maybe" }) });
            Equal("test.doc(a): its admission's value is neither allow(value) nor deny(reason)",
                ((ChordError)await S6Rejection(value.Host.GetAsync(A6Doc, new() { ["id"] = "a" }, A6Ctx))).Message, "a value that is no decision");
            await value.Set("read");
            // Upstream's admit returns a plain allow(...) object; a native Admit returns a watchable, so the native form is a null one.
            await value.Reload(doc: A6DocFacet(new A6DocCode { Admit = (_, _, _) => null! }));
            Check((await A6Rejected(value.Host.GetAsync(A6Doc, new() { ["id"] = "b" }, A6Ctx))).Contains(
                "test.doc(b): admit returns a JSON watchable of allow(value) or deny(reason)"), "no watchable");
        }),

        // --- over a link ---
        Case("host.admission.over-a-link.a-denied-hold-is-failed-denied-a-denial-of-a-bound-one-drops-its-replicas-an-allow-binds-it-again", async () =>
        {
            await using var l = await A6OverLink();
            var (doc, states) = await A6HoldWatched(l.Client, A6Doc, new() { ["id"] = "a" });
            await A6Delivered(l.Server, l.Client);
            Equal("failed denied forbidden: no access", A6Cause(doc.State.Value), "denied over the link");
            Equal("forbidden: no access", await A6Rejected(l.Client.GetAsync(A6Doc, new() { ["id"] = "a" }, A6Ctx)), "a getter rejects");
            await l.Set("write");
            await A6Delivered(l.Server, l.Client);
            Equal("live writer \"t\"", $"{doc.State.Value.Kind} {doc.Variant} {A6Read(doc, "text")}", "the writer");
            var frames = await A6Values(doc, "text");
            await l.Set("none");
            await A6Delivered(l.Server, l.Client);
            Equal<string?>(null, doc.Variant, "no variant");
            Equal("throws forbidden: no access", A6Read(doc, "text"), "no value");
            Equal("{failed: no access}", string.Join(",", A6Now(frames)), "one failed call");
            await l.Set("read");
            await A6Delivered(l.Server, l.Client);
            Equal("live reader \"t\"", $"{doc.State.Value.Kind} {doc.Variant} {A6Read(doc, "text")}", "the reader");
            Equal("{failed: no access},\"t\"", string.Join(",", A6Now(frames)), "the value again");
            Equal("failed: no access,loading,live,failed: no access,loading,live", string.Join(",", A6Now(states)), "its states");
            doc.Release();
        }),
        Case("host.admission.over-a-link.a-running-call-is-answered-forbidden-revoked-by-a-denial-unavailable-by-another-value", async () =>
        {
            await using var l = await A6OverLink(new A6DocCode { Gate = new TaskCompletionSource().Task });
            await l.Set("write");
            var doc = await l.Client.GetAsync(A6Doc, new() { ["id"] = "a" }, A6Ctx);
            var first = A6Rejected(A6Write(doc, "x"));
            await A6Delivered(l.Server, l.Client);
            await l.Set("none");
            Check((await first).StartsWith("forbidden/revoked", StringComparison.Ordinal), "revoked: " + await first);
            await l.Set("write");
            await A6Delivered(l.Server, l.Client);
            var second = A6Rejected(A6Write(doc, "x"));
            await A6Delivered(l.Server, l.Client);
            await l.Set("read");
            Check((await second).StartsWith("unavailable", StringComparison.Ordinal), "unavailable: " + await second);
        }),
        Case("host.admission.over-a-link.a-call-still-waiting-for-a-call-window-slot-at-a-denial-is-rejected-forbidden-revoked-as-one-in-flight-and-never-sent", async () =>
        {
            var ran = new List<string>();
            await using var l = await A6OverLink(new A6DocCode
            {
                Gate = new TaskCompletionSource().Task, Write = (text, _) => { lock (ran) ran.Add(text); return Task.CompletedTask; },
            }, callWindow: 1);
            await l.Set("write");
            var doc = await l.Client.GetAsync(A6Doc, new() { ["id"] = "a" }, A6Ctx);
            // The server announced a window of one call: the others wait at the consumer. The running call's answer frees its slot, so the
            // second is sent and crosses the failed on the wire; the third still waits when it arrives.
            var running = A6Rejected(A6Write(doc, "running"));
            var crossing = A6Rejected(A6Write(doc, "crossing"));
            var waiting = A6Rejected(A6Write(doc, "waiting"));
            await A6Delivered(l.Server, l.Client);
            await l.Set("none");
            const string revoked = "forbidden/revoked: test.doc(a): no access";
            Equal($"{revoked},{revoked},{revoked}", $"{await running},{await crossing},{await waiting}", "all three revoked");
            await A6Delivered(l.Server, l.Client);
            Equal(0, A6Now(ran).Length, "none ran");
            // A call first made after the denial meets the denied reference: its plain forbidden.
            Equal("forbidden: no access", await A6Rejected(A6Write(doc, "late")), "a later call");
        }),
        Case("host.admission.over-a-link.a-second-reference-joins-a-denied-hold-and-shows-the-denial-both-go-live-with-the-allow", async () =>
        {
            await using var l = await A6OverLink();
            var first = l.Client.Hold(A6Doc, new() { ["id"] = "a" }, A6Ctx);
            await A6Delivered(l.Server, l.Client);
            var second = l.Client.Hold(A6Doc, new() { ["id"] = "a" }, A6Ctx);
            Equal("Denied", $"{second.State.Value.Cause?.Kind}", "the joining reference shows the denial");
            await l.Set("read");
            await A6Delivered(l.Server, l.Client);
            Equal("live live", $"{first.State.Value.Kind} {second.State.Value.Kind}", "both live");
            // One wire hold: one object, one presence entry.
            Equal(1, l.Server.References(A6Doc).Value.Count, "one presence entry");
        }),
        Case("host.admission.over-a-link.host-revoke-ends-a-denied-principals-object-its-admissions-requests-go-with-it", async () =>
        {
            var errors = new List<HostError>();
            await using var l = await A6OverLink(errors: errors,
                revokedBy: principal => principal["kind"]?.GetValue<string>() == "owner" ? [JsonValue.Create("owner")] : []);
            var doc = l.Client.Hold(A6Doc, new() { ["id"] = "a" }, A6Ctx);
            await A6Delivered(l.Server, l.Client);
            Equal("Denied", $"{doc.State.Value.Cause?.Kind}", "denied");
            var decision = A6Decision(l.Server, "test.doc(a)");
            Check(decision is not null, "a decision");
            // The server's own Access reference counts too.
            var references = l.Server.Activity.Value.References;
            l.Server.Revoke(JsonValue.Create("owner"), A6Ctx);
            await A6Delivered(l.Server, l.Client);
            Equal(references - 1, l.Server.Activity.Value.References, "the denied hold ended");
            await ExpectCollected(decision!);
            lock (errors) Equal(0, errors.Count, "no errors");
        }),

        // --- a reload of an admission's source (test 20) ---
        Case("host.admission.source-reload.to-an-equal-decision-the-running-call-completes-nothing-is-rebuilt-the-decision-is-kept-while-the-source-loads", async () =>
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var code = new A6DocCode { Gate = gate.Task, Opened = Task.CompletedTask };
            await using var w = await A6Served(code, "write");
            var doc = await w.Host.GetAsync(A6Doc, new() { ["id"] = "a" }, A6Ctx);
            var states = await A6States(doc);
            var writing = A6Write(doc, "x");
            // The source's successor stays loading until its native opens: the last decision is kept meanwhile.
            var report = await w.Reload(access: A6AccessFacet(new A6AccessCode { Opened = opened.Task }));
            Equal("test.access restarted", string.Join(",", report.Objects.Select(entry => $"{entry.Object} {entry.Fate}")), "the source restarted");
            await A6Delivered(w.Host);
            var access = w.Host.Hold(A6Access, new(), A6Ctx);
            Equal("loading live", $"{access.State.Value.Kind} {doc.State.Value.Kind}", "the source loads, the reader stays live");
            gate.SetResult();
            Equal("\"write:x\"", (await writing as JsonNode)?.ToJsonString(), "the running call completes");
            opened.SetResult();
            await A6Delivered(w.Host);
            Equal("live", access.State.Value.Kind, "the source is live");
            // The modes are the store's documents: the same decision, no new attempt, no native reopened.
            Equal("a:write", string.Join(",", code.BuiltNow), "not rebuilt");
            Equal("open", string.Join(",", code.NativesNow), "no native reopened");
            Equal(0, A6Now(states).Length, "no state change");
            Equal(0, w.ErrorsNow.Length, "no errors");
            access.Release();
        }),
        Case("host.admission.source-reload.whose-successor-parks-seals-its-readers-which-rebuild-once-it-is-back", async () =>
        {
            var code = new A6DocCode();
            await using var w = await A6Served(code, "read");
            var doc = await w.Host.GetAsync(A6Doc, new() { ["id"] = "a" }, A6Ctx);
            await w.Reload(access: A6AccessFacet(new A6AccessCode { NeedsMissing = true }));
            await A6Delivered(w.Host);
            Equal("failed Waiting", $"{doc.State.Value.Kind} {doc.State.Value.Cause?.Kind}", "parked on the source");
            // Upstream says "test.missing is not served here"; the native message for a type nobody serves is its 1.5.2 one.
            Equal("waiting for test.access: test.missing: no provider", doc.State.Value.Cause!.Error.Message, "names the source");
            await w.Reload(access: A6AccessFacet());
            await A6Delivered(w.Host);
            Equal("live a:read,a:read", $"{doc.State.Value.Kind} {string.Join(",", code.BuiltNow)}", "rebuilt");
        }),
        Case("host.admission.source-reload.whose-contract-is-bumped-without-accepts-seals-its-readers-until-their-code-follows-with-accepts-it-seals-nothing", async () =>
        {
            var code = new A6DocCode();
            await using var w = await A6Served(code, "read");
            var doc = await w.Host.GetAsync(A6Doc, new() { ["id"] = "a" }, A6Ctx);
            // Version 1 still bound through accepts (an added member needs no mapping): the reader is untouched.
            await w.Reload(access: A6AccessFacet(null, A6AccessV2, new AcceptsTable { [1] = new() }));
            await A6Delivered(w.Host);
            Equal("live a:read", $"{doc.State.Value.Kind} {string.Join(",", code.BuiltNow)}", "untouched");
            // Without accepts the reader's version is refused: failed, not loading, so it fails closed.
            await w.Reload(access: A6AccessFacet(null, A6AccessV2));
            await A6Delivered(w.Host);
            Equal("failed Waiting", $"{doc.State.Value.Kind} {doc.State.Value.Cause?.Kind}", "fails closed");
            Check(doc.State.Value.Cause!.Error.Message.Contains("test.access v1"), "names the version: " + doc.State.Value.Cause!.Error.Message);
            await w.Reload(access: A6AccessFacet(null, A6AccessV2, new AcceptsTable { [1] = new() }));
            await A6Delivered(w.Host);
            Equal("live a:read,a:read", $"{doc.State.Value.Kind} {string.Join(",", code.BuiltNow)}", "rebuilt");
        }),
        Case("host.admission.source-reload.that-removes-it-parks-its-readers-on-it-serving-it-again-rebuilds-them", async () =>
        {
            var code = new A6DocCode();
            await using var w = await A6Served(code, "read");
            var doc = await w.Host.GetAsync(A6Doc, new() { ["id"] = "a" }, A6Ctx);
            await w.Reload(access: Facet.Define((_, _) => { }));
            await A6Delivered(w.Host);
            Equal("failed Waiting", $"{doc.State.Value.Kind} {doc.State.Value.Cause?.Kind}", "parked on the removed source");
            await w.Reload(access: A6AccessFacet());
            // A new store object: its modes start over, and the reader is denied until it is allowed again.
            await A6Delivered(w.Host);
            Equal("failed Denied", $"{doc.State.Value.Kind} {doc.State.Value.Cause?.Kind}", "denied by the new store");
            await w.Set("read");
            await A6Delivered(w.Host);
            Equal("live a:read,a:read", $"{doc.State.Value.Kind} {string.Join(",", code.BuiltNow)}", "rebuilt");
        }),
        // --- a reload of the admitted type (2.5, test 20) ---
        Case("host.admission.type-reload.records-the-new-admit-without-effects-and-rehearses-the-new-factory-with-an-equal-admission-fingerprint", async () =>
        {
            var code = new A6DocCode(); var shares = new List<string>();
            await using var w = await A6Served(code, "read");
            var doc = await w.Host.GetAsync(A6Doc, new() { ["id"] = "a" }, A6Ctx);
            var rehearsed = new A6DocCode { Admit = A6AdmitBy(share: true), Declare = (_, _) => throw new InvalidOperationException("the new factory throws") };
            var report = await w.Reload(doc: A6DocFacet(rehearsed, shares: shares));
            // Rehearsed with the object's current value: its throw keeps the plugin on its old code.
            Equal("a:read", string.Join(",", rehearsed.BuiltNow), "rehearsed as a reader");
            Equal("@test/doc: plugin \"@test/doc\", test.doc(a): the new factory throws",
                string.Join(" | ", report.Failed.Select(failure => $"{failure.Plugin}: {failure.Error.Message}")), "the plugin failed");
            // The recorded admit declared a share: nothing was made for it.
            Equal(0, A6Now(shares).Length, "no share made");
            Equal("live a:read", $"{doc.State.Value.Kind} {string.Join(",", code.BuiltNow)}", "on its old code");
        }),
        Case("host.admission.type-reload.does-not-rehearse-across-another-admission-fingerprint-or-without-a-value-the-factorys-error-is-its-activations", async () =>
        {
            await using var w = await A6Served(new(), "read");
            var doc = await w.Host.GetAsync(A6Doc, new() { ["id"] = "a" }, A6Ctx);
            Action<ISelf, string> job = (self, _) => self.Job(A6Work, new() { Run = (_, _) => Task.FromResult<object?>(JsonValue.Create(1)) });
            int JobReports() => A6Reports(w.ErrorsNow).Count(report => report.Contains("self.job"));
            // Another fingerprint: the plugin applies, and the refused job fails the restarted object.
            var rehearsed = new A6DocCode { Declare = job };
            Equal(0, (await w.Reload(doc: A6DocFacet(rehearsed, A6DocV2))).Failed.Count, "applied across another fingerprint");
            await A6Delivered(w.Host);
            // Upstream reads no run straight after the reload; the native restarted object's activation is posted and may already
            // have run when the reload's await continues, so this counts the runs once it settled: one, the activation's.
            Equal(1, rehearsed.BuiltNow.Length, "not rehearsed: built once, by the restarted object");
            Equal("failed Broken", $"{doc.State.Value.Kind} {doc.State.Value.Cause?.Kind}", "broken by the refused job");
            Equal(1, JobReports(), "reported once");
            // No value (the object is denied): not rehearsed either, reported when it is allowed.
            await w.Reload(doc: A6DocFacet(new()));
            await w.Set("none");
            await A6Delivered(w.Host);
            Equal("failed Denied", $"{doc.State.Value.Kind} {doc.State.Value.Cause?.Kind}", "denied");
            lock (w.Errors) w.Errors.Clear();
            var ran = new A6DocCode { Declare = job };
            Equal(0, (await w.Reload(doc: A6DocFacet(ran))).Failed.Count, "applied while denied");
            Equal(" ", $"{string.Join(",", ran.BuiltNow)} {string.Join(",", A6Reports(w.ErrorsNow))}", "nothing ran, nothing reported");
            await w.Set("read");
            await A6Delivered(w.Host);
            Equal("a:read", string.Join(",", ran.BuiltNow), "ran once allowed");
            Equal(1, JobReports(), "reported at its activation");
        }),
        Case("host.admission.type-reload.a-dependency-only-the-newly-admitted-branch-declares-parks-the-object-after-the-commit", async () =>
        {
            await using var w = await A6Served(new(), "read");
            var doc = await w.Host.GetAsync(A6Doc, new() { ["id"] = "a" }, A6Ctx);
            // Rehearsed as a reader: the writer's request is not recorded.
            var code = new A6DocCode { Declare = (self, mode) => { if (mode == "write") self.Get(A6Missing, new()); } };
            Equal(0, (await w.Reload(doc: A6DocFacet(code))).Failed.Count, "the reload committed");
            await A6Delivered(w.Host);
            Equal("live", doc.State.Value.Kind, "live as a reader");
            await w.Set("write");
            await A6Delivered(w.Host);
            Equal("failed Waiting", $"{doc.State.Value.Kind} {doc.State.Value.Cause?.Kind}", "parked on the writer's dependency");
        }),
        Case("host.admission.type-reload.a-denial-between-staging-and-activation-the-new-factory-never-runs", async () =>
        {
            // Upstream's denial lands between the reload's staging and its successor's activation in the microtasks of one turn. A native
            // reload runs on the dispatcher, so the gap is made by a call the old attempt drains: the successor's factory waits for that
            // drain, and the denial arrives meanwhile.
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var w = await A6Served(new A6DocCode { Gate = gate.Task }, "write");
            var doc = await w.Host.GetAsync(A6Doc, new() { ["id"] = "a" }, A6Ctx);
            var writing = S6Rejection(A6Write(doc, "x"));
            var code = new A6DocCode();
            await w.Reload(doc: A6DocFacet(code));
            await w.Set("none");
            gate.SetResult();
            await writing;
            await A6Delivered(w.Host);
            Equal("failed Denied", $"{doc.State.Value.Kind} {doc.State.Value.Cause?.Kind}", "denied");
            // Staging rehearsed it once; no attempt ran it.
            Equal("a:write", string.Join(",", code.BuiltNow), "rehearsed only");
        }),
        Case("host.admission.type-reload.a-decision-change-while-its-old-attempt-drains-seals-that-attempt-at-once", async () =>
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var w = await A6Served(new A6DocCode { Gate = gate.Task }, "write");
            var doc = await w.Host.GetAsync(A6Doc, new() { ["id"] = "a" }, A6Ctx);
            var writing = A6Rejected(A6Write(doc, "x"));
            var code = new A6DocCode();
            await w.Reload(doc: A6DocFacet(code));
            // The old attempt drains its call; its successor waits for it.
            Equal("live", doc.State.Value.Kind, "live while it drains");
            var present = w.Host.References(A6Doc);
            Equal(1, present.Value.Count, "listed");
            await w.Set("none");
            // The cut is the denial's flush, not the successor's later admission: no value, no presence, from here on.
            Equal("failed Denied", $"{doc.State.Value.Kind} {doc.State.Value.Cause?.Kind}", "denied at the cut");
            Equal("throws forbidden: no access", A6Read(doc, "text"), "no value");
            Equal(0, present.Value.Count, "unlisted");
            Check((await writing).StartsWith("forbidden/revoked", StringComparison.Ordinal), "the drained call is revoked: " + await writing);
            await A6Delivered(w.Host);
            Equal("failed Denied", $"{doc.State.Value.Kind} {doc.State.Value.Cause?.Kind}", "still denied");
            Equal("a:write", string.Join(",", code.BuiltNow), "rehearsed only");
            // Nothing failed in the flush that cut it (a reference released there would have).
            Equal(0, w.ErrorsNow.Length, "no errors");
            gate.SetResult();
        }),
        Case("host.admission.type-reload.a-denial-while-its-old-attempt-drains-is-shown-by-that-attempts-own-decision-whatever-its-successors-admission-waits-for", async () =>
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var w = await A6Served(new A6DocCode { Gate = gate.Task }, "write");
            var doc = await w.Host.GetAsync(A6Doc, new() { ["id"] = "a" }, A6Ctx);
            var writing = A6Rejected(A6Write(doc, "x"));
            // The new admission also needs a type nobody serves: the successor parks on it and decides nothing.
            Admit waits = (parameters, principal, self) => { self.Get(A6Missing, new()); return A6AdmitBy()(parameters, principal, self); };
            await w.Reload(doc: A6DocFacet(new A6DocCode { Admit = waits }));
            await A6Delivered(w.Host);
            Equal("failed Waiting", $"{doc.State.Value.Kind} {doc.State.Value.Cause?.Kind}", "the successor parks");
            Equal("\"t\"", A6Read(doc, "text"), "the draining attempt's value");
            await w.Set("none");
            Equal("failed Denied", $"{doc.State.Value.Kind} {doc.State.Value.Cause?.Kind}", "denied by the old attempt's decision");
            Equal("throws forbidden: no access", A6Read(doc, "text"), "no value");
            Check((await writing).StartsWith("forbidden/revoked", StringComparison.Ordinal), "the drained call is revoked: " + await writing);
            gate.SetResult();
        }),
        Case("host.admission.type-reload.another-value-while-its-old-attempt-drains-seals-that-attempt-its-call-is-rejected-unavailable-its-holders-load", async () =>
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var w = await A6Served(new A6DocCode { Gate = gate.Task }, "write");
            var doc = await w.Host.GetAsync(A6Doc, new() { ["id"] = "a" }, A6Ctx);
            var writing = A6Rejected(A6Write(doc, "x"));
            // Upstream reads doc.text first while it loads and gets the last value from the cut instance; a native reference member made
            // while its object loads has none (a pre-existing difference of the port), so the member is made while live.
            Equal("\"t\"", A6Read(doc, "text"), "live");
            await w.Reload(doc: A6DocFacet(new()));
            await w.Set("read");
            Equal("loading \"t\"", $"{doc.State.Value.Kind} {A6Read(doc, "text")}", "loading with its last value");
            Check((await writing).StartsWith("unavailable", StringComparison.Ordinal), "the drained call: " + await writing);
            gate.SetResult();
            await A6Delivered(w.Host);
            Equal("live reader", $"{doc.State.Value.Kind} {doc.Variant}", "the reader");
            Equal(0, w.ErrorsNow.Length, "no errors");
        }),
        Case("host.admission.type-reload.a-decision-change-while-the-attempt-opens-a-native-abandons-it-the-native-closes-the-next-attempt-admits", async () =>
        {
            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var code = new A6DocCode { Opened = opened.Task };
            await using var w = await A6Served(code, "read");
            var doc = w.Host.Hold(A6Doc, new() { ["id"] = "a" }, A6Ctx);
            await A6Delivered(w.Host);
            Equal("a:read loading", $"{string.Join(",", code.BuiltNow)} {doc.State.Value.Kind}", "opening");
            await w.Set("none");
            Equal("failed Denied", $"{doc.State.Value.Kind} {doc.State.Value.Cause?.Kind}", "denied at once");
            opened.SetResult();
            await A6Delivered(w.Host);
            // Opened after its attempt was abandoned: closed at once, and nothing went live.
            Equal("open,close", string.Join(",", code.NativesNow), "opened then closed");
            Equal("failed Denied", $"{doc.State.Value.Kind} {doc.State.Value.Cause?.Kind}", "still denied");
            doc.Release();
        }),

        // --- an admission that reads a named query ---
        Case("host.admission.named-query.parked-on-a-read-that-failed-is-its-attempts-first-check-a-reload-of-its-type-reports-it-and-does-not-wait-for-the-read", async () =>
        {
            var failing = new A6Flag(false);
            var store = await MemoryStore();
            var h = Q7Host(store, address: Server.Address(), principal: new() { ["kind"] = "server" });
            try
            {
                var shelf = Facet.Define((f, _) => f.Serve(A6Shelf, (_, _, self) =>
                {
                    var rows = ((IStoreSelf)self).Table(A6Items);
                    return new(new Dictionary<string, object?>
                    {
                        ["items"] = ((IStoreSelf)self).Queries(rows, new QueriesOptions(Map: (row, _) =>
                        {
                            if (failing.Value) throw new InvalidOperationException("the mapping is unavailable");
                            return new JsonObject { ["id"] = row["id"]!.DeepClone() };
                        }), Q7Builders(("all", Q7Builder(_ => Tables.Query(A6Items).OrderBy(item => item["id"]))))),
                        ["put"] = Q7Tx(self, (tx, id) => tx.Put(rows, new JsonObject { ["id"] = id!.DeepClone() })),
                    });
                }));
                IReadOnlyList<PluginFacet> Plugins() => [new("@test/shelf", shelf), new("@test/reader", A6ReaderFacet())];
                await h.LoadAsync(Plugins(), A6Ctx);
                var api = await h.GetAsync(A6Shelf, new(), A6Ctx);
                await api.CallAsync("put", JsonValue.Create("a"), A6Ctx);
                var held = await h.GetAsync(A6Reader, new(), A6Ctx);
                // The read's next run fails: the decision cannot be read, so the object is sealed and parks on the read.
                failing.Value = true;
                await api.CallAsync("put", JsonValue.Create("b"), A6Ctx);
                await A6Delivered(h);
                Equal("failed Waiting", $"{held.State.Value.Kind} {held.State.Value.Cause?.Kind}", "parked on the read");
                var reloading = h.ReloadAsync(Plugins(), A6Ctx);
                Check(await Task.WhenAny(reloading, Task.Delay(1000)) == reloading, "the reload does not wait for the read");
                Equal("test.reader parked Waiting", string.Join(",", (await reloading).Objects.Select(entry => $"{entry.Object} {entry.Fate} {entry.Failure?.Kind}")),
                    "reported parked");
                // The read recovers: the parked attempt goes on.
                failing.Value = false;
                await api.CallAsync("put", JsonValue.Create("c"), A6Ctx);
                await A6Delivered(h);
                Equal("live", held.State.Value.Kind, "live again");
            }
            finally { await h.CloseAsync(A6Ctx); await store.CloseAsync(A6Ctx); }
        }),

        // --- a commit in flight at the cut ---
        Case("host.admission.commit-in-flight.stays-durable-the-sealed-attempt-publishes-nothing-after-it", async () =>
        {
            var store = await MemoryStore();
            var errors = new List<HostError>();
            var h = Q7Host(store, errors, address: Server.Address(), principal: new() { ["kind"] = "server" });
            try
            {
                Func<string, ISelf, Task> write = (text, _) => h.RootAsync(async ctx =>
                {
                    var journal = await ctx.GetAsync(A6Journal, new());
                    await journal.CallAsync("append", JsonValue.Create(text), ctx.Context);
                    return true;
                }, A6Ctx);
                await h.LoadAsync([new("@test/access", A6AccessFacet()), new("@test/doc", A6DocFacet(new A6DocCode { Write = write })), A6JournalPlugin()], A6Ctx);
                var access = await h.GetAsync(A6Access, new(), A6Ctx);
                await access.CallAsync("set", new JsonObject { ["who"] = "server", ["mode"] = "write" }, A6Ctx);
                var doc = await h.GetAsync(A6Doc, new() { ["id"] = "a" }, A6Ctx);
                var frames = await A6Values(doc, "text");
                var held = HoldNextCommit(store);
                var writing = A6Rejected(A6Write(doc, "kept"));
                await held.Entered;
                // The cut, with the journal's commit admitted and its step running.
                await access.CallAsync("set", new JsonObject { ["who"] = "server", ["mode"] = "none" }, A6Ctx);
                Check((await writing).StartsWith("forbidden/revoked", StringComparison.Ordinal), "the call is revoked: " + await writing);
                held.Release();
                await A6Delivered(h);
                var journal = await h.GetAsync(A6Journal, new(), A6Ctx);
                Equal("1", (await journal.CallAsync("has", JsonValue.Create("kept"), A6Ctx) as JsonNode)?.ToJsonString(), "the journal's commit is durable");
                // The handler ran on past the commit; its own change was refused, and the denied holder got the denial alone.
                Equal("{failed: no access}", string.Join(",", A6Now(frames)), "the denial alone");
            }
            finally { await h.CloseAsync(A6Ctx); await store.CloseAsync(A6Ctx); }
        }),
        // --- dependencies.test.ts: a denied dependency (slice 14 §2.2) ---
        Case("host.dependencies.denied.parks-its-dependent-showing-the-denial-not-broken-as-an-authority-refusal-it-goes-on-with-the-next-bound", async () =>
        {
            var (host, peer, errors) = await A6ViewOverParent();
            await using var closing = host;
            var view = host.Hold(A6View, new() { ["id"] = 1 }, A6Ctx);
            var hold = (await peer.Next<HoldMessage>()).Hold;
            peer.Send(new FailedMessage(hold, new("denied", WireError.FromException(new ChordError("forbidden", "no access")))));
            await A6Delivered(host);
            Equal("failed denied forbidden: no access", A6Cause(view.State.Value), "the dependent shows the denial");
            Equal<double?>(null, view.State.Value.RetryAt, "no retry");
            // The same refusal as broken (a route's authorize) never heals: the difference is the cause's kind.
            Equal("", string.Join(",", A6Reports(A6Locked(errors))), "no failures reported");
            H6BindHold(peer, hold);
            await A6Delivered(host);
            Equal("live 0", $"{view.State.Value.Kind} {A6Read(view, "n")}", "live with the next bound");
        }),
        Case("host.dependencies.denied.is-no-parked-long-report-however-long-it-stays-denied", async () =>
        {
            // Upstream fakes the timers; here the ready timeout is short and real time passes.
            var (host, peer, errors) = await A6ViewOverParent(TimeSpan.FromMilliseconds(50));
            await using var closing = host;
            var view = host.Hold(A6View, new() { ["id"] = 1 }, A6Ctx);
            var hold = (await peer.Next<HoldMessage>()).Hold;
            peer.Send(new FailedMessage(hold, new("denied", WireError.FromException(new ChordError("forbidden", "no access")))));
            await Task.Delay(500);
            Equal("failed Denied", $"{view.State.Value.Kind} {view.State.Value.Cause?.Kind}", "denied");
            Equal("", string.Join(",", A6Locked(errors).Select(error => error.Kind)), "no reports");
        }),

        // --- reference-source.test.ts: a denied reference's source (slice 14 §2.2) ---
        // Upstream drives a ReferenceSource by hand; the native one is the reference's member (ReferenceMember), driven here through a
        // host: the object bound, followed, denied, told the same denial again, and allowed.
        Case("host.reference-source.denied.keeps-no-value-reads-throw-the-denial-a-watch-opened-now-has-nothing-to-start-at-the-next-bind-is-whole", async () =>
        {
            await using var w = await A6Served(new(), "read");
            var doc = await w.Host.GetAsync(A6Doc, new() { ["id"] = "a" }, A6Ctx);
            var calls = ChangesOf(await doc.Member("text").WatchAsync(A6Ctx));
            string[] Calls() { lock (calls) return [.. calls.Select(call => call is WatchValueCall<JsonNode?> value
                ? $"{value.Value?.ToJsonString()} {OpsJson(value.Ops).ToJsonString()}" : ((Exception)((WatchFailedCall<JsonNode?>)call).Error!).Message)]; }
            await w.Set("none");
            await A6Delivered(w.Host);
            Equal("throws forbidden: no access", A6Read(doc, "text"), "reads throw the denial");
            // Unlike a failed target's member: no last value to start a watch at.
            Equal("forbidden: no access", await A6Rejected(doc.Member("text").WatchAsync(A6Ctx)), "a watch opened now");
            // The same denial told again (its object notifies for another reason) is no second call.
            await w.Set("none", "guest");
            await w.Set("none");
            await A6Delivered(w.Host);
            await w.Set("read");
            await A6Delivered(w.Host);
            Equal("no access|\"t\" [[\"r\",\"t\"]]", string.Join("|", Calls()), "one failure, then a whole value");
        }),
    ];
}
