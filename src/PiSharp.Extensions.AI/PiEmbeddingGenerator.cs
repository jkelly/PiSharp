using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.AI.Media;
using PiSharp.Contracts;
using PiSharp.Contracts.ModelOperations;

namespace PiSharp.Extensions.AI;

/// <summary>
/// Exposes a PiSharp <see cref="IEmbeddingsApi"/> as a <see cref="Microsoft.Extensions.AI.IEmbeddingGenerator{TInput, TEmbedding}"/>.
/// Marked <c>PISHARP_MEDIA001</c> to match the underlying <c>PiSharp.AI.Media</c> contracts.
/// </summary>
[Experimental("PISHARP_MEDIA001")]
public sealed class PiEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly IEmbeddingsApi _api;
    private readonly MediaModel _model;
    private readonly MediaEndpoint _endpoint;
    private readonly MediaOptions _defaultOptions;
    private readonly EmbeddingGeneratorMetadata _metadata;
    private int _disposed;

    public PiEmbeddingGenerator(
        IEmbeddingsApi api,
        MediaModel model,
        MediaEndpoint endpoint,
        MediaOptions? defaultOptions = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(endpoint);
        _api = api;
        _model = model;
        _endpoint = endpoint;
        _defaultOptions = defaultOptions ?? new MediaOptions();
        Uri.TryCreate(endpoint.BaseUrl, UriKind.Absolute, out var providerUri);
        _metadata = new(model.Provider, providerUri, model.Id, model.Dimensions);
    }

    /// <inheritdoc />
    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(values);

        var inputs = values.ToImmutableArray();
        if (inputs.IsEmpty)
            return new GeneratedEmbeddings<Embedding<float>>();

        var isOverriddenModel = !string.IsNullOrWhiteSpace(options?.ModelId)
            && !string.Equals(options.ModelId, _model.Id, StringComparison.Ordinal);
        var effectiveModel = isOverriddenModel
            ? _model with { Id = options!.ModelId!, Dimensions = null }
            : _model;

        string? taskType = null;
        JsonData? extraOptions = null;
        if (options?.AdditionalProperties is { } props)
        {
            if (props.TryGetValue(PiChatAdditionalProperties.TaskType, out var rawTask) && rawTask is string t && t.Length > 0)
                taskType = t;
            if (props.TryGetValue(PiChatAdditionalProperties.EmbeddingOptions, out var rawOpts))
            {
                extraOptions = rawOpts switch
                {
                    JsonData jd => jd,
                    JsonElement je => JsonData.FromElement(je),
                    _ => null
                };
            }
        }

        var request = new EmbeddingRequest(inputs)
        {
            Dimensions = options?.Dimensions ?? effectiveModel.Dimensions,
            TaskType = taskType,
            Options = extraOptions
        };

        var result = await _api.EmbedAsync(effectiveModel, _endpoint, request, _defaultOptions, cancellationToken)
            .ConfigureAwait(false);

        if (result.StopReason == ModelOperationStopReason.Aborted)
            throw new OperationCanceledException(result.ErrorMessage ?? "Embedding request aborted.", cancellationToken);
        if (result.StopReason == ModelOperationStopReason.Error)
            throw new InvalidOperationException(result.ErrorMessage ?? "Embedding request failed.");

        var returnedCount = result.Vectors.IsDefault ? 0 : result.Vectors.Length;
        if (returnedCount != inputs.Length)
            throw new InvalidOperationException($"Embedding API returned {returnedCount} vector(s) for {inputs.Length} input(s).");

        var embeddings = new GeneratedEmbeddings<Embedding<float>>(returnedCount);
        foreach (var vector in result.Vectors)
        {
            embeddings.Add(new Embedding<float>(vector)
            {
                ModelId = result.Model
            });
        }

        if (result.InputTokens is { } inputTokens)
        {
            embeddings.Usage = new UsageDetails
            {
                InputTokenCount = inputTokens,
                TotalTokenCount = inputTokens
            };
        }

        if (result.Cost is { } cost)
            embeddings.AdditionalProperties = new AdditionalPropertiesDictionary { ["cost"] = cost };

        return embeddings;
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null) return null;
        if (serviceType == typeof(EmbeddingGeneratorMetadata)) return _metadata;
        if (serviceType.IsInstanceOfType(this)) return this;
        if (serviceType.IsInstanceOfType(_api)) return _api;
        if (serviceType == typeof(MediaModel)) return _model;
        return null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
    }
}
