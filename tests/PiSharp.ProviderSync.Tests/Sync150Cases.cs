// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/ai/src/utils/streaming-json.ts, json-parse.ts, openrouter-cost.ts,
// constrained-sampling.ts, packages/ai/src/api/openai-completions.ts, openai-responses-shared.ts, google-shared.ts,
// anthropic-messages.ts, pi-messages.ts and packages/coding-agent/src/core/usage-totals.ts.
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI;
using PiSharp.AI.Protocols.GoogleGenerativeAI;
using PiSharp.AI.Protocols.PiMessages;
using PiSharp.AI.Providers;
using PiSharp.CodingAgent.Usage;
using PiSharp.Contracts;

// Authored expectations derived by reading Pi f9a26365 test/streaming-json.test.ts, json-parse.test.ts, openrouter-cost.test.ts and
// the cited source. Nothing here runs upstream code or is an upstream capture. The ported vector tables keep their sources' licences:
// partial-json (MIT) and the Vercel AI SDK fixJson tests (Apache-2.0). The random documents use the same kind of seeded generator as
// the upstream test, not its exact documents.
internal static partial class Program
{
    private static IEnumerable<(string Id, Func<Task> Run)> Sync150Cases() =>
    [
        ("sync150.streaming-json-ported-vectors", S150PortedVectors),
        ("sync150.streaming-json-strings", S150Strings),
        ("sync150.streaming-json-changes", S150Changes),
        ("sync150.streaming-json-malformed-text", S150Malformed),
        ("sync150.streaming-json-random-documents-and-changes", S150RandomDocuments),
        ("sync150.streaming-json-linear-allocation", S150LinearAllocation),
        ("sync150.tool-call-arguments-stream", S150ToolCallArgumentsStream),
        ("sync150.parse-tool-call-arguments-and-repair", S150ParseToolCallArguments),
        ("sync150.completions-grammar-input-deltas", S150CompletionsGrammarDeltas),
        ("sync150.responses-grammar-initial-input-and-final-check", S150ResponsesGrammarInput),
        ("sync150.whitespace-tool-output-placeholders", S150WhitespaceToolOutput),
        ("sync150.usage-total-from-provider-and-combine", S150TotalFromProvider),
        ("sync150.openrouter-reported-cost-completions-and-anthropic", S150OpenRouterCost),
        ("sync150.pi-messages-response-model", S150PiMessagesResponseModel),
        ("sync150.completions-thinking-before-text", S150CompletionsThinkingFirst),
    ];

    // ---------------------------------------------------------------- StreamingJsonParser

    // ' stands for " in the vector tables below; null expected means no value (undefined).
    private static string S150Q(string text) => text.Replace('\'', '"');

    private static string? S150Json(JsonData? value) => value is null ? null : StreamingJson.JsonReformat(value.ToString());

    // finalSnapshot(text, chunkSize): the snapshot after feeding text in pieces of chunkSize; snapshots only at the end, so the
    // copy credit never lags (see StreamingJsonParser's cost note).
    private static JsonData? S150Final(string text, int size)
    {
        var parser = new StreamingJsonParser();
        for (var index = 0; index < text.Length; index += size) parser.Append(text.Substring(index, Math.Min(size, text.Length - index)));
        return parser.Snapshot();
    }

    private static Task S150PortedVectors()
    {
        // partial-json examples.test.js (MIT), with the documented differences (an invalid escape stays visible; Infinity and NaN
        // words are dropped).
        (string Input, string? Expected)[] partialJson =
        [
            ("'", "''"), ("['", "['']"), ("{'': '", "{'':''}"), ("n", "null"), ("t", "true"), ("f", "false"), ("0", "0"),
            ("-1.25e+4", "-12500"), ("-1.25e+", "-1.25"), ("-1.25e", "-1.25"), ("' \\x12", "' \\\\x12'"), ("I", null), ("-I", null), ("N", null),
        ];
        // Vercel AI SDK fix-json.test.ts (Apache-2.0): JSON.parse of its repaired texts; like parseStreamingJson, a number cut after
        // its dot is omitted.
        (string Input, string? Expected)[] fixJson =
        [
            ("", null), ("nul", "null"), ("t", "true"), ("fals", "false"), ("12.2", "12.2"), ("-12", "-12"), ("-", null), ("2.5e", "2.5"),
            ("2.5e-", "2.5"), ("2.5e3", "2500"), ("-2.5e3", "-2500"), ("2.5E", "2.5"), ("2.5E-", "2.5"), ("2.5E3", "2500"), ("-2.5E3", "-2500"),
            ("12.34e", "12.34"), ("5e", "5"), ("'abc", "'abc'"),
            ("'value with \\'quoted\\' text and \\\\ escape", "'value with \\'quoted\\' text and \\\\ escape'"), ("'value with \\", "'value with '"),
            ("'\\u", "''"), ("'\\u12", "''"), ("'text \\u00", "'text '"), ("{'a':'\\u12", "{'a':''}"), ("'value with unicode <'", "'value with unicode <'"),
            ("[", "[]"), ("[[1], [2", "[[1],[2]]"), ("[['1'], ['2", "[['1'],['2']]"), ("[[false], [nu", "[[false],[null]]"), ("[[[]], [[]", "[[[]],[[]]]"),
            ("[[{}], [{", "[[{}],[{}]]"), ("[1, ", "[1]"), ("[[], 123", "[[],123]"), ("{'key':", "{}"),
            ("{'a': {'b': 1}, 'c': {'d': 2", "{'a':{'b':1},'c':{'d':2}}"), ("{'a': {'b': '1'}, 'c': {'d': 2", "{'a':{'b':'1'},'c':{'d':2}}"),
            ("{'a': {'b': false}, 'c': {'d': 2", "{'a':{'b':false},'c':{'d':2}}"), ("{'a': {'b': []}, 'c': {'d': 2", "{'a':{'b':[]},'c':{'d':2}}"),
            ("{'a': {'b': {}}, 'c': {'d': 2", "{'a':{'b':{}},'c':{'d':2}}"), ("{'ke", "{}"), ("{'k1': 1, 'k2", "{'k1':1}"), ("{'k1': 1, 'k2':", "{'k1':1}"),
            ("{'key': 'value'  ", "{'key':'value'}"), ("{'a': {'b': {}", "{'a':{'b':{}}}"), ("[1, [2, 3, [", "[1,[2,3,[]]]"),
            ("[false, [true, [", "[false,[true,[]]]"), ("{'key': {'subKey':", "{'key':{}}"), ("{'key': 123, 'key2': {'subKey':", "{'key':123,'key2':{}}"),
            ("{'key': null, 'key2': {'subKey':", "{'key':null,'key2':{}}"), ("{'key': [1, 2, {", "{'key':[1,2,{}]}"),
            ("[1, 2, {'key': 'value',", "[1,2,{'key':'value'}]"), ("{'a': {'b': ['c', {'d': 'e',", "{'a':{'b':['c',{'d':'e'}]}}"),
            ("{'a': {'b': {'c': {'d':", "{'a':{'b':{'c':{}}}}"), ("{'a': 1, 'b': [", "{'a':1,'b':[]}"), ("{'a': 1, 'b': {", "{'a':1,'b':{}}"),
            ("{'a': 1, 'b': '", "{'a':1,'b':''}"),
            ("{\n  'a': [\n    {\n      'a1': 'v1',\n      'a2': 'v2',\n      'a3': 'v3'\n    }\n  ],\n  'b': [\n    {\n      'b1': 'n",
                "{'a':[{'a1':'v1','a2':'v2','a3':'v3'}],'b':[{'b1':'n'}]}"),
            ("{'type':'div','children':[{'type':'Card','props':{}", "{'type':'div','children':[{'type':'Card','props':{}}]}"),
            ("12.", null), ("12.e", null),
        ];
        foreach (var (raw, expectedRaw) in partialJson.Concat(fixJson))
        {
            var input = S150Q(raw);
            var expected = expectedRaw is null ? null : StreamingJson.JsonReformat(S150Q(expectedRaw));
            foreach (var size in new[] { Math.Max(1, input.Length), 1, 2 })
                Check(S150Json(S150Final(input, size)) == expected, $"Vector {JsonSerializer.Serialize(input)} at size {size}: {S150Json(S150Final(input, size)) ?? "undefined"}");
        }
        return Task.CompletedTask;
    }

