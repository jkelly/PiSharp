// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/auth/resolve.ts (resolveProviderAuth),
// packages/ai/src/providers/anthropic.ts (anthropicApiKeyAuth.resolve), packages/coding-agent/src/core/model-runtime.ts
// (prepareRequest: auth is resolved for every request), packages/coding-agent/src/core/auth-storage.ts (read) and
// packages/coding-agent/src/core/resolve-config-value.ts (template values).
using System.Collections.Concurrent;
using System.Text;
using PiSharp.AI.Authentication;
using PiSharp.AI.Authentication.OAuth;

namespace PiSharp.Cli.Authentication;

/// <summary>The live session's process environment: each name is read once through the runtime's reader and kept for the
/// session. Startup reads the provider names up front; a stored key template may name another variable, read on first use.</summary>
internal sealed class LiveProcessEnvironment : IInjectedEnvironmentLookup
{
    private readonly Func<string, string?> read;
    private readonly ConcurrentDictionary<string, string?> values = new(StringComparer.Ordinal);
    public LiveProcessEnvironment(Func<string, string?> read, IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(read); this.read = read;
        foreach (var name in names) Get(name);
    }
    /// <summary>Source process.env truthiness: an empty value is absent.</summary>
    public string? Get(string name) => values.GetOrAdd(name, key => read(key) is { Length: > 0 } value ? value : null);
    public ValueTask<string?> ReadAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Get(name));
    }
    public ProviderEnvironmentSnapshot Snapshot(params string[] names) =>
        new(process: names.Select(name => KeyValuePair.Create(name, Get(name))));
    public override string ToString() => "LiveProcessEnvironment [values redacted]";
}

/// <summary>Source resolve-config-value.ts resolveConfigValue: <c>$NAME</c>/<c>${NAME}</c> interpolate the credential env, then the
/// process env (a missing one leaves the value unresolved); <c>$$</c> and <c>$!</c> escape; <c>!cmd</c> runs through the shell and its
/// trimmed stdout is used (cached for the process; owner decision 0004).</summary>
internal static class ConfigValueTemplate
{
    public static bool IsCommand(string config) => config.StartsWith('!');

    public static string? Resolve(string config, IReadOnlyDictionary<string, string>? credentialEnvironment, Func<string, string?> process)
    {
        if (IsCommand(config)) return PiSharp.Cli.Models.ConfigValueResolver.Process.Resolve(config);
        var resolved = new StringBuilder(); var index = 0;
        while (index < config.Length)
        {
            var dollar = config.IndexOf('$', index);
            if (dollar < 0) { resolved.Append(config, index, config.Length - index); break; }
            resolved.Append(config, index, dollar - index);
            var next = dollar + 1 < config.Length ? config[dollar + 1] : '\0';
            if (next is '$' or '!') { resolved.Append(next); index = dollar + 2; continue; }
            string? name = null; var end = dollar + 1;
            if (next == '{')
            {
                var close = config.IndexOf('}', dollar + 2);
                if (close < 0) { resolved.Append('$'); index = dollar + 1; continue; }
                var candidate = config[(dollar + 2)..close];
                if (!IsName(candidate)) { resolved.Append(config, dollar, close + 1 - dollar); index = close + 1; continue; }
                name = candidate; end = close + 1;
            }
            else
            {
                var length = 0;
                while (dollar + 1 + length < config.Length && (length == 0 ? IsStart(config[dollar + 1]) : IsPart(config[dollar + 1 + length]))) length++;
                if (length == 0) { resolved.Append('$'); index = dollar + 1; continue; }
                name = config.Substring(dollar + 1, length); end = dollar + 1 + length;
            }
            var value = credentialEnvironment is not null && credentialEnvironment.TryGetValue(name, out var scoped) && scoped.Length != 0 ? scoped : process(name);
            if (string.IsNullOrEmpty(value)) return null;
            resolved.Append(value); index = end;
        }
        return resolved.ToString();
    }

    private static bool IsStart(char value) => char.IsAsciiLetter(value) || value == '_';
    private static bool IsPart(char value) => char.IsAsciiLetterOrDigit(value) || value == '_';
    private static bool IsName(string value) => value.Length != 0 && IsStart(value[0]) && value.All(IsPart);
}

