using System.Text.Json.Nodes;
using PiSharp.Cli.Interactive.Mode.Components;
using PiSharp.Cli.Pi;
using static Expect;

/// <summary>session-selector.ts, session-selector-search.ts, tree-selector.ts and formatResumeCommand (interactive-mode.ts): the
/// expectations of session-selector-search.test.ts, session-selector-rename.test.ts, session-selector-path-delete.test.ts,
/// tree-selector.test.ts and format-resume-command.test.ts, plus render snapshots authored from the sources.</summary>
internal static class SessionSelectorCases
{
    private const string CtrlD = "\u0004";
    private const string CtrlBackspace = "\u001b[127;5u";
    private const string CtrlR = "\u001b[114;5u";
    private const string Up = "\u001b[A";
    private const string Down = "\u001b[B";
    private const string CtrlLeft = "\u001b[1;5D";
    private const string CtrlRight = "\u001b[1;5C";
    private const string AltLeft = "\u001b[1;3D";
    private const string AltRight = "\u001b[1;3C";

    // ---- sessions -------------------------------------------------------------------------------------------------------------

    private static PiSessionInfo S(string id, string? name = null, string? path = null, string? parent = null, DateTimeOffset? modified = null,
        int count = 1, string first = "hello", string? all = null, string cwd = "") =>
        new(path ?? $"/tmp/{id}.jsonl", id, cwd, name, parent, DateTimeOffset.UnixEpoch, modified ?? DateTimeOffset.UnixEpoch, count, first, all ?? first);

    /// <summary>session-selector-search.test.ts makeSession.</summary>
    private static PiSessionInfo M(string id, string modified, string allMessagesText, string? name = null) =>
        new($"/tmp/{id}.jsonl", id, "", name, null, DateTimeOffset.UnixEpoch, DateTimeOffset.Parse(modified, System.Globalization.CultureInfo.InvariantCulture), 1, "(no messages)", allMessagesText);

    private static SessionsLoader Fixed(IReadOnlyList<PiSessionInfo> sessions) => (_, _) => Task.FromResult(sessions);
    private static readonly SessionsLoader Empty = Fixed([]);

    private static SessionSelectorComponent Selector(SessionsLoader current, SessionsLoader? all = null, SessionSelectorOptions? options = null,
        string? currentSessionFilePath = null) =>
        new(current, all ?? Empty, _ => { }, () => { }, () => { }, () => { }, options, currentSessionFilePath);

    private static string Plain(IEnumerable<string> lines) => string.Join("\n", Strip(lines));

    private static string Row(string left, string right, int width = 100) => left + new string(' ', Math.Max(1, width - left.Length - right.Length)) + right;

    private static string HeaderLine(string title, string right, int width = 100)
    {
        var left = title[..Math.Min(title.Length, Math.Max(0, width - right.Length - 1))];
        return left + new string(' ', Math.Max(0, width - left.Length - right.Length)) + right;
    }

    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    /// <summary>A (5m, unnamed), B (3h, named) and C (2d, forked from B).</summary>
    private static List<PiSessionInfo> Three(string cwd = "") =>
    [
        S("a", first: "fix the parser", count: 3, modified: Now.AddMinutes(-5), cwd: cwd),
        S("b", name: "Release prep", first: "x", count: 12, modified: Now.AddHours(-3), cwd: cwd),
        S("c", first: "follow up", count: 2, modified: Now.AddDays(-2), parent: "/tmp/b.jsonl", cwd: cwd),
    ];

    private const string HintsNoRename = "tab scope · re:<pattern> regex · \"phrase\" exact";
    private const string Hints2 = "ctrl+s sort · ctrl+n named · ctrl+d delete · ctrl+p path (off)";

    private static List<string> Frame(IEnumerable<string> header, IEnumerable<string> body) =>
        ["", new string('─', 100), "", .. header, "", .. body, "", new string('─', 100)];

    private sealed class FakeResumeSession(bool persisted = true, string? sessionFile = null, string sessionId = "0197f6e4-4cf9-7f44-a2d8-f8f7f49ee9d3",
        string sessionDir = "/tmp/pi-sessions", bool usesDefaultSessionDir = true) : ResumeCommand.ISession
    {
        public bool IsPersisted() => persisted;
        public string? GetSessionFile() => sessionFile;
        public string GetSessionId() => sessionId;
        public string GetSessionDir() => sessionDir;
        public bool UsesDefaultSessionDir() => usesDefaultSessionDir;
    }

