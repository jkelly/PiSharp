// Pi 1.5.0 (authored from the v1.5.0 sources, f9a26365568127461c47a07a37dcb42c140a3ac5): core/export-html/template.js (iterative
// session tree walks, #11225; cost totals prefer the recorded total, #9980; system message entries, #11261; read line ranges, #9887).
// The cases are modeled on the upstream tests export-html-deep-tree.test.ts, export-html-cost.test.ts,
// export-html-system-messages.test.ts and the HTML viewer part of read-line-range-display.test.ts; their expectations are authored,
// never captured from an upstream run.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jint;
using Jint.Native;
using PiSharp.CodingAgent.Export;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync150CliCases() =>
    [
        Case("sync150.html-export-walks-a-deep-session-tree-without-recursion", HtmlExportDeepTree),
        Case("sync150.html-export-cost-total-prefers-the-recorded-total", HtmlExportCostTotals),
        Case("sync150.html-export-renders-system-messages", HtmlExportSystemMessages),
        Case("sync150.html-export-read-rows-show-line-ranges", HtmlExportReadLineRange),
    ];

    private static string TemplateJs() => Encoding.UTF8.GetString(SessionHtmlExport.ReadAssetBytes("template.js"));

    private static string Iso(long milliseconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>A session file with these entries, exported; returns the page.</summary>
    private static async Task<string> ExportEntries(string name, IEnumerable<object> entries)
    {
        var root = Temp(name); var session = Path.Combine(root, "session.jsonl");
        var lines = new List<string> { JsonSerializer.Serialize(new { type = "session", version = 3, id = name, timestamp = Iso(0), cwd = root }) };
        lines.AddRange(entries.Select(entry => JsonSerializer.Serialize(entry)));
        await File.WriteAllTextAsync(session, string.Join("\n", lines) + "\n");
        return await File.ReadAllTextAsync(await SessionHtmlExport.ExportFromFileAsync(session, new(OutputPath: Path.Combine(root, "out.html")), Themes()));
    }

    /// <summary>The page's viewer script in Jint with the stubs the upstream tests give node:vm; <paramref name="patch"/> inserts the
    /// return that hands the case the viewer's functions.</summary>
    private static JsValue RunExportViewer(string html, Func<string, string> patch, out Engine engine)
    {
        var data = Regex.Match(html, "<script id=\"session-data\" type=\"application/json\">([^<]+)</script>").Groups[1].Value;
        var viewer = Regex.Matches(html, "<script>([\\s\\S]*?)</script>").Select(match => match.Groups[1].Value).LastOrDefault();
        Check(data.Length > 0 && viewer is not null, "the page has no session data or viewer script");
        var patched = patch(viewer!);
        Check(!string.Equals(patched, viewer, StringComparison.Ordinal), "the viewer script lacks the anchor the case patches");
        // A recursive walk over a deep chain fails the case instead of overflowing the process stack.
        engine = new Engine(options => options.LimitRecursion(512));
        engine.SetValue("__data", data);
        engine.SetValue("atob", new Func<string, string>(text => Encoding.Latin1.GetString(Convert.FromBase64String(text))));
        engine.Execute("""
            var document = { getElementById: function () { return { textContent: __data }; }, querySelector: function () { return null; } };
            var window = { location: { search: "" } };
            class TextDecoder {
              decode(bytes) {
                const parts = [];
                for (let i = 0; i < bytes.length; i += 4096) parts.push(String.fromCharCode.apply(null, bytes.subarray(i, i + 4096)));
                return decodeURIComponent(escape(parts.join("")));
              }
            }
            class URLSearchParams { constructor() {} get() { return null; } }
            """);
        return engine.Evaluate(patched);
    }

    /// <summary>The template.js source from <paramref name="startMarker"/> up to <paramref name="endMarker"/>, as the upstream tests slice it.</summary>
    private static string TemplateSlice(string startMarker, string endMarker)
    {
        var template = TemplateJs();
        var start = template.IndexOf(startMarker, StringComparison.Ordinal);
        var end = template.IndexOf(endMarker, StringComparison.Ordinal);
        Check(start >= 0 && end > start, $"template.js has no {startMarker} … {endMarker} slice");
        return template[start..end];
    }

    private static void CloseTo(double expected, double actual, string what) =>
        Check(Math.Abs(expected - actual) < 0.005, $"{what}: expected about {expected.ToString(CultureInfo.InvariantCulture)}, got {actual.ToString(CultureInfo.InvariantCulture)}");

    // export-html-deep-tree.test.ts (#11225): the page's own tree functions build, flatten, follow and walk a single-child chain of
    // 20,000 entries, deeper than a recursive walk could go.
    private static async Task HtmlExportDeepTree()
    {
        const int depth = 20_000;
        var html = await ExportEntries("export-deep", Enumerable.Range(0, depth).Select(i => (object)new
        {
            type = "message", id = $"e{i}", parentId = i == 0 ? null : $"e{i - 1}", timestamp = Iso(i * 1000L),
            message = new { role = "user", content = "x", timestamp = i * 1000L }
        }));
        var api = RunExportViewer(html, viewer => viewer.Replace("let treeNodeMap = null;",
            "let treeNodeMap = null; return { buildTree, buildActivePathIds, flattenTree, findNewestLeaf, getPath };", StringComparison.Ordinal), out var engine);
        engine.SetValue("api", api);
        var leafId = $"e{depth - 1}";
        var summary = engine.Evaluate($$"""
            (() => {
              const tree = api.buildTree();
              const flat = api.flattenTree(tree, api.buildActivePathIds("{{leafId}}"));
              const path = api.getPath("{{leafId}}");
              return JSON.stringify({ roots: tree.length, flat: flat.length, last: flat[flat.length - 1].node.entry.id,
                newest: api.findNewestLeaf("e0"), path: path.length, first: path[0].id, end: path[path.length - 1].id });
            })()
            """).AsString();
        Equal($$"""{"roots":1,"flat":{{depth}},"last":"{{leafId}}","newest":"{{leafId}}","path":{{depth}},"first":"e0","end":"{{leafId}}"}""", summary, "deep tree");
    }

    // export-html-cost.test.ts (#9980): computeStats adds each message's recorded cost.total (a provider-reported total can differ from
    // the catalog-estimated components) and falls back to the component sum for messages written before cost.total was recorded. The
    // exported page keeps the recorded totals, so its own globalStats agree.
    private static async Task HtmlExportCostTotals()
    {
        object[] costs =
        [
            new { input = 0.1, output = 0.2, cacheRead = 0, cacheWrite = 0, total = 0.9 },
            new { input = 0.4, output = 0.1, cacheRead = 0, cacheWrite = 0 }
        ];
        var slice = TemplateSlice("function computeStats(entryList)", "const globalStats = computeStats(entries);");
        var bare = JsonSerializer.Serialize(costs.Select(cost => new { type = "message", message = new { role = "assistant", content = Array.Empty<object>(), usage = new { cost } } }));
        var sliced = JsonDocument.Parse(new Engine().Evaluate(slice + "\nJSON.stringify(computeStats(" + bare + ").cost);").AsString()).RootElement;
        CloseTo(0.5, sliced.GetProperty("input").GetDouble(), "sliced input cost");
        CloseTo(0.3, sliced.GetProperty("output").GetDouble(), "sliced output cost");
        CloseTo(1.4, sliced.GetProperty("total").GetDouble(), "sliced total cost");

        var html = await ExportEntries("export-cost", costs.Select((cost, i) => (object)new
        {
            type = "message", id = $"a{i}", parentId = i == 0 ? null : $"a{i - 1}", timestamp = Iso(i * 1000L),
            message = new
            {
                role = "assistant", content = Array.Empty<object>(), api = "openai-completions", provider = "openrouter", model = "test/model",
                usage = new { input = 10, output = 5, cacheRead = 0, cacheWrite = 0, totalTokens = 15, cost }, stopReason = "stop", timestamp = i * 1000L
            }
        }));
        var page = JsonDocument.Parse(RunExportViewer(html, viewer => viewer.Replace("const globalStats = computeStats(entries);",
            "return JSON.stringify(computeStats(entries).cost);", StringComparison.Ordinal), out _).AsString()).RootElement;
        CloseTo(0.5, page.GetProperty("input").GetDouble(), "page input cost");
        CloseTo(0.3, page.GetProperty("output").GetDouble(), "page output cost");
        CloseTo(1.4, page.GetProperty("total").GetDouble(), "page total cost");
    }

    // export-html-system-messages.test.ts (#11261): system message entries are rendered, the first one on a branch as the system prompt
    // and later ones as updates, with the text the model receives (pi-ai getSystemMessageText and renderSystemMessageUpdate).
    private static async Task HtmlExportSystemMessages()
    {
        var initial = new
        {
            role = "system", content = "You are a helpful assistant.",
            sections = new Dictionary<string, string?> { ["rules"] = "<rules>\nbe concise\n</rules>", ["project_context"] = "<project_context>\nv1\n</project_context>" },
            toolsAdded = new[] { new { name = "read", description = "Read a file", parameters = new { type = "object", properties = new { } } } },
            timestamp = 1
        };
        var update = new
        {
            role = "system", content = new[] { new { type = "text", text = "Extra instructions" } },
            sections = new Dictionary<string, string?> { ["project_context"] = "<project_context>\nv2\n</project_context>", ["rules"] = null },
            toolsAdded = new[] { new { name = "bash", description = "Run a command", parameters = new { type = "object", properties = new { } } } },
            toolsRemoved = new[] { new { name = "read" } },
            timestamp = 2
        };
        object[] entries =
        [
            new { type = "message", id = "s1", parentId = (string?)null, timestamp = Iso(1), message = (object)initial },
            new { type = "message", id = "u1", parentId = (string?)"s1", timestamp = Iso(2), message = (object)new { role = "user", content = "hi", timestamp = 2 } },
            new { type = "message", id = "s2", parentId = (string?)"u1", timestamp = Iso(3), message = (object)update },
        ];
        var helpers = TemplateSlice("function systemContentText(content)", "const hasSystemEntries");
        string Helpers(object[] list, string expression) => new Engine().Evaluate(
            "const entries = " + JsonSerializer.Serialize(list) + ";\nconst byId = new Map(entries.map(entry => [entry.id, entry]));\n" + helpers + "\n" + expression).AsString();
        var results = JsonDocument.Parse(Helpers(entries, """
            JSON.stringify([getSystemMessageText(entries[0].message), renderSystemMessageUpdate(entries[2].message),
              isLeadingSystemEntry(entries[0]), isLeadingSystemEntry(entries[2]), describeSystemUpdate(entries[2].message)]);
            """)).RootElement;
        Equal("You are a helpful assistant.\n\n<rules>\nbe concise\n</rules>\n\n<project_context>\nv1\n</project_context>", results[0].GetString(), "system prompt text");
        Equal("Extra instructions\n\nUpdated system prompt section \"project_context\":\n\n<project_context>\nv2\n</project_context>\n\nRemoved system prompt section \"rules\".",
            results[1].GetString(), "system update text");
        Equal(true, results[2].GetBoolean(), "first system entry leads");
        Equal(false, results[3].GetBoolean(), "later system entry leads");
        Equal("instructions, project_context, -rules, tools +bash -read", results[4].GetString(), "update description");
        // A compaction snapshot is earlier system state.
        Equal("false", Helpers([new { type = "compaction", id = "c1", parentId = (string?)null, systemMessage = (object)initial }, entries[2]],
            "String(isLeadingSystemEntry({ id: 's2', parentId: 'c1' }));"), "system entry after a compaction leads");

        var template = TemplateJs();
        Check(Regex.IsMatch(template, @"if \(msg\.role === 'system'\) \{\s*let html = '';"), "renderEntry has no system branch");
        Check(template.Contains("renderSystemPromptBlock('System Prompt', text)", StringComparison.Ordinal), "no System Prompt block");
        Check(template.Contains("renderToolsList('Available Tools', msg.toolsAdded)", StringComparison.Ordinal), "no Available Tools list");

        // The exported page carries the system entries and its renderers draw them.
        var viewer = RunExportViewer(await ExportEntries("export-system", entries), script => script.Replace("marked.use({",
            "return { renderEntry, getTreeNodeDisplayHtml }; marked.use({", StringComparison.Ordinal), out var engine);
        engine.SetValue("viewer", viewer);
        engine.Execute("const pageEntries = " + JsonSerializer.Serialize(entries) + ";");
        var prompt = engine.Evaluate("viewer.renderEntry(pageEntries[0])").AsString();
        Check(prompt.Contains("<div class=\"system-prompt-header\">System Prompt</div>", StringComparison.Ordinal) &&
            prompt.Contains("Available Tools", StringComparison.Ordinal) && prompt.Contains("<span class=\"tool-item-name\">read</span>", StringComparison.Ordinal),
            "leading system entry: " + prompt);
        var patch = engine.Evaluate("viewer.renderEntry(pageEntries[2])").AsString();
        Check(patch.Contains("System Prompt Update: instructions, project_context, -rules, tools +bash -read", StringComparison.Ordinal) &&
            patch.Contains("Tools Added", StringComparison.Ordinal) && patch.Contains("Tools Removed", StringComparison.Ordinal), "system update entry: " + patch);
        Equal("<span class=\"tree-muted\">[system prompt]</span>", engine.Evaluate("viewer.getTreeNodeDisplayHtml(pageEntries[0])").AsString(), "tree prompt row");
        Equal("<span class=\"tree-muted\">[system update: instructions, project_context, -rules, tools +bash -read]</span>",
            engine.Evaluate("viewer.getTreeNodeDisplayHtml(pageEntries[2])").AsString(), "tree update row");
    }

    // read-line-range-display.test.ts (#9887), the HTML viewer part: read rows show the line range of string or numeric offset/limit
    // arguments, in the tree and the transcript.
    private static async Task HtmlExportReadLineRange()
    {
        var assistant = new
        {
            type = "message", id = "assistant", parentId = (string?)null, timestamp = Iso(0),
            message = new
            {
                role = "assistant", content = new[] { new { type = "toolCall", id = "call", name = "read", arguments = new { path = "notes.txt", offset = "25", limit = "13" } } },
                api = "anthropic-messages", provider = "anthropic", model = "test-model",
                usage = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0, totalTokens = 0, cost = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0, total = 0 } },
                stopReason = "toolUse", timestamp = 0
            }
        };
        var result = new
        {
            type = "message", id = "result", parentId = "assistant", timestamp = Iso(0),
            message = new { role = "toolResult", toolCallId = "call", toolName = "read", content = Array.Empty<object>(), isError = false, timestamp = 0 }
        };
        var viewer = RunExportViewer(await ExportEntries("export-read-range", [assistant, result]), script => script.Replace("marked.use({",
            "return { formatReadLineRange, getTreeNodeDisplayHtml, renderEntry }; marked.use({", StringComparison.Ordinal), out var engine);
        engine.SetValue("viewer", viewer);
        Equal("""[":25-37",":25-37",":1-13",":25","","","",""]""", engine.Evaluate("""
            JSON.stringify([[25, 13], ["25", "13"], [null, "13"], ["25", null], [undefined, undefined], ["abc", 13], [25, ""], [25, "Infinity"]]
              .map(([offset, limit]) => viewer.formatReadLineRange(offset, limit)))
            """).AsString(), "line ranges");
        engine.Execute("const assistantEntry = " + JsonSerializer.Serialize(assistant) + "; const resultEntry = " + JsonSerializer.Serialize(result) + ";");
        Equal("<span class=\"tree-role-tool\">[read: notes.txt:25-37]</span>", engine.Evaluate("viewer.getTreeNodeDisplayHtml(resultEntry)").AsString(), "tree row");
        var row = engine.Evaluate("viewer.renderEntry(assistantEntry)").AsString();
        Check(row.Contains("<span class=\"line-numbers\">:25-37</span>", StringComparison.Ordinal), "read row: " + row);
    }
}
