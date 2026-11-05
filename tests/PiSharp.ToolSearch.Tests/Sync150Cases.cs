using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Discovery;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/coding-agent/test/tool-search.test.ts. Pi 1.5.0's tool_search
// ranking (tools.tool-search-name-weight) and Unicode words (tools.tool-search-unicode-words). The inputs and expected results
// are the upstream tests', read from the source, not captured from a run. Splitting text without spaces (Chinese, Japanese,
// Thai) needs the system's ICU, which Windows 10 1903+, macOS and the usual Linux distributions have.
internal static partial class Program
{
    private static IEnumerable<(string Id, Func<Task> Run)> Sync150Cases() =>
    [
        ("sync150.tool-search-unicode-words.tokenize", Sync(UnicodeTokenize)),
        ("sync150.tool-search-unicode-words.rank", Sync(UnicodeQueries)),
        ("sync150.tool-search-name-weight.exact-name-first", Sync(ExactNameFirst)),
        ("sync150.tool-search-name-weight.name-above-mentions", Sync(NameAboveMentions)),
        ("sync150.tool-search-name-weight.whole-name-bonus", Sync(WholeNameBonus)),
        ("sync150.tool-search-name-weight.coverage-first", Sync(CoverageFirst))
    ];

    private static void UnicodeTokenize()
    {
        Names(["repo", "issue", "search", "query"], ToolSearch.Tokenize("repo.issues/search:queries"), "punctuation separates words");
        (string Text, string[] Words)[] words =
        [
            ("搜索仓库问题", ["搜索", "仓库", "问题"]),
            ("文書を検索", ["文書", "を", "検索"]),
            ("문서 검색", ["문서", "검색"]),
            ("ค้นหาเอกสาร", ["ค้นหา", "เอกสาร"]),
            ("Поиск документов", ["поиск", "документов"]),
            ("البحث عن الملفات", ["البحث", "عن", "الملفات"]),
            ("फ़ाइल खोजें", ["फ़ाइल", "खोजें"]),
            ("Éléments cafés", ["éléments", "cafés"])
        ];
        foreach (var (text, expected) in words) Names(expected, ToolSearch.Tokenize(text), "segments " + text);
        Names(["搜索", "git", "hub", "repo", "issue", "résumé", "cafés"],
            ToolSearch.Tokenize("搜索GitHub_repoIssues résumé cafés"), "non-ASCII words kept, mixed identifiers split");
        Names(["café", "café"], ToolSearch.Tokenize("CAFÉ café"), "canonically equivalent text normalized");
        Names([], ToolSearch.Tokenize(" \t，。!? \U0001F600 "), "whitespace, punctuation and emoji");
        // Authored: a letter outside the BMP is a word character (the source's u flag), a lone surrogate a separator.
        Names(["x\U0001D400y", "\U00020000"], ToolSearch.Tokenize("x\U0001D400y \U00020000"), "supplementary-plane letters");
        Names(["ab", "cd"], ToolSearch.Tokenize("ab\uD800cd"), "lone surrogate");
    }

    private static void UnicodeQueries()
    {
        (string Description, string Query)[] queries =
        [
            ("搜索仓库问题", "问题"),
            ("文書を検索", "検索"),
            ("문서 검색", "검색"),
            ("ค้นหาเอกสาร", "เอกสาร"),
            ("Поиск документов", "документов"),
            ("البحث عن الملفات", "الملفات"),
            ("फ़ाइल खोजें", "खोजें"),
            ("Rechercher un café", "café")
        ];
        foreach (var (description, query) in queries)
        {
            var matches = new Bm25Ranker().Rank(query, [.. Corpus, Document("localized", description)], 8);
            Check(matches.Count == 1 && matches[0].Name == "localized" && matches[0].Score > 0,
                $"{query} against {description}: [{string.Join(',', matches.Select(match => match.Name))}]");
        }
    }

