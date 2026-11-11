// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): provider-brand.ts and preferred-providers.ts.
using System.Globalization;
using System.Text;
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Interactive.Mode.Components;

internal static class PreferredProviders
{
    internal static int Rank(string providerId) => providerId switch { "radius" => 0, "openrouter" => 1, _ => 2 };
    internal static bool IsPreferred(string providerId) => Rank(providerId) < 2;
    internal static IOrderedEnumerable<T> Sort<T>(IEnumerable<T> providers, Func<T, string> id, Func<T, string> name) =>
        providers.OrderBy(provider => Rank(id(provider))).ThenBy(name, StringComparer.Create(CultureInfo.InvariantCulture, false));
}

internal static class ProviderBrand
{
    /// <summary>The four colors of the Radius logo, in the order they stream across the text.</summary>
    private static readonly Color[] RadiusColors = [.. new[] { "#4d9abf", "#83ccd2", "#f1be57", "#f09082" }.Select(Colors.ParseColor)];
    /// <summary>Width of each color band, in characters.</summary>
    private const int CharsPerColor = 4;
    private const int CharsPerSecond = 10;
    private const int AnimationFrameMs = 50;


    internal static Func<string, double, string>? GetStyle(string providerId) => providerId switch
    {
        "radius" => RadiusShimmer,
        "openrouter" => OpenRouterGlow,
        _ => null
    };

    internal static IDisposable StartAnimation(ITui tui, Func<bool> isAnimating) =>
        tui.Loop.SetInterval(() => { if (isAnimating()) tui.RequestRender(); }, AnimationFrameMs);

    internal static string RadiusShimmer(string text, double elapsedMs)
    {
        var mode = theme.GetColorMode();
        var cycle = RadiusColors.Length * CharsPerColor;
        var offset = elapsedMs / 1000 * CharsPerSecond;
        var result = new StringBuilder();
        var index = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var position = ((index - offset) % cycle + cycle) % cycle;
            var band = (int)Math.Floor(position / CharsPerColor);
            var t = position / CharsPerColor - band;
            // Smoothstep keeps each band recognizable while still blending into the next one.
            var amount = t * t * (3 - 2 * t);
            var from = RadiusColors[band];
            var to = RadiusColors[(band + 1) % RadiusColors.Length];
            result.Append(Colors.ForegroundAnsi(Colors.Mix(from, to, amount, srgb: true), mode)).Append(rune.ToString());
            index++;
        }
        return result + "\u001b[39m";
    }

    internal static string OpenRouterGlow(string text, double elapsedMs)
    {
        var dark = theme.Appearance == "dark";
        var baseColor = Colors.ParseColor(dark ? "#c6f243" : "#5d32e1");
        var peak = Colors.ParseColor(dark ? "#efffb0" : "#8f6bff");
        var amount = (1 - Math.Cos(2 * Math.PI * elapsedMs / 2400)) / 2;
        return Colors.ForegroundAnsi(Colors.Mix(baseColor, peak, amount), theme.GetColorMode()) + text + "\u001b[39m";
    }
}
