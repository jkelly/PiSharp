// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/utils/version-check.ts, with management-http.ts
// (fetchWithRetry). Owner decision 12 (docs/decisions/0004-full-parity-owner-decisions.md): PiSharp keeps Pi's timing, gates and
// error handling but asks NuGet for the latest PiSharp.Cli instead of pi.dev for Pi's version. The HTTP client, NuGet base URL and
// environment are injectable.
using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using PiSharp.CodingAgent.Diagnostics;

namespace PiSharp.Cli.Interactive.Mode.Utilities;

/// <summary>Source <c>LatestPiRelease</c>: the newest <c>PiSharp.Cli</c> on the feed (NuGet carries no package-name or note).</summary>
internal sealed record LatestRelease(string Version);

/// <summary>Source <c>{ timeoutMs?, retry? }</c> plus the injected HTTP client, NuGet flat-container base URL and environment.</summary>
internal sealed record VersionCheckOptions
{
    public int? TimeoutMs { get; init; }
    public bool Retry { get; init; }
    public HttpMessageInvoker? Http { get; init; }
    /// <summary>The NuGet V3 flat-container base (null: <see cref="VersionCheck.DefaultNuGetBaseUrl"/>).</summary>
    public string? BaseUrl { get; init; }
    public Func<string, string?>? Env { get; init; }
}

/// <summary>
/// The startup update check. Upstream asks <c>https://pi.dev/api/latest-version</c>; PiSharp asks NuGet's flat container for the
/// versions of <c>PiSharp.Cli</c> and takes the highest one. Prereleases: a stable PiSharp only considers stable versions (the
/// suggested <c>dotnet tool update</c> installs stable versions only); a prerelease PiSharp considers every version, so it hears of
/// both the next prerelease and the release that follows it, and a prerelease suggestion carries <c>--prerelease</c>.
/// </summary>
internal static partial class VersionCheck
{
    public const string DefaultNuGetBaseUrl = "https://api.nuget.org/v3-flatcontainer";
    public const string PackageId = "PiSharp.Cli";
    public const string ChangelogUrl = "https://github.com/jkelly/PiSharp/blob/main/CHANGELOG.md";
    private const int DefaultVersionCheckTimeoutMs = 10000;
    private static readonly HashSet<int> RetryableStatusCodes = [408, 425, 429, 500, 502, 503, 504];
    private static readonly Lazy<HttpClient> SharedClient = new(() => new HttpClient { Timeout = Timeout.InfiniteTimeSpan });

    /// <summary>The flat-container version index of <c>PiSharp.Cli</c> (NuGet lowercases the id).</summary>
    public static string IndexUrl(string? baseUrl = null) =>
        $"{(baseUrl ?? DefaultNuGetBaseUrl).TrimEnd('/')}/{PackageId.ToLowerInvariant()}/index.json";

    /// <summary>The command the notice suggests for <paramref name="version"/>: <c>dotnet tool update -g PiSharp.Cli</c>, with
    /// <c>--prerelease</c> when the version is a prerelease (the plain command installs stable versions only).</summary>
    public static string UpdateCommand(string version) =>
        NuGetVersion.Parse(version) is { IsPrerelease: true } ? $"dotnet tool update -g {PackageId} --prerelease" : $"dotnet tool update -g {PackageId}";

    /// <summary>Include useful errno details hidden behind a generic transport error: codes of the cause (or of each error in an
    /// aggregate cause), else the first cause message. A code is <c>Exception.Data["code"]</c> or a socket error's errno name.</summary>
    public static string FormatVersionCheckError(object? error)
    {
        var rootMessage = error is Exception { Message.Length: > 0 } root ? root.Message : error?.ToString() ?? "undefined";
        var cause = (error as Exception)?.InnerException;
        IReadOnlyList<Exception> causes = cause is AggregateException aggregate ? aggregate.InnerExceptions : cause is null ? [] : [cause];
        var codes = causes.Select(ErrorCode).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        if (codes.Count > 0) return $"{rootMessage} ({string.Join(", ", codes)})";
        var causeMessage = causes.FirstOrDefault(value => value.Message.Length > 0)?.Message;
        return causeMessage is not null ? $"{rootMessage} (cause: {causeMessage})" : rootMessage;
    }

