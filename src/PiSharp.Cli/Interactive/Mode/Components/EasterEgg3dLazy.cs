// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/easter-egg-3d.lazy.ts.
// The dynamic import() becomes a post to the UI loop, so the animation starts on a later turn of the loop as upstream's does.
using PiSharp.Tui.Pi;

namespace PiSharp.Cli.Interactive.Mode.Components;

internal static class EasterEgg3dLazy
{
    /// <summary>
    /// Plays a 3D easter egg (see EasterEgg3d), which loads on first use. Only fullscreen mode can show it, because it dissolves the
    /// rendered screen. The screen is captured before loading. Returns false when it cannot play.
    /// </summary>
    private static bool PlayEasterEgg3d(ITui tui, EasterEgg3d.Egg egg)
    {
        if (tui is not TuiAltScreen altScreen) return false;
        if (altScreen.HasOverlay()) return true;
        var screen = altScreen.GetScreenLines();
        altScreen.Loop.Post(() => _ = EasterEgg3d.PlayEasterEgg3d(altScreen, screen, egg));
        return true;
    }

    /// <summary>Plays the 3D pi logo, lifting off the header logo whose top-left cell is at <paramref name="column"/>, <paramref name="row"/>.</summary>
    public static void PlayPiLogo3d(ITui tui, int column, int row) => PlayEasterEgg3d(tui, EasterEgg3d.Egg.PiLogo(column, row));

    /// <summary>Plays the 3D Armin. Returns false when it cannot play, so the caller can fall back to the inline version.</summary>
    public static bool PlayArmin3d(ITui tui) => PlayEasterEgg3d(tui, EasterEgg3d.Egg.Armin());
}
