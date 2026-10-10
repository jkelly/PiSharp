// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/tui/src/colors.ts and packages/tui/src/oklab.ts
// (Oklab and OKHSL after Björn Ottosson's reference implementation, Copyright (c) 2021 Björn Ottosson, MIT).
using System.Globalization;
using System.Text.RegularExpressions;

namespace PiSharp.Tui.Pi;

public enum ColorKind { Indexed, Rgb, Oklch }
/// <summary>A concrete color: an ANSI palette index, sRGB channels or OKLCH.</summary>
public readonly record struct Color(ColorKind Kind, double A, double B = 0, double C = 0)
{
    public int Index => (int)A;
    public static Color Indexed(int index) => index is < 0 or > 255 ? throw new ArgumentException($"ANSI color index must be an integer from 0 to 255: {index}") : new(ColorKind.Indexed, index);
    public static Color Rgb(double r, double g, double b)
    {
        foreach (var (name, value) in new[] { ("r", r), ("g", g), ("b", b) })
        {
            if (!double.IsFinite(value)) throw new ArgumentException($"{name} must be finite");
            if (value is < 0 or > 255) throw new ArgumentException($"{name} must be between 0 and 255: {value}");
        }
        return new(ColorKind.Rgb, r, g, b);
    }
    public static Color Oklch(double l, double c, double h)
    {
        if (!double.IsFinite(l) || !double.IsFinite(c) || !double.IsFinite(h)) throw new ArgumentException("l, c and h must be finite");
        if (l is < 0 or > 1) throw new ArgumentException($"l must be between 0 and 1: {l}");
        if (c < 0) throw new ArgumentException($"c must not be negative: {c}");
        return new(ColorKind.Oklch, l, c, (h % 360 + 360) % 360);
    }
}
public enum TerminalColorMode { Color256, TrueColor }
public sealed record TextStyle(Color? Fg = null, Color? Bg = null, bool Bold = false, bool Dim = false, bool Italic = false, bool Underline = false, bool Inverse = false, bool Strikethrough = false);

public static partial class Colors
{
    private const string Number = @"[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:e[+-]?\d+)?";
    [GeneratedRegex(@"^oklch\(\s*(" + Number + @")(%)?\s+(" + Number + @")\s+(" + Number + @")(?:deg)?\s*\)$", RegexOptions.IgnoreCase)] private static partial Regex OklchPattern();
    [GeneratedRegex(@"^okhsl\(\s*(" + Number + @")(?:deg)?\s+(" + Number + @")(%)?\s+(" + Number + @")(%)?\s*\)$", RegexOptions.IgnoreCase)] private static partial Regex OkhslPattern();
    [GeneratedRegex("^#([0-9a-f]{3}|[0-9a-f]{6})$", RegexOptions.IgnoreCase)] private static partial Regex HexPattern();
    private static double Parse(string value) => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
    internal static double JsRound(double value) => Math.Floor(value + 0.5);

    public static Color Okhsl(double h, double s, double l)
    {
        if (!double.IsFinite(h) || !double.IsFinite(s) || !double.IsFinite(l)) throw new ArgumentException("h, s and l must be finite");
        if (s is < 0 or > 1) throw new ArgumentException($"s must be between 0 and 1: {s}");
        if (l is < 0 or > 1) throw new ArgumentException($"l must be between 0 and 1: {l}");
        var rgb = Oklab.OkhslToRgb(h, s, l);
        return Color.Rgb(rgb.R, rgb.G, rgb.B);
    }

    /// <summary>Parses <c>#rgb</c>, <c>#rrggbb</c>, <c>oklch(...)</c>, <c>okhsl(...)</c>; throws for anything else.</summary>
    public static Color ParseColor(string value)
    {
        var hex = HexPattern().Match(value);
        if (hex.Success)
        {
            var digits = hex.Groups[1].Value.Length == 3 ? string.Concat(hex.Groups[1].Value.Select(d => $"{d}{d}")) : hex.Groups[1].Value;
            return Color.Rgb(Convert.ToInt32(digits[..2], 16), Convert.ToInt32(digits[2..4], 16), Convert.ToInt32(digits[4..6], 16));
        }
        var oklch = OklchPattern().Match(value);
        if (oklch.Success) return Color.Oklch(Parse(oklch.Groups[1].Value) / (oklch.Groups[2].Success ? 100 : 1), Parse(oklch.Groups[3].Value), Parse(oklch.Groups[4].Value));
        var okhsl = OkhslPattern().Match(value);
        if (okhsl.Success)
            return Okhsl(Parse(okhsl.Groups[1].Value), Parse(okhsl.Groups[2].Value) / (okhsl.Groups[3].Success ? 100 : 1), Parse(okhsl.Groups[4].Value) / (okhsl.Groups[5].Success ? 100 : 1));
        throw new ArgumentException($"Invalid color value: {value}");
    }
    public static Color ParseColor(int index) => Color.Indexed(index);

