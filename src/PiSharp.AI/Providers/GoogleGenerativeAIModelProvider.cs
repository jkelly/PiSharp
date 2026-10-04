using System.Collections.Immutable;
using PiSharp.AI.Catalogs;
using PiSharp.AI.Protocols.GoogleGenerativeAI;
using PiSharp.Contracts;

namespace PiSharp.AI.Providers;

/// <summary>Explicit direct key-auth binding. Owns complete raw catalog rows; borrows the HttpClient.</summary>
public sealed class GoogleGenerativeAIModelProvider : IModelProvider
{
    public string ProviderId { get; }
    public IReadOnlyList<ModelDescriptor> Models { get; }
    public ImmutableArray<FrozenCatalogModel> CatalogModels { get; }
    public IChatTransport Transport { get; }
    private readonly ImmutableDictionary<ModelDescriptor, FrozenCatalogModel> _metadata;
    public GoogleGenerativeAIModelProvider(FrozenModelCatalog catalog, HttpClient client, string? apiKey,
        Func<FrozenCatalogModel, GoogleGenerativeAIOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(catalog); ArgumentNullException.ThrowIfNull(client);
        ProviderId = catalog.Provider;
        CatalogModels = catalog.Models.Where(x => x.Type == CatalogModelType.Chat && x.DeclaredApi == "google-generative-ai").ToImmutableArray();
        if (CatalogModels.IsEmpty) throw new ArgumentException("Catalog has no Google Generative AI chat models.", nameof(catalog));
        var metadata = ImmutableDictionary.CreateBuilder<ModelDescriptor, FrozenCatalogModel>();
        var transports = ImmutableDictionary.CreateBuilder<ModelDescriptor, IChatTransport>();
        foreach (var row in CatalogModels)
        {
            var model = new ModelDescriptor(row.Id, row.DeclaredApi, row.Provider);
            var options = configure?.Invoke(row) ?? new(row.Raw, apiKey);
            if (options.ModelMetadata.ToString() != row.Raw.ToString())
                throw new ArgumentException("Google configuration must retain the complete catalog row.", nameof(configure));
            // An explicit configure callback can override the supplied key, as caller-owned configuration.
            options.Validate(); metadata.Add(model, row); transports.Add(model, new GoogleGenerativeAIHttpTransport(client, model, options));
        }
        _metadata = metadata.ToImmutable(); Models = CatalogModels.Select(x => new ModelDescriptor(x.Id, x.DeclaredApi, x.Provider)).ToImmutableArray();
        Transport = new Bound(transports.ToImmutable());
    }
    public FrozenCatalogModel GetCatalogModel(ModelDescriptor model) => _metadata.TryGetValue(model, out var row) ?
        row : throw new ArgumentException("Unknown Google model identity.", nameof(model));
    private sealed class Bound(ImmutableDictionary<ModelDescriptor, IChatTransport> transports) : IChatTransport
    {
        public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default) =>
            transports.TryGetValue(request.Model, out var transport) ? transport.StreamAsync(request, cancellationToken) :
                throw new ArgumentException("Unknown Google model identity.", nameof(request));
    }
}
