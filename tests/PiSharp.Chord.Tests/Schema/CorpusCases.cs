// Genuine complete schema-switch proof, captured from unchanged upstream v1.5.0. Existing historical files remain untouched.
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Documents;
using PiSharp.Chord.Host;
using PiSharp.Chord.Jobs;
using PiSharp.Chord.Store;

internal static partial class Program
{
    private static string CorpusFile(params string[] parts) => Path.Combine([Path.GetDirectoryName(SchemaFixture("recorded.json"))!, "..", "v1.5.0", "schema-switch", .. parts]);
    private static JsonNode CorpusJson(params string[] parts) => JsonNode.Parse(File.ReadAllText(CorpusFile(parts)))!;
    private static IEnumerable<(string, Func<Task>)> SchemaCorpusCases()
    {
        yield return Case("schema.corpus.manifest-and-source-files", () =>
        {
            var manifest = CorpusJson("manifest.json");
            Equal("f9a26365568127461c47a07a37dcb42c140a3ac5", manifest["upstream"]!["commit"]!.GetValue<string>(), "pinned upstream");
            Equal(2, manifest["reproducibility"]!["runs"]!.GetValue<int>(), "independent runs");
            Equal(1, manifest["normalization"]!.AsArray().Count, "only persisted job UUIDs normalized");
            foreach (var category in new[] { "historical", "first", "second" })
            {
                var inventory = category == "historical" ? manifest["historical"]! : manifest["runs"]![category]!;
                foreach (var entry in inventory.AsArray())
                {
                    var path = entry!["path"]!.GetValue<string>(); var bytes = File.ReadAllBytes(CorpusFile(category, path));
                    Equal(entry["bytes"]!.GetValue<int>(), bytes.Length, path + " length");
                    Equal(entry["sha256"]!.GetValue<string>(), Convert.ToHexStringLower(SHA256.HashData(bytes)), path + " SHA256");
                }
            }
            foreach (var file in new[] { "recorded.json", "stored/store.db", "stored/archive.jsonl" })
                Check(File.ReadAllBytes(SchemaFixture(file)).AsSpan().SequenceEqual(File.ReadAllBytes(CorpusFile("historical", file))), "original historical bytes preserved: " + file);
            var repository = new DirectoryInfo(CorpusFile());
            while (!File.Exists(Path.Combine(repository.FullName, "PiSharp.slnx"))) repository = repository.Parent ?? throw new InvalidOperationException("Repository missing");
            foreach (var (file, key) in new[] { ("capture.mjs", "driverSha256"), ("worker.mjs", "scenarioSha256") })
                Equal(manifest[key]!.GetValue<string>(), Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(repository.FullName, "tools", "ChordSchemaFixtures150", file)))), file + " identity");
        });
        yield return Case("schema.corpus.independent-runs-reproduce", () =>
        {
            var manifest = CorpusJson("manifest.json");
            var maps = new Dictionary<string, Dictionary<string, string>>();
            foreach (var pass in new[] { "first", "second" })
                maps[pass] = CorpusLines(pass, "running.jsonl").Where(part => part?["kind"]?.GetValue<string>() == "document.create" &&
                    part!["record"]?["namespace"]?.GetValue<string>() == "chord.jobs")
                    .Select((part, index) => (Id: part!["record"]!["key"]!.GetValue<string>(), Token: $"<job:{index}>"))
                    .ToDictionary(item => item.Id, item => item.Token, StringComparer.Ordinal);
            foreach (var item in manifest["reproducibility"]!["compared"]!.AsArray())
            {
                var file = item!["path"]!.GetValue<string>();
                JsonNode? Parse(string pass) => file.EndsWith(".jsonl", StringComparison.Ordinal) ? new JsonArray(CorpusLines(pass, file).ToArray()) : CorpusJson(pass, file);
                Json(CorpusNormalize(Parse("first"), maps["first"])!.ToJsonString(), CorpusNormalize(Parse("second"), maps["second"]), file);
            }
        });
        foreach (var pass in new[] { "first", "second" })
        {
            yield return Case($"schema.corpus.{pass}.complete-fingerprints", () =>
            {
                Json(CorpusJson(pass, "recorded.json")["fingerprints"]!.ToJsonString(), SchemaProofs.Fingerprints(), "every contract/document/table/job normal form");
            });
            yield return Case($"schema.corpus.{pass}.sqlite-stopped-export", async () =>
            {
                var temp = Directory.CreateTempSubdirectory("chord-corpus-export-");
                try
                {
                    var path = Path.Combine(temp.FullName, "store.db"); File.Copy(CorpusFile(pass, "store.db"), path);
                    await using var store = await StoreOver(new TestSqliteDatabase(path));
                    var actual = new JsonArray((await Exported(store)).Select(x => (JsonNode?)x).ToArray());
                    Json(new JsonArray(CorpusLines(pass, "stopped.jsonl").ToArray()).ToJsonString(), actual, "exact upstream stopped-store export");
                }
                finally { temp.Delete(true); }
            });
            foreach (var backend in new[] { "store", "archive" })
                yield return Case($"schema.corpus.{pass}.{backend}.native-migrate-and-resume", async () =>
                {
                    var expected = CorpusJson(pass, "fresh-read.json")[backend]!;
                    var actual = await CorpusRead(pass, backend, historical: false);
                    Json(expected.ToJsonString(), actual, "full native schema-switch read after migration and persisted-job resumption");
                });
        }
        foreach (var backend in new[] { "store", "archive" })
            yield return Case($"schema.corpus.historical.{backend}.native-migrate-and-resume", async () =>
            {
                var actual = await CorpusRead("historical", backend, historical: true);
                Json(CorpusJson("first", "recorded.json")["stored"]![backend]!.ToJsonString(), actual, "original TypeBox-written stored data read through native host");
            });
    }

    private static IEnumerable<JsonNode?> CorpusLines(params string[] parts) => File.ReadLines(CorpusFile(parts)).Where(x => x.Length > 0).Select(x => JsonNode.Parse(x));
    private static JsonNode? CorpusNormalize(JsonNode? value, IReadOnlyDictionary<string, string> ids) => value switch
    {
        JsonObject obj => new JsonObject(obj.Select(property => new KeyValuePair<string, JsonNode?>(property.Key, CorpusNormalize(property.Value, ids)))),
        JsonArray array => new JsonArray(array.Select(item => CorpusNormalize(item, ids)).ToArray()),
        System.Text.Json.Nodes.JsonValue scalar when scalar.TryGetValue<string>(out var text) && ids.TryGetValue(text, out var id) => System.Text.Json.Nodes.JsonValue.Create(id),
        _ => value?.DeepClone(),
    };
    private static async Task<JsonNode> CorpusRead(string pass, string backend, bool historical)
    {
        var temp = Directory.CreateTempSubdirectory("chord-corpus-read-");
        var context = ChordContext.Background;
        try
        {
            var path = Path.Combine(temp.FullName, "read.db");
            if (backend == "store") File.Copy(historical ? CorpusFile(pass, "stored", "store.db") : CorpusFile(pass, "store.db"), path);
            else
            {
                var source = historical ? CorpusFile(pass, "stored", "archive.jsonl") : CorpusFile(pass, "running.jsonl");
                await using var imported = (ChordStore)await Tables.ImportStoreAsync(Tables.SqliteBackend(new TestSqliteDatabase(path)),
                    Tables.DecodeJsonl(SchemaFileChunks(source)), context);
            }
            await using var store = await StoreOver(new TestSqliteDatabase(path));
            var finished = new List<JsonNode?>(); var errors = new List<HostError>();
            await using var host = Hosts.Create(new() { Store = store, OnError = e => { lock (errors) errors.Add(e); }, CloseTimeout = TimeSpan.FromMilliseconds(200) });
            await host.LoadAsync([new("@fixture/owner", CorpusOwner(finished))], context);
            using var owner = await host.GetAsync(SchemaDeclarations.Owner, new(), context);
            for (var attempt = 0; ; attempt++)
            {
                lock (finished) if (finished.Count == 3) break;
                if (attempt >= 500) throw new InvalidOperationException("Persisted jobs did not finish: " + string.Join(",", errors.Select(e => e.Kind)));
                await Task.Delay(10);
            }
            var dump = (JsonNode)(await owner.CallAsync("dump", new JsonObject(), context))!;
            var note = (JsonNode)(await owner.CallAsync("read", System.Text.Json.Nodes.JsonValue.Create("b"), context))!;
            lock (finished) return new JsonObject { ["dump"] = dump.DeepClone(), ["finished"] = new JsonArray(finished.Select(x => x?.DeepClone()).ToArray()), ["note"] = note.DeepClone() };
        }
        finally { temp.Delete(true); }
    }

    private static Facet CorpusOwner(List<JsonNode?> finished) => Facet.Define((host, _) => host.Serve(SchemaDeclarations.Owner, (_, _, self) =>
    {
        var profile = self.State(SchemaDeclarations.Profile); var notes = self.KeyedState(SchemaDeclarations.Notes);
        var rows = ((IStoreSelf)self).Table(SchemaDeclarations.Rows); var edits = ((IStoreSelf)self).Table(SchemaDeclarations.EditsV3);
        var imports = self.Job(SchemaDeclarations.ImportJob, new JobImplementation
        {
            Run = async (args, ctx) =>
            {
                await ctx.State!.ChangeAsync(draft => draft["done"] = draft["done"]!.GetValue<int>() + args!["path"]!.GetValue<string>().Length, ctx.Context);
                return new JsonObject { ["count"] = ctx.State.Value!["done"]!.GetValue<int>() };
            },
            Finish = (outcome, _) =>
            {
                lock (finished) finished.Add(outcome.Kind == "done" ? ((JsonNode)outcome.Result!).DeepClone() : System.Text.Json.Nodes.JsonValue.Create(outcome.Kind));
                return Task.CompletedTask;
            },
        });
        return new(new Dictionary<string, object?>
        {
            ["profile"] = profile,
            ["write"] = (HostMethod)((_, _) => throw new InvalidOperationException("The compatibility reader does not create source fixtures")),
            ["read"] = (HostMethod)(async (args, ctx) => System.Text.Json.Nodes.JsonValue.Create((await notes.GetAsync(((JsonNode)args!).GetValue<string>(), ctx.Context))?["body"]?.GetValue<string>() ?? "")),
            ["dump"] = (HostMethod)(async (_, ctx) => new JsonObject
            {
                ["profile"] = profile.Value?.DeepClone(),
                ["notes"] = new JsonObject { ["a"] = (await notes.GetAsync("a", ctx.Context))?.DeepClone(), ["b"] = (await notes.GetAsync("b", ctx.Context))?.DeepClone() },
                ["rows"] = StoreJson((await rows.PageAsync(Tables.Query(SchemaDeclarations.Rows), 10, ctx.Context)).Items),
                ["edits"] = StoreJson((await edits.PageAsync(Tables.Query(SchemaDeclarations.EditsV3), 10, ctx.Context)).Items),
                ["runs"] = imports.Running.Value!.AsArray().Count,
            }),
        });
    }));
}
