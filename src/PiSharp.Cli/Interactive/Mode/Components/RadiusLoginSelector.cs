// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/radius-login-selector.ts.
// The `/login` menu with the animated "Sign in with Radius" option. Internal to the interactive mode: the shimmer
// is Radius-only and is not exposed to other selectors. Upstream subclasses ExtensionSelectorComponent; here
// RadiusLoginMenuComponent wraps one (its Dispose is not virtual, so a subclass could not stop the animation timer when
// disposed through the base type) and post-processes its rendered lines the same way.
using System.Diagnostics;
using System.Text;
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>The "Sign in with Radius" option: <c>Label</c> is the full option, starting with the animated <c>Text</c>.</summary>
internal sealed record RadiusOption(string Label, string Text);

/// <summary>
/// Swaps the line <see cref="ExtensionSelectorComponent"/> draws for the selected Radius option with the animated one. When the
/// selector's row style changes or the label wraps, the line no longer matches and the option renders normally.
/// </summary>
internal sealed class RadiusLoginMenuComponent : IComponent, IInputHandler, IMouseHandler, IDisposableComponent, IDisposable
{
    /// <summary>The four colors of the Radius logo, in the order they stream across the text.</summary>
    private static readonly Color[] RadiusColors = [.. new[] { "#4d9abf", "#83ccd2", "#f1be57", "#f09082" }.Select(Colors.ParseColor)];
    /// <summary>Width of each color band, in characters.</summary>
    private const int CharsPerColor = 4;
    private const int CharsPerSecond = 10;
    private const int AnimationFrameMs = 50;

    private readonly ExtensionSelectorComponent selector;
    private readonly RadiusOption radiusOption;
    private readonly Func<double> now;
    private readonly double animationStart;
    private IDisposable? animationTimer;
    private bool animating;

    /// <param name="now">performance.now() in milliseconds (tests); defaults to a monotonic clock.</param>
    public RadiusLoginMenuComponent(ITui tui, string title, IReadOnlyList<string> options, RadiusOption radiusOption, Action<string> onSelect,
        Action onCancel, Func<double>? now = null)
    {
        this.now = now ?? (() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
        animationStart = this.now();
        selector = new ExtensionSelectorComponent(title, options, option =>
        {
            StopAnimation();
            onSelect(option);
        }, () =>
        {
            StopAnimation();
            onCancel();
        });
        this.radiusOption = radiusOption;
        animationTimer = tui.Loop.SetInterval(() =>
        {
            if (animating) tui.RequestRender();
        }, AnimationFrameMs);
    }

    /// <summary>Whether the last render found and animated the selected Radius line.</summary>
    internal bool Animating => animating;

    /// <summary>Color <paramref name="text"/> with the Radius logo colors flowing left to right; <paramref name="elapsedMs"/> is the animation time.</summary>
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

    public List<string> Render(int width)
    {
        var lines = selector.Render(width);
        var (label, text) = radiusOption;
        var selectedRendered = new Text(theme.Fg("accent", "→ ") + theme.Fg("accent", label), 1, 0).Render(width);
        var index = selectedRendered.Count == 0 ? -1 : lines.IndexOf(selectedRendered[0]);
        animating = index >= 0;
        if (animating)
        {
            var shimmer = RadiusShimmer(text, now() - animationStart);
            var animatedLine = theme.Fg("accent", "→ ") + shimmer + label[Math.Min(text.Length, label.Length)..];
            var rendered = new Text(animatedLine, 1, 0).Render(width);
            lines[index] = rendered.Count > 0 ? rendered[0] : "";
        }
        return lines;
    }

    public void Invalidate() => selector.Invalidate();
    public void HandleInput(string data) => selector.HandleInput(data);
    public TuiMouseEventResult? HandleMouse(TuiMouseEvent mouseEvent) => selector.HandleMouse(mouseEvent);

    private void StopAnimation()
    {
        animationTimer?.Dispose();
        animationTimer = null;
        animating = false;
    }

    public void Dispose()
    {
        StopAnimation();
        selector.Dispose();
    }
}

internal static class RadiusLoginSelector
{
    /// <summary>Top-level <c>/login</c> selector whose Radius option shimmers in the Radius logo colors while it is selected.</summary>
    public static RadiusLoginMenuComponent CreateLoginMenuSelector(ITui tui, string title, IReadOnlyList<string> options, RadiusOption radiusOption,
        Action<string> onSelect, Action onCancel) =>
        new(tui, title, options, radiusOption, onSelect, onCancel);
}
