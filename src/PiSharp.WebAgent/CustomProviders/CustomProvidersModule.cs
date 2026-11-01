// Decision 0011 (run W3, CP): the custom providers area's entry points, which Program.cs calls. The area registers the store of the data
// folder's models.json (CustomProviderStore, also as ICustomProviderSource, which the routing area's catalog and router read when it is
// registered), the area's operations and its connection tester, and maps /api/custom-providers. Its page is CustomProvidersController's
// (Settings › Custom providers). In every mode: with --faux the page still edits the list, but the routing area registers no catalog or
// router of its own, so no custom provider is routed, and tests are offline. Each registration is a TryAdd, so a test or another host can
// put its own first. The area has no webagent.db migrations.
using Microsoft.Extensions.DependencyInjection.Extensions;
using PiSharp.WebAgent.Credentials;

namespace PiSharp.WebAgent.CustomProviders;

/// <summary>Registration and endpoints of the custom providers area.</summary>
public static class CustomProvidersModule
{
    /// <summary>Registers the area's services. Needs the credentials area (<see cref="CredentialsModule.AddCredentialsArea"/>) for the vault
    /// that holds stored keys.</summary>
    public static IServiceCollection AddCustomProvidersArea(this IServiceCollection services, Hosting.WebAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.TryAddSingleton(provider => new CustomProviderStore(provider.GetRequiredService<Hosting.WebAgentOptions>(), provider.GetService<ILogger<CustomProviderStore>>()));
        services.TryAddSingleton<ICustomProviderSource>(provider => provider.GetRequiredService<CustomProviderStore>());
        services.TryAddSingleton(provider => new CustomProviderService(provider.GetRequiredService<CustomProviderStore>(), provider.GetRequiredService<IAccountStore>(),
            provider.GetRequiredService<ICredentialVault>(), provider.GetRequiredService<CredentialsOptions>(), provider.GetService<ILogger<CustomProviderService>>()));
        services.TryAddSingleton(provider => new CustomProviderTester(provider.GetRequiredService<CustomProviderService>(), provider.GetRequiredService<ICredentialVault>(),
            provider.GetRequiredService<CredentialsOptions>()));
        return services;
    }

    /// <summary>Reads models.json once (so its routes copy is current before the first request) and maps <c>/api/custom-providers</c>.</summary>
    public static WebApplication MapCustomProvidersArea(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        _ = app.Services.GetService<CustomProviderStore>()?.Refresh();
        app.MapCustomProviderEndpoints();
        return app;
    }
}