/// <summary>
/// Source resolveProviderAuth for provider <c>anthropic</c>, run at session start and again before every request (model-runtime
/// prepareRequest). A stored <c>auth.json</c> credential owns the provider: OAuth is refreshed (and the rotation persisted) through
/// <see cref="StoredOAuthLifecycle"/> when it expires within five minutes and its access token travels in the apiKey channel; a stored
/// api_key supplies its key and env. Only without a stored credential does anthropicApiKeyAuth.resolve read the environment:
/// ANTHROPIC_AUTH_TOKEN, ANTHROPIC_OAUTH_TOKEN, ANTHROPIC_API_KEY, then workload identity federation.
/// </summary>
internal sealed class AnthropicLiveAuthentication
{
    public const string Provider = "anthropic";
    private readonly AuthJsonCredentialStore? store;
    private readonly LiveProcessEnvironment environment;
    private readonly Func<HttpMessageInvoker> createHttp;
    private readonly TimeProvider? time;
    private readonly StoredOAuthLifecycle? lifecycle;
    private readonly Refresh? refresh;

    public AnthropicLiveAuthentication(AuthJsonCredentialStore? store, LiveProcessEnvironment environment,
        Func<HttpMessageInvoker> createHttp, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(environment); ArgumentNullException.ThrowIfNull(createHttp);
        this.store = store; this.environment = environment; this.createHttp = createHttp; this.time = time;
        if (store is not null) { refresh = new(this); lifecycle = new(store, refresh, time); }
    }

    /// <summary>The models.json apiKey for anthropic, used when nothing is stored (provider-composer composeApiKeyAuth).</summary>
    public Func<string?>? ConfiguredApiKey { get; init; }

    public async ValueTask<AuthenticationResolution> ResolveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stored = store is null ? null : await store.ReadEntryAsync(Provider, cancellationToken).ConfigureAwait(false);
        if (stored is { Type: "oauth" })
        {
            var credential = await lifecycle!.ResolveAsync(Provider, cancellationToken: cancellationToken).ConfigureAwait(false);
            // Logged out meanwhile: no silent environment fallback.
            if (credential is null) return new(AuthenticationDiagnostic.Missing);
            return await InjectedAuthenticationResolver.ResolveAnthropicApiKeyAsync(new ProviderEnvironmentSnapshot(),
                (_, _) => ValueTask.FromResult<StoredApiKeyCredential?>(new(credential.Access)), cancellationToken).ConfigureAwait(false);
        }
        if (stored is not null && stored.Type != "api_key") return new(AuthenticationDiagnostic.Missing);
        StoredApiKeyCredential? apiKey = null;
        // provider-composer composeApiKeyAuth: without a stored credential, a models.json apiKey resolves as a credential key.
        if (stored is null && ConfiguredApiKey?.Invoke() is { Length: > 0 } configured) apiKey = new(configured);
        if (stored is not null)
        {
            apiKey = new(stored.Key is null ? null : ConfigValueTemplate.Resolve(stored.Key, stored.Environment, environment.Get),
                stored.Environment is null ? null : new ProviderEnvironmentSnapshot(scoped: stored.Environment.Select(pair => KeyValuePair.Create(pair.Key, (string?)pair.Value))));
        }
        return await InjectedAuthenticationResolver.ResolveAnthropicApiKeyAsync(environment,
            (_, _) => ValueTask.FromResult(apiKey), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The Anthropic OAuth refresh (auth/oauth/anthropic.ts refresh) over a fresh client per refresh.</summary>
    private sealed class Refresh(AnthropicLiveAuthentication owner) : IAdmittedOAuthRefresh
    {
        public async Task<OAuthCredentialSnapshot> RefreshAsync(string provider, OAuthCredentialSnapshot current, CancellationToken cancellationToken)
        {
            using var http = owner.createHttp();
            return await ((IAdmittedOAuthRefresh)new AnthropicOAuth(http, owner.time)).RefreshAsync(provider, current, cancellationToken).ConfigureAwait(false);
        }
    }
}
