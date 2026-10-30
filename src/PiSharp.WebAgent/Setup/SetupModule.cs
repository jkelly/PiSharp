// Decision 0011 (run W3, SU): the Setup area's entry points, which Program.cs calls: the first-run wizard (GET /setup, SetupController and
// Views/Setup, the mockups' setup.html), its API (/api/setup, SetupEndpoints) and the first-run redirect (SetupRedirect). The area owns one
// settings section (setup: finished or skipped) and builds on the Settings area's ProviderDirectory and the credentials area's
// /api/credentials, so a provider connected in the wizard is the same account Settings › Providers shows.
using Microsoft.Extensions.DependencyInjection.Extensions;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Hosting;
using PiSharp.WebAgent.Settings;

namespace PiSharp.WebAgent.Setup;

/// <summary>Registration and endpoints of the Setup area.</summary>
public static class SetupModule
{
    /// <summary>The wizard's page.</summary>
    public const string SetupPath = "/setup";

    /// <summary>Registers <see cref="SetupState"/>. It resolves ProviderDirectory and the catalog when first used, so it sees the ones the
    /// other areas and the core's fallbacks registered.</summary>
    public static IServiceCollection AddSetupArea(this IServiceCollection services, WebAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.TryAddSingleton(provider => new SetupState(provider.GetRequiredService<ProviderDirectory>(), provider.GetRequiredService<ISettingsStore>(),
            provider.GetRequiredService<IModelCatalog>(), provider.GetRequiredService<WebAgentOptions>()));
        return services;
    }

    /// <summary>Adds the first-run redirect to the pipeline and maps <c>/api/setup</c>. Call it after <c>UseWebAgentCore</c>, so HostGuard
    /// runs first.</summary>
    public static WebApplication MapSetupArea(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseSetupRedirect();
        app.MapSetupEndpoints();
        return app;
    }
}
