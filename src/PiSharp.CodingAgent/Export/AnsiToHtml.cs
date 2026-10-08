// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/export-html/ansi-to-html.ts.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.CodingAgent.Export;

/// <summary>ANSI SGR escapes to HTML with inline styles (16/256/RGB colors, bold, dim, italic, underline, reset).</summary>
public static class AnsiToHtml
{
    private static readonly string[] AnsiColors =
    [
        "#000000", "#800000", "#008000", "#808000", "#000080", "#800080", "#008080", "#c0c0c0",
        "#808080", "#ff0000", "#00ff00", "#ffff00", "#0000ff", "#ff00ff", "#00ffff", "#ffffff"
    ];

    private static string Color256ToHex(double index)
    {
        if (index < 16) return AnsiColors[(int)index];
        if (index < 232)
        {
            var cube = index - 16;
            var r = Math.Floor(cube / 36); var g = Math.Floor(cube % 36 / 6); var b = cube % 6;
            static string Hex(double n) => Js.IntegerToString(n == 0 ? 0 : 55 + n * 40, 16).PadLeft(2, '0');
            return "#" + Hex(r) + Hex(g) + Hex(b);
        }
        var gray = Js.IntegerToString(8 + (index - 232) * 10, 16).PadLeft(2, '0');
        return "#" + gray + gray + gray;
    }

    private static string EscapeHtml(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#039;");

    private sealed class Style { public string? Fg, Bg; public bool Bold, Dim, Italic, Underline; }

    private static string StyleToInlineCss(Style style)
    {
        var parts = new List<string>();
        if (style.Fg is not null) parts.Add("color:" + style.Fg);
        if (style.Bg is not null) parts.Add("background-color:" + style.Bg);
        if (style.Bold) parts.Add("font-weight:bold");
        if (style.Dim) parts.Add("opacity:0.6");
        if (style.Italic) parts.Add("font-style:italic");
        if (style.Underline) parts.Add("text-decoration:underline");
        return string.Join(";", parts);
    }

    private static bool HasStyle(Style style) => style.Fg is not null || style.Bg is not null || style.Bold || style.Dim || style.Italic || style.Underline;

    private static void ApplySgrCode(double[] parameters, Style style)
    {
        var i = 0;
        double? At(int index) => index < parameters.Length ? parameters[index] : null;
        while (i < parameters.Length)
        {
            var code = parameters[i];
            if (code == 0) { style.Fg = null; style.Bg = null; style.Bold = false; style.Dim = false; style.Italic = false; style.Underline = false; }
            else if (code == 1) style.Bold = true;
            else if (code == 2) style.Dim = true;
            else if (code == 3) style.Italic = true;
            else if (code == 4) style.Underline = true;
            else if (code == 22) { style.Bold = false; style.Dim = false; }
            else if (code == 23) style.Italic = false;
            else if (code == 24) style.Underline = false;
            else if (code is >= 30 and <= 37) style.Fg = AnsiColors[(int)code - 30];
            else if (code == 38)
            {
                if (At(i + 1) == 5 && parameters.Length > i + 2) { style.Fg = Color256ToHex(parameters[i + 2]); i += 2; }
                else if (At(i + 1) == 2 && parameters.Length > i + 4)
                { style.Fg = $"rgb({Js.NumberToString(parameters[i + 2])},{Js.NumberToString(parameters[i + 3])},{Js.NumberToString(parameters[i + 4])})"; i += 4; }
            }
            else if (code == 39) style.Fg = null;
            else if (code is >= 40 and <= 47) style.Bg = AnsiColors[(int)code - 40];
            else if (code == 48)
            {
                if (At(i + 1) == 5 && parameters.Length > i + 2) { style.Bg = Color256ToHex(parameters[i + 2]); i += 2; }
                else if (At(i + 1) == 2 && parameters.Length > i + 4)
                { style.Bg = $"rgb({Js.NumberToString(parameters[i + 2])},{Js.NumberToString(parameters[i + 3])},{Js.NumberToString(parameters[i + 4])})"; i += 4; }
            }
            else if (code == 49) style.Bg = null;
            else if (code is >= 90 and <= 97) style.Fg = AnsiColors[(int)code - 90 + 8];
            else if (code is >= 100 and <= 107) style.Bg = AnsiColors[(int)code - 100 + 8];
            i++;
        }
    }

    private static readonly Regex AnsiPattern = new(@"\x1b\[([0-9;]*)m", RegexOptions.CultureInvariant);

    /// <summary>parseInt(p, 10) || 0 over the digits of one SGR parameter.</summary>
    private static double ParseParameter(string value) => value.Length == 0 ? 0 : double.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);

    /// <summary>ansiToHtml.</summary>
    public static string Convert(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var style = new Style(); var result = new StringBuilder(); var lastIndex = 0; var inSpan = false;
        foreach (Match match in AnsiPattern.Matches(text))
        {
            var before = text[lastIndex..match.Index];
            if (before.Length > 0) result.Append(EscapeHtml(before));
            var parameterText = match.Groups[1].Value;
            var parameters = parameterText.Length > 0 ? parameterText.Split(';').Select(ParseParameter).ToArray() : [0d];
            if (inSpan) { result.Append("</span>"); inSpan = false; }
            ApplySgrCode(parameters, style);
            if (HasStyle(style)) { result.Append("<span style=\"").Append(StyleToInlineCss(style)).Append("\">"); inSpan = true; }
            lastIndex = match.Index + match.Length;
        }
        var remaining = text[lastIndex..];
        if (remaining.Length > 0) result.Append(EscapeHtml(remaining));
        if (inSpan) result.Append("</span>");
        return result.ToString();
    }

    /// <summary>ansiLinesToHtml: each line in a div.ansi-line, an empty line as &amp;nbsp;.</summary>
    public static string LinesToHtml(IEnumerable<string> lines) =>
        string.Concat(lines.Select(line => "<div class=\"ansi-line\">" + (Convert(line) is { Length: > 0 } html ? html : "&nbsp;") + "</div>"));
}
