// Decision 0011 (run W2): the routing area's entry points, which Program.cs calls. WU4 created this stub; WU6 fills it in. Without --faux the
// area registers the real catalog (RuntimeModelCatalog, PiSharp.ModelRuntime's built-in models) as IModelCatalog and WebDurableModels (every
// request through its chat's account, AccountRouter) as IDurableModels, in place of WU4's faux fallbacks. With --faux (options.Faux) it
// registers no provider code path at all: the core keeps the faux models and catalog, and nothing can reach a provider. In both modes it
// registers the chats' account seam (AccountChatAccounts over the credentials area's IAccountStore). Every registration is a TryAdd, so a
// test or another host can put its own first. The area has no endpoints and no webagent.db migrations.
//
// W3, CP: when the custom providers area registered its store (ICustomProviderSource), the catalog and the router read its routes copy of
// models.json (custom providers and model overrides); otherwise they are the built-in ones, as before. With --faux nothing changes: the
// faux catalog stays, and no custom provider is routed.
using Microsoft.Extensions.DependencyInjection.Extensions;
using PiSharp.Durable;
using PiSharp.WebAgent.Chat;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Credentials;
using PiSharp.WebAgent.CustomProviders;

namespace PiSharp.WebAgent.Routing;

/// <summary>Registration and endpoints of the routing area.</summary>
public static class RoutingModule
{
    /// <summary>Registers the area's services. Called before the core's fallbacks, so a registration here replaces a faux default. Needs
    /// the credentials area (<see cref="CredentialsModule.AddCredentialsArea"/>) for the accounts and their secrets.</summary>
    public static IServiceCollection AddRoutingArea(this IServiceCollection services, Hosting.WebAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.TryAddSingleton<IChatAccounts>(provider => new AccountChatAccounts(provider.GetRequiredService<IAccountStore>()));
        if (options.Faux) return services;
        services.TryAddSingleton(_ => new RoutingOptions());
        services.TryAddSingleton(provider => provider.GetService<ICustomProviderSource>() is { } custom
            ? new RuntimeModelCatalog(custom, provider.GetService<IAccountStore>())
            : new RuntimeModelCatalog());
        services.TryAddSingleton(provider => new AccountRouter(provider.GetRequiredService<RuntimeModelCatalog>(), provider.GetRequiredService<IAccountStore>(),
            provider.GetRequiredService<ICredentialVault>(), provider.GetRequiredService<CredentialsOptions>(), provider.GetRequiredService<RoutingOptions>(),
            provider.GetService<ILogger<AccountRouter>>(), provider.GetService<ICustomProviderSource>()));
        services.TryAddSingleton(provider => new WebDurableModels(provider.GetRequiredService<RuntimeModelCatalog>(), provider.GetRequiredService<AccountRouter>()));
        // Tried like every area registration (a test or host may put its own first); the core's faux fallbacks come after, so these win.
        services.TryAddSingleton<IModelCatalog>(provider => provider.GetRequiredService<RuntimeModelCatalog>());
        services.TryAddSingleton<IDurableModels>(provider => provider.GetRequiredService<WebDurableModels>());
        return services;
    }

    /// <summary>Maps the area's endpoints (none; MVC controllers are mapped by the core).</summary>
    public static WebApplication MapRoutingArea(this WebApplication app) => app;
}
