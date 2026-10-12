using JsonArray = System.Text.Json.Nodes.JsonArray;
using JsonNode = System.Text.Json.Nodes.JsonNode;
using JsonObject = System.Text.Json.Nodes.JsonObject;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;

// The draft invariant (decision 0005): for every Prepared, DeltaApply.Apply(Base, Ops) deeply equals Value; Value equals an independent
// mutable model of the same edits; key order of Value matches both the draft view and replay; a no-op keeps Base's identity; untouched
// subtrees keep their references; the wire codec round-trips every batch; and DeltaDiff replays between the same revisions, shared or
// reconstructed. Deterministic: System.Random with a fixed seed.
internal static partial class Program
{
    private const int FuzzSeed = 20_261_012;
    private static readonly string[] FuzzKeys = ["a", "b", "c", "d", "e", "f", "0", "1", "12", "__proto__"];

    private static IEnumerable<(string, Func<Task>)> ChordFuzzCases() =>
    [
        Case("chord.fuzz.tracker-ops-replay-to-value", () => TrackerFuzz(FuzzSeed, documents: 100, transactions: 60)),
        Case("chord.fuzz.diff-replays-between-tracked-and-reconstructed-revisions", () =>
        {
            var random = new Random(FuzzSeed + 1);
            for (var document = 0; document < 40; document++)
            {
                var tracker = Tracker.Track(RandomObject(random, 3));
                for (var transaction = 0; transaction < 30; transaction++)
                {
                    var change = tracker.BeginChange();
                    var model = JsonNode.Parse(tracker.Value.ToJson())!;
                    for (var edit = random.Next(1, 9); edit > 0; edit--) FuzzEdit(random, change.State, model, []);
                    var prepared = change.Prepare();
                    var where = $"document {document} transaction {transaction}";
                    foreach (var (before, after) in new[] { (prepared.Base, prepared.Value), (J(prepared.Base.ToJson()), J(prepared.Value.ToJson())) })
                    {
                        var operations = DeltaDiff.DiffRevisions(before, after);
                        Check(JsonValue.DeepEquals(DeltaApply.Apply(before, operations), after), "diff replay " + where);
                        if (JsonValue.DeepEquals(before, after)) Equal(0, operations.Length, "equal revisions diff to nothing " + where);
                    }
                    tracker.Adopt(prepared);
                }
            }
        }),
    ];

