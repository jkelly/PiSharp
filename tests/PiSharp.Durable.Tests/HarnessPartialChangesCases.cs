// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/harness-partial-changes.test.ts (da412b039), ported in the Pi
// v1.5.0 sync (area partial-commits). #9265: partial commits reassigned the growing strings of a streamed answer, and Chord compared each one
// whole. Verbatim apart from these adaptations:
// - The response's events are PiSharp.Contracts frames (TextDelta, ToolCallStarted, ToolCallDelta, TextEnded, ToolCallEnded). The partial
//   message is its pi-ai wire JSON, built fresh for every commit as upstream's copyJson(partial) copies it, and the tool call's arguments in
//   it are what PiSharp.AI's ToolCallArgumentsStream parsed, as upstream's are pi-ai's.
// - commit(generation, message, taken) is Commit(generation, taken): the PartialCommit carries the message take() returned.
// - The snapshot fallback. Upstream commits pi-ai's partial arguments and stops tracking the call; PartialChanges commits a parse of the
//   call's text as take() found it and keeps tracking (see PartialChanges.cs), so the last commit of that case applies the changes after it.
//   The committed arguments are upstream's at every step.
// - Each prepared change is also replayed (TrackerCases.cs Replays) before it is adopted.
// The last five cases are PiSharp-authored: the frames pi-ai has no event for (ToolCallCheckpoint, ThinkingCheckpoint, ContentBlockFinalized,
// ToolCallProvisionalStarted), which PartialChanges maps as its header says.
using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Contracts;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;

#pragma warning disable PISHARP_AI001 // PiSharp.AI's ToolCallArgumentsStream (Pi v1.5.0) is experimental.

internal static partial class Program
{
    /// <summary>
    /// A response streaming one text block (started empty; a thinking block with <c>field: "thinking"</c>) and then one tool call, as a
    /// provider produces it: the partial message it changes and the events it emits (upstream <c>setup()</c>). Partial commits go into a
    /// tracked document, as Durable's generation does.
    /// </summary>
    private sealed class PartialStream(string field = "text")
    {
        private readonly ToolCallArgumentsStream args = new();
        private string text = "";
        // The tool call's arguments as the partial shows them; null until the call starts.
        private JsonValue? arguments;

        public PartialChanges Changes { get; } = new();
        public Tracker Tracker { get; } = Tracker.Track(J("{\"generation\":{\"attempt\":1}}"));
        public List<ImmutableArray<Op>> Ops { get; } = [];

        public void Text(string delta)
        {
            text += delta;
            Changes.Observe(field == "text" ? new TextDelta(0, delta) : new ThinkingDelta(0, delta));
        }

        public void Argument(string delta, bool provisional = false)
        {
            if (arguments is null)
            {
                arguments = JsonObjectValue.Empty;
                var call = new ToolCallContent("c1", "write", JsonData.Parse("{}"));
                Changes.Observe(provisional ? new ToolCallProvisionalStarted(1, call) : new ToolCallStarted(1, call));
            }
            arguments = JsonValue.FromData(args.Append(delta));
            Changes.Observe(new ToolCallDelta(1, delta));
        }

        /// <summary>A ToolCallCheckpoint: the call's whole argument text so far, as a provider that replays it sends it.</summary>
        public void ArgumentCheckpoint(string json)
        {
            arguments = JsonValue.FromData(args.Replace(json).Arguments);
            Changes.Observe(new ToolCallCheckpoint(1, json));
        }

        /// <summary>What the provider ended the blocks with, or replaced them with mid-block.</summary>
        public void Replace(string? text = null, string? arguments = null)
        {
            if (text is not null) this.text = text;
            if (arguments is not null) this.arguments = J(arguments);
        }

        /// <summary>A ThinkingCheckpoint: the provider replaced the thinking block's text.</summary>
        public void ThinkingCheckpoint(string text)
        {
            this.text = text;
            Changes.Observe(new ThinkingCheckpoint(0, text));
        }

