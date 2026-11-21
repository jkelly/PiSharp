// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/coding-agent/test/session-selector-selection.test.ts, session-selector-path-delete.test.ts and
// session-selector-rename.test.ts (the cases Pi v1.6.0 added: #12245, #12106) and interactive-mode.ts showSessionSelector.
// Ported expectations, authored against the native port; not an upstream execution capture. Cases marked "native" have no upstream
// counterpart.
using System.Text;
using System.Text.Json;
using PiSharp.Cli.Interactive.Mode;
using PiSharp.Cli.Interactive.Mode.Components;
using PiSharp.Cli.Pi;
using PiSharp.Tui.Pi;
using static Expect;

/// <summary>Sync 1.6.0, /resume: a query highlights the top match, a cursor key keeps the highlight, the selection survives a reload
/// that delivers the sessions in parts, and a cancelled load stops its listing.</summary>
internal static class Sync160UiSessionSelectorCases
{
    private const string Down = "\u001b[B";
    private const string Up = "\u001b[A";
    private const string Left = "\u001b[D";
    private const string Enter = "\r";
    private const string CtrlD = "\u0004";
    private const string CtrlR = "\u0012";

    private sealed class Disposable : IDisposable { public void Dispose() { } }

    private static void TypeText(IInputHandler target, string text) { foreach (var ch in text) target.HandleInput(ch.ToString()); }

    /// <summary>session-selector-selection.test.ts makeSession.</summary>
    private static PiSessionInfo Session(string id, string text, long modifiedMs, string? path = null) =>
        new(path ?? $"/tmp/{id}.jsonl", id, "", null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.FromUnixTimeMilliseconds(modifiedMs), 1, text, text);

    private static SessionList SessionListOf(IReadOnlyList<PiSessionInfo> sessions, List<string> selected) =>
        new SessionSelectorComponent((_, _) => Task.FromResult(sessions), (_, _) => Task.FromResult<IReadOnlyList<PiSessionInfo>>([]),
            selected.Add, () => { }, () => { }, () => { }).GetSessionList();

