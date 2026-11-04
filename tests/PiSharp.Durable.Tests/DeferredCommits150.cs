// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/src/session/session.ts (#hold, #storeHolds, close),
// packages/durable/src/session/transaction.ts (takeHeld, storeHeld) and packages/durable/src/harness/live.ts (progressCommit).
// Authored Pi v1.5.0 expectations over the Pi v1.2.0 captures (decision 0013: fixtures are not recaptured; area deferred-commits, 6c2fd73b0 and
// e5c6182ba). Nothing here is a capture, and a byte comparison with records an upstream v1.5.0 run wrote needs a recapture. v1.5.0 defers the
// commits that only show progress in pi.live (durable@1.5.0 Breaking Changes#10, Added#2, Changed#5): a generation's attempt and partials, a
// tool's output and details, and the running status of a tool's slot (with a nested call's final arguments), which v1.2.0 committed with the
// tool's intent and v1.5.0 commits apart from it. A held commit is adopted in memory and stored with the next commit that reads or writes the
// document (its changes first, folded with the commit's own by the pending-ops queue; a base the commit writes covers them), or, at the latest,
// when the session closes (every held document in one commit, in the order they were first held). Its seq is never used, so the commits after
// it move down. The captured scenarios' progress window (1 s) never ends before a commit stores the held changes or the session closes, so no
// timer store happens. Read off a capture's main.jsonl, as a plan both backends' forms follow:
// - a commit whose only write changes a pi.live document is held;
// - a pi.tool task's intent, its sidecar and one pi.live change and nothing else, keeps the sidecar; the change (the slot's status) is held;
// - the next commit that changes the held document stores the held changes with its own; one that only reads it, a commit with a submission
//   of the document's conversation (the inbox reads the run), stores them as a change of its own after its writes;
// - what is held at the end is stored at close, the last commit.
// Every other commit and write is kept, in order, with new seqs and ordinals. A document's records start at its last base, as the captures
// keep them (both backends drop what a base covers), so a held change before that base is not in any record. A capture that this plan does
// not cover (a held base, an overflowed queue, a resume that creates documents) throws rather than guess.
using System.Collections.Immutable;
using System.Globalization;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;

/// <summary>The authored v1.5.0 forms of v1.2.0 captures for deferred commits (see the file header).</summary>
internal static class DeferredCommits150
{
    /// <summary>One write of a v1.5.0 commit: a captured write kept, or a change that stores held changes.</summary>
    internal sealed class Write
    {
        /// <summary>The captured write without its ordinal, or the change written for held changes.</summary>
        public required JsonObjectValue Value { get; init; }
        /// <summary>Whether the write has a record (ordinal) in a sidecar file or a revision.</summary>
        public required bool HasRecord { get; init; }
        /// <summary>A kept write's position in its captured commit.</summary>
        public int? Index { get; init; }
        /// <summary>The captured commit and ordinal of a kept write's record.</summary>
        public long? Seq { get; init; }
        public long? Ordinal { get; init; }
        /// <summary>The document a change or a creation writes.</summary>
        public long? Document { get; init; }
        /// <summary>The captured seqs of the held changes this write stores, in order.</summary>
        public List<long> Held { get; } = [];
        /// <summary>The new ordinal.</summary>
        public long NewOrdinal { get; set; }
    }

    /// <summary>One v1.5.0 commit.</summary>
    internal sealed class Commit
    {
        public required long Seq { get; init; }
        /// <summary>The captured commit it keeps; <see cref="long.MaxValue"/> for the close.</summary>
        public required long Position { get; init; }
        public required JsonObjectValue Line { get; init; }
        public List<Write> Writes { get; } = [];
    }

    /// <summary>How a captured session's commits become v1.5.0's, read off its main.jsonl (see the file header).</summary>
    internal sealed class Plan
    {
        private readonly List<Commit> commits;
        private readonly Dictionary<long, long> seqs;
        private readonly long captured;

        internal Plan(List<Commit> commits, Dictionary<long, long> seqs, long captured)
        {
            this.commits = commits;
            this.seqs = seqs;
            this.captured = captured;
        }