        /// <summary>The provider ends both blocks.</summary>
        public void EndBlocks(bool finalized = false)
        {
            var call = new ToolCallContent("c1", "write", arguments!.ToData());
            if (finalized)
            {
                Changes.Observe(new ContentBlockFinalized(0, field == "text" ? new TextContent(text) : new ThinkingContent(text)));
                Changes.Observe(new ContentBlockFinalized(1, call));
                return;
            }
            Changes.Observe(field == "text" ? new TextEnded(0, text) : new ThinkingEnded(0, text));
            Changes.Observe(new ToolCallEnded(1, call));
        }

        /// <summary>The partial message as the generation copies it: fresh wire JSON.</summary>
        public JsonObjectValue Message()
        {
            var blocks = $"{{\"type\":\"{field}\",\"{field}\":{JsonSerializer.Serialize(text)}}}";
            if (arguments is not null) blocks += ",{\"type\":\"toolCall\",\"id\":\"c1\",\"name\":\"write\",\"arguments\":" + arguments.ToJson() + "}";
            return (JsonObjectValue)J("{\"role\":\"assistant\",\"content\":[" + blocks + "],\"api\":\"test\",\"provider\":\"test\",\"model\":\"test\"," +
                "\"usage\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"totalTokens\":0," +
                "\"cost\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"total\":0}},\"stopReason\":\"pending\",\"timestamp\":0}");
        }

        /// <summary>One partial commit, as the generation's throttle makes it; <paramref name="fail"/> aborts it like a rejected
        /// commit.</summary>
        public void Commit(bool fail = false)
        {
            var taken = Changes.Take(Message());
            var change = Tracker.BeginChange();
            Changes.Commit(Root(change).GetObject("generation"), taken);
            if (fail)
            {
                change.Abort();
                Changes.Restore(taken);
                return;
            }
            var prepared = change.Prepare();
            Replays(prepared);
            Ops.Add(prepared.Ops);
            Tracker.Adopt(prepared);
        }

        /// <summary>Another writer resets the generation's partial.</summary>
        public void ResetGeneration()
        {
            var change = Tracker.BeginChange();
            Root(change).Set("generation", J("{\"attempt\":1}"));
            Tracker.Adopt(change.Prepare());
        }

        public (string? Text, JsonValue? Arguments) Committed()
        {
            var content = (JsonArrayValue)At(Tracker.Value, "generation", "message", "content");
            var text = content.Count > 0 && ((JsonObjectValue)content[0]).TryGetValue(field, out var value) ? Str(value) : null;
            var arguments = content.Count > 1 && ((JsonObjectValue)content[1]).TryGetValue("arguments", out var parsed) ? parsed : null;
            return (text, arguments);
        }

        public JsonValue? CommittedArguments() => Committed().Arguments;
    }

    private static IEnumerable<string> PartialPieces(string text, int size)
    {
        for (var index = 0; index < text.Length; index += size) yield return text.Substring(index, Math.Min(size, text.Length - index));
    }

    // JSON.stringify(ARGUMENTS): é and the emoji stay characters, so a piece boundary can split the emoji's surrogate pair.
    private static readonly string PartialArgumentsText = """
        {"path":"src/a.ts","n":1234,"ok":true,"content":"line \"one\"\n\tline \\ two é 😀\n","edits":[{"oldText":"a","newText":"b\nc"},{"oldText":"","newText":"XS"}],"nested":{"deep":{"list":[1,[2,"three"],{"four":null}]}}}
        """.Replace("XS", new string('x', 50), StringComparison.Ordinal);

    private static void PartialSame(string expected, JsonValue? actual, string what) =>
        Check(actual is not null && JsonValue.DeepEquals(J(expected), actual), $"{what}: expected {expected}, actual {actual?.ToJson() ?? "nothing"}");

    private static void PartialSame(JsonValue expected, JsonValue? actual, string what) =>
        Check(actual is not null && JsonValue.DeepEquals(expected, actual), $"{what}: expected {expected.ToJson()}, actual {actual?.ToJson() ?? "nothing"}");

