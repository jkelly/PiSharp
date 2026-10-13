// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): compatibility with sessions the pinned upstream itself wrote. Captured upstream evidence,
// counted apart from the authored cases (status PASS_UPSTREAM_CAPTURE): Fixtures/upstream-sessions holds SQLite and JSONL sessions that
// pi-durable wrote with pi-ai's faux provider, the records upstream reads back from them, and a Chord tracker ops corpus, all produced by
// tools/DurableFixtures/capture.mjs (see its manifest.json). There is no upstream test file. For each scenario and backend:
// - read: PiSharp's storage opens a copy and reads every record equal to upstream's, key order included.
// - raw: every persisted JSON text (SQLite record and content columns, JSONL lines and their records) decodes into PiSharp's record types
//   and encodes back to the same text.
// - harness: a PiSharp Harness opens the copy without resuming it, reconciles its surviving running tasks to pending as upstream's open does,
//   reads upstream's transcripts, tasks and pi.* documents, and closes leaving upstream's records (opened.json: upstream's own open and close).
// - resume (resumable scenarios): PiSharp resumes the copy with the scripted model and ends with upstream's own resumed records.
// - written: PiSharp runs the same scenario (CompatScenarios.cs) and writes the same records; the JSONL files are byte for byte upstream's,
//   and the SQLite record, revision and metadata columns hold upstream's text.
// Only the fields manifest.json lists under "varies" are normalized: sessionId strings and durationMs numbers.
// compat.ops.* replays the ops corpus (CompatOpsCases.cs); compat.reverse.* has the pinned upstream read PiSharp's sessions under Node.
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage.Jsonl;
using static ChatTests;
using static Program;
using static SessionTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> CompatCases() => CompatTests.Cases();
}

/// <summary>The compat.* session cases.</summary>
internal static partial class CompatTests
{
    private static readonly DurableContext Context = DurableContext.Background;
    internal static readonly string[] Backends = ["sqlite", "jsonl"];

    internal static string Fixtures => Path.Combine(RepositoryRoot(), "tests", "PiSharp.Durable.Tests", "Fixtures", "upstream-sessions");

    private static readonly Lazy<JsonObjectValue> Manifest = new(() => Load("manifest.json"));

    internal static JsonObjectValue Load(string path) => (JsonObjectValue)JsonValue.Parse(File.ReadAllText(Path.Combine(Fixtures, path)));

    /// <summary>The fields that vary between captures (manifest.json "varies"), on both sides.</summary>
    internal static JsonValue Normalize(JsonValue value, string? key = null) => value switch
    {
        JsonArrayValue items => JsonValue.Array(items.Select(item => Normalize(item))),
        JsonObjectValue properties => JsonValue.Object(properties.Select(property => KeyValuePair.Create(property.Key, Normalize(property.Value, property.Key)))),
        JsonStringValue when key == "sessionId" => "<session>",
        JsonNumberValue when key == "durationMs" => "<duration>",
        _ => value,
    };

    private static readonly Regex VaryingText = new("\"sessionId\":\"[^\"]*\"|\"durationMs\":[0-9]+", RegexOptions.CultureInvariant);

    /// <summary>The same normalization on persisted JSON text.</summary>
    internal static string NormalizeText(string text) => VaryingText.Replace(text, match =>
        match.Value.StartsWith("\"sessionId\"", StringComparison.Ordinal) ? "\"sessionId\":\"<session>\"" : "\"durationMs\":\"<duration>\"");

    /// <summary>Equal JSON text after normalization: values and key order. On a difference, names the first differing path.</summary>
    internal static void SameJson(JsonValue expected, JsonValue actual, string what)
    {
        var (left, right) = (Normalize(expected), Normalize(actual));
        if (left.ToJson() == right.ToJson()) return;
        throw new InvalidOperationException($"{what}: {FirstDifference(left, right, "$")}");
    }

