using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using PiSharp.AI;
using PiSharp.AI.Media;
using PiSharp.Contracts;

namespace PiSharp.Extensions.AI;

/// <summary>
/// <see cref="IServiceCollection"/> registration helpers for wiring PiSharp transports and media APIs
/// with <see cref="Microsoft.Extensions.AI"/>.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers a singleton <see cref="IChatClient"/> backed by the <see cref="IChatTransport"/> resolved from <paramref name="transportFactory"/>.</summary>
    public static IServiceCollection AddPiSharpChatClient(
        this IServiceCollection services,
        Func<IServiceProvider, IChatTransport> transportFactory,
        ModelDescriptor model,
        PiChatClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(transportFactory);
        ArgumentNullException.ThrowIfNull(model);
        return services.AddSingleton<IChatClient>(sp => new PiChatClient(transportFactory(sp), model, options));
    }

    /// <summary>Registers a singleton <see cref="IChatClient"/> wrapping the <see cref="IChatTransport"/> already registered in <paramref name="services"/>.</summary>
    public static IServiceCollection AddPiSharpChatClient(
        this IServiceCollection services,
        ModelDescriptor model,
        PiChatClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(model);
        return services.AddSingleton<IChatClient>(sp => new PiChatClient(sp.GetRequiredService<IChatTransport>(), model, options));
    }

    /// <summary>Registers a keyed singleton <see cref="IChatClient"/> for multi-model host applications.</summary>
    public static IServiceCollection AddKeyedPiSharpChatClient(
        this IServiceCollection services,
        object serviceKey,
        Func<IServiceProvider, IChatTransport> transportFactory,
        ModelDescriptor model,
        PiChatClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serviceKey);
        ArgumentNullException.ThrowIfNull(transportFactory);
        ArgumentNullException.ThrowIfNull(model);
        return services.AddKeyedSingleton<IChatClient>(serviceKey, (sp, _) => new PiChatClient(transportFactory(sp), model, options));
    }

    /// <summary>Registers a singleton <see cref="IChatTransport"/> adapting the <see cref="IChatClient"/> registered in <paramref name="services"/>.</summary>
    public static IServiceCollection AddPiSharpChatClientTransport(
        this IServiceCollection services,
        ChatClientTransportAdapterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddSingleton<IChatTransport>(sp => new ChatClientTransportAdapter(sp.GetRequiredService<IChatClient>(), options));
    }

    /// <summary>Registers a singleton <see cref="IChatTransport"/> adapting the <see cref="IChatClient"/> resolved from <paramref name="chatClientFactory"/>.</summary>
    public static IServiceCollection AddPiSharpChatClientTransport(
        this IServiceCollection services,
        Func<IServiceProvider, IChatClient> chatClientFactory,
        ChatClientTransportAdapterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(chatClientFactory);
        return services.AddSingleton<IChatTransport>(sp => new ChatClientTransportAdapter(chatClientFactory(sp), options));
    }

    /// <summary>Registers a singleton <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> backed by a PiSharp <see cref="IEmbeddingsApi"/>.</summary>
    [Experimental("PISHARP_MEDIA001")]
    public static IServiceCollection AddPiSharpEmbeddingGenerator(
        this IServiceCollection services,
        Func<IServiceProvider, IEmbeddingsApi> apiFactory,
        MediaModel model,
        MediaEndpoint endpoint,
        MediaOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(apiFactory);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(endpoint);
        return services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            sp => new PiEmbeddingGenerator(apiFactory(sp), model, endpoint, options));
    }

    /// <summary>Registers a singleton <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> wrapping the <see cref="IEmbeddingsApi"/> registered in <paramref name="services"/>.</summary>
    [Experimental("PISHARP_MEDIA001")]
    public static IServiceCollection AddPiSharpEmbeddingGenerator(
        this IServiceCollection services,
        MediaModel model,
        MediaEndpoint endpoint,
        MediaOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(endpoint);
        return services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            sp => new PiEmbeddingGenerator(sp.GetRequiredService<IEmbeddingsApi>(), model, endpoint, options));
    }
}
