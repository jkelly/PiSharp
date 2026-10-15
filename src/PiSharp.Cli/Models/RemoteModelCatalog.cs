// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/coding-agent/src/core/remote-catalog-provider.ts (withRemoteCatalog,
// withPersonalizedCatalog), packages/coding-agent/src/core/models-store.ts, packages/ai/src/models-store.ts, packages/ai/src/models.ts
// (refresh, withKnownModelTypes, RefreshModelsContext.baseUrl), packages/coding-agent/src/utils/management-http.ts (fetchWithRetry),
// packages/coding-agent/src/utils/pi-user-agent.ts and packages/coding-agent/src/modes/interactive/model-catalog-refresh.ts.
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI.Catalogs;

namespace PiSharp.Cli.Models;

/// <summary>Source ModelsStoreEntry: persisted models of every type plus the remote validators.</summary>
internal sealed record ModelsStoreEntry(JsonArray Models, double? LastModified = null, double? CheckedAt = null, string? ETag = null)
{
    internal JsonObject ToJson()
    {
        var json = new JsonObject { ["models"] = Models.DeepClone() };
        if (LastModified is { } lastModified) json["lastModified"] = lastModified;
        if (CheckedAt is { } checkedAt) json["checkedAt"] = checkedAt;
        if (ETag is not null) json["etag"] = ETag;
        return json;
    }
    internal static ModelsStoreEntry? FromJson(JsonNode? node) => node is JsonObject json && json["models"] is JsonArray models
        ? new((JsonArray)models.DeepClone(), JsonTree.Number(json, "lastModified"), JsonTree.Number(json, "checkedAt"), JsonTree.String(json, "etag"))
        : null;
}

/// <summary>Source ModelsStore: persistent model catalogs keyed by provider id.</summary>
internal interface IModelsStore
{
    Task<ModelsStoreEntry?> ReadAsync(string providerId, CancellationToken cancellationToken);
    Task WriteAsync(string providerId, ModelsStoreEntry entry, CancellationToken cancellationToken);
    Task DeleteAsync(string providerId, CancellationToken cancellationToken);
}

internal sealed class InMemoryModelsStore : IModelsStore
{
    private readonly ConcurrentDictionary<string, JsonObject> entries = new(StringComparer.Ordinal);
    public Task<ModelsStoreEntry?> ReadAsync(string providerId, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(entries.TryGetValue(providerId, out var entry) ? ModelsStoreEntry.FromJson(entry) : null); }
    public Task WriteAsync(string providerId, ModelsStoreEntry entry, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); entries[providerId] = entry.ToJson(); return Task.CompletedTask; }
    public Task DeleteAsync(string providerId, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); entries.TryRemove(providerId, out _); return Task.CompletedTask; }
}

/// <summary>Source FileModelsStore: <c>models-store.json</c> next to models.json, locked with <c>models-store.json.lock</c> for writes and
/// written as two-space indented JSON. A missing file reads as empty and is not created by reads.</summary>
internal sealed class FileModelsStore(string path) : IModelsStore
{
    private readonly SemaphoreSlim gate = new(1, 1);
    internal string Path { get; } = System.IO.Path.GetFullPath(path);

    private JsonObject ReadDocument()
    {
        if (!File.Exists(Path)) return [];
        var text = File.ReadAllText(Path, Encoding.UTF8);
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        if (text.Length == 0) return [];
        JsonNode? parsed;
        try { parsed = JsonTree.Parse(text); }
        catch (System.Text.Json.JsonException error) { throw new System.Text.Json.JsonException(PiSharp.Contracts.Compatibility.JsJsonSyntax.Describe(text, error.Message), error); }
        return parsed as JsonObject ?? throw new InvalidDataException("Invalid models-store.json: expected an object");
    }