    private static void ExactNameFirst()
    {
        ToolSearchDocument[] named =
        [
            Document("mcp__app__queue_read", "Read a queued message of this thread."),
            Document("mcp__dev-app__thread_read", "Read durable state and a paginated timeline from any thread. The default view returns user messages, " +
                "assistant messages and plans. Continue with afterPosition until hasMore is false.")
        ];
        foreach (var query in new[] { "thread_read", "dev_app__thread_read", "mcp__dev-app__thread_read", "Thread_Read", "thread read" })
            Names(["mcp__dev-app__thread_read"], new Bm25Ranker().Rank(query, named, 1).Select(match => match.Name), query);
        // Partial names still rank by BM25.
        Names(["mcp__app__queue_read"], new Bm25Ranker().Rank("read", named, 1).Select(match => match.Name), "read");
    }

    private static void NameAboveMentions()
    {
        ToolSearchDocument[] documents =
        [
            Document("mcp__ci__status", "Deploy status: last deploy, deploy log, deploy errors."),
            Document("mcp__ci__deploy_release", "Start a release of the current branch to an environment, run the migrations, switch traffic over, " +
                "and wait until the new version is live and healthy.")
        ];
        Names(["mcp__ci__deploy_release"], new Bm25Ranker().Rank("deploy", documents, 1).Select(match => match.Name), "deploy");
    }

    private static void WholeNameBonus()
    {
        var peekaboo = new ToolNamespace("mcp__peekaboo") { Instructions = "Peekaboo controls macOS apps by app name." };
        (string Name, string Description)[] tools =
        [
            ("mcp__peekaboo__see", "Capture the app window of the peekaboo app and describe the app."),
            ("mcp__peekaboo__menu", "Click a menu item of an app."),
            ("mcp__peekaboo__app", "Launch, quit, relaunch, focus, hide, or switch applications. Targets an application by name, bundle identifier, " +
                "or process ID, and can wait until it is ready."),
            ("mcp__app__queue_read", "Read a queued message of this thread."),
            ("mcp__dev-app__thread_read", "Read durable state and a paginated timeline from any thread. The default view returns user messages, " +
                "assistant messages and plans.")
        ];
        var documents = tools.Select(tool => Document(tool.Name, tool.Description,
            group: tool.Name.StartsWith("mcp__peekaboo__", StringComparison.Ordinal) ? peekaboo : null)).ToArray();
        IEnumerable<string> First(string query) => new Bm25Ranker().Rank(query, documents, 1).Select(match => match.Name);
        Names(["mcp__peekaboo__app"], First("peekaboo app"), "peekaboo app");
        Names(["mcp__peekaboo__app"], First("open the peekaboo app tool"), "open the peekaboo app tool");
        Names(["mcp__dev-app__thread_read"], First("read thread_read state"), "read thread_read state");
        // Without the whole name, mentions in descriptions decide.
        Names(["mcp__peekaboo__see"], First("describe window"), "describe window");
    }

    private static void CoverageFirst()
    {
        ToolSearchDocument[] documents =
        [
            Document("mcp__peekaboo__app", "Launch, quit, focus, or switch applications by name or bundle identifier."),
            Document("mcp__peekaboo__see", "Capture the app window and describe the UI elements of the app."),
            Document("mcp__peekaboo__window", "Move, resize, or close a window of an app."),
            Document("mcp__peekaboo__image", "Take a screenshot of the screen, an app, or a window.")
        ];
        IEnumerable<string> First(string query) => new Bm25Ranker().Rank(query, documents, 1).Select(match => match.Name);
        // `window` and `app` are whole names, but each covers one of the two terms.
        Names(["mcp__peekaboo__image"], First("screenshot window"), "screenshot window");
        Names(["mcp__peekaboo__see"], First("describe app"), "describe app");
        // An exact name still ranks first.
        Names(["mcp__peekaboo__window"], First("window"), "window");
    }
}
