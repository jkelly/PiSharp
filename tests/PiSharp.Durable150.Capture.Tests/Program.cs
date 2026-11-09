// New, unmodified f9a2636 upstream SQLite/JSONL captures; no authored transformations of older evidence.
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage.Jsonl;
using JsonValue = PiSharp.Durable.Chord.JsonValue;

internal static class Program
{
    private static readonly DurableContext Context = DurableContext.Background;
    private const string Pin = "f9a26365568127461c47a07a37dcb42c140a3ac5";
    private static int passed;
    private static int failed;

    private static async Task<int> Main(string[] args)
    {
        var root = args.Length > 0 ? Path.GetFullPath(args[0]) : RepositoryRoot();
        var fixtures = Path.Combine(root, "tests", "PiSharp.Durable.Tests", "Fixtures", "v1.5.0", "upstream-sessions");
        await Case("capture150.manifest-and-raw-file-integrity", () => VerifyManifest(root, fixtures));
        foreach (var run in new[] { "first", "second" })
        foreach (var backend in new[] { "sqlite", "jsonl" })
        {
            var expected = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(fixtures, run, backend, "records.json")))!.AsObject();
            foreach (var stage in expected)
            {
                var name = stage.Key;
                await Case($"capture150.{run}.{backend}.{name}.read", async () =>
                {
                    var temp = Path.Combine(Path.GetTempPath(), "pisharp-capture150-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(temp);
                    try
                    {
                        foreach (var file in Directory.GetFiles(Path.Combine(fixtures, run, backend, name)))
                            File.Copy(file, Path.Combine(temp, Path.GetFileName(file)));
                        IStorage storage = backend == "sqlite" ? await SqliteStorage.OpenAsync(Path.Combine(temp, "session.sqlite")) : await JsonlStorage.OpenAsync(temp, Context);
                        var actual = JsonNode.Parse((await Dump(storage)).ToJson());
                        Same(stage.Value, actual, "all current records, order, versions and values");
                    }
                    finally { Directory.Delete(temp, true); }
                });
            }
        }
        foreach (var backend in new[] { "sqlite", "jsonl" })
            await Case($"capture150.{backend}.two-independent-runs", async () =>
            {
                foreach (var file in new[] { "records.json", "persisted.json", "observers.json", "observed.json" })
                    Same(JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(fixtures, "first", backend, file))),
                        JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(fixtures, "second", backend, file))), file);
            });
        Console.WriteLine($"PASS_UPSTREAM_CAPTURE: {passed}; FAIL: {failed}; SKIP: 0");
        return failed == 0 ? 0 : 1;
    }

    private static async Task VerifyManifest(string root, string fixtures)
    {
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(fixtures, "manifest.json")))!;
        Check(manifest["upstream"]!["commit"]!.GetValue<string>() == Pin, "exact upstream pin");
        Check(manifest["comparison"]!["runs"]!.GetValue<int>() == 2, "two runs");
        Check(manifest["comparison"]!["result"]!.GetValue<string>() == "equal", "successful capture comparison");
        Check(manifest["normalization"]!.AsArray().Count == 1 && manifest["normalization"]![0]!["field"]!.GetValue<string>() == "sessionId string", "explicit normalization only");
        foreach (var run in new[] { "first", "second" })
        foreach (var file in manifest["runs"]![run]!.AsArray())
        {
            var path = file!["path"]!.GetValue<string>();
            var bytes = await File.ReadAllBytesAsync(Path.Combine(fixtures, run, path));
            Check(bytes.Length == file["bytes"]!.GetValue<int>(), path + " bytes");
            Check(Hash(bytes) == file["sha256"]!.GetValue<string>(), path + " SHA256");
        }
        Check(Hash(await File.ReadAllBytesAsync(Path.Combine(root, "tools", "DurableFixtures150", "worker.mjs"))) == manifest["scenarioSha256"]!.GetValue<string>(), "scenario hash");
        Check(Hash(await File.ReadAllBytesAsync(Path.Combine(root, "tools", "DurableFixtures150", "capture.mjs"))) == manifest["captureDriverSha256"]!.GetValue<string>(), "capture driver hash");
    }

    private static async Task<JsonValue> Dump(IStorage storage)
    {
        try
        {
            var conversations = await storage.ScanConversationsAsync(new ConversationQuery(), 1000, null, Context);
            Check(conversations.Next is null, "all conversations in one page");
            var entries = new List<KeyValuePair<string, JsonValue>>();
            foreach (var conversation in conversations.Items)
            {
                var page = await storage.ScanEntriesAsync(new EntryQuery(conversation.Id) { Order = ScanOrder.Ascending }, 1000, null, Context);
                Check(page.Next is null, "all entries in one page");
                entries.Add(new(conversation.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), JsonValue.Array(page.Items.Select(x => (JsonValue)x.ToJson()))));
            }
            var tasks = await storage.ScanTasksAsync(new TaskQuery(), 1000, null, Context);
            var submissions = await storage.ScanSubmissionsAsync(new SubmissionQuery(), 1000, null, Context);
            Check(tasks.Next is null && submissions.Next is null, "all tasks/submissions in one page");
            var scopes = new List<DocumentScope> { DocumentScope.Session };
            scopes.AddRange(conversations.Items.Select(x => DocumentScope.Conversation(x.Id)));
            scopes.AddRange(tasks.Items.Select(x => DocumentScope.Task(x.Id)));
            var documents = new List<JsonValue>();
            foreach (var scope in scopes)
            {
                var page = await storage.ScanDocumentsAsync(new DocumentQuery(scope, DocumentPoint.Current), 1000, null, Context);
                Check(page.Next is null, "all documents in one page");
                foreach (var doc in page.Items)
                {
                    var stored = (await storage.GetDocumentAsync(doc.Id, DocumentPoint.Current, Context))!;
                    documents.Add(Obj(("record", stored.Record.ToJson()), ("version", stored.Version), ("value", stored.Value)));
                }
            }
            return Obj(("conversations", JsonValue.Array(conversations.Items.Select(x => (JsonValue)x.ToJson()))),
                ("entries", JsonValue.Object(entries)), ("tasks", JsonValue.Array(tasks.Items.Select(x => (JsonValue)x.ToJson()))),
                ("submissions", JsonValue.Array(submissions.Items.Select(x => (JsonValue)x.ToJson()))), ("documents", JsonValue.Array(documents)));
        }
        finally { await storage.CloseAsync(Context); }
    }

    private static JsonObjectValue Obj(params (string Key, JsonValue Value)[] pairs) => JsonValue.Object(pairs.Select(x => new KeyValuePair<string, JsonValue>(x.Key, x.Value)));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); }
    private static JsonNode? Normalize(JsonNode? node)
    {
        if (node is JsonObject obj) return new JsonObject(obj.Select(x => new KeyValuePair<string, JsonNode?>(x.Key,
            x.Key == "sessionId" && x.Value is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out _) ? System.Text.Json.Nodes.JsonValue.Create("<session>") : Normalize(x.Value))));
        if (node is JsonArray array) return new JsonArray(array.Select(Normalize).ToArray());
        return node?.DeepClone();
    }
    private static void Same(JsonNode? expected, JsonNode? actual, string label) => Check(Normalize(expected)?.ToJsonString() == Normalize(actual)?.ToJsonString(), label);
    private static async Task Case(string name, Func<Task> run)
    {
        try { await run(); passed++; Console.WriteLine("PASS_UPSTREAM_CAPTURE " + name); }
        catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
    }
    private static string RepositoryRoot()
    {
        for (var path = new DirectoryInfo(AppContext.BaseDirectory); path is not null; path = path.Parent)
            if (File.Exists(Path.Combine(path.FullName, "PiSharp.slnx"))) return path.FullName;
        throw new InvalidOperationException("Pass the repository root as the first argument");
    }
}
