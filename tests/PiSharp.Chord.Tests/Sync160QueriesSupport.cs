using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Documents;
using PiSharp.Chord.Host;
using PiSharp.Chord.State;
using PiSharp.Chord.Store;

// PiSharp-only (no upstream Pi source): the helpers of the chord-queries package's 1.6.0 cases (Host/Sync160NamedQueryCases.cs,
// Sync160QueryReaderCases.cs, Sync160ReadDeclarationsCases.cs, Sync160ListCases.cs, Sync160TilingCases.cs). They stand in for what the
// upstream tests write inline with typed proxies (`board.notes.value.rows`, `club.teams.ofTeam.page(...)`, `await notes.put(row, ctx)`)
// and take from test/support.ts.
//
// Turns. An upstream test runs on the host's one thread: `await flushed()` and `await delivered()` let what is queued run. Here a host
// works on its dispatcher and a test on a pool thread: Q7Settled gives the store's reads, the dispatcher and the watches' deliveries time
// (for assertions that something did not happen), and Q7Until waits for a state that is expected (a read that follows a binding or a
// provider's successor is awaited by nobody).
internal static partial class Program
{
    /// <summary>A method that runs one transaction of its object with its arguments.</summary>
    internal static HostMethod Q7Tx(ISelf self, Action<ITx, JsonNode?> write) => async (args, ctx) =>
    {
        await self.TxAsync(tx =>
        {
            write(tx, (JsonNode?)args);
            return Task.FromResult(true);
        }, ctx.Context);
        return null;
    };

    /// <summary>The builders of <c>self.Queries</c>, in order.</summary>
    internal static IReadOnlyDictionary<string, QueryBuilder> Q7Builders(params (string Name, QueryBuilder Builder)[] builders)
    {
        var map = new OrderedDictionary<string, QueryBuilder>(StringComparer.Ordinal);
        foreach (var (name, builder) in builders) map[name] = builder;
        return map;
    }

    /// <summary>A builder of a query the contract lists.</summary>
    internal static QueryBuilder Q7Builder(Func<JsonNode?, Query> build) => new(build);

    /// <summary>Named query <paramref name="name"/> of <paramref name="reference"/>'s queries member <paramref name="member"/> (upstream
    /// <c>reference.member.name</c>).</summary>
    internal static INamedQuery Q7Named(IReference reference, string member, string name) => reference.QueriesMember(member)[name];

    /// <summary>The value of state member <paramref name="member"/>, as a copy a test may keep.</summary>
    internal static JsonNode? Q7Value(IReference reference, string member) => reference.Member(member).Value?.DeepClone();

    /// <summary>The rows of a live query's or a list's value.</summary>
    internal static JsonArray Q7Rows(IReference reference, string member) => (JsonArray)reference.Member(member).Value!["rows"]!.DeepClone();

    /// <summary>Field <paramref name="field"/> of every row of a <c>{ rows, ... }</c> value, as JSON texts joined by a space.</summary>
    internal static string Q7Field(JsonNode? value, string field) => string.Join(" ", ((JsonArray)value!["rows"]!).Select(row => row![field]!.ToJsonString()));

    /// <summary>The ids of the rows of <paramref name="reference"/>'s member, joined by a space.</summary>
    internal static string Q7Ids(IReference reference, string member, string field = "id") => Q7Field(reference.Member(member).Value, field);

    /// <summary>Calls a method with JSON text as its arguments.</summary>
    internal static Task Q7Call(IReference reference, string method, string? args = null, ChordContext? context = null) =>
        reference.CallAsync(method, args is null ? null : JsonNode.Parse(args), context ?? ChordContext.Background);

