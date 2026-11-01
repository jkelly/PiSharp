using System.Text.RegularExpressions;

// Decision 0011 (run W4, fix 2, docs): decision 0011's W4 lines of "Not done" held to the source, with no browser and no app, as
// W3DocsCases holds W3's (its helpers: RepositoryRoot, Source, SourceHas, DecisionSection). Each W4 gap names a phrase of its line and the
// source facts that show it is still open: while they hold, the line must say it, and once a change closes the gap, the phrase must go (or
// the line, the phrase and the facts are revised together). The W4 browser cases' gaps are held the same way: each part of the line
// "W4's browser cases leave out" names the page hooks (data attributes and ids of the app's views and scripts) a browser case would use to
// cover it, and a part stays listed exactly while no browser case uses any of its hooks. Every hook must still be in the app, so a renamed
// one cannot keep a part listed for ever. A fix in another area that closes one of these gaps fails here until its line is revised.
// W4 retest 2: revised after fix 2's merges, which closed most of the gaps the docs fix listed (agent compare's Stop, time limit, delete
// and memory; the group preflight; the bench's strict note; the settings rows and the footer padding; most of the browser coverage).
// W5 L4 (W4 leftovers): the Playground's controls for the options the run engine carries, presets that keep the newer options and a tool's
// strict flag, and the Tool Bench's Reset, removing a message and Export as JSON (W4BenchResetCases) are closed, so their gaps and lines
// are gone. After W5 the owner's question on R0's body patch was decided (the patch stays), so it is no longer a gap: a case holds the
// decision to the source instead, while RunModelOptions.SendPatch exists.
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
        // Agent compare (R6): the delete API (fix 2, agent-runner) has no button on the run page.
        new("agent compare's run page has no Delete button", "its run page has no Delete button",
            root => AppFilesMatching(root, "compare-agent*.js", @"/delete\b") is { Count: > 0 } files
                ? "an agent compare page script now deletes a run (" + string.Join(", ", files) + ")" : null),
        // Shared web library: Bootstrap's footer padding is calc(var(--bs-modal-padding) - …), invalid with the theme's two values; the
        // Web Agent pads its own footers (site.css), Durable.Web does not.
        new("Durable.Web's dialogs lose their footer padding", "still lose their footer padding",
            root => HubModalFooterUnpadded(root)),
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
        // Covered by PiSharp.WebAgent.Tests only: a faux run stores no raw payloads (W4InspectorCases checks the "not stored" panes, and
        // that the Raw SSE tools are absent without them).
        new("stored raw payloads in the inspector", "stored raw payloads in the inspector", ["data-raw-request", "data-raw-response", "data-sse-group", "data-sse-wrap"]),
        new("agent compare's budget limit", "agent compare's budget limit", ["acCap", "data-ac-continue", "data-ac-budget"]),
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

        Case("browser.w4.docs.the-body-patch-decision-holds-while-the-patch-does", () =>
        {
            var root = RepositoryRoot();
            var w4 = DecisionSection(root, "Runs, Playground, Compare, History, Presets and agent compare (W4, R0–R9 and L3)");
            Check(w4.Contains("**How a run's options reach the provider (decided after W5).**", StringComparison.Ordinal),
                "the W4 section records how a run's options reach the provider");
            Check(SourceHas(root, "src/PiSharp.WebAgent/Runs/RunModelOptions.cs", "SendPatch("),
                "the run's options no longer travel as a body patch (RunModelOptions.SendPatch is gone): revise the decision it records");
            Check(!DecisionSection(root, "Not done").Contains("An owner decision is open", StringComparison.Ordinal),
                "Not done no longer calls the body patch an open owner decision");
            return Task.CompletedTask;
        }),
    ];

    /// <summary>Null while the theme's <c>--bs-modal-padding</c> has two values (so Bootstrap's footer padding, a <c>calc</c> of it, is
    /// invalid) and neither PiSharp.Web.Common nor Durable.Web gives <c>.modal-footer</c> side padding; else what changed. (The Web Agent
    /// pads its own footers in site.css.)</summary>
    private static string? HubModalFooterUnpadded(string root)
    {
        var theme = Source(root, "src/PiSharp.Web.Common/wwwroot/css/pisharp-theme.css");
        if (!Regex.IsMatch(theme, @"--bs-modal-padding:\s*[^;\s]+\s+[^;\s]+\s*;"))
            return "the theme's --bs-modal-padding no longer has two values";
        var sheets = new[] { "PiSharp.Web.Common", "PiSharp.Durable.Web" }
            .SelectMany(project => Directory.EnumerateFiles(Path.Join(root, "src", project, "wwwroot", "css"), "*.css", SearchOption.TopDirectoryOnly))
            .Select(File.ReadAllText);
        var padded = CssRules(sheets)
            .Where(rule => rule.Selectors.Any(selector => selector.EndsWith(".modal-footer", StringComparison.Ordinal))
                && Regex.IsMatch(rule.Body, @"(^|[;\s])padding(-(inline(-start|-end)?|left|right))?\s*:"))
            .Select(rule => string.Join(", ", rule.Selectors)).ToList();
        return padded.Count > 0 ? "a shared or hub stylesheet now pads .modal-footer (" + string.Join("; ", padded) + ")" : null;
    }

    /// <summary>The innermost rules of <paramref name="sheets"/> (inside an at-rule too): each selector of the list, trimmed, and the body.</summary>
    private static IEnumerable<(string[] Selectors, string Body)> CssRules(IEnumerable<string> sheets) =>
        sheets.Select(sheet => Regex.Replace(sheet, @"/\*.*?\*/", " ", RegexOptions.Singleline))
            .SelectMany(sheet => Regex.Matches(sheet, @"([^{}]+)\{([^{}]*)\}").Select(match =>
                (match.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), match.Groups[2].Value)));

    /// <summary>The Web Agent's files (<paramref name="pattern"/>, under <c>src/PiSharp.WebAgent</c>, build output aside) whose text matches
    /// <paramref name="regex"/>, relative to the project.</summary>
    private static List<string> AppFilesMatching(string root, string pattern, string regex, RegexOptions options = RegexOptions.None)
    {
        var project = Path.Join(root, "src", "PiSharp.WebAgent");
        return [.. Directory.EnumerateFiles(project, pattern, SearchOption.AllDirectories)
            .Where(path => !InBuildOutput(project, path) && Regex.IsMatch(File.ReadAllText(path), regex, options))
            .Select(path => Path.GetRelativePath(project, path).Replace('\\', '/')).Order(StringComparer.Ordinal)];
    }

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
