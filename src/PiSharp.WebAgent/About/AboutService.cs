// Decision 0011 (run W3, unit AP): Settings › About's model and its diagnostics report, read fresh on every request from the running app:
// AboutFacts (versions, host), CatalogFacts (IModelCatalog and the built-in catalog), the credentials area's account list (metadata only:
// IAccountStore never hands out a secret), the settings store's Defaults and Tools sections, and the connectivity check's targets and last
// run. Nothing here sends a request or reads a credential.
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Credentials;
using PiSharp.WebAgent.Hosting;

namespace PiSharp.WebAgent.About;

/// <summary>What the connectivity check shows: its targets, its last run, and whether the app is offline.</summary>
/// <param name="Targets">What a check would test now.</param>
/// <param name="Last">The last check since the server started, or null.</param>
/// <param name="Offline">Whether the app is offline (<c>--faux</c>).</param>
/// <param name="Zone">The time zone times are shown in (this computer's).</param>
public sealed record AboutConnectivityView(IReadOnlyList<ConnectivityTarget> Targets, ConnectivityRun? Last, bool Offline, TimeZoneInfo Zone)
{
    /// <summary><paramref name="time"/> in <see cref="Zone"/>.</summary>
    public DateTimeOffset Local(DateTimeOffset time) => TimeZoneInfo.ConvertTime(time, Zone);
}

/// <summary>The model of Settings › About.</summary>
/// <param name="Versions">The versions of this build.</param>
/// <param name="Host">The host facts.</param>
/// <param name="Catalog">The catalog numbers.</param>
/// <param name="Accounts">The accounts (metadata only).</param>
/// <param name="Defaults">The Defaults section.</param>
/// <param name="Tools">The Tools section.</param>
/// <param name="Connectivity">The connectivity check.</param>
/// <param name="Components">The third-party components.</param>
/// <param name="Options">The app's options (folders and the faux switch).</param>
/// <param name="GeneratedAt">When this was read.</param>
public sealed record AboutPage(VersionFacts Versions, HostFacts Host, CatalogStats Catalog, IReadOnlyList<AccountInfo> Accounts,
    DefaultsSettings Defaults, ToolsSettings Tools, AboutConnectivityView Connectivity, IReadOnlyList<ThirdPartyComponent> Components,
    WebAgentOptions Options, DateTimeOffset GeneratedAt)
{
    /// <summary>The repository.</summary>
    public const string RepositoryUrl = "https://github.com/jkelly/PiSharp";

    /// <summary>The documentation.</summary>
    public const string DocsUrl = "https://pisharp.ai/docs/";

    /// <summary>PiSharp's license (MIT).</summary>
    public const string LicenseUrl = RepositoryUrl + "/blob/main/LICENSE";

    /// <summary>PiSharp's third-party notices.</summary>
    public const string NoticesUrl = RepositoryUrl + "/blob/main/THIRD-PARTY-NOTICES.md";

    /// <summary>The diagnostics report of this page.</summary>
    public string Report => DiagnosticsReport.Write(this);
}

/// <summary>Reads <see cref="AboutPage"/>. A singleton.</summary>
public sealed class AboutService(AboutFacts facts, IModelCatalog catalog, IAccountStore accounts, ISettingsStore settings, ConnectivityCheck connectivity,
    WebAgentOptions options, TimeProvider? time = null)
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;

    /// <summary>The connectivity check's view now.</summary>
    public AboutConnectivityView Connectivity() => new(connectivity.Targets(), connectivity.Last, connectivity.Offline, clock.LocalTimeZone);

    /// <summary>The page now.</summary>
    public AboutPage Page() => new(facts.Versions, facts.Host(), CatalogFacts.Read(catalog, settings.Get<ModelsSettings>(), options), accounts.List(),
        settings.Get<DefaultsSettings>(), settings.Get<ToolsSettings>(), Connectivity(), ThirdPartyComponents.All, options,
        TimeZoneInfo.ConvertTime(clock.GetUtcNow(), clock.LocalTimeZone));

    /// <summary>The diagnostics report now.</summary>
    public string Report() => Page().Report;
}