    private static readonly RgbColor[] Basic = [new(0, 0, 0), new(128, 0, 0), new(0, 128, 0), new(128, 128, 0), new(0, 0, 128), new(128, 0, 128), new(0, 128, 128), new(192, 192, 192),
        new(128, 128, 128), new(255, 0, 0), new(0, 255, 0), new(255, 255, 0), new(0, 0, 255), new(255, 0, 255), new(0, 255, 255), new(255, 255, 255)];
    private static readonly int[] Cube = [0, 95, 135, 175, 215, 255];
    private static readonly int[] Gray = Enumerable.Range(0, 24).Select(i => 8 + i * 10).ToArray();

    private static (double R, double G, double B) IndexedToRgb(int index)
    {
        if (index < 16) return (Basic[index].R, Basic[index].G, Basic[index].B);
        if (index < 232) { var cube = index - 16; return (Cube[cube / 36], Cube[cube % 36 / 6], Cube[cube % 6]); }
        var gray = 8 + (index - 232) * 10;
        return (gray, gray, gray);
    }

    private static bool InGamut(double[] linear) => linear.All(channel => channel >= -1e-7 && channel <= 1 + 1e-7);

    private static (double R, double G, double B) OklchToRgb(double l, double c, double h)
    {
        var radians = h * Math.PI / 180; var cos = Math.Cos(radians); var sin = Math.Sin(radians);
        double[] At(double chroma) => Oklab.OklabToLinearSrgb([l, chroma * cos, chroma * sin]);
        var direct = At(c);
        if (InGamut(direct)) return Oklab.LinearSrgbToRgb(direct);
        var linear = At(0); double low = 0, high = c;
        for (var i = 0; i < 20; i++)
        {
            var chroma = (low + high) / 2; var candidate = At(chroma);
            if (InGamut(candidate)) { low = chroma; linear = candidate; } else high = chroma;
        }
        return Oklab.LinearSrgbToRgb(linear);
    }

    public static (double R, double G, double B) ToRgb(Color color) => color.Kind switch
    {
        ColorKind.Indexed => IndexedToRgb(color.Index),
        ColorKind.Rgb => (color.A, color.B, color.C),
        _ => OklchToRgb(color.A, color.B, color.C)
    };

    public static (double L, double C, double H) ToOklch(Color color)
    {
        if (color.Kind == ColorKind.Oklch) return (color.A, color.B, color.C);
        var lab = Oklab.RgbToOklab(ToRgb(color));
        return (lab[0], Math.Sqrt(lab[1] * lab[1] + lab[2] * lab[2]), (Math.Atan2(lab[2], lab[1]) * 180 / Math.PI + 360) % 360);
    }
    public static (double H, double S, double L) ToOkhsl(Color color) => Oklab.RgbToOkhsl(ToRgb(color));

    public static string ToHex(Color color)
    {
        var (r, g, b) = ToRgb(color);
        return "#" + ((int)JsRound(r)).ToString("x2", CultureInfo.InvariantCulture) + ((int)JsRound(g)).ToString("x2", CultureInfo.InvariantCulture) + ((int)JsRound(b)).ToString("x2", CultureInfo.InvariantCulture);
    }

    public static Color Mix(Color first, Color second, double amount, bool srgb = false)
    {
        if (!double.IsFinite(amount)) throw new ArgumentException("amount must be finite");
        if (amount is < 0 or > 1) throw new ArgumentException($"amount must be between 0 and 1: {amount}");
        if (srgb)
        {
            var a = ToRgb(first); var b = ToRgb(second);
            return Color.Rgb(a.R + (b.R - a.R) * amount, a.G + (b.G - a.G) * amount, a.B + (b.B - a.B) * amount);
        }
        var x = ToOklch(first); var y = ToOklch(second);
        var firstHue = x.C < 1e-7 ? y.H : x.H; var secondHue = y.C < 1e-7 ? firstHue : y.H;
        var delta = (secondHue - firstHue + 540) % 360 - 180;
        return Color.Oklch(x.L + (y.L - x.L) * amount, x.C + (y.C - x.C) * amount, firstHue + delta * amount);
    }

