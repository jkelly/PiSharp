using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/delta-append-text.test.ts. Upstream's appendText(draft, key,
// text) is DraftObject.AppendText(key, text), written with the explicit draft API of TrackerCases.cs (`draft.text += x` is a Set of the
// concatenation). Adaptations:
// - The compiler checks what upstream checks at run time for JavaScript callers: AppendText is a member of the object draft, so neither a
//   value that is not a draft ("needs a draft") nor an array draft (key "0", and StringKeys<T>'s array keys) can be passed. A caller
//   holding an untyped Draft gets the object draft from AsObject(), which refuses an array draft. StringKeys<T> itself, which refuses a
//   string literal type or an optional string at compile time, has no C# counterpart: the run-time check, a string at the key, is the one
//   kept, so "type" appends and an absent "signature" throws.
// - The retention case checks with WeakReference and GC.Collect that the ops do not keep the strings their text was cut from alive, in
//   place of used_heap_size under --expose-gc. A .NET string is never a view of a larger one, so an op holds the caller's text as is.
// - The cost case counts the thread's allocated bytes (GC.GetAllocatedBytesForCurrentThread) in place of GCProfiler. Pi v1.5.0 sync (area
//   partial-commits): a .NET string has no rope, so an appended JsonStringValue defers its concatenation (JsonStringValue.Concat) as V8's
//   string does, and the case holds upstream's bounds on everything the thread allocates. It used to subtract the characters each change's
//   new string copied (O(length) a change, about 6 GB for the 500 KB stream); renamed from
//   costs-o-chunk-per-change-beyond-the-copy-a-dotnet-string-makes. The two cases before it are PiSharp-authored checks of that deferred
//   string: built once on first read, each revision keeping its value, and a deep chain read without recursion from several threads.
internal static partial class Program
{
    private static JsonValue AppendTextDoc() =>
        J("{\"title\":\"t\",\"content\":[{\"type\":\"text\",\"text\":\"hello\",\"tags\":[\"a\"]}],\"meta\":{\"note\":\"n\"}}");

    private static DraftObject AppendTextBlock(DraftObject draft, int index = 0) => draft.GetArray("content").GetObject(index);

    /// <summary>Upstream's commit(): runs <paramref name="mutate"/> in one change, checks that replaying its ops gives the new value,
    /// adopts it and returns its ops.</summary>
    private static ImmutableArray<Op> AppendTextCommit(Tracker tracker, Action<DraftObject> mutate)
    {
        var change = tracker.BeginChange();
        mutate(Root(change));
        var prepared = change.Prepare();
        Replays(prepared);
        tracker.Adopt(prepared);
        return prepared.Ops;
    }

    private static void AppendTextOpsAre(string expected, ImmutableArray<Op> ops, string what) =>
        Check(JsonValue.DeepEquals(J(expected), OpsJson(ops)), $"{what}: expected {expected}, actual {OpsJson(ops).ToJson()}");

    private static void AppendTextHasOp(string expected, ImmutableArray<Op> ops) =>
        Check(ops.Any(op => JsonValue.DeepEquals(J(expected), op.ToJson())), $"ops {OpsJson(ops).ToJson()} lack {expected}");

