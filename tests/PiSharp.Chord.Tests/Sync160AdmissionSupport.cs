// PiSharp-only (no upstream Pi source): the helpers of the chord-admission package's 1.6.0 cases (Host/Sync160AdmissionCases.cs,
// Remote/Sync160AdmissionCases.cs, Remote/Sync160RouteAdmissionCases.cs). They stand in for the fixtures of
// packages/chord/test/host/admission.test.ts (Modes, Access, AccessV2, Share, Missing, Doc, DocV2, Panel, Text, accessFacet, docFacet,
// admitBy, decide, served, told, reports) and the parts of packages/chord/test/support.ts it uses (statesOf, failLive) at
// faac585884ae2c013a6b1400f3e9e77f10b34bac, written against the native hosts. Upstream's `delivered()` (let the microtasks and the
// watch deliveries run) is A6Delivered: the host's dispatcher flushed, and time for the pool threads that deliver to watches.
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Documents;
using PiSharp.Chord.Host;
using PiSharp.Chord.State;
using static PiSharp.Chord.Contracts.Schemas;
using static ChordSupport;

internal static partial class Program
{
    internal static readonly StateDefinition A6Modes = StateDefinition.Define("modes", new()
    { Version = 1, Schema = Obj(("modes", Map(Str()))), Initial = () => new() { ["modes"] = new JsonObject() } });

    /// <summary>Who may do what, by principal kind: the store the admissions read.</summary>
    internal static readonly Contract A6Access = Contract.Local("test.access", new()
    {
        Version = 1, Params = new(),
        Members = new() { ["modes"] = Map(Str()), ["set"] = Method(Obj(("who", Str()), ("mode", Str()))) },
    });

    /// <summary><see cref="A6Access"/> with one more member: version 1's consumers bind it only through accepts.</summary>
    internal static readonly Contract A6AccessV2 = Contract.Evolve(A6Access, e => e.AddState("extra", Str()));

    /// <summary>A per-document source of the admission, with params: it ends with its last reference.</summary>
    internal static readonly Contract A6Share = Contract.Local("test.share", new() { Version = 1, Params = new() { ["id"] = Str() }, Members = new() { ["open"] = Str() } });

    /// <summary>A type nobody serves.</summary>
    internal static readonly Contract A6Missing = Contract.Local("test.missing", new() { Version = 1, Params = new(), Members = new() { ["n"] = Int() } });

    internal static readonly JsonObject A6Mode = Obj(("mode", OneOf("read", "write")));

    /// <summary>The admitted type: a reader or a writer variant by the admitted mode.</summary>
    internal static readonly Contract A6Doc = Contract.Remote("test.doc", new()
    {
        Version = 1, Params = new() { ["id"] = Str() }, HostAddress = _ => Server.Address(), Admission = A6Mode,
        Variants = new()
        {
            ["reader"] = new() { State = { ["text"] = Str() } },
            ["writer"] = new() { State = { ["text"] = Str() }, Methods = { ["write"] = Method(Str(), Str()) } },
        },
    });

    /// <summary><see cref="A6Doc"/> with another admission schema (the same wire fingerprint): a reload does not rehearse across it.</summary>
    internal static readonly Contract A6DocV2 = Contract.Evolve(A6Doc, e => e.Admission(Obj(("mode", OneOf("read", "write")), ("since", Int()))));

    /// <summary>A consumer of a document on the same host: a derive over its text.</summary>
    internal static readonly Contract A6Panel = Contract.Local("test.panel", new() { Version = 1, Params = new() { ["id"] = Str() }, Members = new() { ["shout"] = Str() } });

    internal static readonly StateDefinition A6Text = StateDefinition.Define("text", new()
    { Version = 1, Schema = Obj(("text", Str())), Initial = () => new() { ["text"] = "t" } });

    /// <summary>What a served <see cref="A6Access"/> does besides keeping the modes.</summary>
    internal sealed class A6AccessCode
    {
        /// <summary>A native that opens once this completes: a reload's successor stays loading meanwhile.</summary>
        public Task? Opened { get; init; }
        /// <summary>Declares a type nobody serves: the attempt parks failed waiting.</summary>
        public bool NeedsMissing { get; init; }
        /// <summary>Its native's open fails broken while this says so.</summary>
        public A6Flag? Broken { get; init; }
        /// <summary>The factory's selves, for A6FailLive.</summary>
        public List<ISelf>? Selves { get; init; }
    }

