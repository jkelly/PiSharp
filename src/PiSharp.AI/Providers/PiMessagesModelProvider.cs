using System.Collections.Immutable;
using PiSharp.AI.Catalogs;
using PiSharp.AI.Protocols.PiMessages;
using PiSharp.Contracts;

namespace PiSharp.AI.Providers;

/// <summary>Explicit call configuration shared by Pi stream and streamSimple; no credential discovery.</summary>
public sealed record PiMessagesProviderOptions(string? ApiKey = null)
{
    public double? Temperature { get; init; }
    public double? MaxTokens { get; init; }
    public string? Reasoning { get; init; }
    public string? CacheRetention { get; init; }
    public string? SessionId { get; init; }
    public JsonData? ToolChoice { get; init; }
    public bool Debug { get; init; }
    public JsonData? Headers { get; init; }
    public JsonData? Environment { get; init; }
    public Func<string, string?>? EnvironmentLookup { get; init; }
    public PiMessagesLifecycleHooks Hooks { get; init; } = new();
    public Func<HttpResponseMessage, CancellationToken, ValueTask<Stream?>>? BodyReaderFactory { get; init; }

    internal PiMessagesOptions Bind(FrozenCatalogModel model) => new(model.Raw, ApiKey)
    {
        Temperature = Temperature, MaxTokens = MaxTokens, Reasoning = Reasoning,
        CacheRetention = CacheRetention, SessionId = SessionId, ToolChoice = ToolChoice,
        Debug = Debug, Headers = Headers, Environment = Environment,
        // Ambient provider environment is an explicit, unqualified Source compatibility gap.
        EnvironmentLookup = EnvironmentLookup ?? (_ => null),
        Hooks = Hooks, BodyReaderFactory = BodyReaderFactory
    };
    public override string ToString() => nameof(PiMessagesProviderOptions);
}

/// <summary>Owns every Pi Messages chat row and its complete raw catalog metadata; borrows the client.</summary>
public sealed class PiMessagesModelProvider : IModelProvider
{
    public string ProviderId { get; }
    public IReadOnlyList<ModelDescriptor> Models { get; }
    public ImmutableArray<FrozenCatalogModel> CatalogModels { get; }
    public IChatTransport Transport { get; }
    private readonly ImmutableDictionary<ModelDescriptor, FrozenCatalogModel> _metadata;

    public PiMessagesModelProvider(FrozenModelCatalog catalog, HttpClient client, PiMessagesProviderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(client);
        options ??= new();
        ProviderId = catalog.Provider;
        CatalogModels = catalog.Models.Where(model =>
            model.Type == CatalogModelType.Chat && model.DeclaredApi == "pi-messages").ToImmutableArray();
        if (CatalogModels.IsEmpty) throw new ArgumentException("Catalog has no Pi Messages chat models.", nameof(catalog));
        var descriptors = ImmutableArray.CreateBuilder<ModelDescriptor>();
        var metadata = ImmutableDictionary.CreateBuilder<ModelDescriptor, FrozenCatalogModel>();
        var transports = ImmutableDictionary.CreateBuilder<ModelDescriptor, IChatTransport>();
        foreach (var row in CatalogModels)
        {
            var descriptor = new ModelDescriptor(row.Id, row.DeclaredApi, row.Provider);
            descriptors.Add(descriptor);
            metadata.Add(descriptor, row);
            transports.Add(descriptor, new PiMessagesHttpSseTransport(client, descriptor, options.Bind(row)));
        }
        Models = descriptors.ToImmutable();
        _metadata = metadata.ToImmutable();
        Transport = new BoundTransport(transports.ToImmutable());
    }

    // Pi's pinned streamSimple forwards these options directly, without Completions defaults/clamping.
    public static PiMessagesModelProvider CreateDirect(FrozenModelCatalog catalog, HttpClient client,
        PiMessagesProviderOptions? options = null) => new(catalog, client, options);
    public static PiMessagesModelProvider CreateSimple(FrozenModelCatalog catalog, HttpClient client,
        PiMessagesProviderOptions? options = null) => new(catalog, client, options);

    public FrozenCatalogModel GetCatalogModel(ModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return _metadata.TryGetValue(model, out var row) ? row :
            throw new ArgumentException("Unknown Pi Messages model identity.", nameof(model));
    }

    private sealed class BoundTransport(ImmutableDictionary<ModelDescriptor, IChatTransport> transports) : IChatTransport
    {
        public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            return transports.TryGetValue(request.Model, out var transport) ?
                transport.StreamAsync(request, cancellationToken) :
                throw new ArgumentException("Unknown Pi Messages model identity.", nameof(request));
        }
    }
}
