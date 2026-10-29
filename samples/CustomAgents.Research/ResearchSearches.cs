// Decision 0012: the researchers' two searches. x_search is xAI's X search (XaiWebSearch with Sources = X), over XAI_API_KEY; web_search
// is the backend --web names, else the first whose key is set. Scripted runs search canned results instead, so they stay offline.
using PiSharp.CustomAgents.Tools;

namespace PiSharp.CustomAgents.Research;

/// <summary>The X and web search backends, each null with the reason when it is off.</summary>
internal sealed record ResearchSearches(IWebSearchProvider? X, string? XMissing, IWebSearchProvider? Web, string? WebMissing)
{
    public static ResearchSearches Create(ResearchOptions options, bool scripted, Func<string, string?> environment)
    {
        if (scripted) return new(new CannedSearch(posts: true), null, new CannedSearch(posts: false), null);
        var x = environment(XaiWebSearch.ApiKeyVariable) is { Length: > 0 } key
            ? new XaiWebSearch(key, options.XModel)
            {
                Sources = XaiSearchSources.X,
                XFromDate = options.Days is { } days ? DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-days) : null,
            }
            : null;
        var web = options.Web switch
        {
            "none" => null,
            { } backend => WebSearchTool.ProviderFromEnvironment(backend, environment),
            null => WebSearchTool.ProviderFromEnvironment(environment),
        };
        var webMissing = options.Web switch
        {
            "none" => "--web none",
            { } backend => WebSearchTool.ApiKeyVariable(backend) + " is not set",
            null => $"none of {string.Join(", ", WebSearchTool.Backends.Select(WebSearchTool.ApiKeyVariable))} is set",
        };
        return new(x, XaiWebSearch.ApiKeyVariable + " is not set", web, webMissing);
    }
}

/// <summary>Made-up results for scripted runs: two X posts or two web pages per query, with addresses derived from the query.</summary>
internal sealed class CannedSearch(bool posts) : IWebSearchProvider
{
    public string Name => posts ? "canned X posts (scripted)" : "canned web results (scripted)";

    public Task<WebSearchResponse> SearchAsync(WebSearchRequest request, CancellationToken cancellationToken)
    {
        // A stable number per query (string.GetHashCode differs between processes).
        var id = request.Query.Aggregate(2166136261u, (hash, character) => (hash ^ character) * 16777619u) % 1_000_000;
        IReadOnlyList<WebSearchResult> results = posts
            ?
            [
                new("x.com", $"https://x.com/i/status/18{id:000000}01", $"(scripted post) A practitioner's take on {request.Query}."),
                new("x.com", $"https://x.com/i/status/18{id:000000}02", $"(scripted post) A thread arguing the opposite about {request.Query}."),
            ]
            :
            [
                new($"Overview: {request.Query}", $"https://example.org/research/{id}/overview", $"(scripted page) Background on {request.Query}."),
                new($"Data: {request.Query}", $"https://example.org/research/{id}/data", $"(scripted page) Figures on {request.Query}."),
            ];
        return Task.FromResult(new WebSearchResponse { Provider = Name, Query = request.Query, Results = results });
    }
}
