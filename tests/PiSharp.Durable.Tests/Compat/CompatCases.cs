// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): compatibility with sessions the pinned upstream itself wrote. Captured upstream evidence,
// counted apart from the authored cases (status PASS_UPSTREAM_CAPTURE): Fixtures/v1.2.0/upstream-sessions holds SQLite and JSONL sessions
// that pi-durable wrote with pi-ai's faux provider, the records upstream reads back from them, and a Chord tracker ops corpus, all produced by
// tools/DurableFixtures/capture.mjs (see its manifest.json). There is no upstream test file. For each scenario and backend:
// - read: PiSharp's storage opens a copy and reads every record equal to upstream's, key order included.
// - raw: every persisted JSON text (SQLite record and content columns, JSONL lines and their records) decodes into PiSharp's record types
//   and encodes back to the same text.
// - harness: a PiSharp Harness opens the copy without resuming it, reconciles its surviving running tasks to pending as upstream's open does,
//   reads upstream's transcripts, tasks and pi.* documents, and closes leaving upstream's records (opened.json: upstream's own open and close).
// - resume (resumable scenarios): PiSharp resumes the copy with the scripted model and ends with upstream's own resumed records.
// - written: PiSharp runs the same scenario (CompatScenarios.cs) and writes the same records; the JSONL files are byte for byte upstream's,
//   and the SQLite record, revision and metadata columns hold upstream's text.
// Only the fields manifest.json lists under "varies" are normalized: sessionId strings, durationMs numbers, and the codemode scenario's
// wall time, clock and seed; and, PiSharp's own, code mode's engine fingerprint (QuickJS upstream, Jint here: decision 0005).
// The scenarios new in Pi v1.2.0 (CompatScenarios120.cs) run the same checks, plus observed (observed.json: what calls returned or rejected
// with) and events (events.json: the root conversation's agent events, the snapshot first, from the run and from the resume).
// Pi v1.5.0 sync (area tools-schema): their written cases compare with ToolsSchema150.cs's authored forms of the capture, not the capture
// itself: v1.5.0 declares the built-in tools with pi-schema's key order and prints pi-schema's validation messages. Only those differ.
// Pi v1.5.0 sync (area model-requests): every scenario's written, resume and upgrade resumed cases compare with ModelRequests150.cs's authored
// forms, not the capture: v1.5.0 records the requested thinking level on each message it classifies, "off" here. A resume classifies only
// the messages it adds, so the messages the resumed session already held keep their captured form. A case that compares with an authored
// form is authored, not captured evidence: AuthoredOverCapture lists them, and the run reports them as authored.
// Pi v1.5.0 sync (area deferred-commits): the written cases also compare with DeferredCommits150.cs's authored forms: v1.5.0 holds the
// commits that only show progress in pi.live and stores them with the next commit that reads or writes it, or at close, so the commits
// after a held one move down a seq, and the held changes are stored with a later commit. A resume after the run reads the moved seqs.
// Pi v1.5.0 sync (area run-failure): the written cases also compare with RunFailure150.cs's authored forms: v1.5.0's pi.live keeps the
// runFailure of a newest run that ended without an answer (compaction-failure's blocking turn), and PiSharp's run_end event carries it.
// Pi v1.5.0 sync (area events, f3255de76): v1.5.0 removed watchEvents with its events; the events comparisons check PiSharp's projection
// (PiSharp.Durable.Hosting's AgentEvents.cs) against the v1.2.0 events.json captures, which stays honest only while the projection
// reproduces v1.2.0's events. They are evidence for the projection, not for v1.5.0.
// Pi v1.5.0 sync (area restarts, b3a29f111): the cases of a capture that left tasks running compare with Restarts150.cs's authored forms:
// v1.5.0's open counts a restart of each task it finds running (harness, upgrade opened: "restarts": 1), and a clean close writes the
// tasks it stopped back as pending (written, through DeferredCommits150's plan: one more commit, before the held documents are stored).
// A resume of a capture that left a generation's request in flight charges the attempt: the records it ends with stay the captured ones,
// and its events show the backoff (resume events). Its scenario retries at once, as a backoff never ends on the scenarios' frozen clock.
// - upgrade-from-v1.1.0: a v1.2.0 Harness opens copies of the v1.1.0 sessions (Fixtures/upstream-sessions) without resuming them, and
//   resumes the resumable ones with their scenario's model, ending with the records upstream's v1.2.0 left (its v1 pi.tool tasks migrate to
//   version 2 when they run).
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
    private static IEnumerable<(string, Func<Task>)> CompatCases() => [.. CompatTests.Cases(), .. CompatTests.Cases120()];
}