    private static int Closest(int[] values, double target)
    {
        var index = 0; var best = double.PositiveInfinity;
        for (var i = 0; i < values.Length; i++) { var distance = Math.Abs(target - values[i]); if (distance < best) { index = i; best = distance; } }
        return index;
    }
    private static double Distance((double R, double G, double B) a, (double R, double G, double B) b)
    { var dr = a.R - b.R; var dg = a.G - b.G; var db = a.B - b.B; return dr * dr * 0.299 + dg * dg * 0.587 + db * db * 0.114; }

    public static int RgbToAnsi256((double R, double G, double B) color)
    {
        int ri = Closest(Cube, color.R), gi = Closest(Cube, color.G), bi = Closest(Cube, color.B);
        var cubeColor = ((double)Cube[ri], (double)Cube[gi], (double)Cube[bi]);
        var gray = JsRound(0.299 * color.R + 0.587 * color.G + 0.114 * color.B);
        var grayOffset = Closest(Gray, gray); double grayValue = Gray[grayOffset];
        var spread = Math.Max(color.R, Math.Max(color.G, color.B)) - Math.Min(color.R, Math.Min(color.G, color.B));
        if (spread < 10 && Distance(color, (grayValue, grayValue, grayValue)) < Distance(color, cubeColor)) return 232 + grayOffset;
        return 16 + 36 * ri + 6 * gi + bi;
    }

    private static string Ansi(Color color, TerminalColorMode mode, bool background)
    {
        var code = background ? 48 : 38;
        if (color.Kind == ColorKind.Indexed) return $"\u001b[{code};5;{color.Index}m";
        var rgb = ToRgb(color);
        return mode == TerminalColorMode.TrueColor
            ? $"\u001b[{code};2;{(int)JsRound(rgb.R)};{(int)JsRound(rgb.G)};{(int)JsRound(rgb.B)}m"
            : $"\u001b[{code};5;{RgbToAnsi256(rgb)}m";
    }
    public static string ForegroundAnsi(Color color, TerminalColorMode mode) => Ansi(color, mode, false);
    public static string BackgroundAnsi(Color color, TerminalColorMode mode) => Ansi(color, mode, true);

    public static string StyleText(string text, TextStyle style, TerminalColorMode mode) =>
        StyleTextWithAnsi(text, style.Fg is { } fg ? ForegroundAnsi(fg, mode) : null, style.Bg is { } bg ? BackgroundAnsi(bg, mode) : null, style);

    /// <summary>Applies precomputed color sequences and attributes; resets close in reverse order.</summary>
    public static string StyleTextWithAnsi(string text, string? fgAnsi, string? bgAnsi, TextStyle attributes)
    {
        string prefix = "", suffix = "";
        if (!string.IsNullOrEmpty(fgAnsi)) { prefix += fgAnsi; suffix = "\u001b[39m"; }
        if (!string.IsNullOrEmpty(bgAnsi)) { prefix += bgAnsi; suffix = "\u001b[49m" + suffix; }
        if (attributes.Bold) prefix += "\u001b[1m";
        if (attributes.Dim) prefix += "\u001b[2m";
        if (attributes.Bold || attributes.Dim) suffix = "\u001b[22m" + suffix;
        if (attributes.Italic) { prefix += "\u001b[3m"; suffix = "\u001b[23m" + suffix; }
        if (attributes.Underline) { prefix += "\u001b[4m"; suffix = "\u001b[24m" + suffix; }
        if (attributes.Inverse) { prefix += "\u001b[7m"; suffix = "\u001b[27m" + suffix; }
        if (attributes.Strikethrough) { prefix += "\u001b[9m"; suffix = "\u001b[29m" + suffix; }
        return prefix + text + suffix;
    }

    public static TerminalColorMode GetTerminalColorMode(TerminalCapabilities? capabilities = null) =>
        (capabilities ?? TerminalImage.GetCapabilities()).TrueColor ? TerminalColorMode.TrueColor : TerminalColorMode.Color256;
}

