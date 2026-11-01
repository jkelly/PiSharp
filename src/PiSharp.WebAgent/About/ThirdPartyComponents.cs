// Decision 0011 (run W3, unit AP): the open-source components the Web Agent ships, for Settings › About's "Third-party notices". The .NET
// libraries' versions are read from their loaded assemblies (informational version, without the commit); the web assets' versions are those
// of the files PiSharp.Web.Common bundles (each file's own header or README says it; AboutCases checks them). PiSharp's own notices,
// including Pi's MIT notice, are THIRD-PARTY-NOTICES.md in the repository.
using System.Reflection;

namespace PiSharp.WebAgent.About;

/// <summary>An open-source component the Web Agent ships.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Version">Its version, or null when it could not be read.</param>
/// <param name="License">Its license (an SPDX expression).</param>
/// <param name="Url">Where it comes from.</param>
/// <param name="Role">What the Web Agent uses it for.</param>
public sealed record ThirdPartyComponent(string Name, string? Version, string License, string Url, string Role);

/// <summary>The components of <see cref="ThirdPartyComponent"/>.</summary>
public static class ThirdPartyComponents
{
    /// <summary>The version of Bootstrap in <c>_content/PiSharp.Web.Common/lib/bootstrap</c>.</summary>
    public const string BootstrapVersion = "5.3.3";

    /// <summary>The version of Bootstrap Icons in <c>_content/PiSharp.Web.Common/lib/bootstrap-icons</c>.</summary>
    public const string BootstrapIconsVersion = "1.11.3";

    /// <summary>The version of the Lobe Icons provider logos in <c>_content/PiSharp.Web.Common/img/providers</c> (its README).</summary>
    public const string LobeIconsVersion = "1.95.1";

    private static readonly Lazy<IReadOnlyList<ThirdPartyComponent>> Cached = new(Read);

    /// <summary>Every component, Pi first.</summary>
    public static IReadOnlyList<ThirdPartyComponent> All => Cached.Value;

    private static IReadOnlyList<ThirdPartyComponent> Read() =>
    [
        new("Pi (pi-mono) · Mario Zechner", null, "MIT", "https://github.com/earendil-works/pi", "The agent PiSharp ports"),
        new("Bootstrap", BootstrapVersion, "MIT", "https://getbootstrap.com/", "Layout and components"),
        new("Bootstrap Icons", BootstrapIconsVersion, "MIT", "https://icons.getbootstrap.com/", "Icons"),
        new("Lobe Icons", LobeIconsVersion, "MIT", "https://github.com/lobehub/lobe-icons", "Provider logos"),
        new("Markdig", Version("Markdig"), "BSD-2-Clause", "https://github.com/xoofx/markdig", "Markdown in replies"),
        new("HtmlSanitizer", Version("HtmlSanitizer"), "MIT", "https://github.com/mganss/HtmlSanitizer", "Cleans rendered Markdown"),
        new("AngleSharp", Version("AngleSharp"), "MIT", "https://github.com/AngleSharp/AngleSharp", "HTML parser of HtmlSanitizer"),
        new("Microsoft.Data.Sqlite", Version("Microsoft.Data.Sqlite"), "MIT", "https://github.com/dotnet/efcore", "webagent.db and chat storage"),
        new("SQLitePCLRaw", Version("SQLitePCLRaw.core"), "Apache-2.0", "https://github.com/ericsink/SQLitePCL.raw", "SQLite bindings"),
        new("SQLite", null, "blessing", "https://sqlite.org/copyright.html", "The database engine (public domain)"),
    ];

    /// <summary>The informational version of the assembly <paramref name="name"/> (without a <c>+commit</c>), or null when it is not
    /// found.</summary>
    public static string? Version(string name)
    {
        try
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(loaded => loaded.GetName().Name == name) ?? Assembly.Load(new AssemblyName(name));
            return AboutFacts.VersionOf(assembly).Version;
        }
        catch (Exception error) when (error is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return null;
        }
    }
}