    internal sealed class A6Flag(bool value) { public volatile bool Value = value; }

    /// <summary>A facet serving <see cref="A6Access"/> (as <paramref name="contract"/>).</summary>
    internal static Facet A6AccessFacet(A6AccessCode? code = null, Contract? contract = null, AcceptsTable? accepts = null)
    {
        code ??= new(); contract ??= A6Access;
        return Facet.Define((h, _) => h.Serve(contract, (_, _, self) =>
        {
            if (code.Selves is { } selves) lock (selves) selves.Add(self);
            var state = self.State(A6Modes);
            if (code.Opened is not null || code.Broken is not null)
                self.Open<object?>(async _ =>
                {
                    if (code.Broken?.Value == true) throw Failure.Broken(new InvalidOperationException("access is broken"));
                    if (code.Opened is { } opened) await opened;
                    return null;
                }, (_, _, _) => Task.CompletedTask);
            if (code.NeedsMissing) self.Get(A6Missing, new());
            var members = new Dictionary<string, object?>
            {
                ["modes"] = self.Derive([state], values => ((JsonNode)values[0]!)["modes"]!.DeepClone()),
                ["set"] = (HostMethod)(async (arguments, ctx) =>
                {
                    var who = ((JsonNode)arguments!)["who"]!.GetValue<string>(); var mode = ((JsonNode)arguments!)["mode"]!.GetValue<string>();
                    await state.ChangeAsync(draft => { ((JsonObject)draft["modes"]!)[who] = mode; }, ctx.Context);
                    return null;
                }),
            };
            if (ReferenceEquals(contract, A6AccessV2)) members["extra"] = Watchables.Constant(JsonValue.Create("x"));
            return new(members);
        }, accepts is null ? null : new ServeOptions(Accepts: accepts)));
    }

    /// <summary>What a mode means to the admission.</summary>
    internal static JsonNode A6Decide(string? mode)
    {
        if (mode == "throw") throw new InvalidOperationException("the rule threw");
        return mode is "read" or "write" ? Admission.Allow(new JsonObject { ["mode"] = mode }) : Admission.Deny("no access");
    }

    internal static string? A6KindOf(JsonObject principal) => principal["kind"]?.GetValue<string>();

    /// <summary>The admission of <see cref="A6Doc"/>: the caller's mode in Access; with <paramref name="share"/>, its per-document source
    /// too.</summary>
    internal static Admit A6AdmitBy(bool share = false) => (parameters, principal, self) =>
    {
        var modes = self.Get(A6Access, new()).Member("modes");
        if (!share) return self.Derive([modes], values => A6Decide(((JsonNode?)values[0])?[A6KindOf(principal)!]?.GetValue<string>()));
        var open = self.Get(A6Share, new() { ["id"] = parameters["id"]!.DeepClone() }).Member("open");
        return self.Derive([modes, open], values => A6Decide(((JsonNode?)values[0])?[A6KindOf(principal)!]?.GetValue<string>()));
    };

    /// <summary>What a served <see cref="A6Doc"/> does.</summary>
    internal sealed class A6DocCode
    {
        /// <summary>Its factory's runs, <c>id:mode</c>.</summary>
        public List<string> Built { get; } = [];
        /// <summary>What write waits for.</summary>
        public Task? Gate { get; init; }
        /// <summary>A native that opens once this completes; its opens and closes are logged in <see cref="Natives"/>.</summary>
        public Task? Opened { get; init; }
        public List<string> Natives { get; } = [];
        /// <summary>What the native's close waits for.</summary>
        public Task? Closing { get; init; }
        public TimeSpan? Linger { get; init; }
        public Admit? Admit { get; init; }
        /// <summary>A declaration only the factory makes (a job: refused; a throw).</summary>
        public Action<ISelf, string>? Declare { get; init; }
        /// <summary>What write does before it answers.</summary>
        public Func<string, ISelf, Task>? Write { get; init; }

        public string[] BuiltNow { get { lock (Built) return [.. Built]; } }
        public string[] NativesNow { get { lock (Natives) return [.. Natives]; } }
    }

