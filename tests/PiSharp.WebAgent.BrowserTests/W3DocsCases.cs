using System.Reflection;
using System.Text.RegularExpressions;

// Decision 0011 (run W3, fix 2, docs): decision 0011 held to the source, with no browser and no app. Each W3 gap below names a phrase of its
// line in "Not done" and the source facts that show it is still open: while they hold, the line must be there, and once a change closes the
// gap, the line must go (or the line, the phrase and the facts are revised together), so "Not done" can neither miss an open W3 gap nor keep
// a closed one. The W3 dialogs are the modals of the views of run W3; the phone case (W3LayoutCases) opens some of them at 375 px
// (`#modal-….show`). While it leaves any out, the T3 section must not claim every W3 dialog, and Not done's 375 px line names each one it
// leaves out by its id, and none that it opens.
internal static partial class Program
{
    /// <summary>A W3 gap that decision 0011's "Not done" lists until <paramref name="Closed"/> says why it no longer holds.</summary>
    /// <param name="Name">What is open.</param>
    /// <param name="Phrase">A part of its line in "Not done".</param>
    /// <param name="Closed">Given the repository's root: null while the source still shows the gap, else what changed.</param>
    private sealed record W3Gap(string Name, string Phrase, Func<string, string?> Closed);

    /// <summary>What the W3 browser cases did with spend caps, confirm panes and footer marks when decision 0011's line on their coverage
    /// was written (<see cref="W3BrowserSpendFacts(string)"/>): caps per request on everything (one more saved per month, and the provider
    /// pane looked at, neither of them enforced), the request pane, and the metered and uncertain marks.</summary>
    private static readonly string[] W3BrowserSpendFactsWhenWritten =
        ["mark:metered", "mark:uncertain", "pane:sc-request", "period:month", "period:request", "scope:everything", "scope:provider"];