    private static string? ErrorCode(Exception error)
    {
        if (error.Data["code"] is string code) return code;
        if (error is SocketException socket)
        {
            return socket.SocketErrorCode switch
            {
                SocketError.TimedOut => "ETIMEDOUT",
                SocketError.NetworkUnreachable => "ENETUNREACH",
                SocketError.HostUnreachable => "EHOSTUNREACH",
                SocketError.ConnectionRefused => "ECONNREFUSED",
                SocketError.ConnectionReset => "ECONNRESET",
                SocketError.ConnectionAborted => "ECONNABORTED",
                SocketError.HostNotFound => "ENOTFOUND",
                SocketError.TryAgain => "EAI_AGAIN",
                SocketError.NetworkDown => "ENETDOWN",
                SocketError.AddressNotAvailable => "EADDRNOTAVAIL",
                _ => null,
            };
        }
        return null;
    }

    /// <summary>Source comparePackageVersions over NuGet versions: up to four numeric parts (missing parts are 0, so 1.1.0 equals
    /// 1.1.0.0), then prerelease labels; build metadata is ignored. Null when either side is not a version.</summary>
    public static int? ComparePackageVersions(string leftVersion, string rightVersion)
    {
        var left = NuGetVersion.Parse(leftVersion);
        var right = NuGetVersion.Parse(rightVersion);
        if (left is null || right is null) return null;
        return NuGetVersion.Compare(left, right);
    }

    /// <summary>Source isNewerPackageVersion: by version when both parse, else any difference after trimming counts as newer.</summary>
    public static bool IsNewerPackageVersion(string candidateVersion, string currentVersion)
    {
        var comparison = ComparePackageVersions(candidateVersion, currentVersion);
        if (comparison is { } value) return value > 0;
        return PiSharp.Tui.Pi.TextUtils.JsTrim(candidateVersion) != PiSharp.Tui.Pi.TextUtils.JsTrim(currentVersion);
    }

    /// <summary>The highest version in <paramref name="versions"/> PiSharp <paramref name="currentVersion"/> should be offered:
    /// stable versions only, unless the running version is itself a prerelease. Entries that are not versions are skipped.</summary>
    public static string? SelectLatestVersion(IEnumerable<string> versions, string currentVersion)
    {
        var includePrerelease = NuGetVersion.Parse(currentVersion) is { IsPrerelease: true };
        (string Text, NuGetVersion Parsed)? best = null;
        foreach (var text in versions)
        {
            if (NuGetVersion.Parse(text) is not { } parsed || (parsed.IsPrerelease && !includePrerelease)) continue;
            if (best is null || NuGetVersion.Compare(parsed, best.Value.Parsed) > 0) best = (PiSharp.Tui.Pi.TextUtils.JsTrim(text), parsed);
        }
        return best?.Text;
    }

