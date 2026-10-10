// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/model-registry.ts (ctx.modelRegistry: getAll,
// getAvailable, find, findOfType, getModelsOfType, getModelOfType, getAvailableOfType, hasConfiguredAuth, getProviderAuthStatus,
// getProviderDisplayName, getApiKeyForProvider, classify, generateImages, registerProvider with ProviderConfig.images/classifiers),
// packages/coding-agent/src/core/provider-composer.ts (an extension provider's image and classifier APIs) and
// packages/coding-agent/src/core/extensions/types.ts (ProviderConfig, ProviderImageModelConfig, ProviderClassifierModelConfig), for Node
// extensions and native C# extensions (IExtensionModelOperationProviderRegistry).
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI.ModelOperations;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Models;
using PiSharp.Contracts;
using PiSharp.Contracts.ModelOperations;
using PiSharp.Extensions;
using PiSharp.Extensions.Facade.Context;

namespace PiSharp.Cli.Extensions.Pi;

/// <summary>The model registry the extensions see through <c>ctx.modelRegistry</c>: the run's chat catalog, and classifier and image
/// models with the providers Node and native extensions registered (their <c>classify</c>/<c>generateImages</c> run in Node or in the
/// native extension). Native extensions and codemode read <see cref="Operations"/> too.</summary>
internal sealed class PiExtensionModels
{
    private readonly ModelRegistry _chat;
    private readonly ModelOperationsRegistry _builtin;
    private readonly Dictionary<string, string?> _extensionKeys = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private readonly HashSet<(string, string)> _reported = [];
    private ImmutableHashSet<string> _extensionProviders = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
    private PiExtensionHost? _host;
    private Func<string, string?> _environment = _ => null;
    internal ModelOperationsRegistry Operations { get; }

    private PiExtensionModels(ModelRegistry chat, ModelOperationsRegistry builtin)
    {
        _chat = chat; _builtin = builtin;
        Operations = new ModelOperationsRegistry(async (request, token) =>
        {
            bool keyless;
            lock (_extensionKeys)
            {
                if (!_extensionKeys.TryGetValue(request.Provider, out var key)) keyless = false;
                else if (key is not null) return new ProviderAuthResult(request.ApiKey ?? key);
                else keyless = true;
            }
            // composeApiKeyAuth without a key or an inherited method: an explicit request key or a stored api_key credential, else the
            // provider is not configured (model-runtime.ts prepareRequest: "Provider is not configured: <id>").
            return keyless ? await _builtin.ResolveAuthAsync(request, token).ConfigureAwait(false)
                : await _builtin.GetAuthAsync(request.Provider, request.ApiKey, request.Env, token).ConfigureAwait(false);
        });
        foreach (var provider in builtin.GetProviders()) Operations.SetProvider(provider);
    }

    internal static async Task<PiExtensionModels> CreateAsync(PiExtensionHost host, LiveSessionRuntime runtime, CancellationToken token)
    {
        var chat = await runtime.CreateModelRegistryAsync(token).ConfigureAwait(false);
        var builtin = NativeExtensionModelOperations.CreateDefaultRegistry(runtime.ReadEnvironment, runtime.AuthPath, runtime.CreateAuthHttp, runtime.Time);
        var models = new PiExtensionModels(chat, builtin) { _runtime = runtime, _host = host, _environment = runtime.ReadEnvironment };
        host.Stream = models.StreamAsync;
        models.SyncProviders();
        // model-runtime.ts registerProvider/unregisterProvider and reload: the Node and native extensions' providers follow their registrations.
        host.RegistrationsChanged += models.SyncProviders;
        host.NativeModelProviders.Changed += models.SyncProviders;
        host.Models = models.CallAsync;
        host.ModelJson = (provider, id) => chat.Find(provider, id) is { } model ? JsonNode.Parse(model.ToJsonString()) : null;
        // The native extensions' ctx.modelRegistry is this registry too.
        host.BindModelOperations(models.Operations);
        return models;
    }

    /// <summary>The config object of one registered model (a JSON object, else an empty object that fails validation).</summary>
    internal static JsonObject ModelObject(JsonData model) => JsonNode.Parse(model.ToString()) as JsonObject ?? new JsonObject();

