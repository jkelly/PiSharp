// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/tool-search.test.ts ("deferred tools and tool_search"), as
// tool-search.<case>. Verbatim apart from the adaptations in HarnessSupport.cs and ChatSupport.cs, and these:
// - `Type.Object({ number: Type.Optional(Type.Number({ description: "Issue number" })) })` is its JSON as TypeBox prints it.
// - `expect.objectContaining({ toolsAdded: [expect.objectContaining({ name })] })` checks that toolsAdded holds exactly that one declaration.
// Authored, after them:
// - tool-search.errors.*: what the model gets for an empty query or a limit that is not a positive integer, a huge limit, and the first line
//   of a multi-line description. The expected tool results were printed by the pinned upstream tool under Node (its chat-support.ts setup).
// - tool-search.declaration: the tool's name, description, parameters, callers and replay, and its extension.
// - tool-search.search.*: Search.Tokenize and Search.RankDocuments (upstream search.ts) on inputs whose results the pinned upstream
//   search.ts printed under Node: camelCase and acronym splits, the naive stemmer, stop words, non-ASCII text (U+0130 and the Kelvin sign
//   lowercase to ASCII letters), empty and limit edge cases, and ties keeping document order.
// - tool-search.ranking-differential.*: Search against main's port of the coding agent's ranking (PiSharp.Extensions.Abstractions'
//   ToolSearch.Tokenize, ToolSearch.CreateDocument and Bm25Ranker; upstream coding-agent extensions/tool-search/tool.ts, the same
//   algorithm search.ts copies) over seeded random texts, queries and schemas. U+0130 is left out of these texts: main lowercases with
//   .NET's invariant mapping, Search as JavaScript does (see Search.cs), and the search.* cases pin JavaScript's result.
using System.Text;
using System.Text.Json;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Tools;
using static ChatTests;
using static HarnessTests;
using static Program;
using static SessionTests;
using MainBm25Ranker = PiSharp.Extensions.Mcp.Discovery.Bm25Ranker;
using MainToolSearch = PiSharp.Extensions.Mcp.Discovery.ToolSearch;
using MainToolSearchDocument = PiSharp.Extensions.Mcp.Discovery.ToolSearchDocument;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> ToolSearchCases() => ToolSearchTests.Cases("tool-search.");
}

