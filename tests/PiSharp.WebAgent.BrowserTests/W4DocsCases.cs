using System.Text.RegularExpressions;

// Decision 0011 (run W4, fix 2, docs): decision 0011's W4 lines of "Not done" held to the source, with no browser and no app, as
// W3DocsCases holds W3's (its helpers: RepositoryRoot, Source, SourceHas, DecisionSection). Each W4 gap names a phrase of its line and the
// source facts that show it is still open: while they hold, the line must say it, and once a change closes the gap, the phrase must go (or
// the line, the phrase and the facts are revised together). The W4 browser cases' gaps are held the same way: each part of the line
// "W4's browser cases leave out" names the page hooks (data attributes and ids of the app's views and scripts) a browser case would use to
// cover it, and a part stays listed exactly while no browser case uses any of its hooks. Every hook must still be in the app, so a renamed
// one cannot keep a part listed for ever. A fix in another area that closes one of these gaps fails here until its line is revised.
internal static partial class Program
{
    /// <summary>A W4 gap that decision 0011's "Not done" lists until <paramref name="Closed"/> says why it no longer holds.</summary>
    /// <param name="Name">What is open.</param>
    /// <param name="Phrase">A part of its line in "Not done".</param>
    /// <param name="Closed">Given the repository's root: null while the source still shows the gap, else what changed.</param>
    private sealed record W4Gap(string Name, string Phrase, Func<string, string?> Closed);

    /// <summary>A part of the app that the W4 browser cases leave out while none of them uses one of <paramref name="Hooks"/>.</summary>
    /// <param name="Name">What is left out.</param>
    /// <param name="Phrase">Its part of the line "W4's browser cases leave out".</param>
    /// <param name="Hooks">What a browser case would use to reach it: data attributes and element ids of the app's views and scripts (a
    /// leading <c>#</c> is the id as a selector; the id itself must be in the app).</param>
    private sealed record W4CoverageGap(string Name, string Phrase, string[] Hooks);

    private const string W4CoverageLineStart = "- W4's browser cases leave out:";

