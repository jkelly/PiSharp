// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/themed-text.ts.
using PiSharp.Tui.Pi;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>
/// Text whose content applies theme colors. Plain <see cref="Text"/> keeps the colors its string was built with, so
/// a theme change, or the system theme receiving the terminal's colors, would leave it stale. This
/// rebuilds the string from <c>build</c> after every invalidation, which the UI performs on theme changes.
///
/// <c>build</c> must return the same content each time, apart from colors. Snapshot changing data before
/// creating the component, or call <see cref="Invalidate"/> after changing state that <c>build</c> reads.
/// </summary>
internal sealed class ThemedText(Func<string> build, int paddingX = 1, int paddingY = 1) : Text("", paddingX, paddingY)
{
    private readonly Func<string> build = build;
    private bool stale = true;

    public override void Invalidate()
    {
        base.Invalidate();
        stale = true;
    }

    public override List<string> Render(int width)
    {
        if (stale)
        {
            stale = false;
            SetText(build());
        }
        return base.Render(width);
    }
}
