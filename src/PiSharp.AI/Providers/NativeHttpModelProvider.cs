using PiSharp.Contracts;

namespace PiSharp.AI.Providers;

/// <summary>A single explicit model binding. Owns its client; protocol adapters own each stream's requests and responses.</summary>
public sealed class NativeHttpModelProvider : IModelProvider, IChatTransport, IThinkingLevelTransport, IDisposable
{
    private readonly HttpClient _client;
    private readonly IChatTransport _transport;
    private int _disposed;

    internal NativeHttpModelProvider(ModelDescriptor model, HttpClient client, IChatTransport transport)
    {
        Models = Array.AsReadOnly(new[] { model });
        ProviderId = model.Provider;
        _client = client;
        _transport = transport;
    }

    public string ProviderId { get; }
    public IReadOnlyList<ModelDescriptor> Models { get; }
    public IChatTransport Transport => this;

    public System.Collections.Immutable.ImmutableArray<string> GetSupportedThinkingLevels(ModelDescriptor model)
    {
        if (model != Models[0]) throw new ArgumentException("Unknown model/API/provider identity.", nameof(model));
        return ThinkingLevels.GetSupported(_transport, model);
    }

    public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Model != Models[0]) throw new ArgumentException("Unknown model/API/provider identity.", nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        if (request.ThinkingLevel is { } level) ThinkingLevels.Validate(this, request.Model, level);
        return _transport.StreamAsync(request, cancellationToken);
    }

    /// <summary>Dispose after active stream enumerators have settled. Injected handlers remain caller-owned.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _client.Dispose();
    }
}
