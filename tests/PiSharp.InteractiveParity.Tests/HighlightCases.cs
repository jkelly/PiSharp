using PiSharp.Cli.Interactive.Mode;
using static Expect;

/// <summary>utils/syntax-highlight.ts over the highlight.js 10.7.3 port. The engine is compared with goldens the original library
/// produced (tools/HljsGrammars/generate-goldens.mjs: 528 authored snippets over all 191 languages, their truncated/CRLF/fuzzed
/// variants, the eager phase before loadAllHighlightLanguages, supportsLanguage answers and renderHighlightedHtml cases).</summary>
internal static class HighlightCases
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "Highlight");

    public static IEnumerable<(string Id, Func<Task> Run)> All() =>
    [
        ("highlight.hljs-port-matches-the-original-library", Sync(() => Report(HljsDifferentialTests.Run(Data), 2600))),
        ("highlight.hljs-port-is-thread-safe", Sync(() => Report(HljsDifferentialTests.RunParallel(Data), 1000))),
        ("highlight.every-grammar-compiles-in-dotnet", Sync(() => Report(HljsDifferentialTests.CheckGrammars(), 191))),
        ("highlight.cli-theme-scopes-and-language-validation", Sync(CliTheme)),
    ];

    private static void Report(IReadOnlyList<(string Id, bool Passed, string Detail)> results, int minimum)
    {
        var failed = results.Where(result => !result.Passed).Take(5).Select(result => result.Id + ": " + result.Detail).ToList();
        Check(results.Count >= minimum && failed.Count == 0, $"{results.Count} checks, failures: " + string.Join(" | ", failed));
    }

    // theme.ts highlightCode/buildCliHighlightTheme: keywords, strings and titles take their theme colors; an unknown language is
    // not highlighted (mdCodeBlock). Pi 1.6.0 (#11092): text outside every scope, and a scope the theme does not map (hljs-section),
    // take the theme's text color through the `default` formatter; before 1.6.0 they were left uncolored.
    private static void CliTheme()
    {
        var theme = Themes.Current;
        var lines = Themes.HighlightCode("const x = \"s\";", "typescript");
        Equal(theme.Fg("syntaxKeyword", "const") + theme.Fg("text", " x = ") + theme.Fg("syntaxString", "\"s\"") + theme.Fg("text", ";"), lines.Single(), "typescript line");
        Equal(theme.Fg("mdCodeBlock", "plain text"), Themes.HighlightCode("plain text", "not-a-language").Single(), "unknown language");
        Check(SyntaxHighlight.SupportsLanguage("ts") && SyntaxHighlight.SupportsLanguage("python"), "eager languages and aliases");
        SyntaxHighlight.LoadAllLanguages();
        Check(SyntaxHighlight.SupportsLanguage("yaml") && SyntaxHighlight.SupportsLanguage("dockerfile"), "all languages after loading");
        Equal(theme.Fg("text", "# Title"), SyntaxHighlight.Highlight("# Title", "markdown", theme), "unmapped section scope");
    }
}
