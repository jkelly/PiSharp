// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/tui/src/colors.ts and packages/tui/src/oklab.ts.
// oklab.ts ports Bjorn Ottosson's Oklab/OKHSL reference implementation (https://bottosson.github.io/posts/colorpicker/),
// Copyright (c) 2021 Bjorn Ottosson, used under the MIT license: Permission is hereby granted, free of charge, to any person
// obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without
// restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
// The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED.
using System.Globalization;
using System.Text.RegularExpressions;

namespace PiSharp.CodingAgent.Export;

/// <summary>sRGB channels 0-255 (pi-tui RgbColor). Channels may be fractional before rounding.</summary>
public readonly record struct RgbChannels(double R, double G, double B);
/// <summary>OKLCH channels (pi-tui OklchChannels).</summary>
public readonly record struct OklchChannels(double L, double C, double H);
/// <summary>OKHSL channels: hue degrees, saturation and lightness 0-1.</summary>
public readonly record struct OkhslChannels(double H, double S, double L);

/// <summary>A concrete pi-tui Color: an ANSI palette index, sRGB channels, or OKLCH.</summary>
public abstract record PiColor
{
    private PiColor() { }
    public sealed record Indexed(int Index) : PiColor;
    public sealed record Rgb(double R, double G, double B) : PiColor;
    public sealed record Oklch(double L, double C, double H) : PiColor;
}

/// <summary>pi-tui colors.ts/oklab.ts color math with JavaScript number semantics (Math.round, Math.hypot).</summary>
public static class PiColors
{
    private static void RequireFinite(double value, string name)
    { if (!double.IsFinite(value)) throw new ArgumentException($"{name} must be finite"); }

    public static PiColor.Indexed Indexed(double index)
    {
        if (index != Math.Floor(index) || !double.IsFinite(index) || index < 0 || index > 255)
            throw new ArgumentException($"ANSI color index must be an integer from 0 to 255: {Js.NumberToString(index)}");
        return new((int)index);
    }

    public static PiColor.Rgb Rgb(double r, double g, double b)
    {
        foreach (var (name, value) in new[] { ("r", r), ("g", g), ("b", b) })
        {
            RequireFinite(value, name);
            if (value < 0 || value > 255) throw new ArgumentException($"{name} must be between 0 and 255: {Js.NumberToString(value)}");
        }
        return new(r, g, b);
    }

    public static PiColor.Oklch Oklch(double l, double c, double h)
    {
        RequireFinite(l, "l"); RequireFinite(c, "c"); RequireFinite(h, "h");
        if (l < 0 || l > 1) throw new ArgumentException($"l must be between 0 and 1: {Js.NumberToString(l)}");
        if (c < 0) throw new ArgumentException($"c must not be negative: {Js.NumberToString(c)}");
        return new(l, c, ((h % 360) + 360) % 360);
    }

    private const string NumberPattern = @"[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:e[+-]?\d+)?";
    private static readonly Regex HexPattern = new(@"^#([\da-f]{3}|[\da-f]{6})\z", RegexOptions.IgnoreCase | RegexOptions.ECMAScript | RegexOptions.CultureInvariant);
    private static readonly Regex OklchPattern = new(@"^oklch\(\s*(" + NumberPattern + @")(%)?\s+(" + NumberPattern + @")\s+(" + NumberPattern + @")(?:deg)?\s*\)\z",
        RegexOptions.IgnoreCase | RegexOptions.ECMAScript | RegexOptions.CultureInvariant);
    private static readonly Regex OkhslPattern = new(@"^okhsl\(\s*(" + NumberPattern + @")(?:deg)?\s+(" + NumberPattern + @")(%)?\s+(" + NumberPattern + @")(%)?\s*\)\z",
        RegexOptions.IgnoreCase | RegexOptions.ECMAScript | RegexOptions.CultureInvariant);