    private static string FirstDifference(JsonValue expected, JsonValue actual, string path)
    {
        switch (expected, actual)
        {
            case (JsonObjectValue left, JsonObjectValue right):
                if (!left.Keys.SequenceEqual(right.Keys))
                {
                    if (left.Keys.Order(StringComparer.Ordinal).SequenceEqual(right.Keys.Order(StringComparer.Ordinal)))
                        return $"{path}: key order: expected [{string.Join(",", left.Keys)}], actual [{string.Join(",", right.Keys)}]";
                    return $"{path}: keys: expected [{string.Join(",", left.Keys)}], actual [{string.Join(",", right.Keys)}]";
                }
                foreach (var key in left.Keys)
                    if (left[key].ToJson() != right[key].ToJson()) return FirstDifference(left[key], right[key], $"{path}.{key}");
                break;
            case (JsonArrayValue left, JsonArrayValue right):
                if (left.Count != right.Count) return $"{path}: expected {left.Count} items, actual {right.Count}: expected {Clip(left)}, actual {Clip(right)}";
                for (var index = 0; index < left.Count; index++)
                    if (left[index].ToJson() != right[index].ToJson()) return FirstDifference(left[index], right[index], $"{path}[{index}]");
                break;
        }
        return $"{path}: expected {Clip(expected)}, actual {Clip(actual)}";
    }

    private static string Clip(JsonValue value)
    {
        var text = value.ToJson();
        return text.Length <= 2000 ? text : text[..2000] + "...";
    }

