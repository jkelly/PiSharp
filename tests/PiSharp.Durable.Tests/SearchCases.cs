// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/search.test.ts ("tokenize" as search.tokenize.<case>,
// "rankDocuments" as search.rank-documents.<case>). Verbatim apart from these:
// - Each row of an `it.each` is a case of its own, named after the script of its text (chinese, japanese, korean, thai, russian, arabic,
//   hindi, french), as the text would not make an ASCII id.
// - `toHaveLength(1)` checks the count.
// The inputs and expected words are the upstream test's, read from the source, not printed by a run. The Chinese, Japanese and Thai rows split
// text written without spaces, which needs the system's ICU (Search.cs): Windows 10 1903+, macOS and the usual Linux distributions have it.
// Authored, after them:
// - search.code-mode.*: code mode's searchTools() on the same ranking (upstream c7372af42's codemode.ts change, Codemode.cs): a word only in
//   a namespace's instructions finds the namespace's tools, and a query that is exactly a tool's name, or its name after a `__`, ranks that
//   tool first.
using System.Text.RegularExpressions;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Tools;
using static HarnessTests;
using static Program;
using static SessionTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> SearchCases() => SearchTests.Cases();
}

/// <summary>The search.* cases (upstream search.test.ts, and the authored cases of the file header).</summary>
internal static class SearchTests
{
    private static SearchDocument Doc(string name, string text, string? namespaceText = null) =>
        namespaceText is null ? new(name, text) : new(name, text, namespaceText);

    private static readonly (string Id, string Text, string[] Words)[] UnicodeWords =
    [
        ("search.tokenize.segments-unicode-words-in-chinese", "搜索仓库问题", ["搜索", "仓库", "问题"]),
        ("search.tokenize.segments-unicode-words-in-japanese", "文書を検索", ["文書", "を", "検索"]),
        ("search.tokenize.segments-unicode-words-in-korean", "문서 검색", ["문서", "검색"]),
        ("search.tokenize.segments-unicode-words-in-thai", "ค้นหาเอกสาร", ["ค้นหา", "เอกสาร"]),
        ("search.tokenize.segments-unicode-words-in-russian", "Поиск документов", ["поиск", "документов"]),
        ("search.tokenize.segments-unicode-words-in-arabic", "البحث عن الملفات", ["البحث", "عن", "الملفات"]),
        ("search.tokenize.segments-unicode-words-in-hindi", "फ़ाइल खोजें", ["फ़ाइल", "खोजें"]),
        ("search.tokenize.segments-unicode-words-in-french", "Éléments cafés", ["éléments", "cafés"]),
    ];

    private static readonly (string Id, string Text, string Query)[] UnicodeQueries =
    [
        ("search.rank-documents.matches-unicode-queries-against-chinese", "搜索仓库问题", "问题"),
        ("search.rank-documents.matches-unicode-queries-against-japanese", "文書を検索", "検索"),
        ("search.rank-documents.matches-unicode-queries-against-korean", "문서 검색", "검색"),
        ("search.rank-documents.matches-unicode-queries-against-thai", "ค้นหาเอกสาร", "เอกสาร"),
        ("search.rank-documents.matches-unicode-queries-against-russian", "Поиск документов", "документов"),
        ("search.rank-documents.matches-unicode-queries-against-arabic", "البحث عن الملفات", "الملفات"),
        ("search.rank-documents.matches-unicode-queries-against-hindi", "फ़ाइल खोजें", "खोजें"),
        ("search.rank-documents.matches-unicode-queries-against-french", "Rechercher un café", "cafe\u0301"),
    ];

    private static readonly SearchDocument[] Documents =
    [
        Doc("mcp__github__list_issues", "List issues in a repository. state open or closed"),
        Doc("mcp__github__create_pull_request", "Open a pull request."),
        Doc("mcp__linear__search_issues", "Search Linear issues by text."),
        Doc("mcp__docs__search", "Search the documentation."),
    ];

