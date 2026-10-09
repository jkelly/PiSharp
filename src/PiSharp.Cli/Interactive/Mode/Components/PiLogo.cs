// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/pi-logo.ts.
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;
using TuiColor = PiSharp.Tui.Pi.Color;

namespace PiSharp.Cli.Interactive.Mode.Components;

internal static class PiLogo
{
    private static readonly TuiColor Coral = TuiColor.Rgb(228, 138, 122);
    private static readonly TuiColor Blue = TuiColor.Rgb(79, 142, 179);
    private static readonly TuiColor Yellow = TuiColor.Rgb(234, 182, 93);
    private const string Reset = "\u001b[0m";

    /// <summary>Environment reads for <see cref="SupportsPiLogo"/> (tests override).</summary>
    public static Func<string, string?> Environment { get; set; } = System.Environment.GetEnvironmentVariable;

    /// <summary>
    /// The pi logo: 4 cells wide and 2 lines tall. Each cell shows two square pixels with half blocks:
    /// <code>
    ///   coral coral coral .
    ///   blue  .     coral .
    ///   blue  blue  .     yellow
    ///   blue  .     .     yellow
    /// </code>
    /// The brand colors stay fixed across themes; they follow the terminal's color mode.
    /// </summary>
    public static string[] PiLogoLines()
    {
        var mode = theme.GetColorMode();
        string Fg(TuiColor color) => Colors.ForegroundAnsi(color, mode);
        // The fourth cell of the top line is empty, so it is padded to the same width as the bottom line.
        var top = $"{Fg(Coral)}{Colors.BackgroundAnsi(Blue, mode)}▀{Reset}{Fg(Coral)}▀█{Reset} ";
        var bottom = $"{Fg(Blue)}█▀{Reset} {Fg(Yellow)}█{Reset}";
        return [top, bottom];
    }

    /// <summary>
    /// Whether the terminal renders the half-block logo correctly. Apple Terminal draws gaps between rows and
    /// misaligns the half blocks, so it gets the text wordmark instead.
    /// </summary>
    public static bool SupportsPiLogo() => !IsAppleTerminalSession();

    /// <summary>Text fallback for the logo: "Pi" with the logo's coral and yellow.</summary>
    public static string PiWordmark()
    {
        var mode = theme.GetColorMode();
        return $"{Colors.ForegroundAnsi(Coral, mode)}P{Reset}{Colors.ForegroundAnsi(Yellow, mode)}i{Reset}";
    }

    // tui/src/terminal.ts isAppleTerminalSession (not exported by PiSharp.Tui).
    private static bool IsAppleTerminalSession() => OperatingSystem.IsMacOS() && Environment("TERM_PROGRAM") == "Apple_Terminal";
}