    private static Task S150Strings()
    {
        // An escape is held back until complete, at every split position; a partial value is always a prefix of the final one.
        foreach (var (text, value) in new[]
        {
            ("""{"a":"x\u00e9y"}""", "xéy"), ("""{"a":"x\ny"}""", "x\ny"), ("""{"a":"x\\y"}""", "x\\y"), ("""{"a":"x\"y"}""", "x\"y"), ("""{"a":"x\/y"}""", "x/y"),
        })
            for (var split = 0; split <= text.Length; split++)
            {
                var parser = new StreamingJsonParser();
                parser.Append(text[..split]);
                var partial = parser.Snapshot();
                Check(partial is null || partial.Value.ValueKind == JsonValueKind.Object, "Partial root is not an object: " + text[..split]);
                if (partial is not null && partial.Value.TryGetProperty("a", out var shown))
                    Check(value.StartsWith(shown.GetString()!, StringComparison.Ordinal), $"Partial {partial} of {text[..split]} is not a prefix of the value.");
                parser.Append(text[split..]);
                Equal(StreamingJson.JsonReformat("{\"a\":" + StreamingJson.JsonQuote(value) + "}"), S150Json(parser.Snapshot()));
            }
        // Surrogate pairs split across pieces, escaped or raw, join; a high surrogate whose low half has not arrived shows alone.
        foreach (var text in new[] { """{"a":"\ud83d\ude00!"}""", "{\"a\":\"😀!\"}" })
            for (var split = 0; split <= text.Length; split++)
            {
                var parser = new StreamingJsonParser();
                parser.Append(text[..split]); parser.Snapshot(); parser.Append(text[split..]);
                Equal(StreamingJson.JsonReformat("{\"a\":\"😀!\"}"), S150Json(parser.Snapshot()));
            }
        var lone = new StreamingJsonParser(); lone.Append("""{"a":"\ud83d""");
        Equal(StreamingJson.JsonReformat("""{"a":"\ud83d"}"""), S150Json(lone.Snapshot()));
        // Trailing whitespace of an unterminated string is kept; raw control characters and invalid escapes stay visible.
        var spaced = new StreamingJsonParser(); spaced.Append("{\"code\":\"x\t\t  ");
        Equal(StreamingJson.JsonReformat("{\"code\":\"x\\t\\t  \"}"), S150Json(spaced.Snapshot()));
        var visible = new StreamingJsonParser(); visible.Append("{\"a\":\"x\ny \\d \\u12\"");
        Equal(StreamingJson.JsonReformat("{\"a\":\"x\\ny \\\\d \\\\u12\"}"), S150Json(visible.Snapshot()));
        // #10868: an invalid \u escape stays literal without dropping fields; a valid one decodes; a cut one is omitted.
        Equal(StreamingJson.JsonReformat("""{"path":"a.tex","content":"\\usepackage{x}","mode":"w"}"""),
            S150Json(S150Final("""{"path":"a.tex","content":"\usepackage{x}","mode":"w"}""", 1)));
        Equal(StreamingJson.JsonReformat("""{"text":"café \\url"}"""), S150Json(S150Final("""{"text":"caf\u00e9 \url"}""", 3)));
        var cut = new StreamingJsonParser(); cut.Append("""{"text":"ab\u00""");
        Equal("""{"text":"ab"}""", S150Json(cut.Snapshot()));
        // __proto__ is an own member, as JSON.parse makes it.
        const string proto = """{"__proto__":{"polluted":true},"b":{"__proto__":"x"}}""";
        for (var split = 0; split <= proto.Length; split++)
        {
            var parser = new StreamingJsonParser();
            parser.Append(proto[..split]); parser.Snapshot(); parser.Append(proto[split..]);
            var value = parser.Snapshot()!;
            Check(value.Value.TryGetProperty("__proto__", out _), "__proto__ is not an own member.");
            Equal(StreamingJson.JsonReformat(proto), S150Json(value));
        }
        // A duplicate key keeps its last value in the first key's position.
        const string duplicate = """{"a":1,"b":2,"a":"three"}""";
        Equal("""{"a":"three","b":2}""", StreamingJson.JsonReformat(duplicate));
        Equal("""{"a":"three","b":2}""", S150Json(S150Final(duplicate, 1)));
        return Task.CompletedTask;
    }

    private static string S150Path(ImmutableArray<JsonPathSegment> path) =>
        "[" + string.Join(",", path.Select(segment => segment.Key is { } key ? StreamingJson.JsonQuote(key) : segment.Index.ToString(CultureInfo.InvariantCulture))) + "]";

    private static string S150Changes(IReadOnlyList<JsonChange> changes) => string.Join(" | ", changes.Select(change => change switch
    {
        JsonSetChange set => "set " + S150Path(set.Path) + " " + StreamingJson.JsonReformat(set.Value.ToString()),
        JsonAppendChange append => "append " + S150Path(append.Path) + " " + StreamingJson.JsonQuote(append.Text),
        _ => throw new InvalidOperationException("Unknown change."),
    }));

