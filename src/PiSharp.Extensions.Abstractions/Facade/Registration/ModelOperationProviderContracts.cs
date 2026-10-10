// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/extensions/types.ts (ProviderConfig.images and
// ProviderConfig.classifiers, ProviderImageModelConfig, ProviderClassifierModelConfig) and packages/coding-agent/src/core/provider-composer.ts
// (an extension provider's image and classifier implementations, chosen by model.api).
using System.Collections.Immutable;
using PiSharp.Contracts;
using PiSharp.Contracts.ModelOperations;
using PiSharp.Extensions.Facade.Context;

namespace PiSharp.Extensions;

/// <summary>A classifier API implementation (<c>ProviderClassifier.classify</c>): the model as its catalog JSON object (its
/// <c>baseUrl</c> already the resolved one), the request, and the options with the provider's resolved key. Failures should arrive as
/// error results; an exception becomes one.</summary>
public delegate Task<ClassifierResult> ExtensionClassifierImplementation(JsonData model, ClassifierContext context,
    ExtensionModelRequestOptions options, CancellationToken cancellationToken);

/// <summary>An image API implementation (<c>ProviderImages.generateImages</c>), as <see cref="ExtensionClassifierImplementation"/>.</summary>
public delegate Task<AssistantImages> ExtensionImagesImplementation(JsonData model, ImagesContext context,
    ExtensionModelRequestOptions options, CancellationToken cancellationToken);

/// <summary>
/// <c>pi.registerProvider(name, { classifiers, images, models, baseUrl, apiKey, name })</c> for classifier and image models. Each
/// entry of <see cref="Models"/> is a model config object with <c>type</c> <c>"classifier"</c> or <c>"image"</c> and an <c>id</c>;
/// <c>api</c> defaults to the first implementation of its type, <c>baseUrl</c> to <see cref="BaseUrl"/>, <c>name</c> to the id,
/// <c>input</c> to <c>["text"]</c> and <c>cost</c> to zero. <see cref="ApiKey"/> is an environment variable name or a literal key. A
/// provider with the id of a built-in one adds its implementations to the built-in provider's (an implementation of the same API
/// replaces it), and its models replace the built-in models when it lists any.
/// </summary>
public sealed record ExtensionModelOperationProvider(string Name)
{
    /// <summary>The provider's display name (<c>config.name</c>).</summary>
    public string? DisplayName { get; init; }
    public string? BaseUrl { get; init; }
    public string? ApiKey { get; init; }
    public ImmutableArray<JsonData> Models { get; init; } = [];
    /// <summary>Classifier implementations by API id (<c>config.classifiers</c>).</summary>
    public ImmutableDictionary<string, ExtensionClassifierImplementation> Classifiers { get; init; } =
        ImmutableDictionary<string, ExtensionClassifierImplementation>.Empty.WithComparers(StringComparer.Ordinal);
    /// <summary>Image implementations by API id (<c>config.images</c>).</summary>
    public ImmutableDictionary<string, ExtensionImagesImplementation> Images { get; init; } =
        ImmutableDictionary<string, ExtensionImagesImplementation>.Empty.WithComparers(StringComparer.Ordinal);
}

/// <summary>The registry a native extension receives can register classifier and image providers: their models join
/// <c>ctx.modelRegistry</c> (native and Node extensions) and codemode, and <c>classify</c>/<c>generateImages</c> on them run the
/// implementation. Registering a name again replaces the extension's earlier registration; invalid models throw
/// <see cref="ArgumentException"/>. The registrations leave with the extension (unregister, reload, shutdown). Unavailable
/// (<see cref="NotSupportedException"/>) when the host has no model registry.</summary>
public interface IExtensionModelOperationProviderRegistry : IExtensionRegistry
{
    void RegisterModelOperationProvider(ExtensionModelOperationProvider provider);
    void UnregisterModelOperationProvider(string name);
}

/// <summary>The host side of <see cref="IExtensionModelOperationProviderRegistry"/>: registrations by owner.</summary>
public interface IExtensionModelOperationProviderHost
{
    void Register(string ownerId, ExtensionModelOperationProvider provider);
    void Unregister(string ownerId, string name);
    /// <summary>Removes every registration of an owner whose extension ended.</summary>
    void UnregisterOwner(string ownerId);
}