    private static readonly W4Gap[] W4Gaps =
    [
        // Agent compare (R5): Stop passes over a model whose check runs, and the check runs on the run's token, not one Stop can cancel.
        new("Stop does not end a running check command", "Stop does not end a running check command",
            root => SourceHas(root, "src/PiSharp.WebAgent/AgentCompare/Runner/AgentCompareRunner.Actions.cs",
                    "or AgentModelPhase.Checking || model.Outcome is not null) continue;")
                && SourceHas(root, "src/PiSharp.WebAgent/AgentCompare/Runner/AgentCompareRunner.cs",
                    "AgentCheckCommand.RunAsync(command, cwd, TimeSpan.FromSeconds(run.Spec.CheckTimeoutSeconds), token)")
                ? null : "AgentCompareRunner's StopAsync no longer passes over a model being checked, or CheckAsync no longer runs the check on the run's token"),
        new("the per-model time limit has no case", "The per-model time limit has no case",
            root => TestFilesMatching(root, @"\btime[- ]?limit", RegexOptions.IgnoreCase) is { Count: > 0 } files
                ? "a test now names the time limit (" + string.Join(", ", files) + ")" : null),
        new("agent compare's records are never removed", "are never removed",
            root => AppFilesMatching(root, "*.cs", @"DELETE\s+FROM\s+agent_compare_(runs|turns)", RegexOptions.IgnoreCase) is { Count: > 0 } files
                ? "agent compare's records are now deleted (" + string.Join(", ", files) + ")" : null),
        new("AgentCompareStore keeps every run in memory", "reads every run into memory and never lets one go",
            root => Regex.IsMatch(Source(root, "src/PiSharp.WebAgent/AgentCompare/Runner/AgentCompareStore.cs"), @"\bruns\.(TryRemove|Remove|Clear)\(")
                ? "AgentCompareStore now lets runs go from memory" : null),
        // Compare (R2): the estimate by Run all sums each column's own preflight; the Spend Guard has no group preflight.
        new("Compare's Run all estimate has no group preflight", "there is no group preflight",
            root => !SourceHas(root, "src/PiSharp.WebAgent/Compare/CompareEstimate.cs",
                    "each column on its own: the group's sum against a run, daily or monthly cap is checked when Run all reserves the group")
                ? "CompareEstimate no longer says it checks each column on its own and leaves the group's sum to Run all's reservation"
                : AppFilesMatching(root, "*.cs", @"PreflightGroup|GroupPreflight", RegexOptions.IgnoreCase) is { Count: > 0 } files
                    ? "a group preflight is in " + string.Join(", ", files) : null),
        // Presets (R8): presets-host.js writes Compare's draft into its page itself.
        new("compare-request.js has no draft-apply API", "`compare-request.js` has no draft-apply API",
            root =>
            {
                var exports = ScriptExports(Source(root, "src/PiSharp.WebAgent/wwwroot/js/compare-request.js"));
                return exports.SetEquals(["initCompareRequest"]) ? null : "compare-request.js now exports " + string.Join(", ", exports.Order(StringComparer.Ordinal));
            }),
        // Tool Bench (R7): the note under the schema preview, where the mockup has a Strict arguments switch.
        new("the Tool Bench's strict note", "strict arguments (`constrainedSampling`) are not sent yet",
            root => SourceHas(root, "src/PiSharp.WebAgent/Views/Bench/_BenchToolList.cshtml", "are not sent yet")
                ? null : "_BenchToolList's schema preview no longer says strict arguments are not sent yet"),
        // Run engine (R0): the owner has not chosen between the body patch and extending PiSharp.AI's request types.
        new("the owner decision on R0's body patch", "An owner decision is open",
            root => SourceHas(root, "src/PiSharp.WebAgent/Runs/RunModelOptions.cs", "SendPatch(")
                ? null : "the run's extra options no longer travel as a body patch (RunModelOptions.SendPatch is gone): say what the owner chose instead"),
        // Shared web library: .setting-row's control column stays up to 320 px wide until the row stacks below 992 px.
        new("the .setting-row squeeze", "the shared `.setting-row`",
            root => SettingRowSqueezes(root)),
        // Shared web library: Bootstrap's footer padding is calc(var(--bs-modal-padding) - …), invalid with the theme's two values.
        new("the modal footer's missing side padding", "the shared modal footer has no side padding",
            root => ModalFooterUnpadded(root)),
        // Settings › Data & storage keeps its own typed-DELETE dialog because the shared confirm dialog has no such variant.
        new("_ConfirmModal has no typed-DELETE variant", "`_ConfirmModal` has no typed-DELETE variant",
            root =>
            {
                var confirm = Source(root, "src/PiSharp.WebAgent/Views/Shared/_ConfirmModal.cshtml");
                if (Regex.IsMatch(confirm, "typed", RegexOptions.IgnoreCase) || confirm.Contains("DELETE", StringComparison.Ordinal))
                    return "_ConfirmModal now has a typed DELETE";
                return File.Exists(Path.Join(root, "src", "PiSharp.WebAgent", "Views", "Settings", "_DataConfirmModal.cshtml"))
                    ? null : "Settings › Data & storage no longer has its own _DataConfirmModal";
            }),
    ];