    /// <summary>An OKHSL color converted to sRGB (okhslColor).</summary>
    public static PiColor.Rgb Okhsl(double h, double s, double l)
    {
        RequireFinite(h, "h"); RequireFinite(s, "s"); RequireFinite(l, "l");
        if (s < 0 || s > 1) throw new ArgumentException($"s must be between 0 and 1: {Js.NumberToString(s)}");
        if (l < 0 || l > 1) throw new ArgumentException($"l must be between 0 and 1: {Js.NumberToString(l)}");
        var rgb = Oklab.OkhslToRgb(h, s, l);
        return Rgb(rgb.R, rgb.G, rgb.B);
    }

    public static OkhslChannels ToOkhsl(PiColor color) => Oklab.RgbToOkhsl(ToRgb(color));

    /// <summary>parseColor: a palette index, #rgb/#rrggbb, oklch() or okhsl().</summary>
    public static PiColor Parse(double index) => Indexed(index);

    public static PiColor Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var hex = HexPattern.Match(value);
        if (hex.Success)
        {
            var digits = hex.Groups[1].Value.Length == 3 ? string.Concat(hex.Groups[1].Value.Select(digit => new string(digit, 2))) : hex.Groups[1].Value;
            return Rgb(int.Parse(digits[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                int.Parse(digits[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture), int.Parse(digits[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }
        var oklch = OklchPattern.Match(value);
        if (oklch.Success)
        {
            var lightness = Float(oklch.Groups[1].Value) / (oklch.Groups[2].Success ? 100 : 1);
            return Oklch(lightness, Float(oklch.Groups[3].Value), Float(oklch.Groups[4].Value));
        }
        var okhsl = OkhslPattern.Match(value);
        if (okhsl.Success)
        {
            var saturation = Float(okhsl.Groups[2].Value) / (okhsl.Groups[3].Success ? 100 : 1);
            var lightness = Float(okhsl.Groups[4].Value) / (okhsl.Groups[5].Success ? 100 : 1);
            return Okhsl(Float(okhsl.Groups[1].Value), saturation, lightness);
        }
        throw new ArgumentException($"Invalid color value: {value}");
    }

    private static double Float(string value) => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static readonly RgbChannels[] BasicColors =
    [
        new(0, 0, 0), new(128, 0, 0), new(0, 128, 0), new(128, 128, 0), new(0, 0, 128), new(128, 0, 128), new(0, 128, 128), new(192, 192, 192),
        new(128, 128, 128), new(255, 0, 0), new(0, 255, 0), new(255, 255, 0), new(0, 0, 255), new(255, 0, 255), new(0, 255, 255), new(255, 255, 255)
    ];
    private static readonly int[] CubeValues = [0, 95, 135, 175, 215, 255];

    private static RgbChannels IndexedToRgb(int index)
    {
        if (index < 16) return BasicColors[index];
        if (index < 232)
        {
            var cube = index - 16;
            return new(CubeValues[cube / 36], CubeValues[cube % 36 / 6], CubeValues[cube % 6]);
        }
        var gray = 8 + (index - 232) * 10;
        return new(gray, gray, gray);
    }

    private static bool IsInSrgbGamut(Oklab.Vector linear)
    {
        const double epsilon = 1e-7;
        return linear.X >= -epsilon && linear.X <= 1 + epsilon && linear.Y >= -epsilon && linear.Y <= 1 + epsilon &&
            linear.Z >= -epsilon && linear.Z <= 1 + epsilon;
    }

    private static RgbChannels OklchToRgb(double l, double c, double h)
    {
        var radians = h * Math.PI / 180;
        var cos = Math.Cos(radians); var sin = Math.Sin(radians);
        Oklab.Vector AtChroma(double chroma) => Oklab.OklabToLinearSrgb(new(l, chroma * cos, chroma * sin));
        var direct = AtChroma(c);
        if (IsInSrgbGamut(direct)) return Oklab.LinearSrgbToRgb(direct);
        var linear = AtChroma(0); double low = 0, high = c;
        for (var index = 0; index < 20; index++)
        {
            var chroma = (low + high) / 2;
            var candidate = AtChroma(chroma);
            if (IsInSrgbGamut(candidate)) { low = chroma; linear = candidate; }
            else high = chroma;
        }
        return Oklab.LinearSrgbToRgb(linear);
    }

    public static RgbChannels ToRgb(PiColor color) => color switch
    {
        PiColor.Indexed indexed => IndexedToRgb(indexed.Index),
        PiColor.Rgb rgb => new(rgb.R, rgb.G, rgb.B),
        PiColor.Oklch oklch => OklchToRgb(oklch.L, oklch.C, oklch.H),
        _ => throw new ArgumentOutOfRangeException(nameof(color))
    };

    public static OklchChannels ToOklch(PiColor color)
    {
        if (color is PiColor.Oklch oklch) return new(oklch.L, oklch.C, oklch.H);
        var lab = Oklab.RgbToOklab(ToRgb(color));
        return new(lab.X, Js.Hypot(lab.Y, lab.Z), (Math.Atan2(lab.Z, lab.Y) * 180 / Math.PI + 360) % 360);
    }

    public static string ToHex(PiColor color)
    {
        var rgb = ToRgb(color);
        return "#" + Js.Hex2(rgb.R) + Js.Hex2(rgb.G) + Js.Hex2(rgb.B);
    }

    /// <summary>mixColors (OKLCH space unless <paramref name="srgb"/>).</summary>
    public static PiColor Mix(PiColor first, PiColor second, double amount, bool srgb = false)
    {
        RequireFinite(amount, "amount");
        if (amount < 0 || amount > 1) throw new ArgumentException($"amount must be between 0 and 1: {Js.NumberToString(amount)}");
        if (srgb)
        {
            var x = ToRgb(first); var y = ToRgb(second);
            return Rgb(x.R + (y.R - x.R) * amount, x.G + (y.G - x.G) * amount, x.B + (y.B - x.B) * amount);
        }
        var a = ToOklch(first); var b = ToOklch(second);
        var firstHue = a.C < 1e-7 ? b.H : a.H;
        var secondHue = b.C < 1e-7 ? firstHue : b.H;
        var hueDelta = (secondHue - firstHue + 540) % 360 - 180;
        return Oklch(a.L + (b.L - a.L) * amount, a.C + (b.C - a.C) * amount, firstHue + hueDelta * amount);
    }
}

/// <summary>pi-tui oklab.ts: Oklab and OKHSL to/from sRGB.</summary>
internal static class Oklab
{
    internal readonly record struct Vector(double X, double Y, double Z);
    private static Vector Multiply(double[][] m, Vector v) =>
        new(m[0][0] * v.X + m[0][1] * v.Y + m[0][2] * v.Z, m[1][0] * v.X + m[1][1] * v.Y + m[1][2] * v.Z, m[2][0] * v.X + m[2][1] * v.Y + m[2][2] * v.Z);

    private static readonly double[][] LinearSrgbToLms =
    [
        [0.4122214694707629, 0.5363325372617349, 0.0514459932675022],
        [0.2119034958178251, 0.6806995506452344, 0.1073969535369405],
        [0.0883024591900564, 0.2817188391361215, 0.6299787016738222]
    ];
    private static readonly double[][] LmsToLab =
    [
        [0.210454268309314, 0.793617774702305, -0.0040720430116193],
        [1.9779985324311684, -2.42859224204858, 0.450593709617411],
        [0.0259040424655478, 0.7827717124575296, -0.8086757549230774]
    ];
    private static readonly double[][] LabToLms =
    [
        [1, 0.3963377773761749, 0.2158037573099136],
        [1, -0.1055613458156586, -0.0638541728258133],
        [1, -0.0894841775298119, -1.2914855480194092]
    ];
    private static readonly double[][] LmsToLinearSrgb =
    [
        [4.0767416360759583, -3.3077115392580629, 0.2309699031821043],
        [-1.2684379732850315, 2.6097573492876882, -0.341319376002657],
        [-0.0041960761386756, -0.7034186179359362, 1.7076146940746117]
    ];
    private static readonly (double X, double Y, double[] K)[] SaturationFit =
    [
        (-1.8817031, -0.80936501, [1.19086277, 1.76576728, 0.59662641, 0.75515197, 0.56771245]),
        (1.8144408, -1.19445267, [0.73956515, -0.45954404, 0.08285427, 0.12541073, -0.14503204]),
        (0.13110758, 1.81333971, [1.35733652, -0.00915799, -1.1513021, -0.50559606, 0.00692167])
    ];
    private const double K1 = 0.206, K2 = 0.03, K3 = (1 + K1) / (1 + K2);

    internal static double OklabToOkhslLightness(double x) => 0.5 * (K3 * x - K1 + Math.Sqrt(Math.Pow(K3 * x - K1, 2) + 4 * K2 * K3 * x));
    private static double OkhslToOklabLightness(double x) => (x * x + K1 * x) / (K3 * (x + K2));
    private static double LinearToSrgb(double value) => value > 0.0031308 ? 1.055 * Math.Pow(value, 1 / 2.4) - 0.055 : 12.92 * value;
    private static double SrgbToLinear(double value) => value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);

    internal static Vector OklabToLinearSrgb(Vector lab)
    {
        var lms = Multiply(LabToLms, lab);
        return Multiply(LmsToLinearSrgb, new(Math.Pow(lms.X, 3), Math.Pow(lms.Y, 3), Math.Pow(lms.Z, 3)));
    }

    private static Vector LinearSrgbToOklab(Vector rgb)
    {
        var lms = Multiply(LinearSrgbToLms, rgb);
        return Multiply(LmsToLab, new(Math.Cbrt(lms.X), Math.Cbrt(lms.Y), Math.Cbrt(lms.Z)));
    }

    internal static Vector RgbToOklab(RgbChannels rgb) =>
        LinearSrgbToOklab(new(SrgbToLinear(rgb.R / 255), SrgbToLinear(rgb.G / 255), SrgbToLinear(rgb.B / 255)));

    internal static RgbChannels LinearSrgbToRgb(Vector linear)
    {
        static double Channel(double value) => Js.Round(Math.Min(1, Math.Max(0, LinearToSrgb(value))) * 255);
        return new(Channel(linear.X), Channel(linear.Y), Channel(linear.Z));
    }

    private static double[] LmsSlopes(double a, double b) =>
        [LabToLms[0][1] * a + LabToLms[0][2] * b, LabToLms[1][1] * a + LabToLms[1][2] * b, LabToLms[2][1] * a + LabToLms[2][2] * b];

    private static double MaxSaturation(double a, double b)
    {
        var channel = 0;
        while (channel < 2 && !(SaturationFit[channel].X * a + SaturationFit[channel].Y * b > 1)) channel++;
        var k = SaturationFit[channel].K; var weights = LmsToLinearSrgb[channel];
        var saturation = k[0] + k[1] * a + k[2] * b + k[3] * a * a + k[4] * a * b;
        var slopes = LmsSlopes(a, b);
        var baseValues = slopes.Select(value => 1 + saturation * value).ToArray();
        double Dot(double[] values) { double sum = 0; for (var i = 0; i < values.Length; i++) sum += weights[i] * values[i]; return sum; }
        var f = Dot(baseValues.Select(value => Math.Pow(value, 3)).ToArray());
        var f1 = Dot(baseValues.Select((value, index) => 3 * slopes[index] * Math.Pow(value, 2)).ToArray());
        var f2 = Dot(baseValues.Select((value, index) => 6 * Math.Pow(slopes[index], 2) * value).ToArray());
        return saturation - f * f1 / (f1 * f1 - 0.5 * f * f2);
    }

    private static (double L, double C) Cusp(double a, double b)
    {
        var saturation = MaxSaturation(a, b);
        var linear = OklabToLinearSrgb(new(1, saturation * a, saturation * b));
        var lightness = Math.Cbrt(1 / Js.Max(linear.X, linear.Y, linear.Z));
        return (lightness, lightness * saturation);
    }

    private static double MaxChroma(double a, double b, double lightness, (double L, double C) cusp)
    {
        if (lightness <= cusp.L) return cusp.C * lightness / cusp.L;
        var t = cusp.C * (lightness - 1) / (cusp.L - 1);
        var slopes = LmsSlopes(a, b);
        var lms = slopes.Select(k => lightness + t * k).ToArray();
        var cubes = lms.Select(value => Math.Pow(value, 3)).ToArray();
        var first = lms.Select((value, index) => 3 * slopes[index] * Math.Pow(value, 2)).ToArray();
        var second = lms.Select((value, index) => 6 * Math.Pow(slopes[index], 2) * value).ToArray();
        static double Dot(double[] row, double[] values) => row[0] * values[0] + row[1] * values[1] + row[2] * values[2];
        var steps = LmsToLinearSrgb.Select(row =>
        {
            var f = Dot(row, cubes) - 1; var f1 = Dot(row, first); var f2 = Dot(row, second);
            var u = f1 / (f1 * f1 - 0.5 * f * f2);
            return u >= 0 ? -f * u : double.MaxValue;
        }).ToArray();
        return t + Js.Min(steps[0], steps[1], steps[2]);
    }

    private static (double C0, double CMid, double CMax) ChromaStops(double l, double a, double b)
    {
        var peak = Cusp(a, b);
        var cMax = MaxChroma(a, b, l, peak);
        var k = cMax / Math.Min(l * (peak.C / peak.L), (1 - l) * (peak.C / (1 - peak.L)));
        var midS = 0.11516993 + 1 / (7.4477897 + 4.1590124 * b + a * (-2.19557347 + 1.75198401 * b +
            a * (-2.13704948 - 10.02301043 * b + a * (-4.24894561 + 5.38770819 * b + 4.69891013 * a))));
        var midT = 0.11239642 + 1 / (1.6132032 - 0.68124379 * b + a * (0.40370612 + 0.90148123 * b +
            a * (-0.27087943 + 0.6122399 * b + a * (0.00299215 - 0.45399568 * b - 0.14661872 * a))));
        var cMid = 0.9 * k * Math.Sqrt(Math.Sqrt(1 / (1 / Math.Pow(l * midS, 4) + 1 / Math.Pow((1 - l) * midT, 4))));
        var c0 = Math.Sqrt(1 / (1 / Math.Pow(l * 0.4, 2) + 1 / Math.Pow((1 - l) * 0.8, 2)));
        return (c0, cMid, cMax);
    }

    internal static RgbChannels OkhslToRgb(double hue, double saturation, double lightness)
    {
        var l = OkhslToOklabLightness(lightness);
        var lab = new Vector(l, 0, 0);
        if (l > 0 && l < 1 && saturation > 0)
        {
            var angle = 2 * Math.PI * ((hue % 360 + 360) % 360) / 360;
            var a = Math.Cos(angle); var b = Math.Sin(angle);
            var (c0, cMid, cMax) = ChromaStops(l, a, b);
            double chroma;
            if (saturation < 0.8)
            {
                var t = 1.25 * saturation; var k1 = 0.8 * c0;
                chroma = t * k1 / (1 - (1 - k1 / cMid) * t);
            }
            else
            {
                var t = 5 * (saturation - 0.8); var k1 = 0.2 * Math.Pow(cMid, 2) * Math.Pow(1.25, 2) / c0;
                chroma = cMid + t * k1 / (1 - (1 - k1 / (cMax - cMid)) * t);
            }
            lab = new(l, chroma * a, chroma * b);
        }
        return LinearSrgbToRgb(OklabToLinearSrgb(lab));
    }

    internal static OkhslChannels RgbToOkhsl(RgbChannels rgb)
    {
        var lab = RgbToOklab(rgb);
        var chroma = Js.Hypot(lab.Y, lab.Z);
        var lightness = OklabToOkhslLightness(lab.X);
        if (chroma < 1e-9 || lightness <= 0 || lightness >= 1) return new(0, 0, lightness);
        var hue = (Math.Atan2(lab.Z, lab.Y) * 180 / Math.PI + 360) % 360;
        var (c0, cMid, cMax) = ChromaStops(lab.X, lab.Y / chroma, lab.Z / chroma);
        double saturation;
        if (chroma < cMid)
        {
            var k1 = 0.8 * c0;
            saturation = 0.8 * (chroma / (k1 + (1 - k1 / cMid) * chroma));
        }
        else
        {
            var k1 = 0.2 * Math.Pow(cMid, 2) * Math.Pow(1.25, 2) / c0;
            var offset = chroma - cMid;
            saturation = 0.8 + 0.2 * (offset / (k1 + (1 - k1 / (cMax - cMid)) * offset));
        }
        return new(hue, Math.Min(1, Math.Max(0, saturation)), lightness);
    }
}