    public async Task<ModelsStoreEntry?> ReadAsync(string providerId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return ModelsStoreEntry.FromJson(ReadDocument()[providerId]); }
        finally { gate.Release(); }
    }

    public Task WriteAsync(string providerId, ModelsStoreEntry entry, CancellationToken cancellationToken) =>
        ModifyAsync(document => document[providerId] = entry.ToJson(), cancellationToken);

    public Task DeleteAsync(string providerId, CancellationToken cancellationToken) =>
        ModifyAsync(document => document.Remove(providerId), cancellationToken);

    private async Task ModifyAsync(Action<JsonObject> change, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            await using var fileLock = await AcquireLockAsync(cancellationToken).ConfigureAwait(false);
            var document = ReadDocument(); change(document);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(document, new JsonSerializerOptions
                { WriteIndented = true, IndentSize = 2, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            await File.WriteAllBytesAsync(Path, bytes, CancellationToken.None).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task<FileStream> AcquireLockAsync(CancellationToken cancellationToken)
    {
        var lockPath = Path + ".lock"; var started = DateTime.UtcNow; var retry = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose); }
            catch (IOException) when (File.Exists(lockPath) || Directory.Exists(lockPath))
            {
                var stamp = Directory.Exists(lockPath) ? Directory.GetLastWriteTimeUtc(lockPath) : File.GetLastWriteTimeUtc(lockPath);
                if (DateTime.UtcNow - stamp > TimeSpan.FromSeconds(30))
                { try { if (Directory.Exists(lockPath)) Directory.Delete(lockPath); else File.Delete(lockPath); } catch (IOException) { } continue; }
                if (DateTime.UtcNow - started > TimeSpan.FromSeconds(30)) throw new IOException("Failed to acquire models store lock");
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(10 * Math.Pow(2, retry++), 1000)), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>What one provider refresh may do (RefreshModelsContext). <see cref="Publish"/> persists (or deletes with
/// <see cref="ModelsPublication.Delete"/>) and then runs the update; it returns false when the refresh was superseded.</summary>
internal sealed record ModelsPublication(ModelsStoreEntry? Persist = null, bool Delete = false, Action? Update = null);

/// <param name="CredentialKey">The effective credential's key (an OAuth credential's access token) for the network phase.</param>
/// <param name="BaseUrl">RefreshModelsContext.baseUrl: the registered provider's base URL, including models.json and extension overrides.</param>
internal sealed record RemoteRefreshContext(ModelsStoreEntry? Stored, bool AllowNetwork, bool? Force,
    Func<ModelsPublication, Task<bool>> Publish, CancellationToken CancellationToken, string? CredentialKey = null, string? BaseUrl = null);

/// <summary>A refreshable catalog layer over one built-in provider's models.</summary>
internal interface ICatalogOverlay
{
    /// <summary>The provider's models with this layer applied.</summary>
    List<RegistryModel> Apply(IReadOnlyList<RegistryModel> baseline);
    Task RefreshAsync(RemoteRefreshContext context);
    /// <summary>Whether the network phase needs a resolved credential (models.ts refresh skips it without one).</summary>
    bool RequiresCredential { get; }
}

/// <summary>Source withRemoteCatalog: a persisted pi.dev catalog overlay on one static built-in provider.</summary>
internal sealed class RemoteCatalogOverlay : ICatalogOverlay
{
    internal const string DefaultCatalogBaseUrl = "https://pi.dev";
    internal static readonly TimeSpan AttemptTimeout = TimeSpan.FromMilliseconds(4_000);
    internal const double RefreshIntervalMilliseconds = 4 * 60 * 60 * 1000;
    internal static readonly string[] ModelTypes = ["chat", "image", "classifier"];
    private static readonly HashSet<int> RetryableStatus = [408, 425, 429, 500, 502, 503, 504];

    private readonly string providerId;
    private readonly string catalogBaseUrl;
    private readonly long? localGeneratedAt;
    private readonly Func<HttpClient> createClient;
    private readonly Func<DateTimeOffset> now;
    private volatile RegistryModel[] dynamicModels = [];

    internal RemoteCatalogOverlay(string providerId, Func<HttpClient> createClient, string? catalogBaseUrl = null, long? localGeneratedAt = null,
        Func<DateTimeOffset>? now = null)
    {
        this.providerId = providerId; this.createClient = createClient; this.catalogBaseUrl = catalogBaseUrl ?? DefaultCatalogBaseUrl;
        this.localGeneratedAt = localGeneratedAt; this.now = now ?? (() => DateTimeOffset.UtcNow);
    }

    internal IReadOnlyList<RegistryModel> Dynamic => dynamicModels;

    /// <summary>Source mergeModels: dynamic entries replace baseline entries of the same type and id in place; new ones are appended.</summary>
    internal static List<RegistryModel> Merge(IReadOnlyList<RegistryModel> baseline, IReadOnlyList<RegistryModel> dynamic)
    {
        var merged = new List<RegistryModel>(); var index = new Dictionary<(CatalogModelType, string), int>();
        foreach (var model in baseline.Concat(dynamic))
        {
            if (index.TryGetValue((model.Type, model.Id), out var at)) merged[at] = model;
            else { index[(model.Type, model.Id)] = merged.Count; merged.Add(model); }
        }
        return merged;
    }

    public List<RegistryModel> Apply(IReadOnlyList<RegistryModel> baseline) => Merge(baseline, dynamicModels);
    public bool RequiresCredential => false;

    private static bool SupportedType(JsonObject model) =>
        !model.ContainsKey("type") || JsonTree.String(model, "type") is { } type && ModelTypes.Contains(type, StringComparer.Ordinal);

    /// <summary>Source parseCatalog: an array, <c>{ models: [...] }</c>, or an object of entries; entries need an <c>id</c>.</summary>
    internal static List<RegistryModel> ParseCatalog(string providerId, JsonNode? value)
    {
        IEnumerable<JsonNode?>? entries = value switch
        {
            JsonArray array => array,
            JsonObject obj when obj["models"] is JsonArray models => models,
            JsonObject obj => obj.Select(pair => pair.Value),
            _ => null
        };
        if (entries is null) throw new InvalidOperationException($"Invalid model catalog for provider \"{providerId}\"");
        var result = new List<RegistryModel>();
        foreach (var entry in entries.OfType<JsonObject>().Where(entry => entry.ContainsKey("id")).Where(SupportedType))
        {
            var copy = JsonTree.CloneObject(entry); copy["provider"] = providerId;
            try { result.Add(RegistryModel.FromJson(copy)); }
            catch (ArgumentException) { } // An entry this version cannot represent is skipped, like an unknown type.
        }
        return result;
    }

    /// <summary>Source isNewer: whether a pi.dev catalog is newer than the model data shipped with pi.</summary>
    internal static bool IsNewer(double? lastModified, long? localGeneratedAt) =>
        localGeneratedAt is not { } local || lastModified is { } modified && modified > local;

    /// <summary>Source remoteModels: the stored overlay only when it is newer than the built-in catalog.</summary>
    private List<RegistryModel> RemoteModels(ModelsStoreEntry? entry) =>
        entry is not null && IsNewer(entry.LastModified, localGeneratedAt) ? ParseStored(entry) : [];

    internal static List<RegistryModel> ParseStored(ModelsStoreEntry entry)
    {
        var models = new List<RegistryModel>();
        foreach (var node in entry.Models.OfType<JsonObject>())
            try { if (SupportedType(node)) models.Add(RegistryModel.FromJson(node)); } catch (ArgumentException) { }
        return models;
    }

    /// <summary>Source withRemoteCatalog refreshModels.</summary>
    public async Task RefreshAsync(RemoteRefreshContext context)
    {
        var stored = context.Stored; var token = context.CancellationToken;
        var restored = RemoteModels(stored).Where(model => model.Provider == providerId).ToArray();
        if (!await context.Publish(new(Update: () => dynamicModels = restored)).ConfigureAwait(false)) return;
        if (!context.AllowNetwork || token.IsCancellationRequested) return;
        if (context.Force != true && stored?.CheckedAt is { } checkedAt && stored.LastModified is not null &&
            now().ToUnixTimeMilliseconds() - checkedAt < RefreshIntervalMilliseconds) return;
        // Only revalidate when a cached body backs the validator, so a 304 can never leave the overlay empty.
        var validator = stored is not null && stored.Models.Count > 0 ? stored.ETag : null;
        using var response = await RequestCatalogAsync(providerId, catalogBaseUrl, createClient, validator, token).ConfigureAwait(false);
        if (token.IsCancellationRequested) return;
        double checkedNow = now().ToUnixTimeMilliseconds();
        if (response.StatusCode == HttpStatusCode.NotModified && stored is not null)
        { await context.Publish(new(Persist: stored with { CheckedAt = checkedNow })).ConfigureAwait(false); return; }
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotImplemented)
        {
            await context.Publish(new(Persist: (stored ?? new([])) with { CheckedAt = checkedNow, LastModified = 0, ETag = null })).ConfigureAwait(false);
            return;
        }
        if (!response.IsSuccessStatusCode)
        {
            // Transient failure: the cached body and its validator stay valid for the next revalidation.
            await context.Publish(new(Persist: (stored ?? new([])) with { CheckedAt = checkedNow })).ConfigureAwait(false);
            throw new InvalidOperationException($"Model catalog request failed for {providerId}: {(int)response.StatusCode}");
        }
        var (refreshed, lastModified, etag) = await ReadCatalogAsync(providerId, response, token).ConfigureAwait(false);
        if (token.IsCancellationRequested) return;
        var entry = new ModelsStoreEntry(new JsonArray([.. refreshed.Select(model => (JsonNode?)model.CloneJson())]), lastModified, checkedNow, etag);
        var published = RemoteModels(entry).ToArray();
        await context.Publish(new(Persist: entry, Update: () => dynamicModels = published)).ConfigureAwait(false);
    }

    /// <summary>Source readCatalog: a successful pi.dev catalog response; a missing or invalid Last-Modified header is 0.</summary>
    internal static async Task<(List<RegistryModel> Models, double LastModified, string? ETag)> ReadCatalogAsync(string providerId,
        HttpResponseMessage response, CancellationToken token)
    {
        var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        return (ParseCatalog(providerId, JsonTree.Parse(body)), response.Content.Headers.LastModified?.ToUnixTimeMilliseconds() ?? 0,
            response.Headers.ETag?.ToString());
    }

    /// <summary>Source requestCatalog: the provider's pi.dev catalog of every supported type, revalidated with a validator when given.</summary>
    internal static Task<HttpResponseMessage> RequestCatalogAsync(string providerId, string catalogBaseUrl, Func<HttpClient> createClient, string? validator,
        CancellationToken token) =>
        FetchWithRetryAsync(new Uri(new Uri(catalogBaseUrl), "/api/models/providers/" + Uri.EscapeDataString(providerId) + "?types=" +
            Uri.EscapeDataString(string.Join(",", ModelTypes))), createClient, validator, token);

    /// <summary>Source fetchWithRetry with two retries on transport failures, attempt timeouts and retryable statuses.</summary>
    private static async Task<HttpResponseMessage> FetchWithRetryAsync(Uri url, Func<HttpClient> createClient, string? validator, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            attemptTimeout.CancelAfter(AttemptTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("accept", "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            if (validator is not null) request.Headers.TryAddWithoutValidation("if-none-match", validator);
            try
            {
                using var client = createClient();
                var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, attemptTimeout.Token).ConfigureAwait(false);
                if (!(RetryableStatus.Contains((int)response.StatusCode) && attempt < 2)) return response;
                response.Dispose();
            }
            catch (Exception) when (!token.IsCancellationRequested && attempt < 2) { }
        }
    }

    /// <summary>Source getPiUserAgent with the runtime field naming .NET.</summary>
    internal static string UserAgent { get; } = string.Create(CultureInfo.InvariantCulture,
        $"pi/1.2.0 ({(OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : "linux")}; dotnet/{Environment.Version}; {RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()})");
}

/// <summary>
/// Source withPersonalizedCatalog (Pi 1.2.0, used for OpenRouter): on each network refresh the catalog (built-in models plus a newer
/// pi.dev catalog) is passed through <c>personalize</c> and the result is saved. The saved entry holds the personalized list, not the
/// pi.dev catalog, and replaces the catalog, offline too. There is no refresh interval or etag revalidation. An empty list (nothing
/// saved yet, or an empty catalog an older pi saved) falls back to the built-in models.
/// </summary>
internal sealed class PersonalizedCatalogOverlay(string providerId, Func<HttpClient> createClient, Func<IReadOnlyList<RegistryModel>> builtinModels,
    Func<IReadOnlyList<RegistryModel>, RemoteRefreshContext, Task<IReadOnlyList<RegistryModel>>> personalize, string? catalogBaseUrl = null,
    long? localGeneratedAt = null, Func<DateTimeOffset>? now = null) : ICatalogOverlay
{
    private readonly string catalogBaseUrl = catalogBaseUrl ?? RemoteCatalogOverlay.DefaultCatalogBaseUrl;
    private readonly Func<DateTimeOffset> now = now ?? (() => DateTimeOffset.UtcNow);
    private volatile RegistryModel[] personalModels = [];

    internal IReadOnlyList<RegistryModel> Personal => personalModels;

    public bool RequiresCredential => true;

    public List<RegistryModel> Apply(IReadOnlyList<RegistryModel> baseline) => personalModels.Length > 0 ? [.. personalModels] : [.. baseline];

    public async Task RefreshAsync(RemoteRefreshContext context)
    {
        var token = context.CancellationToken;
        var restored = context.Stored is null ? [] : RemoteCatalogOverlay.ParseStored(context.Stored).Where(model => model.Provider == providerId).ToArray();
        if (!await context.Publish(new(Update: () => personalModels = restored)).ConfigureAwait(false)) return;
        if (!context.AllowNetwork || token.IsCancellationRequested) return;
        using var response = await RemoteCatalogOverlay.RequestCatalogAsync(providerId, catalogBaseUrl, createClient, null, token).ConfigureAwait(false);
        if (token.IsCancellationRequested) return;
        IReadOnlyList<RegistryModel> remote = [];
        if (response.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.NotImplemented))
        {
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Model catalog request failed for {providerId}: {(int)response.StatusCode}");
            var catalog = await RemoteCatalogOverlay.ReadCatalogAsync(providerId, response, token).ConfigureAwait(false);
            if (RemoteCatalogOverlay.IsNewer(catalog.LastModified, localGeneratedAt)) remote = catalog.Models;
        }
        var personalized = (await personalize(RemoteCatalogOverlay.Merge(builtinModels(), remote), context).ConfigureAwait(false)).ToArray();
        if (token.IsCancellationRequested) return;
        await context.Publish(new(Persist: new(new JsonArray([.. personalized.Select(model => (JsonNode?)model.CloneJson())]), CheckedAt: now().ToUnixTimeMilliseconds()),
            Update: () => personalModels = personalized)).ConfigureAwait(false);
    }
}

/// <summary>Source refreshModelCatalogs: concurrent interactive all-catalog refreshes share one run; each caller cancels only its wait,
/// and the shared run is cancelled when its last waiter leaves.</summary>
internal sealed class ModelCatalogRefreshCoordinator
{
    private sealed class Active(CancellationTokenSource source, Task task) { internal readonly CancellationTokenSource Source = source; internal readonly Task Task = task; internal int Waiters; }
    private readonly object gate = new();
    private readonly ConditionalWeakTable<object, Active> active = [];
    internal static ModelCatalogRefreshCoordinator Shared { get; } = new();

    internal async Task RefreshAsync(object runtime, Func<CancellationToken, Task> refresh, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Active current;
        lock (gate)
        {
            if (!active.TryGetValue(runtime, out current!))
            {
                var source = new CancellationTokenSource();
                Active? created = null;
                var task = Task.Run(async () =>
                {
                    try { await refresh(source.Token).ConfigureAwait(false); }
                    finally { lock (gate) if (created is not null && active.TryGetValue(runtime, out var now) && ReferenceEquals(now, created)) active.Remove(runtime); }
                }, CancellationToken.None);
                created = current = new(source, task);
                active.Add(runtime, current);
            }
            current.Waiters++;
        }
        try { await current.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        finally
        {
            lock (gate)
            {
                if (--current.Waiters == 0 && active.TryGetValue(runtime, out var now) && ReferenceEquals(now, current)) current.Source.Cancel();
            }
        }
    }
}