    /// <summary>The image and classifier models of a provider config: <c>api</c> defaults to the first implementation of the model's type,
    /// <c>baseUrl</c> to the provider's, <c>name</c> to the id, <c>input</c> to <c>["text"]</c> and <c>cost</c> to zero. Chat models are
    /// the chat registry's. An invalid model is reported through <paramref name="report"/> and skipped.</summary>
    internal static ImmutableArray<OperationModel> BuildModels(string provider, string? baseUrl, IReadOnlyList<string> classifierApis,
        IReadOnlyList<string> imageApis, IEnumerable<JsonObject> configs, Action<string> report)
    {
        var models = ImmutableArray.CreateBuilder<OperationModel>();
        foreach (var model in configs)
        {
            var type = model["type"] is JsonValue typeValue && typeValue.TryGetValue<string>(out var text) ? text : null;
            if (type is not ("image" or "classifier")) continue;
            var json = (JsonObject)model.DeepClone();
            json["provider"] = provider;
            json["api"] ??= type == "image" ? imageApis.FirstOrDefault() : classifierApis.FirstOrDefault();
            json["baseUrl"] ??= baseUrl ?? "";
            json["name"] ??= json["id"]?.DeepClone();
            json["input"] ??= new JsonArray("text");
            json["cost"] ??= new JsonObject { ["input"] = 0, ["output"] = 0, ["cacheRead"] = 0, ["cacheWrite"] = 0 };
            try { models.Add(OperationModel.FromJson(JsonData.Parse(json.ToJsonString()))); }
            catch (FormatException error) { report($"Provider {provider}, model {json["id"]?.ToJsonString() ?? "?"}: {error.Message}"); }
        }
        return models.ToImmutable();
    }

    private sealed record ExtensionProvider(string Name, string? DisplayName, string? ApiKey, ImmutableArray<OperationModel> Models,
        ImmutableDictionary<string, IClassifierApi> Classifiers, ImmutableDictionary<string, IImagesApi> Images);

