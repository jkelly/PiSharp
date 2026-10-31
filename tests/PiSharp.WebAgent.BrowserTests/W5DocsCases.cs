// Decision 0011 (run W5, T5, docs): decision 0011's W5 lines of "Not done" held to the source, with no browser and no app, as W4DocsCases
// holds W4's (and with its helpers: AppFilesMatching, BrowserCaseTexts, AppHookTexts). Each open W5 gap names a phrase of its line and the
// source facts that show it: while they hold, "Not done" must say it, and once a change closes the gap the phrase must go (or the line, the
// phrase and the facts are revised together). Each part of the line "W5's browser cases leave out" names the page hooks a browser case would
// use to reach it, and stays listed exactly while no browser case uses one of them; every hook must still be in the app. The record must
// also call W5 built: its milestone row is not "planned", it has the W5 sections, and nothing in "Not done" still waits for W5.
// W5 fix 2 (docs): two gaps the integration left out of "Not done" were held here: a held suite call is reserved again as its own call when
// its hold no longer fits (RunEngine.StartReservedAsync), which can be refused mid-run and which no browser case reaches (W5 fix 2 (suites)
// reaches it in process), and no case pinned down that a run's drift signals are stored before the run reads complete (W5 fix 2
// (tests-ci) closed that one). The W5 section's sentence on suite calls sent on their holds must name the reserve-again path while the
// engine has it.
internal static partial class Program
{
    private const string W5CoverageLineStart = "- W5's browser cases leave out:";

    private static readonly W4Gap[] W5Gaps =
    [
        // Settings (W2): sections no planned milestone builds, marked "Not in this version" (W5 integration fix 1: they said "Coming in a
        // later milestone", which no Settings view may say again).
        new("Settings' not-in-this-version notes", "Settings marks the mockup sections no planned milestone builds",
            root => AppFilesMatching(root, "*.cshtml", "Not in this version").Where(path => path.StartsWith("Views/Settings/", StringComparison.Ordinal)).ToList()
                is { Count: > 0 } ? null : "no Settings view says \"Not in this version\" any more"),
        // Pins (E7): agent compare runs and agent battles are refused as "agent test cases come later".
        new("agent test cases", "agent test cases",
            root => AppFilesMatching(root, "*.cs", @"\bAgentLater\b") is { Count: > 0 } ? null : "PinRules no longer refuses agent runs (AgentLater is gone)"),
        // Run engine (W5 integration fix 1, fix 2 docs): a suite call whose hold no longer fits (another active account, which a judge
        // without its own account or a target that bound no account when the run started can meet, or the run's Cap output one the call
        // cannot hold, which the runner does not check before it reserves) is released and reserved again as its own call, which can be
        // refused mid-run (the result is stored never sent). W5 fix 2 (suites) reaches both in process (EvalSuiteCases); no browser case
        // does, since faux models have no accounts. A browser case that reaches it would see the hold's release note, so one that names
        // that note closes the "no browser case" part (W5 retest 2: the line said "no in-app case" until the in-process cases met it).
        new("a held suite call reserved again", "is reserved again as its own call", HeldCallReservedAgainChanged),
    ];

    /// <summary>The in-process cases that reach a suite call reserved again (W5 fix 2, suites), as the "Not done" line names them.</summary>
    private static readonly string[] HeldCallReservedAgainCases =
    [
        "evals.suites.a-call-reserved-again-mid-run-is-refused-unsent-and-raises-no-drift",
        "evals.suites.a-held-output-cap-a-target-cannot-hold-is-reserved-again-and-refused-unsent",
    ];

    /// <summary>Null while RunEngine.StartReservedAsync still reserves a held call again when its hold no longer fits, faux models have no
    /// account, SuiteRunner pins only an account it bound and checks no Cap output answer against its targets, the in-process cases that
    /// reach it are there and no browser case names the hold's release note; else what changed.</summary>
    private static string? HeldCallReservedAgainChanged(string root)
    {
        const string engine = "src/PiSharp.WebAgent/Runs/RunEngine.cs";
        const string runner = "src/PiSharp.WebAgent/Evals/Suites/SuiteRunner.cs";
        const string note = "reserved again as the run's own call";
        if (!SourceHas(root, engine, note, "held.Account != account", "CapRefusal(plan, lease.Reservation.MaxTokens) is not null"))
            return "RunEngine.StartReservedAsync no longer reserves a call again when its hold's account or output cap does not fit (\"" + note + "\")";
        if (!SourceHas(root, engine, "if (plan.Model.Api == RunModelOptions.FauxApi) return null;"))
            return "a faux model's run now resolves an account, so \"faux models have no accounts\" no longer holds";
        if (!SourceHas(root, runner, "return accounts is null ? null : (await accounts.BindAsync(provider, cancellationToken)"))
            return "SuiteRunner no longer leaves a target that bound no account unpinned, so \"the runner pins only an account it bound\" no longer holds";
        if (SourceHas(root, runner, "CapRefusal(") || SourceHas(root, runner, "CheckCap("))
            return "SuiteRunner now checks a Cap output answer against its targets before it reserves";
        if (HeldCallReservedAgainCases.FirstOrDefault(id => !SourceHas(root, "tests/PiSharp.WebAgent.Tests/EvalSuiteCases.cs", id)) is { } gone)
            return "the in-process case " + gone + " is gone";
        var reaching = BrowserCaseTexts(root).Where(item => item.Value.Contains(note, StringComparison.Ordinal)).Select(item => item.Key).ToList();
        return reaching.Count > 0 ? "a browser case now reaches it (" + string.Join(", ", reaching) + " names \"" + note + "\")" : null;
    }