/// <summary>Oklab and OKHSL conversions (oklab.ts).</summary>
public static class Oklab
{
    private static readonly double[][] LinearSrgbToLms = [[0.4122214694707629, 0.5363325372617349, 0.0514459932675022], [0.2119034958178251, 0.6806995506452344, 0.1073969535369405], [0.0883024591900564, 0.2817188391361215, 0.6299787016738222]];
    private static readonly double[][] LmsToLab = [[0.210454268309314, 0.793617774702305, -0.0040720430116193], [1.9779985324311684, -2.42859224204858, 0.450593709617411], [0.0259040424655478, 0.7827717124575296, -0.8086757549230774]];
    private static readonly double[][] LabToLms = [[1, 0.3963377773761749, 0.2158037573099136], [1, -0.1055613458156586, -0.0638541728258133], [1, -0.0894841775298119, -1.2914855480194092]];
    private static readonly double[][] LmsToLinearSrgb = [[4.0767416360759583, -3.3077115392580629, 0.2309699031821043], [-1.2684379732850315, 2.6097573492876882, -0.341319376002657], [-0.0041960761386756, -0.7034186179359362, 1.7076146940746117]];
    private static readonly (double X, double Y, double[] K)[] SaturationFit =
    [
        (-1.8817031, -0.80936501, [1.19086277, 1.76576728, 0.59662641, 0.75515197, 0.56771245]),
        (1.8144408, -1.19445267, [0.73956515, -0.45954404, 0.08285427, 0.12541073, -0.14503204]),
        (0.13110758, 1.81333971, [1.35733652, -0.00915799, -1.1513021, -0.50559606, 0.00692167]),
    ];
    private const double K1 = 0.206, K2 = 0.03, K3 = (1 + K1) / (1 + K2);
    private static double[] Multiply(double[][] m, double[] v) => [.. m.Select(row => row[0] * v[0] + row[1] * v[1] + row[2] * v[2])];