        /// <summary>Whether the plan changes anything.</summary>
        public bool Changes => commits.Count != captured || commits.Any(commit => commit.Writes.Any(write => write.Held.Count > 0));

        /// <summary>The v1.5.0 seq of a kept captured commit.</summary>
        public long Seq(long seq) => seqs.TryGetValue(seq, out var mapped) ? mapped
            : throw new InvalidOperationException($"captured commit {seq} is not kept (deferred commits)");

        /// <summary>The records a Dump reads: documents' createdAt and retiredAt seqs move with their commits.</summary>
        public JsonValue Records(JsonValue records)
        {
            if (!Changes) return records;
            var properties = (JsonObjectValue)records;
            var documents = JsonValue.Array(((JsonArrayValue)properties["documents"]).Select(item =>
            {
                var document = (JsonObjectValue)item;
                return (JsonValue)document.SetItem("record", RecordSeqs((JsonObjectValue)document["record"]));
            }));
            return properties.SetItem("documents", documents);
        }

        private JsonObjectValue RecordSeqs(JsonObjectValue record)
        {
            foreach (var key in new[] { "createdAt", "retiredAt" })
                if (record.TryGetValue(key, out var seq) && seq is JsonNumberValue number) record = record.SetItem(key, Seq(Long(number)));
            return record;
        }

        /// <summary>The JSONL files: main.jsonl's commits, and every sidecar record with its new seq and ordinal and the held changes stored.
        /// <paramref name="files"/> maps file names to their (already authored) lines, the empty last one included.</summary>
        public Dictionary<string, string[]> Jsonl(Dictionary<string, string[]> files)
        {
            if (!Changes) return files;
            var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
            var records = new Dictionary<string, Dictionary<(long Seq, long Ordinal), JsonObjectValue>>(StringComparer.Ordinal);
            foreach (var (name, lines) in files)
            {
                if (name == "main.jsonl") continue;
                var byKey = new Dictionary<(long, long), JsonObjectValue>();
                foreach (var line in lines.Where(line => line.Length > 0).Select(line => (JsonObjectValue)JsonValue.Parse(line)))
                    byKey[(Long(line["seq"]), Long(line["ordinal"]))] = line;
                records[name] = byKey;
            }
            // The captured commits, as authored.
            var authored = files["main.jsonl"].Where(line => line.Length > 0).Select(line => (JsonObjectValue)JsonValue.Parse(line))
                .ToDictionary(line => Long(line["seq"]));
            JsonObjectValue Value(Commit commit, Write write) => write.Index is { } index
                ? ((JsonObjectValue)((JsonArrayValue)authored[commit.Position]["writes"])[index]).Remove("ordinal") : write.Value;
            var main = new List<string>();
            var sidecars = files.Keys.Where(name => name != "main.jsonl").ToDictionary(name => name, _ => new List<string>(), StringComparer.Ordinal);
            foreach (var commit in commits)
            {
                main.Add(commit.Line.SetItem("seq", commit.Seq).SetItem("writes", JsonValue.Array(commit.Writes.Select(write =>
                    (JsonValue)(write.HasRecord ? Value(commit, write).SetItem("ordinal", write.NewOrdinal) : Value(commit, write))))).ToJson());
                foreach (var write in commit.Writes.Where(write => write.HasRecord))
                {
                    var name = SidecarFile(write.Value);
                    // A task's sidecar file goes when the task ends.
                    if (!records.TryGetValue(name, out var lines)) continue;
                    var first = lines.Count == 0 ? long.MaxValue : lines.Keys.Min(key => key.Seq);
                    if (commit.Position < first) continue;
                    JsonObjectValue line;
                    if (write.Seq is { } seq)
                    {
                        if (!lines.TryGetValue((seq, write.Ordinal!.Value), out var captured))
                            throw new InvalidOperationException($"{name} holds no record of captured commit {seq}");
                        line = captured;
                        if (write.Held.Count > 0)
                        {
                            var payload = (JsonObjectValue)line["payload"];
                            line = line.SetItem("payload", payload.SetItem("content", Stored(write, (JsonObjectValue)payload["content"], held => Content(lines, held, name))));
                        }
                    }
                    else
                    {
                        var content = Stored(write, own: null, held => Content(lines, held, name));
                        line = JsonValue.Object(
                            KeyValuePair.Create("format", (JsonValue)1),
                            KeyValuePair.Create("type", (JsonValue)"record"),
                            KeyValuePair.Create("seq", (JsonValue)0),
                            KeyValuePair.Create("ordinal", (JsonValue)0),
                            KeyValuePair.Create("payload", (JsonValue)JsonValue.Object(
                                KeyValuePair.Create("type", (JsonValue)"document"),
                                KeyValuePair.Create("id", (JsonValue)write.Document!.Value),
                                KeyValuePair.Create("content", (JsonValue)content))));
                    }
                    sidecars[name].Add(line.SetItem("seq", commit.Seq).SetItem("ordinal", write.NewOrdinal).ToJson());
                }
            }
            result["main.jsonl"] = [.. main, ""];
            foreach (var (name, lines) in sidecars) result[name] = [.. lines, ""];
            return result;
        }