    private static void TrackerFuzz(int seed, int documents, int transactions)
    {
        var random = new Random(seed);
        var checkedBatches = 0;
        var nonEmpty = 0;
        var verbs = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var document = 0; document < documents; document++)
        {
            var tracker = Tracker.Track(RandomObject(random, 3));
            var encoder = new DeltaEncoder();
            var decoder = new DeltaDecoder();
            var replica = tracker.Value;
            for (var transaction = 0; transaction < transactions; transaction++)
            {
                var where = $"seed {seed} document {document} transaction {transaction}";
                var model = JsonNode.Parse(tracker.Value.ToJson())!;
                var change = tracker.BeginChange();
                var touched = new HashSet<string>(StringComparer.Ordinal);
                for (var edit = random.Next(1, 9); edit > 0; edit--) FuzzEdit(random, change.State, model, touched);
                var view = change.State.ToValue();
                var expected = J(model.ToJsonString());
                Check(JsonValue.DeepEquals(view, expected), "draft view equals model " + where);
                var prepared = change.Prepare();
                checkedBatches++;
                if (prepared.Ops.Length > 0) nonEmpty++;
                foreach (var op in prepared.Ops) verbs[op.Verb] = verbs.GetValueOrDefault(op.Verb) + 1;
                Check(JsonValue.DeepEquals(prepared.Value, expected), $"value equals model {where}: {prepared.Value.ToJson()} vs {expected.ToJson()}");
                Equal(view.ToJson(), prepared.Value.ToJson(), "value keeps the draft's key order " + where);
                var replayed = DeltaApply.Apply(prepared.Base, prepared.Ops);
                Check(JsonValue.DeepEquals(replayed, prepared.Value), $"apply(base, ops) equals value {where}: {OpsJson(prepared.Ops).ToJson()}");
                Equal(prepared.Value.ToJson(), replayed.ToJson(), "replay keeps key order " + where);
                if (prepared.Ops.IsEmpty) Check(ReferenceEquals(prepared.Value, prepared.Base), "a no-op keeps identity " + where);
                foreach (var (key, value) in (JsonObjectValue)prepared.Base)
                    if (!touched.Contains(key) && ((JsonObjectValue)prepared.Value).TryGetValue(key, out var kept))
                        Check(ReferenceEquals(value, kept), $"untouched subtree '{key}' keeps its reference {where}");
                foreach (var op in prepared.Ops)
                    if (op is PathOp pathOp) DeltaValidation.AssertSafePath(pathOp.Path);
                if (random.Next(10) == 0)
                {
                    prepared.Abort();
                    ThrowsMatching(() => tracker.Adopt(prepared), "aborted", "aborted " + where);
                    continue;
                }
                tracker.Adopt(prepared);
                replica = DeltaApply.Apply(replica, decoder.Decode(encoder.Encode(prepared.Ops)));
                Check(JsonValue.DeepEquals(replica, tracker.Value), "wire replica follows authority " + where);
            }
        }
        Check(nonEmpty > checkedBatches / 2, $"fuzz exercised real batches: {nonEmpty} of {checkedBatches}");
        var histogram = string.Join(", ", verbs.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));
        foreach (var verb in new[] { "r", "s", "d", "a", "t", "p", "m" }) Check(verbs.ContainsKey(verb), $"fuzz emitted no '{verb}': {histogram}");
        Console.Error.WriteLine($"chord.fuzz: {checkedBatches} batches, {nonEmpty} non-empty, verbs {histogram}");
    }

    private static JsonValue RandomScalar(Random random) => random.Next(6) switch
    {
        0 => JsonValue.Null,
        1 => random.Next(2) == 0,
        2 => random.Next(-5, 50),
        3 => random.Next(4) * 0.5,
        _ => "s" + random.Next(1000)
    };

    private static JsonValue RandomValue(Random random, int depth) => depth <= 0 || random.Next(3) > 0
        ? RandomScalar(random)
        : random.Next(2) == 0 ? RandomObject(random, depth - 1) : RandomArray(random, depth - 1);

    private static JsonValue RandomObject(Random random, int depth)
    {
        var builder = JsonObjectValue.CreateBuilder();
        for (var count = random.Next(1, 6); count > 0; count--) builder[FuzzKeys[random.Next(FuzzKeys.Length - 1)]] = RandomValue(random, depth);
        return builder.ToImmutable();
    }

    private static JsonValue RandomArray(Random random, int depth) =>
        JsonValue.Array(Enumerable.Range(0, random.Next(0, 6)).Select(_ => RandomValue(random, depth)));

    private static JsonNode? Node(JsonValue value) => JsonNode.Parse(value.ToJson());

    /// <summary>One random edit at a random container, applied to the draft and to the mutable model in lockstep.</summary>
    private static void FuzzEdit(Random random, Draft root, JsonNode modelRoot, HashSet<string> touched)
    {
        Draft draft = root;
        var model = modelRoot;
        // Descend through containers, recording the root key the edit lands under.
        for (var depth = 0; depth < 4 && random.Next(3) > 0; depth++)
        {
            if (draft is DraftObject obj)
            {
                var containers = obj.Keys.Where(key => obj.Get(key) is JsonObjectValue or JsonArrayValue).ToList();
                if (containers.Count == 0) break;
                var key = containers[random.Next(containers.Count)];
                if (depth == 0) touched.Add(key);
                draft = obj.GetDraft(key);
                model = model[key]!;
            }
            else
            {
                var array = (DraftArray)draft;
                var containers = Enumerable.Range(0, array.Count).Where(index => array[index] is JsonObjectValue or JsonArrayValue).ToList();
                if (containers.Count == 0) break;
                var index = containers[random.Next(containers.Count)];
                draft = array.GetDraft(index);
                model = model[index]!;
            }
        }
        if (draft is DraftObject target) FuzzObjectEdit(random, target, (JsonObject)model, ReferenceEquals(draft, root) ? touched : null);
        else FuzzArrayEdit(random, (DraftArray)draft, (JsonArray)model);
    }

    private static void FuzzObjectEdit(Random random, DraftObject draft, JsonObject model, HashSet<string>? touched)
    {
        var keys = draft.Keys;
        var existing = keys.Count == 0 ? null : keys[random.Next(keys.Count)];
        var key = random.Next(3) == 0 || existing is null ? FuzzKeys[random.Next(random.Next(20) == 0 ? FuzzKeys.Length : FuzzKeys.Length - 1)] : existing;
        void Put(string name, JsonValue value)
        {
            touched?.Add(name);
            draft.Set(name, value);
            model[name] = Node(value);
        }
        switch (random.Next(8))
        {
            case 0: Put(key, RandomScalar(random)); break;
            case 1: Put(key, RandomValue(random, 2)); break;
            case 2:
                if (existing is null) break;
                touched?.Add(existing);
                Check(draft.Delete(existing), "delete existing");
                model.Remove(existing);
                break;
            case 3:
                if (existing is not null && draft.Get(existing) is JsonStringValue text)
                {
                    touched?.Add(existing);
                    var suffix = "+" + random.Next(100);
                    draft.AppendString(existing, suffix);
                    model[existing] = text.Value + suffix;
                }
                else Put(key, "t" + random.Next(100));
                break;
            case 4:
                if (existing is not null && draft.Get(existing) is JsonStringValue rolling)
                    Put(existing, rolling.Value[random.Next(rolling.Value.Length + 1)..] + (random.Next(2) == 0 ? "" : "~" + random.Next(10)));
                break;
            case 5:
                if (existing is not null) Put(existing, J(draft.Get(existing)!.ToJson())); // deeply equal copy: normalizes away
                break;
            case 6:
                if (existing is not null && draft.Get(existing) is { } same && same is not (JsonObjectValue or JsonArrayValue)) Put(existing, same);
                break;
            default:
                if (existing is not null) { Put(key, RandomScalar(random)); Put(key, draft.Get(key)!); } // write then rewrite
                break;
        }
    }

    private static void FuzzArrayEdit(Random random, DraftArray draft, JsonArray model)
    {
        var count = draft.Count;
        void Replace(IEnumerable<JsonValue> values)
        {
            model.Clear();
            foreach (var value in values) model.Add(Node(value));
        }
        switch (random.Next(12))
        {
            case 0:
            {
                var value = RandomValue(random, 2);
                draft.Add(value);
                model.Add(Node(value));
                break;
            }
            case 1:
            {
                var index = random.Next(count + 1);
                var value = RandomValue(random, 2);
                draft.Insert(index, value);
                model.Insert(index, Node(value));
                break;
            }
            case 2:
                if (count == 0) break;
                var removeAt = random.Next(count);
                draft.RemoveAt(removeAt);
                model.RemoveAt(removeAt);
                break;
            case 3:
            {
                var start = random.Next(count + 1);
                var remove = random.Next(4);
                var items = Enumerable.Range(0, random.Next(3)).Select(_ => RandomValue(random, 1)).ToArray();
                draft.Splice(start, remove, items);
                for (var removed = Math.Min(remove, count - start); removed > 0; removed--) model.RemoveAt(start);
                for (var offset = 0; offset < items.Length; offset++) model.Insert(start + offset, Node(items[offset]));
                break;
            }
            case 4:
            {
                var index = random.Next(count + 1);
                var value = random.Next(2) == 0 ? RandomScalar(random) : RandomValue(random, 2);
                draft.Set(index, value);
                if (index == count) model.Add(Node(value));
                else model[index] = Node(value);
                break;
            }
            case 5:
                draft.Reverse();
                Replace(Enumerable.Range(0, count).Select(index => J(model[count - 1 - index]?.ToJsonString() ?? "null")).ToList());
                break;
            case 6:
            {
                var before = draft.ToValue();
                draft.Sort();
                Replace(((JsonArrayValue)before).OrderBy(DraftArray.JsString, StringComparer.Ordinal).ToList());
                break;
            }
            case 7:
            {
                var before = draft.ToValue();
                Comparison<JsonValue> byJson = (left, right) => string.CompareOrdinal(left.ToJson(), right.ToJson());
                draft.Sort(byJson);
                Replace(((JsonArrayValue)before).OrderBy(value => value, Comparer<JsonValue>.Create(byJson)).ToList());
                break;
            }
            case 8:
            {
                if (count == 0) break;
                int from = random.Next(count), to = random.Next(count);
                draft.Move(from, to);
                var node = J(model[from]?.ToJsonString() ?? "null");
                model.RemoveAt(from);
                model.Insert(to, Node(node));
                break;
            }
            case 9:
            {
                var length = random.Next(Math.Max(0, count - 2), count + 3);
                draft.SetLength(length);
                while (model.Count > length) model.RemoveAt(model.Count - 1);
                while (model.Count < length) model.Add(null);
                break;
            }
            case 10:
            {
                var strings = Enumerable.Range(0, count).Where(index => draft[index] is JsonStringValue).ToList();
                if (strings.Count == 0) break;
                var index = strings[random.Next(strings.Count)];
                var text = "+" + random.Next(100);
                draft.AppendString(index, text);
                model[index] = model[index]!.GetValue<string>() + text;
                break;
            }
            default:
                if (count > 0 && random.Next(4) == 0)
                {
                    draft.Clear();
                    model.Clear();
                }
                else if (count > 0)
                {
                    var index = random.Next(count);
                    draft.Set(index, J(draft[index].ToJson())); // deeply equal copy
                }
                break;
        }
    }
}
