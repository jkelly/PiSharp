// Decision 0012: the web_search tool's backends. Brave Search, Tavily and Firecrawl are search APIs; the others ask a model to run its
// provider's own hosted search and return its reply with the pages it cited: OpenAI and xAI through the Responses API's web_search tool,
// Anthropic through the Messages API's web_search server tool, and OpenRouter through its web plugin, for any model it routes to. Each
// backend is plain HTTP with its key from an environment variable; a refused key is reported by its variable name, never echoed.
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PiSharp.CustomAgents.Tools;

/// <summary>Brave Search's web search API (<c>X-Subscription-Token</c>; at most 20 results).</summary>
public sealed class BraveWebSearch : IWebSearchProvider
{
    /// <summary>The variable <see cref="FromEnvironment"/> reads.</summary>
    public const string ApiKeyVariable = "BRAVE_API_KEY";

    private readonly string apiKey;
    private readonly HttpClient? http;

    /// <summary>A Brave Search backend; <paramref name="http"/> null uses a shared client.</summary>
    public BraveWebSearch(string apiKey, HttpClient? http = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        this.apiKey = apiKey;
        this.http = http;
    }

    /// <summary>A backend over <c>BRAVE_API_KEY</c>; null when it is not set.</summary>
    public static BraveWebSearch? FromEnvironment(Func<string, string?> environment, HttpClient? http = null) =>
        WebSearchHttp.Key(environment, ApiKeyVariable) is { } key ? new BraveWebSearch(key, http) : null;

    /// <inheritdoc/>
    public string Name => "Brave Search";

    /// <summary>The API endpoint.</summary>
    public Uri Endpoint { get; init; } = new("https://api.search.brave.com/res/v1/web/search");

