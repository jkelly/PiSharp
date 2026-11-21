// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/coding-agent/test/syntax-highlight.test.ts ("theme syntax highlighting": the docstring case with
// its v1.6.0 expectation and the #11092 regression) over theme.ts buildCliHighlightTheme.
// Ported expectations, authored against the native port; not an upstream execution capture.
using System.Text.Json;
using PiSharp.Cli.Interactive.Mode;
using static Expect;

/// <summary>Sync 1.6.0: highlighted text outside every scope takes the theme's text color.</summary>
internal static class Sync160UiHighlightCases
{
    public static IEnumerable<(string Id, Func<Task> Run)> All()
    {
        // ---------------------------------------------------------------- syntax-highlight.test.ts
        // #10143, with the 1.6.0 expectation for the unscoped last line.
        yield return ("sync160.ui.highlight.colors-each-line-of-python-docstrings-independently", Sync(() =>
        {
            Themes.InitTheme("dark");
            var theme = Themes.Current;
            const string code = "\"\"\"\nline one\n\nline two\n\"\"\"\nafter";
            var ansi = theme.GetFgAnsi("syntaxString");
            string[] expected =
            [
                $"{ansi}\"\"\"\u001b[39m", $"{ansi}line one\u001b[39m", "", $"{ansi}line two\u001b[39m", $"{ansi}\"\"\"\u001b[39m",
                $"{theme.GetFgAnsi("text")}after\u001b[39m",
            ];
            Equal(string.Join("\n", expected), string.Join("\n", Themes.HighlightCode(code, "python")), "highlightCode");
            Equal(string.Join("\n", expected), string.Join("\n", Themes.GetMarkdownTheme().HighlightCode!(code, "python")), "markdown theme highlightCode");
        }));
        // Regression for #11092.
        yield return ("sync160.ui.highlight.colors-unscoped-highlighted-code-with-the-theme-foreground", Sync(() =>
        {
            Themes.InitTheme("light");
            var lines = Themes.HighlightCode("plainIdentifier\nconst answer = 42;", "typescript");
            var text = Themes.Current.GetFgAnsi("text");
            Equal($"{text}plainIdentifier\u001b[39m", lines[0], "an unscoped identifier");
            Contains(lines[1], $"{text} answer = \u001b[39m", "ordinary text after a keyword");
            Check(text != "\u001b[39m", "the light theme has its own text color, not the terminal's: " + JsonSerializer.Serialize(text));
        }));
    }
}