    private static readonly W4CoverageGap[] W4CoverageGaps =
    [
        // Covered by PiSharp.WebAgent.Tests only.
        new("stored raw payloads in the inspector", "stored raw payloads in the inspector", ["data-raw-request", "data-raw-response"]),
        new("the inspector's HAR and JSON downloads", "the HAR and JSON downloads", ["data-run-download"]),
        new("pins", "pins", ["data-run-pin"]),
        new("re-runs", "re-runs", ["data-run-rerun"]),
        new("Clear history's deletion", "Clear history's deletion", ["data-clear-confirm"]),
        new("preset import and export", "preset import and export", ["data-preset-import", "data-preset-export"]),
        new("Keep branch", "Keep branch", ["acKeepBranch"]),
        new("a refused Apply", "a refused Apply", ["data-ac-refused-files", "data-ac-refused-message"]),
        new("the danger zone", "the danger zone", ["data-danger"]),
        new("run-history retention", "run-history retention", ["data-history-form", "#retention"]),
        new("agent compare's command grants", "command grants for every column", ["data-ac-approve-command"]),
        new("agent compare's Allow-list and Auto modes", "its Allow-list and Auto modes", ["acApprovalList", "acApprovalAuto"]),
        new("agent compare's turn and budget limits", "its turn and budget limits", ["acMaxTurns", "acCap", "data-ac-continue", "data-ac-budget"]),
        new("agent compare's Stop", "and its Stop", ["data-ac-stop"]),
        // Run in no case at all.
        new("the Tool Bench's scripts beyond a manual result", "the Tool Bench's scripts beyond a manual result",
            ["data-bench-rule", "data-bench-mocks", "data-bench-stop", "data-bench-reset", "data-bench-remove", "data-bench-export"]),
        new("History's saved filters dialogs", "History's saved filters dialogs", ["data-rsf-name", "data-rmf-name", "data-rmf-remove", "data-rmf-undo"]),
        new("the inspector's Raw SSE tools", "the inspector's Raw SSE filter", ["data-sse-filter", "data-sse-group", "data-sse-wrap"]),
        new("Settings › Tools' folder browser", "Settings › Tools' folder browser", ["folderBrowser", "data-folder-browser"]),
        new("Data & storage's dirty state", "Data & storage's dirty state", ["data-history-form", "data-history-reset", "data-retention-note", "#retention"]),
    ];

    private static IEnumerable<(string, Func<Task>)> W4DocsCases() =>
    [
        Case("browser.w4.docs.not-done-lists-every-open-w4-gap-and-no-closed-one", () =>
        {
            var root = RepositoryRoot();
            var notDone = DecisionSection(root, "Not done");
            var problems = new List<string>();
            foreach (var gap in W4Gaps)
            {
                var listed = notDone.Contains(gap.Phrase, StringComparison.Ordinal);
                var closed = gap.Closed(root);
                if (closed is null && !listed) problems.Add($"Not done misses an open W4 gap: {gap.Name} (its line says \"{gap.Phrase}\")");
                if (closed is not null && listed)
                    problems.Add($"Not done still lists \"{gap.Name}\", but {closed}: drop its line (\"{gap.Phrase}\") and this gap, or revise both");
            }
            Check(problems.Count == 0, string.Join("; ", problems));
            return Task.CompletedTask;
        }),

        Case("browser.w4.docs.not-done-names-every-part-the-w4-browser-cases-leave-out", () =>
        {
            var root = RepositoryRoot();
            var lines = DecisionSection(root, "Not done").Split('\n').Select(line => line.TrimEnd('\r'))
                .Where(line => line.StartsWith(W4CoverageLineStart, StringComparison.Ordinal)).ToList();
            Check(lines.Count == 1, $"Not done has one line \"{W4CoverageLineStart} …\" (found {lines.Count})");
            var line = lines[0];
            var app = AppHookTexts(root);
            var cases = BrowserCaseTexts(root);
            Check(cases.Count > 0 && cases.Values.Any(text => text.Contains("data-run-output", StringComparison.Ordinal)), "the browser cases are read");
            var problems = new List<string>();
            foreach (var gap in W4CoverageGaps)
            {
                foreach (var hook in gap.Hooks)
                {
                    var name = hook.TrimStart('#');
                    if (!app.Any(text => text.Contains(name, StringComparison.Ordinal)))
                        problems.Add($"\"{hook}\" ({gap.Name}) is no longer in the app's views or scripts: revise the gap's hooks");
                }
                var used = gap.Hooks.SelectMany(hook => cases.Where(item => item.Value.Contains(hook, StringComparison.Ordinal))
                    .Select(item => $"{hook} in {item.Key}")).ToList();
                var listed = line.Contains(gap.Phrase, StringComparison.Ordinal);
                if (used.Count == 0 && !listed)
                    problems.Add($"no browser case reaches {gap.Name}, but the line \"{W4CoverageLineStart}\" does not say \"{gap.Phrase}\"");
                if (used.Count > 0 && listed)
                    problems.Add($"the line \"{W4CoverageLineStart}\" still says \"{gap.Phrase}\", but a browser case now uses {string.Join(", ", used)}: drop it (and this gap), or revise both");
            }
            Check(problems.Count == 0, string.Join("; ", problems));
            return Task.CompletedTask;
        }),
    ];

