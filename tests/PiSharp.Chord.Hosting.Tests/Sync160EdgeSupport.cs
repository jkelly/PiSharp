// PiSharp-only (no upstream Pi source): the helpers of the Pi 1.6.0 edge cases in this suite (Sync160EdgeCases.cs). The desk fixture
// stands in for upstream's `desk()` (packages/chord/test/support.ts at faac585884ae2c013a6b1400f3e9e77f10b34bac), which rebinds a held
// reference to another variant through an admission; admission is the chord-admission package's, so here the variant follows the
// principal of the client's link and a rebind is a reconnect as another principal.
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

    /// <summary>A remote type with an editor variant for owners and a reader variant for everyone else.</summary>
    internal static readonly Contract Desk = Contract.Remote("test.160.desk", new()
    {
        Version = 1, Params = new(), HostAddress = _ => Server.Address(),
        Variants = new()
        {
            ["editor"] = new() { State = { ["text"] = Str() }, Methods = { ["write"] = Method(Str()) } },
            ["reader"] = new() { State = { ["text"] = Str() } },
        },
    });

    /// <summary>A server serving <see cref="Desk"/> and a loaded client that is not linked yet.</summary>
    internal sealed class DeskWorld : IAsyncDisposable
    {
        private ILink? link;
        public required ChordHost Server { get; init; }
        public required ChordHost Client { get; init; }

        /// <summary>Links the client to the server as <paramref name="kind"/> (<c>owner</c> binds the editor variant), dropping the
        /// link it had: a held reference loads again and binds the variant the new principal gets.</summary>
        public void Link(string kind)
        {
            link?.Close();
            var pair = MemoryLinkPair.Create(); link = pair.Second;
            Server.Accept(pair.First, new() { Principal = new() { ["kind"] = kind } }, ChordContext.Background);
            Client.Connect(pair.Second, new() { Principal = new() { ["kind"] = "server" } }, ChordContext.Background);
        }
        public void Unlink() { link?.Close(); link = null; }

        public async ValueTask DisposeAsync()
        {
            await Client.CloseAsync(ChordContext.Background);
            await Server.CloseAsync(ChordContext.Background);
        }
    }

    internal static async Task<DeskWorld> DeskAsync()
    {
        var server = Hosts.Create(new() { Address = Server.Address(), Principal = new() { ["kind"] = "server" }, OnError = _ => { }, CloseTimeout = TimeSpan.FromSeconds(2) });
        var loaded = await server.LoadAsync([new("test.desk", Facet.Define((host, _) => host.Serve(Desk, (_, principal, self) =>
        {
            var text = self.Writable<JsonNode?>(JsonValue.Create("first"));
            var members = new Dictionary<string, object?> { ["text"] = self.Derive([text], values => ((JsonNode?)values[0])?.DeepClone()) };
            if (principal?["kind"]?.GetValue<string>() != "owner") return new(members, "reader");
            members["write"] = (HostMethod)((arguments, _) => { text.Set(((JsonNode?)arguments)?.DeepClone()); return Task.FromResult<object?>(null); });
            return new(members, "editor");
        })))], ChordContext.Background);
        Check(loaded.Failed.Count == 0, "desk facet failed to load: " + string.Join(" | ", loaded.Failed.Select(failure => failure.Error.Message)));
        var client = Hosts.Create(new() { OnError = _ => { }, CloseTimeout = TimeSpan.FromSeconds(2) });
        await client.LoadAsync([], ChordContext.Background);
        return new() { Server = server, Client = client };
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
