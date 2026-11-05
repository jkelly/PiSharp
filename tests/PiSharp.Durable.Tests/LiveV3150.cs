// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/src/harness/live.ts (LiveDoc version 3 and its migrate) and
// packages/durable/src/harness/generation.ts (startRun's run.turns, startToolRound's count).
// Authored Pi v1.5.0 expectations over the Pi v1.2.0 captures (decision 0013: fixtures are not recaptured; area live-turns, 5f312dac4,
// f6a9db1d8, 018fdcf43). Nothing here is a capture. A byte comparison with records that an upstream v1.5.0 run wrote needs a recapture.
// v1.5.0 defines pi.live at version 3. A compaction status ({taskId, trigger, blocking, attempt}) and compactionFailure ({taskId, trigger,
// error}) name their trigger where version 1 named it reason (version 2). pi.live.run carries turns, the tool rounds the run started
// (version 3): startRun writes {taskId, inputs, turns: 0}, and the commit that starts a tool round counts the round. That op on run.turns
// comes after the commit's top-level pi.live ops, as Chord's tracker emits a commit's ops by depth. The v1.2.0 captures hold version 1.
// The forms:
// - Written (Plan, for the compat written cases): a run writes every pi.live revision itself, at version 3. This covers the records
//   (Records), the JSONL lines (Text) and the SQLite revisions (Rows). A revision's count is the number of commits of the run's
//   conversation, from the one that placed the run's first input through the revision's own, that append an assistant message stopping
//   for tool use with a tool call (startToolRound's). The count is read off main.jsonl, because a base drops the revisions before it, and
//   a base is stored whenever nothing runs, so also at each round's start. A run with no input is not covered and throws.
// - Resumed: a resume reads a version 1 value migrated, so a run in flight counts its turns from 0. The first commit that touches pi.live
//   stores version 3, so a pi.live document the resume changed is at version 3 with the migrated value; the others stay as captured. A run
//   still in flight after a resume is not covered and throws.
// - Migrated: what a Harness reads from a stored version 1 value (live.ts migrate): compaction statuses and compactionFailure name their
//   trigger, and a run counts 0 turns (the compat harness cases' snapshots).
// - Frames (the views differential, example 19's ops lines): a watch frame's ops. Setting a run gives it turns 0. The frame of a commit
//   that starts a tool round (the one that sets pi.live.tools) sets run.turns after its pi.live ops, counted from the run's set.
// - Differential (the compaction differential): the pi.live values of a Harness differential dump. Their compactionFailure names its
//   trigger, and none holds a run. The chat-turns, inbox and tool-rounds captures hold neither a run nor a compaction status in pi.live.
// The agent event projection (PiSharp.Durable.Hosting's AgentEvents.cs) keeps v1.2.0's wire: compactions and compaction failures name their
// reason, and a run carries no turns. So events and observed values compare as captured.
using System.Globalization;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;

/// <summary>The authored v1.5.0 forms of v1.2.0 captures for pi.live version 3 (see the file header).</summary>
internal static class LiveV3150
{
    /// <summary>pi.live's version in v1.5.0.</summary>
    internal const int Version = 3;

    /// <summary>A pi.live value as v1.5.0 holds it: compaction statuses and compactionFailure name their trigger, and a run carries
    /// <paramref name="turns"/>, which a value with a run must be given.</summary>
    public static JsonObjectValue Value(JsonObjectValue value, long? turns)
    {
        var live = value;
        if (value.TryGetValue("run", out var run) && run is JsonObjectValue fields)
            live = live.SetItem("run", WithTurns(fields, turns ?? throw new InvalidOperationException("a pi.live run this plan cannot count the turns of (live v3)")));
        if (value.TryGetValue("compactions", out var compactions)) live = live.SetItem("compactions", Renamed(compactions));
        if (value.TryGetValue("compactionFailure", out var failure) && failure is JsonObjectValue recorded) live = live.SetItem("compactionFailure", Renamed(recorded));
        return live;
    }