    private static Task S150Changes()
    {
        // A number that ends and a string that starts in one piece are reported in order; text after the root reports nothing.
        var parser = new StreamingJsonParser();
        Equal("set [] {}", S150Changes(parser.Append("{\"n\":1")));
        Equal("set [\"n\"] 12 | set [\"s\"] \"ab\"", S150Changes(parser.Append("2,\"s\":\"ab")));
        Equal("append [\"s\"] \"c\"", S150Changes(parser.Append("c\",\"t\":tru")));
        Equal("set [\"t\"] true", S150Changes(parser.Append("e}")));
        Equal("", S150Changes(parser.Append(" {\"ignored\":1}")));
        // Nested objects and arrays are reported as empty containers, then their members and items.
        var nested = new StreamingJsonParser();
        Equal("set [] {} | set [\"a\"] [] | set [\"a\",0] 1 | set [\"a\",1] {} | set [\"a\",1,\"b\"] [] | set [\"a\",1,\"b\",0] \"x\"",
            S150Changes(nested.Append("{\"a\":[1,{\"b\":[\"x\"")));
        Equal("set [\"a\",1,\"b\",1] \"y\"", S150Changes(nested.Append(",\"y")));
        Equal("append [\"a\",1,\"b\",1] \"z\" | set [\"a\"] \"again\"", S150Changes(nested.Append("z\"]}],\"a\":\"again\"}")));
        // Split escapes are held back and reported decoded once complete, at every pair of split positions.
        const string escapes = """{"a":"x\u00e9\n\\\"\ud83d\ude00\q"}""";
        var decoded = StreamingJson.JsonReformat("""{"a":"x\u00e9\n\\\"\ud83d\ude00\\q"}""");
        for (var first = 1; first < escapes.Length; first++)
            for (var second = first; second < escapes.Length; second++)
            {
                var split = new StreamingJsonParser(); var mirror = new S150Mirror();
                foreach (var piece in new[] { escapes[..first], escapes[first..second], escapes[second..] }) mirror.Apply(split.Append(piece));
                Equal(decoded, mirror.Json);
            }
        var held = new StreamingJsonParser(); held.Append("{\"a\":\"x");
        Equal("", S150Changes(held.Append("\\u00")));
        Equal("append [\"a\"] \"é\"", S150Changes(held.Append("e9")));
        // A container nested deeper than 32 levels is reported once, whole, when it closes.
        var deep = string.Concat(Enumerable.Repeat("{\"a\":", 40)) + "\"deep\"" + new string('}', 40);
        var deepParser = new StreamingJsonParser(); var deepMirror = new S150Mirror();
        for (var index = 0; index < deep.Length; index += 3) deepMirror.Apply(deepParser.Append(deep.Substring(index, Math.Min(3, deep.Length - index))));
        Equal(StreamingJson.JsonReformat(deep), deepMirror.Json);
        var whole = new StreamingJsonParser().Append(deep);
        Equal(34, whole.Count);
        Check(whole[^1] is JsonSetChange { Path.Length: 32 } last && StreamingJson.JsonReformat(last.Value.ToString()) == StreamingJson.JsonReformat(deep[(32 * 5)..^32]),
            "The deep container was not reported whole at path length 32.");
        // Values in invalid positions and text after the root report nothing.
        var invalid = new StreamingJsonParser();
        Equal("set [] {} | set [\"a\"] 1 | set [\"b\"] \"x\"", S150Changes(invalid.Append("{\"a\":1 2, \"b\" \"x\"}")));
        Equal("", S150Changes(invalid.Append("{\"c\":3}")));
        return Task.CompletedTask;
    }

    private static readonly string[] S150MalformedTexts =
    [
        "{\"a\":1 \"b\":2}", "{\"a\" 1, \"b\": 2}", "{a:1, 'b': 2}", "{\"a\":1,,\"b\":2,}", "[1 2, , 3,]", "{\"a\":[1,2}, \"b\":3]", "]]]}}}{",
        "{\"a\":NaN,\"b\":Infinity,\"c\":-Infinity,\"d\":undefined,\"e\":1}", "{\"a\":01,\"b\":1.,\"c\":.5,\"d\":+1,\"e\":0x10,\"f\":1e}",
        "{\"a\":\"x\"} trailing garbage {\"b\":2}", "{\"a\": tru, \"b\": nulll, \"c\": falsey}", "{\"a\":{\"b\":{\"c\":", "{\"a\\\": 1}",
        "\"unterminated \\", "// comment\n{\"a\":1}", "\u0000{\u0001\"a\"\u0002:\u00031}", "{\"a\":\"x\"}}}]]]",
    ];

    private static Task S150Malformed()
    {
        // Malformed text never throws, and every chunk size reads it the same way, snapshots taken after every piece.
        foreach (var text in S150MalformedTexts)
        {
            var whole = S150Json(S150Final(text, text.Length));
            foreach (var size in new[] { 1, 2, 3, 7 })
            {
                var parser = new StreamingJsonParser(); JsonData? last = null;
                for (var index = 0; index < text.Length; index += size)
                {
                    parser.Append(text.Substring(index, Math.Min(size, text.Length - index)));
                    last = parser.Snapshot();
                }
                Check(S150Json(last) == whole, $"Chunk size {size} read {JsonSerializer.Serialize(text)} differently.");
            }
        }
        // Missing commas and colons are tolerated; values in invalid positions are dropped.
        Equal("{\"a\":1,\"b\":2}", S150Json(S150Final("{\"a\":1 \"b\":2}", 100)));
        Equal("{\"a\":1,\"b\":2}", S150Json(S150Final("{\"a\" 1, \"b\": 2}", 100)));
        Equal("[1,2,3]", S150Json(S150Final("[1 2, , 3,]", 100)));
        Equal("{\"e\":1}", S150Json(S150Final("{\"a\":NaN,\"b\":Infinity,\"e\":1}", 100)));
        Equal("{\"a\":\"x\"}", S150Json(S150Final("{\"a\":\"x\"} trailing garbage {\"b\":2}", 100)));
        return Task.CompletedTask;
    }

    // test/streaming-json.test.ts random(seed): the same linear congruential generator.
    private sealed class S150Random(int seed)
    {
        private long _state = seed;
        public double Next() { _state = (_state * 1103515245 + 12345) & 0x7fffffff; return _state / (double)0x7fffffff; }
        public T Pick<T>(IReadOnlyList<T> items) => items[(int)Math.Floor(Next() * items.Count)];
    }

