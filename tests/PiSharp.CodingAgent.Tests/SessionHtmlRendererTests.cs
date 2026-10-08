using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.CodingAgent.Export;
using PiSharp.Contracts;
using PiSharp.Sessions.Serialization;

internal static class SessionHtmlRendererTests
{
    internal const string Prefix = "session HTML export ";
    private const string Time = "2024-01-01T00:00:00.000Z";
    private static readonly SessionEntryCodec Codec = new();
    private static readonly SessionEntry Header = Codec.Parse("""
        {"type":"session","version":3,"id":"fixture","timestamp":"2024-01-01T00:00:00.000Z","cwd":"C:/fixture","parentSession":"parent.jsonl"}
        """);
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        (Prefix + "selected ancestry retains all branch metadata and complete JSONL", Branches),
        (Prefix + "assistant tool calls results errors thinking and hidden custom text remain inspectable", ToolsAndContent),
        (Prefix + "hostile transcript identifiers and script-closing strings remain inert UTF8 text", EncodingAndDeterminism),
        (Prefix + "explicit null leaf and empty session do not select physical history", EmptySelection),
        (Prefix + "invalid complete forests and absent leaves fail without partial result", Admission),
        (Prefix + "exact UTF8 output input entry limits and caller cancellation are bounded", BoundsAndCancellation),
        (Prefix + "selector budget and cumulative overflow stop admission before later entries", InputAdmissionOrder)
    ];

    private static Task Branches()
    {
        var root = User("root", null, "root prompt");
        var left = User("left", "root", "selected left");
        var right = User("right", "root", "other right");
        var label = Entry("label", "label", "right", "\"targetId\":\"left\",\"label\":\"global label\"");
        var compaction = Entry("compaction", "compact", "left", "\"summary\":\"compacted summary\",\"firstKeptEntryId\":\"compact\",\"tokensBefore\":12");
        var info = Entry("session_info", "info", "compact", "\"name\":\"  Named session  \"");
        ImmutableArray<SessionEntry> entries = [root, left, right, label, compaction, info];
        var html = new SessionHtmlRenderer().Render(new(Header, entries, "info")).Html;
        var selected = Between(html, "<section id=\"selected-transcript\">", "</section>");
        Contains(selected, "root prompt"); Contains(selected, "selected left"); Contains(selected, "compacted summary");
        Check(!selected.Contains("other right", StringComparison.Ordinal), "Sibling leaked into selected transcript.");
        Contains(html, "<title>Named session</title>"); Contains(html, "; label global label");
        Contains(html, "<a href=\"#entry-0\">root</a>"); Contains(html, "parent <a href=\"#entry-1\">left</a>");
        Contains(html, "<dt>Parent session</dt><dd>parent.jsonl</dd>"); Contains(html, "; selected leaf");
        // Viewer ancestry remains visible across compaction; this is not an LLM context export.
        var archive = WebUtility.HtmlDecode(Between(html, "<pre id=\"session-jsonl\">", "</pre>"))!;
        var lines = archive.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Check(lines.Length == entries.Length + 1, "Archive omitted physical entries or the header.");
        Equal("fixture", Codec.Parse(lines[0]).Id);
        Check(lines.Skip(1).Select(line => Codec.Parse(line).Id).SequenceEqual(entries.Select(entry => entry.Id)), "Archive reordered branches.");
        return Task.CompletedTask;
    }

    private static Task ToolsAndContent()
    {
        const string usage = """{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"totalTokens":0,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"total":0}}""";
        var assistant = Entry("message", "assistant", null,
            "\"message\":{\"role\":\"assistant\",\"timestamp\":1,\"content\":[{\"type\":\"text\",\"text\":\"answer\"}," +
            "{\"type\":\"thinking\",\"thinking\":\"reasoning\"},{\"type\":\"toolCall\",\"id\":\"call\",\"name\":\"custom.lookup\",\"arguments\":{\"query\":\"<query>\"}}]," +
            "\"api\":\"fixture\",\"provider\":\"fixture\",\"model\":\"fixture\",\"usage\":" + usage + ",\"stopReason\":\"toolUse\"}");
        var result = Entry("message", "result", "assistant", """
            "message":{"role":"toolResult","timestamp":2,"toolName":"custom.lookup","toolCallId":"call","isError":true,"content":[{"type":"text","text":"<failure>"}],"details":{"opaque":1.00e400}}
            """);
        var custom = Entry("custom_message", "custom", "result", """
            "customType":"hidden.fixture","display":false,"content":[{"type":"text","text":"hidden text"},{"type":"image","mimeType":"image/svg+xml","data":"PHN2Zz4="}]
            """);
        var summary = Entry("branch_summary", "summary", "custom", "\"fromId\":\"old\",\"summary\":\"abandoned text\"");
        var html = new SessionHtmlRenderer().Render(new(Header, [assistant, result, custom, summary], "summary")).Html;
        Contains(html, "Tool call: custom.lookup (call call)"); Contains(html, "&lt;query&gt;");
        Contains(html, "&lt;failure&gt;"); Contains(html, "<dt>isError</dt><dd>true</dd>");
        Contains(html, "<summary>Thinking</summary><pre><code>reasoning</code></pre>"); Contains(html, "(hidden in terminal)");
        Contains(html, "hidden text"); Contains(html, "abandoned text"); Contains(html, "1.00e400");
        Contains(html, "Image omitted from display: image/svg+xml"); Check(!html.Contains("<img", StringComparison.Ordinal), "Image was activated.");
        return Task.CompletedTask;
    }

    private static Task EncodingAndDeterminism()
    {
        const string hostile = "\r\n</script><script>alert('x')</script><img src=\"https://evil.invalid/\" onerror=\"run()\">& \u03c0 \U0001f600";
        var id = "\" onclick=\"run()\" <leaf>";
        var user = User(id, null, hostile);
        var tools = JsonData.Parse("[{\"name\":\"<tool>\",\"parameters\":{\"description\":" + JsonSerializer.Serialize(hostile) + "}}]");
        var snapshot = new SessionHtmlSnapshot(Header, [user], id, hostile, tools);
        var raw = user.WireBody.ToString(); var renderer = new SessionHtmlRenderer();
        var result = renderer.Render(snapshot); var again = renderer.Render(snapshot);
        Equal(result.Html, again.Html); Check(result.Utf8Bytes.SequenceEqual(again.Utf8Bytes), "UTF8 output was nondeterministic.");
        Equal(result.Html, new UTF8Encoding(false, true).GetString(result.Utf8Bytes.AsSpan()));
        Check(!result.Utf8Bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }), "Export has a BOM.");
        Contains(result.Html, WebUtility.HtmlEncode(hostile)!.Replace("\r", "&#13;", StringComparison.Ordinal)); Contains(result.Html, "id=\"entry-0\"");
        Contains(result.Html, "<pre><code>&#13;\n&lt;/script&gt;");
        Contains(result.Html, "default-src 'none'"); Contains(result.Html, "Captured tool definitions");
        foreach (var forbidden in new[] { "<script", "</script", "<img", "<iframe", "<link", " src=\"", " onclick=\"" })
            Check(!result.Html.Contains(forbidden, StringComparison.OrdinalIgnoreCase), "Transcript activated markup: " + forbidden);
        Equal(raw, user.WireBody.ToString());
        var archive = WebUtility.HtmlDecode(Between(result.Html, "<pre id=\"session-jsonl\">", "</pre>"))!;
        var restored = Codec.Parse(archive.Split('\n', StringSplitOptions.RemoveEmptyEntries)[1]);
        Equal(hostile, restored.WireBody.Value.GetProperty("message").GetProperty("content").GetString());
        return Task.CompletedTask;
    }

    private static Task EmptySelection()
    {
        var renderer = new SessionHtmlRenderer();
        var html = renderer.Render(new(Header, [User("physical", null, "history")], null)).Html;
        Contains(html, "<dt>Selected leaf</dt><dd>(none)</dd>"); Contains(html, "<dt>Physical leaf</dt><dd>physical</dd>");
        Check(!Between(html, "<section id=\"selected-transcript\">", "</section>").Contains("history", StringComparison.Ordinal), "Null leaf defaulted to physical leaf.");
        Contains(html, "Other branch entries");
        var empty = renderer.Render(new(Header, [], null)); Equal(0, empty.EntryCount); Contains(empty.Html, "No selected entries.");
        return Task.CompletedTask;
    }

    private static Task Admission()
    {
        var renderer = new SessionHtmlRenderer(); var root = User("root", null, "text");
        Fails(SessionHtmlRenderFailure.InvalidSnapshot, () => renderer.Render(new(root, [], null)));
        Fails(SessionHtmlRenderFailure.InvalidSnapshot, () => renderer.Render(new(Header, default, null)));
        Fails(SessionHtmlRenderFailure.InvalidSnapshot, () => renderer.Render(new(Header, [root, root], "root")));
        Fails(SessionHtmlRenderFailure.InvalidSnapshot, () => renderer.Render(new(Header, [User("orphan", "missing", "text")], "orphan")));
        Fails(SessionHtmlRenderFailure.InvalidSnapshot, () => renderer.Render(new(Header, [User("loop", "loop", "text")], "loop")));
        Fails(SessionHtmlRenderFailure.MissingLeaf, () => renderer.Render(new(Header, [root], "absent")));
        Fails(SessionHtmlRenderFailure.InvalidSnapshot, () => renderer.Render(new(Header, [root], "root", "\ud800")));
        Fails(SessionHtmlRenderFailure.InvalidSnapshot, () => renderer.Render(new(Header, [root], "root", Tools: JsonData.Parse("{}"))));
        var future = Entry("message", "future", null, "\"message\":{\"role\":\"future-role\",\"content\":{\"inert\":\"<future>\"}}");
        Contains(renderer.Render(new(Header, [future], "future")).Html, "&lt;future&gt;");
        return Task.CompletedTask;
    }

    private static Task BoundsAndCancellation()
    {
        var user = User("root", null, "\u03c0\U0001f600<&"); var snapshot = new SessionHtmlSnapshot(Header, [user], "root");
        var normal = new SessionHtmlRenderer().Render(snapshot);
        Equal(normal.Html, new SessionHtmlRenderer(new(MaximumOutputBytes: normal.Utf8Bytes.Length)).Render(snapshot).Html);
        Fails(SessionHtmlRenderFailure.ResourceLimit, () => new SessionHtmlRenderer(new(MaximumOutputBytes: normal.Utf8Bytes.Length - 1)).Render(snapshot));
        var input = Header.WireBody.ToString().Length + user.WireBody.ToString().Length + snapshot.LeafId!.Length;
        new SessionHtmlRenderer(new(MaximumInputCharacters: input)).Render(snapshot);
        Fails(SessionHtmlRenderFailure.ResourceLimit, () => new SessionHtmlRenderer(new(MaximumInputCharacters: input - 1)).Render(snapshot));
        Fails(SessionHtmlRenderFailure.ResourceLimit, () => new SessionHtmlRenderer(new(MaximumEntries: 1)).Render(snapshot with { Entries = [user, User("tail", "root", "text")] }));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { new SessionHtmlRenderer().Render(snapshot, canceled.Token); throw new InvalidOperationException("Canceled render completed."); }
        catch (OperationCanceledException error) { Equal(canceled.Token, error.CancellationToken); }
        return Task.CompletedTask;
    }

    private static Task InputAdmissionOrder()
    {
        var headerCharacters = Header.WireBody.ToString().Length;
        var missing = new SessionHtmlSnapshot(Header, [], "absent");
        // A selector exactly inside the budget reaches lookup; one character over never does.
        Fails(SessionHtmlRenderFailure.MissingLeaf, () => new SessionHtmlRenderer(new(MaximumInputCharacters: headerCharacters + 6)).Render(missing));
        Fails(SessionHtmlRenderFailure.ResourceLimit, () => new SessionHtmlRenderer(new(MaximumInputCharacters: headerCharacters + 5)).Render(missing));
        Fails(SessionHtmlRenderFailure.ResourceLimit, () => new SessionHtmlRenderer(new(MaximumInputCharacters: headerCharacters))
            .Render(missing with { LeafId = new string('x', 4096) }));
        // The null sentinel would produce InvalidSnapshot if admission inspected it after overflow.
        Fails(SessionHtmlRenderFailure.ResourceLimit, () => new SessionHtmlRenderer(new(MaximumInputCharacters: headerCharacters))
            .Render(new(Header, [null!], "x")));
        var first = User("root", null, "record over budget");
        Fails(SessionHtmlRenderFailure.ResourceLimit, () => new SessionHtmlRenderer(new(MaximumInputCharacters: headerCharacters + first.WireBody.ToString().Length - 1))
            .Render(new(Header, [first, null!], null)));
        var metadata = new SessionHtmlSnapshot(Header, [first], "root", "prompt", JsonData.Parse("[]"));
        var exact = headerCharacters + first.WireBody.ToString().Length + metadata.LeafId!.Length + metadata.SystemPrompt!.Length + metadata.Tools!.ToString().Length;
        new SessionHtmlRenderer(new(MaximumInputCharacters: exact)).Render(metadata);
        Fails(SessionHtmlRenderFailure.ResourceLimit, () => new SessionHtmlRenderer(new(MaximumInputCharacters: exact - 1)).Render(metadata));
        return Task.CompletedTask;
    }

    private static SessionEntry User(string id, string? parent, string text) => Entry("message", id, parent,
        "\"message\":{\"role\":\"user\",\"timestamp\":1,\"content\":" + JsonSerializer.Serialize(text) + "}");
    private static SessionEntry Entry(string type, string id, string? parent, string fields) => Codec.Parse(
        "{\"type\":" + JsonSerializer.Serialize(type) + ",\"id\":" + JsonSerializer.Serialize(id) + ",\"parentId\":" +
        JsonSerializer.Serialize(parent) + ",\"timestamp\":\"" + Time + "\"," + fields + "}");
    private static string Between(string text, string start, string end)
    {
        var first = text.IndexOf(start, StringComparison.Ordinal); Check(first >= 0, "Missing section start."); first += start.Length;
        var last = text.IndexOf(end, first, StringComparison.Ordinal); Check(last >= 0, "Missing section end."); return text[first..last];
    }
    private static void Contains(string text, string expected) => Check(text.Contains(expected, StringComparison.Ordinal), "Missing expected export content: " + expected);
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), "Values differ.");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Fails(SessionHtmlRenderFailure expected, Action action)
    { try { action(); } catch (SessionHtmlRenderException error) { Equal(expected, error.Failure); return; } throw new InvalidOperationException("Expected HTML render failure."); }
}