    /// <summary>What a Harness reads from a stored version 1 value (live.ts migrate): the trigger, and a run's turns from 0.</summary>
    public static JsonObjectValue Migrated(JsonObjectValue value) => Value(value, 0);

    /// <summary>Whether <paramref name="records"/> (a records dump) holds a pi.live value that migrates to a different one.</summary>
    public static bool Migrates(JsonValue records) => LiveDocuments(records).Any(document =>
        Migrated((JsonObjectValue)document["value"]).ToJson() != document["value"].ToJson());

    /// <summary>A resumed records dump's authored form: each pi.live document the resume changed (its value differs from the one in
    /// <paramref name="before"/>) at version 3, with its value migrated; the others as captured.</summary>
    public static JsonValue Resumed(JsonValue resumed, JsonValue before)
    {
        var held = LiveDocuments(before).ToDictionary(document => Long(((JsonObjectValue)document["record"])["id"]), document => document["value"].ToJson());
        return MapDocuments(resumed, document => held.TryGetValue(Long(((JsonObjectValue)document["record"])["id"]), out var value) &&
            value == document["value"].ToJson() ? document : Stored(document, Value((JsonObjectValue)document["value"], null)));
    }

    /// <summary>The resumed records dump of a session v1.5.0 wrote: every pi.live document at version 3, no run in flight.</summary>
    public static JsonValue WrittenResumed(JsonValue resumed) => MapDocuments(resumed, document => Stored(document, Value((JsonObjectValue)document["value"], null)));

    /// <summary>A Harness differential capture's authored form: every pi.live value in a <c>docs</c> object, none with a run.</summary>
    public static JsonObjectValue Differential(JsonObjectValue capture) => (JsonObjectValue)Docs(capture);

    private static JsonValue Docs(JsonValue value) => value switch
    {
        JsonArrayValue items => JsonValue.Array(items.Select(Docs)),
        JsonObjectValue properties => JsonValue.Object(properties.Select(property => KeyValuePair.Create(property.Key,
            property.Key == "docs" && property.Value is JsonObjectValue docs && docs.TryGetValue("pi.live", out var live) && live is JsonObjectValue fields
                ? docs.SetItem("pi.live", Value(fields, null)) : Docs(property.Value)))),
        _ => value,
    };

    /// <summary>views.upstream.json's authored form: each scenario's frames (<see cref="Frames"/>), and its initial and final views'
    /// pi.live with the count where each stands.</summary>
    public static JsonObjectValue Views(JsonObjectValue capture) => JsonObjectValue.From(capture.Select(scenario =>
    {
        if (scenario.Value is not JsonObjectValue parts || !parts.TryGetValue("frames", out var frames)) return scenario;
        var counter = new Counter();
        parts = parts.SetItem("initial", View(parts["initial"], counter));
        parts = parts.SetItem("frames", JsonValue.Array(((JsonArrayValue)frames).Select(frame => (JsonValue)counter.Frame((JsonArrayValue)frame))));
        return KeyValuePair.Create(scenario.Key, (JsonValue)parts.SetItem("final", View(parts["final"], counter)));
    }));

    /// <summary>Example 19's ops lines (JSON mode): the first, <c>{"view": ...}</c>, and then one <c>{"ops": frame}</c> per frame, as
    /// <see cref="Views"/> authors them. A line that changes must print back as captured first.</summary>
    public static string[] OpsLines(string[] lines)
    {
        var counter = new Counter();
        return [.. lines.Select((line, index) =>
        {
            var parsed = (JsonObjectValue)JsonValue.Parse(line);
            var authored = index == 0 ? parsed.SetItem("view", View(parsed["view"], counter))
                : parsed.SetItem("ops", counter.Frame((JsonArrayValue)parsed["ops"]));
            return Printed(line, parsed, authored);
        })];
    }

