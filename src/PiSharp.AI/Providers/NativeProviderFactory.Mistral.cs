using PiSharp.AI.Protocols.MistralConversations;
using PiSharp.Contracts;

namespace PiSharp.AI.Providers;

public static partial class NativeProviderFactory
{
    /// <summary>Direct Mistral binding. The explicit token limit is sent without Simple budget derivation.</summary>
    public static NativeHttpModelProvider CreateMistral(ModelDescriptor model, Uri endpoint, string explicitApiKey,
        MistralTextOptions options, HttpMessageHandler? handler = null)
    {
        ValidateMistral(model, endpoint, explicitApiKey, options);
        return Bind(model, handler, client => new MistralTextHttpSseTransport(client, model, options with { ApiKey = explicitApiKey }));
    }

    /// <summary>Simple Mistral binding. Complete explicit model metadata determines the per-request output budget and thinking capabilities.</summary>
    public static NativeHttpModelProvider CreateMistralSimple(ModelDescriptor model, Uri endpoint, string explicitApiKey,
        JsonData modelMetadata, MistralTextOptions options, HttpMessageHandler? handler = null)
    {
        ValidateMistral(model, endpoint, explicitApiKey, options);
        return Bind(model, handler, client => new MistralSimpleHttpSseTransport(client, model, modelMetadata,
            options with { ApiKey = explicitApiKey }));
    }

    private static void ValidateMistral(ModelDescriptor model, Uri endpoint, string key, MistralTextOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(model, endpoint, key, "mistral", "mistral-conversations", "https://api.mistral.ai/");
        if (options.BaseUrl != endpoint) throw new ArgumentException("Mistral options must retain the selected endpoint.");
        options.Validate();
    }
}
