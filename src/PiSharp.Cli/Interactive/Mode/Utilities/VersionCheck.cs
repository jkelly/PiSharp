// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/utils/version-check.ts, with management-http.ts
// (fetchWithRetry) and the semver valid/compare it uses. The HTTP client and environment are injectable.
using System.Net.Sockets;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using PiSharp.CodingAgent.Diagnostics;

namespace PiSharp.Cli.Interactive.Mode.Utilities;

/// <summary>Source <c>LatestPiRelease</c>.</summary>
internal sealed record LatestPiRelease(string Version, string? PackageName = null, string? Note = null);

/// <summary>Source <c>{ timeoutMs?, retry? }</c> plus the injected HTTP client and environment.</summary>
internal sealed record VersionCheckOptions
{
    public int? TimeoutMs { get; init; }
    public bool Retry { get; init; }
    public HttpMessageInvoker? Http { get; init; }
    public Func<string, string?>? Env { get; init; }
}

internal static partial class VersionCheck
{
    public const string LatestVersionUrl = "https://pi.dev/api/latest-version";
    private const int DefaultVersionCheckTimeoutMs = 10000;
    private static readonly HashSet<int> RetryableStatusCodes = [408, 425, 429, 500, 502, 503, 504];
    private static readonly Lazy<HttpClient> SharedClient = new(() => new HttpClient { Timeout = Timeout.InfiniteTimeSpan });

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

    public static int? ComparePackageVersions(string leftVersion, string rightVersion)
    {
        var left = SemVer.Parse(PiSharp.Tui.Pi.TextUtils.JsTrim(leftVersion));
        var right = SemVer.Parse(PiSharp.Tui.Pi.TextUtils.JsTrim(rightVersion));
        if (left is null || right is null) return null;
        return SemVer.Compare(left, right);
    }

    public static bool IsNewerPackageVersion(string candidateVersion, string currentVersion)
    {
        var comparison = ComparePackageVersions(candidateVersion, currentVersion);
        if (comparison is { } value) return value > 0;
        return PiSharp.Tui.Pi.TextUtils.JsTrim(candidateVersion) != PiSharp.Tui.Pi.TextUtils.JsTrim(currentVersion);
    }

    public static async Task<LatestPiRelease?> GetLatestPiRelease(string currentVersion, VersionCheckOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new();
        var env = options.Env ?? Environment.GetEnvironmentVariable;
        if (!string.IsNullOrEmpty(env("PI_OFFLINE"))) return null;

        using var response = await FetchWithRetry(options.Http ?? SharedClient.Value, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, LatestVersionUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", BugReport.PiUserAgent(currentVersion));
            request.Headers.TryAddWithoutValidation("accept", "application/json");
            return request;
        }, options.Retry ? 2 : 0, options.TimeoutMs ?? DefaultVersionCheckTimeoutMs, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(text);
        var data = document.RootElement;
        // `data.version` on a JSON null throws a TypeError upstream; other non-objects read undefined.
        if (data.ValueKind == JsonValueKind.Null) throw new InvalidOperationException("Cannot read properties of null (reading 'version')");
        if (data.ValueKind != JsonValueKind.Object) return null;
        string? Text(string name) => data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var version = Text("version");
        if (version is null || PiSharp.Tui.Pi.TextUtils.JsTrim(version).Length == 0) return null;
        var packageName = Text("packageName") is { } name && PiSharp.Tui.Pi.TextUtils.JsTrim(name).Length > 0 ? PiSharp.Tui.Pi.TextUtils.JsTrim(name) : null;
        var note = Text("note") is { } rawNote && PiSharp.Tui.Pi.TextUtils.JsTrim(rawNote).Length > 0 ? PiSharp.Tui.Pi.TextUtils.JsTrim(rawNote) : null;
        return new(PiSharp.Tui.Pi.TextUtils.JsTrim(version), packageName, note);
    }