    private static readonly string[] S150StringPieces = ["a", "b", "x y", " ", "\n", "\t", "\\", "\"", "/", "é", "😀", "\u0001", "\u001f", "\u2028", "{", "]", ","];

    // A valid JSON document like the upstream test's randomDocuments: mostly {"path":...,"args":...}, with random insignificant
    // whitespace when spaced. Generated keys may repeat; JSON.parse keeps the last value.
    private static string S150Document(S150Random rand, bool spaced)
    {
        string Space() => spaced ? rand.Pick(["", "", " ", "\n  ", "\t", "\r\n"]) : "";
        string Text()
        {
            var text = new StringBuilder();
            for (var count = (int)Math.Floor(rand.Next() * 6); count > 0; count--) text.Append(rand.Pick(S150StringPieces));
            return StreamingJson.JsonQuote(text.ToString());
        }
        string Scalar() => (int)Math.Floor(rand.Next() * 10) switch
        {
            0 => Text(), 1 => JsonNumber.Text(Math.Floor(rand.Next() * 2000) - 1000), 2 => JsonNumber.Text(rand.Next() * 100), 3 => "1e+21",
            4 => "-5e-8", 5 => "-0", 6 => "0", 7 => "true", 8 => "false", _ => "null",
        };
        string Value(int depth)
        {
            var roll = rand.Next();
            if (depth > 3 || roll < 0.3) return Scalar();
            var count = (int)Math.Floor(rand.Next() * 4);
            if (roll < 0.65)
                return "{" + Space() + string.Join(Space() + "," + Space(), Enumerable.Range(0, count).Select(_ => Text() + Space() + ":" + Space() + Value(depth + 1))) + Space() + "}";
            return "[" + Space() + string.Join(Space() + "," + Space(), Enumerable.Range(0, count).Select(_ => Value(depth + 1))) + Space() + "]";
        }
        return rand.Next() < 0.8 ? "{" + Space() + "\"path\"" + Space() + ":" + Space() + Text() + Space() + "," + Space() + "\"args\"" + Space() + ":" + Space() + Value(0) + Space() + "}"
            : Value(0);
    }

    private static Task S150RandomDocuments()
    {
        var rand = new S150Random(1);
        string[] noise = ["\"", "\\", "{", "}", "[", "]", ",", ":", "x", "1", "-", "\n", "t"];
        for (var document = 0; document < 300; document++)
        {
            var text = S150Document(rand, document % 2 == 0);
            var expected = StreamingJson.JsonReformat(text);
            // Complete documents read exactly like JSON.parse at every chunk size; the changes keep a copy equal to it.
            foreach (var size in new[] { 1, 2, 3, 7 })
            {
                Check(S150Json(S150Final(text, size)) == expected, $"Size {size} final differs for {JsonSerializer.Serialize(text)}");
                var parser = new StreamingJsonParser(); var mirror = new S150Mirror();
                for (var index = 0; index < text.Length; index += size) mirror.Apply(parser.Append(text.Substring(index, Math.Min(size, text.Length - index))));
                // A space ends a number or literal at the root, which changes do not report until it ends.
                mirror.Apply(parser.Append(" "));
                Check(mirror.Json == expected, $"Size {size} mirror differs for {JsonSerializer.Serialize(text)}: {mirror.Json}");
            }
            // Random corruption: snapshots never throw, and the changes keep a copy equal to the parser's own value.
            var characters = text.ToList();
            for (var edit = 0; edit < 3; edit++)
            {
                var at = (int)Math.Floor(rand.Next() * (characters.Count + 1));
                if (rand.Next() < 0.5) { if (at < characters.Count) characters.RemoveAt(at); }
                else characters.InsertRange(at, rand.Pick(noise));
            }
            var corrupted = new string([.. characters]);
            var corruptedParser = new StreamingJsonParser(); var corruptedMirror = new S150Mirror();
            for (var index = 0; index < corrupted.Length; index += 4)
            {
                corruptedMirror.Apply(corruptedParser.Append(corrupted.Substring(index, Math.Min(4, corrupted.Length - index))));
                corruptedParser.Snapshot();
            }
            corruptedMirror.Apply(corruptedParser.Append(" "));
            var fresh = new StreamingJsonParser(); fresh.Append(corrupted); fresh.Append(" ");
            Check(corruptedMirror.Json == S150Json(fresh.Snapshot()), $"Mirror differs for corrupted {JsonSerializer.Serialize(corrupted)}");
        }
        return Task.CompletedTask;
    }

    /// <summary>A copy of a value kept current by applying the parser's changes, as a writer such as Durable keeps one. Strings keep
    /// their JSON-escaped text, so a surrogate pair split across two appends joins.</summary>
    private sealed class S150Mirror
    {
        private abstract class Node;
        private sealed class ObjectNode : Node { public readonly List<string> Keys = []; public readonly Dictionary<string, Node> Values = new(StringComparer.Ordinal); }
        private sealed class ArrayNode : Node { public readonly List<Node> Items = []; }
        private sealed class StringNode(string escaped) : Node { public readonly StringBuilder Escaped = new(escaped); }
        private sealed class RawNode(string text) : Node { public readonly string Text = text; }
        private Node? _root;

        public string? Json => _root is null ? null : StreamingJson.JsonReformat(Write(_root));

        public void Apply(IReadOnlyList<JsonChange> changes)
        {
            foreach (var change in changes)
            {
                if (change is JsonAppendChange append)
                {
                    var target = (StringNode)Find(append.Path);
                    target.Escaped.Append(StreamingJson.JsonQuote(append.Text).AsSpan()[1..^1]);
                    continue;
                }
                var set = (JsonSetChange)change;
                var raw = set.Value.ToString();
                Node node = set.Value.Value.ValueKind switch
                {
                    JsonValueKind.Object when raw == "{}" => new ObjectNode(),
                    JsonValueKind.Array when raw == "[]" => new ArrayNode(),
                    JsonValueKind.String => new StringNode(raw[1..^1]),
                    _ => new RawNode(raw),
                };
                if (set.Path.Length == 0) { _root = node; continue; }
                var parent = Find(set.Path[..^1]);
                var segment = set.Path[^1];
                if (parent is ObjectNode members)
                {
                    if (!members.Values.ContainsKey(segment.Key!)) members.Keys.Add(segment.Key!);
                    members.Values[segment.Key!] = node;
                }
                else if (segment.Index == ((ArrayNode)parent).Items.Count) ((ArrayNode)parent).Items.Add(node);
                else ((ArrayNode)parent).Items[segment.Index] = node;
            }
        }

