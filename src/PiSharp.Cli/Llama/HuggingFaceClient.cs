// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/extensions/llama/huggingface.ts.
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PiSharp.Cli.Llama;

/// <summary>huggingface.ts <c>HuggingFaceModel</c>: a search result.</summary>
internal sealed record HuggingFaceModel(string Id, double Downloads);

/// <summary>huggingface.ts <c>HuggingFaceQuantization</c>: a quantization and its total size when every file reports one.</summary>
internal sealed record HuggingFaceQuantization(string Name, double? Size = null);

/// <summary>huggingface.ts <c>HuggingFaceModelDetails</c>: <see cref="Gated"/> is null, <c>auto</c> or <c>manual</c>.</summary>
internal sealed record HuggingFaceModelDetails(string Id, string? Gated, IReadOnlyList<HuggingFaceQuantization> Quantizations);

/// <summary>huggingface.ts <c>HuggingFaceClient</c>: GGUF model search and a repository's quantizations (<c>/api/models</c>).</summary>
internal sealed partial class HuggingFaceClient
{
    internal const string DefaultUrl = "https://huggingface.co";
    private static readonly HttpClient SharedHttp = new();
    private readonly string? token;
    private readonly string baseUrl;
    private readonly HttpMessageInvoker http;

    internal HuggingFaceClient(string? token = null, string baseUrl = DefaultUrl, HttpMessageInvoker? http = null)
    {
        this.token = token; this.baseUrl = baseUrl.TrimEnd('/'); this.http = http ?? SharedHttp;
    }

    [GeneratedRegex(@"(?:^|[-_.])((?:UD-)?(?:IQ\d(?:_[A-Z0-9]+)+|Q\d(?:_[A-Z0-9]+)+|BF16|F16|F32|MXFP\d(?:_[A-Z0-9]+)*))$", RegexOptions.IgnoreCase | RegexOptions.ECMAScript)]
    private static partial Regex QuantizationPattern();
    [GeneratedRegex(@"-\d{5}-of-\d{5}$", RegexOptions.ECMAScript)]
    private static partial Regex ShardSuffixPattern();
    [GeneratedRegex(@"(?:^|;)t=(\d+)", RegexOptions.ECMAScript)]
    private static partial Regex RateLimitDelay();

    /// <summary>huggingface.ts <c>findHuggingFaceToken</c>: <c>HF_TOKEN</c>, then the token files of <c>HF_TOKEN_PATH</c>,
    /// <c>$HF_HOME/token</c>, <c>$XDG_CACHE_HOME/huggingface/token</c> and <c>~/.cache/huggingface/token</c>.</summary>
    internal static async Task<string?> FindTokenAsync(Func<string, string?> environment, string? home = null)
    {
        if (environment("HF_TOKEN")?.Trim() is { Length: > 0 } fromEnvironment) return fromEnvironment;
        var homeDirectory = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string?[] candidates =
        [
            environment("HF_TOKEN_PATH") is { Length: > 0 } tokenPath ? tokenPath : null,
            environment("HF_HOME") is { Length: > 0 } hfHome ? Path.Join(hfHome, "token") : null,
            environment("XDG_CACHE_HOME") is { Length: > 0 } cache ? Path.Join(cache, "huggingface", "token") : null,
            Path.Join(homeDirectory, ".cache", "huggingface", "token")
        ];
        foreach (var path in candidates.OfType<string>().Distinct(StringComparer.Ordinal))
        {
            try { if ((await File.ReadAllTextAsync(path, Encoding.UTF8).ConfigureAwait(false)).Trim() is { Length: > 0 } found) return found; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        return null;
    }

    private async Task<JsonNode?> RequestAsync(string path, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(LlamaClient.RequestTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);
        if (!string.IsNullOrEmpty(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var response = await http.SendAsync(request, linked.Token).ConfigureAwait(false);
            JsonNode? payload;
            try { payload = JsonNode.Parse(await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false)); }
            catch (JsonException) { payload = null; }
            if (response.IsSuccessStatusCode) return payload;
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = response.Headers.TryGetValues("retry-after", out var values) ? values.FirstOrDefault() : null;
                var rateLimit = response.Headers.TryGetValues("ratelimit", out var limits) ? limits.FirstOrDefault() : null;
                var delay = retryAfter is not null && LlamaCatalog.JsNumber(retryAfter) is { } seconds && seconds != 0 ? seconds
                    : rateLimit is not null && RateLimitDelay().Match(rateLimit) is { Success: true } match ? double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : (double?)null;
                throw new InvalidOperationException(delay is { } wait && wait != 0
                    ? $"Hugging Face rate limit reached; retry in {wait.ToString("R", CultureInfo.InvariantCulture)}s" : "Hugging Face rate limit reached");
            }
            throw new InvalidOperationException(payload is JsonObject value && value["error"] is JsonValue error && error.TryGetValue<string>(out var text) && text.Length > 0
                ? text : $"Hugging Face returned HTTP {(int)response.StatusCode}");
        }
        catch (OperationCanceledException error) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        { throw new LlamaConnectionException("The operation was aborted due to timeout", error); }
        catch (HttpRequestException error) { throw new LlamaConnectionException("fetch failed", error); }
    }

    /// <summary>The 20 most downloaded GGUF models matching <paramref name="query"/>.</summary>
    internal async Task<IReadOnlyList<HuggingFaceModel>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var parameters = LlamaClient.FormEncode([("search", query), ("filter", "gguf"), ("sort", "downloads"), ("direction", "-1"), ("limit", "20")]);
        var payload = await RequestAsync("/api/models?" + parameters, cancellationToken).ConfigureAwait(false);
        if (payload is not JsonArray results) throw new InvalidOperationException("Hugging Face returned invalid search results");
        return [.. results.OfType<JsonObject>().Where(model => model["id"] is JsonValue id && id.GetValueKind() == JsonValueKind.String)
            .Select(model => new HuggingFaceModel((string)model["id"]!,
                model["downloads"] is JsonValue downloads && downloads.GetValueKind() == JsonValueKind.Number ? LlamaModelInfo.JsonNumber(downloads) : 0))];
    }

