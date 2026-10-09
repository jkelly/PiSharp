// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/auth-url.ts.
// The copy action defaults to utils/clipboard.ts copyToClipboard (Utilities/Clipboard.cs); hosts may pass their own.
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>
/// A sign-in URL with a click hint and a copy hint. Hosts call <see cref="Copy"/> when <c>app.message.copy</c> is
/// pressed, since a long URL wraps and often cannot be selected or clicked as a whole (SSH, tmux).
/// </summary>
internal sealed class AuthUrlComponent : Container
{
    /// <summary>utils/clipboard.ts copyToClipboard, used when no delegate is passed to the constructor.</summary>
    public static Func<string, Task>? DefaultCopyToClipboard { get; set; } = text => Utilities.Clipboard.CopyToClipboard(text);

    public string Url { get; }
    private readonly ITui tui;
    private readonly Text hint;
    private readonly Func<string, Task>? copyToClipboard;

    public AuthUrlComponent(ITui tui, string url, Func<string, Task>? copyToClipboard = null)
    {
        this.tui = tui;
        Url = url;
        this.copyToClipboard = copyToClipboard;
        AddChild(new Text(theme.Fg("accent", TerminalImage.Hyperlink(url, url)), 1, 0));
        hint = new Text("", 1, 0);
        AddChild(hint);
        SetHint(KeybindingHints.KeyHint("app.message.copy", "to copy"));
    }

    private void SetHint(string suffix)
    {
        var clickHint = OperatingSystem.IsMacOS() ? "Cmd+click to open" : "Ctrl+click to open";
        hint.SetText($"{theme.Fg("dim", TerminalImage.Hyperlink(clickHint, Url))} {theme.Fg("dim", "•")} {suffix}");
        tui.RequestRender();
    }

    public async Task Copy()
    {
        try
        {
            var copy = copyToClipboard ?? DefaultCopyToClipboard ?? throw new InvalidOperationException("Clipboard is not available");
            await copy(Url);
            SetHint(theme.Fg("success", "Copied URL to clipboard"));
        }
        catch (Exception error)
        {
            SetHint(theme.Fg("error", error.Message));
        }
    }
}
