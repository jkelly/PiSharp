// Decision 0011 (run W5, T5, docs): decision 0011's W5 lines of "Not done" held to the source, with no browser and no app, as W4DocsCases
// holds W4's (and with its helpers: AppFilesMatching, BrowserCaseTexts, AppHookTexts). Each open W5 gap names a phrase of its line and the
// source facts that show it: while they hold, "Not done" must say it, and once a change closes the gap the phrase must go (or the line, the
// phrase and the facts are revised together). Each part of the line "W5's browser cases leave out" names the page hooks a browser case would
// use to reach it, and stays listed exactly while no browser case uses one of them; every hook must still be in the app. The record must
// also call W5 built: its milestone row is not "planned", it has the W5 sections, and nothing in "Not done" still waits for W5.
internal static partial class Program
{
    private const string W5CoverageLineStart = "- W5's browser cases leave out:";

    private static readonly W4Gap[] W5Gaps =
    [
        // Settings (W2): sections shown as "Coming in a later milestone" notes, which no planned milestone builds.
        new("Settings' later-milestone notes", "Settings still shows",
            root => AppFilesMatching(root, "*.cshtml", "Coming in a later milestone").Where(path => path.StartsWith("Views/Settings/", StringComparison.Ordinal)).ToList()
                is { Count: > 0 } ? null : "no Settings view says \"Coming in a later milestone\" any more"),
        // Pins (E7): agent compare runs and agent battles are refused as "agent test cases come later".
        new("agent test cases", "agent test cases",
            root => AppFilesMatching(root, "*.cs", @"\bAgentLater\b") is { Count: > 0 } ? null : "PinRules no longer refuses agent runs (AgentLater is gone)"),
        // Pins (E7): the Tool Bench's views are not E7's, so the bench hosts no Pin as test.
        new("the Tool Bench has no Pin as test", "a Pin as test button on the Tool Bench",
            root => AppFilesMatching(root, "*.cshtml", "modal-test-case").Any(path => path.StartsWith("Views/Bench/", StringComparison.Ordinal))
                ? "a Tool Bench view now opens modal-test-case" : null),
    ];

    private static readonly W4CoverageGap[] W5CoverageGaps =
    [
        new("pinning from the other sources", "Pin as test from chat, the Playground, a Compare column, History and the inspector", ["data-pin-chat", "data-pin-run"]),
        new("the Arena's ranking", "the Rank pane", ["data-arena-show-pane", "data-arena-rank"]),
        new("Stream anyway", "Stream anyway", ["data-arena-stream-anyway"]),
        new("Stop all", "Stop all", ["data-arena-stop"]),
        new("New round", "New round", ["data-arena-again"]),
        new("a new vote tag", "a new tag", ["data-arena-tag-input", "data-arena-new-tag"]),
        new("an agent battle's approvals and budget", "an agent battle's approvals and budget Continue", ["data-arena-approve", "data-arena-continue"]),
        new("an agent battle's Keep", "Keep after an agent battle's vote", ["data-arena-keep"]),
        new("the schedule's cap field and Turn off", "the schedule's cap field and Turn off", ["data-cap-input", "data-ss-off"]),
        new("the check editor's Try, Save and Remove", "the check editor's Try, Save and Remove", ["data-ce-try-button", "data-ce-save", "data-ce-remove"]),
        new("tool-argument rules", "tool-argument rules", ["data-ce-add-arg"]),
        new("a suite's targets", "adding or removing a target", ["data-add-target", "data-remove-target"]),
        new("a suite's Rename", "a suite's Rename", ["data-rename-suite", "data-rename-form"]),
        new("Cancel run", "Cancel run", ["data-cancel-run"]),
        new("removing a case", "removing a case", ["data-delete-case"]),
        new("the votes CSV", "the votes CSV", ["data-votes-csv"]),
    ];

    private static IEnumerable<(string, Func<Task>)> W5DocsCases() =>
    [
        Case("browser.w5.docs.not-done-lists-every-open-w5-gap-and-no-closed-one", () =>
        {
            var root = RepositoryRoot();
            var notDone = DecisionSection(root, "Not done");
            var problems = new List<string>();
            foreach (var gap in W5Gaps)
            {
                var listed = notDone.Contains(gap.Phrase, StringComparison.Ordinal);
                var closed = gap.Closed(root);
                if (closed is null && !listed) problems.Add($"Not done misses an open W5 gap: {gap.Name} (its line says \"{gap.Phrase}\")");
                if (closed is not null && listed)
                    problems.Add($"Not done still lists \"{gap.Name}\", but {closed}: drop its line (\"{gap.Phrase}\") and this gap, or revise both");
            }
            Check(problems.Count == 0, string.Join("; ", problems));
            return Task.CompletedTask;
        }),

        Case("browser.w5.docs.not-done-names-every-part-the-w5-browser-cases-leave-out", () =>
        {
            var root = RepositoryRoot();
            var lines = DecisionSection(root, "Not done").Split('\n').Select(line => line.TrimEnd('\r'))
                .Where(line => line.StartsWith(W5CoverageLineStart, StringComparison.Ordinal)).ToList();
            Check(lines.Count == 1, $"Not done has one line \"{W5CoverageLineStart} …\" (found {lines.Count})");
            var line = lines[0];
            var app = AppHookTexts(root);
            var cases = BrowserCaseTexts(root);
            Check(cases.Keys.Any(name => name.StartsWith("W5", StringComparison.Ordinal)) && cases.Values.Any(text => text.Contains("data-arena-vote", StringComparison.Ordinal)),
                "the W5 browser cases are read");
            var problems = new List<string>();
            foreach (var gap in W5CoverageGaps)
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
                    problems.Add($"no browser case reaches {gap.Name}, but the line \"{W5CoverageLineStart}\" does not say \"{gap.Phrase}\"");
                if (used.Count > 0 && listed)
                    problems.Add($"the line \"{W5CoverageLineStart}\" still says \"{gap.Phrase}\", but a browser case now uses {string.Join(", ", used)}: drop it (and this gap), or revise both");
            }
            Check(problems.Count == 0, string.Join("; ", problems));
            return Task.CompletedTask;
        }),

        Case("browser.w5.docs.the-record-calls-w5-built", () =>
        {
            var root = RepositoryRoot();
            var milestones = DecisionSection(root, "Milestones").Split('\n').Select(line => line.TrimEnd('\r'))
                .Where(line => line.StartsWith("| W5 |", StringComparison.Ordinal)).ToList();
            Check(milestones.Count == 1 && !milestones[0].TrimEnd().EndsWith("| planned |", StringComparison.Ordinal),
                "the W5 milestone row says what happened to it: " + string.Join(" / ", milestones));
            Check(DecisionSection(root, "Model Arena, test suites and Drift Watch (W5, E0–E7 and L4)").Contains("(E7)", StringComparison.Ordinal), "the W5 section describes every unit");
            Check(DecisionSection(root, "Recovery and browser tests (W5, T5)").Contains("EvalRecoveryCases", StringComparison.Ordinal), "the T5 section names its cases");
            var notDone = DecisionSection(root, "Not done");
            foreach (var waiting in new[] { "both W5", "in W5", "later-milestone page" })
                Check(!notDone.Contains(waiting, StringComparison.Ordinal), $"Not done still waits for W5 (\"{waiting}\")");
            return Task.CompletedTask;
        }),
    ];
}