    private static JsonValue View(JsonValue view, Counter counter) =>
        view is JsonObjectValue fields && fields.TryGetValue("docs", out var docs) && docs is JsonObjectValue documents &&
        documents.TryGetValue("pi.live", out var live) && live is JsonObjectValue value
            ? fields.SetItem("docs", documents.SetItem("pi.live", Value(value, counter.Turns))) : view;

    /// <summary>The count of one watch's frames: set to 0 by a run's set, one more at each tool round's start.</summary>
    private sealed class Counter
    {
        private static readonly string[] Prefix = ["docs", "pi.live"];

        public long? Turns { get; private set; }

        public JsonArrayValue Frame(JsonArrayValue frame)
        {
            var ops = new List<JsonValue>();
            var last = -1;
            var round = false;
            foreach (var item in frame)
            {
                var op = (JsonArrayValue)item;
                if (Relative(op, Prefix) is not { } path)
                {
                    ops.Add(op);
                    continue;
                }
                last = ops.Count;
                var kind = ((JsonStringValue)op[0]).Value;
                if (kind == "s" && path is ["run"]) Turns = 0;
                else if (kind == "d" && path is ["run"]) Turns = null;
                else if (kind == "s" && path is ["tools"]) round = true;
                ops.Add(LiveOp(op, Prefix.Length, path, Turns));
            }
            if (round)
            {
                Turns = (Turns ?? throw new InvalidOperationException("a tool round starts without a run (live v3)")) + 1;
                ops.Insert(last + 1, JsonValue.Array("s", JsonValue.Array([.. Prefix.Select(segment => (JsonValue)segment), "run", "turns"]), Turns.Value));
            }
            return JsonValue.Array(ops);
        }
    }

    /// <summary>How a captured session's pi.live revisions become v1.5.0's, read off its jsonl directory (see the file header).</summary>
    internal sealed class Plan
    {
        // pi.live documents and their conversations.
        private readonly Dictionary<long, long> conversations;
        // The run's turns after each captured revision of a pi.live document (null: no run).
        private readonly Dictionary<(long Document, long Seq), long?> turns;
        // The revisions of the commits that start a tool round.
        private readonly HashSet<(long Document, long Seq)> rounds;
        // Each pi.live document's last revision.
        private readonly Dictionary<long, long> last;

        internal Plan(Dictionary<long, long> conversations, Dictionary<(long, long), long?> turns, HashSet<(long, long)> rounds, Dictionary<long, long> last)
        {
            this.conversations = conversations;
            this.turns = turns;
            this.rounds = rounds;
            this.last = last;
        }

        /// <summary>The records dump a written run reads: every pi.live document at version 3, its value with the turns after its last
        /// revision.</summary>
        public JsonValue Records(JsonValue records) => MapDocuments(records, document =>
        {
            var id = Long(((JsonObjectValue)document["record"])["id"]);
            return Stored(document, Value((JsonObjectValue)document["value"], last.TryGetValue(id, out var seq) ? turns[(id, seq)] : null));
        });

        /// <summary>A JSONL line's authored form: a pi.live revision record at version 3 (see <see cref="Content"/>). A line that changes
        /// must print back as captured first.</summary>
        public string Text(string line)
        {
            if (line.Length == 0 || !line.Contains("\"type\":\"document\"", StringComparison.Ordinal)) return line;
            var parsed = (JsonObjectValue)JsonValue.Parse(line);
            if (parsed["type"] is not JsonStringValue { Value: "record" } || parsed["payload"] is not JsonObjectValue payload ||
                payload["type"] is not JsonStringValue { Value: "document" } || !conversations.ContainsKey(Long(payload["id"])))
                return line;
            var content = Content(Long(payload["id"]), Long(parsed["seq"]), (JsonObjectValue)payload["content"]);
            return Printed(line, parsed, parsed.SetItem("payload", payload.SetItem("content", content)));
        }

