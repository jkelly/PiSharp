// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/extensions/llama/client.ts.
using System.Collections.Immutable;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Cli.Pi;

namespace PiSharp.Cli.Llama;

/// <summary>client.ts <c>LlamaModelInfo</c>: one entry of the router's <c>GET /models</c>, kept as the server's JSON object.</summary>
internal sealed class LlamaModelInfo(JsonObject raw)
{
    internal JsonObject Raw { get; } = raw;
    internal string Id => (string)Raw["id"]!;
    private JsonObject? StatusObject => Raw["status"] as JsonObject;
    /// <summary><c>status.value</c>: unloaded, loading, loaded, downloading or sleeping.</summary>
    internal string Status => StatusObject?["value"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    internal ImmutableArray<string> Args => StatusObject?["args"] is JsonArray args
        ? [.. args.Select(arg => arg is JsonValue value && value.TryGetValue<string>(out var text) ? text : arg?.ToJsonString() ?? "null")] : [];
    internal bool Failed => StatusObject?["failed"] is JsonValue value && value.TryGetValue<bool>(out var failed) && failed;
    internal string? ExitCode => StatusObject?["exit_code"] is JsonValue value && value.GetValueKind() == JsonValueKind.Number ? value.ToJsonString() : null;
    internal JsonNode? Progress => StatusObject?["progress"];
    internal string? Source => Raw["source"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    internal bool InputModality(string name) => Modality("input_modalities", name);
    internal bool OutputModality(string name) => Modality("output_modalities", name);
    private bool Modality(string list, string name) => (Raw["architecture"] as JsonObject)?[list] is JsonArray items &&
        items.Any(item => item is JsonValue value && value.TryGetValue<string>(out var text) && text == name);
    /// <summary>A positive <c>meta</c> number (<c>n_ctx</c>, <c>n_ctx_train</c>), as JavaScript's truthy check reads it.</summary>
    internal double? Meta(string name) => (Raw["meta"] as JsonObject)?[name] is JsonValue value && value.GetValueKind() == JsonValueKind.Number &&
        JsonNumber(value) is var number && number != 0 && !double.IsNaN(number) ? number : null;

    internal static double JsonNumber(JsonValue value) => value.TryGetValue<double>(out var number) ? number : double.Parse(value.ToJsonString(), CultureInfo.InvariantCulture);

    /// <summary>client.ts <c>isModelInfo</c>: a string id and a string <c>status.value</c>.</summary>
    internal static bool IsModelInfo(JsonNode? value) => value is JsonObject entry && entry["id"] is JsonValue id && id.GetValueKind() == JsonValueKind.String &&
        entry["status"] is JsonObject status && status["value"] is JsonValue state && state.GetValueKind() == JsonValueKind.String;
}

/// <summary>client.ts <c>LlamaServerProps</c>.</summary>
internal sealed record LlamaServerProps(bool? ModelsAutoload = null, string? ChatTemplate = null);

/// <summary>client.ts <c>LlamaModelEvent</c>: one <c>/models/sse</c> event.</summary>
internal sealed record LlamaModelEvent(string Model, string Event, JsonNode? Data);

/// <summary>client.ts <c>LlamaProgress</c>. <see cref="HasRatio"/> and <see cref="HasDetail"/> say whether the update carries the field
/// (JavaScript's own properties, which <c>Object.assign</c> copies even when undefined).</summary>
internal sealed record LlamaProgress(string Message, double? Ratio = null, string? Detail = null)
{
    public bool HasRatio { get; init; }
    public bool HasDetail { get; init; }
}

/// <summary>A request that never reached the llama.cpp server or timed out (upstream's fetch failures and timeout aborts).</summary>
internal sealed class LlamaConnectionException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>client.ts <c>LlamaClient</c>: the router's management API (<c>/models</c>, <c>/props</c>, load, unload and download,
/// <c>/models/sse</c>) with a bearer key. Every request but the event stream times out after 15 seconds.</summary>
internal sealed class LlamaClient
{
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly HttpClient SharedHttp = new();
    private readonly string? apiKey;
    private readonly HttpMessageInvoker http;

    internal string ServerUrl { get; }

    internal LlamaClient(string serverUrl, string? apiKey = null, HttpMessageInvoker? http = null)
    {
        ServerUrl = NormalizeServerUrl(serverUrl);
        this.apiKey = apiKey;
        this.http = http ?? SharedHttp;
    }

    /// <summary>client.ts <c>normalizeLlamaServerUrl</c>: http or https only, no query or fragment, no trailing slash or <c>/v1</c>.</summary>
    internal static string NormalizeServerUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var url)) throw new InvalidOperationException("Invalid URL");
        if (url.Scheme is not ("http" or "https")) throw new InvalidOperationException("Server URL must use http or https");
        var path = url.AbsolutePath;
        var end = path.Length; while (end > 0 && path[end - 1] == '/') end--;
        path = path[..end];
        if (path.EndsWith("/v1", StringComparison.Ordinal)) path = path[..^3];
        if (path.Length == 0) path = "/";
        var text = url.GetLeftPart(UriPartial.Authority) + path;
        return text.EndsWith('/') ? text[..^1] : text;
    }

    /// <summary>client.ts <c>llamaInferenceUrl</c>: the OpenAI-compatible <c>/v1</c> URL.</summary>
    internal static string InferenceUrl(string serverUrl) => NormalizeServerUrl(serverUrl) + "/v1";

    /// <summary>client.ts <c>formatBytes</c>.</summary>
    internal static string FormatBytes(double bytes)
    {
        if (bytes < 1024) return Js(bytes) + " B";
        string[] units = ["KiB", "MiB", "GiB", "TiB"];
        var value = bytes / 1024; var unit = units[0];
        for (var index = 1; index < units.Length && value >= 1024; index++) { value /= 1024; unit = units[index]; }
        return (value >= 10 ? value.ToString("F1", CultureInfo.InvariantCulture) : value.ToString("F2", CultureInfo.InvariantCulture)) + " " + unit;
    }

    private static string Js(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>URLSearchParams serialization (application/x-www-form-urlencoded).</summary>
    internal static string FormEncode(IEnumerable<(string Name, string Value)> pairs) =>
        string.Join("&", pairs.Select(pair => Form(pair.Name) + "=" + Form(pair.Value)));

    private static string Form(string text)
    {
        var builder = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            var c = (char)b;
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '*' or '-' or '.' or '_') builder.Append(c);
            else if (c == ' ') builder.Append('+');
            else builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    /// <summary>client.ts <c>errorMessage</c>: <c>error.message</c> of the payload, else the fallback.</summary>
    internal static string ErrorMessage(JsonNode? payload, string fallback) =>
        payload is JsonObject value && value["error"] is JsonObject error && error["message"] is JsonValue message &&
        message.TryGetValue<string>(out var text) && text.Length > 0 ? text : fallback;

    private void Authorize(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    private async Task<JsonNode?> RequestAsync(HttpMethod method, string path, JsonObject? body, CancellationToken token)
    {
        using var timeout = new CancellationTokenSource(RequestTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        using var request = new HttpRequestMessage(method, ServerUrl + path);
        if (body is not null)
            request.Content = new StringContent(PiJson.Stringify(body), Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        Authorize(request);
        try
        {
            using var response = await http.SendAsync(request, linked.Token).ConfigureAwait(false);
            JsonNode? payload;
            try { payload = JsonNode.Parse(await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false)); }
            catch (JsonException) { payload = null; }
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ErrorMessage(payload, $"llama.cpp returned HTTP {(int)response.StatusCode}"));
            return payload;
        }
        catch (OperationCanceledException error) when (timeout.IsCancellationRequested && !token.IsCancellationRequested)
        { throw new LlamaConnectionException("The operation was aborted due to timeout", error); }
        catch (HttpRequestException error) { throw new LlamaConnectionException("fetch failed", error); }
    }

    /// <summary><c>GET /models</c> (<c>?reload=1</c> rescans the model directory). Every entry must be router model info.</summary>
    internal async Task<IReadOnlyList<LlamaModelInfo>> ListAsync(bool reload = false, CancellationToken token = default)
    {
        var payload = await RequestAsync(HttpMethod.Get, "/models" + (reload ? "?reload=1" : ""), null, token).ConfigureAwait(false);
        if (payload is not JsonObject value || value["data"] is not JsonArray data) throw new InvalidOperationException("llama.cpp returned an invalid model catalog");
        if (!data.All(LlamaModelInfo.IsModelInfo)) throw new InvalidOperationException("Server is not running in llama.cpp router mode");
        return [.. data.Select(entry => new LlamaModelInfo((JsonObject)entry!.DeepClone()))];
    }

    /// <summary><c>GET /props</c>, of one model without autoloading it when <paramref name="model"/> is given.</summary>
    internal async Task<LlamaServerProps> PropsAsync(string? model = null, CancellationToken token = default)
    {
        var query = string.IsNullOrEmpty(model) ? "" : "?" + FormEncode([("model", model), ("autoload", "false")]);
        var payload = await RequestAsync(HttpMethod.Get, "/props" + query, null, token).ConfigureAwait(false);
        if (payload is not JsonObject value) return new();
        return new(value["models_autoload"] is JsonValue autoload && autoload.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? autoload.GetValue<bool>() : null,
            value["chat_template"] is JsonValue template && template.TryGetValue<string>(out var text) ? text : null);
    }

    internal Task LoadAsync(string model, CancellationToken token = default) => RequestAsync(HttpMethod.Post, "/models/load", new() { ["model"] = model }, token);
    internal Task UnloadAsync(string model, CancellationToken token = default) => RequestAsync(HttpMethod.Post, "/models/unload", new() { ["model"] = model }, token);
    internal Task DownloadAsync(string model, CancellationToken token = default) => RequestAsync(HttpMethod.Post, "/models", new() { ["model"] = model }, token);

    internal async Task UnloadAndWaitAsync(string model, CancellationToken token = default)
    {
        await UnloadAsync(model, token).ConfigureAwait(false);
        while (true)
        {
            var entry = (await ListAsync(token: token).ConfigureAwait(false)).FirstOrDefault(candidate => candidate.Id == model);
            if (entry is null || entry.Status == "unloaded") return;
            await Task.Delay(100, token).ConfigureAwait(false);
        }
    }

    /// <summary><c>GET /models/sse</c>: model events until the stream ends; malformed events are ignored (catalog polling stays authoritative).</summary>
    internal async Task WatchAsync(Action<LlamaModelEvent> onEvent, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ServerUrl + "/models/sse");
        Authorize(request);
        using var response = await (http is HttpClient client ? client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token) : http.SendAsync(request, token)).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"llama.cpp SSE returned HTTP {(int)response.StatusCode}");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[8192]; var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var buffer = new StringBuilder();
        while (true)
        {
            var read = await stream.ReadAsync(bytes, token).ConfigureAwait(false);
            if (read == 0) break;
            var count = decoder.GetChars(bytes, 0, read, chars, 0);
            buffer.Append(chars, 0, count).Replace("\r\n", "\n");
            var text = buffer.ToString();
            var boundary = text.IndexOf("\n\n", StringComparison.Ordinal);
            while (boundary >= 0)
            {
                var frame = text[..boundary];
                text = text[(boundary + 2)..];
                var data = string.Join("\n", frame.Split('\n').Where(line => line.StartsWith("data:", StringComparison.Ordinal)).Select(line => line[5..].TrimStart()));
                if (data.Length > 0)
                {
                    try
                    {
                        if (JsonNode.Parse(data) is JsonObject value && value["model"] is JsonValue model && model.TryGetValue<string>(out var modelId) &&
                            value["event"] is JsonValue name && name.TryGetValue<string>(out var eventName))
                            onEvent(new(modelId, eventName, value["data"]));
                    }
                    catch (JsonException) { }
                }
                boundary = text.IndexOf("\n\n", StringComparison.Ordinal);
            }
            buffer.Clear().Append(text);
        }
    }

    /// <summary>client.ts <c>parseLoadProgress</c>: the stage of the <c>progress</c> object and its ratio across the stages.</summary>
    internal static LlamaProgress? ParseLoadProgress(JsonNode? data)
    {
        if (data is not JsonObject value || value["progress"] is not JsonObject progress) return null;
        static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        var stage = Text(progress["current"]) ?? Text(progress["stage"]);
        var stages = progress["stages"] is JsonArray list ? list.Select(Text).OfType<string>().ToList() : [];
        double? stageRatio = progress["value"] is JsonValue number && number.GetValueKind() == JsonValueKind.Number
            ? Math.Max(0, Math.Min(1, LlamaModelInfo.JsonNumber(number))) : null;
        var ratio = stageRatio;
        if (stage is not null && stages.Count > 0 && stages.IndexOf(stage) is var index and >= 0) ratio = (index + (stageRatio ?? 0)) / stages.Count;
        return new(stage is not null ? "Loading " + stage.Replace("_", " ", StringComparison.Ordinal) : "Loading model", ratio) { HasRatio = true };
    }

    /// <summary>client.ts <c>parseDownloadProgress</c>: done and total bytes summed over the files.</summary>
    internal static LlamaProgress? ParseDownloadProgress(JsonNode? data)
    {
        if (data is not JsonObject value) return null;
        var files = value["progress"] as JsonObject ?? value;
        double done = 0, total = 0;
        foreach (var (_, entry) in files)
        {
            if (entry is not JsonObject file || file["done"] is not JsonValue d || d.GetValueKind() != JsonValueKind.Number ||
                file["total"] is not JsonValue t || t.GetValueKind() != JsonValueKind.Number) continue;
            done += LlamaModelInfo.JsonNumber(d); total += LlamaModelInfo.JsonNumber(t);
        }
        if (total <= 0) return null;
        return new("Downloading model", done / total, $"{FormatBytes(done)} / {FormatBytes(total)}") { HasRatio = true, HasDetail = true };
    }

    /// <summary>Starts the event stream in the background; its failures are ignored (polling decides).</summary>
    private Task StartWatch(Action<LlamaModelEvent> onEvent, CancellationToken token) =>
        Task.Run(async () => { try { await WatchAsync(onEvent, token).ConfigureAwait(false); } catch (Exception) { } }, CancellationToken.None);

    /// <summary>client.ts <c>loadAndWait</c>: load, then poll the catalog every 250 ms until the model is loaded; load events report
    /// progress.</summary>
    internal async Task<LlamaModelInfo> LoadAndWaitAsync(string model, Action<LlamaProgress> onProgress, CancellationToken token = default)
    {
        using var watcher = CancellationTokenSource.CreateLinkedTokenSource(token);
        var eventLoaded = false; string? eventError = null; var gate = new object();
        var watch = StartWatch(change =>
        {
            if (change.Model != model || change.Event is not ("model_status" or "status_change")) return;
            var status = (change.Data as JsonObject)?["status"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
            lock (gate)
            {
                if (status == "loaded") eventLoaded = true;
                if (status == "unloaded") eventError = "Model failed to load";
            }
            if (ParseLoadProgress(change.Data) is { } progress) onProgress(progress);
        }, watcher.Token);
        try
        {
            await LoadAsync(model, token).ConfigureAwait(false);
            onProgress(new("Loading model"));
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var entry = (await ListAsync(token: token).ConfigureAwait(false)).FirstOrDefault(candidate => candidate.Id == model);
                if (entry?.Status == "loaded") return entry;
                bool loaded; string? failure;
                lock (gate) { loaded = eventLoaded; failure = eventError; }
                if (loaded && entry is null) return new(new JsonObject { ["id"] = model, ["status"] = new JsonObject { ["value"] = "loaded" } });
                if (entry?.Failed == true || failure is not null)
                    throw new InvalidOperationException(entry?.ExitCode is not { } code ? failure ?? "Model failed to load" : $"Model exited with code {code}");
                await Task.Delay(250, token).ConfigureAwait(false);
            }
        }
        finally { watcher.Cancel(); await watch.ConfigureAwait(false); }
    }

    /// <summary>client.ts <c>downloadAndWait</c>: download, then poll every 500 ms until it finished; returns the reloaded catalog.</summary>
    internal async Task<IReadOnlyList<LlamaModelInfo>> DownloadAndWaitAsync(string model, Action<LlamaProgress> onProgress, CancellationToken token = default)
    {
        using var watcher = CancellationTokenSource.CreateLinkedTokenSource(token);
        var finished = false; string? failure = null; var sawDownloading = false; var polls = 0; var gate = new object();
        var watch = StartWatch(change =>
        {
            if (change.Model != model) return;
            lock (gate)
            {
                if (change.Event == "download_finished") finished = true;
                if (change.Event == "download_failed") failure = ErrorMessage(change.Data, "Download failed");
                if (change.Event == "download_progress") sawDownloading = true;
            }
            if (change.Event == "download_progress" && ParseDownloadProgress(change.Data) is { } progress) onProgress(progress);
        }, watcher.Token);
        try
        {
            await DownloadAsync(model, token).ConfigureAwait(false);
            onProgress(new("Downloading model"));
            while (true)
            {
                token.ThrowIfCancellationRequested();
                string? failed; lock (gate) failed = failure;
                if (failed is not null) throw new InvalidOperationException(failed);
                var models = await ListAsync(token: token).ConfigureAwait(false);
                polls++;
                var entry = models.FirstOrDefault(candidate => candidate.Id == model);
                bool done, seen;
                if (entry?.Status == "downloading")
                {
                    lock (gate) sawDownloading = true;
                    if (ParseDownloadProgress(entry.Progress) is { } progress) onProgress(progress);
                }
                else
                {
                    lock (gate) { done = finished; seen = sawDownloading; }
                    if (done || entry is not null && (seen || polls >= 2)) return await ListAsync(reload: true, token).ConfigureAwait(false);
                }
                await Task.Delay(500, token).ConfigureAwait(false);
            }
        }
        finally { watcher.Cancel(); await watch.ConfigureAwait(false); }
    }
}