    internal static IEnumerable<(string, Func<Task>)> Cases()
    {
        // ─── tokenize ───────────────────────────────────────────────────────

        yield return Case("search.tokenize.splits-camelcase-and-snake-case-drops-stop-words-and-folds-plurals", () =>
        {
            Seq(["list", "issue", "git", "hub", "repo"], Search.Tokenize("listIssues for the GitHub_repo"), "listIssues for the GitHub_repo");
            Seq(["search", "query", "http", "server"], Search.Tokenize("searches queries HTTPServer"), "searches queries HTTPServer");
            Seq(["repo", "issue", "search", "query"], Search.Tokenize("repo.issues/search:queries"), "repo.issues/search:queries");
        });

        foreach (var (id, text, words) in UnicodeWords)
            yield return Case(id, () => Seq(words, Search.Tokenize(text), text));

        yield return Case("search.tokenize.preserves-non-ascii-words-and-splits-mixed-language-identifiers", () =>
            Seq(["搜索", "git", "hub", "repo", "issue", "résumé", "cafés"], Search.Tokenize("搜索GitHub_repoIssues résumé cafés"), "mixed"));

        yield return Case("search.tokenize.normalizes-canonically-equivalent-unicode-text", () =>
            Seq(["café", "café"], Search.Tokenize("CAFÉ cafe\u0301"), "CAFÉ cafe + U+0301"));

        yield return Case("search.tokenize.ignores-whitespace-punctuation-and-emoji", () =>
            Seq([], Search.Tokenize(" \t，。!? \U0001F600 "), "whitespace, punctuation and emoji"));

        // ─── rankDocuments ──────────────────────────────────────────────────

        yield return Case("search.rank-documents.ranks-by-term-relevance-and-respects-the-limit", () =>
        {
            Seq(["mcp__linear__search_issues", "mcp__github__list_issues"], Search.RankDocuments("issue", Documents, 8), "issue");
            Equal("mcp__github__create_pull_request", Search.RankDocuments("pull requests", Documents, 8)[0], "pull requests");
            Equal(1, Search.RankDocuments("search", Documents, 1).Count, "search, limit 1");
            Seq(["mcp__github__list_issues"], Search.RankDocuments("closed", Documents, 8), "closed");
        });

        foreach (var (id, text, query) in UnicodeQueries)
            yield return Case(id, () =>
                Seq(["localized"], Search.RankDocuments(query, [.. Documents, Doc("localized", text)], 8), $"{query} against {text}"));

        yield return Case("search.rank-documents.returns-nothing-for-unknown-or-empty-queries", () =>
        {
            Seq([], Search.RankDocuments("kubernetes", Documents, 8), "kubernetes");
            Seq([], Search.RankDocuments("the", Documents, 8), "the");
            Seq([], Search.RankDocuments("未知", Documents, 8), "未知");
        });

        yield return Case("search.rank-documents.searches-the-namespace-text", () =>
        {
            SearchDocument[] namespaced = [Doc("mcp__x__run", "Run it.", "mcp__x Kubernetes cluster tools")];
            Seq(["mcp__x__run"], Search.RankDocuments("kubernetes", namespaced, 8), "kubernetes");
        });

        // https://github.com/earendil-works/pi/issues/11331
        yield return Case("search.rank-documents.ranks-a-tool-first-when-the-query-is-its-name", () =>
        {
            SearchDocument[] named =
            [
                Doc("mcp__app__queue_read", "Read a queued message of this thread."),
                Doc("mcp__dev-app__thread_read", "Read durable state and a paginated timeline from any thread. The default view returns user messages, " +
                    "assistant messages and plans. Continue with afterPosition until hasMore is false."),
            ];
            foreach (var query in new[] { "thread_read", "dev_app__thread_read", "mcp__dev-app__thread_read", "Thread_Read" })
                Seq(["mcp__dev-app__thread_read"], Search.RankDocuments(query, named, 1), query);
            // Partial names still rank by BM25.
            Seq(["mcp__app__queue_read"], Search.RankDocuments("read", named, 1), "read");
        });

        // https://github.com/earendil-works/pi/issues/11331
        yield return Case("search.rank-documents.ranks-a-name-match-above-mentions-in-descriptions", () =>
        {
            SearchDocument[] ci =
            [
                Doc("mcp__ci__status", "Deploy status: last deploy, deploy log, deploy errors."),
                Doc("mcp__ci__deploy_release", "Start a release of the current branch to an environment, run the migrations, switch traffic over, " +
                    "and wait until the new version is live and healthy."),
            ];
            Seq(["mcp__ci__deploy_release"], Search.RankDocuments("deploy", ci, 1), "deploy");
        });

        // https://github.com/earendil-works/pi/issues/11331
        yield return Case("search.rank-documents.ranks-a-tool-first-when-the-query-contains-its-whole-name", () =>
        {
            const string peekaboo = "mcp__peekaboo Peekaboo controls macOS apps by app name.";
            SearchDocument[] named =
            [
                Doc("mcp__peekaboo__see", "Capture the app window of the peekaboo app and describe the app.", peekaboo),
                Doc("mcp__peekaboo__menu", "Click a menu item of an app.", peekaboo),
                Doc("mcp__peekaboo__app", "Launch, quit, relaunch, focus, hide, or switch applications. Targets an application by name, bundle " +
                    "identifier, or process ID, and can wait until it is ready.", peekaboo),
                Doc("mcp__app__queue_read", "Read a queued message of this thread."),
                Doc("mcp__dev-app__thread_read", "Read durable state and a paginated timeline from any thread. The default view returns user messages, " +
                    "assistant messages and plans."),
            ];
            Seq(["mcp__peekaboo__app"], Search.RankDocuments("peekaboo app", named, 1), "peekaboo app");
            Seq(["mcp__peekaboo__app"], Search.RankDocuments("open the peekaboo app tool", named, 1), "open the peekaboo app tool");
            Seq(["mcp__dev-app__thread_read"], Search.RankDocuments("read thread_read state", named, 1), "read thread_read state");
            // Without the whole name, mentions in descriptions decide.
            Seq(["mcp__peekaboo__see"], Search.RankDocuments("describe window", named, 1), "describe window");
        });

        // https://github.com/earendil-works/pi/issues/11331
        yield return Case("search.rank-documents.ranks-a-tool-matching-more-query-terms-first-whatever-the-scores", () =>
        {
            SearchDocument[] peekaboo =
            [
                Doc("mcp__peekaboo__app", "Launch, quit, focus, or switch applications by name or bundle identifier."),
                Doc("mcp__peekaboo__see", "Capture the app window and describe the UI elements of the app."),
                Doc("mcp__peekaboo__window", "Move, resize, or close a window of an app."),
                Doc("mcp__peekaboo__image", "Take a screenshot of the screen, an app, or a window."),
            ];
            // `window` and `app` are whole names, but each covers one of the two terms.
            Seq(["mcp__peekaboo__image"], Search.RankDocuments("screenshot window", peekaboo, 1), "screenshot window");
            Seq(["mcp__peekaboo__see"], Search.RankDocuments("describe app", peekaboo, 1), "describe app");
            // An exact name still ranks first.
            Seq(["mcp__peekaboo__window"], Search.RankDocuments("window", peekaboo, 1), "window");
        });

        // ─── Authored: code mode's searchTools() (see the file header) ──────

        yield return Case("search.code-mode.search-tools-searches-namespace-instructions-and-ranks-a-named-tool-first", async () =>
        {
            var chat = new CodemodeTests.Chat([],
            [
                new CodemodeNamespace("github", "mcp__github__") { Description = "GitHub repositories", Instructions = "Use owner/repo for repositories." },
                new CodemodeNamespace("app", "mcp__app__") { Description = "Thread and queue state of the app" },
            ]);
            AddTool(chat.Setup.Registry, Described("mcp__github__list_issues", "List issues in a repository."));
            AddTool(chat.Setup.Registry, Described("mcp__app__queue_read", "Read a queued message of this thread."));
            AddTool(chat.Setup.Registry, Described("mcp__app__thread_read", "Read durable state and a paginated timeline from any thread. The default " +
                "view returns user messages, assistant messages and plans. Continue with afterPosition until hasMore is false."));
            var ran = await CodemodeTests.RunScript("""
                const names = async (query, options) => (await searchTools(query, options)).map((tool) => tool.name);
                				return {
                					owner: await names("owner"),
                					name: await names("thread_read", { limit: 1 }),
                					qualified: await names("app__thread_read", { limit: 1 }),
                					read: await names("read", { namespace: "app" }),
                				};
                """, chat);
            // `owner` is only in github's instructions; `read` is in both app tools' names, and queue_read's shorter sample scores it higher.
            SameJson("""{"owner":["mcp__github__list_issues"],"name":["mcp__app__thread_read"],"qualified":["mcp__app__thread_read"],"read":["mcp__app__queue_read","mcp__app__thread_read"]}""",
                JsonValue.Parse(ScriptHeader.Replace(ran.Text, "", 1)), "result");
        });
    }

    private static readonly Regex ScriptHeader = new(@"^Script (completed|failed)\nWall time [\d.]+ seconds\nOutput:\n", RegexOptions.CultureInvariant);

    private static ToolRegistration Described(string name, string description) => Define.Tool(new ToolRegistration
    {
        Name = name,
        Description = description,
        Parameters = CodemodeTests.Parameters,
        Execute = (_, _, _) => Task.FromResult(CodemodeTests.TextResult(name + " ran")),
    });
}
