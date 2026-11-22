// PiSharp-only (no upstream Pi source): the helpers of the Pi 1.6.0 chord-edge cases of this suite (Surfaces/Sync160*Cases.cs,
// Sync/Sync160*Cases.cs). They stand in for the parts of packages/chord/test/surfaces/client.test.ts `setup` and
// packages/chord/test/support.ts `desk()` at faac585884ae2c013a6b1400f3e9e77f10b34bac that these cases use. `desk()` admits the
// variant a local mode names, so a held reference rebinds to another variant, or is denied, when the mode is set.
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Documents;
using PiSharp.Chord.Host;
using PiSharp.Chord.Link;
using PiSharp.Chord.Surfaces;
using static PiSharp.Chord.Contracts.Schemas;

internal static partial class Program
{
    /// <summary>Waits until <paramref name="check"/> holds (upstream's <c>until</c> and <c>expect.poll</c>).</summary>
    private static async Task Sync160Until(Func<bool> check, string what, int seconds = 5)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!check())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("timed out waiting until " + what);
            await Task.Delay(5);
        }
    }

    // ---- client.test.ts ----

    private static readonly Contract Sync160Greeting = Contract.Local("test.160.greeting", new() { Version = 1, Params = new(), Members = new() { ["text"] = Str() } });

    /// <summary>A registered script of plugin <paramref name="plugin"/> serving the greeting with <paramref name="text"/>.</summary>
    private static RegisteredScript Sync160Script(string plugin, string digest, string text) => new(plugin, digest, (_, module) =>
        module.Exports = Facet.Define((host, _) => host.Serve(Sync160Greeting, (_, _, _) =>
            new(new Dictionary<string, object?> { ["text"] = PiSharp.Chord.State.Watchables.Constant(JsonValue.Create(text)) }))));

    private static ScriptSet Sync160SetOf(string hash, IReadOnlyList<RegisteredScript> scripts) => new(hash, "/combined.js",
        [.. scripts.Select(script => new ScriptEntry(script.Plugin, script.Digest, $"/{script.Plugin}.{script.Digest}.js", $"/{script.Plugin}.{script.Digest}.css"))]);

    private sealed record Sync160ClientOptions
    {
        public bool Follow { get; init; } = true;
        /// <summary>The server never answers: the client's sockets never open.</summary>
        public bool Offline { get; init; }
        /// <summary>The set the server shows from the start (default: the initial scripts').</summary>
        public IReadOnlyList<RegisteredScript>? Shown { get; init; }
        /// <summary>Holds the server's base answers until it settles (a slow network).</summary>
        public Task? BaseAnswers { get; init; }
        public PartLimits? Limits { get; init; }
    }

    /// <summary>A server serving the web scripts object, and a client started against it (upstream <c>setup</c>).</summary>
    private sealed class Sync160ClientWorld : IAsyncDisposable
    {
        private readonly object gate = new();
        private readonly List<string> fetched = [], outdated = [], asked = [];
        private readonly List<ClientError> errors = [];
        private readonly List<ConnectionState> states = [];
        private readonly List<(ISocketEvents Events, ILink Link)> sockets = [];
        // The scripts object's state as its current activation holds it: the client's hold's, and the server's own (PublishAsync's).
        private ISingletonHandle? held, own;
        public ChordHost Server { get; private set; } = null!;
        public Client Client { get; private set; } = null!;
        public string ServerBase = "B1";
        /// <summary>Whether the client's opens reach the server (set false to take the server away, true to bring it back).</summary>
        public volatile bool Offline;
        public IReadOnlyList<string> Fetched { get { lock (gate) return [.. fetched]; } }
        public IReadOnlyList<string> Outdated { get { lock (gate) return [.. outdated]; } }
        public IReadOnlyList<string> Asked { get { lock (gate) return [.. asked]; } }
        public IReadOnlyList<ClientError> Errors { get { lock (gate) return [.. errors]; } }
        public IReadOnlyList<(ISocketEvents Events, ILink Link)> Sockets { get { lock (gate) return [.. sockets]; } }

        public static async Task<Sync160ClientWorld> StartAsync(IReadOnlyList<RegisteredScript> initial, IReadOnlyList<RegisteredScript>? available = null,
            Sync160ClientOptions? more = null)
        {
            more ??= new(); available ??= [];
            var world = new Sync160ClientWorld { Offline = more.Offline };
            world.Server = Hosts.Create(new() { Address = Scripts.Server.Address(), Principal = new() { ["kind"] = "server" }, OnError = _ => { } });
            var shown = Sync160SetOf("B1", more.Shown ?? initial);
            var definition = StateDefinition.Define("test.160.scripts", new() { Version = 1, Schema = Scripts.Schema, Initial = shown.ToJson });
            var loaded = await world.Server.LoadAsync([new("chord.web", Facet.Define((facet, _) => facet.Serve(Scripts.WebScripts, (_, principal, self) =>
            {
                var state = self.State(definition);
                if (principal?["kind"]?.GetValue<string>() == "user") world.held = state; else world.own = state;
                return new(new Dictionary<string, object?> { ["set"] = state }, "main");
            })))], ChordContext.Background);
            Equal(0, loaded.Failed.Count, "the server's scripts facet loaded");
            world.Client = await Client.StartAsync(new()
            {
                Scripts = Scripts.WebScripts, Base = "B1", Initial = initial, Follow = more.Follow, Limits = more.Limits,
                Environment = new LoopEnvironment(),
                Open = events =>
                {
                    var pair = MemoryLinkPair.Create();
                    lock (world.gate) world.sockets.Add((events, pair.Second));
                    if (world.Offline) return pair.Second;
                    world.Server.Accept(pair.First, new() { Principal = new() { ["kind"] = "user", ["id"] = "u" } }, ChordContext.Background);
                    events.Opened();
                    return pair.Second;
                },
                FetchScript = (entry, _) =>
                {
                    lock (world.gate) world.fetched.Add(entry.Url);
                    var found = available.FirstOrDefault(script => script.Plugin == entry.Name && script.Digest == entry.Digest);
                    return found is null ? Task.FromException<RegisteredScript>(new IOException("no " + entry.Url)) : Task.FromResult(found);
                },
                ServerBase = async _ =>
                {
                    lock (world.gate) world.asked.Add(world.ServerBase);
                    if (more.BaseAnswers is { } answers) await answers;
                    return world.ServerBase;
                },
                // With the connection state then: the loop has stopped (Waiting) before the app starts over.
                Outdated = () => { lock (world.gate) world.outdated.Add($"outdated ({(world.states.Count == 0 ? "none" : world.states[^1].ToString())})"); },
                OnConnection = state => { lock (world.gate) world.states.Add(state); },
                OnError = error => { lock (world.gate) world.errors.Add(error); },
            });
            return world;
        }

        /// <summary>Publishes <paramref name="set"/> on the server's scripts object (upstream <c>publishTo</c>).</summary>
        public async Task PublishAsync(ScriptSet set)
        {
            // The scripts object is active while it is held: by the client's hold, or (a static client holds nothing) by one made here.
            using var reference = held is null ? await Server.GetAsync(Scripts.WebScripts, new(), ChordContext.Background) : null;
            await (held ?? own)!.ChangeAsync(draft =>
            { draft["base"] = set.Base; draft["combined"] = set.Combined; draft["plugins"] = set.ToJson()["plugins"]!.DeepClone(); }, ChordContext.Background);
        }

        public async Task<string?> GreetingAsync()
        {
            using var greeting = await Client.Host.GetAsync(Sync160Greeting, new(), ChordContext.Background);
            return greeting.Member("text").Value?.GetValue<string>();
        }

        public async ValueTask DisposeAsync()
        {
            await Client.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Server.CloseAsync(ChordContext.Background);
        }
    }

    // ---- variant.test.ts ----

    /// <summary>A desk its caller reads or edits by its admitted mode: a variant each (upstream <c>Desk</c>).</summary>
    private static readonly Contract Sync160Desk = Contract.Remote("test.160.desk", new()
    {
        Version = 1, Params = new(), HostAddress = _ => ChordSupport.Server.Address(), Admission = OneOf("reader", "editor"),
        Variants = new()
        {
            ["reader"] = new() { State = { ["text"] = Str() } },
            ["editor"] = new() { State = { ["text"] = Str() }, Methods = { ["write"] = Method(Str()) } },
        },
    });

    /// <summary>The mode <see cref="Sync160Desk"/>'s admission reads (upstream <c>DeskMode</c>).</summary>
    private static readonly Contract Sync160DeskMode = Contract.Local("test.160.desk.mode", new()
    { Version = 1, Params = new(), Members = new() { ["mode"] = Str(), ["set"] = Method(Str()) } });

    /// <summary>A server serving <see cref="Sync160Desk"/> and a client linked to it, admitted as the desk's mode says:
    /// <c>reader</c> or <c>editor</c> binds that variant, anything else is denied (upstream <c>desk()</c>).</summary>
    private sealed class Sync160DeskWorld : IAsyncDisposable
    {
        private IReference mode = null!;
        public ChordHost Server { get; private set; } = null!;
        public ChordHost Client { get; private set; } = null!;

        public static async Task<Sync160DeskWorld> StartAsync()
        {
            var world = new Sync160DeskWorld
            {
                Server = Hosts.Create(new() { Address = ChordSupport.Server.Address(), Principal = new() { ["kind"] = "server" }, OnError = _ => { } }),
                Client = Hosts.Create(new() { OnError = _ => { } }),
            };
            var loaded = await world.Server.LoadAsync([new("test.desk", Facet.Define((host, _) =>
            {
                host.Serve(Sync160DeskMode, (_, _, self) =>
                {
                    var mode = self.Writable<JsonNode?>(JsonValue.Create("reader"));
                    return new(new Dictionary<string, object?>
                    {
                        ["mode"] = self.Derive([mode], values => ((JsonNode?)values[0])?.DeepClone()),
                        ["set"] = (HostMethod)((arguments, _) => { mode.Set((arguments as JsonNode)?.DeepClone()); return Task.FromResult<object?>(null); }),
                    });
                });
                host.Serve(Sync160Desk, (_, _, admitted, self) =>
                {
                    var text = self.Writable<JsonNode?>(JsonValue.Create("a desk"));
                    var members = new Dictionary<string, object?> { ["text"] = self.Derive([text], values => ((JsonNode?)values[0])?.DeepClone()) };
                    if (admitted?.GetValue<string>() == "reader") return new(members, "reader");
                    members["write"] = (HostMethod)((arguments, _) => { text.Set((arguments as JsonNode)?.DeepClone()); return Task.FromResult<object?>(null); });
                    return new(members, "editor");
                }, new ServeOptions(Admit: (_, _, self) => self.Derive([self.Get(Sync160DeskMode, new()).Member("mode")], values =>
                    ((JsonNode?)values[0])?.GetValue<string>() is { } mode && mode is "reader" or "editor"
                        ? Admission.Allow(JsonValue.Create(mode)) : Admission.Deny("no access"))));
            }))], ChordContext.Background);
            Equal(0, loaded.Failed.Count, "the desk facet loaded");
            await world.Client.LoadAsync([], ChordContext.Background);
            world.mode = await world.Server.GetAsync(Sync160DeskMode, new(), ChordContext.Background);
            var pair = MemoryLinkPair.Create();
            world.Server.Accept(pair.First, new() { Principal = new() { ["kind"] = "guest" } }, ChordContext.Background);
            world.Client.Connect(pair.Second, new() { Principal = new() { ["kind"] = "server" } }, ChordContext.Background);
            return world;
        }

        /// <summary>Sets the desk's mode: <c>reader</c> or <c>editor</c> admits that variant, anything else denies.</summary>
        public Task Admit(string next) => mode.CallAsync("set", JsonValue.Create(next), ChordContext.Background);

        public async ValueTask DisposeAsync()
        {
            mode.Release();
            await Client.CloseAsync(ChordContext.Background);
            await Server.CloseAsync(ChordContext.Background);
        }
    }
}