    /// <summary>Null while the shared <c>.setting-row</c> keeps its control column up to 320 px wide and stacks only below 992 px, with no
    /// Web Agent stylesheet laying it out otherwise; else what changed.</summary>
    private static string? SettingRowSqueezes(string root)
    {
        var shell = Source(root, "src/PiSharp.Web.Common/wwwroot/css/shell.css");
        if (!shell.Contains("grid-template-columns: minmax(0, 1fr) minmax(0, 320px)", StringComparison.Ordinal))
            return "shell.css's .setting-row no longer keeps its control column at up to 320 px";
        if (!Regex.IsMatch(shell, @"max-width:\s*991\.98px\)\s*\{\s*\.setting-row\s*\{\s*grid-template-columns:\s*minmax\(0,\s*1fr\);"))
            return "shell.css's .setting-row no longer stacks at the 991.98 px breakpoint";
        var laidOut = CssRules(AppStylesheets(root, includeShared: false))
            .Where(rule => rule.Selectors.Any(selector => selector.Contains(".setting-row", StringComparison.Ordinal))
                && rule.Body.Contains("grid", StringComparison.Ordinal))
            .Select(rule => string.Join(", ", rule.Selectors)).ToList();
        return laidOut.Count > 0 ? "a Web Agent stylesheet now lays out .setting-row (" + string.Join("; ", laidOut) + ")" : null;
    }

    /// <summary>Null while the theme's <c>--bs-modal-padding</c> has two values (so Bootstrap's footer padding, a <c>calc</c> of it, is
    /// invalid) and no stylesheet gives <c>.modal-footer</c> side padding; else what changed.</summary>
    private static string? ModalFooterUnpadded(string root)
    {
        var theme = Source(root, "src/PiSharp.Web.Common/wwwroot/css/pisharp-theme.css");
        if (!Regex.IsMatch(theme, @"--bs-modal-padding:\s*[^;\s]+\s+[^;\s]+\s*;"))
            return "the theme's --bs-modal-padding no longer has two values";
        var padded = CssRules(AppStylesheets(root, includeShared: true))
            .Where(rule => rule.Selectors.Any(selector => selector.EndsWith(".modal-footer", StringComparison.Ordinal))
                && Regex.IsMatch(rule.Body, @"(^|[;\s])padding(-(inline(-start|-end)?|left|right))?\s*:"))
            .Select(rule => string.Join(", ", rule.Selectors)).ToList();
        return padded.Count > 0 ? "a stylesheet now pads .modal-footer (" + string.Join("; ", padded) + ")" : null;
    }

    /// <summary>The Web Agent's stylesheets (and, with <paramref name="includeShared"/>, PiSharp.Web.Common's), not the vendored ones.</summary>
    private static IEnumerable<string> AppStylesheets(string root, bool includeShared)
    {
        var folders = new List<string> { Path.Join(root, "src", "PiSharp.WebAgent", "wwwroot", "css") };
        if (includeShared) folders.Add(Path.Join(root, "src", "PiSharp.Web.Common", "wwwroot", "css"));
        return folders.SelectMany(folder => Directory.EnumerateFiles(folder, "*.css", SearchOption.TopDirectoryOnly)).Select(File.ReadAllText);
    }

