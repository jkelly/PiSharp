// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/providers/amazon-bedrock.ts (api "bedrock-converse-stream")
// and packages/ai/src/api/bedrock-converse-stream.ts (streamSimple).
using PiSharp.AI.Protocols.Bedrock;
using PiSharp.Contracts;

namespace PiSharp.AI.Providers;

public static partial class NativeProviderFactory
{
    /// <summary>Amazon Bedrock ConverseStream for provider "amazon-bedrock". Credentials come from the options (bearer token or
    /// apiKey), the scoped/process AWS environment, or the AWS default credential chain over <paramref name="environment"/>.
    /// Each request's thinking level selects the streamSimple resolution.</summary>
    public static NativeHttpModelProvider CreateBedrock(ModelDescriptor model, JsonData modelMetadata, BedrockConverseOptions options,
        AwsEnvironment environment, HttpMessageHandler? handler = null, string? defaultThinkingLevel = null)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(modelMetadata);
        ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(environment);
        if (model.Provider != "amazon-bedrock" || model.Api != "bedrock-converse-stream" || string.IsNullOrWhiteSpace(model.Id) ||
            model.Id.Length > 2048 || model.Id.Any(char.IsControl)) throw new ArgumentException("Unsupported native model or endpoint selection.");
        return Bind(model, handler, client => new BedrockConverseStreamTransport(client, model, modelMetadata, options, environment, defaultThinkingLevel));
    }
}
