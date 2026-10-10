// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/extensions/types.ts
// (ExtensionContext.modelRegistry) and packages/coding-agent/src/core/model-registry.ts.
using System.Collections.Immutable;
using PiSharp.Contracts;
using PiSharp.Contracts.ModelOperations;
using PiSharp.Extensions;
using PiSharp.Extensions.Facade.Context;

namespace PiSharp.Cli.Extensions;

/// <summary>ctx.modelRegistry for the Pi entry's native extensions: the context reads' model registry (the run's, once bound).</summary>
public sealed partial class NativeExtensionRegistrationFacadeHost : IExtensionModelOperationsHost
{
    public ImmutableArray<JsonData> GetModelsOfType(IExtensionContext context, ModelType type, string? provider) => reads.GetModelsOfType(context, type, provider);
    public JsonData? GetModelOfType(IExtensionContext context, ModelType type, string provider, string id) => reads.GetModelOfType(context, type, provider, id);
    public Task<ImmutableArray<JsonData>> GetAvailableOfTypeAsync(IExtensionContext context, ModelType type, string? provider, CancellationToken cancellationToken) =>
        reads.GetAvailableOfTypeAsync(context, type, provider, cancellationToken);
    public Task<ClassifierResult> ClassifyAsync(IExtensionContext context, JsonData model, ClassifierContext classifierContext,
        ExtensionModelRequestOptions? options, CancellationToken cancellationToken) => reads.ClassifyAsync(context, model, classifierContext, options, cancellationToken);
    public Task<AssistantImages> GenerateImagesAsync(IExtensionContext context, JsonData model, ImagesContext imagesContext,
        ExtensionModelRequestOptions? options, CancellationToken cancellationToken) => reads.GenerateImagesAsync(context, model, imagesContext, options, cancellationToken);
}