        /// <summary>The SQLite rows of one <see cref="CompatTests"/> column query: a pi.live revision (document_id, seq, kind, version,
        /// content) at version 3 (see <see cref="Content"/>).</summary>
        public List<string[]> Rows(string sql, List<string[]> rows)
        {
            if (!sql.Contains("FROM document_revisions", StringComparison.Ordinal)) return rows;
            return [.. rows.Select(row =>
            {
                var document = long.Parse(row[0], CultureInfo.InvariantCulture);
                if (!conversations.ContainsKey(document)) return row;
                var kind = row[2];
                var body = kind == "base" ? "value" : "ops";
                var content = Content(document, long.Parse(row[1], CultureInfo.InvariantCulture), JsonValue.Object(
                    KeyValuePair.Create("version", (JsonValue)long.Parse(row[3], CultureInfo.InvariantCulture)), KeyValuePair.Create("kind", (JsonValue)kind),
                    KeyValuePair.Create(body, JsonValue.Parse(row[4]))));
                return new[] { row[0], row[1], kind, Version.ToString(CultureInfo.InvariantCulture), content[body].ToJson() };
            })];
        }

        /// <summary>A captured revision of pi.live document <paramref name="document"/> at <paramref name="seq"/> as v1.5.0 stores it: at
        /// version 3, a base's value with the trigger and the run's turns, a delta's ops with them, and one more op on run.turns when its
        /// commit starts a tool round.</summary>
        private JsonObjectValue Content(long document, long seq, JsonObjectValue content)
        {
            if (!turns.TryGetValue((document, seq), out var count))
                throw new InvalidOperationException($"pi.live document {document} has no captured revision {seq} (live v3)");
            var kind = ((JsonStringValue)content["kind"]).Value;
            var authored = content.SetItem("version", Version);
            if (kind == "base") return authored.SetItem("value", Value((JsonObjectValue)content["value"], count));
            var ops = ((JsonArrayValue)content["ops"]).Select(item =>
            {
                var op = (JsonArrayValue)item;
                return LiveOp(op, 0, Relative(op, []) ?? [], count);
            }).ToList();
            if (rounds.Contains((document, seq))) ops.Add(JsonValue.Array("s", JsonValue.Array("run", "turns"), count ?? throw new InvalidOperationException("a tool round without a run (live v3)")));
            return authored.SetItem("ops", JsonValue.Array(ops));
        }
    }