    /// <summary>registerProvider(name, config) of the Node extensions (their implementations run in Node) and of the native extensions
    /// (their C# callbacks): every registration with classifier or image implementations or models joins <see cref="Operations"/>; a
    /// provider of a built-in id keeps the built-in implementations it does not replace (provider-composer.ts), and its models when it
    /// lists none. Providers no extension registers any more leave (or return to their built-in definition).</summary>
    internal void SyncProviders()
    {
        if (_host is not { } host) return;
        var providers = new List<ExtensionProvider>();
        foreach (var registration in host.ProviderRegistrations)
        {
            var name = registration["name"] is JsonValue nameValue && nameValue.TryGetValue<string>(out var text) ? text : null;
            if (name is null || registration["config"] is not JsonObject config) continue;
            var imageApis = (config["images"] as JsonObject)?.Select(item => item.Key).ToImmutableArray() ?? [];
            var classifierApis = (config["classifiers"] as JsonObject)?.Select(item => item.Key).ToImmutableArray() ?? [];
            if (imageApis.IsEmpty && classifierApis.IsEmpty) continue;
            var extensionPath = registration["extensionPath"]?.GetValue<string>() ?? name;
            var models = BuildModels(name, config["baseUrl"]?.GetValue<string>(), classifierApis, imageApis, (config["models"] as JsonArray ?? []).OfType<JsonObject>(),
                error => { lock (_reported) if (!_reported.Add((extensionPath, error))) return; _ = host.ReportAsync(extensionPath, "register_provider", error); });
            providers.Add(new(name, config["name"]?.GetValue<string>(), config["apiKey"]?.GetValue<string>(), models,
                classifierApis.ToImmutableDictionary(api => api, api => (IClassifierApi)new NodeClassifier(host, name, api), StringComparer.Ordinal),
                imageApis.ToImmutableDictionary(api => api, api => (IImagesApi)new NodeImages(host, name, api), StringComparer.Ordinal)));
        }
        foreach (var provider in host.NativeModelProviders.Current)
        {
            var models = BuildModels(provider.Name, provider.BaseUrl, [.. provider.Classifiers.Keys], [.. provider.Images.Keys], provider.Models.Select(ModelObject), _ => { });
            providers.Add(new(provider.Name, provider.DisplayName, provider.ApiKey, models,
                provider.Classifiers.ToImmutableDictionary(pair => pair.Key, pair => (IClassifierApi)new NativeClassifier(pair.Key, pair.Value), StringComparer.Ordinal),
                provider.Images.ToImmutableDictionary(pair => pair.Key, pair => (IImagesApi)new NativeImages(pair.Key, pair.Value), StringComparer.Ordinal)));
        }
        lock (_sync)
        {
            var current = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
            foreach (var provider in providers)
            {
                current.Add(provider.Name);
                var builtin = _builtin.GetProvider(provider.Name);
                lock (_extensionKeys)
                {
                    // composeApiKeyAuth: the extension's apiKey (an environment variable name or a literal), else the built-in auth; a
                    // provider with neither (null) has only a request key or a stored credential.
                    if (provider.ApiKey is { } apiKey) _extensionKeys[provider.Name] = _environment(apiKey) ?? apiKey;
                    else if (builtin is null) _extensionKeys[provider.Name] = null;
                    else _extensionKeys.Remove(provider.Name);
                }
                var keepModels = provider.Models.IsEmpty && builtin is not null;
                Operations.SetProvider(new ModelOperationsProvider(provider.Name, provider.DisplayName ?? builtin?.Name ?? provider.Name)
                {
                    Models = keepModels ? builtin!.Models : provider.Models, ModelSource = keepModels ? builtin!.ModelSource : null,
                    Classifiers = (builtin?.Classifiers ?? ImmutableDictionary<string, IClassifierApi>.Empty.WithComparers(StringComparer.Ordinal)).SetItems(provider.Classifiers),
                    Images = (builtin?.Images ?? ImmutableDictionary<string, IImagesApi>.Empty.WithComparers(StringComparer.Ordinal)).SetItems(provider.Images),
                    ResolveModel = builtin?.ResolveModel, FilterAllModels = builtin?.FilterAllModels
                });
            }
            foreach (var removed in _extensionProviders.Except(current))
            {
                lock (_extensionKeys) _extensionKeys.Remove(removed);
                if (_builtin.GetProvider(removed) is { } builtin) Operations.SetProvider(builtin);
                else Operations.RemoveProvider(removed);
            }
            _extensionProviders = current.ToImmutable();
        }
    }

    /// <summary>The request options a provider implementation receives (the Node bridge sends the same fields).</summary>
    private static ExtensionModelRequestOptions ExtensionOptions(ModelRequestOptions? options) => new()
    {
        ApiKey = options?.ApiKey, Headers = options?.Headers ?? [], TimeoutMs = options?.TimeoutMs, MaxRetries = options?.MaxRetries,
        MaxRetryDelayMs = options?.MaxRetryDelayMs, Temperature = (options as ClassifierOptions)?.Temperature
    };

    private sealed class NativeClassifier(string api, ExtensionClassifierImplementation implementation) : IClassifierApi
    {
        public string Api => api;
        public Task<ClassifierResult> ClassifyAsync(ClassifierModel model, ClassifierContext context, ClassifierOptions? options = null, CancellationToken cancellationToken = default) =>
            implementation(model.ToJson(), context, ExtensionOptions(options), cancellationToken);
    }

    private sealed class NativeImages(string api, ExtensionImagesImplementation implementation) : IImagesApi
    {
        public string Api => api;
        public Task<AssistantImages> GenerateImagesAsync(ImageModel model, ImagesContext context, ImagesOptions? options = null, CancellationToken cancellationToken = default) =>
            implementation(model.ToJson(), context, ExtensionOptions(options), cancellationToken);
    }

