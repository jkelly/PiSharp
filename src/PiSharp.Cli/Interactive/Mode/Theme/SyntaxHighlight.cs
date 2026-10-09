// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/utils/syntax-highlight.ts (highlight, supportsLanguage,
// loadAllHighlightLanguages) and theme.ts buildCliHighlightTheme (scope to theme token mapping), over the highlight.js 10.7.3 port
// (Theme/Highlight: the engine and every lib/languages grammar, verified against the original's output).
namespace PiSharp.Cli.Interactive.Mode;

internal static class SyntaxHighlight
{
    /// <summary>supportsLanguage: hljs.getLanguage(name) !== undefined (the eager languages until <see cref="LoadAllLanguages"/>).</summary>
    public static bool SupportsLanguage(string name) => Hljs.SupportsLanguage(name);

    /// <summary>loadAllHighlightLanguages: registers every highlight.js language (interactive mode, after startup).</summary>
    public static void LoadAllLanguages() => Hljs.LoadAllLanguages();

    /// <summary>highlight(code, { language, ignoreIllegals: true, theme: getCliHighlightTheme(theme) }).</summary>
    public static string Highlight(string code, string language, Theme theme) =>
        HighlightRenderer.Render(Hljs.HighlightHtml(code, language, ignoreIllegals: true), CliHighlightTheme(theme));

    private static Theme? cachedFor;
    private static IReadOnlyDictionary<string, Func<string, string>>? cached;
    private static readonly object Gate = new();

    /// <summary>getCliHighlightTheme: buildCliHighlightTheme, cached per theme.</summary>
    private static IReadOnlyDictionary<string, Func<string, string>> CliHighlightTheme(Theme t)
    {
        lock (Gate)
        {
            if (ReferenceEquals(cachedFor, t) && cached is not null) return cached;
            cachedFor = t;
            return cached = new Dictionary<string, Func<string, string>>(StringComparer.Ordinal)
            {
                ["keyword"] = s => t.Fg("syntaxKeyword", s),
                ["built_in"] = s => t.Fg("syntaxType", s),
                ["literal"] = s => t.Fg("syntaxNumber", s),
                ["number"] = s => t.Fg("syntaxNumber", s),
                ["regexp"] = s => t.Fg("syntaxString", s),
                ["string"] = s => t.Fg("syntaxString", s),
                ["subst"] = s => t.Fg("text", s),
                ["comment"] = s => t.Fg("syntaxComment", s),
                ["doctag"] = s => t.Fg("syntaxComment", s),
                ["meta"] = s => t.Fg("muted", s),
                ["function"] = s => t.Fg("syntaxFunction", s),
                ["title"] = s => t.Fg("syntaxFunction", s),
                ["class"] = s => t.Fg("syntaxType", s),
                ["type"] = s => t.Fg("syntaxType", s),
                ["tag"] = s => t.Fg("syntaxPunctuation", s),
                ["name"] = s => t.Fg("syntaxKeyword", s),
                ["attr"] = s => t.Fg("syntaxVariable", s),
                ["variable"] = s => t.Fg("syntaxVariable", s),
                ["params"] = s => t.Fg("syntaxVariable", s),
                ["operator"] = s => t.Fg("syntaxOperator", s),
                ["punctuation"] = s => t.Fg("syntaxPunctuation", s),
                ["emphasis"] = s => t.Italic(s),
                ["strong"] = s => t.Bold(s),
                ["link"] = s => t.Underline(s),
                ["addition"] = s => t.Fg("toolDiffAdded", s),
                ["deletion"] = s => t.Fg("toolDiffRemoved", s),
            };
        }
    }
}
