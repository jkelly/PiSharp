// Decision 0011 (run W2, WU3): the hosts, ports and paths HostGuard enforces, set by each app instead of being fixed in the guard. The hub
// adds its Hub:AllowedHosts and Hub:AllowedPorts; the Web Agent keeps the defaults (loopback names on any port its own binding accepts).
// Both send DefaultContentSecurityPolicy: every script and style is a file of the app or of PiSharp.Web.Common (no inline script, so the
// theme is set by theme-boot.js), style attributes stay allowed for the views' few layout tweaks, and images may be data: URLs (Bootstrap's
// form icons, images in a transcript).
namespace PiSharp.Web.Common;

/// <summary>What <see cref="HostGuard"/> allows. Set with <see cref="WebCommonExtensions.AddHostGuard"/>.</summary>
public sealed class HostGuardOptions
{
    /// <summary>The host names every guard answers: <c>localhost</c>, <c>127.0.0.1</c> and <c>::1</c> (sent as <c>[::1]</c>).</summary>
    public static IReadOnlyList<string> LoopbackHosts { get; } = ["localhost", "127.0.0.1", "::1"];

    /// <summary>Host names answered besides <see cref="LoopbackHosts"/>, compared without case (for example a name the user maps to
    /// 127.0.0.1). Empty by default.</summary>
    public List<string> AllowedHosts { get; set; } = [];

    /// <summary>The ports a Host header may name. Empty (the default) allows any port, since the server binds only the ports it means to;
    /// otherwise a Host without a port stands for its scheme's default port (80 or 443).</summary>
    public List<int> AllowedPorts { get; set; } = [];

    /// <summary>Path prefixes whose writes (any method but GET, HEAD and OPTIONS) with a body must be JSON; default <c>/api</c>.</summary>
    public List<string> JsonPaths { get; set; } = ["/api"];

    /// <summary>The body of a 421 answer.</summary>
    public string MisdirectedMessage { get; set; } = "Misdirected request: this server answers only localhost.";

    /// <summary>The Content-Security-Policy header sent with every answer the guard lets through, or null (the default) for none.</summary>
    public string? ContentSecurityPolicy { get; set; }

    /// <summary>The policy both apps send: scripts, styles, fonts and connections from the app's own origin only, inline style attributes,
    /// images from the origin or data: URLs, no plugins, no framing, and forms that post only to the app.</summary>
    public const string DefaultContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
        "font-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
}
