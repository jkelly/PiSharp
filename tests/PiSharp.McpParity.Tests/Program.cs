using System.Text;
using System.Text.Json;

// Authored offline expectations for IMPL-H (MCP completion) of PiSharp's full parity with Pi v1.1.0: project mcp.json with project
// trust, background connection with the first prompt's 10 s wait for servers with direct tools, resource tools, tool_search
// registration and waits, session close without durable withdrawal, the per-server OAuth refresh lock, auth.provider servers,
// sign-ins outside the session and the `/mcp` manager. Derived from the pinned upstream sources (extensions/mcp/index.ts, config.ts,
// oauth.ts, cli.ts, resources.ts, extensions/tool-search, core/mcp-servers.ts, docs/mcp.md) and their tests
// (test/suite/agent-session-mcp.test.ts, test/mcp-*.test.ts) by reading, never captured from an upstream run. Fake MCP servers, a fake
// authorization server and a fake Anthropic endpoint answer in process; no network or live credentials are used.
internal static partial class Program
{
    private const string Upstream = "abe508e1b89912adde45528136c3221eb69acdd7";

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 0 && (args.Length != 2 || args[0] != "--report")) throw new ArgumentException("Use [--report <fresh path>].");
        var cases = new List<(string Id, Func<Task> Run)>
        {
            ("project.untrusted-project-file-is-ignored-silently", UntrustedProject),
            ("project.trusted-project-adds-and-overrides-servers", TrustedProject),
            ("startup.first-prompt-waits-for-direct-servers", FirstPromptWaitsForDirectServers),
            ("startup.first-prompt-waits-at-most-the-startup-cap", FirstPromptWaitCap),
            ("startup.problems-reported-once-after-all-servers-settled", ProblemsReportedOnce),
            ("resources.direct-server-resources-declared-and-read", DirectResources),
            ("resources.deferred-server-resources-reached-through-tool-search", DeferredResources),
            ("tool-search.registered-inactive-and-selectable-without-deferred-servers", ToolSearchInactive),
            ("lifecycle.close-records-no-withdrawal", CloseRecordsNoWithdrawal),
            ("lifecycle.close-during-background-publication-does-not-fault", CloseDuringPublication),
            ("oauth.refresh-lock-file-name-and-stale-takeover", RefreshLockFile),
            ("oauth.refresh-lock-serializes-refreshes-of-one-server", RefreshLockSerializes),
            ("oauth.token-about-to-expire-is-refreshed-before-sending", RefreshBeforeExpiry),
            ("oauth.auth-provider-server-sends-the-provider-token", AuthProviderServer),
            ("oauth.sign-in-outside-the-session-reconnects-before-the-next-prompt", ExternalSignIn),
            ("oauth.mcp-login-command-client-secret-from-command", ClientSecretCommand),
            ("manager.status-command-and-menus", ManagerStatusAndMenus),
            ("manager.disable-enable-reconnect-and-exposure", ManagerActions),
            ("manager.project-override-enable-and-disable", ManagerProjectOverride),
            ("manager.login-logout-through-the-command", ManagerLoginLogout),
            ("notifications.server-log-and-tool-list-changes", NotificationsLogAndToolListChanges),
            ("notifications.server-log-format-and-rotation", ServerLogFormatAndRotation),
            ("cli.mcp-list-resource-and-template-counts", ListResourceCounts),
            ("prompt.servers-section-follows-manager-changes", ServersSectionFollowsManagerChanges),
            ("results.call-tool-results-convert-for-the-model", ToolResultConversion),
            ("retry.http-connect-retried-twice-after-transient-errors", ConnectRetries),
            ("retry.http-connect-fails-after-the-retries", ConnectRetriesExhausted),
            ("retry.expired-session-call-and-transient-read-retried", CallAndReadRetries),
            ("retry.tool-calls-are-not-retried-after-transient-errors", CallsAreNotRetried),
            ("registration.native-api-validation-ownership-and-change-event", NativeRegistrationApiAndEvent),
            ("registration.registered-servers-connect-override-and-disconnect", RegisteredServersConnect)
        };
        var results = new List<object>(); var failures = 0;
        foreach (var test in cases)
        {
            try { await test.Run().WaitAsync(TimeSpan.FromSeconds(120)); results.Add(new { test.Id, status = "PASS_AUTHORED_NATIVE_ONLY" }); }
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

    internal static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    internal static void Equal<T>(T expected, T actual, string what)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{what}: expected <{expected}>, actual <{actual}>."); }
    internal static void Names(IEnumerable<string> expected, IEnumerable<string> actual, string what) =>
        Check(expected.SequenceEqual(actual, StringComparer.Ordinal), $"{what}: expected [{string.Join(',', expected)}], actual [{string.Join(',', actual)}].");
    private static string Temp(string name) => Path.Combine(Path.GetFullPath(Path.GetTempPath()), "pisharp-mcp-parity", name);
}
