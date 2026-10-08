// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/modes/interactive/session-share.ts (tryShareViaRadius:
// modelRuntime.getProvider("radius") and getAuth("radius", { minOAuthValidityMs: 5 * 60_000 })).
using PiSharp.CodingAgent.Export;

namespace PiSharp.Cli.Authentication;

/// <summary>The /share Radius auth over the live provider resolution: auth.json OAuth (refreshed to the requested validity), a stored
/// API key, then RADIUS_API_KEY. Radius is a built-in provider, so it is always registered.</summary>
internal sealed class RadiusShareAuthentication(ProviderLiveAuthentication radius) : IRadiusShareAuthentication
{
    public bool HasProvider => true;

    public async ValueTask<ShareProviderAuth?> GetAuthAsync(long minimumOAuthValidityMilliseconds, CancellationToken cancellationToken)
    {
        var auth = await radius.ResolveAsync(cancellationToken, minimumOAuthValidityMilliseconds).ConfigureAwait(false);
        return auth is null ? null : new(auth.ApiKey, auth.Headers);
    }

    public static RadiusShareAuthentication Create(AuthJsonCredentialStore? store, Func<string, string?> environment,
        Func<HttpMessageInvoker> createHttp, TimeProvider? time = null)
    {
        var entry = ProviderAuthCatalog.Find(SessionShare.RadiusProviderId) ?? throw new InvalidOperationException("Radius provider auth is not registered.");
        return new(new ProviderLiveAuthentication(entry, store, new LiveProcessEnvironment(environment, entry.EnvironmentVariables), createHttp, time));
    }
}
