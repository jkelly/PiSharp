// Decision 0011 (run W2, WU9): the Settings area's entry points, which Program.cs calls. The area registers ProviderDirectory (the providers
// the settings pages list, over IModelCatalog, IAccountStore and ISignInFlows) and maps /api/settings (SettingsEndpoints); its pages are
// SettingsController's (Views/Settings). The Defaults, Tools and Models sections themselves are the core's ISettingsStore (WU4); keys and
// sign-ins are the credentials area's /api/credentials (WU5).
using Microsoft.Extensions.DependencyInjection.Extensions;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Credentials;

namespace PiSharp.WebAgent.Settings;

/// <summary>Registration and endpoints of the Settings area.</summary>
public static class SettingsModule
{
    /// <summary>Registers the area's services. Called before the core's fallbacks; ProviderDirectory resolves the catalog when first used,
    /// so it sees the catalog the fallbacks (or the Routing area) registered.</summary>
    public static IServiceCollection AddSettingsArea(this IServiceCollection services, Hosting.WebAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.TryAddSingleton(provider => new ProviderDirectory(provider.GetRequiredService<IModelCatalog>(), provider.GetRequiredService<IAccountStore>(),
            provider.GetRequiredService<ISignInFlows>(), provider.GetService<CredentialsOptions>()));
        return services;
    }

    /// <summary>Maps <c>/api/settings</c>.</summary>
    public static WebApplication MapSettingsArea(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapSettingsEndpoints();
        return app;
    }
}
