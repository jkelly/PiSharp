// lovely-mermaid 0.3.3 (Apache-2.0): src/diagrams/pie.ts.
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace PiSharp.Cli.Interactive.Mode.LovelyMermaid;

internal static class MermaidPie
{
    private const int BarWidth = 20;
    private static readonly string[] Eighths = ["", "▏", "▎", "▍", "▌", "▋", "▊", "▉"];
    private sealed record Slice(string Label, double Value);

    public static MermaidDrawn? Render(string src)
    {
        var statements = MermaidStatements.StatementsOf(src);
        if (MermaidStatements.HeaderKind(statements) != "pie") return null;
        var head = MermaidStatements.Words(statements[0]);
        var showData = head.Any(w => w.ToLowerInvariant() == "showdata");
        var inlineTitle = head.FindIndex(w => w.ToLowerInvariant() == "title");
        string? title = inlineTitle < 0 ? null : MermaidStatements.NonEmpty(string.Join(' ', head.Skip(inlineTitle + 1)));
        var slices = new List<Slice>();
        var warnings = new List<string>();
        foreach (var st in statements.Skip(1))
        {
            if (MermaidStatements.Words(st).FirstOrDefault()?.ToLowerInvariant() == "title")
            {
                title = MermaidStatements.NonEmpty(MermaidJs.Trim(st[(st.ToLowerInvariant().IndexOf("title", StringComparison.Ordinal) + 5)..]));
                continue;
            }
            var chars = MermaidJs.CodePoints(st);
            var quoted = MermaidStatements.QuoteMask(chars);
            var colon = -1;
            for (var i = 0; i < chars.Length; i++) if (chars[i] == ":" && quoted[i] == 0) { colon = i; break; }
            Slice? slice = null;
            if (colon >= 0)
            {
                var label = MermaidLabels.FitLabel(MermaidLabels.CleanLabel(MermaidJs.Join(chars, 0, colon)), 24);
                var value = Number(MermaidJs.Trim(MermaidJs.Join(chars, colon + 1)));
                if (label != "" && double.IsFinite(value) && value >= 0) slice = new(label, value);
            }
            if (slice is null) { warnings.Add($"dropped, unreadable statement: \"{st}\""); continue; }
            if (slices.Count >= MermaidGraph.MaxNodes) { warnings.Add($"diagram truncated: slice cap ({MermaidGraph.MaxNodes}) reached"); break; }
            slices.Add(slice);
        }
        if (slices.Count == 0) return null;
        var labelWidth = slices.Max(s => MermaidWidth.StringWidth(s.Label));
        var total = slices.Aggregate(0d, (sum, s) => sum + s.Value);
        var rows = slices.Select(s =>
        {
            var share = total == 0 ? 0 : s.Value / total;
            var pct = (NumberString(MermaidJs.Round(share * 100)) + "%").PadLeft(4);
            return (Slice: s, Share: share, Suffix: pct + (showData ? $"  ({NumberString(s.Value)})" : ""));
        }).ToList();
        var barX = labelWidth + 2;
        var suffixX = barX + BarWidth + 1;
        var width = suffixX + rows.Max(r => MermaidWidth.StringWidth(r.Suffix));
        var top = title is null ? 0 : 1;
        var canvas = new MermaidCanvas(width, top + rows.Count);
        if (title is not null) MermaidCanvas.DrawText(canvas, title, Math.Max(0, MermaidJs.Half(width - MermaidWidth.StringWidth(title))), 0, MermaidCls.Title);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var y = top + i;
            MermaidCanvas.DrawText(canvas, row.Slice.Label, 0, y, MermaidCls.Text);
            var eighths = (int)MermaidJs.Round(row.Share * BarWidth * 8);
            var bar = new string('█', eighths / 8) + Eighths[eighths % 8];
            if (bar == "" && row.Slice.Value > 0) bar = "▏";
            MermaidCanvas.DrawText(canvas, bar, barX, y, MermaidCls.Edge);
            var barWidth = MermaidWidth.StringWidth(bar);
            MermaidCanvas.DrawText(canvas, new string('░', BarWidth - barWidth), barX + barWidth, y, MermaidCls.Border);
            MermaidCanvas.DrawText(canvas, row.Suffix, suffixX, y, MermaidCls.EdgeLabel);
        }
        return new(canvas, warnings, new());
    }

    // Number(string) accepts empty strings and unsigned radix literals, unlike Double.Parse.
    private static double Number(string text)
    {
        if (text.Length == 0) return 0;
        if (text.Length > 2 && text[0] == '0')
        {
            var radix = char.ToLowerInvariant(text[1]) switch { 'x' => 16, 'b' => 2, 'o' => 8, _ => 0 };
            if (radix != 0)
            {
                BigInteger n = 0;
                foreach (var c in text[2..])
                {
                    var digit = c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'f' ? c - 'a' + 10 : c is >= 'A' and <= 'F' ? c - 'A' + 10 : -1;
                    if (digit < 0 || digit >= radix) return double.NaN;
                    n = n * radix + digit;
                }
                return double.Parse(n.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            }
        }
        if (!Regex.IsMatch(text, @"^[+-]?(?:(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?|Infinity)$", RegexOptions.CultureInvariant)) return double.NaN;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN;
    }

    // ECMAScript prints fixed notation from 1e-6 through values below 1e21.
    private static string NumberString(double value)
    {
        if (value == 0) return "0";
        var raw = value.ToString("R", CultureInfo.InvariantCulture).ToLowerInvariant();
        var e = raw.IndexOf('e');
        if (e < 0) return raw;
        var exponent = int.Parse(raw[(e + 1)..], CultureInfo.InvariantCulture);
        var mantissa = raw[..e];
        if (value >= 1e-6 && value < 1e21)
        {
            var dot = mantissa.IndexOf('.');
            var digits = mantissa.Replace(".", "", StringComparison.Ordinal);
            var position = (dot < 0 ? mantissa.Length : dot) + exponent;
            if (position <= 0) return "0." + new string('0', -position) + digits;
            if (position >= digits.Length) return digits + new string('0', position - digits.Length);
            return digits[..position] + "." + digits[position..];
        }
        return mantissa + "e" + (exponent >= 0 ? "+" : "") + exponent.ToString(CultureInfo.InvariantCulture);
    }
}