    private static JsonObject Options(ModelRequestOptions? options)
    {
        var json = new JsonObject();
        if (options?.ApiKey is { } key) json["apiKey"] = key;
        if (options is { Headers.IsDefaultOrEmpty: false })
        {
            var headers = new JsonObject();
            foreach (var (name, value) in options.Headers) headers[name] = value;
            json["headers"] = headers;
        }
        if (options?.TimeoutMs is { } timeout) json["timeoutMs"] = timeout;
        if (options?.MaxRetries is { } retries) json["maxRetries"] = retries;
        if (options is ClassifierOptions { Temperature: { } temperature }) json["temperature"] = temperature;
        return json;
    }

    private sealed class NodeClassifier(PiExtensionHost host, string provider, string api) : IClassifierApi
    {
        public string Api => api;
        public async Task<ClassifierResult> ClassifyAsync(ClassifierModel model, ClassifierContext context, ClassifierOptions? options = null, CancellationToken cancellationToken = default)
        {
            var result = await host.CallAsync("provider.call", new JsonObject
            {
                ["provider"] = provider, ["api"] = api, ["op"] = "classify",
                ["args"] = new JsonArray(JsonNode.Parse(model.ToJson().ToString()), JsonNode.Parse(ModelOperationJson.WriteClassifierContext(context).ToString()), Options(options))
            }, cancellationToken).ConfigureAwait(false);
            return ModelOperationJson.ParseClassifierResult(result ?? throw new InvalidOperationException("The extension classifier returned nothing"));
        }
    }

    private sealed class NodeImages(PiExtensionHost host, string provider, string api) : IImagesApi
    {
        public string Api => api;
        public async Task<AssistantImages> GenerateImagesAsync(ImageModel model, ImagesContext context, ImagesOptions? options = null, CancellationToken cancellationToken = default)
        {
            var result = await host.CallAsync("provider.call", new JsonObject
            {
                ["provider"] = provider, ["api"] = api, ["op"] = "generateImages",
                ["args"] = new JsonArray(JsonNode.Parse(model.ToJson().ToString()), JsonNode.Parse(ModelOperationJson.WriteImagesContext(context).ToString()), Options(options))
            }, cancellationToken).ConfigureAwait(false);
            return ModelOperationJson.ParseAssistantImages(result ?? throw new InvalidOperationException("The extension image provider returned nothing"));
        }
    }

    // ---------------------------------------------------------------------------------------------------------------- streams

    private LiveSessionRuntime? _runtime;

