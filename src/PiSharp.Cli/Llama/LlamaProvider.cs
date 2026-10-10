// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/extensions/llama/provider.ts.
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using PiSharp.AI.Authentication.OAuth;
using PiSharp.Cli.Models;

namespace PiSharp.Cli.Llama;

/// <summary>What one llama.cpp catalog refresh may do (provider.ts refreshModels' RefreshModelsContext): the stored catalog, the
/// provider's stored api_key credential (its key and env), whether the network may be used, and the publication.</summary>
internal sealed record LlamaRefreshContext(ModelsStoreEntry? Stored, ProviderStoredCredentialView? Credential, bool AllowNetwork,
    Func<ModelsPublication, Task<bool>> Publish, CancellationToken CancellationToken, HttpMessageInvoker? Http = null);

/// <summary>The stored credential as refreshModels reads it: its type, resolved key and env.</summary>
internal sealed record ProviderStoredCredentialView(string Type, string? Key, IReadOnlyDictionary<string, string>? Environment);

/// <summary>Resolved request auth of the llama.cpp provider (provider.ts auth.apiKey.resolve).</summary>
internal sealed record LlamaAuth(string ApiKey, string ServerUrl, string Source)
{
    internal string BaseUrl => LlamaClient.InferenceUrl(ServerUrl);
    public override string ToString() => $"LlamaAuth ({Source}) [redacted]";
}

/// <summary>
/// provider.ts <c>createLlamaProvider</c>: the <c>llama.cpp</c> provider's chat and classifier models. Upstream the extension's provider
/// object holds them for the process; PiSharp builds model registries per need, so the catalog lives here once per models store
/// (keyed by the store's path) and every registry over that store reads it.
/// </summary>
internal sealed class LlamaCatalog
{
    internal const string ProviderId = "llama.cpp";
    internal const string DefaultServerUrl = "http://127.0.0.1:8080";
    internal const string LlamaClassifyApi = "llama-cpp-classify", SystemOneApi = "typesafe-system-one";

    private sealed record Snapshot(IReadOnlyList<JsonObject> Chat, IReadOnlyList<JsonObject> Classifiers);
    private static readonly ConcurrentDictionary<string, LlamaCatalog> Catalogs = new(StringComparer.Ordinal);
    private Snapshot current = new([], []);
    private int loaded;

    private LlamaCatalog(string? storePath) => StorePath = storePath;

    /// <summary>A catalog no model registry shares (a fresh createLlamaProvider, for probes and tests).</summary>
    internal static LlamaCatalog Detached() => new(null);

    /// <summary>The models store this catalog belongs to (null: no persisted catalog).</summary>
    internal string? StorePath { get; }

    /// <summary>The catalog of the models store at <paramref name="storePath"/> (null: the process's unpersisted catalog).</summary>
    internal static LlamaCatalog For(string? storePath)
    {
        var full = storePath is null ? null : Path.GetFullPath(storePath);
        return Catalogs.GetOrAdd(full ?? "", _ => new LlamaCatalog(full));
    }

    /// <summary>getModels: the chat models.</summary>
    internal IReadOnlyList<RegistryModel> ChatModels => [.. Volatile.Read(ref current).Chat.Select(RegistryModel.FromJson)];
    /// <summary>getAllModels: chat models, then classifier models.</summary>
    internal IReadOnlyList<RegistryModel> AllModels { get { var snapshot = Volatile.Read(ref current); return [.. snapshot.Chat.Concat(snapshot.Classifiers).Select(RegistryModel.FromJson)]; } }
    /// <summary>The classifier models as catalog JSON (the model registry for classification reads these).</summary>
    internal IReadOnlyList<JsonObject> ClassifierJson => [.. Volatile.Read(ref current).Classifiers.Select(model => (JsonObject)model.DeepClone())];

    /// <summary>The catalog once a refresh ran in this process; before it, the store's persisted entry is read once.</summary>
    internal IReadOnlyList<JsonObject> LoadedClassifierJson()
    {
        if (Volatile.Read(ref loaded) == 0 && StorePath is { } path && File.Exists(path))
        {
            try
            {
                if (new FileModelsStore(path).ReadAsync(ProviderId, CancellationToken.None).GetAwaiter().GetResult() is { } stored) Restore(stored);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException) { }
            Interlocked.CompareExchange(ref loaded, 1, 0);
        }
        return ClassifierJson;
    }

    private void Set(IReadOnlyList<JsonObject> chat, IReadOnlyList<JsonObject> classifiers)
    {
        Volatile.Write(ref current, new([.. chat], [.. classifiers]));
        Volatile.Write(ref loaded, 1);
    }

