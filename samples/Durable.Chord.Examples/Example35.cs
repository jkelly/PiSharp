// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/examples/35-durable-on-the-store.ts
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Host;
using PiSharp.Chord.Store;
using PiSharp.Durable.Store;
using static PiSharp.Chord.Contracts.Schemas;
using ChordJson = System.Text.Json.Nodes.JsonValue;

namespace PiSharp.Durable.Chord.Examples;

public sealed record StoreExampleResult(long Sequences, long NextId, long LastSeq, IReadOnlyDictionary<string, int> Parts,
    IReadOnlyList<string> ReopenedEntries, string ReopenedNote);

public static partial class ChordExamples
{
    public static readonly Contract StoreChat = Contract.Local("example.chat", new()
    {
        Version = 1, Params = new(), Members = new() { ["note"] = Method(Str()), ["say"] = Method(Str()) },
    });
    private static readonly TableDefinition Notes = Tables.DefineTable("notes", new()
    {
        Version = 1, Columns = new() { ["id"] = Str(), ["text"] = Str() }, Key = ["id"],
    });

    public static Facet StoreFacet(ChordStore store) => Facet.Define((host, _) => host.Serve(StoreChat, (_, _, self) =>
    {
        var notes = ((IStoreSelf)self).Table(Notes);
        var harness = OpenHarness(self, store, StoreChat.Name, Registry.Create());
        return new(new Dictionary<string, object?>
        {
            ["note"] = (HostMethod)(async (args, ctx) =>
            {
                var text = ((JsonNode)args!).GetValue<string>();
                await self.TxAsync(tx =>
                {
                    tx.Put(notes, new JsonObject { ["id"] = $"n{text.Length}", ["text"] = text });
                    return Task.FromResult(true);
                }, ctx.Context);
                return null;
            }),
            ["say"] = (HostMethod)(async (args, ctx) =>
            {
                await WriteAsync(harness.Value, "example.said", ViewBridge.Context(ctx.Context), ((JsonNode)args!).GetValue<string>());
                return null;
            }),
        });
    }));

    public static async Task<StoreExampleResult> StoreAsync(TextWriter output)
    {
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-chord-35-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "session.sqlite");
        try
        {
            long sequences, nextId, lastSeq = -1;
            var parts = new Dictionary<string, int>();
            await using (var store = await OpenStoreAsync(path))
            {
                await using (var host = Hosts.Create(new() { Store = store, OnError = e => output.WriteLine($"onError: {e.Kind}") }))
                {
                    await host.LoadAsync([new("@example/core", StoreFacet(store))], Context);
                    using var chat = await host.GetAsync(StoreChat, new(), Context);
                    var before = store.LastSeq;
                    await chat.CallAsync("note", ChordJson.Create("hello"), Context);
                    await chat.CallAsync("say", ChordJson.Create("hello, Durable"), Context);
                    sequences = store.LastSeq - before;
                    nextId = store.NextId;
                    await foreach (var part in store.Export(Context))
                    {
                        var kind = part["kind"]!.GetValue<string>();
                        if (kind == "manifest") lastSeq = part["lastSeq"]!.GetValue<long>();
                        var owner = kind is "table" or "put" ? part["table"]!.GetValue<string>().Split(':')[0]
                            : kind == "document.create" ? part["record"]?["owner"]?.GetValue<string>() : null;
                        if (owner is not null) parts[$"{owner} {kind}"] = parts.GetValueOrDefault($"{owner} {kind}") + 1;
                    }
                }
                // The host releases its attachment and all owner claims; it does not close the app's store.
                await using var secondHost = Hosts.Create(new() { Store = store, OnError = _ => { } });
                await secondHost.LoadAsync([], Context);
            }
            await using var reopened = await OpenStoreAsync(path);
            var storage = await StoreStorage.OpenAsync(reopened, StoreChat.Name, DurableContext);
            IReadOnlyList<string> entries;
            try { entries = (await storage.ScanEntriesAsync(new(ConversationId.Root), 10, null, DurableContext)).Items.Select(e => e.Kind).ToArray(); }
            finally { await storage.CloseAsync(DurableContext); }
            var row = await reopened.ReadAsync(db => db.GetAsync("SELECT text FROM \"example.chat:notes\" WHERE id = ?", "n5"), Context);
            var note = (string)row!["text"]!;
            await output.WriteLineAsync($"35 seqs taken: {sequences}");
            await output.WriteLineAsync($"35 manifest: {{lastSeq:{lastSeq},nextId:{nextId}}}");
            await output.WriteLineAsync($"35 parts: {System.Text.Json.JsonSerializer.Serialize(parts)}");
            await output.WriteLineAsync($"35 Durable entries after reopen: {string.Join(", ", entries)}; note: {note}");
            return new(sequences, nextId, lastSeq, parts, entries, note);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