    public static IEnumerable<(string Id, Func<Task> Run)> All()
    {
        // ---------------------------------------------------------------- session-selector-selection.test.ts
        // Regression test for #12245: the /login fix applied to /resume.
        yield return ("sync160.ui.session.selects-the-best-match-after-typing-a-query-even-after-scrolling", Sync(() =>
        {
            var selected = new List<string>();
            var list = SessionListOf([Session("login", "fix login", 6000), Session("logout", "fix logout", 5000), Session("refactor", "refactor", 4000),
                Session("docs", "docs", 3000), Session("tests", "tests", 2000)], selected);
            for (var i = 0; i < 4; i++) list.HandleInput(Down);
            Equal("/tmp/tests.jsonl", list.GetSelectedSessionPath(), "scrolled to the last session");
            TypeText(list, "fix");
            list.HandleInput(Enter);
            Equal("/tmp/login.jsonl", string.Join(",", selected), "Enter picks the best match");
        }));
        yield return ("sync160.ui.session.keeps-the-selection-when-a-cursor-key-leaves-the-query-unchanged", Sync(() =>
        {
            var list = SessionListOf([Session("login", "fix login", 6000), Session("logout", "fix logout", 5000)], []);
            TypeText(list, "fix");
            list.HandleInput(Down);
            list.HandleInput(Left);
            Equal("/tmp/logout.jsonl", list.GetSelectedSessionPath(), "selection after Left");
        }));
        yield return ("sync160.ui.session.keeps-the-top-row-selected-when-sessions-arrive-after-typing-a-query", Sync(() =>
        {
            var sessions = new List<PiSessionInfo> { Session("login", "fix login", 6000), Session("docs", "docs", 3000) };
            var list = SessionListOf(sessions, []);
            list.HandleInput(Down);
            TypeText(list, "fix");
            list.SetSessions([Session("newer", "fix newer", 7000), .. sessions], false);
            Equal("/tmp/newer.jsonl", list.GetSelectedSessionPath(), "the top row stays selected");
        }));

        // ---------------------------------------------------------------- session-selector-path-delete.test.ts (#12106)
        yield return ("sync160.ui.session.keeps-the-next-session-selected-when-a-progressive-reload-after-delete-starts-with-another-session", Sync(() =>
        {
            var sessions = Enumerable.Range(1, 5).Select(i => Session($"s{i}", "hello", 10_000 - i * 1000)).ToList();
            // Upstream deletes real files; the delete operation is the option here, so "exists" is this set.
            var deleted = new HashSet<string>(StringComparer.Ordinal);
            var selector = new SessionSelectorComponent((onProgress, _) =>
                {
                    IReadOnlyList<PiSessionInfo> remaining = [.. sessions.Where(session => !deleted.Contains(session.Path))];
                    // The first partial list holds only the file that finished loading first, here the oldest
                    onProgress?.Invoke(1, remaining.Count, [remaining[^1]]);
                    onProgress?.Invoke(remaining.Count, remaining.Count, remaining);
                    return Task.FromResult(remaining);
                }, (_, _) => Task.FromResult<IReadOnlyList<PiSessionInfo>>([]), _ => { }, () => { }, () => { }, () => { },
                new SessionSelectorOptions
                {
                    DeleteSessionFile = path => { deleted.Add(path); return Task.FromResult(new DeleteSessionFileResult(true, "unlink")); },
                    SetTimeout = (_, _) => new Disposable(),
                });
            var list = selector.GetSessionList();
            list.HandleInput(Down);
            list.HandleInput(Up);
            Equal(sessions[0].Path, list.GetSelectedSessionPath(), "first session selected");
            list.HandleInput(CtrlD);
            list.HandleInput(Enter);
            Check(deleted.Contains(sessions[0].Path), "the session is deleted");
            Equal(sessions[1].Path, list.GetSelectedSessionPath(), "the next session is selected after the reload");
        }));

        // ---------------------------------------------------------------- session-selector-rename.test.ts (#12106)
        yield return ("sync160.ui.session.keeps-the-renamed-session-selected-when-the-reload-starts-with-another-session", Sync(() =>
        {
            IReadOnlyList<PiSessionInfo> sessions = [.. Enumerable.Range(1, 5).Select(i => Session($"s{i}", "hello", 10_000 - i * 1000))];
            var loads = 0;
            var selector = new SessionSelectorComponent((onProgress, _) =>
                {
                    // On reload, the first partial list holds only the file that finished loading first
                    if (loads++ > 0) onProgress?.Invoke(1, sessions.Count, [sessions[4]]);
                    return Task.FromResult(sessions);
                }, (_, _) => Task.FromResult<IReadOnlyList<PiSessionInfo>>([]), _ => { }, () => { }, () => { }, () => { },
                new SessionSelectorOptions { RenameSession = (_, _) => Task.CompletedTask, ShowRenameHint = true });
            var list = selector.GetSessionList();
            list.HandleInput(Down);
            list.HandleInput(Down);
            Equal(sessions[2].Path, list.GetSelectedSessionPath(), "third session selected");
            list.HandleInput(CtrlR);
            selector.HandleInput("X");
            selector.HandleInput(Enter);
            Equal(2, loads, "reloaded after the rename");
            Equal(sessions[2].Path, selector.GetSessionList().GetSelectedSessionPath(), "the renamed session stays selected");
        }));

        // Native: a selected session that a complete (not partial) list no longer holds is not kept as a target.
        yield return ("sync160.ui.session.native-a-complete-list-drops-the-pending-target", Sync(() =>
        {
            var sessions = Enumerable.Range(1, 3).Select(i => Session($"s{i}", "hello", 10_000 - i * 1000)).ToList();
            var list = SessionListOf(sessions, []);
            list.HandleInput(Down);
            Equal(sessions[1].Path, list.GetSelectedSessionPath(), "second session selected");
            list.SetSessions([sessions[2]], false, partial: true);   // still loading: s2 stays the target
            list.SetSessions([sessions[0], sessions[2]], false);     // complete without s2: the target is dropped
            var adopted = list.GetSelectedSessionPath();
            list.SetSessions(sessions, false);                       // s2 is back, but no longer a target
            Equal(adopted, list.GetSelectedSessionPath(), "the row adopted from the complete list stays selected");
            Check(adopted != sessions[1].Path, "the dropped target is not selected again: " + adopted);
        }));

        // ---------------------------------------------------------------- interactive-mode.ts showSessionSelector (#12106, #11956)
        // The selector cancels a listing it replaces (reload after delete or rename). The token has to reach PiSessions, or the
        // listing reads every session file to the end and its result is thrown away.
        yield return ("sync160.ui.session.native-the-resume-listing-stops-when-its-load-is-cancelled", async () =>
        {
            using var dir = new TempDir();
            var sessionDir = Path.Combine(dir.Path, "sessions");
            Directory.CreateDirectory(sessionDir);
            using (var writer = new StreamWriter(Path.Combine(sessionDir, "big.jsonl"), false, new UTF8Encoding(false)) { NewLine = "\n" })
            {
                writer.WriteLine(JsonSerializer.Serialize(new { type = "session", version = 3, id = "big", timestamp = "2026-01-01T00:00:00.000Z", cwd = dir.Path }));
                for (var index = 0; index < 400_000; index++)
                    writer.WriteLine("{\"type\":\"message\",\"id\":\"m" + index + "\",\"timestamp\":\"2026-01-01T00:00:01.000Z\",\"message\":{\"role\":\"user\",\"content\":\"hello\",\"timestamp\":1767225601000}}");
            }
            var (current, all) = InteractiveMode.SessionLoaders(dir.Path, sessionDir, Path.Combine(dir.Path, "agent"));
            foreach (var (name, loader) in new[] { ("current", current), ("all", all) })
            {
                using var cancellation = new CancellationTokenSource();
                var listing = loader(null, cancellation.Token);
                cancellation.CancelAfter(15);
                try
                {
                    var sessions = await listing.WaitAsync(TimeSpan.FromSeconds(60));
                    throw new InvalidOperationException($"the {name} listing ran to the end after its load was cancelled ({sessions.Count} sessions)");
                }
                catch (OperationCanceledException) { }
            }
            // Not cancelled, the same loaders list the session.
            Equal("big", (await current(null, CancellationToken.None)).Single().Id, "current scope");
            Equal(400_000, (await all(null, CancellationToken.None)).Single().MessageCount, "all scope");
        });
    }
}