        private static JsonObjectValue Content(Dictionary<(long Seq, long Ordinal), JsonObjectValue> lines, long seq, string name)
        {
            var found = lines.Where(entry => entry.Key.Seq == seq).Select(entry => entry.Value).ToList();
            if (found.Count != 1) throw new InvalidOperationException($"{name} holds no record of held commit {seq}");
            return (JsonObjectValue)((JsonObjectValue)found[0]["payload"])["content"];
        }

        /// <summary>The SQLite rows of one <see cref="CompatTests"/> column query, as the plan moves them.</summary>
        public List<string[]> Rows(string sql, List<string[]> rows)
        {
            if (!Changes) return rows;
            if (sql.Contains("FROM durable_metadata", StringComparison.Ordinal))
                return [.. rows.Select(row => new[] { row[0], (commits.Count + 1).ToString(CultureInfo.InvariantCulture) })];
            if (sql.Contains("FROM entries", StringComparison.Ordinal))
                return [.. rows.Select(row => row.Select((cell, index) => index == 3 ? Text(Seq(long.Parse(cell, CultureInfo.InvariantCulture))) : cell).ToArray())];
            if (sql.Contains("FROM documents", StringComparison.Ordinal))
                return [.. rows.Select(row => row.Select((cell, index) => index is 6 or 7 && cell != "null" ? Text(Seq(long.Parse(cell, CultureInfo.InvariantCulture)))
                    : index == 8 ? RecordSeqs((JsonObjectValue)JsonValue.Parse(cell)).ToJson() : cell).ToArray())];
            if (sql.Contains("FROM document_revisions", StringComparison.Ordinal)) return Revisions(rows);
            return rows;
        }

        // document_id, seq, kind, version, content: a base stores its value, a delta its operations.
        private List<string[]> Revisions(List<string[]> rows)
        {
            var byDocument = rows.GroupBy(row => long.Parse(row[0], CultureInfo.InvariantCulture))
                .ToDictionary(group => group.Key, group => group.ToDictionary(row => long.Parse(row[1], CultureInfo.InvariantCulture)));
            JsonObjectValue ContentOf(string[] row) => JsonValue.Object(
                KeyValuePair.Create("version", (JsonValue)long.Parse(row[3], CultureInfo.InvariantCulture)),
                KeyValuePair.Create("kind", (JsonValue)row[2]),
                KeyValuePair.Create(row[2] == "base" ? "value" : "ops", JsonValue.Parse(row[4])));
            var result = new List<string[]>();
            foreach (var commit in commits)
                foreach (var write in commit.Writes.Where(write => write.Document is not null && write.HasRecord))
                {
                    var document = write.Document!.Value;
                    if (!byDocument.TryGetValue(document, out var revisions)) continue;
                    if (commit.Position < revisions.Keys.Min()) continue;
                    JsonObjectValue Held(long seq) => revisions.TryGetValue(seq, out var row) ? ContentOf(row)
                        : throw new InvalidOperationException($"document {document} has no revision of held commit {seq}");
                    JsonObjectValue content;
                    if (write.Seq is { } seq)
                    {
                        if (!revisions.TryGetValue(seq, out var row)) throw new InvalidOperationException($"document {document} has no revision {seq}");
                        content = write.Held.Count > 0 ? Stored(write, ContentOf(row), Held) : ContentOf(row);
                    }
                    else content = Stored(write, own: null, Held);
                    var kind = S(content["kind"]);
                    result.Add([Text(document), Text(commit.Seq), kind, Text(Long(content["version"])), content[kind == "base" ? "value" : "ops"].ToJson()]);
                }
            return [.. result.OrderBy(row => long.Parse(row[0], CultureInfo.InvariantCulture)).ThenBy(row => long.Parse(row[1], CultureInfo.InvariantCulture))];
        }