    /// <summary>parent.jsonl and child.jsonl in a real directory reached through two symbolic-link aliases (skips when links cannot be made).</summary>
    private static (string ParentAliasA, string ParentAliasB, string ChildAliasB) SymlinkedSessionPaths(string baseDir)
    {
        var shared = Path.Combine(baseDir, "real", "sessions");
        Directory.CreateDirectory(shared);
        Directory.CreateDirectory(Path.Combine(baseDir, "alias-a"));
        Directory.CreateDirectory(Path.Combine(baseDir, "alias-b"));
        var aliasA = Path.Combine(baseDir, "alias-a", "sessions");
        var aliasB = Path.Combine(baseDir, "alias-b", "sessions");
        try
        {
            Directory.CreateSymbolicLink(aliasA, shared);
            Directory.CreateSymbolicLink(aliasB, shared);
        }
        catch (Exception error) when (OperatingSystem.IsWindows() && error is IOException or UnauthorizedAccessException)
        {
            // Without the symbolic-link privilege, directory junctions (which realpath also resolves) stand in.
            foreach (var alias in new[] { aliasA, aliasB })
            {
                if (Directory.Exists(alias)) continue;
                using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
                { ArgumentList = { "/c", "mklink", "/J", alias, shared }, UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
                mklink.StandardOutput.ReadToEnd();
                mklink.WaitForExit();
                if (mklink.ExitCode != 0) throw new SkipCaseException("Symbolic links and junctions unavailable: " + error.Message);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new SkipCaseException("Symbolic links unavailable: " + error.Message);
        }
        File.WriteAllText(Path.Combine(shared, "parent.jsonl"), "parent\n");
        File.WriteAllText(Path.Combine(shared, "child.jsonl"), "child\n");
        return (Path.Combine(aliasA, "parent.jsonl"), Path.Combine(aliasB, "parent.jsonl"), Path.Combine(aliasB, "child.jsonl"));
    }

    // ---- tree entries ---------------------------------------------------------------------------------------------------------

    private static string Iso() => PiSessions.IsoTimestamp(DateTimeOffset.UtcNow);

    private static JsonObject UserMessage(string id, string? parentId, string content) => new()
    {
        ["type"] = "message", ["id"] = id, ["parentId"] = parentId, ["timestamp"] = Iso(),
        ["message"] = new JsonObject { ["role"] = "user", ["content"] = content, ["timestamp"] = 0 }
    };

    private static JsonObject AssistantMessage(string id, string? parentId, string text) => new()
    {
        ["type"] = "message", ["id"] = id, ["parentId"] = parentId, ["timestamp"] = Iso(),
        ["message"] = new JsonObject
        {
            ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["api"] = "anthropic-messages", ["provider"] = "anthropic", ["model"] = "claude-sonnet-4", ["stopReason"] = "stop", ["timestamp"] = 0
        }
    };

    private static JsonObject ToolCallOnlyAssistant(string id, string? parentId) => new()
    {
        ["type"] = "message", ["id"] = id, ["parentId"] = parentId, ["timestamp"] = Iso(),
        ["message"] = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "toolCall", ["id"] = $"tc-{id}", ["name"] = "read", ["arguments"] = new JsonObject { ["path"] = "test.ts" } }),
            ["stopReason"] = "toolUse", ["timestamp"] = 0
        }
    };

    private static JsonObject ToolResult(string id, string? parentId, string toolCallId, string toolName) => new()
    {
        ["type"] = "message", ["id"] = id, ["parentId"] = parentId, ["timestamp"] = Iso(),
        ["message"] = new JsonObject { ["role"] = "toolResult", ["toolCallId"] = toolCallId, ["toolName"] = toolName, ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "ok" }) }
    };

    private static JsonObject ModelChange(string id, string? parentId) => new()
    {
        ["type"] = "model_change", ["id"] = id, ["parentId"] = parentId, ["timestamp"] = Iso(), ["provider"] = "anthropic", ["modelId"] = "claude-sonnet-4"
    };

    /// <summary>tree-selector.test.ts buildTree.</summary>
    private static List<SessionTreeNode> BuildTree(IEnumerable<JsonObject> entries)
    {
        var nodes = entries.Select(entry => new SessionTreeNode(entry)).ToList();
        var byId = nodes.ToDictionary(node => (string)node.Entry["id"]!);
        var roots = new List<SessionTreeNode>();
        foreach (var node in nodes)
        {
            var parentId = (string?)node.Entry["parentId"];
            if (parentId is null) roots.Add(node);
            else if (byId.TryGetValue(parentId, out var parent)) parent.Children.Add(node);
        }
        return roots;
    }

    private static TreeSelectorComponent Tree(List<SessionTreeNode> tree, string? leaf, FilterMode? filter = null, Action<string, string?>? onLabelChange = null) =>
        new(tree, leaf, 24, _ => { }, () => { }, onLabelChange, null, filter);

    private static string? SelectedId(TreeSelectorComponent selector) => (string?)selector.GetTreeList().GetSelectedNode()?.Entry["id"];

    private static List<SessionTreeNode> BranchingTree() => BuildTree(
    [
        UserMessage("user-1", null, "first message"),
        AssistantMessage("asst-1", "user-1", "response 1"),
        UserMessage("user-2", "asst-1", "second message"),
        AssistantMessage("asst-2", "user-2", "response 2"),
        // Branch A (active)
        UserMessage("user-3a", "asst-2", "branch A start"),
        AssistantMessage("asst-3a", "user-3a", "branch A response"),
        UserMessage("user-4a", "asst-3a", "branch A deep"),
        AssistantMessage("asst-4a", "user-4a", "branch A leaf"),
        // Branch B
        UserMessage("user-3b", "asst-2", "branch B start"),
        AssistantMessage("asst-3b", "user-3b", "branch B response"),
        UserMessage("user-4b", "asst-3b", "branch B deep"),
    ]);

    public static IEnumerable<(string Id, Func<Task> Run)> All()
    {
        // ---- session-selector-search.test.ts ----------------------------------------------------------------------------------
        yield return ("ses.search.phrase-whitespace-normalized", Sync(() =>
        {
            var result = SessionSelectorSearch.FilterAndSortSessions(
                [M("a", "2026-01-01T00:00:00.000Z", "node\n\n   cve was discussed"), M("b", "2026-01-02T00:00:00.000Z", "node something else")], "\"node cve\"", SortMode.Recent);
            Equal("a", string.Join(",", result.Select(s => s.Id)), "phrase match");
        }));
        yield return ("ses.search.regex-case-insensitive", Sync(() =>
        {
            var result = SessionSelectorSearch.FilterAndSortSessions(
                [M("a", "2026-01-02T00:00:00.000Z", "Brave is great"), M("b", "2026-01-03T00:00:00.000Z", "bravery is not the same")], "re:\\bbrave\\b", SortMode.Recent);
            Equal("a", string.Join(",", result.Select(s => s.Id)), "regex match");
        }));
        yield return ("ses.search.recent-preserves-input-order", Sync(() =>
        {
            var result = SessionSelectorSearch.FilterAndSortSessions(
                [M("newer", "2026-01-03T00:00:00.000Z", "brave"), M("older", "2026-01-01T00:00:00.000Z", "brave"), M("nomatch", "2026-01-04T00:00:00.000Z", "something else")],
                "\"brave\"", SortMode.Recent);
            Equal("newer,older", string.Join(",", result.Select(s => s.Id)), "recent order");
        }));
        yield return ("ses.search.relevance-score-then-modified", Sync(() =>
        {
            var result1 = SessionSelectorSearch.FilterAndSortSessions(
                [M("late", "2026-01-03T00:00:00.000Z", "xxxx brave"), M("early", "2026-01-01T00:00:00.000Z", "brave xxxx")], "\"brave\"", SortMode.Relevance);
            Equal("early,late", string.Join(",", result1.Select(s => s.Id)), "score order");
            var result2 = SessionSelectorSearch.FilterAndSortSessions(
                [M("older", "2026-01-01T00:00:00.000Z", "brave"), M("newer", "2026-01-03T00:00:00.000Z", "brave")], "\"brave\"", SortMode.Relevance);
            Equal("newer,older", string.Join(",", result2.Select(s => s.Id)), "tie-break by modified desc");
        }));
        yield return ("ses.search.invalid-regex-empty", Sync(() =>
        {
            Equal(0, SessionSelectorSearch.FilterAndSortSessions([M("a", "2026-01-01T00:00:00.000Z", "brave")], "re:(", SortMode.Recent).Count, "invalid regex");
            Equal(0, SessionSelectorSearch.FilterAndSortSessions([M("a", "2026-01-01T00:00:00.000Z", "brave")], "re:", SortMode.Recent).Count, "empty regex");
            Equal("Empty regex", SessionSelectorSearch.ParseSearchQuery("re:  ").Error, "empty regex error");
        }));
        List<PiSessionInfo> named =
        [
            M("named1", "2026-01-03T00:00:00.000Z", "blueberry", "My Project"),
            M("named2", "2026-01-02T00:00:00.000Z", "blueberry", "Another Named"),
            M("other1", "2026-01-04T00:00:00.000Z", "blueberry"),
            M("other2", "2026-01-01T00:00:00.000Z", "blueberry"),
        ];
        yield return ("ses.search.name-filter-all", Sync(() =>
            Equal("named1,named2,other1,other2", string.Join(",", SessionSelectorSearch.FilterAndSortSessions(named, "", SortMode.Recent, NameFilter.All).Select(s => s.Id)), "all")));
        yield return ("ses.search.name-filter-named", Sync(() =>
            Equal("named1,named2", string.Join(",", SessionSelectorSearch.FilterAndSortSessions(named, "", SortMode.Recent, NameFilter.Named).Select(s => s.Id)), "named")));
        yield return ("ses.search.name-filter-before-query", Sync(() =>
            Equal("named1,named2", string.Join(",", SessionSelectorSearch.FilterAndSortSessions(named, "blueberry", SortMode.Recent, NameFilter.Named).Select(s => s.Id)), "named + query")));
        yield return ("ses.search.whitespace-names-not-named", Sync(() =>
        {
            var result = SessionSelectorSearch.FilterAndSortSessions(
                [M("whitespace", "2026-01-01T00:00:00.000Z", "test", "   "), M("empty", "2026-01-02T00:00:00.000Z", "test", ""), M("named", "2026-01-03T00:00:00.000Z", "test", "Real Name")],
                "", SortMode.Recent, NameFilter.Named);
            Equal("named", string.Join(",", result.Select(s => s.Id)), "whitespace names");
        }));
        yield return ("ses.search.parse-tokens", Sync(() =>
        {
            var parsed = SessionSelectorSearch.ParseSearchQuery("foo \"node  cve\" bar");
            Equal("fuzzy:foo|phrase:node  cve|fuzzy:bar", string.Join("|", parsed.Tokens.Select(t => t.Kind + ":" + t.Value)), "quoted tokens");
            var unbalanced = SessionSelectorSearch.ParseSearchQuery("foo \"bar baz");
            Equal("fuzzy:foo|fuzzy:\"bar|fuzzy:baz", string.Join("|", unbalanced.Tokens.Select(t => t.Kind + ":" + t.Value)), "unbalanced quote falls back");
            Equal("tokens", SessionSelectorSearch.ParseSearchQuery("   ").Mode, "empty query");
        }));

        // ---- session-selector-path-delete.test.ts -----------------------------------------------------------------------------
        yield return ("ses.delete.ctrl-backspace-with-query-not-delete", Sync(() =>
        {
            var selector = Selector(Fixed([S("a"), S("b")]));
            var list = selector.GetSessionList();
            var changes = new List<string?>();
            list.OnDeleteConfirmationChange = changes.Add;
            list.HandleInput("a");
            list.HandleInput(CtrlBackspace);
            Equal(0, changes.Count, "no confirmation");
        }));
        yield return ("ses.delete.ctrl-d-with-query-confirms", Sync(() =>
        {
            var sessions = new List<PiSessionInfo> { S("a"), S("b") };
            var selector = Selector(Fixed(sessions));
            var list = selector.GetSessionList();
            var changes = new List<string?>();
            list.OnDeleteConfirmationChange = changes.Add;
            list.HandleInput("a");
            list.HandleInput(CtrlD);
            Equal(sessions[0].Path, string.Join(",", changes), "confirmation path");
        }));
        yield return ("ses.delete.ctrl-backspace-empty-confirms-then-enter-deletes", Sync(() =>
        {
            var sessions = new List<PiSessionInfo> { S("a"), S("b") };
            var selector = Selector(Fixed(sessions));
            var list = selector.GetSessionList();
            var changes = new List<string?>();
            list.OnDeleteConfirmationChange = changes.Add;
            string? deleted = null;
            list.OnDeleteSession = path => { deleted = path; return Task.CompletedTask; };
            list.HandleInput(CtrlBackspace);
            Equal(sessions[0].Path, string.Join(",", changes), "confirmation");
            list.HandleInput("\r");
            Equal(sessions[0].Path + ",<null>", string.Join(",", changes.Select(c => c ?? "<null>")), "confirmation cleared");
            Equal(sessions[0].Path, deleted, "deleted path");
        }));
        yield return ("ses.scope.all-resolving-after-toggle-back-keeps-current", Sync(() =>
        {
            var deferred = new TaskCompletionSource<IReadOnlyList<PiSessionInfo>>();
            var allLoadCalls = 0;
            var selector = Selector(Fixed([S("current")]), (_, _) => { allLoadCalls++; return deferred.Task; });
            var list = selector.GetSessionList();
            list.HandleInput("\t");
            list.HandleInput("\t");
            deferred.SetResult([S("all")]);
            Equal(1, allLoadCalls, "all loads");
            var output = Plain(selector.Render(120));
            Contains(output, "Resume Session (Current Folder)", "current title");
            Check(!output.Contains("Resume Session (All)", StringComparison.Ordinal), "all title absent");
        }));
        yield return ("ses.scope.no-redundant-all-loads", Sync(() =>
        {
            var allSessions = new List<PiSessionInfo> { S("all") };
            var deferred = new TaskCompletionSource<IReadOnlyList<PiSessionInfo>>();
            var allLoadCalls = 0;
            var selector = Selector(Fixed([S("current")]), (onProgress, _) => { allLoadCalls++; onProgress?.Invoke(1, 2, allSessions); return deferred.Task; });
            var list = selector.GetSessionList();
            list.HandleInput("\t");
            list.HandleInput("\t");
            list.HandleInput("\t");
            Equal(1, allLoadCalls, "all loads");
            Equal(allSessions[0].Path, selector.GetSessionList().GetSelectedSessionPath(), "partial sessions shown");
            Contains(Plain(selector.Render(120)), "Loading", "loading header");
            deferred.SetResult(allSessions);
            Check(!Plain(selector.Render(120)).Contains("Loading", StringComparison.Ordinal), "loaded");
        }));
        yield return ("ses.thread.symlink-aliases", Sync(() =>
        {
            using var dir = new TempDir();
            var paths = SymlinkedSessionPaths(dir.Path);
            var selector = Selector(Fixed(
            [
                S("parent", name: "Parent", path: paths.ParentAliasB, modified: DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)),
                S("child", name: "Child", path: paths.ChildAliasB, parent: paths.ParentAliasA, modified: DateTimeOffset.Parse("2025-12-31T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)),
            ]));
            var output = Plain(selector.Render(120));
            Contains(output, "Parent", "parent");
            Contains(output, "└─ Child", "threaded child");
        }));
        yield return ("ses.thread.sorted-by-subtree-activity", Sync(() =>
        {
            var parentOne = S("parent-one", name: "Parent one", modified: DateTimeOffset.Parse("2026-01-02T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
            var parentTwo = S("parent-two", name: "Parent two", modified: DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
            var childTwo = S("child-two", name: "Child two", parent: parentTwo.Path, modified: DateTimeOffset.Parse("2026-01-03T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
            var output = Plain(Selector(Fixed([parentOne, parentTwo, childTwo])).Render(120));
            var parentTwoIndex = output.IndexOf("Parent two", StringComparison.Ordinal);
            var childTwoIndex = output.IndexOf("└─ Child two", StringComparison.Ordinal);
            var parentOneIndex = output.IndexOf("Parent one", StringComparison.Ordinal);
            Check(parentTwoIndex >= 0, "parent two shown");
            Check(childTwoIndex > parentTwoIndex, "child after its parent");
            Check(parentOneIndex > childTwoIndex, "older subtree last");
        }));
        yield return ("ses.delete.current-session-across-aliases-refused", Sync(() =>
        {
            using var dir = new TempDir();
            var paths = SymlinkedSessionPaths(dir.Path);
            var selector = Selector(Fixed([S("parent", name: "Parent", path: paths.ParentAliasB)]), currentSessionFilePath: paths.ParentAliasA);
            var list = selector.GetSessionList();
            var changes = new List<string?>();
            string? error = null;
            list.OnDeleteConfirmationChange = changes.Add;
            list.OnError = message => error = message;
            list.HandleInput(CtrlD);
            Equal(0, changes.Count, "no confirmation");
            Equal("Cannot delete the currently active session", error, "error");
        }));
        yield return ("ses.delete.current-session-status-auto-hides", Sync(() =>
        {
            var timers = new List<(Action Action, double Ms)>();
            var selector = Selector(Fixed([S("a", first: "only")]), options: new SessionSelectorOptions { SetTimeout = (action, ms) => { timers.Add((action, ms)); return new Disposable(); } },
                currentSessionFilePath: "/tmp/a.jsonl");
            selector.HandleInput(CtrlD);
            var lines = Strip(selector.Render(100));
            Equal("Cannot delete the currently active session", lines[4].TrimEnd(), "status line");
            Equal("", lines[5].TrimEnd(), "second hint line blank");
            Equal(1, timers.Count, "one timer");
            Equal(3000.0, timers[0].Ms, "auto-hide delay");
            timers[0].Action();
            Equal(HintsNoRename, Strip(selector.Render(100))[4].TrimEnd(), "hints restored");
        }));

        // ---- delete flow --------------------------------------------------------------------------------------------------------
        yield return ("ses.delete.flow-removes-and-reloads", Sync(() =>
        {
            var sessions = new List<PiSessionInfo> { S("a", first: "first"), S("b", first: "second") };
            var loads = 0; string? deleted = null;
            var selector = Selector((_, _) => { loads++; return Task.FromResult<IReadOnlyList<PiSessionInfo>>([.. sessions]); }, options: new SessionSelectorOptions
            {
                DeleteSessionFile = path => { deleted = path; sessions.RemoveAll(s => s.Path == path); return Task.FromResult(new DeleteSessionFileResult(true, "unlink")); },
                SetTimeout = (_, _) => new Disposable(),
            });
            selector.HandleInput(CtrlD);
            var confirming = Strip(selector.Render(100));
            Equal("Delete session? enter confirm · escape/ctrl+c cancel", confirming[4].TrimEnd(), "confirm hint");
            selector.HandleInput("x"); // ignored while confirming
            selector.HandleInput("\r");
            Equal("/tmp/a.jsonl", deleted, "deleted");
            Equal(2, loads, "reloaded after delete");
            var lines = Strip(selector.Render(100));
            Equal("Session deleted", lines[4].TrimEnd(), "status");
            Check(!Plain(selector.Render(100)).Contains("first", StringComparison.Ordinal), "deleted row gone");
            Contains(Plain(selector.Render(100)), "second", "remaining row");
        }));
        yield return ("ses.delete.flow-trash-and-failure-messages", Sync(() =>
        {
            var result = new DeleteSessionFileResult(true, "trash");
            var selector = Selector(Fixed([S("a")]), options: new SessionSelectorOptions { DeleteSessionFile = _ => Task.FromResult(result), SetTimeout = (_, _) => new Disposable() });
            selector.HandleInput(CtrlD); selector.HandleInput("\r");
            Equal("Session moved to trash", Strip(selector.Render(100))[4].TrimEnd(), "trash status");
            result = new DeleteSessionFileResult(false, "unlink", "EACCES: permission denied");
            selector.HandleInput(CtrlD); selector.HandleInput("\r");
            Equal("Failed to delete: EACCES: permission denied", Strip(selector.Render(100))[4].TrimEnd(), "failure status");
            selector.HandleInput(CtrlD); selector.HandleInput("\u001b");
            Equal("Failed to delete: EACCES: permission denied", Strip(selector.Render(100))[4].TrimEnd(), "escape cancels confirmation; status stays");
        }));
        yield return ("ses.delete.default-trash-or-unlink", Sync(() =>
        {
            using var dir = new TempDir();
            var file = Path.Combine(dir.Path, "s.jsonl");
            File.WriteAllText(file, "{}\n");
            var result = SessionSelectorComponent.DeleteSessionFile(file).GetAwaiter().GetResult();
            Check(result.Ok, "deleted: " + result.Error);
            Check(result.Method is "trash" or "unlink", "method " + result.Method);
            Check(!File.Exists(file), "file gone");
            var sub = Path.Combine(dir.Path, "dir.jsonl");
            Directory.CreateDirectory(sub);
            var failed = SessionSelectorComponent.DeleteSessionFile(sub).GetAwaiter().GetResult();
            if (!failed.Ok)
            {
                Equal("unlink", failed.Method, "failure method");
                Check(failed.Error!.StartsWith("EISDIR", StringComparison.Ordinal), "unlink error first: " + failed.Error);
            }
        }));
        yield return ("ses.load.failure-status", Sync(() =>
        {
            var selector = Selector((_, _) => Task.FromException<IReadOnlyList<PiSessionInfo>>(new InvalidOperationException("disk gone")),
                options: new SessionSelectorOptions { SetTimeout = (_, _) => new Disposable() });
            var lines = Strip(selector.Render(100));
            Equal("Failed to load sessions: disk gone", lines[4].TrimEnd(), "status");
            Equal("  No sessions in current folder. Press Tab to view all.", lines[9].TrimEnd(), "empty list");
        }));

        // ---- session-selector-rename.test.ts ----------------------------------------------------------------------------------
        yield return ("ses.rename.hint-shown", Sync(() =>
        {
            var output = Plain(Selector(Fixed([S("a")]), options: new SessionSelectorOptions { ShowRenameHint = true }).Render(120));
            Contains(output, "ctrl+r", "key");
            Contains(output, "rename", "label");
        }));
        yield return ("ses.rename.hint-hidden", Sync(() =>
        {
            var output = Plain(Selector(Fixed([S("a")]), options: new SessionSelectorOptions { ShowRenameHint = false }).Render(120));
            Check(!output.Contains("ctrl+r", StringComparison.Ordinal), "no key");
            Check(!output.Contains("rename", StringComparison.Ordinal), "no label");
        }));
        yield return ("ses.rename.ctrl-r-then-enter", Sync(() =>
        {
            var sessions = new List<PiSessionInfo> { S("a", name: "Old") };
            var calls = new List<(string Path, string? Name)>();
            var selector = Selector(Fixed(sessions), options: new SessionSelectorOptions
            {
                RenameSession = (path, name) => { calls.Add((path, name)); return Task.CompletedTask; },
                ShowRenameHint = true,
            });
            selector.GetSessionList().HandleInput(CtrlR);
            var output = Plain(selector.Render(120));
            Contains(output, "Rename Session", "rename layout");
            Check(!output.Contains("Resume Session", StringComparison.Ordinal), "header hidden");
            selector.HandleInput("X");
            selector.HandleInput("\r");
            Equal(1, calls.Count, "rename calls");
            Equal(sessions[0].Path, calls[0].Path, "path");
            Equal("XOld", calls[0].Name, "new name");
            Contains(Plain(selector.Render(120)), "Resume Session", "back to list");
        }));
        yield return ("ses.rename.render-and-escape", Sync(() =>
        {
            var selector = Selector(Fixed([S("a", name: "Old")]), options: new SessionSelectorOptions { RenameSession = (_, _) => Task.CompletedTask });
            selector.HandleInput(CtrlR);
            Lines(["", new string('─', 100), "", " Rename Session", "", "> Old", "", " enter to save · escape/ctrl+c to cancel", "", new string('─', 100)],
                selector.Render(100), "rename panel");
            selector.HandleInput("\u001b");
            Contains(Plain(selector.Render(100)), "Resume Session (Current Folder)", "list restored");
            selector.HandleInput(CtrlR);
            selector.HandleInput("\u000b"); // ctrl+k clears the input (cursor at start)
            selector.HandleInput("   ");
            selector.HandleInput("\r"); // blank names are ignored
            Contains(Plain(selector.Render(100)), "Rename Session", "still renaming");
        }));
        yield return ("ses.rename.unavailable-without-callback", Sync(() =>
        {
            var selector = Selector(Fixed([S("a", name: "Old")]));
            selector.HandleInput(CtrlR);
            Contains(Plain(selector.Render(100)), "Resume Session", "no rename mode");
        }));

        // ---- render snapshots -------------------------------------------------------------------------------------------------
        yield return ("ses.render.current-threaded", Sync(() =>
        {
            var selector = Selector(Fixed(Three()));
            Lines(Frame(
                [HeaderLine("Resume Session (Current Folder)", "◉ Current Folder | ○ All  Name: All  Sort: Threaded"), HintsNoRename, Hints2],
                [">", "", Row("› fix the parser", "3 5m"), Row("  Release prep", "12 3h"), Row("     └─ follow up", "2 2d")]), selector.Render(100), "current threaded");
        }));
        yield return ("ses.render.header-truncates-title", Sync(() =>
        {
            var lines = Strip(Selector(Fixed(Three())).Render(80));
            Equal(HeaderLine("Resume Session (Current Folder)", "◉ Current Folder | ○ All  Name: All  Sort: Threaded", 80), lines[3], "header at 80");
            Equal("Resume Session (Current Fold ◉ Current Folder | ○ All  Name: All  Sort: Threaded", lines[3], "title cut to fit");
        }));
        yield return ("ses.render.all-scope", Sync(() =>
        {
            var selector = Selector(Fixed(Three()), Fixed(Three("/work/proj")));
            selector.HandleInput("\t");
            Lines(Frame(
                [HeaderLine("Resume Session (All)", "○ Current Folder | ◉ All  Name: All  Sort: Threaded"), HintsNoRename, Hints2],
                [">", "", Row("› fix the parser", "/work/proj 3 5m"), Row("  Release prep", "/work/proj 12 3h"), Row("     └─ follow up", "/work/proj 2 2d")]),
                selector.Render(100), "all scope");
            selector.HandleInput("\t");
            Contains(Plain(selector.Render(100)), "◉ Current Folder | ○ All", "back to current");
        }));
        yield return ("ses.render.all-scope-loading", Sync(() =>
        {
            var deferred = new TaskCompletionSource<IReadOnlyList<PiSessionInfo>>();
            SessionListProgress? progress = null;
            var selector = Selector(Fixed(Three()), (onProgress, _) => { progress = onProgress; return deferred.Task; });
            selector.HandleInput("\t");
            var lines = Strip(selector.Render(100));
            Equal(HeaderLine("Resume Session (All)", "○ Current Folder | Loading ...  Name: All  Sort: Threaded"), lines[3], "loading header");
            Equal("  No sessions found", lines[9].TrimEnd(), "empty while loading");
            progress!(3, 7, null);
            Equal(HeaderLine("Resume Session (All)", "○ Current Folder | Loading 3/7  Name: All  Sort: Threaded"), Strip(selector.Render(100))[3], "progress");
            deferred.SetResult(Three("/w"));
            Equal(Row("› fix the parser", "/w 3 5m"), Strip(selector.Render(100))[9], "loaded");
        }));
        yield return ("ses.render.sort-modes", Sync(() =>
        {
            var selector = Selector(Fixed(Three()));
            selector.HandleInput("\u0013"); // ctrl+s: recent
            Lines(Frame(
                [HeaderLine("Resume Session (Current Folder)", "◉ Current Folder | ○ All  Name: All  Sort: Recent"), HintsNoRename, Hints2],
                [">", "", Row("› fix the parser", "3 5m"), Row("  Release prep", "12 3h"), Row("  follow up", "2 2d")]), selector.Render(100), "recent");
            selector.HandleInput("\u0013"); // relevance
            Equal(HeaderLine("Resume Session (Current Folder)", "◉ Current Folder | ○ All  Name: All  Sort: Fuzzy"), Strip(selector.Render(100))[3], "fuzzy header");
            selector.HandleInput("\u0013"); // threaded
            Equal(Row("     └─ follow up", "2 2d"), Strip(selector.Render(100))[11], "threaded again");
        }));
        yield return ("ses.render.named-filter", Sync(() =>
        {
            var selector = Selector(Fixed(Three()));
            selector.HandleInput("\u000e"); // ctrl+n
            Lines(Frame(
                [HeaderLine("Resume Session (Current Folder)", "◉ Current Folder | ○ All  Name: Named  Sort: Threaded"), HintsNoRename, Hints2],
                [">", "", Row("› Release prep", "12 3h")]), selector.Render(100), "named");
            var empty = Selector(Fixed([S("a")]));
            empty.HandleInput("\u000e");
            Equal("  No named sessions in current folder. Press ctrl+n to show all, or Tab to view all.", Strip(empty.Render(100))[9].TrimEnd(), "named empty current");
            var emptyAll = Selector(Fixed([]), Fixed([S("a")]));
            emptyAll.HandleInput("\t");
            emptyAll.HandleInput("\u000e");
            Equal("  No named sessions found. Press ctrl+n to show all.", Strip(emptyAll.Render(100))[9].TrimEnd(), "named empty all");
        }));
        yield return ("ses.render.search", Sync(() =>
        {
            var selector = Selector(Fixed(Three()));
            foreach (var ch in "follow") selector.HandleInput(ch.ToString());
            var lines = Strip(selector.Render(100));
            Equal("> follow", lines[7].TrimEnd(), "query");
            Equal(Row("› follow up", "2 2d"), lines[9], "flat match");
            Equal(12, lines.Count, "one match");
            var none = Selector(Fixed(Three()));
            foreach (var ch in "zzz") none.HandleInput(ch.ToString());
            Equal("  No sessions in current folder. Press Tab to view all.", Strip(none.Render(100))[9].TrimEnd(), "no match");
        }));
        yield return ("ses.render.selection-path-and-current", Sync(() =>
        {
            var selector = Selector(Fixed(Three()), currentSessionFilePath: "/tmp/b.jsonl");
            selector.HandleInput(Down);
            selector.HandleInput("\u0010"); // ctrl+p: path on
            var lines = Strip(selector.Render(100));
            Equal("ctrl+s sort · ctrl+n named · ctrl+d delete · ctrl+p path (on)", lines[5].TrimEnd(), "path hint");
            Equal(Row("  fix the parser", "/tmp/a.jsonl 3 5m"), lines[9], "unselected row with path");
            Equal(Row("› Release prep", "/tmp/b.jsonl 12 3h"), lines[10], "selected current row");
            var raw = selector.Render(100);
            Contains(raw[10], PiSharp.Cli.Interactive.Mode.ThemeGlobals.theme.Fg("accent", "Release prep"), "current session in accent");
            Contains(raw[9], PiSharp.Cli.Interactive.Mode.ThemeGlobals.theme.Fg("dim", "/tmp/a.jsonl 3 5m"), "dim right part");
        }));
        yield return ("ses.render.scroll-indicator", Sync(() =>
        {
            var many = Enumerable.Range(0, 15).Select(i => S($"s{i:00}", first: $"message {i:00}", modified: Now.AddMinutes(-10 - i))).ToList();
            var selector = Selector(Fixed(many));
            for (var i = 0; i < 9; i++) selector.HandleInput(Down);
            var lines = Strip(selector.Render(100));
            Equal(Row("  message 04", "1 14m"), lines[9], "first visible");
            Equal(Row("› message 09", "1 19m"), lines[14], "selected centered");
            Equal("  (10/15)", lines[19].TrimEnd(), "scroll indicator");
            selector.HandleInput("\u001b[6~"); // pageDown
            Equal("  (15/15)", Strip(selector.Render(100))[19].TrimEnd(), "page down clamps");
        }));
        yield return ("ses.select-and-cancel-callbacks", Sync(() =>
        {
            string? selected = null; var cancelled = 0;
            var selector = new SessionSelectorComponent(Fixed(Three()), Empty, path => selected = path, () => cancelled++, () => { }, () => { });
            selector.HandleInput(Down);
            selector.HandleInput("\r");
            Equal("/tmp/b.jsonl", selected, "selected path");
            selector.HandleInput("\u001b");
            Equal(1, cancelled, "cancelled");
        }));
        yield return ("ses.format-session-date", Sync(() =>
        {
            var now = DateTimeOffset.Parse("2026-06-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
            string F(TimeSpan ago) => SessionList.FormatSessionDate(now - ago, now);
            Equal("now", F(TimeSpan.FromSeconds(59)), "now");
            Equal("59m", F(TimeSpan.FromMinutes(59)), "minutes");
            Equal("23h", F(TimeSpan.FromHours(23)), "hours");
            Equal("6d", F(TimeSpan.FromDays(6)), "days");
            Equal("4w", F(TimeSpan.FromDays(29)), "weeks");
            Equal("12mo", F(TimeSpan.FromDays(364)), "months");
            Equal("2y", F(TimeSpan.FromDays(800)), "years");
        }));

        // ---- format-resume-command.test.ts ------------------------------------------------------------------------------------
        yield return ("ses.resume.default-session-dir", Sync(() =>
        {
            using var dir = new TempDir();
            var file = Path.Combine(dir.Path, "session.jsonl"); File.WriteAllText(file, "\n");
            Equal("pi --session test-session", ResumeCommand.FormatResumeCommand(new FakeResumeSession(sessionFile: file, sessionId: "test-session"), true), "command");
        }));
        yield return ("ses.resume.custom-session-dir-unquoted", Sync(() =>
        {
            using var dir = new TempDir();
            var file = Path.Combine(dir.Path, "session.jsonl"); File.WriteAllText(file, "\n");
            Equal("pi --session-dir /tmp/custom-pi-sessions --session test-session",
                ResumeCommand.FormatResumeCommand(new FakeResumeSession(sessionFile: file, sessionId: "test-session", sessionDir: "/tmp/custom-pi-sessions", usesDefaultSessionDir: false), true), "command");
        }));
        yield return ("ses.resume.quotes-spaces", Sync(() =>
        {
            using var dir = new TempDir();
            var file = Path.Combine(dir.Path, "session.jsonl"); File.WriteAllText(file, "\n");
            Equal("pi --session-dir '/tmp/custom pi sessions' --session test-session",
                ResumeCommand.FormatResumeCommand(new FakeResumeSession(sessionFile: file, sessionId: "test-session", sessionDir: "/tmp/custom pi sessions", usesDefaultSessionDir: false), true), "command");
        }));
        yield return ("ses.resume.quotes-single-quotes", Sync(() =>
        {
            using var dir = new TempDir();
            var file = Path.Combine(dir.Path, "session.jsonl"); File.WriteAllText(file, "\n");
            Equal("pi --session-dir '/tmp/custom pi'\\''s sessions' --session test-session",
                ResumeCommand.FormatResumeCommand(new FakeResumeSession(sessionFile: file, sessionId: "test-session", sessionDir: "/tmp/custom pi's sessions", usesDefaultSessionDir: false), true), "command");
        }));
        yield return ("ses.resume.not-tty", Sync(() =>
        {
            using var dir = new TempDir();
            var file = Path.Combine(dir.Path, "session.jsonl"); File.WriteAllText(file, "\n");
            Equal(null, ResumeCommand.FormatResumeCommand(new FakeResumeSession(sessionFile: file), false), "not a tty");
        }));
        yield return ("ses.resume.in-memory", Sync(() =>
        {
            using var dir = new TempDir();
            var file = Path.Combine(dir.Path, "session.jsonl"); File.WriteAllText(file, "\n");
            Equal(null, ResumeCommand.FormatResumeCommand(new FakeResumeSession(persisted: false, sessionFile: file), true), "in memory");
        }));
        yield return ("ses.resume.missing-file", Sync(() =>
            Equal(null, ResumeCommand.FormatResumeCommand(new FakeResumeSession(sessionFile: Path.Combine(Path.GetTempPath(), "pi-missing-session-" + Guid.NewGuid().ToString("N") + ".jsonl")), true), "missing")));
        yield return ("ses.resume.no-file", Sync(() =>
            Equal(null, ResumeCommand.FormatResumeCommand(new FakeResumeSession(sessionFile: null), true), "unset")));

        // ---- tree-selector.test.ts --------------------------------------------------------------------------------------------
        yield return ("ses.tree.initial-model-change-leaf", Sync(() =>
        {
            var tree = BuildTree([UserMessage("user-1", null, "hello"), AssistantMessage("asst-1", "user-1", "hi"), UserMessage("user-2", "asst-1", "active branch"),
                ModelChange("model-1", "user-2"), UserMessage("user-3", "asst-1", "sibling branch")]);
            Equal("user-2", SelectedId(Tree(tree, "model-1")), "nearest visible ancestor");
        }));
        yield return ("ses.tree.context-edit-hidden-and-labeled-in-all", Sync(() =>
        {
            var tree = BuildTree([UserMessage("user-1", null, "hello"), AssistantMessage("asst-1", "user-1", "hi"),
                new JsonObject { ["type"] = "context_edit", ["id"] = "edit-1", ["parentId"] = "asst-1", ["timestamp"] = Iso(), ["targetId"] = "asst-1", ["replacement"] = null }]);
            Equal("asst-1", SelectedId(Tree(tree, "edit-1")), "default hides context edits");
            var all = Tree(tree, "edit-1", FilterMode.All);
            Contains(Plain(all.GetTreeList().Render(200)), "[context omit: asst-1]", "all mode label");
            Lines(["  • user: hello", "  • assistant: hi", "› • [context omit: asst-1]", "  (3/3) [all]"], all.GetTreeList().Render(80), "all render");
        }));
        yield return ("ses.tree.initial-thinking-level-leaf", Sync(() =>
        {
            var tree = BuildTree([UserMessage("user-1", null, "hello"), AssistantMessage("asst-1", "user-1", "hi"), UserMessage("user-2", "asst-1", "active branch"),
                new JsonObject { ["type"] = "thinking_level_change", ["id"] = "thinking-1", ["parentId"] = "user-2", ["timestamp"] = Iso(), ["thinkingLevel"] = "high" },
                UserMessage("user-3", "asst-1", "sibling branch")]);
            Equal("user-2", SelectedId(Tree(tree, "thinking-1")), "nearest visible ancestor");
        }));
        List<SessionTreeNode> SwitchTree() => BuildTree([UserMessage("user-1", null, "hello"), AssistantMessage("asst-1", "user-1", "hi"),
            UserMessage("user-2", "asst-1", "active branch"), AssistantMessage("asst-2", "user-2", "response"), UserMessage("user-3", "asst-1", "sibling branch")]);
        yield return ("ses.tree.user-only-nearest-user", Sync(() =>
        {
            var selector = Tree(SwitchTree(), "asst-2");
            Equal("asst-2", SelectedId(selector), "initial");
            selector.HandleInput("\u0015");
            Equal("user-2", SelectedId(selector), "user-only");
        }));
        yield return ("ses.tree.back-to-default-keeps-ancestor", Sync(() =>
        {
            var selector = Tree(SwitchTree(), "asst-2");
            selector.HandleInput("\u0015");
            Equal("user-2", SelectedId(selector), "user-only");
            selector.HandleInput(CtrlD);
            Equal("user-2", SelectedId(selector), "default");
        }));
        yield return ("ses.tree.help-narrow", Sync(() =>
        {
            var selector = Tree(BuildTree([UserMessage("user-1", null, "hello"), AssistantMessage("asst-1", "user-1", "hi")]), "asst-1");
            var plainLines = Strip(selector.Render(30));
            var plain = string.Join("\n", plainLines);
            foreach (var word in new[] { "branch", "copy", "filters", "cycle", "label time" }) Contains(plain, word, "help " + word);
            Check(!plain.Contains("...", StringComparison.Ordinal), "no truncation");
            Check(plainLines.All(line => PiSharp.Tui.Pi.TextUtils.VisibleWidth(line) <= 30), "fits 30 columns");
        }));
        yield return ("ses.tree.copy-full-message", Sync(() =>
        {
            var message = string.Concat(Enumerable.Repeat("long message ", 30)) + "\nsecond line";
            var selector = Tree(BuildTree([UserMessage("user-1", null, "hello"), AssistantMessage("asst-1", "user-1", message)]), "asst-1");
            string? copied = null;
            selector.OnCopy = text => copied = text;
            selector.HandleInput("\u0018");
            Equal(message, copied, "copied");
        }));
        yield return ("ses.tree.label-timestamps", Sync(() =>
        {
            var tree = BuildTree([UserMessage("user-1", null, "hello"), AssistantMessage("asst-1", "user-1", "hi")]);
            var today = DateTime.Now;
            var labelDate = new DateTime(today.Year, 3, today.Month == 3 && today.Day == 28 ? 27 : 28, 14, 32, 0, DateTimeKind.Local);
            tree[0].Label = "checkpoint";
            tree[0].LabelTimestamp = PiSessions.IsoTimestamp(new DateTimeOffset(labelDate));
            var selector = Tree(tree, "asst-1");
            var list = selector.GetTreeList();
            var expected = $"3/{labelDate.Day} 14:32";
            var render = Plain(list.Render(200));
            Contains(render, "[checkpoint]", "label");
            Check(!render.Contains(expected, StringComparison.Ordinal), "no time yet");
            Check(!render.Contains("[+label time]", StringComparison.Ordinal), "no status yet");
            selector.HandleInput("T");
            render = Plain(list.Render(200));
            Contains(render, expected, "time shown");
            Contains(render, "[+label time]", "status label");
            Contains(render, $"• [checkpoint] {expected} user: hello", "order: label, time, content");
        }));
        yield return ("ses.tree.empty-labeled-filter-preserves-selection", Sync(() =>
        {
            var selector = Tree(BuildTree([UserMessage("user-1", null, "hello"), AssistantMessage("asst-1", "user-1", "hi"),
                UserMessage("user-2", "asst-1", "bye"), AssistantMessage("asst-2", "user-2", "goodbye")]), "asst-2");
            Equal("asst-2", SelectedId(selector), "initial");
            selector.HandleInput("\u000c");
            Equal(null, SelectedId(selector), "empty");
            Lines(["  No entries found", "  (0/0) [labeled]"], selector.GetTreeList().Render(80), "empty render");
            selector.HandleInput(CtrlD);
            Equal("asst-2", SelectedId(selector), "restored");
        }));
        yield return ("ses.tree.multiple-empty-filter-switches", Sync(() =>
        {
            var selector = Tree(BuildTree([UserMessage("user-1", null, "hello"), AssistantMessage("asst-1", "user-1", "hi")]), "asst-1");
            selector.HandleInput("\u000c");
            Equal(null, SelectedId(selector), "labeled");
            selector.HandleInput("\u000c");
            Equal("asst-1", SelectedId(selector), "default via toggle");
            selector.HandleInput("\u000c");
            Equal(null, SelectedId(selector), "labeled again");
            selector.HandleInput(CtrlD);
            Equal("asst-1", SelectedId(selector), "default");
        }));
        yield return ("ses.tree.ctrl-right-unfold-then-jump", Sync(() =>
        {
            var selector = Tree(BranchingTree(), "asst-4a");
            foreach (var (key, expected) in new[] { (CtrlLeft, "user-3a"), (CtrlLeft, "user-3a"), (Down, "user-3b"), (Up, "user-3a"), (CtrlRight, "user-3a"),
                         (Down, "asst-3a"), (CtrlLeft, "user-3a"), (CtrlRight, "asst-4a") })
            {
                selector.HandleInput(key);
                Equal(expected, SelectedId(selector), "after " + key.Replace("\u001b", "ESC", StringComparison.Ordinal));
            }
        }));
        yield return ("ses.tree.alt-arrows-alias", Sync(() =>
        {
            var selector = Tree(BranchingTree(), "asst-4a");
            foreach (var (key, expected) in new[] { (AltLeft, "user-3a"), (AltLeft, "user-3a"), (AltRight, "user-3a"), (AltRight, "asst-4a") })
            {
                selector.HandleInput(key);
                Equal(expected, SelectedId(selector), "after " + key.Replace("\u001b", "ESC", StringComparison.Ordinal));
            }
        }));
        yield return ("ses.tree.fold-root-preserves-nested-fold", Sync(() =>
        {
            var selector = Tree(BranchingTree(), "asst-4a");
            foreach (var (key, expected) in new[] { (CtrlLeft, "user-3a"), (CtrlLeft, "user-3a"), (CtrlLeft, "user-1"), (CtrlLeft, "user-1"), (Down, "user-1"),
                         (CtrlRight, "user-1"), (CtrlRight, "user-3a"), (Down, "user-3b") })
            {
                selector.HandleInput(key);
                Equal(expected, SelectedId(selector), "after " + key.Replace("\u001b", "ESC", StringComparison.Ordinal));
            }
        }));
        yield return ("ses.tree.fold-non-active-branch", Sync(() =>
        {
            var selector = Tree(BranchingTree(), "asst-4a");
            var found = false;
            for (var i = 0; i < 20 && !found; i++) { selector.HandleInput(Down); found = SelectedId(selector) == "user-3b"; }
            Check(found, "reached user-3b");
            foreach (var (key, expected) in new[] { (CtrlRight, "user-4b"), (CtrlLeft, "user-3b"), (CtrlLeft, "user-3b"), (CtrlLeft, "user-1") })
            {
                selector.HandleInput(key);
                Equal(expected, SelectedId(selector), "after " + key.Replace("\u001b", "ESC", StringComparison.Ordinal));
            }
        }));
        yield return ("ses.tree.fold-multiple-roots", Sync(() =>
        {
            var selector = Tree(BuildTree([UserMessage("user-1", null, "first root"), AssistantMessage("asst-1", "user-1", "response 1"),
                UserMessage("user-2", null, "second root"), AssistantMessage("asst-2", "user-2", "response 2")]), "asst-1");
            Equal("asst-1", SelectedId(selector), "initial");
            foreach (var (key, expected) in new[] { (CtrlLeft, "user-1"), (CtrlLeft, "user-1"), (Down, "user-2"), (CtrlRight, "asst-2"), (CtrlLeft, "user-2"),
                         (CtrlLeft, "user-2"), (CtrlLeft, "user-2") })
            {
                selector.HandleInput(key);
                Equal(expected, SelectedId(selector), "after " + key.Replace("\u001b", "ESC", StringComparison.Ordinal));
            }
        }));
        yield return ("ses.tree.fold-root-through-filtered-intermediate", Sync(() =>
        {
            var selector = Tree(BuildTree([UserMessage("user-1", null, "hello"), ToolCallOnlyAssistant("tool-asst-1", "user-1"),
                UserMessage("user-2", "tool-asst-1", "follow up"), AssistantMessage("asst-2", "user-2", "response")]), "asst-2");
            foreach (var (key, expected) in new[] { (CtrlLeft, "user-1"), (CtrlLeft, "user-1"), (Down, "user-1") })
            {
                selector.HandleInput(key);
                Equal(expected, SelectedId(selector), "after " + key.Replace("\u001b", "ESC", StringComparison.Ordinal));
            }
        }));
        yield return ("ses.tree.search-resets-folds", Sync(() =>
        {
            var selector = Tree(BranchingTree(), "asst-4a");
            selector.HandleInput(CtrlLeft); selector.HandleInput(CtrlLeft);
            selector.HandleInput(Down);
            Equal("user-3b", SelectedId(selector), "folded skip");
            selector.HandleInput("b");
            selector.HandleInput("\u001b");
            var current = "";
            for (var i = 0; i < 20; i++) { selector.HandleInput(Down); current = SelectedId(selector) ?? ""; if (current == "user-3a") break; }
            Equal("user-3a", current, "found user-3a");
            selector.HandleInput(Down);
            Equal("asst-3a", SelectedId(selector), "unfolded");
        }));
        yield return ("ses.tree.filter-change-resets-folds", Sync(() =>
        {
            var selector = Tree(BranchingTree(), "asst-4a");
            selector.HandleInput(CtrlLeft); selector.HandleInput(CtrlLeft);
            selector.HandleInput("\u0015"); selector.HandleInput(CtrlD);
            var current = "";
            for (var i = 0; i < 20; i++) { selector.HandleInput(Down); current = SelectedId(selector) ?? ""; if (current == "user-3a") break; }
            Equal("user-3a", current, "found user-3a");
            selector.HandleInput(Down);
            Equal("asst-3a", SelectedId(selector), "unfolded");
        }));

        // ---- tree render snapshots --------------------------------------------------------------------------------------------
        yield return ("ses.tree.render-full-default", Sync(() =>
        {
            var selector = Tree(BranchingTree(), "asst-4a");
            Lines(
            [
                "", new string('─', 80), "   Session Tree",
                "  ↑/↓ move · ←/→ page · ctrl+←/→ branch · ctrl+x copy · shift+l label",
                "  shift+t label time · filters ctrl+d/t/u/l/a · cycle ctrl+o/shift+ctrl+o",
                "  Type to search:", new string('─', 80), "",
                "  • user: first message",
                "  • assistant: response 1",
                "  • user: second message",
                "  • assistant: response 2",
                "  ├⊟ • user: branch A start",
                "  │     • assistant: branch A response",
                "  │     • user: branch A deep",
                "› │     • assistant: branch A leaf",
                "  └⊟ user: branch B start",
                "        assistant: branch B response",
                "        user: branch B deep",
                "  (8/11)",
                "", new string('─', 80),
            ], selector.Render(80), "full tree");
        }));
        yield return ("ses.tree.render-help-wide", Sync(() =>
        {
            var lines = Strip(Tree(BranchingTree(), "asst-4a").Render(120));
            Equal("  ↑/↓ move · ←/→ page · ctrl+←/→ branch · ctrl+x copy · shift+l label · shift+t label time · filters ctrl+d/t/u/l/a", lines[3].TrimEnd(), "help line 1");
            Equal("  cycle ctrl+o/shift+ctrl+o", lines[4].TrimEnd(), "help line 2");
        }));
        yield return ("ses.tree.render-user-only", Sync(() =>
        {
            var selector = Tree(BranchingTree(), "asst-4a");
            selector.HandleInput("\u0015");
            Lines(
            [
                "  • user: first message",
                "  • user: second message",
                "  ├⊟ • user: branch A start",
                "› │     • user: branch A deep",
                "  └⊟ user: branch B start",
                "        user: branch B deep",
                "  (4/6) [user]",
            ], selector.GetTreeList().Render(80), "user-only");
            selector.HandleInput("\u000f"); // ctrl+o: cycle forward to labeled-only
            Lines(["  No entries found", "  (0/0) [labeled]"], selector.GetTreeList().Render(80), "cycled to labeled");
            selector.HandleInput("\u000f");
            Contains(Plain(selector.GetTreeList().Render(80)), "(7/11) [all]", "cycled to all (selection stays on user-4a)");
            selector.HandleInput("\u000f");
            Equal("  (7/11)", Strip(selector.GetTreeList().Render(80))[^1].TrimEnd(), "cycled to default");
        }));
        yield return ("ses.tree.render-tools-and-no-tools", Sync(() =>
        {
            var tree = BuildTree([UserMessage("user-1", null, "read it"), ToolCallOnlyAssistant("tool-asst", "user-1"),
                ToolResult("result-1", "tool-asst", "tc-tool-asst", "read"), AssistantMessage("asst-2", "result-1", "done"), ModelChange("model-1", "asst-2")]);
            var selector = Tree(tree, "asst-2");
            Lines(["  • user: read it", "  • [read: test.ts]", "› • assistant: done", "  (3/3)"], selector.GetTreeList().Render(80), "default");
            selector.HandleInput("\u0014"); // ctrl+t
            Lines(["  • user: read it", "› • assistant: done", "  (2/2) [no-tools]"], selector.GetTreeList().Render(80), "no-tools");
            selector.HandleInput("\u0001"); // ctrl+a
            Lines(["  • user: read it", "  • [read: test.ts]", "› • assistant: done", "  [model: claude-sonnet-4]", "  (3/4) [all]"],
                selector.GetTreeList().Render(80), "all");
        }));
        yield return ("ses.tree.render-folding", Sync(() =>
        {
            var selector = Tree(BranchingTree(), "asst-4a");
            selector.HandleInput(CtrlLeft); selector.HandleInput(CtrlLeft);
            Lines(
            [
                "  • user: first message",
                "  • assistant: response 1",
                "  • user: second message",
                "  • assistant: response 2",
                "› ├⊞ • user: branch A start",
                "  └⊟ user: branch B start",
                "        assistant: branch B response",
                "        user: branch B deep",
                "  (5/8)",
            ], selector.GetTreeList().Render(80), "folded branch");
            selector.HandleInput(CtrlLeft); selector.HandleInput(CtrlLeft);
            Lines(["› ⊞ • user: first message", "  (1/1)"], selector.GetTreeList().Render(80), "folded root");
        }));
        yield return ("ses.tree.render-search", Sync(() =>
        {
            var selector = Tree(BranchingTree(), "asst-4a");
            foreach (var ch in "deep") selector.HandleInput(ch.ToString());
            var lines = Strip(selector.Render(80));
            Equal("  Type to search: deep", lines[5].TrimEnd(), "search line");
            Lines(["› • user: branch A deep", "  user: branch B deep", "  (1/2)"], selector.GetTreeList().Render(80), "matches");
            selector.HandleInput("\u007f"); // backspace
            Equal("  Type to search: dee", Strip(selector.Render(80))[5].TrimEnd(), "backspace");
            selector.HandleInput("\u001b");
            Equal("  Type to search:", Strip(selector.Render(80))[5].TrimEnd(), "escape clears");
        }));
        yield return ("ses.tree.label-editing", Sync(() =>
        {
            var changes = new List<(string Id, string? Label)>();
            var selector = Tree(BuildTree([UserMessage("user-1", null, "hello"), AssistantMessage("asst-1", "user-1", "hi")]), "asst-1", onLabelChange: (id, label) => changes.Add((id, label)));
            selector.HandleInput("L");
            var lines = Strip(selector.Render(80));
            Lines(["", "  Label (empty to remove):", "  >", "  enter save  escape/ctrl+c cancel", "", new string('─', 80)], lines.Skip(7), "label input");
            selector.HandleInput("c"); selector.HandleInput("p");
            selector.HandleInput("\r");
            Equal("asst-1:cp", string.Join(",", changes.Select(c => c.Id + ":" + c.Label)), "label change");
            Lines(["  • user: hello", "› • [cp] assistant: hi", "  (2/2)"], selector.GetTreeList().Render(80), "labeled");
            Check(selector.GetTreeList().GetSelectedNode()!.LabelTimestamp is not null, "label timestamp set");
            selector.HandleInput("L");
            Equal("  > cp", Strip(selector.Render(80))[9].TrimEnd(), "current label prefilled");
            selector.HandleInput("\u000b"); // ctrl+k clears the input (cursor at start)
            selector.HandleInput("\r");
            Equal("asst-1:", changes[^1].Id + ":" + changes[^1].Label, "label removed");
            Check(selector.GetTreeList().GetSelectedNode()!.LabelTimestamp is null, "timestamp cleared");
            selector.HandleInput("L");
            selector.HandleInput("x");
            selector.HandleInput("\u001b");
            Equal(2, changes.Count, "cancel does not change");
            Lines(["  • user: hello", "› • assistant: hi", "  (2/2)"], selector.GetTreeList().Render(80), "unlabeled");
        }));
        yield return ("ses.tree.select-and-cancel", Sync(() =>
        {
            string? selected = null; var cancelled = 0;
            var selector = new TreeSelectorComponent(BranchingTree(), "asst-4a", 24, id => selected = id, () => cancelled++);
            selector.HandleInput(Up);
            selector.HandleInput("\r");
            Equal("user-4a", selected, "selected");
            selector.HandleInput("\u001b");
            Equal(1, cancelled, "cancel");
            var timers = new List<double>();
            _ = new TreeSelectorComponent([], null, 24, _ => { }, () => cancelled++, setTimeout: (action, ms) => { timers.Add(ms); action(); return new Disposable(); });
            Equal(100.0, timers.Single(), "empty tree cancels after 100ms");
            Equal(2, cancelled, "empty tree cancelled");
        }));
        yield return ("ses.tree.entry-display-kinds", Sync(() =>
        {
            var root = UserMessage("u", null, "start");
            JsonObject E(string id, string parent, string type, params (string Key, JsonNode? Value)[] fields)
            {
                var entry = new JsonObject { ["type"] = type, ["id"] = id, ["parentId"] = parent, ["timestamp"] = Iso() };
                foreach (var (key, value) in fields) entry[key] = value;
                return entry;
            }
            var entries = new List<JsonObject>
            {
                root,
                E("c1", "u", "compaction", ("tokensBefore", 12500), ("summary", "sum")),
                E("b1", "c1", "branch_summary", ("summary", "went\tthere\nand back")),
                E("cm", "b1", "custom_message", ("customType", "note"), ("content", new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "a" }, new JsonObject { ["type"] = "text", ["text"] = "b" }))),
                E("bx", "cm", "message", ("message", new JsonObject { ["role"] = "bashExecution", ["command"] = "ls -la" })),
                E("er", "bx", "message", ("message", new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray(), ["stopReason"] = "error", ["errorMessage"] = "overloaded" })),
                E("ab", "er", "message", ("message", new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray(), ["stopReason"] = "aborted" })),
                E("si", "ab", "session_info", ("name", "My title")),
                E("se", "si", "session_info"),
                E("tl", "se", "thinking_level_change", ("thinkingLevel", "high")),
                E("cu", "tl", "custom", ("customType", "ext")),
                E("lb", "cu", "label", ("targetId", "u"), ("label", "x")),
                E("lc", "lb", "label", ("targetId", "u")),
                E("ce", "lc", "context_edit", ("targetId", "u"), ("replacement", new JsonObject())),
                E("us", "ce", "usage"),
                E("zz", "us", "message", ("message", new JsonObject { ["role"] = "custom" })),
            };
            var selector = new TreeSelectorComponent(BuildTree(entries), "u", 40, _ => { }, () => { }, initialFilterMode: FilterMode.All);
            Lines(
            [
                "› • user: start",
                "  [compaction: 13k tokens]",
                "  [branch summary]: went there and back",
                "  [note]: ab",
                "  [bash]: ls -la",
                "  assistant: overloaded",
                "  assistant: (aborted)",
                "  [title: My title]",
                "  [title: empty]",
                "  [thinking: high]",
                "  [custom: ext]",
                "  [label: x]",
                "  [label: (cleared)]",
                "  [context replace: u]",
                "  [custom]",
                "  (1/15) [all]",
            ], selector.GetTreeList().Render(80), "entry kinds");
            string? copied = null;
            selector.OnCopy = text => copied = text;
            selector.HandleInput(Down); selector.HandleInput("\u0018");
            Equal("sum", copied, "compaction copies summary");
            for (var i = 0; i < 4; i++) selector.HandleInput(Down);
            selector.HandleInput("\u0018");
            Equal("overloaded", copied, "error message copied");
            selector.HandleInput(Down); selector.HandleInput(Down); selector.HandleInput("\u0018");
            Equal(null, copied, "session info has no copy text");
        }));
        yield return ("ses.tree.format-tool-call", Sync(() =>
        {
            Equal("[read: a.ts:10-14]", TreeList.FormatToolCall("read", new JsonObject { ["path"] = "a.ts", ["offset"] = 10, ["limit"] = 5 }), "read range");
            Equal("[read: a.ts:1-5]", TreeList.FormatToolCall("read", new JsonObject { ["file_path"] = "a.ts", ["limit"] = 5 }), "read limit");
            Equal("[read: a.ts:7]", TreeList.FormatToolCall("read", new JsonObject { ["path"] = "a.ts", ["offset"] = 7 }), "read offset");
            Equal("[write: b.ts]", TreeList.FormatToolCall("write", new JsonObject { ["path"] = "b.ts" }), "write");
            Equal("[edit: c.ts]", TreeList.FormatToolCall("edit", new JsonObject { ["file_path"] = "c.ts" }), "edit");
            Equal($"[bash: {new string('x', 50)}...]", TreeList.FormatToolCall("bash", new JsonObject { ["command"] = new string('x', 60) }), "bash long");
            Equal("[bash: a b]", TreeList.FormatToolCall("bash", new JsonObject { ["command"] = "a\nb" }), "bash newline");
            Equal("[grep: /foo/ in .]", TreeList.FormatToolCall("grep", new JsonObject { ["pattern"] = "foo" }), "grep");
            Equal("[find: *.ts in src]", TreeList.FormatToolCall("find", new JsonObject { ["pattern"] = "*.ts", ["path"] = "src" }), "find");
            Equal("[ls: .]", TreeList.FormatToolCall("ls", new JsonObject()), "ls");
            Equal("[mcp: {\"q\":\"hello\"}]", TreeList.FormatToolCall("mcp", new JsonObject { ["q"] = "hello" }), "custom short");
            Equal("[mcp: {\"query\":\"" + new string('a', 30) + "...]", TreeList.FormatToolCall("mcp", new JsonObject { ["query"] = new string('a', 40) }), "custom long");
        }));
        yield return ("ses.tree.multiple-roots-render", Sync(() =>
        {
            var selector = Tree(BuildTree([UserMessage("user-1", null, "first root"), AssistantMessage("asst-1", "user-1", "response 1"),
                UserMessage("user-2", null, "second root"), AssistantMessage("asst-2", "user-2", "response 2")]), "asst-2");
            Lines(["  • user: second root", "›    • assistant: response 2", "  user: first root", "     assistant: response 1", "  (2/4)"],
                selector.GetTreeList().Render(80), "active root first");
        }));
        yield return ("ses.tree.horizontal-scroll", Sync(() =>
        {
            // A deep chain of branch points pushes the selected entry's anchor right; the body pans while the gutter stays.
            var entries = new List<JsonObject> { UserMessage("r", null, "root") };
            var parent = "r";
            for (var i = 0; i < 12; i++)
            {
                entries.Add(UserMessage($"s{i}", parent, $"side {i}"));
                entries.Add(UserMessage($"m{i}", parent, $"main {i}"));
                parent = $"m{i}";
            }
            var selector = new TreeSelectorComponent(BuildTree(entries), parent, 80, _ => { }, () => { });
            var lines = Strip(selector.GetTreeList().Render(30));
            var selected = lines.Single(line => line.StartsWith("› ", StringComparison.Ordinal));
            Contains(selected, "• user: main 11", "selected text visible");
            Check(lines.All(line => PiSharp.Tui.Pi.TextUtils.VisibleWidth(line) <= 30), "fits width");
        }));
    }

    private sealed class Disposable : IDisposable { public void Dispose() { } }
}