    /// <summary>A facet serving <see cref="A6Doc"/> (as <paramref name="contract"/>) and Share and Panel.</summary>
    internal static Facet A6DocFacet(A6DocCode code, Contract? contract = null, List<string>? shares = null) => Facet.Define((h, _) =>
    {
        h.Serve(contract ?? A6Doc, (parameters, _, admitted, self) =>
        {
            var id = parameters["id"]!.GetValue<string>(); var mode = admitted!["mode"]!.GetValue<string>();
            lock (code.Built) code.Built.Add($"{id}:{mode}");
            code.Declare?.Invoke(self, mode);
            var text = self.State(A6Text);
            if (code.Opened is { } opened)
                self.Open<object?>(async _ =>
                {
                    await opened;
                    lock (code.Natives) code.Natives.Add("open");
                    return null;
                }, async (_, _, _) =>
                {
                    if (code.Closing is { } closing) await closing;
                    lock (code.Natives) code.Natives.Add("close");
                });
            var shown = self.Derive([text], values => ((JsonNode)values[0]!)["text"]!.DeepClone());
            if (mode == "read") return new(new Dictionary<string, object?> { ["text"] = shown }, "reader");
            HostMethod write = async (arguments, ctx) =>
            {
                var next = ((JsonNode)arguments!).GetValue<string>();
                // As a handler waits: its caller's cancellation and its attempt's seal end the wait.
                await (code.Gate ?? Task.CompletedTask).WaitAsync(ctx.AbortSignal);
                if (code.Write is { } before) await before(next, self);
                await text.ChangeAsync(draft => { draft["text"] = next; }, ctx.Context);
                return JsonValue.Create($"{mode}:{next}");
            };
            return new(new Dictionary<string, object?> { ["text"] = shown, ["write"] = write }, "writer");
        }, new ServeOptions(Linger: code.Linger, Admit: code.Admit ?? A6AdmitBy()));
        h.Serve(A6Share, (parameters, _, _) =>
        {
            if (shares is not null) lock (shares) shares.Add(parameters["id"]!.GetValue<string>());
            return new(new Dictionary<string, object?> { ["open"] = Watchables.Constant(JsonValue.Create("yes")) });
        });
        h.Serve(A6Panel, (parameters, _, self) =>
        {
            var doc = self.Get(A6Doc, new() { ["id"] = parameters["id"]!.DeepClone() });
            return new(new Dictionary<string, object?>
            {
                ["shout"] = self.Derive([doc.Member("text")], values => JsonValue.Create(((JsonNode)values[0]!).GetValue<string>().ToUpperInvariant())),
            });
        });
    });

    /// <summary>A served Access and Doc: the host, its errors, setting a principal kind's mode, and reloading with other facets.</summary>
    internal sealed class A6World : IAsyncDisposable
    {
        internal required ChordHost Host { get; init; }
        internal required List<HostError> Errors { get; init; }
        internal required IReference Access { get; init; }
        internal required Dictionary<string, Facet> Plugins { get; init; }

        /// <summary>Sets <paramref name="who"/>'s mode (the server's by default), as a method call on the Access object.</summary>
        internal Task Set(string mode, string who = "server") =>
            Access.CallAsync("set", new JsonObject { ["who"] = who, ["mode"] = mode }, ChordContext.Background);

        internal Task<ReloadReport> Reload(Facet? access = null, Facet? doc = null)
        {
            if (access is not null) Plugins["access"] = access;
            if (doc is not null) Plugins["doc"] = doc;
            return Host.ReloadAsync(Facets(Plugins), ChordContext.Background);
        }

        internal static IReadOnlyList<PluginFacet> Facets(Dictionary<string, Facet> plugins) =>
            [new("@test/access", plugins["access"]), new("@test/doc", plugins["doc"])];

        internal HostError[] ErrorsNow { get { lock (Errors) return [.. Errors]; } }

        public async ValueTask DisposeAsync() => await Host.CloseAsync(ChordContext.Background);
    }