/// <summary>The compat.* session cases.</summary>
internal static partial class CompatTests
{
    private static readonly DurableContext Context = DurableContext.Background;
    internal static readonly string[] Backends = ["sqlite", "jsonl"];

    /// <summary>The Pi v1.2.0 capture (Fixtures/v1.2.0/index.json).</summary>
    internal static string Fixtures => Path.Combine(RepositoryRoot(), "tests", "PiSharp.Durable.Tests", "Fixtures", "v1.2.0", "upstream-sessions");

    /// <summary>The v1.2.0 capture, for the scenarios new in v1.2.0.</summary>
    internal static string Fixtures120 => Fixtures;

    /// <summary>The Pi v1.1.0 capture, which upgrade-from-v1.1.0 opens with v1.2.0.</summary>
    internal static string Fixtures110 => Path.Combine(RepositoryRoot(), "tests", "PiSharp.Durable.Tests", "Fixtures", "upstream-sessions");

    private static readonly Lazy<JsonObjectValue> Manifest = new(() => Load("manifest.json"));

    internal static JsonObjectValue Load(string path, string? root = null) =>
        (JsonObjectValue)JsonValue.Parse(File.ReadAllText(Path.Combine(root ?? Fixtures, path)));

    /// <summary>The fields that vary between captures (manifest.json "varies"), on both sides (compare-captures.mjs <c>normalize</c>):
    /// sessionId strings, durationMs numbers, the seconds of "Wall time &lt;seconds&gt; seconds", and code mode's own clock and seed (in an
    /// object with a numeric <c>seed</c>, pi.codemode.run's start, that seed and its startedAt and deadline; the <c>at</c> of an object of only
    /// id, at, issuedAfter and global, a pi.codemode.journal entry). PiSharp also normalizes the start's engine <c>fingerprint</c>, which names
    /// QuickJS upstream and Jint here (an intentional difference, decision 0005), as a property and as the value of a set operation.</summary>
    internal static JsonValue Normalize(JsonValue value, string? key = null) => value switch
    {
        JsonArrayValue { Count: 3 } items when items[0] is JsonStringValue { Value: "s" } && items[1] is JsonArrayValue { Count: > 0 } path &&
            path[^1] is JsonStringValue { Value: "fingerprint" } && items[2] is JsonStringValue =>
            JsonValue.Array([Normalize(items[0]), Normalize(items[1]), "<fingerprint>"]),
        JsonArrayValue items => JsonValue.Array(items.Select(item => Normalize(item))),
        JsonObjectValue properties => NormalizeObject(properties),
        JsonStringValue when key == "sessionId" => "<session>",
        JsonStringValue when key == "fingerprint" => "<fingerprint>",
        JsonNumberValue when key == "durationMs" => "<duration>",
        JsonStringValue text when text.Value.Contains("Wall time ", StringComparison.Ordinal) => WallTime.Replace(text.Value, "Wall time <seconds> seconds"),
        _ => value,
    };

    private static readonly HashSet<string> JournalEntryKeys = new(["id", "at", "issuedAfter", "global"], StringComparer.Ordinal);

    private static JsonObjectValue NormalizeObject(JsonObjectValue properties)
    {
        var seeded = properties.TryGetValue("seed", out var seed) && seed is JsonNumberValue;
        var journalEntry = properties.TryGetValue("id", out var id) && id is JsonNumberValue && properties.TryGetValue("at", out var at) && at is JsonNumberValue &&
            properties.Keys.All(JournalEntryKeys.Contains);
        return JsonValue.Object(properties.Select(property => KeyValuePair.Create(property.Key, property switch
        {
            { Key: "seed" } when seeded => "<seed>",
            { Key: "startedAt" or "deadline", Value: JsonNumberValue } when seeded => "<clock>",
            { Key: "at" } when journalEntry => (JsonValue)"<clock>",
            _ => Normalize(property.Value, property.Key),
        })));
    }

    private static readonly Regex WallTime = new(@"Wall time [\d.]+ seconds", RegexOptions.CultureInvariant);

