// PiSharp-only (no upstream Pi source): the helpers of the Pi 1.6.0 edge cases in this suite (Sync160EdgeCases.cs). The desk fixture
// stands in for upstream's `desk()` (packages/chord/test/support.ts at faac585884ae2c013a6b1400f3e9e77f10b34bac), which admits the
// variant a local mode names, so a held reference rebinds to another variant, or is denied, when the mode is set.
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Host;
using PiSharp.Chord.Link;
using static PiSharp.Chord.Contracts.Schemas;

internal static class Sync160EdgeSupport
{
    internal static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    internal static async Task Until(Func<bool> value, string what, int seconds = 5)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!value())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("timed out waiting until " + what);
            await Task.Delay(5);
        }
    }

    internal static readonly HostKind Server = new("server");

    /// <summary>A desk its caller reads or edits by its admitted mode: a variant each (upstream <c>Desk</c>).</summary>
    internal static readonly Contract Desk = Contract.Remote("test.160.desk", new()
    {
        Version = 1, Params = new(), HostAddress = _ => Server.Address(), Admission = OneOf("reader", "editor"),
        Variants = new()
        {
            ["reader"] = new() { State = { ["text"] = Str() } },
            ["editor"] = new() { State = { ["text"] = Str() }, Methods = { ["write"] = Method(Str()) } },
        },
    });

    /// <summary>The mode <see cref="Desk"/>'s admission reads (upstream <c>DeskMode</c>).</summary>
    internal static readonly Contract DeskMode = Contract.Local("test.160.desk.mode", new()
    { Version = 1, Params = new(), Members = new() { ["mode"] = Str(), ["set"] = Method(Str()) } });

    /// <summary>A server serving <see cref="Desk"/> and a client linked to it, admitted as the desk's mode says: <c>reader</c> or
    /// <c>editor</c> binds that variant, anything else is denied (upstream <c>desk()</c>).</summary>
    internal sealed class DeskWorld : IAsyncDisposable
    {
        public required ChordHost Server { get; init; }
        public required ChordHost Client { get; init; }
        public required IReference Mode { get; init; }

        /// <summary>Sets the desk's mode: <c>reader</c> or <c>editor</c> admits that variant, anything else denies.</summary>
        public Task Admit(string next) => Mode.CallAsync("set", JsonValue.Create(next), ChordContext.Background);

        public async ValueTask DisposeAsync()
        {
            Mode.Release();
            await Client.CloseAsync(ChordContext.Background);
            await Server.CloseAsync(ChordContext.Background);
        }
    }

    internal static async Task<DeskWorld> DeskAsync()
    {
        var server = Hosts.Create(new() { Address = Server.Address(), Principal = new() { ["kind"] = "server" }, OnError = _ => { }, CloseTimeout = TimeSpan.FromSeconds(2) });
        var loaded = await server.LoadAsync([new("test.desk", Facet.Define((host, _) =>
        {
            host.Serve(DeskMode, (_, _, self) =>
            {
                var mode = self.Writable<JsonNode?>(JsonValue.Create("reader"));
                return new(new Dictionary<string, object?>
                {
                    ["mode"] = self.Derive([mode], values => ((JsonNode?)values[0])?.DeepClone()),
                    ["set"] = (HostMethod)((arguments, _) => { mode.Set(((JsonNode?)arguments)?.DeepClone()); return Task.FromResult<object?>(null); }),
                });
            });
            host.Serve(Desk, (_, _, admitted, self) =>
            {
                var text = self.Writable<JsonNode?>(JsonValue.Create("a desk"));
                var members = new Dictionary<string, object?> { ["text"] = self.Derive([text], values => ((JsonNode?)values[0])?.DeepClone()) };
                if (admitted?.GetValue<string>() == "reader") return new(members, "reader");
                members["write"] = (HostMethod)((arguments, _) => { text.Set(((JsonNode?)arguments)?.DeepClone()); return Task.FromResult<object?>(null); });
                return new(members, "editor");
            }, new ServeOptions(Admit: (_, _, self) => self.Derive([self.Get(DeskMode, new()).Member("mode")], values =>
                ((JsonNode?)values[0])?.GetValue<string>() is { } mode && mode is "reader" or "editor"
                    ? Admission.Allow(JsonValue.Create(mode)) : Admission.Deny("no access"))));
        }))], ChordContext.Background);
        Check(loaded.Failed.Count == 0, "desk facet failed to load: " + string.Join(" | ", loaded.Failed.Select(failure => failure.Error.Message)));
        var client = Hosts.Create(new() { OnError = _ => { }, CloseTimeout = TimeSpan.FromSeconds(2) });
        await client.LoadAsync([], ChordContext.Background);
        var modeReference = await server.GetAsync(DeskMode, new(), ChordContext.Background);
        var pair = MemoryLinkPair.Create();
        server.Accept(pair.First, new() { Principal = new() { ["kind"] = "guest" } }, ChordContext.Background);
        client.Connect(pair.Second, new() { Principal = new() { ["kind"] = "server" } }, ChordContext.Background);
        return new() { Server = server, Client = client, Mode = modeReference };
    }

    /// <summary>Whether <paramref name="reference"/> is collected once nothing else holds it (upstream <c>expectCollected</c>).</summary>
    internal static async Task<bool> CollectedAsync(WeakReference reference)
    {
        for (var round = 0; round < 40 && reference.IsAlive; round++)
        {
            await Task.Delay(25);
            Collect();
        }
        return !reference.IsAlive;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
}