    /// <summary>Waits (ten seconds at most) until <paramref name="check"/> holds.</summary>
    internal static async Task Q7Until(Func<bool> check, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            bool held;
            try { held = check(); }
            catch { held = false; }
            if (held) return;
            if (DateTime.UtcNow >= deadline)
            {
                // Run it once more outside the catch: a throwing check tells why.
                Check(check(), what + " did not settle");
                return;
            }
            await Task.Delay(5);
        }
    }

    /// <summary>Gives the store's reads, the host's dispatcher and the watches' deliveries time: upstream's <c>flushed()</c> and
    /// <c>delivered()</c> before an assertion that something did not happen, or happened once.</summary>
    internal static async Task Q7Settled(params ChordHost[] hosts)
    {
        for (var round = 0; round < 4; round++)
        {
            await Task.Delay(15);
            foreach (var host in hosts) await host.Dispatcher.RunAsync(() => Task.CompletedTask);
        }
    }

    /// <summary>The kinds of <paramref name="calls"/>: "value", or "failed: message".</summary>
    internal static List<string> Q7Kinds(IEnumerable<WatchCall<JsonNode?>> calls)
    {
        lock (calls) return [.. calls.Select(call => call is WatchFailedCall<JsonNode?> failed ? "failed: " + ((failed.Error as Exception)?.Message ?? failed.Error?.ToString()) : "value")];
    }

    /// <summary>What a failing activation reported, one text per error: the failure's message, or the error's kind.</summary>
    internal static List<string> Q7Failures(List<HostError> errors)
    {
        lock (errors) return [.. errors.Select(error => error.Failure is { } failure ? failure.Error.Message : error.Kind)];
    }

    /// <summary>A host on <paramref name="store"/> collecting its errors, with the short timeouts of a test.</summary>
    internal static ChordHost Q7Host(ChordStore? store, List<HostError>? errors = null, PiSharp.Telemetry.ITelemetryContext? telemetry = null,
        Address? address = null, JsonObject? principal = null) => Hosts.Create(new()
    {
        Store = store, Telemetry = telemetry, Address = address, Principal = principal,
        OnError = error => { if (errors is not null) lock (errors) errors.Add(error); },
        ReadyTimeout = TimeSpan.FromSeconds(10), CloseTimeout = TimeSpan.FromSeconds(2),
    });

    /// <summary>A plugin of one facet.</summary>
    internal static PluginFacet Q7Plugin(string name, Action<IFacetHost> serve) => new(name, Facet.Define((h, _) => serve(h)));

    /// <summary>The attempt serving <paramref name="type"/> now.</summary>
    internal static Attempt Q7Attempt(ChordHost host, Contract type) =>
        host.Dispatcher.Invoke(() => host.Objects.Values.Single(owner => owner.Served.Contract.Name == type.Name && owner.State.Kind != "ended").Current!);

    /// <summary>Weak references to the sources of the readers <paramref name="type"/>'s attempt declared, made in a frame of its own.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static WeakReference[] Q7ReaderSources(ChordHost host, Contract type) =>
        host.Dispatcher.Invoke(() => host.Objects.Values.Single(owner => owner.Served.Contract.Name == type.Name).Current!.Self.Readers
            .Select(reader => new WeakReference(reader.Source)).ToArray());

    /// <summary>The number of reads the store keeps over <paramref name="table"/>.</summary>
    internal static int Q7Reads(ChordStore store, string table) => store.ReadIndex.On(table)?.Count ?? 0;

    /// <summary>A table that is none of a factory's (upstream passes <c>{} as never</c>).</summary>
    internal sealed class Q7ForeignTable : ITable
    {
        public Task<Page> PageAsync(Query query, long limit, ChordContext context, PageOptions? options = null) => throw new NotSupportedException();

        public IAsyncEnumerable<IReadOnlyDictionary<string, JsonNode?>> Rows(Query query, ChordContext context) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, JsonNode?>?> GetAsync(object? key, ChordContext context) => throw new NotSupportedException();

        public Task<long> CountAsync(Query query, ChordContext context) => throw new NotSupportedException();
    }

    /// <summary>A named query that is none of Chord's (upstream passes a table or <c>"no query" as never</c>).</summary>
    internal sealed class Q7ForeignQuery : INamedQuery
    {
        public Task<QueryPage> PageAsync(JsonNode? args, long limit, ChordContext context, PageOptions? options = null) => throw new NotSupportedException();
    }

    /// <summary>Runs <paramref name="work"/> and records the message of what it throws (upstream's <c>attempt(() =&gt; ...)</c> in a factory).</summary>
    internal static void Q7Attempting(List<string> thrown, Action work)
    {
        try { work(); }
        catch (Exception error) { lock (thrown) thrown.Add(error.Message); }
    }
}