    private static readonly Regex VaryingText = new(
        "\"sessionId\":\"[^\"]*\"|\"durationMs\":[0-9]+|Wall time [0-9.]+ seconds|\"startedAt\":[0-9]+,\"seed\":[0-9]+(,\"deadline\":[0-9]+)?|" +
        "\\[\"start\",\"fingerprint\"\\],\"[^\"]*\"|\"fingerprint\":\"[^\"]*\"|\\{\"id\":[0-9]+,\"at\":[0-9]+",
        RegexOptions.CultureInvariant);

    /// <summary>The same normalization on persisted JSON text. Code mode's start is matched by its key order (version, startedAt, seed,
    /// deadline?) and a journal entry by its leading id and at, which only those objects have.</summary>
    internal static string NormalizeText(string text) => VaryingText.Replace(text, match => match.Value switch
    {
        var found when found.StartsWith("\"sessionId\"", StringComparison.Ordinal) => "\"sessionId\":\"<session>\"",
        var found when found.StartsWith("\"durationMs\"", StringComparison.Ordinal) => "\"durationMs\":\"<duration>\"",
        var found when found.StartsWith("Wall time", StringComparison.Ordinal) => "Wall time <seconds> seconds",
        var found when found.StartsWith("\"startedAt\"", StringComparison.Ordinal) =>
            "\"startedAt\":\"<clock>\",\"seed\":\"<seed>\"" + (found.Contains("deadline", StringComparison.Ordinal) ? ",\"deadline\":\"<clock>\"" : ""),
        var found when found.StartsWith("[", StringComparison.Ordinal) => "[\"start\",\"fingerprint\"],\"<fingerprint>\"",
        var found when found.StartsWith("\"fingerprint\"", StringComparison.Ordinal) => "\"fingerprint\":\"<fingerprint>\"",
        var found => found[..(found.IndexOf("\"at\":", StringComparison.Ordinal) + 5)] + "\"<clock>\"",
    });

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
    internal static string CopyFixture(string scenario, string backend, string? root = null)
    {
        var target = Temp($"compat-{scenario}-{backend}");
        foreach (var file in Directory.GetFiles(Path.Combine(root ?? Fixtures, scenario, backend)))
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

    private static JsonValue Expected(string scenario, string file, string backend, string? root = null) => Load(Path.Combine(scenario, file), root)[backend];

    /// <summary>The compat.* cases whose expectation is an authored Pi v1.5.0 form of a capture (ToolsSchema150.cs, ModelRequests150.cs), not
    /// the capture itself; Program reports them as authored, never as captured evidence. Filled as the cases are listed.</summary>
    internal static readonly HashSet<string> AuthoredOverCapture = new(StringComparer.Ordinal);

    /// <summary>Lists <paramref name="id"/> in <see cref="AuthoredOverCapture"/>.</summary>
    private static string AuthoredCase(string id)
    {
        AuthoredOverCapture.Add(id);
        return id;
    }

    internal static IEnumerable<(string, Func<Task>)> Cases()
    {
        // The scenarios both pins capture: those without `since`.
        var captured = ((JsonArrayValue)Manifest.Value["scenarios"]).Cast<JsonObjectValue>().Where(item => !item.ContainsKey("since"))
            .Select(item => S(item["name"])).ToList();
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
                yield return Case(Counted(prefix + "harness", scenario.Name, backend), () => HarnessReads(scenario.Name, backend));
                if (scenario.Resume is { } resume)
                    yield return Case(AuthoredCase(prefix + "resume"), async () =>
                    {
                        var directory = CopyFixture(scenario.Name, backend);
                        await resume(() => OpenBackend(backend, directory));
                        // Pi v1.5.0 (area model-requests): the messages the resume classifies record the thinking level (ModelRequests150.cs).
                        SameJson(ModelRequests150.Authored(Expected(scenario.Name, "resumed.json", backend), Expected(scenario.Name, "records.json", backend)),
                            await Dump(await OpenBackend(backend, directory)), "resumed records");
                    });
                yield return Case(AuthoredCase(prefix + "written"), async () =>
                {
                    // Pi v1.5.0 (area model-requests): the messages PiSharp classifies record the thinking level (ModelRequests150.cs).
                    // (area deferred-commits) and DeferredCommits150's, the commits v1.5.0 holds and stores later.
                    var deferred = DeferredCommits150.For(Path.Combine(Fixtures, scenario.Name));
                    var directory = Temp($"compat-written-{scenario.Name}-{backend}");
                    await scenario.Run(() => OpenBackend(backend, directory));
                    SameJson(deferred.Records(ModelRequests150.Authored(Expected(scenario.Name, "records.json", backend))), await Dump(await OpenBackend(backend, directory)),
                        "records");
                    var upstream = Path.Combine(Fixtures, scenario.Name, backend);
                    if (backend == "jsonl") SameFiles(upstream, directory, ModelRequests150.AuthoredText, deferred);
                    // A copy: even a read-only connection to a WAL database leaves -wal and -shm files beside it.
                    else SameColumns(Path.Combine(CopyFixture(scenario.Name, backend), "session.sqlite"), Path.Combine(directory, "session.sqlite"),
                        ModelRequests150.AuthoredText, deferred);
                    if (scenario.Resume is { } resume)
                    {
                        await resume(() => OpenBackend(backend, directory));
                        SameJson(deferred.Records(ModelRequests150.Authored(Expected(scenario.Name, "resumed.json", backend))),
                            await Dump(await OpenBackend(backend, directory)), "resumed records");
                    }
                });
            }
        }
        foreach (var scenario in CompatScenarios.All)
        {
            var upgrade = Path.Combine("upgrade-from-v1.1.0", scenario.Name);
            foreach (var backend in Backends)
            {
                var prefix = $"compat.upgrade-from-v1.1.0.{scenario.Name}.{backend}.";
                yield return Case(Counted(prefix + "opened", scenario.Name, backend, Fixtures110), async () =>
                {
                    var directory = CopyFixture(scenario.Name, backend, Fixtures110);
                    await OpenOnly(() => OpenBackend(backend, directory));
                    // Pi v1.5.0 (area restarts): the open counts a restart of each task it finds running (Restarts150.cs).
                    SameJson(Restarts150.Opened(Expected(upgrade, "opened.json", backend), Expected(scenario.Name, "records.json", backend, Fixtures110)),
                        await Dump(await OpenBackend(backend, directory)), "opened records");
                });
                if (scenario.Resume is { } resume)
                    yield return Case(AuthoredCase(prefix + "resumed"), async () =>
                    {
                        var directory = CopyFixture(scenario.Name, backend, Fixtures110);
                        await resume(() => OpenBackend(backend, directory));
                        // Pi v1.5.0 (area model-requests): the messages the resume classifies record the thinking level (ModelRequests150.cs).
                        SameJson(ModelRequests150.Authored(Expected(upgrade, "resumed.json", backend), Expected(upgrade, "opened.json", backend)),
                            await Dump(await OpenBackend(backend, directory)), "resumed records");
                    });
            }
        }
    }

    /// <summary><paramref name="id"/>, listed as authored when the capture of <paramref name="scenario"/> left tasks running, whose restarts
    /// v1.5.0's open counts (area restarts, Restarts150.cs).</summary>
    private static string Counted(string id, string scenario, string backend, string? root = null) =>
        Restarts150.Running(Expected(scenario, "records.json", backend, root)).Count > 0 ? AuthoredCase(id) : id;

    /// <summary>scenarios.mjs <c>openOnly</c>: a Harness opens the session and closes it without resuming.</summary>
    private static async Task OpenOnly(Func<Task<IStorage>> openStorage)
    {
        var setup = new ChatSetup(new FauxModels(new FauxOptions { Api = "faux" }));
        var harness = await Harness.OpenAsync(await openStorage(),
            new HarnessOptions { Models = setup.Models, Registry = setup.Registry, Settings = setup.Settings, Now = () => CompatScenarios.Now }, Context);
        await harness.CloseAsync(Context);
    }

    /// <summary>The compat.* cases of the scenarios new in Pi v1.2.0 (CompatScenarios120.cs), against Fixtures/v1.2.0/upstream-sessions.</summary>
    internal static IEnumerable<(string, Func<Task>)> Cases120()
    {
        var root = Fixtures120;
        var captured = ((JsonArrayValue)Load("manifest.json", root)["scenarios"]).Select(item => S(((JsonObjectValue)item)["name"])).ToHashSet();
        foreach (var scenario in CompatScenarios120.All)
        {
            Check(captured.Contains(scenario.Name), $"Fixtures/v1.2.0 captures {scenario.Name}");
            foreach (var backend in Backends)
            {
                var prefix = $"compat.{scenario.Name}.{backend}.";
                yield return Case(prefix + "read", async () =>
                    SameJson(Expected(scenario.Name, "records.json", backend, root),
                        await Dump(await OpenBackend(backend, CopyFixture(scenario.Name, backend, root))), "records"));
                yield return Case(prefix + "raw", () =>
                {
                    var directory = CopyFixture(scenario.Name, backend, root);
                    var checkedTexts = backend == "sqlite" ? RawSqlite(Path.Combine(directory, "session.sqlite")) : RawJsonl(directory);
                    Check(checkedTexts > 0, "persisted texts were checked");
                });
                yield return Case(Counted(prefix + "harness", scenario.Name, backend, root), () => HarnessReads(scenario.Name, backend, root));
                var eventsFile = Path.Combine(root, scenario.Name, "events.json");
                JsonObjectValue? expectedEvents = File.Exists(eventsFile) ? (JsonObjectValue)Expected(scenario.Name, "events.json", backend, root) : null;
                if (scenario.Resume is { } resume)
                    yield return Case(scenario.ResumeUpstream is null ? AuthoredCase(prefix + "resume") : prefix + "resume", async () =>
                    {
                        var directory = CopyFixture(scenario.Name, backend, root);
                        if (scenario.ResumeUpstream is { } own)
                        {
                            await own(() => OpenBackend(backend, directory));
                            return;
                        }
                        // Pi v1.5.0 (area model-requests): the messages the resume classifies record the thinking level (ModelRequests150.cs).
                        var held = Expected(scenario.Name, "records.json", backend, root);
                        // (area restarts) and the backoff of the request the capture left in flight, which the resume charges (Restarts150.cs).
                        var events = CompatEvents.Expect(expectedEvents is null ? null
                            : (JsonObjectValue)Restarts150.Charged(ModelRequests150.Authored(expectedEvents, held), held, CompatScenarios.Now), "resume");
                        await CompatEvents.Record(events, () => resume(() => OpenBackend(backend, directory)));
                        SameJson(ModelRequests150.Authored(Expected(scenario.Name, "resumed.json", backend, root), held), await Dump(await OpenBackend(backend, directory)),
                            "resumed records");
                        events?.Check("resume events");
                    });
                // A scenario whose run needs a later area's port (CompatScenarios120.cs) is read only.
                if (scenario.Run is not { } runScenario) continue;
                yield return Case(AuthoredCase(prefix + "written"), async () =>
                {
                    // Pi v1.5.0 (area tools-schema): what PiSharp writes is compared with ToolsSchema150's authored forms of the capture, the
                    // built-in tools' pi-schema declarations and pi-schema's validation messages; (area model-requests) and ModelRequests150's, the
                    // thinking level each classified message records.
                    // (area deferred-commits) and DeferredCommits150's, the commits v1.5.0 holds and stores later (records only: no event,
                    // frame or observed value holds a seq).
                    // (area run-failure) and RunFailure150's, the runFailure a scenario's failed newest run leaves in pi.live.
                    var runFailure = RunFailure150.For(scenario.Name);
                    JsonValue Authored(JsonValue capture) => runFailure.Authored(ModelRequests150.Authored(ToolsSchema150.Authored(capture)));
                    string AuthoredText(string text) => runFailure.AuthoredText(ModelRequests150.AuthoredText(ToolsSchema150.AuthoredText(text)));
                    var deferred = DeferredCommits150.For(Path.Combine(root, scenario.Name));
                    var writtenEvents = expectedEvents is null ? null : (JsonObjectValue)Authored(expectedEvents);
                    var directory = Temp($"compat-written-{scenario.Name}-{backend}");
                    var runEvents = CompatEvents.Expect(writtenEvents, "run");
                    var observed = await CompatEvents.Record(runEvents, () => runScenario(() => OpenBackend(backend, directory)));
                    runEvents?.Check("run events");
                    SameJson(deferred.Records(Authored(Expected(scenario.Name, "records.json", backend, root))), await Dump(await OpenBackend(backend, directory)),
                        "records");
                    var upstream = Path.Combine(root, scenario.Name, backend);
                    if (backend == "jsonl") SameFiles(upstream, directory, AuthoredText, deferred);
                    else SameColumns(Path.Combine(CopyFixture(scenario.Name, backend, root), "session.sqlite"), Path.Combine(directory, "session.sqlite"),
                        AuthoredText, deferred);
                    var observedFile = Path.Combine(root, scenario.Name, "observed.json");
                    JsonObjectValue? expectedObserved = File.Exists(observedFile)
                        ? (JsonObjectValue)Authored(Expected(scenario.Name, "observed.json", backend, root)) : null;
                    if (expectedObserved?.TryGetValue("run", out var run) == true)
                        SameObserved(run, observed ?? throw new InvalidOperationException("the run observed nothing"), scenario.UnobservedFields, "observed run");
                    if (scenario.Resume is { } resume)
                    {
                        var resumeEvents = CompatEvents.Expect(writtenEvents, "resume");
                        var resumed = await CompatEvents.Record(resumeEvents, () => resume(() => OpenBackend(backend, directory)));
                        resumeEvents?.Check("resume events");
                        SameJson(deferred.Records(Authored(Expected(scenario.Name, "resumed.json", backend, root))), await Dump(await OpenBackend(backend, directory)),
                            "resumed records");
                        if (expectedObserved?.TryGetValue("resume", out var onResume) == true)
                            SameObserved(onResume, resumed ?? throw new InvalidOperationException("the resume observed nothing"), scenario.UnobservedFields,
                                "observed resume");
                    }
                });
            }
        }
    }

    /// <summary>What a scenario observed equals upstream's, apart from the top-level fields it cannot observe (see CompatScenarios120.cs).</summary>
    private static void SameObserved(JsonValue expected, JsonValue actual, IReadOnlyList<string>? skipped, string what)
    {
        JsonValue Without(JsonValue value) => skipped is null || value is not JsonObjectValue properties ? value
            : JsonValue.Object(properties.Where(property => !skipped.Contains(property.Key)));
        SameJson(Without(expected), Without(actual), what);
    }

    // ─── harness ────────────────────────────────────────────────────────────────

    private static readonly (string Name, ConversationDocToken Doc)[] PiDocuments =
    [
        ("pi.live", Live.LiveDoc), ("pi.inbox", Inbox.InboxDoc), ("pi.usage", UsageLedger.UsageDoc), ("pi.agent", Agents.AgentDoc),
        ("pi.provider", ProviderIdentity.ProviderDoc),
    ];

    /// <summary>A Harness that is never resumed reads upstream's transcripts, tasks and pi.* documents, and closes with upstream's records;
    /// Pi v1.5.0 (area restarts): with a restart counted for each task it found running (Restarts150.cs).</summary>
    private static async Task HarnessReads(string scenario, string backend, string? root = null)
    {
        var expected = (JsonObjectValue)Restarts150.Opened(Expected(scenario, "opened.json", backend, root), Expected(scenario, "records.json", backend, root));
        var directory = CopyFixture(scenario, backend, root);
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

    /// <summary>The JSONL session PiSharp wrote is upstream's, byte for byte, apart from the varying fields; <paramref name="authored"/>, when
    /// given, turns each captured line into its authored form, and <paramref name="deferred"/> then moves the commits v1.5.0 holds.</summary>
    private static void SameFiles(string upstream, string pisharp, Func<string, string>? authored = null, DeferredCommits150.Plan? deferred = null)
    {
        static List<string> Names(string directory) => [.. Directory.GetFiles(directory).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)];
        Equal(string.Join(",", Names(upstream)), string.Join(",", Names(pisharp)), "JSONL files");
        var files = Names(upstream).ToDictionary(name => name,
            name => File.ReadAllText(Path.Combine(upstream, name)).Split('\n').Select(authored ?? (line => line)).ToArray(), StringComparer.Ordinal);
        if (deferred is not null) files = deferred.Jsonl(files);
        foreach (var name in Names(upstream))
        {
            var expected = NormalizeText(string.Join('\n', files[name])).Split('\n');
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

    /// <summary>The SQLite session PiSharp wrote holds upstream's rows and JSON text, apart from the varying fields; <paramref name="authored"/>,
    /// when given, turns each captured column into its authored form, and <paramref name="deferred"/> then moves the commits v1.5.0 holds.</summary>
    private static void SameColumns(string upstream, string pisharp, Func<string, string>? authored = null, DeferredCommits150.Plan? deferred = null)
    {
        using var left = ReadOnly(upstream);
        using var right = ReadOnly(pisharp);
        foreach (var sql in Columns)
        {
            var expected = Rows(left, sql).Select(row => row.Select(authored ?? (cell => cell)).ToArray()).ToList();
            if (deferred is not null) expected = deferred.Rows(sql, expected);
            var actual = Rows(right, sql);
            for (var index = 0; index < Math.Max(expected.Count, actual.Count); index++)
                Equal(index < expected.Count ? NormalizeText(string.Join(" | ", expected[index])) : "<end>",
                    index < actual.Count ? NormalizeText(string.Join(" | ", actual[index])) : "<end>", $"{sql} row {index + 1}");
        }
    }
}