    /// <inheritdoc/>
    public async Task<WebSearchResponse> SearchAsync(WebSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var uri = new UriBuilder(Endpoint)
        {
            Query = "q=" + Uri.EscapeDataString(request.Query) + "&count=" + Math.Clamp(request.Count, 1, 20).ToString(CultureInfo.InvariantCulture),
        }.Uri;
        using var message = new HttpRequestMessage(HttpMethod.Get, uri);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.TryAddWithoutValidation("X-Subscription-Token", apiKey);
        var root = await WebSearchHttp.SendAsync(http, message, Name, ApiKeyVariable, cancellationToken).ConfigureAwait(false);
        var results = (root["web"]?["results"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(item => new WebSearchResult(WebSearchHttp.Text(item["title"]) ?? "", WebSearchHttp.Text(item["url"]) ?? "", WebSearchHttp.Text(item["description"])));
        return new WebSearchResponse { Provider = Name, Query = request.Query, Results = [.. results] };
    }
}

/// <summary>Tavily's search API (<c>Authorization: Bearer</c>; at most 20 results; optionally its own answer).</summary>
public sealed class TavilyWebSearch : IWebSearchProvider
{
    /// <summary>The variable <see cref="FromEnvironment"/> reads.</summary>
    public const string ApiKeyVariable = "TAVILY_API_KEY";

    private readonly string apiKey;
    private readonly HttpClient? http;

    /// <summary>A Tavily backend; <paramref name="http"/> null uses a shared client.</summary>
    public TavilyWebSearch(string apiKey, HttpClient? http = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        this.apiKey = apiKey;
        this.http = http;
    }

    /// <summary>A backend over <c>TAVILY_API_KEY</c>; null when it is not set.</summary>
    public static TavilyWebSearch? FromEnvironment(Func<string, string?> environment, HttpClient? http = null) =>
        WebSearchHttp.Key(environment, ApiKeyVariable) is { } key ? new TavilyWebSearch(key, http) : null;

    /// <inheritdoc/>
    public string Name => "Tavily";

    /// <summary>The API endpoint.</summary>
    public Uri Endpoint { get; init; } = new("https://api.tavily.com/search");

    /// <summary>Tavily's <c>search_depth</c>: <c>basic</c> (the default), <c>advanced</c>, <c>fast</c> or <c>ultra-fast</c>.</summary>
    public string SearchDepth { get; init; } = "basic";

    /// <summary>Tavily's <c>topic</c>: <c>general</c> (the default), <c>news</c> or <c>finance</c>.</summary>
    public string Topic { get; init; } = "general";

    /// <summary>Whether Tavily also writes an answer (<c>include_answer</c>).</summary>
    public bool IncludeAnswer { get; init; }

    /// <inheritdoc/>
    public async Task<WebSearchResponse> SearchAsync(WebSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = new JsonObject
        {
            ["query"] = request.Query,
            ["max_results"] = Math.Clamp(request.Count, 1, 20),
            ["search_depth"] = SearchDepth,
            ["topic"] = Topic,
            ["include_answer"] = IncludeAnswer,
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = WebSearchHttp.Json(body) };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        var root = await WebSearchHttp.SendAsync(http, message, Name, ApiKeyVariable, cancellationToken).ConfigureAwait(false);
        var results = (root["results"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(item => new WebSearchResult(WebSearchHttp.Text(item["title"]) ?? "", WebSearchHttp.Text(item["url"]) ?? "", WebSearchHttp.Text(item["content"])));
        return new WebSearchResponse { Provider = Name, Query = request.Query, Answer = WebSearchHttp.Text(root["answer"]), Results = [.. results] };
    }
}

/// <summary>Firecrawl's search API (v2, <c>Authorization: Bearer</c>; web results, optionally news too). The pages are not scraped, so a
/// search costs Firecrawl's search credits only.</summary>
public sealed class FirecrawlWebSearch : IWebSearchProvider
{
    /// <summary>The variable <see cref="FromEnvironment"/> reads.</summary>
    public const string ApiKeyVariable = "FireCrawl_API_KEY";

    private readonly string apiKey;
    private readonly HttpClient? http;

    /// <summary>A Firecrawl backend; <paramref name="http"/> null uses a shared client.</summary>
    public FirecrawlWebSearch(string apiKey, HttpClient? http = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        this.apiKey = apiKey;
        this.http = http;
    }

    /// <summary>A backend over <c>FireCrawl_API_KEY</c>; null when it is not set.</summary>
    public static FirecrawlWebSearch? FromEnvironment(Func<string, string?> environment, HttpClient? http = null) =>
        WebSearchHttp.Key(environment, ApiKeyVariable) is { } key ? new FirecrawlWebSearch(key, http) : null;

    /// <inheritdoc/>
    public string Name => "Firecrawl";

    /// <summary>The API endpoint.</summary>
    public Uri Endpoint { get; init; } = new("https://api.firecrawl.dev/v2/search");

    /// <summary>Firecrawl's <c>sources</c>: <c>web</c> (the default), <c>news</c>, or both. Each source returns up to the requested count,
    /// web results first.</summary>
    public IReadOnlyList<string> Sources { get; init; } = ["web"];

    /// <summary>Firecrawl's <c>country</c> (an ISO code such as <c>DE</c>); null leaves Firecrawl's default, <c>US</c>.</summary>
    public string? Country { get; init; }

    /// <summary>Firecrawl's <c>tbs</c> time filter, for example <c>qdr:w</c> for the past week; null searches any time.</summary>
    public string? TimeFilter { get; init; }

    /// <inheritdoc/>
    public async Task<WebSearchResponse> SearchAsync(WebSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = new JsonObject
        {
            ["query"] = request.Query,
            ["limit"] = Math.Clamp(request.Count, 1, 100),
            ["sources"] = new JsonArray([.. Sources.Select(source => (JsonNode)new JsonObject { ["type"] = source })]),
        };
        if (Country is { } country) body["country"] = country;
        if (TimeFilter is { } filter) body["tbs"] = filter;
        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = WebSearchHttp.Json(body) };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        var root = await WebSearchHttp.SendAsync(http, message, Name, ApiKeyVariable, cancellationToken).ConfigureAwait(false);
        if (root["success"] is System.Text.Json.Nodes.JsonValue flag && flag.TryGetValue<bool>(out var success) && !success)
            throw new WebSearchException($"{Name} failed: {WebText.Cut(WebText.Clean(WebSearchHttp.Text(root["error"]) ?? "no message"), 300)}");
        var data = root["data"] as JsonObject;
        var results = new List<WebSearchResult>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in (data?["web"] as JsonArray ?? []).OfType<JsonObject>())
            if (WebSearchHttp.Text(item["url"]) is { } url && seen.Add(url))
                results.Add(new WebSearchResult(WebSearchHttp.Text(item["title"]) ?? "", url,
                    WebSearchHttp.Text(item["description"]) ?? WebSearchHttp.Text(item["metadata"]?["description"])));
        foreach (var item in (data?["news"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (WebSearchHttp.Text(item["url"]) is not { } url || !seen.Add(url)) continue;
            var snippet = WebSearchHttp.Text(item["snippet"]);
            var date = WebSearchHttp.Text(item["date"]);
            results.Add(new WebSearchResult(WebSearchHttp.Text(item["title"]) ?? "", url, date is null || snippet is null ? snippet : date + ": " + snippet));
        }
        return new WebSearchResponse { Provider = Name, Query = request.Query, Results = results };
    }
}

/// <summary>
/// An OpenAI model that searches with OpenAI's hosted <c>web_search</c> tool (the Responses API, <c>tool_choice: required</c>). The answer is
/// the model's reply; the results are the pages it cited, then the other pages it consulted. Every search is a paid model request.
/// </summary>
public sealed class OpenAIWebSearch : IWebSearchProvider
{
    /// <summary>The variable <see cref="FromEnvironment"/> reads.</summary>
    public const string ApiKeyVariable = "OPENAI_API_KEY";

    /// <summary>The default model.</summary>
    public const string DefaultModel = "gpt-5.4-mini";

    private readonly string apiKey;
    private readonly HttpClient? http;

    /// <summary>An OpenAI backend; <paramref name="http"/> null uses a shared client.</summary>
    public OpenAIWebSearch(string apiKey, string model = DefaultModel, HttpClient? http = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        this.apiKey = apiKey;
        this.http = http;
        Model = model;
    }

    /// <summary>A backend over <c>OPENAI_API_KEY</c>; null when it is not set.</summary>
    public static OpenAIWebSearch? FromEnvironment(Func<string, string?> environment, string model = DefaultModel, HttpClient? http = null) =>
        WebSearchHttp.Key(environment, ApiKeyVariable) is { } key ? new OpenAIWebSearch(key, model, http) : null;

    /// <summary>The model that searches.</summary>
    public string Model { get; }

    /// <inheritdoc/>
    public string Name => "OpenAI web search, " + Model;

    /// <summary>The API endpoint.</summary>
    public Uri Endpoint { get; init; } = new("https://api.openai.com/v1/responses");

    /// <inheritdoc/>
    public Task<WebSearchResponse> SearchAsync(WebSearchRequest request, CancellationToken cancellationToken) =>
        ResponsesSearch.SearchAsync(http, Endpoint, apiKey, Model, Name, ApiKeyVariable, request, ResponsesSearch.WebTool(),
            ResponsesSearch.Prompt(request?.Query ?? ""), cancellationToken);
}

/// <summary>What an <see cref="XaiWebSearch"/> searches.</summary>
public enum XaiSearchSources
{
    /// <summary>The web (xAI's <c>web_search</c> tool); the default.</summary>
    Web,
    /// <summary>Posts on X (xAI's <c>x_search</c> tool).</summary>
    X,
    /// <summary>Both: the model chooses which tool to use for each search.</summary>
    WebAndX,
}

/// <summary>
/// An xAI Grok model that searches with xAI's hosted search tools (xAI's Responses API, the same shape as OpenAI's): <c>web_search</c> for the
/// web, <c>x_search</c> for posts on X, or both (<see cref="Sources"/>). The answer is the model's reply; the results are the pages and posts
/// it cited (an X post as an <c>x.com/i/status/…</c> address), then the other sources it found. Every search is a paid model request, and X
/// search also costs xAI's fee per post fetched.
/// </summary>
public sealed class XaiWebSearch : IWebSearchProvider
{
    /// <summary>The variable <see cref="FromEnvironment"/> reads.</summary>
    public const string ApiKeyVariable = "XAI_API_KEY";

    /// <summary>The default model.</summary>
    public const string DefaultModel = "grok-4.7";

    private readonly string apiKey;
    private readonly HttpClient? http;

    /// <summary>An xAI backend; <paramref name="http"/> null uses a shared client.</summary>
    public XaiWebSearch(string apiKey, string model = DefaultModel, HttpClient? http = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        this.apiKey = apiKey;
        this.http = http;
        Model = model;
    }

    /// <summary>A backend over <c>XAI_API_KEY</c>; null when it is not set.</summary>
    public static XaiWebSearch? FromEnvironment(Func<string, string?> environment, string model = DefaultModel, HttpClient? http = null) =>
        WebSearchHttp.Key(environment, ApiKeyVariable) is { } key ? new XaiWebSearch(key, model, http) : null;

    /// <summary>The model that searches.</summary>
    public string Model { get; }

    /// <inheritdoc/>
    public string Name => Sources switch
    {
        XaiSearchSources.X => "xAI X search, ",
        XaiSearchSources.WebAndX => "xAI web and X search, ",
        _ => "xAI web search, ",
    } + Model;

    /// <summary>The API endpoint.</summary>
    public Uri Endpoint { get; init; } = new("https://api.x.ai/v1/responses");

    /// <summary>What the model searches: the web (the default), posts on X, or both.</summary>
    public XaiSearchSources Sources { get; init; }

    /// <summary>X search only: posts from this day on (UTC; <c>from_date</c>); null for no lower bound.</summary>
    public DateOnly? XFromDate { get; init; }

    /// <summary>X search only: posts before this day (UTC, exclusive; <c>to_date</c>); null for no upper bound.</summary>
    public DateOnly? XToDate { get; init; }

    /// <summary>X search only: just the posts of these accounts (handles, with or without "@"; at most 20; <c>allowed_x_handles</c>).</summary>
    public IReadOnlyList<string> XHandles { get; init; } = [];

    /// <summary>X search only: no posts from these accounts (at most 20; <c>excluded_x_handles</c>). Cannot be combined with
    /// <see cref="XHandles"/>.</summary>
    public IReadOnlyList<string> XExcludedHandles { get; init; } = [];

    /// <inheritdoc/>
    public Task<WebSearchResponse> SearchAsync(WebSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tools = new JsonArray();
        if (Sources != XaiSearchSources.X) tools.Add(new JsonObject { ["type"] = "web_search" });
        if (Sources != XaiSearchSources.Web) tools.Add(XTool());
        var prompt = Sources switch
        {
            XaiSearchSources.X => "Search posts on X for: " + request.Query + "\nAnswer in at most five sentences, citing the posts you used.",
            XaiSearchSources.WebAndX => "Search the web and posts on X for: " + request.Query +
                "\nAnswer in at most five sentences, citing the pages and posts you used.",
            _ => ResponsesSearch.Prompt(request.Query),
        };
        return ResponsesSearch.SearchAsync(http, Endpoint, apiKey, Model, Name, ApiKeyVariable, request, tools, prompt, cancellationToken);
    }

    private JsonObject XTool()
    {
        if (XHandles.Count > 0 && XExcludedHandles.Count > 0) throw new WebSearchException($"{Name}: XHandles and XExcludedHandles cannot both be set.");
        if (XHandles.Count > 20 || XExcludedHandles.Count > 20) throw new WebSearchException($"{Name}: X search takes at most 20 handles.");
        var tool = new JsonObject { ["type"] = "x_search" };
        if (XFromDate is { } from) tool["from_date"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (XToDate is { } to) tool["to_date"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (XHandles.Count > 0) tool["allowed_x_handles"] = Handles(XHandles);
        if (XExcludedHandles.Count > 0) tool["excluded_x_handles"] = Handles(XExcludedHandles);
        return tool;
    }

    private static JsonArray Handles(IReadOnlyList<string> handles) => new([.. handles.Select(handle => (JsonNode)handle.Trim().TrimStart('@'))]);
}

/// <summary>
/// A Claude model that searches with Anthropic's <c>web_search</c> server tool (the Messages API). The answer is Claude's reply; the results
/// are the pages it cited, then the other pages its searches returned. Every search is a paid model request, plus Anthropic's per-search
/// fee. A turn paused by the server's tool loop (<c>pause_turn</c>) is resumed up to three times.
/// </summary>
public sealed class AnthropicWebSearch : IWebSearchProvider
{
    /// <summary>The variable <see cref="FromEnvironment"/> reads.</summary>
    public const string ApiKeyVariable = "ANTHROPIC_API_KEY";

    /// <summary>The default model.</summary>
    public const string DefaultModel = "claude-opus-5-5";

    /// <summary>The <c>anthropic-version</c> sent.</summary>
    public const string ApiVersion = "2026-10-21";

    private const string FallbackBeta = "server-side-fallback-2026-07-01";
    private const int MaxContinuations = 3;

    // The models whose web search has dynamic filtering (web_search_20260209); older models take the basic web_search_20250305.
    private static readonly string[] FilteringModels =
        ["claude-opus-5-5", "claude-opus-5", "claude-opus-4-8", "claude-opus-4-7", "claude-opus-4-6", "claude-sonnet-5-5", "claude-sonnet-5", "claude-sonnet-4-6"];
    // The models that take output_config.effort.
    private static readonly string[] EffortModels =
        ["claude-fable-5", "claude-mythos-5", "claude-opus-5", "claude-opus-4-8", "claude-opus-4-7", "claude-opus-4-6", "claude-sonnet-5", "claude-sonnet-4-6", "claude-haiku-5-5"];
    // The models that take the server-side refusal fallback (fallbacks: "default").
    private static readonly string[] FallbackModels = ["claude-fable-5-1", "claude-opus-5-5", "claude-opus-5", "claude-sonnet-5-5"];

    private readonly string apiKey;
    private readonly HttpClient? http;

    /// <summary>An Anthropic backend; <paramref name="http"/> null uses a shared client.</summary>
    public AnthropicWebSearch(string apiKey, string model = DefaultModel, HttpClient? http = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        this.apiKey = apiKey;
        this.http = http;
        Model = model;
        ToolType = FilteringModels.Contains(model, StringComparer.Ordinal) ? "web_search_20260209" : "web_search_20250305";
        Effort = EffortModels.Any(family => model.StartsWith(family, StringComparison.Ordinal)) ? "low" : null;
        RefusalFallback = FallbackModels.Contains(model, StringComparer.Ordinal);
    }

    /// <summary>A backend over <c>ANTHROPIC_API_KEY</c>; null when it is not set.</summary>
    public static AnthropicWebSearch? FromEnvironment(Func<string, string?> environment, string model = DefaultModel, HttpClient? http = null) =>
        WebSearchHttp.Key(environment, ApiKeyVariable) is { } key ? new AnthropicWebSearch(key, model, http) : null;

    /// <summary>The model that searches.</summary>
    public string Model { get; }

    /// <inheritdoc/>
    public string Name => "Claude web search, " + Model;

    /// <summary>The API endpoint.</summary>
    public Uri Endpoint { get; init; } = new("https://api.anthropic.com/v1/messages");

    /// <summary>The server tool's type: <c>web_search_20260209</c> (dynamic filtering) on Opus 5.5, 5, 4.8, 4.7 and 4.6 and Sonnet 5.5, 5
    /// and 4.6, else <c>web_search_20250305</c>.</summary>
    public string ToolType { get; init; }

    /// <summary>How many searches Claude may run for one query (<c>max_uses</c>); default 3.</summary>
    public int MaxUses { get; init; } = 3;

    /// <summary><c>output_config.effort</c>: <c>low</c> by default on the models that take it (a search is a simple task), else not sent.</summary>
    public string? Effort { get; init; }

    /// <summary>Whether a refusal is retried on another model by the server (<c>fallbacks: "default"</c>); default on for Claude Fable 5.1,
    /// Opus 5.5, Opus 5 and Sonnet 5.5, the models that take it.</summary>
    public bool RefusalFallback { get; init; }

    /// <summary>The reply's token cap; default 16000.</summary>
    public int MaxTokens { get; init; } = 16000;

    /// <inheritdoc/>
    public async Task<WebSearchResponse> SearchAsync(WebSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var question = new JsonObject { ["role"] = "user", ["content"] = ResponsesSearch.Prompt(request.Query) };
        var body = new JsonObject
        {
            ["model"] = Model,
            ["max_tokens"] = MaxTokens,
            ["tools"] = new JsonArray(new JsonObject { ["type"] = ToolType, ["name"] = "web_search", ["max_uses"] = MaxUses }),
        };
        if (Effort is { } effort) body["output_config"] = new JsonObject { ["effort"] = effort };
        if (RefusalFallback) body["fallbacks"] = "default";
        var content = new JsonArray();
        for (var turn = 0; ; turn++)
        {
            // A paused turn resumes from the question and everything Claude produced so far, sent back unchanged.
            body["messages"] = turn == 0
                ? new JsonArray(question.DeepClone())
                : new JsonArray(question.DeepClone(), new JsonObject { ["role"] = "assistant", ["content"] = content.DeepClone() });
            using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = WebSearchHttp.Json(body) };
            message.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            message.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
            if (RefusalFallback) message.Headers.TryAddWithoutValidation("anthropic-beta", FallbackBeta);
            var root = await WebSearchHttp.SendAsync(http, message, Name, ApiKeyVariable, cancellationToken).ConfigureAwait(false);
            var stop = WebSearchHttp.Text(root["stop_reason"]);
            if (stop == "refusal")
                throw new WebSearchException((root["stop_details"] as JsonObject)?["category"] is var details && WebSearchHttp.Text(details) is { } category
                    ? $"{Name} declined the search ({category})."
                    : $"{Name} declined the search.");
            foreach (var block in (root["content"] as JsonArray ?? []).ToList()) content.Add(block?.DeepClone());
            if (stop != "pause_turn" || turn == MaxContinuations) break;
        }
        return Read(content, request.Query);
    }

    private WebSearchResponse Read(JsonArray content, string query)
    {
        var answer = new StringBuilder();
        var results = new List<WebSearchResult>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var found = new List<WebSearchResult>();
        var errors = new List<string>();
        foreach (var block in content.OfType<JsonObject>())
            switch (WebSearchHttp.Text(block["type"]))
            {
                case "text":
                    answer.Append(WebSearchHttp.Text(block["text"]));
                    foreach (var citation in (block["citations"] as JsonArray ?? []).OfType<JsonObject>()
                        .Where(note => WebSearchHttp.Text(note["type"]) == "web_search_result_location"))
                        if (WebSearchHttp.Text(citation["url"]) is { } url && seen.Add(url))
                            results.Add(new WebSearchResult(WebSearchHttp.Title(WebSearchHttp.Text(citation["title"]), url), url, WebSearchHttp.Text(citation["cited_text"])));
                    break;
                case "web_search_tool_result":
                    // A success holds a list of results; a failure holds one error object.
                    if (block["content"] is JsonArray list)
                        found.AddRange(list.OfType<JsonObject>().Where(item => WebSearchHttp.Text(item["type"]) == "web_search_result")
                            .Select(item => new WebSearchResult(WebSearchHttp.Text(item["title"]) ?? "", WebSearchHttp.Text(item["url"]) ?? "", null)));
                    else if (block["content"] is JsonObject failure && WebSearchHttp.Text(failure["error_code"]) is { } code) errors.Add(code);
                    break;
            }
        foreach (var result in found)
            if (result.Url.Length > 0 && seen.Add(result.Url)) results.Add(result with { Title = WebSearchHttp.Title(result.Title, result.Url) });
        var text = answer.ToString().Trim();
        if (text.Length == 0 && results.Count == 0 && errors.Count > 0)
            throw new WebSearchException($"{Name} could not search: {string.Join(", ", errors.Distinct(StringComparer.Ordinal))}.");
        return new WebSearchResponse { Provider = Name, Query = query, Answer = text.Length == 0 ? null : text, Results = results };
    }
}

/// <summary>
/// OpenRouter's web plugin (<c>plugins: [{"id": "web"}]</c>) on any model OpenRouter routes to. The answer is the model's reply; the results are
/// the pages it cited (<c>url_citation</c> annotations). Every search is a paid model request plus the search engine's fee.
/// </summary>
public sealed class OpenRouterWebSearch : IWebSearchProvider
{
    /// <summary>The variable <see cref="FromEnvironment"/> reads.</summary>
    public const string ApiKeyVariable = "OPENROUTER_API_KEY";

    /// <summary>The default model.</summary>
    public const string DefaultModel = "openai/gpt-5.4-mini";

    private readonly string apiKey;
    private readonly HttpClient? http;

    /// <summary>An OpenRouter backend; <paramref name="http"/> null uses a shared client.</summary>
    public OpenRouterWebSearch(string apiKey, string model = DefaultModel, HttpClient? http = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        this.apiKey = apiKey;
        this.http = http;
        Model = model;
    }

    /// <summary>A backend over <c>OPENROUTER_API_KEY</c>; null when it is not set.</summary>
    public static OpenRouterWebSearch? FromEnvironment(Func<string, string?> environment, string model = DefaultModel, HttpClient? http = null) =>
        WebSearchHttp.Key(environment, ApiKeyVariable) is { } key ? new OpenRouterWebSearch(key, model, http) : null;

    /// <summary>The model that searches, as an OpenRouter slug (for example <c>anthropic/claude-sonnet-5.5</c>).</summary>
    public string Model { get; }

    /// <inheritdoc/>
    public string Name => "OpenRouter web search, " + Model;

    /// <summary>The API endpoint.</summary>
    public Uri Endpoint { get; init; } = new("https://openrouter.ai/api/v1/chat/completions");

    /// <summary>The plugin's search engine (<c>native</c>, <c>exa</c>, <c>firecrawl</c>, <c>parallel</c> or <c>perplexity</c>); null lets
    /// OpenRouter choose: the model provider's native search where it has one, else Exa.</summary>
    public string? Engine { get; init; }

    /// <inheritdoc/>
    public async Task<WebSearchResponse> SearchAsync(WebSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var plugin = new JsonObject { ["id"] = "web", ["max_results"] = Math.Clamp(request.Count, 1, 10) };
        if (Engine is { } engine) plugin["engine"] = engine;
        var body = new JsonObject
        {
            ["model"] = Model,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = ResponsesSearch.Prompt(request.Query) }),
            ["plugins"] = new JsonArray(plugin),
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = WebSearchHttp.Json(body) };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        var root = await WebSearchHttp.SendAsync(http, message, Name, ApiKeyVariable, cancellationToken).ConfigureAwait(false);
        // OpenRouter can report a provider's failure in a 200 reply.
        if (root["error"] is JsonObject error)
            throw new WebSearchException($"{Name} failed: {WebText.Cut(WebText.Clean(WebSearchHttp.Text(error["message"]) ?? "no message"), 300)}");
        var reply = ((root["choices"] as JsonArray)?.FirstOrDefault() as JsonObject)?["message"] as JsonObject;
        var results = new List<WebSearchResult>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var citation in (reply?["annotations"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(note => WebSearchHttp.Text(note["type"]) == "url_citation").Select(note => note["url_citation"]).OfType<JsonObject>())
            if (WebSearchHttp.Text(citation["url"]) is { } url && seen.Add(url))
                results.Add(new WebSearchResult(WebSearchHttp.Title(WebSearchHttp.Text(citation["title"]), url), url, WebSearchHttp.Text(citation["content"])));
        var answer = WebSearchHttp.Text(reply?["content"])?.Trim();
        return new WebSearchResponse { Provider = Name, Query = request.Query, Answer = string.IsNullOrEmpty(answer) ? null : answer, Results = results };
    }
}

/// <summary>A search through the Responses API's hosted search tools (OpenAI's and xAI's <c>web_search</c>, xAI's <c>x_search</c>).</summary>
internal static class ResponsesSearch
{
    /// <summary>The question a model-based backend is asked.</summary>
    public static string Prompt(string query) => "Search the web for: " + query + "\nAnswer in at most five sentences, citing the pages you used.";

    /// <summary>Only the <c>web_search</c> tool.</summary>
    public static JsonArray WebTool() => [new JsonObject { ["type"] = "web_search" }];

    public static async Task<WebSearchResponse> SearchAsync(HttpClient? http, Uri endpoint, string apiKey, string model, string provider, string keyVariable,
        WebSearchRequest request, JsonArray tools, string prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = new JsonObject
        {
            ["model"] = model,
            ["input"] = prompt,
            ["tools"] = tools,
            ["tool_choice"] = "required",
            ["store"] = false,
        };
        // The pages a web search consulted, beyond the ones the reply cites; X search has no such list.
        if (tools.OfType<JsonObject>().Any(tool => WebSearchHttp.Text(tool["type"]) == "web_search"))
            body["include"] = new JsonArray("web_search_call.action.sources");
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = WebSearchHttp.Json(body) };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        var root = await WebSearchHttp.SendAsync(http, message, provider, keyVariable, cancellationToken).ConfigureAwait(false);
        var output = (root["output"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var answer = new StringBuilder();
        var results = new List<WebSearchResult>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in output.Where(item => WebSearchHttp.Text(item["type"]) == "message").SelectMany(item => (item["content"] as JsonArray ?? []).OfType<JsonObject>()))
        {
            if (WebSearchHttp.Text(part["type"]) != "output_text" || WebSearchHttp.Text(part["text"]) is not { } text) continue;
            foreach (var citation in (part["annotations"] as JsonArray ?? []).OfType<JsonObject>().Where(note => WebSearchHttp.Text(note["type"]) == "url_citation"))
            {
                if (WebSearchHttp.Text(citation["url"]) is not { } url || !seen.Add(url)) continue;
                var start = WebSearchHttp.Integer(citation["start_index"]);
                var end = WebSearchHttp.Integer(citation["end_index"]);
                var cited = start is { } from && end is { } to && from >= 0 && to > from && to <= text.Length ? text[from..to] : null;
                results.Add(new WebSearchResult(WebSearchHttp.Title(WebSearchHttp.Text(citation["title"]), url), url, cited));
            }
            if (answer.Length > 0) answer.Append("\n\n");
            answer.Append(text);
        }
        foreach (var source in output.Where(item => WebSearchHttp.Text(item["type"]) == "web_search_call")
            .SelectMany(item => (item["action"] as JsonObject)?["sources"] as JsonArray ?? []).OfType<JsonObject>())
            if (WebSearchHttp.Text(source["url"]) is { } url && seen.Add(url))
                results.Add(new WebSearchResult(WebSearchHttp.Title(WebSearchHttp.Text(source["title"]), url), url, null));
        // xAI also lists every source its tools found, cited or not, as addresses (or objects with a url).
        foreach (var source in root["citations"] as JsonArray ?? [])
            if ((WebSearchHttp.Text(source) ?? WebSearchHttp.Text((source as JsonObject)?["url"])) is { } url && seen.Add(url))
                results.Add(new WebSearchResult(WebSearchHttp.Title(null, url), url, null));
        return new WebSearchResponse { Provider = provider, Query = request.Query, Answer = answer.Length == 0 ? null : answer.ToString(), Results = results };
    }
}

/// <summary>The backends' HTTP and JSON helpers.</summary>
internal static class WebSearchHttp
{
    private static readonly Lazy<HttpClient> Shared = new(() => new HttpClient(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = false,
    })
    { MaxResponseContentBufferSize = 8 * 1024 * 1024 });

    /// <summary>The value of <paramref name="variable"/>; null when it is unset or empty.</summary>
    public static string? Key(Func<string, string?> environment, string variable) =>
        (environment ?? throw new ArgumentNullException(nameof(environment)))(variable) is { Length: > 0 } key ? key : null;

    public static StringContent Json(JsonObject body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    /// <summary>Sends <paramref name="message"/> and parses the JSON reply; a refusal or failure is a <see cref="WebSearchException"/>.</summary>
    public static async Task<JsonNode> SendAsync(HttpClient? http, HttpRequestMessage message, string provider, string keyVariable, CancellationToken cancellationToken)
    {
        using var response = await (http ?? Shared.Value).SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        // A refused key's body can echo part of the key, so it is not quoted.
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new WebSearchException($"{provider} refused the API key (HTTP {status}); check {keyVariable}.");
        if (!response.IsSuccessStatusCode)
        {
            var failure = $"{provider} returned HTTP {status}{ErrorText(body)}";
            throw new WebSearchException(failure.EndsWith('.') || failure.EndsWith('…') ? failure : failure + ".")
            {
                RetryAfter = response.StatusCode == HttpStatusCode.TooManyRequests ? RetryAfter(response) : null,
            };
        }
        try { return JsonNode.Parse(body) as JsonObject ?? throw new WebSearchException($"{provider} returned a response that is not a JSON object."); }
        catch (JsonException) { throw new WebSearchException($"{provider} returned a response that is not JSON."); }
    }

    /// <summary>How long a rate-limited reply asks to wait (its Retry-After, 1 to 30 seconds), else 10 seconds: per-minute limits refill
    /// within a minute.</summary>
    private static TimeSpan RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        var wait = header?.Delta ?? (header?.Date is { } at ? at - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(10));
        return TimeSpan.FromSeconds(Math.Clamp(wait.TotalSeconds, 1, 30));
    }

    public static string? Text(JsonNode? node) => node is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    public static int? Integer(JsonNode? node) => node is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    /// <summary>A result's title, or its host when the backend gives none, only repeats the address, or gives a citation number (xAI's
    /// titles are often "1", "2" …).</summary>
    public static string Title(string? title, string url) =>
        !string.IsNullOrWhiteSpace(title) && title != url && !title.All(char.IsAsciiDigit) ? title
            : Uri.TryCreate(url, UriKind.Absolute, out var page) ? page.Host : url;

    /// <summary>": " and the error message of an error body (OpenAI's, xAI's and Anthropic's error.message, Tavily's detail.error, Brave's
    /// error.detail, Firecrawl's error), else its start; empty for an empty body.</summary>
    private static string ErrorText(string body)
    {
        string? message = null;
        try
        {
            // error and detail may be objects or plain strings.
            if (JsonNode.Parse(body) is JsonObject root)
                message = Text((root["error"] as JsonObject)?["message"]) ?? Text((root["error"] as JsonObject)?["detail"]) ??
                    Text((root["detail"] as JsonObject)?["error"]) ?? Text(root["detail"]) ?? Text(root["message"]) ?? Text(root["error"]);
        }
        catch (JsonException) { }
        message = WebText.Clean(message ?? body);
        return message.Length == 0 ? "" : ": " + WebText.Cut(message, 300);
    }
}

/// <summary>Text cleanup for titles and snippets.</summary>
internal static partial class WebText
{
    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>The text without HTML tags or entities, on one line.</summary>
    public static string Clean(string? text) => string.IsNullOrEmpty(text) ? "" : Spaces().Replace(WebUtility.HtmlDecode(Tags().Replace(text, "")), " ").Trim();

    /// <summary>At most <paramref name="max"/> characters, ending in an ellipsis when cut.</summary>
    public static string Cut(string text, int max) => text.Length <= max ? text : text[..Math.Max(0, max - 1)].TrimEnd() + "…";

    public static bool IsWebAddress(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var address) && (address.Scheme == Uri.UriSchemeHttps || address.Scheme == Uri.UriSchemeHttp);
}
