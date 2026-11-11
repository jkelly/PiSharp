// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): coding-agent/src/modes/interactive/components/radius-login-selector.ts.
// The `/login` menu with the animated "Sign in with Radius" option. Internal to the interactive mode: the shimmer
// is shared with the provider selector through ProviderBrand. Upstream subclasses ExtensionSelectorComponent; here
// RadiusLoginMenuComponent wraps one (its Dispose is not virtual, so a subclass could not stop the animation timer when
// disposed through the base type) and post-processes its rendered lines the same way.
using System.Diagnostics;
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
        animationTimer = ProviderBrand.StartAnimation(tui, () => animating);
    }

    /// <summary>Whether the last render found and animated the selected Radius line.</summary>
    internal bool Animating => animating;

    /// <summary>Color <paramref name="text"/> with the Radius logo colors flowing left to right; <paramref name="elapsedMs"/> is the animation time.</summary>
    internal static string RadiusShimmer(string text, double elapsedMs) => ProviderBrand.RadiusShimmer(text, elapsedMs);

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