    /// <summary>pi-ai stream/streamSimple/complete called from extension code (summaries, sub-agents): the model streams through its
    /// PiSharp live route with the run's credentials; the result is the final AssistantMessage (an error message on failure).
    /// The context's <c>systemPrompt</c> and <c>tools</c> become the leading system message, as the session's requests carry them.</summary>
    internal async Task<JsonNode?> StreamAsync(JsonElement model, JsonElement context, CancellationToken token)
    {
        var provider = model.GetProperty("provider").GetString()!; var id = model.GetProperty("id").GetString()!;
        try
        {
            var entry = _chat.Find(provider, id) ?? RegistryModel.FromJson(JsonNode.Parse(model.GetRawText())!.AsObject());
            await using var connection = LiveSessionSelection.FromEntry(entry, _chat, null, useModelMaximum: true).Connect(_runtime);
            var messages = ImmutableArray.CreateBuilder<TranscriptEntry>();
            var system = new JsonObject { ["role"] = "system", ["content"] = context.TryGetProperty("systemPrompt", out var prompt) && prompt.ValueKind == JsonValueKind.String ? prompt.GetString() : "",
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            if (context.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() > 0)
                system["toolsAdded"] = new JsonArray([.. tools.EnumerateArray().Select(tool => (JsonNode)new JsonObject
                {
                    ["name"] = tool.GetProperty("name").GetString(), ["description"] = tool.TryGetProperty("description", out var d) ? d.GetString() : "",
                    ["parameters"] = tool.TryGetProperty("parameters", out var p) ? JsonNode.Parse(p.GetRawText()) : new JsonObject()
                })]);
            messages.Add(new("system", JsonData.Parse(system.ToJsonString())));
            if (context.TryGetProperty("messages", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var message in list.EnumerateArray())
                    messages.Add(new(message.TryGetProperty("role", out var role) ? role.GetString() ?? "user" : "user", JsonData.Parse(message.GetRawText())));
            StreamTerminalEvent? terminal = null;
            await foreach (var observation in connection.CreateTransport().StreamAsync(new PiSharp.AI.ChatRequest(new(entry.Id, entry.Api, entry.Provider), messages.ToImmutable(),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), token).ConfigureAwait(false))
                if (observation is StreamTerminalEvent done) terminal = done;
            return terminal is null ? Failed("The stream ended without a final message") : JsonNode.Parse(PiWireJson.WriteMessage(terminal.Message).ToString());
        }
        catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
        { return Failed(error.Message); }

        JsonObject Failed(string message) => new()
        {
            ["role"] = "assistant", ["content"] = new JsonArray(), ["api"] = model.TryGetProperty("api", out var api) ? api.GetString() : "", ["provider"] = provider, ["model"] = id,
            ["usage"] = new JsonObject { ["input"] = 0, ["output"] = 0, ["cacheRead"] = 0, ["cacheWrite"] = 0, ["totalTokens"] = 0,
                ["cost"] = new JsonObject { ["input"] = 0, ["output"] = 0, ["cacheRead"] = 0, ["cacheWrite"] = 0, ["total"] = 0 } },
            ["stopReason"] = "error", ["errorMessage"] = message, ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    }

    // ---------------------------------------------------------------------------------------------------------------- calls

    private static string? Text(JsonElement args, int index) =>
        args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > index && args[index].ValueKind == JsonValueKind.String ? args[index].GetString() : null;
    private static ModelType TypeOf(string? type) => type switch { "image" => ModelType.Image, "classifier" => ModelType.Classifier, _ => ModelType.Chat };
    private static JsonNode? Node(RegistryModel? model) => model is null ? null : JsonNode.Parse(model.ToJsonString());
    private static JsonArray Nodes(IEnumerable<RegistryModel> models) => new([.. models.Select(model => JsonNode.Parse(model.ToJsonString()))]);
    private static JsonArray Nodes(IEnumerable<OperationModel> models) => new([.. models.Select(model => JsonNode.Parse(model.ToJson().ToString()))]);

    internal async Task<JsonNode?> CallAsync(string op, JsonElement args, CancellationToken token)
    {
        switch (op)
        {
            case "getAll": return Nodes(_chat.GetAll());
            case "getAvailable": return Nodes(_chat.GetAvailable());
            case "find": return Text(args, 0) is { } provider && Text(args, 1) is { } id ? Node(_chat.Find(provider, id)) : null;
            case "getError": return _chat.GetError();
            case "getRegisteredProviderIds": return new JsonArray([.. _chat.GetProviderIds().Select(id => (JsonNode)id)]);
            case "getProviderDisplayName": return Text(args, 0) is { } displayed ? _chat.GetProviderDisplayName(displayed) : null;
            case "hasConfiguredAuth":
                return args.GetArrayLength() > 0 && args[0].ValueKind == JsonValueKind.Object && args[0].TryGetProperty("provider", out var owner) && _chat.HasConfiguredAuth(owner.GetString()!);
            case "isUsingOAuth": return false;
            case "getProviderAuthStatus": return Text(args, 0) is { } status ? JsonSerializer.SerializeToNode(_chat.GetProviderAuthStatus(status)) : null;
            case "getApiKeyForProvider":
                return Text(args, 0) is { } keyed && _chat.GetAll().FirstOrDefault(model => model.Provider == keyed) is { } sample && _chat.ResolveRequestAuth(sample, out _) is { } sampleAuth ? sampleAuth.ApiKey : null;
            case "getApiKeyAndHeaders":
            {
                if (args.GetArrayLength() == 0 || args[0].ValueKind != JsonValueKind.Object) return new JsonObject { ["ok"] = false, ["error"] = "No model" };
                var model = _chat.Find(args[0].GetProperty("provider").GetString()!, args[0].GetProperty("id").GetString()!);
                if (model is null) return new JsonObject { ["ok"] = false, ["error"] = "Unknown model" };
                var auth = _chat.ResolveRequestAuth(model, out var error);
                if (auth is null) return new JsonObject { ["ok"] = false, ["error"] = error };
                var headers = new JsonObject(); foreach (var (name, value) in auth.Headers ?? new Dictionary<string, string>()) headers[name] = value;
                return new JsonObject { ["ok"] = true, ["apiKey"] = auth.ApiKey, ["headers"] = headers };
            }
            case "getModelsOfType": return TypeOf(Text(args, 0)) == ModelType.Chat ? Nodes(_chat.GetAll()) : Nodes(Operations.GetModelsOfType(TypeOf(Text(args, 0)), Text(args, 1)));
            case "getModelOfType":
            case "findOfType":
                return TypeOf(Text(args, 0)) == ModelType.Chat ? Node(Text(args, 1) is { } p && Text(args, 2) is { } i ? _chat.Find(p, i) : null)
                    : Text(args, 1) is { } provider2 && Text(args, 2) is { } id2 && Operations.GetModelOfType(TypeOf(Text(args, 0)), provider2, id2) is { } typed ? JsonNode.Parse(typed.ToJson().ToString()) : null;
            case "getAvailableOfType":
                return TypeOf(Text(args, 0)) == ModelType.Chat ? Nodes(_chat.GetAvailable())
                    : Nodes(await Operations.GetAvailableOfTypeAsync(TypeOf(Text(args, 0)), Text(args, 1), token).ConfigureAwait(false));
            case "classify":
            {
                var model = OperationModel.FromJson(JsonData.Parse(args[0].GetRawText()));
                var result = model is ClassifierModel classifier
                    ? await Operations.ClassifyAsync(classifier, ModelOperationJson.ParseClassifierContext(args[1]), ReadOptions<ClassifierOptions>(args, 2), token).ConfigureAwait(false)
                    : new ClassifierResult(model.Api, model.Provider, model.Id, [], ModelOperationStopReason.Error, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                    { ErrorMessage = $"Model {model.Provider}/{model.Id} is not a classifier model" };
                return JsonNode.Parse(ModelOperationJson.WriteClassifierResult(result).ToString());
            }
            case "generateImages":
            {
                var model = OperationModel.FromJson(JsonData.Parse(args[0].GetRawText()));
                var result = model is ImageModel image
                    ? await Operations.GenerateImagesAsync(image, ModelOperationJson.ParseImagesContext(args[1]), ReadOptions<ImagesOptions>(args, 2), token).ConfigureAwait(false)
                    : new AssistantImages(model.Api, model.Provider, model.Id, [], ModelOperationStopReason.Error, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                    { ErrorMessage = $"Model {model.Provider}/{model.Id} is not an image model" };
                return JsonNode.Parse(ModelOperationJson.WriteAssistantImages(result).ToString());
            }
            case "refresh": return new JsonObject();
            default: throw new NotSupportedException($"ctx.modelRegistry.{op}() is not available in this PiSharp host");
        }
    }

    private static T? ReadOptions<T>(JsonElement args, int index) where T : ModelRequestOptions, new()
    {
        if (args.GetArrayLength() <= index || args[index].ValueKind != JsonValueKind.Object) return null;
        var value = args[index];
        var options = new T
        {
            ApiKey = value.TryGetProperty("apiKey", out var key) && key.ValueKind == JsonValueKind.String ? key.GetString() : null,
            TimeoutMs = value.TryGetProperty("timeoutMs", out var timeout) && timeout.ValueKind == JsonValueKind.Number ? timeout.GetInt32() : null,
            MaxRetries = value.TryGetProperty("maxRetries", out var retries) && retries.ValueKind == JsonValueKind.Number ? retries.GetInt32() : null,
            Headers = value.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Object
                ? [.. headers.EnumerateObject().Select(item => KeyValuePair.Create(item.Name, item.Value.ValueKind == JsonValueKind.String ? item.Value.GetString() : null))] : []
        };
        if (options is ClassifierOptions classifier && value.TryGetProperty("temperature", out var temperature) && temperature.ValueKind == JsonValueKind.Number)
            return (T)(ModelRequestOptions)(classifier with { Temperature = temperature.GetDouble() });
        return options;
    }
}
