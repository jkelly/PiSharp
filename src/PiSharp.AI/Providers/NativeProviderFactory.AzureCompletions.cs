// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/providers/azure.ts and packages/ai/src/api/azure-openai-config.ts.
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI.Authentication;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.Contracts;

namespace PiSharp.AI.Providers;

/// <summary>AzureEndpointOptions: explicit overrides, then the caller-supplied AZURE_OPENAI_* values, then model.baseUrl.
/// Chat Completions uses the OpenAI client, so AZURE_OPENAI_API_VERSION does not apply.</summary>
public sealed record AzureEndpointOptions
{
    public string? AzureBaseUrl { get; init; }
    public string? AzureResourceName { get; init; }
    public string? AzureDeploymentName { get; init; }
    /// <summary>Copied provider environment (getProviderEnvValue); no ambient environment is read.</summary>
    public ProviderEnvironmentSnapshot? Environment { get; init; }
}

/// <summary>Pi azure-openai-config.ts endpoint and deployment resolution. Diagnostics never echo configured values.</summary>
public static class AzureOpenAIConfiguration
{
    public const string BaseUrlVariable = "AZURE_OPENAI_BASE_URL";
    public const string ResourceNameVariable = "AZURE_OPENAI_RESOURCE_NAME";
    public const string DeploymentNameMapVariable = "AZURE_OPENAI_DEPLOYMENT_NAME_MAP";

    /// <summary>resolveDeploymentName: explicit name, then the model's AZURE_OPENAI_DEPLOYMENT_NAME_MAP entry, then the model id.</summary>
    public static string ResolveDeploymentName(string modelId, AzureEndpointOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        if (!string.IsNullOrEmpty(options?.AzureDeploymentName)) return options.AzureDeploymentName;
        string? mapped = null;
        foreach (var entry in (options?.Environment?.GetValue(DeploymentNameMapVariable) ?? "").Split(','))
        {
            // JS split("=", 2) keeps the first two fields; later entries for the same model win (Map.set).
            var fields = entry.Trim().Split('=');
            if (fields.Length >= 2 && fields[0].Length != 0 && fields[1].Length != 0 && fields[0].Trim() == modelId) mapped = fields[1].Trim();
        }
        return string.IsNullOrEmpty(mapped) ? modelId : mapped;
    }

    /// <summary>resolveAzureBaseUrl: explicit or AZURE_OPENAI_BASE_URL, then a resource name, then model.baseUrl; Azure hosts get /openai/v1.</summary>
    public static string ResolveBaseUrl(string? modelBaseUrl, AzureEndpointOptions? options = null)
    {
        var baseUrl = Truthy(options?.AzureBaseUrl?.Trim()) ?? Truthy(options?.Environment?.GetValue(BaseUrlVariable)?.Trim());
        var resource = Truthy(options?.AzureResourceName) ?? options?.Environment?.GetValue(ResourceNameVariable);
        if (baseUrl is null && resource is not null)
        {
            // PiSharp keeps the resource a single DNS label rather than letting URL parsing reinterpret it.
            if (resource.Length > 63 || !char.IsAsciiLetterOrDigit(resource[0]) || !char.IsAsciiLetterOrDigit(resource[^1]) ||
                resource.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '-')))
                throw new ArgumentException("Invalid Azure OpenAI resource name.");
            baseUrl = "https://" + resource + ".openai.azure.com/openai/v1";
        }
        baseUrl ??= Truthy(modelBaseUrl) ?? throw new ArgumentException(
            "Azure OpenAI base URL is required. Set AZURE_OPENAI_BASE_URL or AZURE_OPENAI_RESOURCE_NAME, or pass azureBaseUrl, azureResourceName, or model.baseUrl.");
        return NormalizeBaseUrl(baseUrl);
    }

    private static string NormalizeBaseUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim().TrimEnd('/'), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) throw new ArgumentException("Invalid Azure OpenAI base URL.");
        var azure = uri.Host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".cognitiveservices.azure.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".ai.azure.com", StringComparison.OrdinalIgnoreCase);
        var path = uri.AbsolutePath.TrimEnd('/');
        if (azure && path is "" or "/openai" or "/openai/v1/responses") return new UriBuilder(uri) { Path = "/openai/v1", Query = "" }.Uri.AbsoluteUri.TrimEnd('/');
        // The SDK appends the resource path to the base URL text, so a query would land before it.
        if (uri.Query.Length != 0) throw new ArgumentException("Unsupported Azure OpenAI base URL query.");
        return uri.AbsoluteUri.TrimEnd('/');
    }

    private static string? Truthy(string? value) => string.IsNullOrEmpty(value) ? null : value;
}

public static partial class NativeProviderFactory
{
    /// <summary>
    /// Azure (Foundry) Chat Completions under provider "azure" (Pi 1.0.3 providers/azure.ts over openai-completions): the resolved
    /// base URL replaces model.baseUrl, the request goes to {base}/chat/completions with the OpenAI client's Bearer key, and a
    /// deployment name other than the model id replaces only the body's model field.
    /// </summary>
    public static NativeHttpModelProvider CreateAzureCompletions(ModelDescriptor model, string explicitApiKey, AzureEndpointOptions? azure = null,
        CompletionsTranscriptProjectionOptions? projectionOptions = null, CompletionsKeyAuthRequestOptions? requestOptions = null,
        HttpMessageHandler? handler = null, JsonData? modelMetadata = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Provider != "azure" || model.Api != "openai-completions" || string.IsNullOrWhiteSpace(model.Id) ||
            model.Id.Length > 1024 || model.Id.Any(char.IsControl)) throw new ArgumentException("Unsupported native model or endpoint selection.");
        if (string.IsNullOrEmpty(explicitApiKey) || explicitApiKey.Length > 4096 || explicitApiKey.Any(value => value is < '!' or > '~'))
            throw new ArgumentException("Invalid explicit native API key.");
        requestOptions ??= new();
        var source = requestOptions.ModelMetadata ?? modelMetadata;
        var declared = source?.Value.ValueKind == JsonValueKind.Object ? source.Value : default;
        var declaredBaseUrl = declared.ValueKind == JsonValueKind.Object && declared.TryGetProperty("baseUrl", out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
        // Like the CLI's catalog routes, the model's reasoning flag selects the projection unless the caller supplies one.
        projectionOptions ??= new(Reasoning: declared.ValueKind == JsonValueKind.Object && declared.TryGetProperty("reasoning", out var reasoning) && reasoning.ValueKind == JsonValueKind.True);
        var baseUrl = AzureOpenAIConfiguration.ResolveBaseUrl(declaredBaseUrl, azure);
        var endpoint = new Uri(baseUrl + "/chat/completions");
        // resolveAzureModel: { ...model, baseUrl } keeps the field's position.
        JsonData? Resolve(JsonData? metadata)
        {
            if (metadata is null || JsonNode.Parse(metadata.ToString()) is not JsonObject owned) return metadata;
            owned["baseUrl"] = baseUrl; return JsonData.Parse(owned.ToJsonString());
        }
        var deployment = AzureOpenAIConfiguration.ResolveDeploymentName(model.Id, azure);
        requestOptions = requestOptions with
        {
            ModelMetadata = Resolve(requestOptions.ModelMetadata ?? modelMetadata),
            RequestModelId = deployment == model.Id ? requestOptions.RequestModelId : deployment
        };
        return BindCompletions(model, endpoint, explicitApiKey, projectionOptions, requestOptions, handler, Resolve(modelMetadata));
    }
}