        /// <summary>What a write that stores held changes stores (transaction.ts storeHeld): its own base, which covers them, or the held
        /// changes' operations and its own, folded.</summary>
        private static JsonObjectValue Stored(Write write, JsonObjectValue? own, Func<long, JsonObjectValue> held)
        {
            if (own is not null && S(own["kind"]) == "base") return own;
            var pending = new PendingOps();
            long? version = null;
            foreach (var seq in write.Held)
            {
                var content = held(seq);
                if (S(content["kind"]) != "delta") throw new InvalidOperationException($"held commit {seq} selected a base (deferred commits)");
                version = Long(content["version"]);
                pending.Push(Ops(content));
            }
            if (own is not null)
            {
                version = Long(own["version"]);
                pending.Push(Ops(own));
            }
            var taken = pending.Take();
            if (taken.Kind == PendingTakeKind.Replace) throw new InvalidOperationException("the held changes overflowed (deferred commits)");
            return JsonValue.Object(
                KeyValuePair.Create("version", (JsonValue)version!.Value),
                KeyValuePair.Create("kind", (JsonValue)"delta"),
                KeyValuePair.Create("ops", (JsonValue)JsonValue.Array(taken.Ops.Select(op => (JsonValue)op.ToJson()))));
        }

        private static ImmutableArray<Op> Ops(JsonObjectValue content) => [.. ((JsonArrayValue)content["ops"]).Select(Op.FromJson)];
    }

    private static string SidecarFile(JsonObjectValue write) => S(write["type"]) switch
    {
        "task.sidecar" => $"task-{Long(write["id"])}.jsonl",
        "document.create" => $"doc-{Long(((JsonObjectValue)write["record"])["id"])}.jsonl",
        _ => $"doc-{Long(write["id"])}.jsonl",
    };

