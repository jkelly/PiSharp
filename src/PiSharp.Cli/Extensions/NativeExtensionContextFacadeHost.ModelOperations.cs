// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/extensions/types.ts
// (ExtensionContext.modelRegistry) and packages/coding-agent/src/core/model-registry.ts.
using System.Collections.Immutable;
using PiSharp.AI.ModelOperations;
using PiSharp.Cli.Commands;
using PiSharp.Contracts;
using PiSharp.Contracts.ModelOperations;
using PiSharp.Extensions;
using PiSharp.Extensions.Facade.Context;

namespace PiSharp.Cli.Extensions;

public sealed partial class NativeExtensionContextFacadeHost : IExtensionModelOperationsHost
{
    // Upstream every session has a model registry. Without an explicit binding the host uses the process-wide CLI
    // registry over the embedded catalogs, the process environment and the default auth.json (stored OAuth refreshed as Pi does),
    // built on first use.
    private static readonly Lazy<NativeExtensionModelOperations> DefaultModelOperations = new(() => new(
        NativeExtensionModelOperations.CreateDefaultRegistry(LiveSessionRuntime.Default.ReadEnvironment, LiveSessionRuntime.Default.AuthPath,
            LiveSessionRuntime.Default.CreateAuthHttp, LiveSessionRuntime.Default.Time)));
    private NativeExtensionModelOperations? modelOperations;

    /// <summary>Binds this host's model registry once, before its first use. Only application composition binds it;
    /// extensions reach it through their admitted callback's facade.</summary>
    internal void ConfigureModelOperations(ModelOperationsRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (Interlocked.CompareExchange(ref modelOperations, new(registry), null) is not null)
            throw new InvalidOperationException("The model registry is already bound.");
    }

    private NativeExtensionModelOperations Operations(IExtensionContext context) => Read(context, _ =>
    {
        Interlocked.CompareExchange(ref modelOperations, DefaultModelOperations.Value, null);
        return Volatile.Read(ref modelOperations)!;
    });

    public ImmutableArray<JsonData> GetModelsOfType(IExtensionContext context, ModelType type, string? provider)
        => Operations(context).GetModelsOfType(type, provider);

    public JsonData? GetModelOfType(IExtensionContext context, ModelType type, string provider, string id)
        => Operations(context).GetModelOfType(type, provider, id);

    public Task<ImmutableArray<JsonData>> GetAvailableOfTypeAsync(IExtensionContext context, ModelType type, string? provider,
        CancellationToken cancellationToken) => Operations(context).GetAvailableOfTypeAsync(type, provider, cancellationToken);

    public Task<ClassifierResult> ClassifyAsync(IExtensionContext context, JsonData model, ClassifierContext classifierContext,
        ExtensionModelRequestOptions? options, CancellationToken cancellationToken)
        => Operations(context).ClassifyAsync(model, classifierContext, options, cancellationToken);

    public Task<AssistantImages> GenerateImagesAsync(IExtensionContext context, JsonData model, ImagesContext imagesContext,
        ExtensionModelRequestOptions? options, CancellationToken cancellationToken)
        => Operations(context).GenerateImagesAsync(model, imagesContext, options, cancellationToken);
}
