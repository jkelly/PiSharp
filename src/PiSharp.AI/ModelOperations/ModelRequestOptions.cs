// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/types.ts (ProviderRequestOptions, ClassifierOptions,
// ImagesOptions, ProviderResponse).
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PiSharp.Contracts;

namespace PiSharp.AI.ModelOperations;

/// <summary>types.ts <c>ProviderResponse</c>: the status and lower-cased headers of an accepted HTTP response.</summary>
public sealed record ProviderResponseInfo(int Status, ImmutableArray<KeyValuePair<string, string>> Headers);

/// <summary>Replaces the payload before it is sent; return null to keep it (<c>onPayload</c>). The callback receives
/// its own copy.</summary>
public delegate ValueTask<JsonNode?> ModelPayloadCallback(JsonNode payload, OperationModel model, CancellationToken cancellationToken);

/// <summary>Observes an accepted response before its body is used (<c>onResponse</c>).</summary>
public delegate ValueTask ModelResponseCallback(ProviderResponseInfo response, OperationModel model, CancellationToken cancellationToken);

/// <summary>types.ts <c>ProviderRequestOptions</c>. Cancellation is the method's token (upstream <c>signal</c>).</summary>
public record ModelRequestOptions
{
    /// <summary>The credential sent as <c>Authorization: Bearer</c>. Registry calls resolve it from provider auth.</summary>
    public string? ApiKey { get; init; }
    /// <summary>Request headers merged case-insensitively over provider defaults and model headers, in order; a null value
    /// suppresses a default or model header of that name.</summary>
    public ImmutableArray<KeyValuePair<string, string?>> Headers { get; init; } = [];
    /// <summary>Provider-scoped environment values, e.g. <c>CLOUDFLARE_ACCOUNT_ID</c> for endpoint placeholders.</summary>
    public ImmutableDictionary<string, string> Env { get; init; } = ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);
    public ModelPayloadCallback? OnPayload { get; init; }
    public ModelResponseCallback? OnResponse { get; init; }
    /// <summary>Per-attempt request timeout in milliseconds.</summary>
    public int? TimeoutMs { get; init; }
    /// <summary>Client-side retries. Classifiers default to 2, image generation to 0.</summary>
    public int? MaxRetries { get; init; }
    /// <summary>Longest server-requested retry delay that is honored; longer requests fail at once. Default 60000; 0 disables the cap.</summary>
    public int? MaxRetryDelayMs { get; init; }
    /// <summary>The HTTP transport (upstream <c>fetch</c>). Null uses a shared client that does not follow redirects.</summary>
    public HttpMessageInvoker? Http { get; init; }
    /// <summary>Clock for retry delays, timeouts and result timestamps.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    /// <summary>Registry calls only (models.ts <c>transformHeaders</c>): rewrites the merged auth, model and request
    /// headers last, before the API adds its defaults.</summary>
    public Func<ImmutableArray<KeyValuePair<string, string?>>, CancellationToken, ValueTask<ImmutableArray<KeyValuePair<string, string?>>>>? TransformHeaders { get; init; }
}

/// <summary>types.ts <c>ClassifierOptions</c>.</summary>
public sealed record ClassifierOptions : ModelRequestOptions
{
    /// <summary>Divides the answer logits by this value before they are normalized into probabilities. Must be positive.
    /// APIs that cannot apply it ignore it.</summary>
    public double? Temperature { get; init; }
}

/// <summary>types.ts <c>ImagesOptions</c>.</summary>
public sealed record ImagesOptions : ModelRequestOptions
{
    /// <summary>Request metadata; providers use the fields they understand (OpenRouter images uses none).</summary>
    public JsonData? Metadata { get; init; }
}
