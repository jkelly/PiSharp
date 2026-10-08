using PiSharp.AI.Authentication;
using PiSharp.AI.Providers;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.AnthropicMessages;

/// <summary>Protocol composition for already resolved main and summary authentication.
/// Each call returns a fresh owner. Settle its stream enumerators before disposing it.
/// No environment, settings, credential store, registry or session is consulted.</summary>
public static class AnthropicResolvedTransports
{
    public static ValueTask<AnthropicInjectedTransportLease> AcquireMainAsync(ModelDescriptor model, Uri endpoint,
        AuthenticationResolution authentication, AnthropicMessagesRequestOptions projection,
        AnthropicMessagesKeyAuthRequestOptions? options = null, JsonData? modelMetadata = null,
        HttpMessageHandler? handler = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projection);
        return Acquire(model, endpoint, authentication, projection, options, modelMetadata, handler, cancellationToken);
    }

    public static ValueTask<AnthropicInjectedTransportLease> AcquireSummaryAsync(ModelDescriptor model, Uri endpoint,
        AuthenticationResolution authentication, int maximumTokens, AnthropicMessagesRequestOptions projection,
        AnthropicMessagesKeyAuthRequestOptions? options = null, HttpMessageHandler? handler = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projection);
        if (maximumTokens <= 0) throw new ArgumentOutOfRangeException(nameof(maximumTokens));
        // R342's resolved summary route uses the same auth channel and genuine identity,
        // with a fresh provider, explicit integer cap, no cache or thinking metadata route.
        return Acquire(model, endpoint, authentication, projection with
        {
            MaximumTokens = maximumTokens, CacheRetention = AnthropicCacheRetention.None,
            ThinkingEnabled = false, ForceAdaptiveThinking = false, Effort = null, ThinkingBudgetTokens = 0
        }, (options ?? new()) with { MaxTokens = maximumTokens }, null, handler, cancellationToken);
    }

    private static ValueTask<AnthropicInjectedTransportLease> Acquire(ModelDescriptor model, Uri endpoint,
        AuthenticationResolution authentication, AnthropicMessagesRequestOptions projection,
        AnthropicMessagesKeyAuthRequestOptions? options, JsonData? metadata, HttpMessageHandler? handler,
        CancellationToken token) => AnthropicInjectedTransportAdapter.AcquireAsync(model, authentication,
        (selected, binding, _) =>
        {
            var provider = AnthropicResolvedProviderFactory.Create(selected, endpoint, binding, projection,
                options, handler, metadata);
            return ValueTask.FromResult(new AnthropicTransportAdmission(selected, provider.Transport,
                new ProviderResources(provider)));
        }, token);

    private sealed class ProviderResources(NativeHttpModelProvider provider) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { provider.Dispose(); return ValueTask.CompletedTask; }
    }
}