/// <summary>The tool-search.* cases (upstream tool-search.test.ts, and the authored cases of the file header).</summary>
internal static class ToolSearchTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    private static readonly JsonObjectValue IssueParameters =
        O("""{"type":"object","properties":{"number":{"type":"number","description":"Issue number"}}}""");

    private static ToolRegistration Tool(string name, string description, Func<ToolRegistration, ToolRegistration>? extra = null)
    {
        var tool = Define.Tool(new ToolRegistration
        {
            Name = name,
            Description = description,
            Parameters = IssueParameters,
            Execute = (_, _, _) => Task.FromResult(new ToolExecutionResult { Output = JsonValue.Array(FauxText($"{name} ran")) }),
        });
        return extra is null ? tool : extra(tool);
    }

    private static readonly JsonObjectValue Done = FauxAssistantMessage([FauxText("done")]);

    private static JsonObjectValue Call(string name, JsonObjectValue? args = null, string id = "c1") =>
        FauxAssistantMessage([FauxToolCall(name, args ?? JsonObjectValue.Empty, id)], stopReason: "toolUse");

    /// <summary><c>alpha</c>, the deferred <c>github_get_issue</c> and <c>github_list_repos</c>, then <c>omega</c>, and <c>tool_search</c>
    /// last.</summary>
    private static ChatSetup Setup()
    {
        var chat = ChatTests.Setup();
        AddTool(chat.Registry, Tool("alpha", "Alpha tool"));
        AddTool(chat.Registry, Tool("github_get_issue", "Get a GitHub issue by number", tool => tool with { Deferred = true }));
        AddTool(chat.Registry, Tool("github_list_repos", "List the repositories of a user", tool => tool with { Deferred = true }));
        AddTool(chat.Registry, Tool("omega", "Omega tool"));
        chat.Registry.Install(ToolSearch.CreateExtension());
        return chat;
    }

    private static async Task<List<string>> Offered(IConversation root) => [.. (await root.AgentAsync(Context)).Tools.Select(each => each.Name)];

    private static async Task<List<string>> DeferredTools(IConversation root) => [.. (await root.AgentAsync(Context)).Deferred.Select(each => each.Name)];

    private static async Task<JsonValue?> ModelTools(IHarness harness, IConversation root) =>
        (await harness.SnapshotAsync(Agents.AgentDoc, root.Id, Context)) is { } agent && agent.TryGetValue("modelTools", out var filter) ? filter : null;

    /// <summary>The model messages of the <c>pi.system</c> entries, oldest first.</summary>
    private static List<JsonObjectValue> SystemMessages(IEnumerable<EntryRecord> entries) =>
        [.. entries.Where(entry => entry.Kind == "pi.system").Select(entry => (JsonObjectValue)entry.Model![0])];

    private static List<string>? AddedNames(JsonObjectValue message) =>
        message.TryGetValue("toolsAdded", out var tools) ? [.. ((JsonArrayValue)tools).Select(tool => S(((JsonObjectValue)tool)["name"]))] : null;

    private static List<string> ToolResultTexts(IEnumerable<EntryRecord> entries) =>
        [.. entries.Where(entry => entry.Kind == "pi.tool-result").Select(entry =>
            string.Concat(((JsonArrayValue)((JsonObjectValue)entry.Model![0])["content"]).Select(item =>
                ((JsonObjectValue)item).TryGetValue("text", out var text) ? S(text) : "")))];

    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix)
    {
        yield return Case(prefix + "offers-a-deferred-tool-only-once-tool-search-loads-it-by-appending-its-declaration", async () =>
        {
            var chat = Setup();
            chat.Faux.SetResponses(Call("tool_search", Obj(("query", "github issue"), ("limit", 1))), Call("github_get_issue", Obj(("number", 42)), "c2"), Done);
            var (harness, root) = await OpenChat(new MemoryStorage(), chat);
            Seq(["alpha", "omega", "tool_search"], await Offered(root), "offered");
            Seq(["github_get_issue", "github_list_repos"], await DeferredTools(root), "deferred");
            // Deferred tools stay callable by other tools, such as code mode scripts.
            Check((await root.AgentAsync(Context)).Callable.Any(each => each.Name == "github_get_issue"), "callable contains github_get_issue");
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            var entries = await AllEntries(root);
            var texts = ToolResultTexts(entries);
            Equal("Loaded 1 tool. They are available from your next call:\n- github_get_issue: Get a GitHub issue by number", texts[0], "search");
            Equal("github_get_issue ran", texts[1], "issue");
            SameJson("""{"add":["github_get_issue"]}""", await ModelTools(harness, root), "modelTools");
            Seq(["alpha", "omega", "tool_search", "github_get_issue"], await Offered(root), "offered after the load");
            // A later search sees only the tools not loaded yet.
            Seq(["github_list_repos"], await DeferredTools(root), "deferred after the load");
            // The load appended the declaration: no tool was removed and declared again, so the cached prefix holds.
            var system = SystemMessages(entries);
            var (baseline, load) = (system[0], system[1]);
            Seq(["alpha", "omega", "tool_search"], AddedNames(baseline)!, "baseline toolsAdded");
            Seq(["github_get_issue"], AddedNames(load) ?? [], "load toolsAdded");
            Check(!load.ContainsKey("toolsRemoved"), "load toolsRemoved is undefined");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "appends-each-loaded-tool-in-load-order-not-registry-order", async () =>
        {
            var chat = Setup();
            chat.Faux.SetResponses(Call("tool_search", Obj(("query", "repositories"), ("limit", 1))),
                Call("tool_search", Obj(("query", "issue"), ("limit", 1)), "c2"), Done);
            var (harness, root) = await OpenChat(new MemoryStorage(), chat);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            Seq(["alpha", "omega", "tool_search", "github_list_repos", "github_get_issue"], await Offered(root), "offered");
            var loads = SystemMessages(await AllEntries(root)).Skip(1).ToList();
            Equal(2, loads.Count, "loads");
            Seq(["github_list_repos"], AddedNames(loads[0]) ?? [], "first load");
            Seq(["github_get_issue"], AddedNames(loads[1]) ?? [], "second load");
            Check(loads.All(message => !message.ContainsKey("toolsRemoved")), "no load removes a tool");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "records-loads-by-name-unknown-and-non-deferred-names-change-nothing-remove-wins-and-a-load-lifts-it", async () =>
        {
            var chat = Setup();
            var grow = Define.Tool(new ToolRegistration
            {
                Name = "grow",
                Description = "grow",
                Parameters = ToolRegistration.EmptyParameters,
                Execute = (_, _, _) => Task.FromResult(new ToolExecutionResult
                {
                    Output = JsonArrayValue.Empty, Control = new ToolControl { AddTools = ["github_get_issue", "alpha", "ghost"] },
                }),
            });
            AddTool(chat.Registry, grow);
            chat.Faux.SetResponses(Call("grow"), Done, Call("grow"), Done);
            var (harness, root) = await OpenChat(new MemoryStorage(), chat);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            SameJson("""{"add":["github_get_issue","alpha","ghost"]}""", await ModelTools(harness, root), "modelTools");
            Seq(["alpha", "omega", "tool_search", "grow", "github_get_issue"], await Offered(root), "offered");
            // A name in both remove and add is not offered.
            var getIssue = (await root.AgentAsync(Context)).Callable.First(each => each.Name == "github_get_issue");
            await root.ConfigureAsync(new AgentChange { ModelTools = ModelToolSelection.Edit(remove: [getIssue], add: [getIssue]) }, Context);
            Check(!(await Offered(root)).Contains("github_get_issue"), "remove wins over add");
            // Loading it again drops it from remove.
            await (await root.SubmitAsync(SubmissionDraft.Input("again"), Context)).WaitAsync(Context);
            SameJson("""{"add":["github_get_issue","alpha","ghost"]}""", await ModelTools(harness, root), "modelTools after the second load");
            Check((await Offered(root)).Contains("github_get_issue"), "offered after the second load");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-a-loaded-tool-when-its-extension-is-gone-for-a-while-and-appends-to-an-explicit-array", async () =>
        {
            var chat = Setup();
            var issue = Tool("github_get_issue", "Get a GitHub issue by number", tool => tool with { Deferred = true });
            chat.Faux.SetResponses(Call("tool_search", Obj(("query", "github issue"), ("limit", 1))), Done);
            var (harness, root) = await OpenChat(new MemoryStorage(), chat);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            // The server goes away: its extension is installed again without the tool.
            chat.Registry.Install(Define.Extension(new Extension { Name = "tool:github_get_issue", Tools = [] }));
            Check(!(await Offered(root)).Contains("github_get_issue"), "not offered while its extension is gone");
            chat.Registry.Install(Define.Extension(new Extension { Name = "tool:github_get_issue", Tools = [issue] }));
            Check((await Offered(root)).Contains("github_get_issue"), "offered again once it is back");
            // With an explicit array, a load appends to it as before.
            var agent = await root.AgentAsync(Context);
            var alpha = agent.Tools.First(each => each.Name == "alpha");
            var search = agent.Tools.First(each => each.Name == "tool_search");
            await root.ConfigureAsync(new AgentChange { ModelTools = ModelToolSelection.Only(alpha, search) }, Context);
            chat.Faux.SetResponses(Call("tool_search", Obj(("query", "repositories"), ("limit", 1))), Done);
            await (await root.SubmitAsync(SubmissionDraft.Input("more"), Context)).WaitAsync(Context);
            SameJson("""["alpha","tool_search","github_list_repos"]""", await ModelTools(harness, root), "modelTools");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-loaded-tools-per-branch-and-in-the-baseline-after-a-reset", async () =>
        {
            var chat = Setup();
            chat.Faux.SetResponses(Call("tool_search", Obj(("query", "github issue"), ("limit", 1))), Done);
            var (harness, root) = await OpenChat(new MemoryStorage(), chat);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            var entries = await AllEntries(root);
            var before = await root.ForkAsync(entries[0].Id, new ConversationCreateOptions(ConversationOwnership.Ownerless), Context);
            var after = await root.ForkAsync(entries[^1].Id, new ConversationCreateOptions(ConversationOwnership.Ownerless), Context);
            Check(!(await Offered(before)).Contains("github_get_issue"), "a fork before the load does not offer it");
            Check((await Offered(after)).Contains("github_get_issue"), "a fork after the load offers it");
            await root.ResetAsync(null, Context);
            chat.Faux.SetResponses(Done);
            await (await root.SubmitAsync(SubmissionDraft.Input("fresh"), Context)).WaitAsync(Context);
            var baseline = SystemMessages(await AllEntries(root))[^1];
            Seq(["alpha", "omega", "tool_search", "github_get_issue"], AddedNames(baseline)!, "baseline after the reset");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "finds-nothing-for-an-unrelated-query-and-loads-nothing", async () =>
        {
            var chat = Setup();
            chat.Faux.SetResponses(Call("tool_search", Obj(("query", "weather forecast"))), Done);
            var (harness, root) = await OpenChat(new MemoryStorage(), chat);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            Seq(["No matching tools found."], ToolResultTexts(await AllEntries(root)), "tool results");
            Check(await ModelTools(harness, root) is null, "modelTools is undefined");
            await harness.CloseAsync(Context);
        });

        // ─── Authored (see the file header) ─────────────────────────────────

        foreach (var (slug, args, result, modelTools) in ErrorCases)
        {
            yield return Case(prefix + "errors." + slug, async () =>
            {
                var chat = ChatTests.Setup();
                AddTool(chat.Registry, Define.Tool(new ToolRegistration
                {
                    Name = "github_get_issue",
                    Description = "  \n Get a GitHub issue\r\nby number",
                    Deferred = true,
                    Parameters = ToolRegistration.EmptyParameters,
                    Execute = (_, _, _) => Task.FromResult(new ToolExecutionResult { Output = JsonArrayValue.Empty }),
                }));
                chat.Registry.Install(ToolSearch.CreateExtension());
                chat.Faux.SetResponses(Call("tool_search", O(args)), Done);
                var (harness, root) = await OpenChat(new MemoryStorage(), chat);
                await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
                var message = (JsonObjectValue)(await AllEntries(root)).Single(entry => entry.Kind == "pi.tool-result").Model![0];
                SameJson(result, JsonValue.Object(message.Where(property => property.Key is not ("timestamp" or "durationMs"))), "tool result");
                if (modelTools is null) Check(await ModelTools(harness, root) is null, "modelTools is undefined");
                else SameJson(modelTools, await ModelTools(harness, root), "modelTools");
                await harness.CloseAsync(Context);
            });
        }

        yield return Case(prefix + "declaration", () =>
        {
            var tool = ToolSearch.CreateTool();
            Equal("tool_search", tool.Name, "name");
            Equal(ToolSearch.ToolName, tool.Name, "ToolName");
            Equal(8, ToolSearch.DefaultLimit, "DefaultLimit");
            Equal("# Tool discovery\n\nSearches over deferred tool metadata with BM25 and exposes matching tools for the next model call.\n\n" +
                "Some of the tools, such as tools of MCP servers, may not have been provided to you upfront, and you should use this tool " +
                "(`tool_search`) to search for the required tools. For MCP tool discovery, always use `tool_search`.", tool.Description, "description");
            Equal("""{"type":"object","required":["query"],"properties":{"query":{"type":"string","description":"Search query for deferred tools."},"limit":{"type":"number","description":"Maximum number of tools to return. Defaults to 8."}}}""",
                tool.Parameters.ToJson(), "parameters");
            Seq([ToolCaller.Model], tool.Callers!.Value, "callers");
            Equal(ToolReplay.Safe, tool.Replay, "replay");
            Equal(false, tool.Deferred, "deferred");
            Check(tool.StructuredOutputSchema is null && tool.ExecutionMode is null && tool.OutputLimits is null, "no other options");
            var extension = ToolSearch.CreateExtension();
            Equal("tool-search", extension.Name, "extension name");
            Seq(["tool_search"], extension.Tools!.Select(each => each.Name), "extension tools");
            Check(!ReferenceEquals(ToolSearch.CreateTool(), ToolSearch.CreateTool()), "each call creates a registration");
        });

        foreach (var (index, (text, tokens)) in TokenizeGoldens.Index())
            yield return Case(prefix + $"search.tokenize.{index + 1:00}-{Slug(text)}", () => Seq(tokens, Search.Tokenize(text), $"Tokenize({JsonValue.String(text).ToJson()})"));

        foreach (var (index, (query, limit, names)) in RankGoldens.Index())
            yield return Case(prefix + $"search.rank.{index + 1:00}-{Slug(query)}", () =>
                Seq(names, Search.RankDocuments(query, RankDocuments, limit), $"RankDocuments({JsonValue.String(query).ToJson()}, {limit})"));

        yield return Case(prefix + "ranking-differential.tokenize", () =>
        {
            var next = Random(1);
            for (var trial = 0; trial < 2000; trial++)
            {
                var text = RandomText(next, 1 + (int)(next() * 12));
                Seq(MainToolSearch.Tokenize(text), Search.Tokenize(text), $"trial {trial}: Tokenize({JsonValue.String(text).ToJson()})");
            }
        });

        yield return Case(prefix + "ranking-differential.rank", () =>
        {
            var next = Random(2);
            for (var trial = 0; trial < 500; trial++)
            {
                var count = (int)(next() * 12);
                var documents = Enumerable.Range(0, count).Select(index =>
                    new SearchDocument($"tool_{index}", next() < 0.15 && index > 0 ? $"copy {index - 1}" : RandomText(next, (int)(next() * 30)))).ToList();
                // A tie: some documents repeat an earlier one's text under another name.
                documents = [.. documents.Select(document => document.Text.StartsWith("copy ", StringComparison.Ordinal)
                    ? document with { Text = documents[int.Parse(document.Text[5..], System.Globalization.CultureInfo.InvariantCulture)].Text } : document)];
                var query = RandomText(next, 1 + (int)(next() * 4));
                var limit = (int)(next() * 10) - 1;
                var main = new MainBm25Ranker().Rank(query, [.. documents.Select(document => new MainToolSearchDocument(document.Name, document.Text))], limit);
                Seq(main.Select(match => match.Name), Search.RankDocuments(query, documents, limit), $"trial {trial}: {JsonValue.String(query).ToJson()} limit {limit}");
            }
        });

        yield return Case(prefix + "ranking-differential.search-text", () =>
        {
            var next = Random(3);
            for (var trial = 0; trial < 500; trial++)
            {
                var name = RandomName(next);
                var description = RandomText(next, (int)(next() * 8));
                var parameters = (JsonObjectValue)RandomSchema(next, 0);
                var tool = Define.Tool(new ToolRegistration
                {
                    Name = name, Description = description, Parameters = parameters,
                    Execute = (_, _, _) => Task.FromResult(new ToolExecutionResult()),
                });
                using var document = JsonDocument.Parse(parameters.ToJson());
                var main = MainToolSearch.CreateDocument(name, description, document.RootElement);
                Equal(main.Text, ToolSearch.SearchText(tool), $"trial {trial}: search text of {parameters.ToJson()}");
            }
        });
    }

    // ─── Goldens printed by the pinned upstream under Node (see the file header) ───

    private static readonly (string Slug, string Args, string Result, string? ModelTools)[] ErrorCases =
    [
        ("blank-query", """{"query":"   "}""",
            """{"role":"toolResult","toolCallId":"c1","toolName":"tool_search","content":[{"type":"text","text":"<harness>\n[error] query must not be empty\n</harness>"}],"isError":true}""", null),
        ("bom-query", """{"query":"\uFEFF"}""",
            """{"role":"toolResult","toolCallId":"c1","toolName":"tool_search","content":[{"type":"text","text":"<harness>\n[error] query must not be empty\n</harness>"}],"isError":true}""", null),
        ("zero-limit", """{"query":"github","limit":0}""",
            """{"role":"toolResult","toolCallId":"c1","toolName":"tool_search","content":[{"type":"text","text":"<harness>\n[error] limit must be a positive integer\n</harness>"}],"isError":true}""", null),
        ("fractional-limit", """{"query":"github","limit":1.5}""",
            """{"role":"toolResult","toolCallId":"c1","toolName":"tool_search","content":[{"type":"text","text":"<harness>\n[error] limit must be a positive integer\n</harness>"}],"isError":true}""", null),
        ("negative-limit", """{"query":"github","limit":-2}""",
            """{"role":"toolResult","toolCallId":"c1","toolName":"tool_search","content":[{"type":"text","text":"<harness>\n[error] limit must be a positive integer\n</harness>"}],"isError":true}""", null),
        ("huge-limit-and-the-first-line-of-a-description", """{"query":"github","limit":1e20}""",
            """{"role":"toolResult","toolCallId":"c1","toolName":"tool_search","content":[{"type":"text","text":"Loaded 1 tool. They are available from your next call:\n- github_get_issue: Get a GitHub issue"}],"details":{"loaded":["github_get_issue"]},"isError":false}""",
            """{"add":["github_get_issue"]}"""),
    ];

    private static readonly (string Text, string[] Tokens)[] TokenizeGoldens =
    [
        ("getHTTPResponseCode", ["get", "http", "response", "code"]),
        ("XMLHttpRequest parser", ["xml", "http", "request", "parser"]),
        ("github_list_repos", ["github", "list", "repo"]),
        ("Searches issues boxes classes buses glasses wishes quizzes", ["search", "issue", "box", "class", "buse", "glass", "wish", "quizz"]),
        ("The tool is for a user, and it's ready", ["tool", "user", "s", "ready"]),
        ("na\u00EFve caf\u00E9 \u0130stanbul KELVIN-\u212A sign", ["na", "ve", "caf", "i", "stanbul", "kelvin", "k", "sign"]),
        ("ABCDef123gHI jklMNOpq", ["abc", "def123g", "hi", "jkl", "mn", "opq"]),
        ("  --  ", []),
        ("", []),
        ("x2Y z9A 3D", ["x2", "y", "z9", "3", "d"]),
        ("ies series ss s as yes this that", ["ies", "sery", "ss", "s", "yes"]),
        ("emoji \U0001F600 split", ["emoji", "split"]),
        ("tab\tnew\nline\r\nend", ["tab", "new", "line", "end"]),
        ("IOError HTMLElement aB", ["io", "error", "html", "element", "b"]),
    ];

    private static readonly SearchDocument[] RankDocuments =
    [
        new("github_get_issue", "github_get_issue github get issue Get a GitHub issue by number Issue number number"),
        new("github_list_repos", "github_list_repos github list repos List the repositories of a user Issue number number"),
        new("twin_a", "weather forecast for a city"),
        new("twin_b", "weather forecast for a city"),
        new("slack_post_message", "slack_post_message slack post message Post a message to a Slack channel channel text"),
        new("searchFiles", "searchFiles search Files Searches files by glob pattern pattern path"),
        new("empty", ""),
        new("stop", "the of and a to with"),
    ];

    private static readonly (string Query, int Limit, string[] Names)[] RankGoldens =
    [
        ("github issue", 1, ["github_get_issue"]),
        ("github issue", 8, ["github_get_issue", "github_list_repos"]),
        ("repositories", 1, ["github_list_repos"]),
        ("issues", 8, ["github_get_issue", "github_list_repos"]),
        ("weather forecast", 8, ["twin_a", "twin_b"]),
        ("weather", 1, ["twin_a"]),
        ("search", 8, ["searchFiles"]),
        ("message slack channel", 2, ["slack_post_message"]),
        ("the a of", 8, []),
        ("nothing here", 8, []),
        ("Number", 3, ["github_get_issue", "github_list_repos"]),
        ("GitHub", 0, []),
        ("github", -1, []),
    ];

    private static string Slug(string text)
    {
        var slug = new StringBuilder();
        foreach (var c in text.ToLowerInvariant()) slug.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '-');
        var value = string.Join("-", slug.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
        return value.Length == 0 ? "empty" : value.Length > 40 ? value[..40] : value;
    }

    // ─── Seeded random inputs of the differential cases ─────────────────────

    private static readonly string[] Words =
    [
        "github", "issue", "issues", "repo", "repos", "repositories", "search", "searches", "box", "boxes", "class", "classes", "glass", "wish",
        "wishes", "quiz", "quizzes", "series", "ies", "s", "ss", "as", "yes", "the", "a", "of", "and", "with", "to", "getHTTPResponse", "XMLHttp",
        "IOError", "parseJSON", "userId", "ID", "aB", "x2Y", "3D", "v1", "list_files", "read-file", "na\u00EFve", "caf\u00E9", "\u212Aelvin",
        "\u00C9COLE", "stra\u00DFe", "\U0001F600", "\u03A3\u03C3\u03C2", "number", "Number", "slack", "channel", "message", "post", "weather",
        "forecast", "city", "", " ", "--", "_", "\t", "\n", "\r\n", "\u00A0", "\u3000", "\uFEFF", "\u2028",
    ];

    /// <summary>mulberry32: a seeded generator of doubles in [0, 1).</summary>
    private static Func<double> Random(uint seed)
    {
        var state = seed;
        return () =>
        {
            state += 0x6D2B79F5;
            var t = state;
            t = (t ^ (t >> 15)) * (t | 1);
            t ^= t + (t ^ (t >> 7)) * (t | 61);
            return ((t ^ (t >> 14)) >> 0) / 4294967296.0;
        };
    }

    private static string RandomText(Func<double> next, int words)
    {
        var text = new StringBuilder();
        for (var index = 0; index < words; index++)
        {
            if (index > 0) text.Append(next() < 0.8 ? " " : next() < 0.5 ? "," : "");
            text.Append(Words[(int)(next() * Words.Length)]);
        }
        return text.ToString();
    }

    private static string RandomName(Func<double> next) =>
        string.Join(next() < 0.5 ? "_" : "", Enumerable.Range(0, 1 + (int)(next() * 3)).Select(_ => Words[(int)(next() * 30)].Trim()));

    /// <summary>A JSON Schema-like value: descriptions, properties (some with array-index names), items and anyOf/oneOf/allOf variants,
    /// and values of the wrong type where a schema keyword holds them.</summary>
    private static JsonValue RandomSchema(Func<double> next, int depth)
    {
        var properties = new List<KeyValuePair<string, JsonValue>> { new("type", next() < 0.7 ? "object" : "string") };
        if (next() < 0.6) properties.Add(new("description", next() < 0.9 ? RandomText(next, (int)(next() * 6)) : JsonValue.Number(3)));
        if (depth < 3 && next() < 0.6)
        {
            var named = new List<KeyValuePair<string, JsonValue>>();
            for (var index = (int)(next() * 4); index > 0; index--)
            {
                var key = next() < 0.2 ? ((int)(next() * 20)).ToString(System.Globalization.CultureInfo.InvariantCulture) : RandomName(next);
                if (named.All(property => property.Key != key)) named.Add(new(key, RandomSchema(next, depth + 1)));
            }
            properties.Add(new("properties", next() < 0.9 ? JsonValue.Object(named) : JsonValue.Array()));
        }
        if (depth < 3 && next() < 0.3) properties.Add(new("items", next() < 0.8 ? RandomSchema(next, depth + 1) : JsonValue.Array(RandomSchema(next, depth + 1))));
        foreach (var key in new[] { "anyOf", "oneOf", "allOf" })
            if (depth < 3 && next() < 0.15)
                properties.Add(new(key, next() < 0.85
                    ? JsonValue.Array(Enumerable.Range(0, 1 + (int)(next() * 3)).Select(_ => RandomSchema(next, depth + 1)))
                    : RandomSchema(next, depth + 1)));
        if (next() < 0.2) properties.Add(new("title", RandomText(next, 2)));
        return JsonValue.Object(properties);
    }
}