    private static readonly W3Gap[] W3Gaps =
    [
        // The chat's queued-send case: after a Spend Guard stop ends the run, its queued follow-up can still be withdrawn (nothing placed it).
        new("a follow-up queued during a run waits after a Spend Guard stop", "stays queued after a Spend Guard stop",
            root => SourceHas(root, "tests/PiSharp.WebAgent.Tests/ChatSpendCases.cs", "chat.spend.queued-send-keeps-its-approval-from-the-running-turn",
                "the follow-up stays queued", "\"the queued message is withdrawn\"")
                ? null : "ChatSpendCases' queued-send case no longer withdraws a follow-up that stayed queued after the stop"),
        // History › Spend's filters offer the quick filters, and nothing saves the user's own.
        new("History › Spend has quick filters only", "not the mockup's saved filters",
            root => SourceHas(root, "src/PiSharp.WebAgent/Views/Runs/_SpendFilters.cshtml", "Quick filters</button>") &&
                !Regex.IsMatch(Source(root, "src/PiSharp.WebAgent/Views/Runs/_SpendFilters.cshtml"), "Saved filters|Save (current|these) filters", RegexOptions.IgnoreCase)
                ? null : "_SpendFilters.cshtml offers saved filters"),
        // SpendRecovery marks every row left reserved as uncertain, and nothing in the Spend area takes or waits for a lock on the data folder
        // (a FileLock, a FileShare.None stream, or a data-folder lock of the app's).
        new("SpendRecovery assumes one app process per data folder", "one app process per data folder",
            root => SourceHas(root, "src/PiSharp.WebAgent/Spend/Ledger/SpendRecovery.cs", "IHostedService") &&
                !Directory.EnumerateFiles(Path.Join(root, "src", "PiSharp.WebAgent", "Spend"), "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText)
                    .Any(text => text.Contains("FileLock", StringComparison.Ordinal) || text.Contains("FileShare.None", StringComparison.Ordinal) ||
                        text.Contains("DataLock", StringComparison.Ordinal))
                ? null : "the Spend area now takes or waits for a lock on the data folder"),
        // The W3 browser cases do no more with caps, confirm panes and marks than when the line was written.
        new("W3's browser cases enforce only per-request caps on everything", "per-request caps on everything",
            root => W3BrowserSpendFacts(root).Except(W3BrowserSpendFactsWhenWritten, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList() is { Count: > 0 } added
                ? "the W3 browser cases now also use " + string.Join(", ", added) + " (revise W3BrowserSpendFactsWhenWritten with the line)"
                : null),
    ];

    private static IEnumerable<(string, Func<Task>)> W3DocsCases() =>
    [
        Case("browser.w3.docs.not-done-lists-every-open-w3-gap-and-no-closed-one", () =>
        {
            var root = RepositoryRoot();
            Check(W3BrowserSpendFacts(root).Contains("pane:sc-request"), "the W3 browser cases are read: " + string.Join(", ", W3BrowserSpendFacts(root)));
            var notDone = DecisionSection(root, "Not done");
            var problems = new List<string>();
            foreach (var gap in W3Gaps)
            {
                var listed = notDone.Contains(gap.Phrase, StringComparison.Ordinal);
                var closed = gap.Closed(root);
                if (closed is null && !listed) problems.Add($"Not done misses an open W3 gap: {gap.Name} (its line says \"{gap.Phrase}\")");
                if (closed is not null && listed)
                    problems.Add($"Not done still lists \"{gap.Name}\", but {closed}: drop its line (\"{gap.Phrase}\") and this gap, or revise both");
            }
            Check(problems.Count == 0, string.Join("; ", problems));
            return Task.CompletedTask;
        }),

        Case("browser.w3.docs.not-done-names-every-w3-dialog-the-phone-case-leaves-out", () =>
        {
            var root = RepositoryRoot();
            var w3Dialogs = Directory.EnumerateFiles(Path.Join(root, "src", "PiSharp.WebAgent", "Views"), "*.cshtml", SearchOption.AllDirectories)
                .Select(File.ReadAllText).Where(text => text.StartsWith("@* Decision 0011 (run W3", StringComparison.Ordinal))
                .SelectMany(text => Regex.Matches(text, "<div class=\"modal fade\" id=\"(modal-[a-z-]+)\"").Select(match => match.Groups[1].Value))
                .ToHashSet(StringComparer.Ordinal);
            Check(w3Dialogs.Contains("modal-spend-confirm") && w3Dialogs.Contains("modal-resolve-reservation"), "the W3 dialogs are found: " + string.Join(", ", w3Dialogs));
            var phone = Regex.Matches(Source(root, "tests/PiSharp.WebAgent.BrowserTests/W3LayoutCases.cs"), @"#(modal-[a-z-]+)\.show")
                .Select(match => match.Groups[1].Value).Where(w3Dialogs.Contains).ToHashSet(StringComparer.Ordinal);
            Check(phone.Count > 0, "the phone case opens W3 dialogs");

            var t3 = DecisionSection(root, "Recovery and browser tests (W3, T3)");
            var phoneLine = DecisionSection(root, "Not done").Split('\n').Select(line => line.TrimEnd('\r'))
                .SingleOrDefault(line => line.StartsWith("- ", StringComparison.Ordinal) && line.Contains("375 px", StringComparison.Ordinal));
            var left = w3Dialogs.Except(phone).Order(StringComparer.Ordinal).ToList();
            if (left.Count > 0)
            {
                Check(!Regex.IsMatch(t3, @"every W3 (page and )?dialog"), "the T3 section claims every W3 dialog at 375 px, but the phone case leaves out " + string.Join(", ", left));
                Check(phoneLine is not null, "Not done has no 375 px line for the W3 dialogs the phone case leaves out: " + string.Join(", ", left));
                foreach (var id in left) Check(phoneLine!.Contains($"`{id}`", StringComparison.Ordinal), $"Not done's 375 px line does not name `{id}`, which the phone case leaves out");
            }
            foreach (var id in phone)
                Check(phoneLine is null || !phoneLine.Contains($"`{id}`", StringComparison.Ordinal), $"Not done's 375 px line names `{id}`, which the phone case opens");
            return Task.CompletedTask;
        }),
    ];

    /// <summary>What the W3 browser cases (this file aside) do with spend caps, confirm panes and footer marks, as <c>kind:value</c> facts: a
    /// cap's period and scope, posted (C# <c>["period"] = "day"</c>, a page script's <c>period: 'day'</c>) or chosen in the cap modal
    /// (<c>capWinDay</c>, <c>#capScope</c>'s option); every <c>sc-*</c> confirm pane named; every <c>data-spend-mark</c> kind looked for.</summary>
    private static HashSet<string> W3BrowserSpendFacts(string root)
    {
        var texts = Directory.EnumerateFiles(Path.Join(root, "tests", "PiSharp.WebAgent.BrowserTests"), "W3*Cases.cs")
            .Where(path => !Path.GetFileName(path).Equals("W3DocsCases.cs", StringComparison.Ordinal)).Select(File.ReadAllText).ToList();
        var facts = new HashSet<string>(StringComparer.Ordinal);
        void Add(string kind, string pattern)
        {
            foreach (var text in texts)
                foreach (Match match in Regex.Matches(text, pattern)) facts.Add(kind + ":" + match.Groups[1].Value.ToLowerInvariant());
        }
        foreach (var key in new[] { "period", "scope" })
        {
            Add(key, $"""\["{key}"\]\s*=\s*"([a-z-]+)""");
            Add(key, $"""\b{key}:\s*'([a-z-]+)'""");
        }
        Add("period", "capWin(Request|Run|Day|Month)");
        Add("scope", """capScope"\)\.SelectOptionAsync\("([a-z-]+)""");
        Add("pane", @"\b(sc-[a-z]+)");
        Add("mark", """data-spend-mark=\\?['"]([a-z]+)""");
        return facts;
    }

    /// <summary>The repository's root: two folders above the Web Agent project this suite was built against.</summary>
    private static string RepositoryRoot()
    {
        var web = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(attribute => attribute.Key == "WebProject").Value!;
        var root = Path.GetFullPath(Path.Join(web, "..", ".."));
        Check(File.Exists(Path.Join(root, "docs", "decisions", "0011-web-agent.md")), "decision 0011 is at " + root);
        return root;
    }

    /// <summary>The text of a file under <paramref name="root"/> (a missing file fails the case).</summary>
    private static string Source(string root, string relative) => File.ReadAllText(Path.Join(root, relative));

    /// <summary>Whether a file under <paramref name="root"/> holds every one of <paramref name="parts"/>.</summary>
    private static bool SourceHas(string root, string relative, params string[] parts)
    {
        var text = Source(root, relative);
        return parts.All(part => text.Contains(part, StringComparison.Ordinal));
    }

    /// <summary>Decision 0011's section <c>## <paramref name="heading"/></c>, up to the next <c>## </c> heading.</summary>
    private static string DecisionSection(string root, string heading)
    {
        var text = Source(root, "docs/decisions/0011-web-agent.md").Replace("\r\n", "\n", StringComparison.Ordinal);
        var start = text.IndexOf("\n## " + heading + "\n", StringComparison.Ordinal);
        Check(start >= 0, "decision 0011 has a section \"" + heading + "\"");
        var body = text[(start + heading.Length + 5)..];
        var end = body.IndexOf("\n## ", StringComparison.Ordinal);
        return end < 0 ? body : body[..end];
    }
}