    /// <summary>The plan of a captured scenario directory (its jsonl/main.jsonl and the pi.live documents' jsonl/doc-*.jsonl).</summary>
    public static Plan For(string scenario)
    {
        var directory = Path.Combine(scenario, "jsonl");
        var conversations = new Dictionary<long, long>();
        // Each submission's first placement, and each conversation's commits that start a tool round.
        var placed = new Dictionary<long, long>();
        var roundCommits = new Dictionary<long, List<long>>();
        foreach (var line in File.ReadAllText(Path.Combine(directory, "main.jsonl")).Split('\n').Where(line => line.Length > 0))
        {
            var commit = (JsonObjectValue)JsonValue.Parse(line);
            var seq = Long(commit["seq"]);
            foreach (var write in ((JsonArrayValue)commit["writes"]).Cast<JsonObjectValue>())
            {
                switch (((JsonStringValue)write["type"]).Value)
                {
                    case "document.create" when write["record"] is JsonObjectValue record && record["kind"] is JsonStringValue { Value: "pi.live" }:
                        conversations[Long(record["id"])] = Long(((JsonObjectValue)record["scope"])["conversationId"]);
                        break;
                    case "submission" when write["value"] is JsonObjectValue submission && submission["status"] is JsonStringValue { Value: "placed" }:
                        placed.TryAdd(Long(submission["id"]), seq);
                        break;
                    case "entry" when write["value"] is JsonObjectValue entry && StartsRound(entry):
                        var list = roundCommits.TryGetValue(Long(entry["conversationId"]), out var found) ? found : roundCommits[Long(entry["conversationId"])] = [];
                        if (list.Count == 0 || list[^1] != seq) list.Add(seq);
                        break;
                }
            }
        }
        long TurnsAt(long conversation, JsonObjectValue run, long seq)
        {
            if (run["inputs"] is not JsonArrayValue { Count: > 0 } inputs)
                throw new InvalidOperationException($"conversation {conversation}'s run has no input to count its turns from (live v3)");
            var start = placed.TryGetValue(Long(inputs[0]), out var at) ? at
                : throw new InvalidOperationException($"submission {inputs[0].ToJson()} was never placed (live v3)");
            return roundCommits.TryGetValue(conversation, out var seqs) ? seqs.Count(round => round >= start && round <= seq) : 0;
        }
        var turns = new Dictionary<(long, long), long?>();
        var rounds = new HashSet<(long, long)>();
        var last = new Dictionary<long, long>();
        foreach (var (document, conversation) in conversations)
        {
            var file = Path.Combine(directory, $"doc-{document.ToString(CultureInfo.InvariantCulture)}.jsonl");
            JsonValue? value = null;
            foreach (var line in File.ReadAllText(file).Split('\n').Where(line => line.Length > 0))
            {
                var record = (JsonObjectValue)JsonValue.Parse(line);
                var seq = Long(record["seq"]);
                var content = (JsonObjectValue)((JsonObjectValue)record["payload"])["content"];
                var isBase = content["kind"] is JsonStringValue { Value: "base" };
                value = isBase ? content["value"]
                    : DeltaApply.Apply(value ?? throw new InvalidOperationException($"{file} starts with a delta"), ((JsonArrayValue)content["ops"]).Select(Op.FromJson));
                turns[(document, seq)] = ((JsonObjectValue)value).TryGetValue("run", out var run) && run is JsonObjectValue fields
                    ? TurnsAt(conversation, fields, seq) : null;
                if (!isBase && roundCommits.TryGetValue(conversation, out var seqs) && seqs.Contains(seq)) rounds.Add((document, seq));
                last[document] = seq;
            }
        }
        return new Plan(conversations, turns, rounds, last);
    }

    /// <summary>Whether a captured entry is the assistant message a tool round starts with: it stops for tool use with a tool call
    /// (generation.ts classify, startToolRound).</summary>
    private static bool StartsRound(JsonObjectValue entry) =>
        entry["kind"] is JsonStringValue { Value: "pi.assistant" } && entry["model"] is JsonArrayValue { Count: > 0 } model &&
        model[0] is JsonObjectValue message && message.TryGetValue("stopReason", out var stop) && stop is JsonStringValue { Value: "toolUse" } &&
        message["content"] is JsonArrayValue parts && parts.Any(part => part is JsonObjectValue fields && fields["type"] is JsonStringValue { Value: "toolCall" });

    /// <summary><paramref name="op"/>'s path after <paramref name="prefix"/>, as strings and numbers; null when it does not start with it.</summary>
    private static object[]? Relative(JsonArrayValue op, string[] prefix)
    {
        if (op[0] is JsonStringValue { Value: "r" }) return prefix.Length == 0 ? [] : null;
        var path = (JsonArrayValue)op[1];
        if (path.Count < prefix.Length) return null;
        for (var index = 0; index < prefix.Length; index++)
            if (path[index] is not JsonStringValue { Value: var segment } || segment != prefix[index]) return null;
        return [.. path.Skip(prefix.Length).Select(segment => segment is JsonStringValue text ? (object)text.Value : (long)((JsonNumberValue)segment).Value)];
    }

