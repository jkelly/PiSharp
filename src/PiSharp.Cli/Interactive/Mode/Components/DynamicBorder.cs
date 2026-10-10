// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/dynamic-border.ts.
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>
/// Dynamic border component that adjusts to viewport width.
/// Note: extensions should pass an explicit color function; the default reads the global theme.
/// </summary>
internal sealed class DynamicBorder(Func<string, string>? color = null) : IComponent
{
    private readonly Func<string, string> color = color ?? (str => theme.Fg("border", str));

    public void Invalidate()
    {
        // No cached state to invalidate currently
    }

    public List<string> Render(int width) => [color(TextUtils.Repeat("─", Math.Max(1, width)))];
}