    /// <summary>provider.ts <c>setCatalog</c>: the selectable models of a router catalog.</summary>
    internal void SetCatalog(IReadOnlyList<LlamaModelInfo> catalog, string serverUrl, bool routerAutoload = false)
    {
        var selectable = catalog.Where(model => ModelIsSelectable(model, routerAutoload)).ToList();
        Set([.. selectable.Where(IsChatModel).Select(model => ToChatModel(model, serverUrl))], [.. selectable.Select(model => ToClassifierModel(model, serverUrl))]);
    }

    private void Restore(ModelsStoreEntry stored)
    {
        var models = stored.Models.OfType<JsonObject>().Where(model => JsonTree.String(model, "provider") == ProviderId).ToList();
        Set([.. models.Where(model => JsonTree.String(model, "type") is null or "chat" && JsonTree.String(model, "api") == "openai-completions").Select(JsonTree.CloneObject)],
            [.. models.Where(IsLlamaClassifierModel).Select(JsonTree.CloneObject)]);
    }

    /// <summary>provider.ts <c>refreshModels</c>: a stored catalog is restored (and remembered for its context windows); with the network
    /// allowed and a stored api_key credential naming the server, the router's catalog is read, the chat templates of loaded models
    /// probed, and the selectable models published and persisted.</summary>
    internal async Task RefreshAsync(LlamaRefreshContext context)
    {
        var cachedContextWindows = new Dictionary<string, double>(StringComparer.Ordinal);
        if (context.Stored is { } stored)
        {
            var models = stored.Models.OfType<JsonObject>().Where(model => JsonTree.String(model, "provider") == ProviderId).ToList();
            var restored = models.Where(model => JsonTree.String(model, "type") is null or "chat" && JsonTree.String(model, "api") == "openai-completions").ToList();
            var restoredClassifiers = models.Where(IsLlamaClassifierModel).ToList();
            foreach (var model in restored.Concat(restoredClassifiers))
                if (JsonTree.String(model, "id") is { } id && JsonTree.Number(model, "contextWindow") is { } window) cachedContextWindows[id] = window;
            if (!await context.Publish(new(Update: () => Set([.. restored.Select(JsonTree.CloneObject)], [.. restoredClassifiers.Select(JsonTree.CloneObject)])))
                .ConfigureAwait(false)) return;
        }
        var token = context.CancellationToken;
        if (!context.AllowNetwork || token.IsCancellationRequested || context.Credential?.Type != "api_key") return;
        if (CredentialServerUrl(context.Credential.Environment) is not { } serverUrl) return;
        var client = new LlamaClient(serverUrl, context.Credential.Key, context.Http);
        var catalog = await client.ListAsync(token: token).ConfigureAwait(false);
        if (token.IsCancellationRequested) return;
        var routerAutoload = await RouterAutoloadEnabledAsync(client, catalog, token).ConfigureAwait(false);
        if (token.IsCancellationRequested) return;
        var selectable = catalog.Where(model => ModelIsSelectable(model, routerAutoload)).ToList();
        // Only loaded models expose their chat template without side effects. Unloaded autoload presets would need to be loaded, while
        // querying sleeping models may wake them; they stay without thinking support until a later refresh finds them loaded.
        var refreshed = await Task.WhenAll(selectable.Where(IsChatModel).Select(async model =>
        {
            double? cached = cachedContextWindows.TryGetValue(model.Id, out var window) ? window : null;
            if (model.Status != "loaded") return ToChatModel(model, serverUrl, null, cached);
            var props = await client.PropsAsync(model.Id, token).ConfigureAwait(false);
            return ToChatModel(model, serverUrl, props, cached);
        })).ConfigureAwait(false);
        var refreshedClassifiers = selectable.Select(model => ToClassifierModel(model, serverUrl,
            cachedContextWindows.TryGetValue(model.Id, out var window) ? window : null)).ToList();
        if (token.IsCancellationRequested) return;
        var persisted = new JsonArray([.. refreshed.Concat(refreshedClassifiers).Select(model => (JsonNode)model.DeepClone())]);
        await context.Publish(new(new ModelsStoreEntry(persisted, CheckedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            Update: () => Set(refreshed, refreshedClassifiers))).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------------------------------------------------- model rules

    /// <summary>provider.ts <c>modelIsSelectable</c>: loaded and sleeping models (requests wake sleeping ones); unloaded presets only
    /// when the router autoloads them.</summary>
    internal static bool ModelIsSelectable(LlamaModelInfo model, bool routerAutoload) =>
        model.Status is "loaded" or "sleeping" || routerAutoload && model.Status == "unloaded" && !model.Failed && model.Source == "preset";

    /// <summary>provider.ts <c>routerAutoloadEnabled</c>: <c>/props</c> <c>models_autoload</c>, asked only when an unloaded preset exists.</summary>
    internal static async Task<bool> RouterAutoloadEnabledAsync(LlamaClient client, IReadOnlyList<LlamaModelInfo> catalog, CancellationToken token)
    {
        if (!catalog.Any(model => model.Status == "unloaded" && model.Source == "preset")) return false;
        try { return (await client.PropsAsync(token: token).ConfigureAwait(false)).ModelsAutoload == true; }
        catch (Exception) { return false; }
    }

    /// <summary>provider.ts <c>configuredContextWindow</c>: <c>--ctx-size</c>, <c>-c</c> or <c>-ctx</c> in the model's server arguments.</summary>
    internal static double? ConfiguredContextWindow(LlamaModelInfo model)
    {
        var args = model.Args;
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (args[index] is not ("--ctx-size" or "-c" or "-ctx")) continue;
            if (JsNumber(args[index + 1]) is { } window && window > 0 && window <= 9007199254740991 && Math.Floor(window) == window) return window;
        }
        return null;
    }

    /// <summary>JavaScript <c>Number(text)</c> for the decimal forms server arguments use (NaN as null).</summary>
    internal static double? JsNumber(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return 0;
        return double.TryParse(trimmed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    /// <summary>provider.ts <c>contextWindowOf</c>: the runtime <c>n_ctx</c>, the configured size, the cached value, <c>n_ctx_train</c>,
    /// else 128000.</summary>
    internal static double ContextWindowOf(LlamaModelInfo model, double? cachedContextWindow = null)
    {
        if (model.Meta("n_ctx") is { } runtime && runtime > 0) return runtime;
        if (ConfiguredContextWindow(model) is { } configured) return configured;
        if (cachedContextWindow is { } cached && cached > 0) return cached;
        return model.Meta("n_ctx_train") is { } training && training > 0 ? training : 128000;
    }

    /// <summary>provider.ts <c>isDecisionModel</c>: llama.cpp 0.6.0 and later list <c>decisions</c> in
    /// <c>architecture.output_modalities</c>, also for unloaded and sleeping models.</summary>
    internal static bool IsDecisionModel(LlamaModelInfo model) => model.OutputModality("decisions");

    /// <summary>provider.ts <c>isChatModel</c>: decision-only models cannot generate text.</summary>
    internal static bool IsChatModel(LlamaModelInfo model) => !IsDecisionModel(model) || model.OutputModality("text");

    private static JsonObject FreeCost() => new() { ["input"] = 0, ["output"] = 0, ["cacheRead"] = 0, ["cacheWrite"] = 0 };

    /// <summary>provider.ts <c>toPiClassifierModel</c>: decision models answer through System One at the <c>/v1</c> URL; chat models
    /// through <c>llama-cpp-classify</c> at the server URL.</summary>
    internal static JsonObject ToClassifierModel(LlamaModelInfo model, string serverUrl, double? cachedContextWindow = null)
    {
        var decision = IsDecisionModel(model);
        return new()
        {
            ["type"] = "classifier", ["id"] = model.Id, ["name"] = model.Id, ["api"] = decision ? SystemOneApi : LlamaClassifyApi, ["provider"] = ProviderId,
            ["baseUrl"] = decision ? LlamaClient.InferenceUrl(serverUrl) : serverUrl, ["input"] = new JsonArray("text"), ["cost"] = FreeCost(),
            ["contextWindow"] = Number(ContextWindowOf(model, cachedContextWindow))
        };
    }

    private static bool IsLlamaClassifierModel(JsonObject model) =>
        JsonTree.String(model, "type") == "classifier" && JsonTree.String(model, "api") is LlamaClassifyApi or SystemOneApi;

    /// <summary>provider.ts <c>toPiModel</c>: an openai-completions chat model; a chat template that reads <c>enable_thinking</c> makes it
    /// a reasoning model with the qwen-chat-template thinking format.</summary>
    internal static JsonObject ToChatModel(LlamaModelInfo model, string serverUrl, LlamaServerProps? props = null, double? cachedContextWindow = null)
    {
        var contextWindow = Number(ContextWindowOf(model, cachedContextWindow));
        var reasoning = props?.ChatTemplate?.Contains("enable_thinking", StringComparison.Ordinal) == true;
        var json = new JsonObject
        {
            ["id"] = model.Id, ["name"] = model.Id, ["api"] = "openai-completions", ["provider"] = ProviderId, ["baseUrl"] = LlamaClient.InferenceUrl(serverUrl),
            ["reasoning"] = reasoning
        };
        if (reasoning)
            json["thinkingLevelMap"] = new JsonObject { ["off"] = "off", ["minimal"] = null, ["low"] = null, ["medium"] = "medium", ["high"] = null, ["xhigh"] = null };
        json["input"] = model.InputModality("image") ? new JsonArray("text", "image") : new JsonArray("text");
        json["cost"] = FreeCost();
        json["contextWindow"] = contextWindow.DeepClone();
        json["maxTokens"] = contextWindow;
        var compat = new JsonObject
        {
            ["supportsStore"] = false, ["supportsDeveloperRole"] = false, ["supportsReasoningEffort"] = false, ["supportsUsageInStreaming"] = true,
            ["supportsStrictMode"] = false, ["maxTokensField"] = "max_tokens"
        };
        if (reasoning) compat["thinkingFormat"] = "qwen-chat-template";
        json["compat"] = compat;
        return json;
    }

    /// <summary>A JSON number written as JavaScript writes it (integers without a fraction).</summary>
    private static JsonNode Number(double value) => value == Math.Floor(value) && Math.Abs(value) < 9007199254740992 ? JsonValue.Create((long)value) : JsonValue.Create(value);

    // ---------------------------------------------------------------------------------------------------------------- auth

    /// <summary>provider.ts <c>credentialServerUrl</c>: the credential env's <c>LLAMA_BASE_URL</c>, normalized (an invalid URL throws).</summary>
    internal static string? CredentialServerUrl(IReadOnlyDictionary<string, string>? environment) =>
        environment?.GetValueOrDefault("LLAMA_BASE_URL") is { } value && value.Trim().Length > 0 ? LlamaClient.NormalizeServerUrl(value) : null;

    /// <summary>provider.ts <c>resolveServerUrl</c>: the credential's URL, else the <c>LLAMA_BASE_URL</c> environment variable.</summary>
    internal static string? ResolveServerUrl(ProviderStoredCredentialView? credential, Func<string, string?> environment)
    {
        var configured = CredentialServerUrl(credential?.Environment) ?? environment("LLAMA_BASE_URL")?.Trim();
        return string.IsNullOrEmpty(configured) ? null : LlamaClient.NormalizeServerUrl(configured);
    }

    /// <summary>auth.apiKey.check: the source label when a server URL is configured.</summary>
    internal static string? Check(ProviderStoredCredentialView? credential, Func<string, string?> environment) =>
        ResolveServerUrl(credential, environment) is null ? null : credential is not null ? "stored credential" : "LLAMA_BASE_URL";

    /// <summary>auth.apiKey.resolve: the credential's key, else <c>LLAMA_API_KEY</c>, else <c>local</c>, for the server's <c>/v1</c> URL.</summary>
    internal static LlamaAuth? Resolve(ProviderStoredCredentialView? credential, Func<string, string?> environment) =>
        ResolveServerUrl(credential, environment) is not { } serverUrl ? null
            : new(credential?.Key ?? environment("LLAMA_API_KEY") ?? "local", serverUrl, credential is not null ? "stored credential" : "LLAMA_BASE_URL");

    /// <summary>auth.apiKey.login: the server URL (default the <c>LLAMA_BASE_URL</c> environment variable, else
    /// <see cref="DefaultServerUrl"/>) and an optional API key; the router's catalog must be readable with them. The credential stores
    /// the key (when one was entered) and the URL as <c>env.LLAMA_BASE_URL</c>.</summary>
    internal static async Task<(string? Key, IReadOnlyDictionary<string, string> Environment)> LoginAsync(IProviderAuthInteraction interaction,
        Func<string, string?> environment, CancellationToken token, HttpMessageInvoker? http = null)
    {
        var enteredUrl = await interaction.PromptAsync(new(AuthPromptKind.Text, "llama.cpp server URL", environment("LLAMA_BASE_URL") ?? DefaultServerUrl), token).ConfigureAwait(false);
        var fallback = environment("LLAMA_BASE_URL");
        var serverUrl = LlamaClient.NormalizeServerUrl(enteredUrl.Trim() is { Length: > 0 } entered ? entered : !string.IsNullOrEmpty(fallback) ? fallback : DefaultServerUrl);
        var apiKey = (await interaction.PromptAsync(new(AuthPromptKind.Secret, "API key (optional)"), token).ConfigureAwait(false)).Trim();
        await new LlamaClient(serverUrl, apiKey.Length > 0 ? apiKey : null, http).ListAsync(token: token).ConfigureAwait(false);
        return (apiKey.Length > 0 ? apiKey : null, new Dictionary<string, string>(StringComparer.Ordinal) { ["LLAMA_BASE_URL"] = serverUrl });
    }
}
