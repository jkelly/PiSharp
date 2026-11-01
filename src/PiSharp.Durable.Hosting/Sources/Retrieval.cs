// PiSharp-only (decision 0010, M3): how a host retrieves source content. The limits are hard ceilings no source can raise; optional
// providers stay off until the host configures them, and a source must also allow fallback before one is tried.
namespace PiSharp.Durable.Hosting;

/// <summary>Per-fetch hard ceilings.</summary>
public sealed record RetrievalLimits
{
    /// <summary>The largest response body, counted both as received and after decompression.</summary>
    public int MaxResponseBytes { get; init; } = 2 * 1024 * 1024;
    /// <summary>The most text kept from one document or item.</summary>
    public int MaxTextCharacters { get; init; } = 256 * 1024;
    /// <summary>The most redirects one fetch follows; each target is checked again.</summary>
    public int MaxRedirects { get; init; } = 5;
    /// <summary>How long one fetch may take, redirects included.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>The most fetch attempts of one source in one check, across the fallback chain and delayed retries.</summary>
    public int MaxAttempts { get; init; } = 3;
    /// <summary>The most pages a paged feed or release list may follow.</summary>
    public int MaxPages { get; init; } = 5;
    /// <summary>The longest a check waits (a durable sleep) for an origin to cool down before it retries; a longer cooldown defers the
    /// source to its next check.</summary>
    public TimeSpan MaxRetryWait { get; init; } = TimeSpan.FromMinutes(30);
}

/// <summary>Source retrieval settings of a device.</summary>
public sealed record RetrievalOptions
{
    /// <summary>The hard ceilings.</summary>
    public RetrievalLimits Limits { get; init; } = new();

    /// <summary>Host names or IP literals that may resolve to loopback, private, link-local or other non-public addresses (an intranet
    /// source). Cloud metadata addresses stay blocked even here.</summary>
    public IReadOnlyList<string> AllowedPrivateHosts { get; init; } = [];

    /// <summary><c>Authorization</c> header values by credential reference. A source names only the reference; the value stays in the
    /// host's options and is never sent to another origin or to a fallback provider.</summary>
    public IReadOnlyDictionary<string, string> Credentials { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Providers tried after direct HTTP fails in an eligible way, in order, for sources that allow fallback. Empty: none.</summary>
    public IReadOnlyList<IRetrievalProvider> Fallbacks { get; init; } = [];

    /// <summary>The GitHub REST API root of <c>github-releases</c> sources.</summary>
    public string GitHubApi { get; init; } = "https://api.github.com";

    /// <summary>The <c>User-Agent</c> of direct fetches.</summary>
    public string UserAgent { get; init; } = "PiSharp-Durable/1 (source monitor)";
}

/// <summary>One fetch.</summary>
/// <param name="Accept">The <c>Accept</c> header; null sends none.</param>
/// <param name="Authorization">The <c>Authorization</c> header for <paramref name="Url"/>'s origin only.</param>
/// <param name="ETag">A validator for a conditional request.</param>
/// <param name="LastModified">A validator for a conditional request.</param>
public sealed record RetrievalRequest(Uri Url, string? Accept, string? Authorization, string? ETag, string? LastModified, RetrievalLimits Limits);

/// <summary>A fetched response: a 2xx status with its bounded body, or 304.</summary>
/// <param name="Usage">What the fetch used, by meter; null is the provider's estimate.</param>
/// <param name="Link">The response's <c>Link</c> header, for paging.</param>
public sealed record RetrievalResponse(int Status, Uri FinalUrl, byte[] Body, string? ContentType, string? ETag, string? LastModified,
    IReadOnlyDictionary<string, long>? Usage = null, string? Link = null)
{
    /// <summary>Whether the content did not change since the validators.</summary>
    public bool NotModified => Status == 304;
}

/// <summary>A way to retrieve a URL: direct HTTP, or a configured search or extraction service.</summary>
public interface IRetrievalProvider
{
    /// <summary>A stable ID, the ledger's provider and the key of its health.</summary>
    string Id { get; }

    /// <summary>What one fetch reserves before it starts: at least <c>requests</c>; a paid provider also names its money or credits, so a
    /// capped budget can refuse it.</summary>
    IReadOnlyDictionary<string, long> Estimate(RetrievalRequest request);

    /// <summary>Fetches the URL within the request's limits.</summary>
    /// <exception cref="RetrievalException">The fetch failed.</exception>
    Task<RetrievalResponse> FetchAsync(RetrievalRequest request, CancellationToken cancellationToken);
}

/// <summary>A failed retrieval and its class.</summary>
/// <param name="failure"><c>blocked</c>, <c>too_large</c>, <c>redirects</c>, <c>timeout</c>, <c>network</c>, <c>forbidden</c>,
/// <c>rate_limited</c>, <c>server</c>, <c>http</c>, <c>parse</c> or <c>credential</c>.</param>
public sealed class RetrievalException(string failure, string message, int? status = null, TimeSpan? retryAfter = null) : Exception(message)
{
    /// <summary>The failure class.</summary>
    public string Failure { get; } = failure;
    /// <summary>The HTTP status, when there was one.</summary>
    public int? Status { get; } = status;
    /// <summary>The server's <c>Retry-After</c>.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;

    /// <summary>Whether the origin may be tried again later, or by another provider: network trouble, timeouts, 403, 429 and 5xx. A blocked
    /// address, an oversized body, too many redirects or a parse error never is.</summary>
    public bool Transient => Failure is "network" or "timeout" or "forbidden" or "rate_limited" or "server";
}
