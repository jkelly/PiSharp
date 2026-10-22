// Decision 0011 (run W2, WU3): the URLs of the static files this Razor class library serves. ASP.NET Core serves a referenced class library's
// wwwroot under _content/<assembly name>/ (from the static web assets manifest in a build output, from wwwroot/_content in a publish
// output), so both apps link the same files and neither keeps a copy.
namespace PiSharp.Web.Common;

/// <summary>Root-relative URLs of PiSharp.Web.Common's static files.</summary>
public static class WebCommonAssets
{
    /// <summary>The folder every file below is served from.</summary>
    public const string Root = "/_content/PiSharp.Web.Common";

    /// <summary>The design system's tokens: Bootstrap 5.3 variables for dark (the default) and light.</summary>
    public const string ThemeCss = Root + "/css/pisharp-theme.css";

    /// <summary>The parts of the mockups' app.css both apps use: page frame, transcript, tool cards, composer, empty states, setting rows,
    /// choice cards and provider logos.</summary>
    public const string ShellCss = Root + "/css/shell.css";

    /// <summary>Bootstrap 5.3.3's stylesheet.</summary>
    public const string BootstrapCss = Root + "/lib/bootstrap/css/bootstrap.min.css";

    /// <summary>Bootstrap 5.3.3's script bundle (with Popper).</summary>
    public const string BootstrapJs = Root + "/lib/bootstrap/js/bootstrap.bundle.min.js";

    /// <summary>Bootstrap Icons 1.11.3.</summary>
    public const string BootstrapIconsCss = Root + "/lib/bootstrap-icons/bootstrap-icons.min.css";

    /// <summary>The module with <c>MuxStream</c>, a tab's one Server-Sent Events stream of <c>{sub, frame}</c> messages.</summary>
    public const string StreamJs = Root + "/js/stream.js";

    /// <summary>The module with <c>applyItems</c>, <c>swapHtml</c>, <c>keepPinned</c> and <c>element</c>.</summary>
    public const string SwapJs = Root + "/js/swap.js";

    /// <summary>The module with <c>initTheme</c>, <c>applyTheme</c> and <c>currentTheme</c> (the dark/light toggle).</summary>
    public const string ThemeJs = Root + "/js/theme.js";

    /// <summary>A classic script for the head that applies the stored theme before the first paint (its <c>data-theme-key</c> names the
    /// storage key).</summary>
    public const string ThemeBootJs = Root + "/js/theme-boot.js";

    /// <summary>The PiSharp mark (24×20).</summary>
    public const string Mark = Root + "/img/mark.svg";

    /// <summary>The favicon.</summary>
    public const string Favicon = Root + "/img/favicon.svg";
}