    /// <summary>A server with Access and Doc in two plugins, its errors, and the server's mode set to <paramref name="mode"/> (upstream
    /// <c>served</c>).</summary>
    internal static async Task<A6World> A6Served(A6DocCode code, string? mode = null, A6AccessCode? access = null, TimeSpan? readyTimeout = null,
        PiSharp.Telemetry.ITelemetryContext? telemetry = null, List<string>? shares = null)
    {
        var errors = new List<HostError>();
        var host = Hosts.Create(new()
        {
            Address = Server.Address(), Principal = new() { ["kind"] = "server" }, ReadyTimeout = readyTimeout ?? TimeSpan.FromSeconds(60),
            CloseTimeout = TimeSpan.FromSeconds(2), OnError = error => { lock (errors) errors.Add(error); }, Telemetry = telemetry,
        });
        var plugins = new Dictionary<string, Facet> { ["access"] = A6AccessFacet(access), ["doc"] = A6DocFacet(code, shares: shares) };
        var loaded = await host.LoadAsync(A6World.Facets(plugins), ChordContext.Background);
        Equal(0, loaded.Failed.Count, "the facets loaded");
        var world = new A6World { Host = host, Errors = errors, Access = await host.GetAsync(A6Access, new(), ChordContext.Background), Plugins = plugins };
        if (mode is not null) await world.Set(mode);
        return world;
    }

    /// <summary>Upstream <c>delivered()</c>: the host's dispatcher flushed, and time for the watch deliveries that run on pool threads.</summary>
    internal static Task A6Delivered(params ChordHost[] hosts) => S6Settled(hosts);

    /// <summary>An error's code, reason and message, as <c>code/reason: message</c> (upstream <c>told</c>).</summary>
    internal static string A6Told(object? error) => error is ChordError chord
        ? (chord.Reason is null ? $"{chord.Code}: {chord.Message}" : $"{chord.Code}/{chord.Reason}: {chord.Message}")
        : $"{error?.GetType().Name}: {(error as Exception)?.Message}";

    /// <summary>A reference state as upstream's tests write it: <c>failed: message</c>, or the kind.</summary>
    internal static string A6Shown(RefState state) => state.Kind == "failed" ? $"failed: {state.Cause!.Error.Message}" : state.Kind;

    /// <summary>A failed state's cause kind and error, as <c>failed denied forbidden: no access</c>; the kind otherwise.</summary>
    internal static string A6Cause(RefState state) => state.Cause is { } cause
        ? $"{state.Kind} {cause.Kind.ToString().ToLowerInvariant()} {A6Told(cause.Error)}" : state.Kind;

    /// <summary>Starts <paramref name="watch"/> and records each state it gets (upstream <c>statesOf</c>).</summary>
    internal static List<string> A6StatesOf(INativeWatchHandle<RefState> watch)
    {
        var states = new List<string>();
        watch.Start((call, _) =>
        {
            if (call is NativeWatchValueCall<RefState> value) lock (states) states.Add(A6Shown(value.Value));
            return Task.CompletedTask;
        });
        return states;
    }

    internal static async Task<List<string>> A6States(IReference reference) => A6StatesOf(await reference.State.WatchAsync(ChordContext.Background));

    /// <summary>Holds <paramref name="type"/> and watches its state in one turn of <paramref name="host"/> (upstream holds and watches
    /// before its microtasks run): the watch starts at what the hold shows before its activation ran.</summary>
    internal static async Task<(IReference Reference, List<string> States)> A6HoldWatched(ChordHost host, Contract type, JsonObject parameters)
    {
        var (reference, watching) = host.Dispatcher.Invoke(() =>
        {
            var held = host.Hold(type, parameters, ChordContext.Background);
            return (held, held.State.WatchAsync(ChordContext.Background));
        });
        return (reference, A6StatesOf(await watching));
    }

    internal static string[] A6Now(List<string> list) { lock (list) return [.. list]; }

    /// <summary>The values a member watch got, a failure as <c>{failed: message}</c>, as JSON (upstream <c>valuesOf(changesOf(...))</c>).</summary>
    internal static List<string> A6ValuesOf(IWatchHandle<JsonNode?> watch)
    {
        var values = new List<string>();
        watch.Start((call, _) =>
        {
            lock (values) values.Add(call is WatchValueCall<JsonNode?> value ? value.Value?.ToJsonString() ?? "null"
                : $"{{failed: {((Exception)((WatchFailedCall<JsonNode?>)call).Error!).Message}}}");
            return Task.CompletedTask;
        });
        return values;
    }

    internal static async Task<List<string>> A6Values(IReference reference, string member) =>
        A6ValuesOf(await reference.Member(member).WatchAsync(ChordContext.Background));

    /// <summary>The member's value now, or <c>throws code: message</c>.</summary>
    internal static string A6Read(IReference reference, string member)
    {
        try { return reference.Member(member).Value?.ToJsonString() ?? "null"; }
        catch (Exception error) { return "throws " + A6Told(error); }
    }