    /// <summary>The plan of a captured scenario directory (its jsonl/main.jsonl and records.json).</summary>
    public static Plan For(string scenario)
    {
        var lines = File.ReadAllText(Path.Combine(scenario, "jsonl", "main.jsonl")).Split('\n').Where(line => line.Length > 0)
            .Select(line => (JsonObjectValue)JsonValue.Parse(line)).ToList();
        var records = (JsonObjectValue)((JsonObjectValue)JsonValue.Parse(File.ReadAllText(Path.Combine(scenario, "records.json"))))["jsonl"];
        var tools = ((JsonArrayValue)records["tasks"]).Cast<JsonObjectValue>().Where(task => S(task["kind"]) == "pi.tool")
            .Select(task => Long(task["id"])).ToHashSet();
        // pi.live documents and their conversations.
        var live = new Dictionary<long, long>();
        // Held documents, in the order they were first held, and the captured commits of their held changes.
        var holds = new List<(long Document, List<long> Seqs)>();
        List<long> Hold(long document)
        {
            var index = holds.FindIndex(hold => hold.Document == document);
            if (index >= 0) return holds[index].Seqs;
            holds.Add((document, []));
            return holds[^1].Seqs;
        }
        var commits = new List<Commit>();
        var seqs = new Dictionary<long, long>();
        bool IsLiveChange(JsonObjectValue write) => S(write["type"]) == "document.change" && live.ContainsKey(Long(write["id"]));
        foreach (var line in lines)
        {
            var seq = Long(line["seq"]);
            var writes = ((JsonArrayValue)line["writes"]).Cast<JsonObjectValue>().ToList();
            foreach (var write in writes.Where(write => S(write["type"]) == "document.create"))
            {
                var record = (JsonObjectValue)write["record"];
                if (S(record["kind"]) == "pi.live") live[Long(record["id"])] = Long(((JsonObjectValue)record["scope"])["conversationId"]);
            }
            // A commit that only shows progress in pi.live is held.
            if (writes.Count == 1 && IsLiveChange(writes[0]))
            {
                Hold(Long(writes[0]["id"])).Add(seq);
                continue;
            }
            // A pi.tool intent: the slot's change is a deferred commit of its own.
            if (writes.Count == 2 && writes.Count(write => S(write["type"]) == "task.sidecar" && tools.Contains(Long(write["id"]))) == 1 &&
                writes.Count(IsLiveChange) == 1)
            {
                var slot = writes.Single(IsLiveChange);
                Hold(Long(slot["id"])).Add(seq);
                writes.Remove(slot);
            }
            var commit = new Commit { Seq = commits.Count + 1, Position = seq, Line = line };
            var captured = (JsonArrayValue)line["writes"];
            foreach (var write in writes)
                commit.Writes.Add(new Write
                {
                    Index = Enumerable.Range(0, captured.Count).Single(index => ReferenceEquals(captured[index], write)),
                    Value = write.Remove("ordinal"),
                    HasRecord = write.ContainsKey("ordinal"),
                    Seq = write.ContainsKey("ordinal") ? seq : null,
                    Ordinal = write.TryGetValue("ordinal", out var ordinal) ? Long(ordinal) : null,
                    Document = S(write["type"]) switch
                    {
                        "document.change" => Long(write["id"]),
                        "document.create" => Long(((JsonObjectValue)write["record"])["id"]),
                        _ => null,
                    },
                });
            // The next commit that writes or reads a held document stores its held changes.
            var submissions = writes.Where(write => S(write["type"]) == "submission")
                .Select(write => Long(((JsonObjectValue)write["value"])["conversationId"])).ToHashSet();
            foreach (var (document, held) in holds.ToList())
            {
                var own = commit.Writes.FirstOrDefault(write => S(write.Value["type"]) == "document.change" && write.Document == document);
                if (own is null && submissions.Contains(live[document]))
                {
                    own = new Write { Value = Change(document), HasRecord = true, Document = document };
                    commit.Writes.Add(own);
                }
                if (own is null) continue;
                own.Held.AddRange(held);
                holds.RemoveAll(hold => hold.Document == document);
            }
            Number(commit);
            seqs[seq] = commit.Seq;
            commits.Add(commit);
        }
        if (holds.Count > 0)
        {
            var close = new Commit { Seq = commits.Count + 1, Position = long.MaxValue, Line = lines[^1] };
            foreach (var (document, held) in holds)
            {
                var write = new Write { Value = Change(document), HasRecord = true, Document = document };
                write.Held.AddRange(held);
                close.Writes.Add(write);
            }
            Number(close);
            commits.Add(close);
        }
        return new Plan(commits, seqs, lines.Count);
    }

    private static JsonObjectValue Change(long document) => JsonValue.Object(
        KeyValuePair.Create("type", (JsonValue)"document.change"), KeyValuePair.Create("id", (JsonValue)document));

    private static void Number(Commit commit)
    {
        var ordinal = 0;
        foreach (var write in commit.Writes.Where(write => write.HasRecord)) write.NewOrdinal = ordinal++;
    }

    private static long Long(JsonValue value) => (long)((JsonNumberValue)value).Value;

    private static string S(JsonValue value) => ((JsonStringValue)value).Value;

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);
}