    public static double OklabToOkhslLightness(double x) => 0.5 * (K3 * x - K1 + Math.Sqrt((K3 * x - K1) * (K3 * x - K1) + 4 * K2 * K3 * x));
    private static double OkhslToOklabLightness(double x) => (x * x + K1 * x) / (K3 * (x + K2));
    private static double LinearToSrgb(double value) => value > 0.0031308 ? 1.055 * Math.Pow(value, 1 / 2.4) - 0.055 : 12.92 * value;
    private static double SrgbToLinear(double value) => value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);

    public static double[] OklabToLinearSrgb(double[] lab) => Multiply(LmsToLinearSrgb, [.. Multiply(LabToLms, lab).Select(v => v * v * v)]);
    private static double[] LinearSrgbToOklab(double[] rgb) => Multiply(LmsToLab, [.. Multiply(LinearSrgbToLms, rgb).Select(Math.Cbrt)]);
    public static double[] RgbToOklab((double R, double G, double B) rgb) => LinearSrgbToOklab([SrgbToLinear(rgb.R / 255), SrgbToLinear(rgb.G / 255), SrgbToLinear(rgb.B / 255)]);
    public static (double R, double G, double B) LinearSrgbToRgb(double[] linear)
    {
        double Channel(double value) => Colors.JsRound(Math.Min(1, Math.Max(0, LinearToSrgb(value))) * 255);
        return (Channel(linear[0]), Channel(linear[1]), Channel(linear[2]));
    }

    private static double[] LmsSlopes(double a, double b) => [.. LabToLms.Select(row => row[1] * a + row[2] * b)];

    private static double MaxSaturation(double a, double b)
    {
        var channel = 0;
        while (channel < 2 && !(SaturationFit[channel].X * a + SaturationFit[channel].Y * b > 1)) channel++;
        var k = SaturationFit[channel].K; var weights = LmsToLinearSrgb[channel];
        var saturation = k[0] + k[1] * a + k[2] * b + k[3] * a * a + k[4] * a * b;
        var slopes = LmsSlopes(a, b);
        var baseValues = slopes.Select(s => 1 + saturation * s).ToArray();
        double Dot(IEnumerable<double> values) => values.Select((value, index) => weights[index] * value).Sum();
        var f = Dot(baseValues.Select(v => v * v * v));
        var f1 = Dot(baseValues.Select((v, i) => 3 * slopes[i] * v * v));
        var f2 = Dot(baseValues.Select((v, i) => 6 * slopes[i] * slopes[i] * v));
        return saturation - f * f1 / (f1 * f1 - 0.5 * f * f2);
    }

    private static (double L, double C) Cusp(double a, double b)
    {
        var saturation = MaxSaturation(a, b);
        var lightness = Math.Cbrt(1 / OklabToLinearSrgb([1, saturation * a, saturation * b]).Max());
        return (lightness, lightness * saturation);
    }

    private static double MaxChroma(double a, double b, double lightness, (double L, double C) cusp)
    {
        if (lightness <= cusp.L) return cusp.C * lightness / cusp.L;
        var t = cusp.C * (lightness - 1) / (cusp.L - 1);
        var slopes = LmsSlopes(a, b);
        var lms = slopes.Select(k => lightness + t * k).ToArray();
        var cubes = lms.Select(v => v * v * v).ToArray();
        var first = lms.Select((v, i) => 3 * slopes[i] * v * v).ToArray();
        var second = lms.Select((v, i) => 6 * slopes[i] * slopes[i] * v).ToArray();
        double Dot(double[] row, double[] values) => row[0] * values[0] + row[1] * values[1] + row[2] * values[2];
        var steps = LmsToLinearSrgb.Select(row =>
        {
            var f = Dot(row, cubes) - 1; var f1 = Dot(row, first); var f2 = Dot(row, second);
            var u = f1 / (f1 * f1 - 0.5 * f * f2);
            return u >= 0 ? -f * u : double.MaxValue;
        });
        return t + steps.Min();
    }

    private static (double C0, double CMid, double CMax) ChromaStops(double l, double a, double b)
    {
        var peak = Cusp(a, b);
        var cMax = MaxChroma(a, b, l, peak);
        var k = cMax / Math.Min(l * (peak.C / peak.L), (1 - l) * (peak.C / (1 - peak.L)));
        var midS = 0.11516993 + 1 / (7.4477897 + 4.1590124 * b + a * (-2.19557347 + 1.75198401 * b + a * (-2.13704948 - 10.02301043 * b + a * (-4.24894561 + 5.38770819 * b + 4.69891013 * a))));
        var midT = 0.11239642 + 1 / (1.6132032 - 0.68124379 * b + a * (0.40370612 + 0.90148123 * b + a * (-0.27087943 + 0.6122399 * b + a * (0.00299215 - 0.45399568 * b - 0.14661872 * a))));
        var cMid = 0.9 * k * Math.Sqrt(Math.Sqrt(1 / (1 / Math.Pow(l * midS, 4) + 1 / Math.Pow((1 - l) * midT, 4))));
        var c0 = Math.Sqrt(1 / (1 / Math.Pow(l * 0.4, 2) + 1 / Math.Pow((1 - l) * 0.8, 2)));
        return (c0, cMid, cMax);
    }

    public static (double R, double G, double B) OkhslToRgb(double hue, double saturation, double lightness)
    {
        var l = OkhslToOklabLightness(lightness);
        double[] lab = [l, 0, 0];
        if (l > 0 && l < 1 && saturation > 0)
        {
            var angle = 2 * Math.PI * ((hue % 360 + 360) % 360) / 360;
            var a = Math.Cos(angle); var b = Math.Sin(angle);
            var (c0, cMid, cMax) = ChromaStops(l, a, b);
            double chroma;
            if (saturation < 0.8) { var t = 1.25 * saturation; var k1 = 0.8 * c0; chroma = t * k1 / (1 - (1 - k1 / cMid) * t); }
            else { var t = 5 * (saturation - 0.8); var k1 = 0.2 * cMid * cMid * 1.25 * 1.25 / c0; chroma = cMid + t * k1 / (1 - (1 - k1 / (cMax - cMid)) * t); }
            lab = [l, chroma * a, chroma * b];
        }
        return LinearSrgbToRgb(OklabToLinearSrgb(lab));
    }

    public static (double H, double S, double L) RgbToOkhsl((double R, double G, double B) rgb)
    {
        var lab = RgbToOklab(rgb);
        var chroma = Math.Sqrt(lab[1] * lab[1] + lab[2] * lab[2]);
        var lightness = OklabToOkhslLightness(lab[0]);
        if (chroma < 1e-9 || lightness <= 0 || lightness >= 1) return (0, 0, lightness);
        var hue = (Math.Atan2(lab[2], lab[1]) * 180 / Math.PI + 360) % 360;
        var (c0, cMid, cMax) = ChromaStops(lab[0], lab[1] / chroma, lab[2] / chroma);
        double saturation;
        if (chroma < cMid) { var k1 = 0.8 * c0; saturation = 0.8 * (chroma / (k1 + (1 - k1 / cMid) * chroma)); }
        else { var k1 = 0.2 * cMid * cMid * 1.25 * 1.25 / c0; var offset = chroma - cMid; saturation = 0.8 + 0.2 * (offset / (k1 + (1 - k1 / (cMax - cMid)) * offset)); }
        return (hue, Math.Min(1, Math.Max(0, saturation)), lightness);
    }
}