    /// <summary>One pi.live op (its path <paramref name="path"/> after <paramref name="prefix"/> segments) as v1.5.0 writes it: a run's
    /// set with <paramref name="turns"/>, compaction statuses and compactionFailure with their trigger, and a whole value authored.</summary>
    private static JsonValue LiveOp(JsonArrayValue op, int prefix, object[] path, long? turns)
    {
        var kind = ((JsonStringValue)op[0]).Value;
        JsonArrayValue With(int index, JsonValue value) => JsonValue.Array(op.Select((item, at) => at == index ? value : item));
        JsonArrayValue Path(int index, string segment) => With(1, JsonValue.Array(((JsonArrayValue)op[1]).Select((item, at) => at == prefix + index ? segment : item)));
        switch (kind, path)
        {
            case ("r", _):
                return With(1, Value((JsonObjectValue)op[1], turns));
            case ("s", []):
                return With(2, Value((JsonObjectValue)op[2], turns));
            case ("s", ["run"]):
                return With(2, WithTurns((JsonObjectValue)op[2], turns ?? 0));
            case ("s", ["compactions"]):
                return With(2, Renamed(op[2]));
            case ("s", ["compactions", long]):
                return With(2, Renamed(op[2]));
            case ("p", ["compactions"]):
                return With(4, Renamed(op[4]));
            case (_, ["compactions", long, "reason"]):
                return Path(2, "trigger");
            case ("s", ["compactionFailure"]):
                return op[2] is JsonObjectValue failure ? With(2, Renamed(failure)) : op;
            case (_, ["compactionFailure", "reason"]):
                return Path(1, "trigger");
            default:
                return op;
        }
    }

    private static JsonObjectValue WithTurns(JsonObjectValue run, long turns) => run.SetItem("turns", turns);

    /// <summary>A compaction status or failure with reason renamed trigger in its place (live.ts renameReason); an array of them.</summary>
    private static JsonValue Renamed(JsonValue value) => value switch
    {
        JsonArrayValue items => JsonValue.Array(items.Select(item => item is JsonObjectValue status ? Renamed(status) : item)),
        JsonObjectValue status => Renamed(status),
        _ => value,
    };

    private static JsonObjectValue Renamed(JsonObjectValue status) => !status.ContainsKey("reason") ? status
        : JsonObjectValue.From(status.Select(field => KeyValuePair.Create(field.Key == "reason" ? "trigger" : field.Key, field.Value)));

    /// <summary>A records dump's pi.live documents ({record, version, value}).</summary>
    private static IEnumerable<JsonObjectValue> LiveDocuments(JsonValue records) =>
        ((JsonArrayValue)((JsonObjectValue)records)["documents"]).Cast<JsonObjectValue>().Where(IsLive);

    private static bool IsLive(JsonObjectValue document) =>
        document["record"] is JsonObjectValue record && record["kind"] is JsonStringValue { Value: "pi.live" };

    private static JsonValue MapDocuments(JsonValue records, Func<JsonObjectValue, JsonObjectValue> map)
    {
        var properties = (JsonObjectValue)records;
        return properties.SetItem("documents", JsonValue.Array(((JsonArrayValue)properties["documents"]).Cast<JsonObjectValue>()
            .Select(document => (JsonValue)(IsLive(document) ? map(document) : document))));
    }

    private static JsonObjectValue Stored(JsonObjectValue document, JsonObjectValue value) => document.SetItem("version", Version).SetItem("value", value);

    /// <summary>The authored text of a captured JSON line: as captured when nothing changes, else the authored value printed, once the
    /// captured line printed back as captured.</summary>
    private static string Printed(string line, JsonValue parsed, JsonValue authored)
    {
        var text = authored.ToJson();
        if (text == parsed.ToJson()) return line;
        if (parsed.ToJson() != line) throw new InvalidOperationException($"a captured JSON line does not print back as captured: {line[..Math.Min(200, line.Length)]}");
        return text;
    }

    private static long Long(JsonValue value) => (long)((JsonNumberValue)value).Value;
}