    /// <summary>The innermost rules of <paramref name="sheets"/> (inside an at-rule too): each selector of the list, trimmed, and the body.</summary>
    private static IEnumerable<(string[] Selectors, string Body)> CssRules(IEnumerable<string> sheets) =>
        sheets.Select(sheet => Regex.Replace(sheet, @"/\*.*?\*/", " ", RegexOptions.Singleline))
            .SelectMany(sheet => Regex.Matches(sheet, @"([^{}]+)\{([^{}]*)\}").Select(match =>
                (match.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), match.Groups[2].Value)));

    /// <summary>The names a page script exports (<c>export function</c>, <c>const</c>, <c>class</c>, … and <c>export { … }</c>).</summary>
    private static HashSet<string> ScriptExports(string script)
    {
        var names = Regex.Matches(script, @"^\s*export\s+(?:default\s+)?(?:async\s+)?(?:function\*?|const|let|var|class)\s+([A-Za-z_$][\w$]*)", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        foreach (Match list in Regex.Matches(script, @"^\s*export\s*\{([^}]*)\}", RegexOptions.Multiline))
            foreach (var item in list.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                names.Add(item.Split(" as ", StringSplitOptions.TrimEntries)[^1]);
        return names;
    }

    /// <summary>The Web Agent's files (<paramref name="pattern"/>, under <c>src/PiSharp.WebAgent</c>, build output aside) whose text matches
    /// <paramref name="regex"/>, relative to the project.</summary>
    private static List<string> AppFilesMatching(string root, string pattern, string regex, RegexOptions options = RegexOptions.None)
    {
        var project = Path.Join(root, "src", "PiSharp.WebAgent");
        return [.. Directory.EnumerateFiles(project, pattern, SearchOption.AllDirectories)
            .Where(path => !InBuildOutput(project, path) && Regex.IsMatch(File.ReadAllText(path), regex, options))
            .Select(path => Path.GetRelativePath(project, path).Replace('\\', '/')).Order(StringComparer.Ordinal)];
    }

    /// <summary>The Web Agent's two test suites' sources (decision 0011's docs cases aside) whose text matches <paramref name="regex"/>.</summary>
    private static List<string> TestFilesMatching(string root, string regex, RegexOptions options = RegexOptions.None) =>
    [
        .. new[] { "PiSharp.WebAgent.Tests", "PiSharp.WebAgent.BrowserTests" }
            .SelectMany(suite => Directory.EnumerateFiles(Path.Join(root, "tests", suite), "*.cs", SearchOption.TopDirectoryOnly))
            .Where(path => !Path.GetFileName(path).EndsWith("DocsCases.cs", StringComparison.Ordinal) && Regex.IsMatch(File.ReadAllText(path), regex, options))
            .Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal),
    ];

    /// <summary>The browser cases' sources (decision 0011's docs cases aside), by file name.</summary>
    private static Dictionary<string, string> BrowserCaseTexts(string root) =>
        Directory.EnumerateFiles(Path.Join(root, "tests", "PiSharp.WebAgent.BrowserTests"), "*.cs", SearchOption.TopDirectoryOnly)
            .Where(path => !Path.GetFileName(path).EndsWith("DocsCases.cs", StringComparison.Ordinal))
            .ToDictionary(path => Path.GetFileName(path), File.ReadAllText, StringComparer.Ordinal);

    /// <summary>The texts of the app's views and page scripts, where its hooks are.</summary>
    private static List<string> AppHookTexts(string root)
    {
        var project = Path.Join(root, "src", "PiSharp.WebAgent");
        return [.. Directory.EnumerateFiles(Path.Join(project, "Views"), "*.cshtml", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Join(project, "wwwroot", "js"), "*.js", SearchOption.AllDirectories)).Select(File.ReadAllText)];
    }

    private static bool InBuildOutput(string project, string path)
    {
        var first = Path.GetRelativePath(project, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return first is "bin" or "obj";
    }
}