    public static async Task<string?> GetLatestPiVersion(string currentVersion, VersionCheckOptions? options = null,
        CancellationToken cancellationToken = default) =>
        (await GetLatestPiRelease(currentVersion, options, cancellationToken).ConfigureAwait(false))?.Version;

    /// <summary>The automatic startup check: skipped with <c>PI_SKIP_VERSION_CHECK</c>, one request, never throws.</summary>
    public static async Task<LatestPiRelease?> CheckForNewPiVersion(string currentVersion, VersionCheckOptions? options = null)
    {
        options ??= new();
        var env = options.Env ?? Environment.GetEnvironmentVariable;
        if (!string.IsNullOrEmpty(env("PI_SKIP_VERSION_CHECK"))) return null;
        try
        {
            var latestRelease = await GetLatestPiRelease(currentVersion, options with { Retry = false }).ConfigureAwait(false);
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

    /// <summary>The semver package's <c>valid</c> (strict: optional leading <c>v</c>) and <c>compare</c> (main, then prerelease).</summary>
    private sealed partial record SemVer(BigInteger Major, BigInteger Minor, BigInteger Patch, string[] Prerelease)
    {
        private const string NumericIdentifier = "0|[1-9][0-9]*";
        private const string PrereleaseIdentifier = "(?:" + NumericIdentifier + "|[0-9]*[a-zA-Z-][a-zA-Z0-9-]*)";

        [GeneratedRegex("^v?(" + NumericIdentifier + @")\.(" + NumericIdentifier + @")\.(" + NumericIdentifier + ")" +
            @"(?:-(" + PrereleaseIdentifier + @"(?:\." + PrereleaseIdentifier + @")*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$")]
        private static partial Regex Full();

        private static readonly BigInteger MaxSafeInteger = new(9007199254740991);

        public static SemVer? Parse(string version)
        {
            if (version.Length > 256) return null;
            var match = Full().Match(version);
            if (!match.Success) return null;
            var major = BigInteger.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var minor = BigInteger.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            var patch = BigInteger.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (major > MaxSafeInteger || minor > MaxSafeInteger || patch > MaxSafeInteger) return null;
            var prerelease = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
            return new(major, minor, patch, prerelease);
        }

        public static int Compare(SemVer left, SemVer right)
        {
            var main = left.Major != right.Major ? left.Major.CompareTo(right.Major)
                : left.Minor != right.Minor ? left.Minor.CompareTo(right.Minor) : left.Patch.CompareTo(right.Patch);
            if (main != 0) return Math.Sign(main);
            if (left.Prerelease.Length > 0 && right.Prerelease.Length == 0) return -1;
            if (left.Prerelease.Length == 0 && right.Prerelease.Length > 0) return 1;
            if (left.Prerelease.Length == 0 && right.Prerelease.Length == 0) return 0;
            for (var index = 0; ; index++)
            {
                if (index >= left.Prerelease.Length && index >= right.Prerelease.Length) return 0;
                if (index >= right.Prerelease.Length) return 1;
                if (index >= left.Prerelease.Length) return -1;
                var comparison = CompareIdentifiers(left.Prerelease[index], right.Prerelease[index]);
                if (comparison != 0) return comparison;
            }
        }

        private static int CompareIdentifiers(string a, string b)
        {
            var aNumeric = a.All(char.IsAsciiDigit);
            var bNumeric = b.All(char.IsAsciiDigit);
            if (aNumeric && bNumeric)
            {
                var difference = BigInteger.Parse(a, System.Globalization.CultureInfo.InvariantCulture)
                    .CompareTo(BigInteger.Parse(b, System.Globalization.CultureInfo.InvariantCulture));
                return Math.Sign(difference);
            }
            if (a == b) return 0;
            if (aNumeric) return -1;
            if (bNumeric) return 1;
            return string.CompareOrdinal(a, b) < 0 ? -1 : 1;
        }
    }
}
