// PiSharp-only (no upstream Pi source): the media tools of `pisharp durable` (images, video, speech, transcription, music, embeddings,
// rerank and decisions), over the CLI's model registry and credentials. Settings:
//   "media": { "enabled": true, "outputDir": ".pi/media", "deferSpecializedTools": true,
//              "<kind>": { "model": "provider/id", "options": { <tool arguments> } } }
// where <kind> is image, video, speech, transcription, music, embedding, rerank or decision. set_media_defaults writes the <kind>
// entries to the global or project settings file.
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PiSharp.AI.Catalogs;
using PiSharp.AI.Media;
using PiSharp.AI.ModelOperations;
using PiSharp.Cli.Models;
using PiSharp.Cli.Pi;
using PiSharp.Contracts;
using PiSharp.Durable.Tools;

namespace PiSharp.Cli.Durable;

internal static class DurableMedia
{
    /// <summary>
    /// The media tool options for a session, or null when <c>media.enabled</c> is false or no provider with a media model is configured
    /// (then the tools stay out of the prompt). Auth is the chat models' (auth.json credentials with OAuth refresh, models.json keys and
    /// headers, the environment), resolved through one of the provider's chat models, which also gives the provider's base URL.
    /// </summary>
    internal static async Task<MediaToolsOptions?> CreateAsync(PiSettings settings, DurableModels models, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(models);
        var section = settings.Merged["media"] as JsonObject;
        if (Flag(section, "enabled") == false) return null;
        var registry = Create(models);
        if ((await registry.GetAvailableAsync(null, cancellationToken).ConfigureAwait(false)).IsEmpty) return null;
        var initial = new Dictionary<MediaKind, MediaDefault>();
        foreach (var kind in Enum.GetValues<MediaKind>())
            if (section?[MediaCatalog.Name(kind)] is JsonObject entry) initial[kind] = ReadDefault(entry);
        return new MediaToolsOptions
        {
            Media = registry,
            OutputDirectory = Text(section, "outputDir") ?? ".pi/media",
            DeferSpecializedTools = Flag(section, "deferSpecializedTools") ?? true,
            Defaults = new MediaDefaults(initial) { Persist = (kind, value, scope, _) => { Save(settings, kind, value, scope); return Task.CompletedTask; } },
        };
    }

    private static MediaDefault ReadDefault(JsonObject entry) => new(Text(entry, "model"),
        entry["options"] is JsonObject options ? (PiSharp.Durable.Chord.JsonObjectValue)PiSharp.Durable.Chord.JsonValue.Parse(options.ToJsonString())
            : PiSharp.Durable.Chord.JsonObjectValue.Empty);

    /// <summary>Writes a kind's defaults into the scope's <c>media</c> object (other keys kept; an empty object is removed). It runs after
    /// <see cref="MediaDefaults"/> has applied the change to the session, so a file that cannot be written leaves the change in effect,
    /// and the error says so.</summary>
    internal static void Save(PiSettings settings, MediaKind kind, MediaDefault? value, string scope)
    {
        var layer = scope == "project" ? settings.Project : settings.Global;
        var media = layer["media"] is JsonObject current ? (JsonObject)current.DeepClone() : [];
        var name = MediaCatalog.Name(kind);
        if (value is null) media.Remove(name);
        else
        {
            var entry = new JsonObject();
            if (value.Model is { } model) entry["model"] = model;
            if (value.Options.Count > 0) entry["options"] = JsonNode.Parse(value.Options.ToString());
            media[name] = entry;
        }
        settings.SetField(scope, "media", media.Count == 0 ? null : media);
        if (settings.LastWriteError is { } error) throw new InvalidOperationException($"The defaults apply to this session but were not saved: {error}");
    }

    /// <summary>A media registry over the CLI registry: the built-in media catalog plus the registry's image models (OpenRouter's).</summary>
    internal static MediaRegistry Create(DurableModels models)
    {
        var registry = models.Registry;
        var environment = MediaAuth.Environment(models.Runtime.ReadEnvironment);
        // Each provider's first chat model, which carries its auth and base URL; built once, not scanned per request.
        var chatModels = new Lazy<Dictionary<string, RegistryModel>>(() =>
        {
            var table = new Dictionary<string, RegistryModel>(StringComparer.Ordinal);
            foreach (var entry in registry.GetAll())
                if (entry.Type == CatalogModelType.Chat && !VirtualModels.IsVirtual(entry)) table.TryAdd(entry.Provider, entry);
            return table;
        });
        RegistryModel? ChatModel(string provider) => chatModels.Value.GetValueOrDefault(provider);
        ValueTask<ProviderAuthResult?> AuthAsync(ProviderAuthRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A ChatGPT subscription token reaches only the Codex Responses route, not OpenAI's media endpoints.
            if (request.Provider == "openai" && string.IsNullOrEmpty(request.ApiKey) && registry.IsUsingOAuth("openai"))
                return ValueTask.FromResult<ProviderAuthResult?>(null);
            if (string.IsNullOrEmpty(request.ApiKey) && ChatModel(request.Provider) is { } entry)
            {
                if (request.Check) return ValueTask.FromResult(registry.HasConfiguredAuth(request.Provider) ? new ProviderAuthResult(null) : null);
                if (registry.ResolveRequestAuth(entry, out _) is { ApiKey: { Length: > 0 } key } resolved)
                    return ValueTask.FromResult<ProviderAuthResult?>(new ProviderAuthResult(key)
                    {
                        BaseUrl = entry.BaseUrl.Length == 0 ? null : entry.BaseUrl,
                        Headers = [.. (resolved.Headers ?? ImmutableDictionary<string, string>.Empty).Select(pair => KeyValuePair.Create(pair.Key, (string?)pair.Value))],
                    });
            }
            return environment(request, cancellationToken);
        }
        var media = new MediaRegistry(AuthAsync, new MediaRegistryOptions
        {
            Http = models.HttpHandler is { } handler ? new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan } : null,
            TimeProvider = models.Runtime.Time,
            ProviderBaseUrl = provider => ChatModel(provider)?.BaseUrl is { Length: > 0 } baseUrl ? baseUrl : null,
        });
        var images = new List<MediaModel>();
        foreach (var entry in registry.GetAllOfType(CatalogModelType.Image))
            try
            {
                if (OperationModel.FromJson(JsonData.Parse(entry.ToJsonString())) is ImageModel image) images.Add(MediaCatalog.FromImageModel(image));
            }
            catch (FormatException) { }
        media.AddModels(images);
        return media;
    }

    private static string? Text(JsonObject? section, string name) =>
        section?[name] is JsonValue value && value.TryGetValue<string>(out var text) && text.Trim().Length > 0 ? text.Trim() : null;

    private static bool? Flag(JsonObject? section, string name) =>
        section?[name] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;
}