    /// <summary>What <paramref name="work"/> rejects with, told (upstream <c>told(await rejection(...))</c>).</summary>
    internal static async Task<string> A6Rejected(Task work) => A6Told(await S6Rejection(work));

    /// <summary>Fails the live attempt behind <paramref name="self"/> with <paramref name="error"/> (upstream <c>failLive</c>).</summary>
    internal static void A6FailLive(ChordHost host, ISelf self, ChordError error) =>
        host.Dispatcher.Invoke(() => ((Self)self).Attempt.Owner.Fail(error));

    /// <summary>The reported activation failures and long parks, by kind and message (upstream <c>reports</c>).</summary>
    internal static string[] A6Reports(IEnumerable<HostError> errors) =>
        [.. errors.Select(error => error.Kind == "activation_failed" ? $"activation_failed: {error.Failure?.Error.Message}" : error.Kind)];

    /// <summary>The object labelled <paramref name="label"/> on <paramref name="host"/> now (not ended), or null.</summary>
    internal static ChordObject? A6Object(ChordHost host, string label) =>
        host.Dispatcher.Invoke(() => host.Objects.Values.FirstOrDefault(owner => owner.Label == label && owner.State.Kind != "ended"));

    /// <summary>The attempt of the object labelled <paramref name="label"/> now.</summary>
    internal static Attempt? A6Attempt(ChordHost host, string label) => host.Dispatcher.Invoke(() => A6Object(host, label)?.Current);

    private static readonly System.Reflection.FieldInfo A6DecisionField =
        typeof(Attempt).GetField("decision", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

    /// <summary>A weak reference to the decision <paramref name="label"/>'s current attempt declared (upstream's <c>decisions</c>, which
    /// counts every Decision declared), or null when it declared none yet. Made in a frame of its own.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal static WeakReference? A6Decision(ChordHost host, string label) => host.Dispatcher.Invoke(() =>
        A6Object(host, label)?.Current is { } attempt && A6DecisionField.GetValue(attempt) is { } decision ? new WeakReference(decision) : null);

    /// <summary>A server serving Access and Doc (one facet each) and a client linked to it as <paramref name="principal"/> (an owner by
    /// default), with setting the owner's mode (upstream <c>overLink</c>).</summary>
    internal sealed class A6Linked : IAsyncDisposable
    {
        internal required ChordHost Server { get; init; }
        internal required ChordHost Client { get; init; }
        internal required IReference Access { get; init; }
        internal Task Set(string mode, string who = "owner") =>
            Access.CallAsync("set", new JsonObject { ["who"] = who, ["mode"] = mode }, ChordContext.Background);
        public async ValueTask DisposeAsync()
        {
            await Client.CloseAsync(ChordContext.Background);
            await Server.CloseAsync(ChordContext.Background);
        }
    }

    internal static async Task<A6Linked> A6OverLink(A6DocCode? code = null, int? callWindow = null, List<HostError>? errors = null,
        Func<JsonObject, IEnumerable<JsonNode?>>? revokedBy = null)
    {
        var server = Hosts.Create(new()
        {
            Address = Server.Address(), Principal = new() { ["kind"] = "server" }, CallWindow = callWindow ?? 10_000, RevokedBy = revokedBy,
            CloseTimeout = TimeSpan.FromSeconds(2), OnError = error => { if (errors is not null) lock (errors) errors.Add(error); },
        });
        var loaded = await server.LoadAsync([new("@test/access", A6AccessFacet()), new("@test/doc", A6DocFacet(code ?? new()))], ChordContext.Background);
        Equal(0, loaded.Failed.Count, "the facets loaded");
        var client = Hosts.Create(new() { CloseTimeout = TimeSpan.FromSeconds(2), OnError = _ => { } });
        await client.LoadAsync([], ChordContext.Background);
        var pair = PiSharp.Chord.Link.MemoryLinkPair.Create();
        server.Accept(pair.First, new() { Principal = new() { ["kind"] = "owner" } }, ChordContext.Background);
        client.Connect(pair.Second, new() { Principal = new() { ["kind"] = "server" } }, ChordContext.Background);
        return new() { Server = server, Client = client, Access = await server.GetAsync(A6Access, new(), ChordContext.Background) };
    }

    internal static Task<object?> A6Write(IReference doc, string text) => doc.CallAsync("write", JsonValue.Create(text), ChordContext.Background);
}
