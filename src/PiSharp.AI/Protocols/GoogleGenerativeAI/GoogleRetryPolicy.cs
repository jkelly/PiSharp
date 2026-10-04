// Pi v0.99.1 utils/provider-retry.ts and api/google-shared.ts (MIT), commit d86654abb8862e201933517d6f1fce9f88dd117f.
using System.Globalization;
using System.Text.RegularExpressions;

namespace PiSharp.AI.Protocols.GoogleGenerativeAI;

internal static class GoogleRetryPolicy
{
    // ECMAScript whitespace and ASCII digits, followed by the valid parseFloat prefix.
    private static readonly Regex FloatPrefix = new(@"^[\u0009-\u000D\u0020\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]*([+-]?(?:Infinity|(?:[0-9]+\.?[0-9]*|\.[0-9]+)(?:[eE][+-]?[0-9]+)?))",
        RegexOptions.CultureInvariant);

    internal static bool Retryable(HttpResponseMessage response, GoogleGenerativeAIOptions options)
    {
        var directive = Header(response, "x-should-retry", options.MaximumHeaderCharacters);
        return directive switch { "true" => true, "false" => false,
            _ => (int)response.StatusCode is 408 or 409 or 429 || (int)response.StatusCode >= 500 };
    }
    private static string? Header(HttpResponseMessage response, string name, int maximumCharacters)
    {
        if (!response.Headers.TryGetValues(name, out var values) && !response.Content.Headers.TryGetValues(name, out values)) return null;
        var parts = new List<string>(); long length = 0;
        foreach (var value in values)
        {
            length += value.Length + (parts.Count == 0 ? 0 : 2);
            if (length > maximumCharacters) throw GoogleData.Fail(GoogleFailure.ResourceLimit);
            parts.Add(value);
        }
        return string.Join(", ", parts).Trim(' ', '\t');
    }
    internal static TimeSpan Delay(HttpResponseMessage response, GoogleGenerativeAIOptions options, int retryIndex, string providerErrorMessage)
    {
        var milliseconds = Header(response, "retry-after-ms", options.MaximumHeaderCharacters);
        if (!string.IsNullOrEmpty(milliseconds) && TryFloat(milliseconds, out var value)) return ServerDelay(value, options, providerErrorMessage);
        var after = Header(response, "retry-after", options.MaximumHeaderCharacters);
        if (!string.IsNullOrEmpty(after))
        {
            var delay = TryFloat(after, out var seconds) ? seconds * 1000 :
                DateTimeOffset.TryParse(after, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ?
                    (date - options.RetryTimeProvider.GetUtcNow()).TotalMilliseconds : double.NaN;
            return ServerDelay(delay, options, providerErrorMessage);
        }
        var jitter = options.RetryJitterSample();
        if (!double.IsFinite(jitter) || jitter is < 0 or >= 1) throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
        return TimeSpan.FromMilliseconds(Math.Min(500 * Math.Pow(2, retryIndex), 8000) * (1 - jitter * 0.25));
    }
    private static TimeSpan ServerDelay(double milliseconds, GoogleGenerativeAIOptions options, string providerErrorMessage)
    {
        if (options.MaxRetryDelayMilliseconds > 0 && milliseconds > options.MaxRetryDelayMilliseconds)
            throw new GoogleGenerativeAIException(GoogleFailure.ProviderError,
                FormattableString.Invariant($"Server requested {Math.Ceiling(milliseconds / 1000)}s retry delay (max: {Math.Ceiling(options.MaxRetryDelayMilliseconds / 1000d)}s). {providerErrorMessage}"));
        if (double.IsNaN(milliseconds) || milliseconds <= 0) return TimeSpan.Zero;
        // Native timer admission is explicit; JS's overflowing setTimeout behavior is
        // outside this finite native profile.
        if (!double.IsFinite(milliseconds) || milliseconds > int.MaxValue - 1)
            throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
        return TimeSpan.FromMilliseconds(milliseconds);
    }
    private static bool TryFloat(string text, out double value)
    {
        var match = FloatPrefix.Match(text);
        var prefix = match.Groups[1].Value;
        if (match.Success && prefix is "Infinity" or "+Infinity" or "-Infinity")
        { value = prefix[0] == '-' ? double.NegativeInfinity : double.PositiveInfinity; return true; }
        return double.TryParse(prefix, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
