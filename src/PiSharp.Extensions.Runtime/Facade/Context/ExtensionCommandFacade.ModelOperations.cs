// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/extensions/types.ts
// (ExtensionContext.modelRegistry: getModelsOfType, findOfType, getAvailableOfType, classify, generateImages).
using System.Collections.Immutable;
using PiSharp.Contracts;
using PiSharp.Contracts.ModelOperations;
using PiSharp.Extensions.Facade.Context;

namespace PiSharp.Extensions.Runtime.Facade.Context;

public static partial class ExtensionCommandFacade
{
    private sealed partial class View : IExtensionModelOperationsFacade
    {
        private IExtensionModelOperationsHost ModelOperations => host as IExtensionModelOperationsHost ??
            throw new NotSupportedException("The admitted host has no model registry binding.");

        public ImmutableArray<JsonData> GetModelsOfType(ModelType type, string? provider = null)
            => Read(() => ModelOperations.GetModelsOfType(context, type, provider));

        public JsonData? GetModelOfType(ModelType type, string provider, string id)
        {
            ArgumentNullException.ThrowIfNull(provider); ArgumentNullException.ThrowIfNull(id);
            return Read(() => ModelOperations.GetModelOfType(context, type, provider, id));
        }

        public ValueTask<ImmutableArray<JsonData>> GetAvailableOfTypeAsync(ModelType type, string? provider = null,
            CancellationToken cancellationToken = default)
        {
            Check(); var operations = ModelOperations;
            return lease.Start<ImmutableArray<JsonData>, ImmutableArray<JsonData>>(
                () => new(operations.GetAvailableOfTypeAsync(context, type, provider, cancellationToken)), static value => value);
        }

        public ValueTask<ClassifierResult> ClassifyAsync(JsonData model, ClassifierContext classifierContext,
            ExtensionModelRequestOptions? options = null, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(classifierContext);
            Check(); var operations = ModelOperations;
            return lease.Start<ClassifierResult, ClassifierResult>(
                () => new(operations.ClassifyAsync(context, model, classifierContext, options, cancellationToken)), static value => value);
        }

        public ValueTask<AssistantImages> GenerateImagesAsync(JsonData model, ImagesContext imagesContext,
            ExtensionModelRequestOptions? options = null, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(imagesContext);
            Check(); var operations = ModelOperations;
            return lease.Start<AssistantImages, AssistantImages>(
                () => new(operations.GenerateImagesAsync(context, model, imagesContext, options, cancellationToken)), static value => value);
        }
    }
}