    /// <summary>Opens PiSharp's storage over a session directory: <c>session.sqlite</c> in it, or the directory as JSONL storage.</summary>
    internal static async Task<IStorage> OpenBackend(string backend, string directory) => backend switch
    {
        "sqlite" => await SqliteStorage.OpenAsync(Path.Combine(directory, "session.sqlite")),
        "jsonl" => await JsonlStorage.OpenAsync(directory, Context),
        _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, null),
    };

    /// <summary>A fresh copy of a captured session, so no case changes the fixtures.</summary>
    internal static string CopyFixture(string scenario, string backend)
    {
        var target = Temp($"compat-{scenario}-{backend}");
        foreach (var file in Directory.GetFiles(Path.Combine(Fixtures, scenario, backend)))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        return target;
    }

    private static async Task<JsonArrayValue> All<T>(Func<Task<Page<T>>> scan, Func<T, JsonValue> json)
    {
        var page = await scan();
        Check(page.Next is null, "a dump reads one page");
        return JsonValue.Array(page.Items.Select(json));
    }

    /// <summary>scenarios.mjs <c>dumpStorage</c>: everything the storage reads back, in its own order and key order. Closes it.</summary>
    internal static async Task<JsonObjectValue> Dump(IStorage storage)
    {
        try
        {
            var conversationPage = await storage.ScanConversationsAsync(new ConversationQuery(), 1000, null, Context);
            Check(conversationPage.Next is null, "a dump reads one page");
            var conversations = conversationPage.Items;
            var entries = new List<KeyValuePair<string, JsonValue>>();
            foreach (var conversation in conversations)
                entries.Add(KeyValuePair.Create(conversation.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), (JsonValue)await All(
                    () => storage.ScanEntriesAsync(new EntryQuery(conversation.Id) { Order = ScanOrder.Ascending }, 1000, null, Context),
                    entry => entry.ToJson())));
            var taskPage = await storage.ScanTasksAsync(new TaskQuery(), 1000, null, Context);
            Check(taskPage.Next is null, "a dump reads one page");
            var submissions = await All(() => storage.ScanSubmissionsAsync(new SubmissionQuery(), 1000, null, Context), submission => submission.ToJson());
            var scopes = new List<DocumentScope> { DocumentScope.Session };
            scopes.AddRange(conversations.Select(conversation => DocumentScope.Conversation(conversation.Id)));
            scopes.AddRange(taskPage.Items.Select(task => DocumentScope.Task(task.Id)));
            var documents = new List<JsonValue>();
            foreach (var scope in scopes)
            {
                var page = await storage.ScanDocumentsAsync(new DocumentQuery(scope, DocumentPoint.Current), 1000, null, Context);
                Check(page.Next is null, "a dump reads one page");
                foreach (var record in page.Items)
                {
                    var stored = (await storage.GetDocumentAsync(record.Id, DocumentPoint.Current, Context))!;
                    documents.Add(Obj(("record", stored.Record.ToJson()), ("version", stored.Version), ("value", stored.Value)));
                }
            }
            return Obj(
                ("conversations", JsonValue.Array(conversations.Select(conversation => (JsonValue)conversation.ToJson()))),
                ("entries", JsonValue.Object(entries)),
                ("tasks", JsonValue.Array(taskPage.Items.Select(task => (JsonValue)task.ToJson()))),
                ("submissions", submissions),
                ("documents", JsonValue.Array(documents)));
        }
        finally
        {
            await storage.CloseAsync(Context);
        }
    }

    private static JsonValue Expected(string scenario, string file, string backend) => Load(Path.Combine(scenario, file))[backend];

    internal static IEnumerable<(string, Func<Task>)> Cases()
    {
        var captured = ((JsonArrayValue)Manifest.Value["scenarios"]).Select(item => S(((JsonObjectValue)item)["name"])).ToList();
        Check(captured.SequenceEqual(CompatScenarios.All.Select(scenario => scenario.Name)), "CompatScenarios.cs runs the captured scenarios, in order");
        foreach (var scenario in CompatScenarios.All)
        {
            foreach (var backend in Backends)
            {
                var prefix = $"compat.{scenario.Name}.{backend}.";
                yield return Case(prefix + "read", async () =>
                    SameJson(Expected(scenario.Name, "records.json", backend), await Dump(await OpenBackend(backend, CopyFixture(scenario.Name, backend))),
                        "records"));
                yield return Case(prefix + "raw", () =>
                {
                    var directory = CopyFixture(scenario.Name, backend);
                    var checkedTexts = backend == "sqlite" ? RawSqlite(Path.Combine(directory, "session.sqlite")) : RawJsonl(directory);
                    Check(checkedTexts > 0, "persisted texts were checked");
                });
                yield return Case(prefix + "harness", () => HarnessReads(scenario.Name, backend));
                if (scenario.Resume is { } resume)
                    yield return Case(prefix + "resume", async () =>
                    {
                        var directory = CopyFixture(scenario.Name, backend);
                        await resume(() => OpenBackend(backend, directory));
                        SameJson(Expected(scenario.Name, "resumed.json", backend), await Dump(await OpenBackend(backend, directory)), "resumed records");
                    });
                yield return Case(prefix + "written", async () =>
                {
                    var directory = Temp($"compat-written-{scenario.Name}-{backend}");
                    await scenario.Run(() => OpenBackend(backend, directory));
                    SameJson(Expected(scenario.Name, "records.json", backend), await Dump(await OpenBackend(backend, directory)), "records");
                    var upstream = Path.Combine(Fixtures, scenario.Name, backend);
                    if (backend == "jsonl") SameFiles(upstream, directory);
                    else SameColumns(Path.Combine(upstream, "session.sqlite"), Path.Combine(directory, "session.sqlite"));
                    if (scenario.Resume is { } resume)
                    {
                        await resume(() => OpenBackend(backend, directory));
                        SameJson(Expected(scenario.Name, "resumed.json", backend), await Dump(await OpenBackend(backend, directory)), "resumed records");
                    }
                });
            }
        }
    }

    // ─── harness ────────────────────────────────────────────────────────────────

    private static readonly (string Name, ConversationDocToken Doc)[] PiDocuments =
    [
        ("pi.live", Live.LiveDoc), ("pi.inbox", Inbox.InboxDoc), ("pi.usage", UsageLedger.UsageDoc), ("pi.agent", Agents.AgentDoc),
        ("pi.provider", ProviderIdentity.ProviderDoc),
    ];

    /// <summary>A Harness that is never resumed reads upstream's transcripts, tasks and pi.* documents, and closes with upstream's records.</summary>
    private static async Task HarnessReads(string scenario, string backend)
    {
        var expected = (JsonObjectValue)Expected(scenario, "opened.json", backend);
        var directory = CopyFixture(scenario, backend);
        var setup = new ChatSetup(new FauxModels(new FauxOptions { Api = "faux" }));
        var harness = await Harness.OpenAsync(await OpenBackend(backend, directory),
            new HarnessOptions { Models = setup.Models, Registry = setup.Registry, Now = () => CompatScenarios.Now }, Context);
        try
        {
            var documents = ((JsonArrayValue)expected["documents"]).Cast<JsonObjectValue>().ToList();
            foreach (var conversationJson in ((JsonArrayValue)expected["conversations"]).Cast<JsonObjectValue>())
            {
                var id = new ConversationId((long)N(conversationJson["id"]));
                var conversation = (await harness.ConversationAsync(id, Context))!;
                var page = await conversation.EntriesAsync(new EntryQueryBounds(), 1000, null, Context);
                SameJson(((JsonObjectValue)expected["entries"])[id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                    JsonValue.Array(page.Items.Reverse().Select(entry => (JsonValue)entry.ToJson())), $"conversation {id} entries");
                foreach (var (name, doc) in PiDocuments)
                {
                    var stored = documents.SingleOrDefault(document =>
                    {
                        var record = (JsonObjectValue)document["record"];
                        var scope = (JsonObjectValue)record["scope"];
                        return S(record["kind"]) == name && S(scope["kind"]) == "conversation" && N(scope["conversationId"]) == id.Value;
                    });
                    var snapshot = await harness.SnapshotAsync(doc, id, Context);
                    if (stored is null) Check(snapshot is null, $"conversation {id} has no {name}");
                    else SameJson(stored["value"], snapshot!, $"conversation {id} {name}");
                }
            }
            foreach (var taskJson in ((JsonArrayValue)expected["tasks"]).Cast<JsonObjectValue>())
            {
                var task = await harness.GetTaskAsync(new TaskId((long)N(taskJson["id"])), Context);
                SameJson(taskJson, task!.ToJson(), $"task {N(taskJson["id"])}");
            }
            var inspection = await harness.InspectAsync(Context);
            Equal(((JsonArrayValue)expected["tasks"]).Count(task => S(((JsonObjectValue)((JsonObjectValue)task)["state"])["status"]) != "terminal"),
                inspection.Tasks.Length, "live tasks");
        }
        finally
        {
            await harness.CloseAsync(Context);
        }
        SameJson(expected, await Dump(await OpenBackend(backend, directory)), "records after the Harness closed");
    }

    // ─── raw ────────────────────────────────────────────────────────────────────

    /// <summary>Checks one persisted JSON text: JavaScript formatting of the parsed value, and PiSharp's record encoding when given.</summary>
    private static void SameText(string raw, Func<JsonValue, JsonValue>? reencode, string what)
    {
        var parsed = JsonValue.Parse(raw);
        Equal(raw, parsed.ToJson(), what + " text");
        if (reencode is not null) Equal(raw, reencode(parsed).ToJson(), what + " record");
    }

    private static readonly (string Table, Func<JsonValue, JsonValue> Reencode)[] RecordTables =
    [
        ("conversations", json => ConversationRecord.FromJson(json).ToJson()),
        ("entries", json => EntryRecord.FromJson(json).ToJson()),
        ("tasks", json => TaskRecord.FromJson(json).ToJson()),
        ("submissions", json => SubmissionRecord.FromJson(json).ToJson()),
        ("documents", json => DocumentRecord.FromJson(json).ToJson()),
    ];

    private static SqliteConnection ReadOnly(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static List<string[]> Rows(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<string[]>();
        while (reader.Read())
            rows.Add([.. Enumerable.Range(0, reader.FieldCount).Select(index => reader.IsDBNull(index) ? "null" : Convert.ToString(reader.GetValue(index),
                System.Globalization.CultureInfo.InvariantCulture)!)]);
        return rows;
    }

    private static int RawSqlite(string path)
    {
        var count = 0;
        using var connection = ReadOnly(path);
        Equal("1", Rows(connection, "SELECT version FROM durable_schema")[0][0], "durable_schema version");
        foreach (var (table, reencode) in RecordTables)
            foreach (var row in Rows(connection, $"SELECT id, record FROM {table} ORDER BY id"))
            {
                SameText(row[1], reencode, $"{table} {row[0]}");
                count++;
            }
        foreach (var row in Rows(connection, "SELECT document_id, seq, kind, content FROM document_revisions ORDER BY document_id, seq"))
        {
            // A base stores its value, a delta its operations.
            SameText(row[3], row[2] == "base" ? null : json => JsonValue.Array(((JsonArrayValue)json).Select(op => (JsonValue)Op.FromJson(op).ToJson())),
                $"document {row[0]} revision {row[1]}");
            count++;
        }
        return count;
    }

    private static readonly IReadOnlyDictionary<string, Func<JsonValue, JsonValue>> WriteValues = new Dictionary<string, Func<JsonValue, JsonValue>>
    {
        ["conversation"] = json => ConversationRecord.FromJson(json).ToJson(),
        ["entry"] = json => EntryRecord.FromJson(json).ToJson(),
        ["submission"] = json => SubmissionRecord.FromJson(json).ToJson(),
        ["task"] = json => TaskRecord.FromJson(json).ToJson(),
    };

    private static int RawJsonl(string directory)
    {
        var count = 0;
        foreach (var file in Directory.GetFiles(directory).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            var text = File.ReadAllText(file);
            Check(text.EndsWith('\n'), $"{name} ends with a newline");
            var lines = text[..^1].Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                var what = $"{name} line {index + 1}";
                SameText(lines[index], null, what);
                var line = (JsonObjectValue)JsonValue.Parse(lines[index]);
                if (S(line["type"]) == "commit")
                {
                    foreach (var write in ((JsonArrayValue)line["writes"]).Cast<JsonObjectValue>())
                    {
                        var type = S(write["type"]);
                        if (WriteValues.TryGetValue(type, out var reencode)) SameText(write["value"].ToJson(), reencode, $"{what} {type}");
                        else if (type == "document.create") SameText(write["record"].ToJson(), json => DocumentCreate.FromJson(json).ToJson(), $"{what} {type}");
                        count++;
                    }
                }
                else
                {
                    var payload = (JsonObjectValue)line["payload"];
                    if (S(payload["type"]) == "task") SameText(payload["value"].ToJson(), json => TaskRecord.FromJson(json).ToJson(), $"{what} task");
                    else SameText(payload["content"].ToJson(), json => DocumentContent.FromJson(json).ToJson(), $"{what} document content");
                    count++;
                }
            }
        }
        return count;
    }

    // ─── written ────────────────────────────────────────────────────────────────

    /// <summary>The JSONL session PiSharp wrote is upstream's, byte for byte, apart from the varying fields.</summary>
    private static void SameFiles(string upstream, string pisharp)
    {
        static List<string> Names(string directory) => [.. Directory.GetFiles(directory).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)];
        Equal(string.Join(",", Names(upstream)), string.Join(",", Names(pisharp)), "JSONL files");
        foreach (var name in Names(upstream))
        {
            var expected = NormalizeText(File.ReadAllText(Path.Combine(upstream, name))).Split('\n');
            var actual = NormalizeText(File.ReadAllText(Path.Combine(pisharp, name))).Split('\n');
            for (var index = 0; index < Math.Max(expected.Length, actual.Length); index++)
                Equal(index < expected.Length ? expected[index] : "<end>", index < actual.Length ? actual[index] : "<end>", $"{name} line {index + 1}");
        }
    }

    private static readonly string[] Columns =
    [
        "SELECT next_id, next_seq FROM durable_metadata",
        "SELECT id, record_type FROM record_ids ORDER BY id",
        "SELECT id, owner_conversation_id, owner_task_id, record FROM conversations ORDER BY id",
        "SELECT id, conversation_id, head, commit_seq, record FROM entries ORDER BY id",
        "SELECT id, conversation_id, kind, status, abort_requested, background, record FROM tasks ORDER BY id",
        "SELECT id, conversation_id, request_id, status, record FROM submissions ORDER BY id",
        "SELECT id, kind, family, key_value, scope_kind, owner_id, created_at, retired_at, record FROM documents ORDER BY id",
        "SELECT document_id, seq, kind, version, content FROM document_revisions ORDER BY document_id, seq",
    ];

    /// <summary>The SQLite session PiSharp wrote holds upstream's rows and JSON text, apart from the varying fields.</summary>
    private static void SameColumns(string upstream, string pisharp)
    {
        using var left = ReadOnly(upstream);
        using var right = ReadOnly(pisharp);
        foreach (var sql in Columns)
        {
            var expected = Rows(left, sql);
            var actual = Rows(right, sql);
            for (var index = 0; index < Math.Max(expected.Count, actual.Count); index++)
                Equal(index < expected.Count ? NormalizeText(string.Join(" | ", expected[index])) : "<end>",
                    index < actual.Count ? NormalizeText(string.Join(" | ", actual[index])) : "<end>", $"{sql} row {index + 1}");
        }
    }
}
