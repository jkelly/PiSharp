// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/coding-agent/test/export-html-ansi-links.test.ts and
// packages/coding-agent/test/export-html-whitespace.test.ts (#12003: /export showed raw OSC 8 hyperlink escape codes around links in
// custom tool output). Ported by reading; nothing is captured from an upstream run.
using PiSharp.CodingAgent.Export;

internal static partial class Program
{
    private const string Esc = "\u001b", Bel = "\u0007", St = "\u001b\\";

    /// <summary>An OSC 8 hyperlink around <paramref name="text"/>, terminated by ST or BEL.</summary>
    private static string Osc8(string url, string text, string end = St) => $"{Esc}]8;;{url}{end}{text}{Esc}]8;;{end}";

    private sealed class FixedLines(params string[] lines) : IToolRenderComponent
    {
        public IReadOnlyList<string> Render(int width) => lines;
    }

    private static IEnumerable<(string, Func<Task>)> Sync160ExportCases() =>
    [
        Case("sync160.export-html.osc8-hyperlinks-become-anchors", () =>
        {
            Equal("see <a href=\"https://example.com/a?b=1&amp;c=2\"><span style=\"text-decoration:underline\">docs</span></a>.",
                AnsiToHtml.Convert($"see {Osc8("https://example.com/a?b=1&c=2", Esc + "[4mdocs" + Esc + "[24m")}."), "link with a styled label");
            Equal("<a href=\"mailto:me@example.com\">mail</a>", AnsiToHtml.Convert(Osc8("mailto:me@example.com", "mail", Bel)), "BEL-terminated mailto link");
            // Link parameters ("id=...") are ignored, the scheme is case-insensitive, and the URL is trimmed.
            Equal("<a href=\"HTTP://example.com/\">x</a>", AnsiToHtml.Convert($"{Esc}]8;id=7;  HTTP://example.com/ {St}x{Esc}]8;;{St}"), "parameters, case and spaces");
        }),

        Case("sync160.export-html.osc8-style-nests-inside-the-anchor-and-an-open-link-is-closed", () =>
        {
            Equal("<span style=\"color:#800000\">red </span><a href=\"https://example.com\"><span style=\"color:#800000\">link</span></a>",
                AnsiToHtml.Convert($"{Esc}[31mred {Esc}]8;;https://example.com{St}link"), "styled text across an unterminated link");
            // A second link closes the first, and a style change inside a link reopens the span inside the anchor.
            Equal("<a href=\"https://a.example\">a</a><a href=\"https://b.example\">b<span style=\"font-weight:bold\">c</span></a>",
                AnsiToHtml.Convert($"{Esc}]8;;https://a.example{St}a{Esc}]8;;https://b.example{St}b{Esc}[1mc"), "consecutive links");
        }),

        Case("sync160.export-html.unsafe-links-and-other-escape-sequences-are-dropped", () =>
        {
            Equal("x", AnsiToHtml.Convert(Osc8("javascript:alert(1)", "x")), "javascript: link");
            // Browsers strip C0 controls from URLs, so they go before the scheme is checked.
            Equal("x", AnsiToHtml.Convert(Osc8("java\u0001script:alert(1)", "x")), "javascript: hidden behind a control character");
            Equal("<a href=\"https://example.com\">x</a>", AnsiToHtml.Convert(Osc8("https://exa\u0001mple.com", "x")), "control character inside a safe URL");
            Equal("x", AnsiToHtml.Convert(Osc8("data:text/html,x", "x")), "data: link");
            Equal("x", AnsiToHtml.Convert(Osc8("/relative/path", "x")), "a link without a scheme");
            // The Latin long s upper-cases to S; JavaScript's /i does not match it and neither does the export.
            Equal("x", AnsiToHtml.Convert(Osc8("http\u017f://example.com", "x")), "non-ASCII scheme letter");
            Equal("abcde", AnsiToHtml.Convert($"{Esc}]0;title{Bel}a{Esc}[2Kb{Esc}]133;A{St}c{Esc}_pi:c{Bel}d{Esc}e"), "OSC, CSI, APC and a stray ESC");
            Equal("a&lt;b", AnsiToHtml.Convert($"a{Esc}<b"), "a stray ESC before text that is escaped");
            Equal("<span style=\"color:#800000;font-weight:bold\">red</span> plain", AnsiToHtml.Convert($"{Esc}[1;31mred{Esc}[0m plain"), "SGR is unchanged");
            Equal("plain", AnsiToHtml.StripAnsiEscapes($"{Esc}[31m{Osc8("https://example.com", "plain")}{Esc}[0m{Esc}[?25h{Esc}"), "stripAnsiEscapes");
        }),

        Case("sync160.export-html.rendered-result-lines-of-escapes-only-are-trimmed", () =>
        {
            Equal("<div class=\"ansi-line\">one</div><div class=\"ansi-line\">two</div>", AnsiToHtml.LinesToHtml(["one", "two"]), "no whitespace between lines");
            // A trailing line that holds only a reset and an empty hyperlink is TUI spacing, as an empty line is.
            var component = new FixedLines("", Esc + "[31mone" + Esc + "[0m", "two", Esc + "[0m" + Esc + "]8;;" + Bel);
            var renderer = new TuiToolHtmlRenderer(new ToolHtmlRendererDependencies(
                _ => new ToolRenderers(null, (_, _, _, _) => component), Theme: null, Cwd: "/tmp"));
            var rendered = renderer.RenderResult("id", "custom", Js.ParseJson("[]"), Js.Undefined, false);
            Equal("<div class=\"ansi-line\"><span style=\"color:#800000\">one</span></div><div class=\"ansi-line\">two</div>", rendered?.Expanded, "expanded HTML");
            Equal(null, rendered?.Collapsed, "collapsed equals expanded");
            // A link in custom tool output reaches the page as an anchor.
            var linked = new TuiToolHtmlRenderer(new ToolHtmlRendererDependencies(
                _ => new ToolRenderers(null, (_, _, _, _) => new FixedLines(Osc8("https://example.com/docs", "docs"))), Theme: null, Cwd: "/tmp"));
            Equal("<div class=\"ansi-line\"><a href=\"https://example.com/docs\">docs</a></div>",
                linked.RenderResult("id", "custom", Js.ParseJson("[]"), Js.Undefined, false)?.Expanded, "link in tool output");
        }),

        Case("sync160.export-html.template-leaves-link-clicks-to-the-link", () =>
        {
            // template.js: a click on a link inside expandable tool output follows the link instead of toggling the output.
            var template = SessionHtmlExport.ReadAsset("template.js");
            Check(template.Contains("onclick=\"if(window.getSelection().toString()||event.target.closest('a'))return;this.classList.toggle('expanded')\"",
                StringComparison.Ordinal), "expandable ANSI output handler");
            Equal("f7961ffb1ac9fc4dc6659ac6df97dc2bc777c494", SessionHtmlExport.AssetBlobs["template.js"], "template.js is the v1.6.0 blob");
        }),
    ];
}