        private Node Find(ImmutableArray<JsonPathSegment> path)
        {
            var node = _root ?? throw new InvalidOperationException("A change addressed a value before the root.");
            foreach (var segment in path) node = node is ObjectNode members ? members.Values[segment.Key!] : ((ArrayNode)node).Items[segment.Index];
            return node;
        }

        private static string Write(Node node) => node switch
        {
            ObjectNode members => "{" + string.Join(",", members.Keys.Select(key => StreamingJson.JsonQuote(key) + ":" + Write(members.Values[key]))) + "}",
            ArrayNode items => "[" + string.Join(",", items.Items.Select(Write)) + "]",
            StringNode text => "\"" + text.Escaped + "\"",
            RawNode raw => raw.Text,
            _ => throw new InvalidOperationException("Unknown node."),
        };
    }

    private static Task S150LinearAllocation()
    {
        // #9265: streamed arguments were re-parsed in full on every delta. `write`-style arguments streamed in 32-character deltas
        // must allocate linearly in the text: ten times the text costs about ten times the bytes (quadratic would be about 100x).
        var content = string.Concat(Enumerable.Range(0, 20_000).Select(line => $"const line{line} = \"value {line}\"; // \\ done\n"));
        string WriteArguments(int size) => "{\"path\":\"/src/file.ts\",\"content\":" + StreamingJson.JsonQuote(content[..size]) + "}";
        static long Allocated(string text)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var arguments = new ToolCallArgumentsStream();
            for (var index = 0; index < text.Length; index += 32) arguments.Append(text.Substring(index, Math.Min(32, text.Length - index)));
            var result = arguments.Finish();
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Check(result.ArgumentsError is null, "Streamed write arguments did not parse.");
            return bytes;
        }
        var small = WriteArguments(50_000); var large = WriteArguments(500_000);
        Allocated(small);
        var smallBytes = Allocated(small); var largeBytes = Allocated(large);
        Check(largeBytes / (double)smallBytes < 20, $"Allocation grew {largeBytes / (double)smallBytes:F1}x for 10x the text.");
        // An authored bound: measured about 150 bytes per character here (owned snapshots copy more than upstream's shared ones);
        // re-parsing the accumulated text costs far more.
        Check(largeBytes / (double)large.Length < 300, $"Allocated {largeBytes / (double)large.Length:F0} bytes per character.");
        return Task.CompletedTask;
    }

    private static Task S150ToolCallArgumentsStream()
    {
        // Partial arguments show as an object; the finish is one full parse.
        var arguments = new ToolCallArgumentsStream();
        Equal("{}", arguments.Append("  ").ToString());
        Equal("""{"path":"a.ts","content":"x"}""", S150Json(arguments.Append("""{"path":"a.ts","content":"x""")));
        var before = arguments.Append("y");
        Check(ReferenceEquals(before, arguments.Append("")), "An empty delta returned a new partial.");
        Equal("""  {"path":"a.ts","content":"xy""", arguments.Text);
        var expected = StreamingJson.ParseToolCallArguments("""  {"path":"a.ts","content":"xy""");
        var unfinished = arguments.Finish();
        Equal(expected.Arguments.ToString(), unfinished.Arguments.ToString()); Equal(expected.ArgumentsError, unfinished.ArgumentsError);
        Check(unfinished.ArgumentsError is not null && unfinished.Arguments.ToString() == "{}", "Cut-off arguments were completed.");
        arguments.Append("\"}");
        var finished = arguments.Finish();
        Equal("""{"path":"a.ts","content":"xy"}""", S150Json(finished.Arguments)); Equal(null, finished.ArgumentsError);
        // A root that is not an object shows as no arguments yet and finishes with an error.
        var array = new ToolCallArgumentsStream();
        Equal("{}", array.Append("[1,2").ToString());
        array.Append("]");
        Equal(NotAnObject + "\n\nReceived arguments:\n[1,2]", array.Finish().ArgumentsError);
        // The provider's complete text replaces the streamed text, returning the missing piece when it continues it.
        var replaced = new ToolCallArgumentsStream();
        replaced.Append("""{"a":"x""");
        var (delta, partial) = replaced.Replace("""{"a":"xyz"}""");
        Equal("yz\"}", delta); Equal("""{"a":"xyz"}""", S150Json(partial));
        (delta, partial) = replaced.Replace("""{"b":2}""");
        Equal(null, delta); Equal("""{"b":2}""", S150Json(partial));
        Equal("""{"b":2}""", replaced.Text);
        return Task.CompletedTask;
    }

    private static Task S150ParseToolCallArguments()
    {
        // json-parse.test.ts #10897: complete JSON parses and invalid escapes are repaired.
        var repaired = StreamingJson.ParseToolCallArguments("""{"kind":"seek","note":"\url"}""");
        Equal("""{"kind":"seek","note":"\\url"}""", S150Json(repaired.Arguments)); Equal(null, repaired.ArgumentsError);
        var empty = StreamingJson.ParseToolCallArguments("");
        Equal("{}", empty.Arguments.ToString()); Equal(null, empty.ArgumentsError);
        // The valid prefix of malformed JSON is not kept, and cut-off JSON is not completed.
        foreach (var raw in new[] { """{"action":"apply","actions":[{"kind":"seek","time":.5}]}""", """{"kind":"seek","clipId":"clip-fl""" })
        {
            var result = StreamingJson.ParseToolCallArguments(raw);
            Equal("{}", result.Arguments.ToString());
            Check(result.ArgumentsError is { } error && error.Contains("\n\nReceived arguments:\n", StringComparison.Ordinal) && error.EndsWith(raw, StringComparison.Ordinal),
                "argumentsError does not report the received text: " + result.ArgumentsError);
        }
        var notObject = StreamingJson.ParseToolCallArguments("[1,2]");
        Equal("{}", notObject.Arguments.ToString()); Equal(NotAnObject + "\n\nReceived arguments:\n[1,2]", notObject.ArgumentsError);
        // parseStreamingJson #10868, and repairJson no longer treats \u as always valid: four hex digits stay, anything else is doubled.
        Equal("""{"text":"\\url{x}"}""", S150Json(StreamingJson.Parse("""{"text":"\url{x}"}""")));
        Equal("""{"path":"a.tex","content":"\\usepackage{x}","mode":"w"}""", S150Json(StreamingJson.Parse("""{"path":"a.tex","content":"\usepackage{x}","mode":"w"}""")));
        Equal(StreamingJson.JsonReformat("""{"text":"café \\url"}"""), S150Json(StreamingJson.Parse("""{"text":"caf\u00e9 \url"}""")));
        Equal("""{"text":"ab"}""", S150Json(StreamingJson.Parse("""{"text":"ab\u00""")));
        Equal("""{"a":"\u00e9 \\url \\u12"}""", StreamingJson.RepairJson("""{"a":"\u00e9 \url \u12"}"""));
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- Grammar tool input

    private static string S150CompletionsChunk(string delta, string? finish = null) =>
        "{\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-5-fixture\",\"choices\":[{\"index\":0,\"delta\":" + delta +
        ",\"finish_reason\":" + (finish is null ? "null" : "\"" + finish + "\"") + "}]}";

    private static async Task<(List<string> Deltas, StreamTerminalEvent? Terminal)> S150Stream(IChatTransport transport, ChatRequest request)
    {
        var deltas = new List<string>(); StreamTerminalEvent? terminal = null;
        await foreach (var progress in transport.StreamAsync(request).WithCancellation(CancellationToken.None))
            if (progress is ToolCallDelta delta) deltas.Add(delta.Delta);
            else if (progress is StreamTerminalEvent ended) terminal = ended;
        return (deltas, terminal);
    }

    private static async Task S150CompletionsGrammarDeltas()
    {
        // openai-completions.ts: a custom tool call's input streams as appendGrammarToolInputJsonDelta pieces of {"code":"<input>"}, each
        // linear in the new input, and finishBlock closes it with closeGrammarToolInputJson.
        var model = new ModelDescriptor("gpt-5-fixture", "openai-completions", "openai");
        var stream = Frames(S150CompletionsChunk("""{"role":"assistant","tool_calls":[{"index":0,"id":"call_c","type":"custom","custom":{"name":"codemode","input":"print(\"hi\")"}}]}"""),
            S150CompletionsChunk("""{"tool_calls":[{"index":0,"custom":{"input":"\n"}}]}"""), S150CompletionsChunk("{}", "tool_calls"));
        using var handler = new Handler(_ => Task.FromResult(Sse(null, stream)));
        using var provider = NativeProviderFactory.CreateCompletions(model, new("https://api.openai.com/v1/chat/completions"), Key,
            new(ToolDeclarations: new() { SupportsOpenAIGrammarTools = true }), null, handler);
        var (deltas, terminal) = await S150Stream(provider.Transport, new(model, [GrammarSystem, User("go", 1)], 1)).WaitAsync(Deadline);
        Equal("""{"code":"print(\"hi\")|\n|"}""", string.Join("|", deltas));
        Check(terminal is StreamDone { Reason: StopReason.ToolUse } done && done.Message.Content.Single() is ToolCallContent { Id: "call_c", Name: "codemode" } call &&
            call.Arguments.ToString() == """{"code":"print(\"hi\")\n"}""", "Completions grammar call final differs.");
    }

    private static async Task S150ResponsesGrammarInput()
    {
        // openai-responses-shared.ts customInput: an item's initial input goes out with the first delta; the final input must continue
        // the streamed one (closeGrammarToolInputJson), and output_item.done with the same input adds nothing.
        static string Stream(string? delta, string done) => Frames(new[]
        {
            """{"type":"response.created","response":{"id":"r1"}}""",
            """{"type":"response.output_item.added","output_index":0,"item":{"type":"custom_tool_call","id":"ctc_i","call_id":"call_i","name":"codemode","input":"print("}}""",
        }.Concat(delta is null ? [] : ["""{"type":"response.custom_tool_call_input.delta","output_index":0,"item_id":"ctc_i","delta":""" + JsonSerializer.Serialize(delta) + "}"])
            .Concat(
        [
            """{"type":"response.custom_tool_call_input.done","output_index":0,"item_id":"ctc_i","input":""" + JsonSerializer.Serialize(done) + "}",
            """{"type":"response.output_item.done","output_index":0,"item":{"type":"custom_tool_call","id":"ctc_i","call_id":"call_i","name":"codemode","input":""" + JsonSerializer.Serialize(done) + "}}",
            Completed,
        ]).ToArray());
        async Task<(List<string> Deltas, StreamTerminalEvent? Terminal)> Run(string body)
        {
            using var handler = new Handler(_ => Task.FromResult(Sse(null, body)));
            using var provider = NativeProviderFactory.CreateResponses(GrammarModel, new("https://api.openai.com/v1/responses"), Key, new(false), null, handler, GrammarMetadata(true));
            return await S150Stream(provider.Transport, new(GrammarModel, [GrammarSystem, User("go", 1)], 1)).WaitAsync(Deadline);
        }
        var (deltas, terminal) = await Run(Stream("1)", "print(1)"));
        Equal("""{"code":"print(1)|"}""", string.Join("|", deltas));
        Check(terminal is StreamDone { Reason: StopReason.ToolUse } done && done.Message.Content.Single() is ToolCallContent call &&
            call.Arguments.ToString() == """{"code":"print(1)"}""", "Responses grammar call final differs.");
        (deltas, _) = await Run(Stream(null, "print(1)"));
        Equal("""{"code":"print(1)"}""", string.Join("|", deltas));
        // A final input that does not continue the streamed one fails the turn.
        (_, terminal) = await Run(Stream("1)", "print(2)"));
        Check(terminal is not null && terminal.Message.StopReason == StopReason.Error, "A non-monotonic final grammar input did not fail the turn.");
    }

    // ---------------------------------------------------------------- Whitespace tool output (4dd2b149d)

    private static async Task<JsonElement> S150Body(Func<HttpMessageHandler, IChatTransport> create, ModelDescriptor model, ImmutableArray<TranscriptEntry> messages)
    {
        string? sent = null;
        using var handler = new Handler(async request =>
        {
            sent = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        });
        await new ChatClient(create(handler)).CompleteAsync(new(model, messages, 5)).WaitAsync(Deadline);
        return JsonDocument.Parse(sent ?? throw new InvalidOperationException("No request was sent.")).RootElement.Clone();
    }

    private static ImmutableArray<TranscriptEntry> S150WhitespaceTranscript(string provider, string api, string model) =>
    [
        Entry("""{"role":"system","content":"Base","toolsAdded":[""" + ReadTool + """],"timestamp":0}"""), User("go", 1),
        Calls(provider, api, model, 2, ("call_1", "read", """{"path":"a"}""")), Result("call_1", "read", "\\r\\n \\t", 3),
    ];

    private static async Task S150WhitespaceToolOutput()
    {
        // hasText = textResult.trim().length > 0: whitespace-only text is no output.
        var completions = new ModelDescriptor("gpt-fixture", "openai-completions", "openai");
        var completionsBody = await S150Body(handler => NativeProviderFactory.CreateCompletions(completions, new("https://api.openai.com/v1/chat/completions"), Key,
            null, null, handler).Transport, completions, S150WhitespaceTranscript("openai", "openai-completions", "gpt-fixture"));
        Equal("(no tool output)", completionsBody.GetProperty("messages").EnumerateArray().Single(message => message.GetProperty("role").GetString() == "tool")
            .GetProperty("content").GetString());
        var responses = new ModelDescriptor("gpt-fixture", "openai-responses", "openai");
        var responsesBody = await S150Body(handler => NativeProviderFactory.CreateResponses(responses, new("https://api.openai.com/v1/responses"), Key, new(false), null,
            handler).Transport, responses, S150WhitespaceTranscript("openai", "openai-responses", "gpt-fixture"));
        Equal("(no tool output)", responsesBody.GetProperty("input").EnumerateArray().Single(item => item.TryGetProperty("type", out var type) &&
            type.GetString() == "function_call_output").GetProperty("output").GetString());
        // google-shared.ts sends "" (the placeholder only when the result has images).
        var googleBody = await S150Body(handler =>
        {
            var client = new HttpClient(handler);
            return new GoogleGenerativeAIHttpTransport(client, Gemini, new GoogleGenerativeAIOptions(GoogleMetadata(), Key));
        }, Gemini, S150WhitespaceTranscript("google", "google-generative-ai", Gemini.Id));
        var response = googleBody.GetProperty("contents").EnumerateArray().SelectMany(content => content.GetProperty("parts").EnumerateArray())
            .Single(part => part.TryGetProperty("functionResponse", out _)).GetProperty("functionResponse").GetProperty("response");
        Equal("", response.GetProperty("output").GetString());
    }

    // ---------------------------------------------------------------- Usage cost totalFromProvider

    private static JsonElement S150Cost(AssistantMessage message) => PiWireJson.WriteMessage(message).Value.GetProperty("usage").GetProperty("cost");

    private static async Task<AssistantMessage> S150CompletionsUsage(ModelDescriptor model, Uri endpoint, string usage)
    {
        var stream = Frames(S150CompletionsChunk("""{"role":"assistant","content":"ok"}"""), S150CompletionsChunk("{}", "stop"),
            "{\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"" + model.Id + "\",\"choices\":[],\"usage\":" + usage + "}");
        using var handler = new Handler(_ => Task.FromResult(Sse(null, stream)));
        using var provider = NativeProviderFactory.CreateCompletions(model, endpoint, Key, null, null, handler);
        var result = await new ChatClient(provider).CompleteAsync(new(model, [Ask], 1)).WaitAsync(Deadline);
        Equal(StopReason.Stop, result.Message.StopReason);
        return result.Message;
    }

    private static async Task<AssistantMessage> S150AnthropicUsage(JsonData metadata, Uri endpoint, object deltaUsage)
    {
        static string Frame(string type, object value) => "event: " + type + "\ndata: " + JsonSerializer.Serialize(value) + "\n\n";
        var stream = Frame("message_start", new { type = "message_start", message = new { id = "authored", role = "assistant", model = "claude-haiku-5-5", content = Array.Empty<object>(), usage = new { input_tokens = 1000, output_tokens = 0 } } })
            + Frame("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } })
            + Frame("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = "ok" } })
            + Frame("content_block_stop", new { type = "content_block_stop", index = 0 })
            + Frame("message_delta", new { type = "message_delta", delta = new { stop_reason = "end_turn" }, usage = deltaUsage })
            + Frame("message_stop", new { type = "message_stop" });
        var model = new ModelDescriptor("claude-haiku-5-5", "anthropic-messages", "anthropic");
        using var handler = new Handler(_ => Task.FromResult(EventStream(stream)));
        using var provider = NativeProviderFactory.CreateAnthropic(model, endpoint, Key, new(MaximumTokens: 4096, ModelReasoning: true, ThinkingEnabled: false), null, handler, metadata);
        var result = await new ChatClient(provider).CompleteAsync(new(model, [Ask], 1)).WaitAsync(Deadline);
        Equal(StopReason.Stop, result.Message.StopReason);
        return result.Message;
    }

    private static async Task S150TotalFromProvider()
    {
        // models.ts calculateCost ends with cost.totalFromProvider = false; the field follows the five cost fields.
        var completions = await S150CompletionsUsage(new("gpt-fixture", "openai-completions", "openai"), new("https://api.openai.com/v1/chat/completions"),
            """{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15,"cost":0.5,"is_byok":false}""");
        Equal("""{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"total":0,"totalFromProvider":false}""", S150Cost(completions).GetRawText());
        var anthropic = await S150AnthropicUsage(CatalogRow("anthropic", "claude-haiku-5-5"), new("https://api.anthropic.com/"), new { output_tokens = 1000, cost = 0.25, is_byok = false });
        Check(S150Cost(anthropic).GetProperty("totalFromProvider").ValueKind == JsonValueKind.False &&
            S150Cost(anthropic).GetProperty("total").GetRawText() == JsonNumber.Text(0.0001 + 0.0005),
            "Anthropic's own endpoint used a reported cost: " + S150Cost(anthropic).GetRawText());
        var (_, responsesResults) = await GrammarExchange(false, ([User("go", 1)], Frames("""{"type":"response.created","response":{"id":"r"}}""", Completed)));
        Equal("""{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"total":0,"totalFromProvider":false}""", S150Cost(responsesResults.Single().Message).GetRawText());
        var googleStream = "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"ok\"}]},\"finishReason\":\"STOP\"}],\"usageMetadata\":{\"promptTokenCount\":1,\"candidatesTokenCount\":1,\"totalTokenCount\":2}}\n\n";
        using (var handler = new Handler(_ => Task.FromResult(EventStream(googleStream))))
        using (var client = new HttpClient(handler))
        {
            var google = await new ChatClient(new GoogleGenerativeAIHttpTransport(client, Gemini, new GoogleGenerativeAIOptions(GoogleMetadata(), Key)))
                .CompleteAsync(new(Gemini, [Ask], 1)).WaitAsync(Deadline);
            Check(S150Cost(google.Message).GetProperty("totalFromProvider").ValueKind == JsonValueKind.False, "Google cost lacks totalFromProvider false.");
        }
        // usage-totals.ts combineUsage: a sum is provider-reported only when every part is.
        static TokenUsage Usage(string? reported) => new(1, 1, 0, 0, 2, new(0, 0, 0, 0, 1,
            reported is null ? null : JsonFields.Empty.Set("totalFromProvider", JsonData.Parse(reported))));
        static string Combined(TokenUsage first, TokenUsage second) =>
            UsageTotalsCalculator.CombineUsage(first, second).Cost.ExtraProperties!.TryGet("totalFromProvider", out var value) ? value!.ToString() : "absent";
        Equal("true", Combined(Usage("true"), Usage("true")));
        Equal("false", Combined(Usage("true"), Usage("false")));
        Equal("false", Combined(Usage("true"), Usage(null)));
        Equal("false", Combined(Usage(null), Usage(null)));
    }

    private static async Task S150OpenRouterCost()
    {
        // openrouter-cost.ts applyOpenRouterReportedCost after calculateCost, for a provider of openrouter or a base URL on openrouter.ai.
        var model = new ModelDescriptor("fixture", "openai-completions", "openrouter");
        var endpoint = new Uri("https://openrouter.ai/api/v1/chat/completions");
        async Task<JsonElement> Cost(string fields) =>
            S150Cost(await S150CompletionsUsage(model, endpoint, """{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15,""" + fields + "}"));
        void Reported(JsonElement cost, string total)
        {
            Check(cost.GetProperty("totalFromProvider").ValueKind == JsonValueKind.True, "Reported cost not marked: " + cost.GetRawText());
            Equal(total, cost.GetProperty("total").GetRawText());
        }
        // Not BYOK: OpenRouter's billed total.
        Reported(await Cost("""
            "cost":0.5,"is_byok":false,"cost_details":{"upstream_inference_cost":0,"upstream_inference_prompt_cost":0,"upstream_inference_completions_cost":0}
            """), "0.5");
        Reported(await Cost("""
            "cost":0,"is_byok":false,"cost_details":{"upstream_inference_cost":0,"upstream_inference_prompt_cost":0,"upstream_inference_completions_cost":0}
            """), "0");
        // BYOK: OpenRouter's fee plus the upstream inference cost, or its components when the total is null.
        Reported(await Cost("""
            "cost":0.1,"is_byok":true,"cost_details":{"upstream_inference_cost":0.9,"upstream_inference_prompt_cost":0.6,"upstream_inference_completions_cost":0.3}
            """), "1");
        Reported(await Cost("""
            "cost":0.1,"is_byok":true,"cost_details":{"upstream_inference_cost":null,"upstream_inference_prompt_cost":0.6,"upstream_inference_completions_cost":0.3}
            """), JsonNumber.Text(0.1 + (0.6 + 0.3)));
        // Incomplete or invalid accounting keeps the catalog estimate.
        foreach (var fields in new[]
        {
            "\"cost\":null,\"is_byok\":false,\"cost_details\":null", "\"cost\":0.1,\"is_byok\":true,\"cost_details\":null", "\"cost\":-1,\"is_byok\":false",
            "\"cost\":0.1", "\"cost\":\"0.1\",\"is_byok\":false", "\"cost\":0.1,\"is_byok\":true,\"cost_details\":{\"upstream_inference_cost\":null,\"upstream_inference_prompt_cost\":0.6}",
        })
        {
            var cost = await Cost(fields);
            Check(cost.GetProperty("totalFromProvider").ValueKind == JsonValueKind.False && cost.GetProperty("total").GetRawText() == "0",
                "Invalid accounting replaced the estimate: " + fields + " " + cost.GetRawText());
        }
        // anthropic-messages.ts message_delta: a model whose base URL names openrouter.ai reports the billed total.
        var row = JsonNode.Parse(CatalogRow("anthropic", "claude-haiku-5-5").ToString())!;
        row["baseUrl"] = "https://openrouter.ai/api";
        var anthropic = await S150AnthropicUsage(JsonData.Parse(row.ToJsonString()), new("https://openrouter.ai/api/"), new { output_tokens = 1000, cost = 0.25, is_byok = false });
        Reported(S150Cost(anthropic), "0.25");
    }

    // ---------------------------------------------------------------- pi-messages responseModel, Completions block order

    private static async Task S150PiMessagesResponseModel()
    {
        // pi-messages.ts: done and error events carrying responseModel set it on the partial, so the final message reports it.
        var model = new ModelDescriptor("inert", "pi-messages", "authored");
        var metadata = JsonData.Parse("""{"id":"inert","api":"pi-messages","provider":"authored","baseUrl":"https://pi-messages.invalid/v1"}""");
        const string usage = """{"input":1,"output":1,"cacheRead":0,"cacheWrite":0,"totalTokens":2,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"total":0}}""";
        foreach (var (terminal, reason) in new[]
        {
            ("{\"type\":\"done\",\"reason\":\"stop\",\"usage\":" + usage + ",\"responseModel\":\"virtual/actual-model\"}", StopReason.Stop),
            ("{\"type\":\"error\",\"reason\":\"error\",\"usage\":" + usage + ",\"errorMessage\":\"boom\",\"responseModel\":\"virtual/actual-model\"}", StopReason.Error),
        })
        {
            var body = string.Concat(new[]
            {
                """{"type":"start"}""", """{"type":"text_start","contentIndex":0}""", """{"type":"text_delta","contentIndex":0,"delta":"ok"}""",
                """{"type":"text_end","contentIndex":0,"content":"ok"}""", terminal,
            }.Select(frame => "data: " + frame + "\n\n"));
            using var handler = new Handler(_ => Task.FromResult(EventStream(body)));
            using var client = new HttpClient(handler);
            var transport = new PiMessagesHttpSseTransport(client, model, new PiMessagesOptions(metadata, Key) { EnvironmentLookup = _ => null });
            var result = await new ChatClient(transport).CompleteAsync(new(model, [Ask], 1)).WaitAsync(Deadline);
            Equal(reason, result.Message.StopReason);
            Equal("virtual/actual-model", PiWireJson.WriteMessage(result.Message).Value.GetProperty("responseModel").GetString());
        }
    }

    private static async Task S150CompletionsThinkingFirst()
    {
        // openai-completions.ts (63035734f) reads a chunk's reasoning before its content: a proxy such as LiteLLM may send both in one
        // delta, and the thinking block comes first.
        var model = new ModelDescriptor("gpt-fixture", "openai-completions", "openai");
        var stream = Frames(S150CompletionsChunk("""{"role":"assistant","content":"Hi","reasoning_content":"plan"}"""),
            S150CompletionsChunk("""{"content":" there","reasoning_content":" more"}"""), S150CompletionsChunk("{}", "stop"));
        using var handler = new Handler(_ => Task.FromResult(Sse(null, stream)));
        using var provider = NativeProviderFactory.CreateCompletions(model, new("https://api.openai.com/v1/chat/completions"), Key, null, null, handler);
        var result = await new ChatClient(provider).CompleteAsync(new(model, [Ask], 1)).WaitAsync(Deadline);
        Check(result.Message.Content is [ThinkingContent { Thinking: "plan more" }, TextContent { Text: "Hi there" }],
            "Completions content order differs: " + PiWireJson.WriteMessage(result.Message));
    }
}
