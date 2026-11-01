// Decision 0011 (run W2, WU5): the credentials and sign-in area's entry points, which Program.cs calls. The area registers migration 100
// (accounts), CredentialsOptions (offline with --faux), the CredentialVault as IAccountStore and ICredentialVault, the sign-in flows and
// the connection tester, and maps /api/credentials. Each registration is a TryAdd, so a test or another host can put its own first.
using Microsoft.Extensions.DependencyInjection.Extensions;
using PiSharp.WebAgent.Data;

namespace PiSharp.WebAgent.Credentials;

/// <summary>Registration and endpoints of the credentials and sign-in area.</summary>
public static class CredentialsModule
{
    /// <summary>Registers the area's services (and its webagent.db migrations). Called before the core's fallbacks, so a registration here
    /// replaces a faux default.</summary>
    public static IServiceCollection AddCredentialsArea(this IServiceCollection services, Hosting.WebAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.AddWebAgentMigration(CredentialVault.Migration);
        services.TryAddSingleton(_ => new CredentialsOptions { Offline = options.Faux });
        services.TryAddSingleton<CredentialVault>();
        services.TryAddSingleton<IAccountStore>(provider => provider.GetRequiredService<CredentialVault>());
        services.TryAddSingleton<ICredentialVault>(provider => provider.GetRequiredService<CredentialVault>());
        services.TryAddSingleton(provider => new SignInFlows(provider.GetRequiredService<CredentialVault>(), provider.GetRequiredService<CredentialsOptions>(),
            provider, provider.GetService<ILogger<SignInFlows>>()));
        services.TryAddSingleton<ISignInFlows>(provider => provider.GetRequiredService<SignInFlows>());
        services.TryAddSingleton<IConnectionTester, ConnectionTester>();
        return services;
    }

    /// <summary>Prepares the vault (removes secrets a crash left without an account, reads the environment accounts) and maps
    /// <c>/api/credentials</c>. Call it after the core prepared webagent.db.</summary>
    public static WebApplication MapCredentialsArea(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.Services.GetService<CredentialVault>() is { } vault) _ = vault.List();
        app.MapCredentialsEndpoints();
        return app;
    }
}