    private static IEnumerable<(string, Func<Task>)> HarnessPartialChangesCases() =>
    [
        Case("harness.partial-changes.commits-tool-call-arguments-equal-to-the-final-parse-at-every-piece-size-and-commit-rhythm", () =>
        {
            var expected = J(PartialArgumentsText);
            foreach (var size in new[] { 1, 2, 3, 5, 7, 16 })
            {
                foreach (var every in new[] { 1, 3, 10 })
                {
                    var stream = new PartialStream();
                    var index = 0;
                    foreach (var piece in PartialPieces(PartialArgumentsText, size))
                    {
                        stream.Argument(piece);
                        if (index % every == every - 1) stream.Commit();
                        index++;
                    }
                    stream.Commit();
                    PartialSame(expected, stream.CommittedArguments(), $"size {size}, every {every}");
                }
            }
        }),
        Case("harness.partial-changes.handles-a-number-ending-and-a-string-starting-in-one-piece-and-escapes-split-across-pieces", () =>
        {
            var stream = new PartialStream();
            foreach (var piece in new[]
            {
                "{\"n\":1", "2,\"s\":\"a", "b\\", "u00e9\\\"", "q\\u", "00", "41", "\",\"t\":tr", "ue,", "\"u\":\"\\ud83d", "\\ude00\"}",
            })
            {
                stream.Argument(piece);
                stream.Commit();
            }
            PartialSame("{\"n\":12,\"s\":\"abé\\\"qA\",\"t\":true,\"u\":\"😀\"}", stream.CommittedArguments(), "arguments");
        }),
        Case("harness.partial-changes.appends-growing-argument-and-text-strings-instead-of-reassigning-them", () =>
        {
            var stream = new PartialStream();
            stream.Text("Hello");
            stream.Argument("{\"path\":\"a.ts\",\"content\":\"x");
            stream.Commit();
            for (var index = 0; index < 20; index++)
            {
                stream.Text(" more");
                stream.Argument("yz");
                stream.Commit();
            }
            foreach (var ops in stream.Ops.Skip(1))
            {
                Same("[[\"a\",[\"generation\",\"message\",\"content\",0,\"text\"],\" more\"]," +
                    "[\"a\",[\"generation\",\"message\",\"content\",1,\"arguments\",\"content\"],\"yz\"]]", OpsJson(ops), "ops");
            }
            var (text, arguments) = stream.Committed();
            Equal("Hello" + string.Concat(Enumerable.Repeat(" more", 20)), text, "text");
            PartialSame(JsonValue.Object(KeyValuePair.Create("path", (JsonValue)"a.ts"),
                KeyValuePair.Create("content", (JsonValue)("x" + string.Concat(Enumerable.Repeat("yz", 20))))), arguments, "arguments");
        }),
        Case("harness.partial-changes.applies-the-changes-of-a-failed-commit-in-the-next-one", () =>
        {
            var stream = new PartialStream();
            stream.Argument("{\"a\":\"x");
            stream.Commit();
            stream.Argument("y\",\"b\":[1");
            stream.Commit(fail: true);
            stream.Argument(",2],\"c\":\"z\"}");
            stream.Commit();
            PartialSame("{\"a\":\"xy\",\"b\":[1,2],\"c\":\"z\"}", stream.CommittedArguments(), "arguments");
        }),
        Case("harness.partial-changes.re-applies-a-failed-commits-changes-even-when-it-would-have-added-the-call", () =>
        {
            var stream = new PartialStream();
            stream.Commit();
            stream.Argument("{\"a\":\"x");
            stream.Commit(fail: true);
            stream.Argument("y\"");
            stream.Commit();
            PartialSame("{\"a\":\"xy\"}", stream.CommittedArguments(), "arguments");
        }),
        Case("harness.partial-changes.falls-back-to-the-snapshot-when-the-committed-message-lost-a-call-whose-changes-were-committed", () =>
        {
            var stream = new PartialStream();
            stream.Argument("{\"a\":\"x");
            stream.Commit();
            // Another writer resets the generation's partial.
            stream.ResetGeneration();
            stream.Argument("y\"");
            stream.Commit();
            PartialSame("{\"a\":\"xy\"}", stream.CommittedArguments(), "after the reset");
            stream.Argument(",\"b\":2}");
            stream.Commit();
            PartialSame("{\"a\":\"xy\",\"b\":2}", stream.CommittedArguments(), "after the next piece");
        }),
        Case("harness.partial-changes.commits-the-final-arguments-after-the-call-ends-and-drops-text-appends-when-a-block-ends", () =>
        {
            var stream = new PartialStream();
            stream.Text("draft");
            stream.Argument("{\"a\":\"x");
            stream.Commit();
            // Providers may replace a block's text and arguments as it ends; the last commit assigns what they ended with.
            stream.Text(" more");
            stream.Replace(text: "replaced", arguments: "{\"a\":\"final\"}");
            stream.EndBlocks();
            stream.Commit();
            var (text, arguments) = stream.Committed();
            Equal("replaced", text, "text");
            PartialSame("{\"a\":\"final\"}", arguments, "arguments");
        }),
        // PiSharp-authored from here (see the header).
        Case("harness.partial-changes.reads-a-tool-call-checkpoint-that-continues-the-argument-text-as-the-missing-piece", () =>
        {
            var stream = new PartialStream();
            stream.Argument("{\"a\":\"x");
            stream.Commit();
            stream.ArgumentCheckpoint("{\"a\":\"xyz");
            stream.Commit();
            Same("[[\"a\",[\"generation\",\"message\",\"content\",1,\"arguments\",\"a\"],\"yz\"]]", OpsJson(stream.Ops[^1]), "ops");
            stream.Argument("\"}");
            stream.Commit();
            PartialSame("{\"a\":\"xyz\"}", stream.CommittedArguments(), "arguments");
        }),
        Case("harness.partial-changes.commits-a-parse-of-a-tool-call-checkpoint-that-starts-over-even-after-a-failed-commit", () =>
        {
            var stream = new PartialStream();
            stream.Argument("{\"a\":\"x");
            stream.Commit();
            stream.ArgumentCheckpoint("{\"b\":\"q");
            stream.Commit();
            PartialSame("{\"b\":\"q\"}", stream.CommittedArguments(), "the restarted arguments");
            stream.Argument("r");
            stream.Commit();
            Same("[[\"a\",[\"generation\",\"message\",\"content\",1,\"arguments\",\"b\"],\"r\"]]", OpsJson(stream.Ops[^1]), "an append after it");
            // A failed commit of a restart keeps it a restart.
            stream.ArgumentCheckpoint("{\"c\":[1");
            stream.Commit(fail: true);
            stream.Argument(",2]}");
            stream.Commit();
            PartialSame("{\"c\":[1,2]}", stream.CommittedArguments(), "after a failed restart");
        }),
        Case("harness.partial-changes.drops-thinking-appends-when-a-thinking-checkpoint-replaces-the-text", () =>
        {
            var stream = new PartialStream("thinking");
            stream.Text("plan");
            stream.Commit();
            // The appends since the commit would give the replacement's length: they are dropped, not appended.
            stream.Text(" more");
            stream.ThinkingCheckpoint("PLAN MORE");
            stream.Commit();
            Equal("PLAN MORE", stream.Committed().Text, "the replaced thinking");
            stream.Text(" then");
            stream.Commit();
            Equal("PLAN MORE then", stream.Committed().Text, "an append after it");
            Same("[[\"a\",[\"generation\",\"message\",\"content\",0,\"thinking\"],\" then\"]]", OpsJson(stream.Ops[^1]), "ops");
        }),
        Case("harness.partial-changes.ends-blocks-at-content-block-finalized", () =>
        {
            var stream = new PartialStream();
            stream.Text("draft");
            stream.Argument("{\"a\":\"x");
            stream.Commit();
            stream.Text(" more");
            stream.Replace(text: "DRAFT MORE", arguments: "{\"a\":\"final\"}");
            stream.EndBlocks(finalized: true);
            stream.Commit();
            var (text, arguments) = stream.Committed();
            Equal("DRAFT MORE", text, "text");
            PartialSame("{\"a\":\"final\"}", arguments, "arguments");
        }),
        Case("harness.partial-changes.tracks-a-tool-call-from-its-provisional-start", () =>
        {
            var stream = new PartialStream();
            stream.Argument("{\"path\":\"a", provisional: true);
            stream.Commit();
            stream.Argument(".ts\"}");
            stream.Commit();
            Same("[[\"a\",[\"generation\",\"message\",\"content\",1,\"arguments\",\"path\"],\".ts\"]]", OpsJson(stream.Ops[^1]), "ops");
            PartialSame("{\"path\":\"a.ts\"}", stream.CommittedArguments(), "arguments");
        }),
    ];
}
