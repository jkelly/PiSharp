using System.Text;
using System.Text.Json;
using PiSharp.Sessions.Serialization;

internal static class SessionEntryCodecTests
{
    private const string Usage = """{"input":1,"output":2,"cacheRead":3,"cacheWrite":4,"totalTokens":10,"cost":{"input":0.01,"output":0.02,"cacheRead":0.03,"cacheWrite":0.04,"total":0.10}}""";
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("session codec current header and all eleven entry kinds round-trip", RecordInventory),
        ("session codec known message/content variants retain wire meaning", MessageVariants),
        ("session codec opaque future data and document ownership remain lossless", OpaqueOwnership),
        ("session codec absent/null distinctions and context replacement shape", MissingAndNull),
        ("session codec rejects permissive element syntax malformed records and duplicates", MalformedAndDuplicate),
        ("session codec native integer profile and opaque numeric tokens", NumberProfile),
        ("session codec exact character UTF8 depth boundaries and cross-codec limits", Limits),
        ("session codec strict UTF8 and paired Unicode retain strings", UnicodeProfile)
    ];

    public static Task RecordInventory()
    {
        var codec = new SessionEntryCodec();
        var header = codec.Parse("""{"type":"session","version":3,"id":"compatible-id","timestamp":"header-time","cwd":"C:\\project","parentSession":null,"future":{"a":null}}""");
        Equal(SessionEntryKind.Header, header.Kind); Check(header.IsHeader, "Header entered the tree profile.");
        Equal("compatible-id", header.Id); Equal("header-time", header.Timestamp); Equal<string?>(null, header.ParentId);
        RoundTrip(codec, header);
        var examples = new (SessionEntryKind Kind, string Type, string Fields)[]
        {
            (SessionEntryKind.Message, "message", "\"message\":" + Message("user", "\"content\":\"hello\"")),
            (SessionEntryKind.ThinkingLevelChange, "thinking_level_change", "\"thinkingLevel\":\"future-level\""),
            (SessionEntryKind.ModelChange, "model_change", "\"provider\":\"future-provider\",\"modelId\":\"virtual/router\""),
            (SessionEntryKind.Usage, "usage", "\"kind\":\"unknown-kind\",\"provider\":\"p\",\"model\":\"m\",\"usage\":" + Usage),
            (SessionEntryKind.Compaction, "compaction", "\"summary\":\"summary\",\"firstKeptEntryId\":\"entry/id\",\"tokensBefore\":50000,\"details\":null,\"fromHook\":false"),
            (SessionEntryKind.BranchSummary, "branch_summary", "\"fromId\":\"old/branch\",\"summary\":\"branch\",\"usage\":" + Usage),
            (SessionEntryKind.Custom, "custom", "\"customType\":\"extension/state\",\"data\":{\"x\":null}"),
            (SessionEntryKind.CustomMessage, "custom_message", "\"customType\":\"extension/message\",\"content\":\"hidden context\",\"display\":false"),
            (SessionEntryKind.ContextEdit, "context_edit", "\"targetId\":\"old/id\",\"replacement\":null"),
            (SessionEntryKind.Label, "label", "\"targetId\":\"old/id\",\"label\":\"checkpoint\""),
            (SessionEntryKind.SessionInfo, "session_info", "\"name\":\"name\"")
        };
        foreach (var example in examples)
        {
            var parsed = codec.Parse(Entry(example.Type, example.Fields, "\"parent/non-guid\""));
            Equal(example.Kind, parsed.Kind); Equal(example.Type, parsed.Type); Equal("entry/id", parsed.Id);
            Equal("parent/non-guid", parsed.ParentId); Equal("entry-time", parsed.Timestamp);
            Check(!parsed.IsHeader, "Tree record became a header."); RoundTrip(codec, parsed);
        }
        return Task.CompletedTask;
    }

    public static Task MessageVariants()
    {
        var codec = new SessionEntryCodec();
        var bodies = new[]
        {
            Message("system", Fields("""{"content":[{"type":"text","text":"base"}],"sections":{"removed":null,"opaque":"section"},"toolsAdded":[{"name":"tool","parameters":{"type":"object"}}]}""")),
            Message("user", Fields("""{"content":[{"type":"text","text":"prompt"},{"type":"image","data":"opaque-base64","mimeType":"image/png","unknown":null}]}""")),
            Message("toolResult", Fields("""{"toolCallId":"call/id","toolName":"tool","content":[{"type":"text","text":"result"},{"type":"image","data":"image","mimeType":"image/jpeg"}],"isError":false,"details":null,"nestedCalls":{"future":true}}""")),
            Message("bashExecution", Fields("""{"command":"echo example","output":"output","cancelled":false,"truncated":true,"fullOutputPath":"unopened/path"}""")),
            Message("bashExecution", Fields("""{"command":"cmd","output":"","exitCode":null,"cancelled":true,"truncated":false}""")),
            Message("custom", Fields("""{"customType":"extension","content":"injected","display":false,"details":null}""")),
            Message("branchSummary", Fields("""{"summary":"summary","fromId":null}""")),
            Message("compactionSummary", Fields("""{"summary":"summary","tokensBefore":12}"""))
        };
        foreach (var body in bodies) RoundTrip(codec, codec.Parse(Entry("message", "\"message\":" + body)));
        foreach (var stop in new[] { "pending", "stop", "length", "toolUse", "error", "aborted", "deferred" })
        {
            var body = Message("assistant", Fields("""{"content":[{"type":"text","text":"answer","textSignature":"opaque"},{"type":"thinking","thinking":"","thinkingSignature":"encrypted","redacted":true},{"type":"toolCall","id":"call/id","name":"tool","arguments":{"large":1e400},"thoughtSignature":"signed","namespace":"opaque"}],"api":"future-api","provider":"p","model":"m","responseId":null}""") + ",\"usage\":" + Usage + ",\"stopReason\":" + JsonSerializer.Serialize(stop));
            RoundTrip(codec, codec.Parse(Entry("message", "\"message\":" + body)));
        }
        RoundTrip(codec, codec.Parse(Entry("custom_message", Fields("""{"customType":"x","content":[{"type":"image","data":"image","mimeType":"image/png"}],"display":true}"""))));
        RoundTrip(codec, codec.Parse(Entry("context_edit", Fields("""{"targetId":"target","replacement":{"content":[{"type":"thinking","thinking":"replaced"},{"type":"toolCall","id":"call","name":"tool","arguments":{}}],"opaque":null}}"""))));
        return Task.CompletedTask;
    }

    public static Task OpaqueOwnership()
    {
        var codec = new SessionEntryCodec();
        var raw = Entry("future_record", Fields("""{"opaque":{"huge":1234567890123456789012345678901234567890,"overflow":1e400,"negativeZero":-0,"decimal":0.123456789012345678901234567890123456789,"array":[null,{"v":"\\\"\n"}]}}"""));
        SessionEntry owned;
        using (var document = JsonDocument.Parse(raw)) owned = codec.Read(document.RootElement);
        Equal(SessionEntryKind.Unknown, owned.Kind); Equal(raw, owned.WireBody.ToString());
        RoundTrip(codec, owned); Equal(raw, codec.Serialize(owned));
        var role = codec.Parse(Entry("message", Fields("""{"message":{"role":"extension/future","arbitrary":null,"number":1e400}}""")));
        RoundTrip(codec, role);
        var block = codec.Parse(Entry("message", "\"message\":" + Message("user", Fields("""{"content":[{"type":"future_content","value":{"signature":null}}]}"""))));
        RoundTrip(codec, block);
        var checkpoint = codec.Parse(Entry("compaction", Fields("""{"summary":"s","firstKeptEntryId":"self","tokensBefore":0,"systemMessage":{"role":"system","content":"checkpoint","sections":{"remove":null},"timestamp":0},"usage":null}""")));
        RoundTrip(codec, checkpoint);
        return Task.CompletedTask;
    }

    public static Task MissingAndNull()
    {
        var codec = new SessionEntryCodec();
        foreach (var pair in new[] { ("custom", "data", "\"customType\":\"x\""), ("label", "label", "\"targetId\":\"target\""), ("session_info", "name", "") })
        {
            var absent = codec.Parse(Entry(pair.Item1, pair.Item3));
            var present = codec.Parse(Entry(pair.Item1, (pair.Item3.Length == 0 ? "" : pair.Item3 + ",") + JsonSerializer.Serialize(pair.Item2) + ":null"));
            Check(!absent.WireBody.Value.TryGetProperty(pair.Item2, out _), "Absent optional field appeared.");
            Equal(JsonValueKind.Null, present.WireBody.Value.GetProperty(pair.Item2).ValueKind);
            RoundTrip(codec, absent); RoundTrip(codec, present);
        }
        var omit = codec.Parse(Entry("context_edit", "\"targetId\":\"target\",\"replacement\":null"));
        Equal(JsonValueKind.Null, omit.WireBody.Value.GetProperty("replacement").ValueKind);
        RoundTrip(codec, codec.Parse(Entry("context_edit", Fields("""{"targetId":"target","replacement":{"content":"new text"}}"""))));
        Fails(SessionEntryCodecFailure.InvalidRecord, () => codec.Parse(Entry("context_edit", "\"targetId\":\"target\"")));
        Fails(SessionEntryCodecFailure.InvalidRecord, () => codec.Parse(Entry("context_edit", "\"targetId\":\"target\",\"replacement\":\"text\"")));
        Fails(SessionEntryCodecFailure.InvalidRecord, () => codec.Parse(Entry("context_edit", "\"targetId\":\"target\",\"replacement\":{}")));
        Fails(SessionEntryCodecFailure.InvalidRecord, () => codec.Parse(Entry("custom", "\"customType\":\"x\"").Replace("\"parentId\":null,", "", StringComparison.Ordinal)));
        Fails(SessionEntryCodecFailure.InvalidRecord, () => codec.Parse(Entry("message", "\"message\":null")));
        return Task.CompletedTask;
    }

    public static Task MalformedAndDuplicate()
    {
        var codec = new SessionEntryCodec();
        foreach (var raw in new[] { "", "{", "{}{}", "{\"type\":}", "/*comment*/{}", "{\"type\":\"custom\",}", "{\"type\":\"secret-private-payload\"}\n{" })
            Fails(SessionEntryCodecFailure.MalformedJson, () => codec.Parse(raw));
        var permissiveSyntax = new[]
        {
            Entry("custom", "\"customType\":\"x\"")[..^1] + ",}",
            Entry("custom", "\"customType\":\"x\",\"data\":{\"nested\":[1,],}"),
            Entry("future_record", "\"opaque\":{\"nested\":[{\"value\":1,}],}"),
            Entry("custom", "/*secret-private-payload*/\"customType\":\"x\""),
            Entry("custom", "\"customType\":\"x\",\"data\":{\"nested\":[/*secret-private-payload*/1]}"),
            Entry("future_record", "\"opaque\":{\"nested\":/*secret-private-payload*/{\"value\":1}}")
        };
        foreach (var raw in permissiveSyntax)
        {
            using var permissiveDocument = JsonDocument.Parse(raw, new JsonDocumentOptions
            { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            Fails(SessionEntryCodecFailure.MalformedJson, () => codec.Read(permissiveDocument.RootElement));
            Fails(SessionEntryCodecFailure.MalformedJson, () => codec.Parse(raw));
        }
        foreach (var raw in new[] { "null", "[]", "1", "{}", Entry("custom"), Entry("usage", "\"kind\":\"k\",\"provider\":\"p\",\"model\":\"m\",\"usage\":[]"),
            Entry("custom_message", "\"customType\":\"x\",\"content\":null,\"display\":true"),
            Entry("custom_message", "\"customType\":\"x\",\"content\":\"text\",\"display\":1"),
            Entry("message", "\"message\":" + Message("system", Fields("""{"content":[{"type":"image","data":"x","mimeType":"x"}]}"""))),
            Entry("message", "\"message\":" + Message("assistant", "\"content\":\"text\"")),
            Entry("message", "\"message\":" + Message("user", "\"content\":[1]")),
            Entry("message", "\"message\":" + Message("user", Fields("""{"content":[{"type":"text"}]}"""))) })
            Fails(SessionEntryCodecFailure.InvalidRecord, () => codec.Parse(raw));
        foreach (var fields in new[] { "\"id\":\"duplicate\"", "\"customType\":\"x\",\"data\":{\"secret-private-payload\":1,\"secret-private-payload\":2}",
            "\"customType\":\"x\",\"data\":[{\"a\":1,\"\\u0061\":2}]" })
        {
            var raw = Entry("custom", fields);
            Fails(SessionEntryCodecFailure.DuplicateProperty, () => codec.Parse(raw));
            using var document = JsonDocument.Parse(raw);
            Fails(SessionEntryCodecFailure.DuplicateProperty, () => codec.Read(document.RootElement));
        }
        foreach (var version in new[] { "", ",\"version\":null", ",\"version\":1", ",\"version\":2", ",\"version\":4", ",\"version\":3.0", ",\"version\":\"3\"" })
            Fails(SessionEntryCodecFailure.UnsupportedVersion, () => codec.Parse("{\"type\":\"session\",\"id\":\"id\",\"timestamp\":\"time\",\"cwd\":\"cwd\"" + version + "}"));
        Fails(SessionEntryCodecFailure.InvalidRecord, () => codec.Read(default));
        return Task.CompletedTask;
    }

    public static Task NumberProfile()
    {
        var codec = new SessionEntryCodec();
        foreach (var timestamp in new[] { "-9223372036854775808", "9223372036854775807", "9007199254740993", "-0" })
        {
            var raw = Entry("message", "\"message\":" + Message("user", "\"content\":\"x\"", timestamp));
            Equal(timestamp, codec.Parse(raw).WireBody.Value.GetProperty("message").GetProperty("timestamp").GetRawText());
            Equal(raw, codec.Serialize(codec.Parse(raw)));
        }
        foreach (var timestamp in new[] { "9223372036854775808", "-9223372036854775809", "1.0", "1e3", "1e400" })
            Fails(SessionEntryCodecFailure.UnsupportedInteger, () => codec.Parse(Entry("message", "\"message\":" + Message("user", "\"content\":\"x\"", timestamp))));
        Fails(SessionEntryCodecFailure.InvalidRecord, () => codec.Parse(Entry("message", "\"message\":" + Message("user", "\"content\":\"x\"", "null"))));
        Fails(SessionEntryCodecFailure.UnsupportedInteger, () => codec.Parse(Entry("compaction", "\"summary\":\"x\",\"firstKeptEntryId\":\"id\",\"tokensBefore\":-1")));
        var usage = Usage.Replace("\"input\":1", "\"input\":9223372036854775807", StringComparison.Ordinal)
            .Replace("0.01", "1e400", StringComparison.Ordinal).Replace("0.02", "-0", StringComparison.Ordinal)
            .Replace("0.03", "0.123456789012345678901234567890123456789", StringComparison.Ordinal);
        var record = codec.Parse(Entry("usage", "\"kind\":\"opaque-kind\",\"provider\":\"p\",\"model\":\"m\",\"usage\":" + usage));
        RoundTrip(codec, record);
        Equal("1e400", record.WireBody.Value.GetProperty("usage").GetProperty("cost").GetProperty("input").GetRawText());
        return Task.CompletedTask;
    }

    public static Task Limits()
    {
        var raw = Entry("custom", "\"customType\":\"x\",\"data\":{\"array\":[{}]}" ); // root/object/array/object = four containers
        var exact = new SessionEntryCodec(new(raw.Length, Encoding.UTF8.GetByteCount(raw), 4));
        RoundTrip(exact, exact.Parse(raw)); exact.ParseUtf8(Encoding.UTF8.GetBytes(raw));
        Fails(SessionEntryCodecFailure.CharacterLimit, () => exact.Parse(raw + " "));
        var smallerBytes = new SessionEntryCodec(new(raw.Length, Encoding.UTF8.GetByteCount(raw) - 1, 4));
        Fails(SessionEntryCodecFailure.Utf8ByteLimit, () => smallerBytes.ParseUtf8(Encoding.UTF8.GetBytes(raw)));
        var shallower = new SessionEntryCodec(new(raw.Length, Encoding.UTF8.GetByteCount(raw), 3));
        Fails(SessionEntryCodecFailure.DepthLimit, () => shallower.Parse(raw));
        using var document = JsonDocument.Parse(raw);
        Fails(SessionEntryCodecFailure.DepthLimit, () => shallower.Read(document.RootElement));
        Fails(SessionEntryCodecFailure.DepthLimit, () => shallower.Serialize(exact.Read(document.RootElement)));
        var smaller = new SessionEntryCodec(new(raw.Length - 1));
        Fails(SessionEntryCodecFailure.CharacterLimit, () => smaller.Read(document.RootElement));
        Fails(SessionEntryCodecFailure.CharacterLimit, () => smaller.Serialize(exact.Parse(raw)));
        var whitespace = " \r\n" + raw + "\r\n ";
        Equal(raw, new SessionEntryCodec().Serialize(new SessionEntryCodec().Parse(whitespace)));
        var quoted = Entry("custom", Fields("""{"customType":"x","data":"[[[[ { \\\" }"}"""));
        new SessionEntryCodec(new(MaximumJsonDepth: 1)).Parse(quoted);
        // JsonData.MaximumDepth (1,000) levels: JSON.parse has no limit and V8's JSON.stringify gives up at about 1,700.
        var deepest = Entry("custom", "\"customType\":\"x\",\"data\":" + new string('[', 999) + "0" + new string(']', 999));
        var maximumDepth = new SessionEntryCodec(new(MaximumRecordCharacters: 1_048_576, MaximumJsonDepth: 1000));
        RoundTrip(maximumDepth, maximumDepth.Parse(deepest));
        Fails(SessionEntryCodecFailure.DepthLimit, () => maximumDepth.Parse(Entry("custom", "\"customType\":\"x\",\"data\":" + new string('[', 1000) + "0" + new string(']', 1000))));
        foreach (var options in new[] { new SessionEntryCodecOptions(0), new SessionEntryCodecOptions(MaximumUtf8Bytes: 0),
            new SessionEntryCodecOptions(MaximumJsonDepth: 0), new SessionEntryCodecOptions(MaximumJsonDepth: 1001) })
            Throws<ArgumentOutOfRangeException>(() => new SessionEntryCodec(options));
        return Task.CompletedTask;
    }

    public static Task UnicodeProfile()
    {
        var codec = new SessionEntryCodec();
        var raw = Entry("custom", Fields("""{"customType":"x","data":{"emoji":"\uD83D\uDE00","controls":"\r\n\t","escaped":"\\\""}}""") +
            ",\"literal\":\"\u03C0\U0001F600\",\"combining\":\"e\u0301\"");
        Equal("\u03C0\U0001F600", codec.Parse(raw).WireBody.Value.GetProperty("literal").GetString());
        Equal("e\u0301", codec.Parse(raw).WireBody.Value.GetProperty("combining").GetString());
        Equal(raw, codec.Serialize(codec.Parse(raw))); RoundTrip(codec, codec.ParseUtf8(Encoding.UTF8.GetBytes(raw)));
        // session-manager.ts JSON.parse keeps an escaped lone surrogate of a string value, and JSON.stringify writes it back as its escape.
        var lone = Entry("custom", Fields("""{"customType":"x","data":["\uD800","a\udc00b"]}"""));
        Equal(lone, codec.Serialize(codec.Parse(lone)));
        Equal("a\udc00b", PiSharp.Contracts.JsonUtf16.GetString(codec.Parse(lone).WireBody.Value.GetProperty("data")[1]));
        // A lone surrogate of a name, or a raw (unescaped) lone surrogate code unit, stays outside the owned profile.
        foreach (var fields in new[] { Fields("""{"customType":"x","data":{"\uDC00":1}}"""), "\"customType\":\"x\",\"data\":\"" + '\uD800' + "\"" })
            Fails(SessionEntryCodecFailure.UnsupportedUnicode, () => codec.Parse(Entry("custom", fields)));
        Fails(SessionEntryCodecFailure.UnsupportedUnicode, () => codec.ParseUtf8(new byte[] { 0xC0, 0xAF }));
        Fails(SessionEntryCodecFailure.MalformedJson, () => codec.ParseUtf8(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(raw)).ToArray()));
        var byteCount = Encoding.UTF8.GetByteCount(raw);
        Check(byteCount > raw.Length, "Unicode probe failed to distinguish UTF-8 bytes from UTF-16 characters.");
        var codecWithExactUnicodeBytes = new SessionEntryCodec(new(raw.Length, byteCount));
        codecWithExactUnicodeBytes.ParseUtf8(Encoding.UTF8.GetBytes(raw));
        Fails(SessionEntryCodecFailure.Utf8ByteLimit, () => new SessionEntryCodec(new(raw.Length, byteCount - 1)).ParseUtf8(Encoding.UTF8.GetBytes(raw)));
        return Task.CompletedTask;
    }

    private static string Entry(string type, string fields = "", string parent = "null") =>
        "{\"type\":" + JsonSerializer.Serialize(type) + ",\"id\":\"entry/id\",\"parentId\":" + parent +
        ",\"timestamp\":\"entry-time\"" + (fields.Length == 0 ? "" : "," + fields) + "}";
    private static string Message(string role, string fields, string timestamp = "17") =>
        "{\"role\":" + JsonSerializer.Serialize(role) + ",\"timestamp\":" + timestamp + "," + fields + "}";
    private static string Fields(string jsonObject) => jsonObject[1..^1];
    private static void RoundTrip(SessionEntryCodec codec, SessionEntry entry)
    {
        var encoded = codec.Serialize(entry);
        Check(!encoded.Contains('\r') && !encoded.Contains('\n'), "Encoded record contains a line terminator.");
        var reread = codec.Parse(encoded);
        Equal(entry.Kind, reread.Kind); Equal(entry.Type, reread.Type); Equal(entry.Id, reread.Id);
        Equal(entry.ParentId, reread.ParentId); Equal(entry.Timestamp, reread.Timestamp);
        Check(JsonElement.DeepEquals(entry.WireBody.Value, reread.WireBody.Value), "Round-trip changed JSON structure.");
    }
    private static void Fails(SessionEntryCodecFailure expected, Action action)
    {
        try { action(); }
        catch (SessionEntryCodecException error)
        {
            Equal(expected, error.Failure);
            Check(!error.Message.Contains("secret-private-payload", StringComparison.Ordinal) && error.InnerException is null,
                "Failure exposed rejected input.");
            return;
        }
        throw new InvalidOperationException("Expected session record codec failure.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
}
