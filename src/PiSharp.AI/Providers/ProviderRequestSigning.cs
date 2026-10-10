namespace PiSharp.AI.Providers;

/// <summary>A signed provider request (Bedrock SigV4) carries how to sign it again, so HTTP-boundary hooks that change its payload or
/// headers (before_provider_request, before_provider_headers) produce a request signed over the final bytes, as the SDK signs after
/// onPayload and its header middleware.</summary>
public static class ProviderRequestSigning
{
    public static readonly HttpRequestOptionsKey<Func<HttpRequestMessage, CancellationToken, Task>> Resign = new("pisharp.provider.resign");
}