    /// <summary>Source getLatestPiRelease: nothing with <c>PI_OFFLINE</c>; otherwise one GET of the NuGet version index (retried when
    /// requested), nothing on a non-OK status or an unexpected body. Transport errors throw.</summary>
    public static async Task<LatestRelease?> GetLatestRelease(string currentVersion, VersionCheckOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new();
        var env = options.Env ?? Environment.GetEnvironmentVariable;
        if (!string.IsNullOrEmpty(env("PI_OFFLINE"))) return null;

        var url = IndexUrl(options.BaseUrl);
        using var response = await FetchWithRetry(options.Http ?? SharedClient.Value, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", $"pisharp/{currentVersion} ({BugReport.Platform}; {BugReport.Runtime}; {BugReport.Arch})");
            request.Headers.TryAddWithoutValidation("accept", "application/json");
            return request;
        }, options.Retry ? 2 : 0, options.TimeoutMs ?? DefaultVersionCheckTimeoutMs, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(text);
        var data = document.RootElement;
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Array)
            return null;
        var latest = SelectLatestVersion(versions.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!),
            currentVersion);
        return latest is null ? null : new(latest);
    }

    /// <summary>Source checkForNewPiVersion, the automatic startup check: skipped with <c>PI_SKIP_VERSION_CHECK</c> (which
    /// <c>--offline</c> also sets), one request, never throws; the release only when it is newer than the running version.</summary>
    public static async Task<LatestRelease?> CheckForNewVersion(string currentVersion, VersionCheckOptions? options = null)
    {
        options ??= new();
        var env = options.Env ?? Environment.GetEnvironmentVariable;
        if (!string.IsNullOrEmpty(env("PI_SKIP_VERSION_CHECK"))) return null;
        try
        {
            var latestRelease = await GetLatestRelease(currentVersion, options with { Retry = false }).ConfigureAwait(false);
            return latestRelease is not null && IsNewerPackageVersion(latestRelease.Version, currentVersion) ? latestRelease : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>management-http.ts <c>fetchWithRetry</c> (retryOnStatus on, no per-attempt timeout): transport failures and
    /// retryable statuses are retried up to <paramref name="maxRetries"/> times within the overall <paramref name="timeoutMs"/>.</summary>
    private static async Task<HttpResponseMessage> FetchWithRetry(HttpMessageInvoker http, Func<HttpRequestMessage> createRequest, int maxRetries,
        int timeoutMs, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeoutMs > 0) timeout.CancelAfter(timeoutMs);
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (timeout.IsCancellationRequested) throw new TimeoutException("The operation was aborted due to timeout");
            using var request = createRequest();
            try
            {
                var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
                if (!(RetryableStatusCodes.Contains((int)response.StatusCode) && attempt < maxRetries)) return response;
                response.Dispose();
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested && !timeout.IsCancellationRequested && attempt < maxRetries)
            {
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("The operation was aborted due to timeout");
            }
        }
    }

    /// <summary>A NuGet (SemVer 2.0) version: one to four numeric parts, optional dot-separated prerelease labels and build metadata.
    /// Comparison follows NuGet: numeric parts, then a release above its prereleases, then label by label (numeric labels
    /// numerically and below alphanumeric ones, alphanumeric ones case-insensitively), more labels winning a common prefix.</summary>
    private sealed partial record NuGetVersion(long[] Parts, string[] Prerelease)
    {
        public bool IsPrerelease => Prerelease.Length > 0;

        [GeneratedRegex(@"^v?([0-9]+)(?:\.([0-9]+))?(?:\.([0-9]+))?(?:\.([0-9]+))?(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$",
            RegexOptions.CultureInvariant)]
        private static partial Regex Full();

        public static NuGetVersion? Parse(string version)
        {
            var trimmed = PiSharp.Tui.Pi.TextUtils.JsTrim(version);
            if (trimmed.Length > 256) return null;
            var match = Full().Match(trimmed);
            if (!match.Success) return null;
            var parts = new long[4];
            for (var index = 0; index < 4; index++)
            {
                var group = match.Groups[index + 1];
                if (!group.Success) continue;
                if (!int.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var part)) return null;
                parts[index] = part;
            }
            return new(parts, match.Groups[5].Success ? match.Groups[5].Value.Split('.') : []);
        }

        public static int Compare(NuGetVersion left, NuGetVersion right)
        {
            for (var index = 0; index < 4; index++)
                if (left.Parts[index] != right.Parts[index]) return left.Parts[index] < right.Parts[index] ? -1 : 1;
            if (left.IsPrerelease != right.IsPrerelease) return left.IsPrerelease ? -1 : 1;
            for (var index = 0; ; index++)
            {
                if (index >= left.Prerelease.Length && index >= right.Prerelease.Length) return 0;
                if (index >= right.Prerelease.Length) return 1;
                if (index >= left.Prerelease.Length) return -1;
                var comparison = CompareLabels(left.Prerelease[index], right.Prerelease[index]);
                if (comparison != 0) return comparison;
            }
        }

        private static int CompareLabels(string a, string b)
        {
            var aNumeric = a.All(char.IsAsciiDigit);
            var bNumeric = b.All(char.IsAsciiDigit);
            if (aNumeric && bNumeric)
            {
                var lengthA = a.TrimStart('0').Length;
                var lengthB = b.TrimStart('0').Length;
                if (lengthA != lengthB) return lengthA < lengthB ? -1 : 1;
                return Math.Sign(string.CompareOrdinal(a.TrimStart('0'), b.TrimStart('0')));
            }
            if (aNumeric) return -1;
            if (bNumeric) return 1;
            return Math.Sign(StringComparer.OrdinalIgnoreCase.Compare(a, b));
        }
    }
}
