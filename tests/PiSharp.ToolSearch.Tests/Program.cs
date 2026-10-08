using System.Text;
using System.Text.Json;

// Authored offline expectations for PiSharp 1.1.0.1: the built-in `tool_search` and `deferred` MCP exposure of Pi v1.1.0
// (packages/coding-agent/src/extensions/tool-search, extensions/mcp/index.ts and core/agent-session.ts). They are derived
// from the pinned upstream source and its tests (test/tool-search.test.ts) by reading, never captured from an upstream run.
// Fake MCP servers and a fake Anthropic endpoint answer in process; no network or live credentials are used.
internal static partial class Program
{
    private const string Upstream = "abe508e1b89912adde45528136c3221eb69acdd7";

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 0 && (args.Length != 2 || args[0] != "--report")) throw new ArgumentException("Use [--report <fresh path>].");
        var cases = new List<(string Id, Func<Task> Run)>
        {
            ("ranker.tokenize-camel-snake-stop-words-and-plurals", Sync(Tokenize)),
            ("ranker.bm25-ranks-upstream-corpus-and-respects-limit", Sync(RanksCorpus)),
            ("ranker.unknown-stop-word-and-synonym-queries-find-nothing", Sync(FindsNothing)),
            ("ranker.namespace-schema-and-score-formula", Sync(DocumentsAndScores)),
            ("tool.declaration-description-and-schema", Sync(Declaration)),
            ("tool.result-text-and-input-errors", Sync(ResultText)),
            ("session.search-loads-deferred-tools-for-the-next-request", SearchLoadsForNextRequest),
            ("session.loaded-tools-persist-across-turns-and-transcript-reopen", LoadedToolsPersist),
            ("mcp.deferred-server-connects-and-tool-search-loads-its-tools", DeferredServerProductionSession),
            ("mcp.mcp-servers-section-lists-deferred-servers", ServersSectionForDeferredServers),
            ("mcp.cli-reopen-applies-the-initial-selection", CliReopenAppliesInitialSelection),
            ("selection.tools-and-exclude-tools-gate-tool-search", ToolSelectionGatesToolSearch),
            ("mcp.codemode-servers-still-need-codemode", CodemodeServersStillSkipped),
            ("calls.direct-server-tool-call-succeeds", DirectServerCallSucceeds),
            ("calls.late-background-server-tool-call-succeeds-after-registration", LateBackgroundServerCallSucceeds),
            ("calls.skipped-server-and-unloaded-tools-are-not-callable", SkippedServerCallsFail),
            ("calls.grant-rule-exact-name-target-and-admitted-scope", CallGrantRule)
        };
        var results = new List<object>(); var failures = 0;
        foreach (var test in cases)
        {
            try { await test.Run().WaitAsync(TimeSpan.FromSeconds(90)); results.Add(new { test.Id, status = "PASS_AUTHORED_NATIVE_ONLY" }); }
            catch (Exception error) { failures++; results.Add(new { test.Id, status = "FAIL", failure = error.ToString() }); }
        }
        var report = new { sourceSha = Upstream, status = "AUTHORED NATIVE; NO UPSTREAM CAPTURE", cases = cases.Count, failures,
            genuineSourceCasesCaptured = 0, results };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        if (args.Length == 2)
        {
            await using var file = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await file.WriteAsync(Encoding.UTF8.GetBytes(json));
        }
        Console.WriteLine(json);
        return failures == 0 ? 0 : 1;
    }

    private static Func<Task> Sync(Action run) => () => { run(); return Task.CompletedTask; };
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static void Equal<T>(T expected, T actual, string what)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{what}: expected <{expected}>, actual <{actual}>."); }
    private static void Names(IEnumerable<string> expected, IEnumerable<string> actual, string what) =>
        Check(expected.SequenceEqual(actual, StringComparer.Ordinal), $"{what}: expected [{string.Join(',', expected)}], actual [{string.Join(',', actual)}].");
    private static T Throws<T>(Action run, string what) where T : Exception
    {
        try { run(); } catch (T error) { return error; }
        throw new InvalidOperationException(what + ": expected " + typeof(T).Name + ".");
    }
    private static string Temp(string name) => Path.Combine(Path.GetFullPath(Path.GetTempPath()), "pisharp-tool-search", name);
}