    /// <summary>A repository's access requirement and its GGUF quantizations (shards summed, <c>mmproj</c> files skipped), Q4_K_M first,
    /// then by size and name.</summary>
    internal async Task<HuggingFaceModelDetails> DetailsAsync(string id, CancellationToken cancellationToken = default)
    {
        var encodedId = string.Join("/", id.Split('/').Select(EncodeUriComponent));
        var payload = await RequestAsync($"/api/models/{encodedId}?blobs=true", cancellationToken).ConfigureAwait(false);
        if (payload is not JsonObject model) throw new InvalidOperationException("Hugging Face returned invalid model details");
        var sizes = new Dictionary<string, (double Total, bool Complete)>(StringComparer.Ordinal); var order = new List<string>();
        if (model["siblings"] is JsonArray siblings)
            foreach (var file in siblings.OfType<JsonObject>())
            {
                if (file["rfilename"] is not JsonValue nameValue || !nameValue.TryGetValue<string>(out var rfilename) || !rfilename.ToLowerInvariant().EndsWith(".gguf", StringComparison.Ordinal)) continue;
                var filename = rfilename.Split('/')[^1];
                if (filename.ToLowerInvariant().StartsWith("mmproj", StringComparison.Ordinal)) continue;
                var stem = ShardSuffixPattern().Replace(filename[..^5], "", 1);
                if (QuantizationPattern().Match(stem) is not { Success: true } match) continue;
                var quantization = match.Groups[1].Value.ToUpperInvariant();
                if (!sizes.TryGetValue(quantization, out var current)) { current = (0, true); order.Add(quantization); }
                if (file["size"] is JsonValue size && size.GetValueKind() == JsonValueKind.Number) current.Total += LlamaModelInfo.JsonNumber(size);
                else current.Complete = false;
                sizes[quantization] = current;
            }
        // Array.prototype.sort is stable: OrderBy keeps the files' order among equal entries.
        var quantizations = order.Select(name => new HuggingFaceQuantization(name, sizes[name].Complete ? sizes[name].Total : null))
            .OrderBy(entry => entry, Comparer<HuggingFaceQuantization>.Create((left, right) =>
            {
                if (left.Name == right.Name) return 0;
                if (left.Name == "Q4_K_M") return -1;
                if (right.Name == "Q4_K_M") return 1;
                var bySize = (left.Size ?? 9007199254740991).CompareTo(right.Size ?? 9007199254740991);
                return bySize != 0 ? bySize : string.Compare(left.Name, right.Name, StringComparison.InvariantCulture);
            })).ToList();
        return new(model["id"] is JsonValue modelId && modelId.TryGetValue<string>(out var text) ? text : id,
            model["gated"] is JsonValue gated && gated.TryGetValue<string>(out var gate) && gate is "auto" or "manual" ? gate : null, quantizations);
    }

    /// <summary>JavaScript <c>encodeURIComponent</c>.</summary>
    internal static string EncodeUriComponent(string value)
    {
        var builder = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.' or '!' or '~' or '*' or '\'' or '(' or ')') builder.Append(c);
            else builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }
}