    // W5 fix 2 (tests-ci): W5PinCases, W5SuiteCases and W5ArenaPlayCases reach every part this list named (Stream anyway, Stop all, New round,
    // a new vote tag, an agent battle's approvals, budget Continue and Keep, the schedule's cap field and Turn off, the check editor's Try, Save
    // and Remove, tool-argument rules, a suite's targets and Rename, Cancel run, removing a case and the votes CSV), so their gaps and phrases
    // are gone. What the line still lists has no page hook a case could use yet (a phone-width run page, samplingParams on real wire formats),
    // or shares its hook with parts a case reaches: the Tool Bench's Pin as test (W5 fix 2 (pins)) uses data-pin-run, as the other run pins do.
    private static readonly W4CoverageGap[] W5CoverageGaps = [];

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
            // W5 retest 2: while the reserve-again gap is open, its line names the in-process cases that reach it and no longer says no
            // in-app case does.
            if (HeldCallReservedAgainChanged(root) is null)
            {
                foreach (var id in HeldCallReservedAgainCases.Select(id => id["evals.suites.".Length..]))
                    if (!notDone.Contains(id, StringComparison.Ordinal)) problems.Add($"Not done's reserve-again line does not name the in-process case {id}");
                if (notDone.Contains("No in-app case reaches this", StringComparison.Ordinal))
                    problems.Add("Not done still says no in-app case reaches a suite call reserved again, but EvalSuiteCases does in process");
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

        // W5 fix 2 (docs): "so no other reservation can take the headroom between hold and call" held only while the hold fits the call;
        // while the engine reserves a call again when it does not, the sentence that says so must name that path too.
        Case("browser.w5.docs.suite-calls-on-their-holds-name-the-reserve-again-path", () =>
        {
            var root = RepositoryRoot();
            var section = DecisionSection(root, "Model Arena, test suites and Drift Watch (W5, E0–E7 and L4)");
            const string claim = "no other reservation can take the headroom between hold and call";
            var at = section.IndexOf(claim, StringComparison.Ordinal);
            Check(at >= 0, $"the W5 section still says \"{claim}\" (revise this case with the sentence)");
            var dot = section.LastIndexOf(". ", at, StringComparison.Ordinal);
            var start = dot < 0 ? 0 : dot + 2;
            var end = section.IndexOf(". ", at, StringComparison.Ordinal);
            var sentence = section[start..(end < 0 ? section.Length : end)];
            Check(sentence.Contains("StartReservedAsync", StringComparison.Ordinal), "the sentence is the one on suite calls sent on their holds: " + sentence);
            if (SourceHas(root, "src/PiSharp.WebAgent/Runs/RunEngine.cs", "reserved again as the run's own call"))
                Check(sentence.Contains("no longer fits", StringComparison.Ordinal) && sentence.Contains("reserved again", StringComparison.Ordinal) &&
                    sentence.Contains("refused", StringComparison.Ordinal),
                    "the W5 section's sentence on suite calls sent on their holds names no reserve-again path (a hold that no longer fits is reserved " +
                    "again and can be refused mid-run): " + sentence);
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
            // W5 integration fix 1: no Settings view promises a later milestone, and Not done no longer says one does.
            Check(AppFilesMatching(root, "*.cshtml", "Coming in a later milestone").All(path => !path.StartsWith("Views/Settings/", StringComparison.Ordinal)),
                "a Settings view still says \"Coming in a later milestone\"");
            Check(!notDone.Contains("Coming in a later milestone", StringComparison.Ordinal), "Not done still quotes \"Coming in a later milestone\"");
            return Task.CompletedTask;
        }),
    ];
}