    private static IEnumerable<(string, Func<Task>)> ChordDeltaAppendTextCases() =>
    [
        Case("chord.delta-append-text.records-an-append-op-and-the-appended-value", () =>
        {
            var tracker = Tracker.Track(AppendTextDoc());
            var before = tracker.Value;
            AppendTextOpsAre("[[\"a\",[\"content\",0,\"text\"],\" world\"]]",
                AppendTextCommit(tracker, draft => AppendTextBlock(draft).AppendText("text", " world")), "block text");
            Equal("hello world", Str(At(tracker.Value, "content", 0, "text")), "appended value");
            Equal("hello", Str(At(before, "content", 0, "text")), "previous revision");
            AppendTextOpsAre("[[\"a\",[\"title\"],\"!\"]]", AppendTextCommit(tracker, draft => draft.AppendText("title", "!")), "root key");
            AppendTextOpsAre("[[\"a\",[\"meta\",\"note\"],\"ote\"]]",
                AppendTextCommit(tracker, draft => draft.GetObject("meta").AppendText("note", "ote")), "nested key");
            Same("{\"title\":\"t!\",\"content\":[{\"type\":\"text\",\"text\":\"hello world\",\"tags\":[\"a\"]}],\"meta\":{\"note\":\"note\"}}",
                tracker.Value, "value");
        }),
        Case("chord.delta-append-text.joins-several-appends-to-one-key-in-one-change-into-one-op-next-to-other-changes", () =>
        {
            var tracker = Tracker.Track(AppendTextDoc());
            var ops = AppendTextCommit(tracker, draft =>
            {
                AppendTextBlock(draft).AppendText("text", " a");
                draft.Set("title", "new");
                AppendTextBlock(draft).AppendText("text", " b");
                AppendTextBlock(draft).AppendText("text", "");
                AppendTextBlock(draft).AppendText("text", " c");
            });
            AppendTextHasOp("[\"a\",[\"content\",0,\"text\"],\" a b c\"]", ops);
            AppendTextHasOp("[\"s\",[\"title\"],\"new\"]", ops);
            Equal(2, ops.Length, "ops");
            Equal("hello a b c", Str(At(tracker.Value, "content", 0, "text")), "value");
        }),
        Case("chord.delta-append-text.falls-back-to-the-string-comparison-when-the-same-key-is-also-assigned-or-deleted", () =>
        {
            var tracker = Tracker.Track(AppendTextDoc());
            AppendTextOpsAre("[[\"a\",[\"content\",0,\"text\"],\" x y\"]]", AppendTextCommit(tracker, draft =>
            {
                AppendTextBlock(draft).AppendText("text", " x");
                AppendTextBlock(draft).Set("text", Str(AppendTextBlock(draft)["text"]) + " y");
            }), "append then +=");
            AppendTextOpsAre("[[\"s\",[\"content\",0,\"text\"],\"replaced\"]]", AppendTextCommit(tracker, draft =>
            {
                AppendTextBlock(draft).AppendText("text", " z");
                AppendTextBlock(draft).Set("text", "replaced");
            }), "append then assignment");
            AppendTextOpsAre("[[\"s\",[\"title\"],\"assigned then appended\"]]", AppendTextCommit(tracker, draft =>
            {
                draft.Set("title", "assigned");
                draft.AppendText("title", " then appended");
            }), "assignment then append");
            AppendTextOpsAre("[[\"d\",[\"meta\",\"note\"]]]", AppendTextCommit(tracker, draft =>
            {
                draft.GetObject("meta").AppendText("note", "x");
                draft.GetObject("meta").Delete("note");
            }), "append then delete");
        }),
        Case("chord.delta-append-text.appends-inside-a-block-the-same-change-inserted-or-replaced", () =>
        {
            var tracker = Tracker.Track(AppendTextDoc());
            AppendTextCommit(tracker, draft =>
            {
                var content = draft.GetArray("content");
                content.Add(J("{\"type\":\"text\",\"text\":\"new\",\"tags\":[]}"));
                content.GetObject(1).AppendText("text", " block");
                content.Set(0, J("{\"type\":\"text\",\"text\":\"other\",\"tags\":[]}"));
                content.GetObject(0).AppendText("text", " one");
            });
            var texts = ((JsonArrayValue)At(tracker.Value, "content")).Select(block => Str(At(block, "text")));
            Equal("other one|new block", string.Join("|", texts), "texts");
        }),
        Case("chord.delta-append-text.rejects-what-a-javascript-caller-can-get-wrong", () =>
        {
            // A value that is not a draft cannot be passed in C# (see the header).
            var tracker = Tracker.Track(AppendTextDoc());
            var change = tracker.BeginChange();
            var state = Root(change);
            ThrowsMatching(() => state.AppendText("content", "x"), "AppendText() needs a string at \"content\"", "an array value");
            ThrowsMatching(() => state.AppendText("missing", "x"), "AppendText() needs a string at \"missing\"", "a missing key");
            // An array draft has no AppendText; held as a Draft, it is refused as an object draft.
            ThrowsMatching(() => state.GetDraft("content").AsObject().AppendText("0", "x"), "is an array", "an array draft");
            var block = AppendTextBlock(state);
            change.Prepare();
            ThrowsMatching(() => block.AppendText("text", "x"), "Cannot use a settled overlay", "a prepared change");
            var aborted = tracker.BeginChange();
            var held = Root(aborted);
            aborted.Abort();
            ThrowsMatching(() => held.AppendText("title", "x"), "Cannot use a settled overlay", "an aborted change");
        }),
        Case("chord.delta-append-text.accepts-only-keys-whose-type-is-exactly-string", () =>
        {
            // StringKeys<Block> is compile-time only (see the header); C# checks the value at the key when the call runs.
            var tracker = Tracker.Track(AppendTextDoc());
            var change = tracker.BeginChange();
            var block = AppendTextBlock(Root(change));
            block.AppendText("type", "x");
            Equal("textx", Str(block["type"]), "a string literal type in TypeScript is a string here");
            Throws<InvalidOperationException>(() => block.AppendText("signature", "x"), "an optional string that is absent");
            Throws<InvalidOperationException>(() => block.AppendText("tags", "x"), "an array");
            change.Abort();
        }),
        Case("chord.delta-append-text.keeps-an-op-text-that-does-not-hold-a-larger-string-it-was-cut-from-alive", () =>
        {
            var tracker = Tracker.Track(AppendTextDoc());
            var ops = new List<Op>();
            var sources = new List<WeakReference>();
            for (var index = 0; index < 50; index++) sources.Add(AppendTextFromLargerString(tracker, ops, index));
            CollectGarbage();
            Equal(0, sources.Count(source => source.IsAlive), "sources kept alive");
            Equal(50, ops.Count, "ops");
            Check(ops.All(op => op is AppendOp { Text.Length: 40 }), "each op holds the 40 characters appended");
            GC.KeepAlive(tracker);
        }),
        // PiSharp-authored: the deferred concatenation of an appended JsonStringValue (see the header).
        Case("chord.delta-append-text.builds-a-long-appended-string-once-and-keeps-each-revisions-value", () =>
        {
            var tracker = Tracker.Track(AppendTextDoc());
            var expected = new System.Text.StringBuilder("hello");
            JsonValue? middle = null;
            for (var index = 0; index < 2_000; index++)
            {
                var chunk = index.ToString("D5", CultureInfo.InvariantCulture);
                AppendTextCommit(tracker, draft => AppendTextBlock(draft).AppendText("text", chunk));
                expected.Append(chunk);
                if (index == 999) middle = tracker.Value;
            }
            var text = (JsonStringValue)At(tracker.Value, "content", 0, "text");
            Equal(expected.Length, text.Length, "the length, known before the string is built");
            var built = text.Value;
            Equal(expected.ToString(), built, "the string");
            Check(ReferenceEquals(built, text.Value), "built once: a second read returns the same string");
            // An earlier revision shares the appends before it and reads its own value, after the later one was built.
            Equal(expected.ToString(0, 5 + 1_000 * 5), Str(At(middle!, "content", 0, "text")), "the earlier revision's text");
            Check(JsonValue.DeepEquals(JsonValue.String(expected.ToString()), text), "equal to the flat string");
            Check(!JsonValue.DeepEquals(JsonValue.String(expected.ToString(0, expected.Length - 1) + "?"), text), "unequal to another string");
        }),
        Case("chord.delta-append-text.reads-a-deep-chain-of-deferred-appends-without-recursion-from-several-threads", () =>
        {
            // A chain far deeper than a recursive build's stack allows, read at once by several threads.
            var value = JsonValue.String(new string('a', 2_000));
            for (var index = 0; index < 200_000; index++) value = JsonStringValue.Concat(value, "b");
            var expected = new string('a', 2_000) + new string('b', 200_000);
            var reads = new string[8];
            Parallel.For(0, reads.Length, index => reads[index] = value.Value);
            Check(reads.All(read => read == expected), "every thread reads the whole string");
            Equal(expected.Length, value.Length, "the length");
            // Below 1024 characters an append concatenates at once.
            var small = JsonStringValue.Concat(JsonValue.String("ab"), "c");
            Equal("abc", small.Value, "a short append");
            Check(ReferenceEquals(value, JsonStringValue.Concat(value, "")), "an empty append is the string itself");
        }),
        Case("chord.delta-append-text.cost.costs-o-chunk-per-change-a-500-kb-stream-allocates-linearly", () =>
        {
            var small = AppendTextStream(50_000);
            var large = AppendTextStream(500_000);
            Equal("hello" + string.Concat(Enumerable.Repeat(AppendTextChunk, 500_000 / AppendTextChunk.Length)), AppendTextStreamed,
                "the streamed text");
            // Ten times the text: linear growth is about 10x.
            Check((double)large / small < 20, $"large {large} / small {small}");
            Check(large / 12_500d < 40_000, $"{large / 12_500d:F0} bytes per change");
        }),
    ];

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AppendTextFromLargerString(Tracker tracker, List<Op> ops, int index)
    {
        var source = string.Concat(Enumerable.Repeat(index.ToString(CultureInfo.InvariantCulture), 100_000));
        ops.AddRange(AppendTextCommit(tracker, draft => AppendTextBlock(draft).AppendText("text", source[..40])));
        return new WeakReference(source);
    }

    private const string AppendTextChunk = "0123456789abcdefghij0123456789abcdefghij";

    // The text the last AppendTextStream streamed, read after its measure.
    private static string AppendTextStreamed = "";

    /// <summary>One change per 40-character chunk, appended to one streamed text until it has <paramref name="length"/> characters: the
    /// bytes the thread allocates.</summary>
    private static long AppendTextStream(int length)
    {
        var tracker = Tracker.Track(AppendTextDoc());
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var streamed = 0; streamed < length; streamed += AppendTextChunk.Length)
        {
            var change = tracker.BeginChange();
            AppendTextBlock(Root(change)).AppendText("text", AppendTextChunk);
            tracker.Adopt(change.Prepare());
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        AppendTextStreamed = Str(At(tracker.Value, "content", 0, "text"));
        return allocated;
    }
}
