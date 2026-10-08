using System.Collections.Immutable;
using PiSharp.Contracts;

namespace PiSharp.AI.Providers;

/// <summary>Immutable native routing by the complete, ordinal model/API/provider identity.</summary>
public sealed class ModelTransportRegistry : IChatTransport, IThinkingLevelTransport
{
    private readonly ImmutableDictionary<ModelDescriptor, IChatTransport> _transports;
    public ImmutableArray<ModelDescriptor> Models { get; }

    public ModelTransportRegistry(IEnumerable<IModelProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var transports = ImmutableDictionary.CreateBuilder<ModelDescriptor, IChatTransport>();
        var models = ImmutableArray.CreateBuilder<ModelDescriptor>();
        foreach (var provider in providers)
        {
            if (provider is null)
                throw new ArgumentException("Invalid model provider.", nameof(providers));
            var providerId = provider.ProviderId;
            var transport = provider.Transport;
            var providerModels = provider.Models;
            if (string.IsNullOrWhiteSpace(providerId) || providerModels is null || transport is null)
                throw new ArgumentException("Invalid model provider.", nameof(providers));
            var selected = providerModels.ToArray();
            if (selected.Length == 0) throw new ArgumentException("Provider has no models.", nameof(providers));
            foreach (var model in selected)
            {
                if (model is null || string.IsNullOrWhiteSpace(model.Id) || string.IsNullOrWhiteSpace(model.Api) ||
                    model.Provider != providerId)
                    throw new ArgumentException("Invalid model provider identity.", nameof(providers));
                if (!transports.TryAdd(model, transport))
                    throw new ArgumentException("Duplicate model/API/provider identity.", nameof(providers));
                models.Add(model);
            }
        }
        _transports = transports.ToImmutable();
        Models = models.ToImmutable();
    }

    public IChatTransport Resolve(ModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return _transports.TryGetValue(model, out var transport) ? transport :
            throw new ArgumentException("Unknown model/API/provider identity.", nameof(model));
    }

    public ImmutableArray<string> GetSupportedThinkingLevels(ModelDescriptor model) =>
        ThinkingLevels.GetSupported(Resolve(model), model);

    public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Resolve before admitting the selected provider's iterator or any provider callback.
        return Resolve(request.Model).StreamAsync(request, cancellationToken);
    }
}
