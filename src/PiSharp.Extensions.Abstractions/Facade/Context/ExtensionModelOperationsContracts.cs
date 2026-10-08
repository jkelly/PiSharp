// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/extensions/types.ts
// (ExtensionContext.modelRegistry) and packages/coding-agent/src/core/model-registry.ts (getModelsOfType,
// getModelOfType/findOfType, getAvailableOfType, classify, generateImages).
using System.Collections.Immutable;
using PiSharp.Contracts;
using PiSharp.Contracts.ModelOperations;

namespace PiSharp.Extensions.Facade.Context;

/// <summary>Request options an extension may pass to <c>classify</c> and <c>generateImages</c>
/// (<c>ModelsClassifierOptions</c>/<c>ModelsImagesOptions</c> without the in-process callbacks). Values win over the
/// provider's resolved auth per field.</summary>
public sealed record ExtensionModelRequestOptions
{
    /// <summary>An explicit provider key used instead of the stored or environment credential.</summary>
    public string? ApiKey { get; init; }
    /// <summary>Extra request headers; a null value suppresses a model or default header of that name.</summary>
    public ImmutableArray<KeyValuePair<string, string?>> Headers { get; init; } = [];
    public int? TimeoutMs { get; init; }
    public int? MaxRetries { get; init; }
    public int? MaxRetryDelayMs { get; init; }
    /// <summary>Classifier logit temperature; must be positive. Ignored by image generation and by APIs that cannot apply it.</summary>
    public double? Temperature { get; init; }
}

/// <summary>
/// Optional capability of the admitted host: the session's model registry for classifier and image models
/// (upstream <c>ctx.modelRegistry</c>). Models travel as their catalog JSON objects. <c>classify</c> and
/// <c>generateImages</c> use the supplied model object as given (api, provider, id, baseUrl, headers, cost) and apply the
/// resolved auth of its <c>provider</c>, as Pi does for trusted extensions: a custom <c>baseUrl</c> receives that provider's
/// credentials. They never throw for provider failures: those arrive as error or aborted results.
/// </summary>
public interface IExtensionModelOperationsHost : IExtensionContextReadHost
{
    ImmutableArray<JsonData> GetModelsOfType(IExtensionContext context, ModelType type, string? provider);
    JsonData? GetModelOfType(IExtensionContext context, ModelType type, string provider, string id);
    Task<ImmutableArray<JsonData>> GetAvailableOfTypeAsync(IExtensionContext context, ModelType type, string? provider,
        CancellationToken cancellationToken);
    Task<ClassifierResult> ClassifyAsync(IExtensionContext context, JsonData model, ClassifierContext classifierContext,
        ExtensionModelRequestOptions? options, CancellationToken cancellationToken);
    Task<AssistantImages> GenerateImagesAsync(IExtensionContext context, JsonData model, ImagesContext imagesContext,
        ExtensionModelRequestOptions? options, CancellationToken cancellationToken);
}

/// <summary>The callback-bound view of <see cref="IExtensionModelOperationsHost"/> (upstream <c>ctx.modelRegistry</c>):
/// valid only inside the originating admitted callback. A host without the capability refuses with
/// <see cref="NotSupportedException"/>.</summary>
public interface IExtensionModelOperationsFacade
{
    ImmutableArray<JsonData> GetModelsOfType(ModelType type, string? provider = null);
    JsonData? GetModelOfType(ModelType type, string provider, string id);
    ValueTask<ImmutableArray<JsonData>> GetAvailableOfTypeAsync(ModelType type, string? provider = null,
        CancellationToken cancellationToken = default);
    ValueTask<ClassifierResult> ClassifyAsync(JsonData model, ClassifierContext context, ExtensionModelRequestOptions? options = null,
        CancellationToken cancellationToken = default);
    ValueTask<AssistantImages> GenerateImagesAsync(JsonData model, ImagesContext context, ExtensionModelRequestOptions? options = null,
        CancellationToken cancellationToken = default);
}
