using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.OpenAICompletions;

/// <summary>Native retry decision after rejected ownership closes; contains no private request or error data.</summary>
public sealed record CompletionsRetryObservation(int RetryIndex, int? Status, TimeSpan Delay);

/// <summary>Explicit bounded outer retries. Zero retries preserves one send; zero server-delay cap disables that cap.</summary>
public sealed record CompletionsRetryOptions(int MaxRetries = 0, int MaxRetryDelayMilliseconds = 60_000)
{
    public int MaximumRequestBodyBytes { get; init; } = 1_048_576;
    public int MaximumRequestHeaders { get; init; } = 128;
    public int MaximumRequestOptions { get; init; } = 128;
    public int MaximumRequestHeaderCharacters { get; init; } = 32_768;
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    public Action<CompletionsRetryObservation>? OnRetry { get; init; }

    internal void Validate()
    {
        if (MaxRetries is < 0 or > 32 || MaxRetryDelayMilliseconds < 0 || MaximumRequestBodyBytes is < 2 or > 8_388_608 ||
            MaximumRequestHeaders is < 1 or > 4096 || MaximumRequestOptions is < 1 or > 4096 ||
            MaximumRequestHeaderCharacters is < 1 or > 1_048_576 || TimeProvider is null)
            throw new ArgumentOutOfRangeException(nameof(CompletionsRetryOptions), "Invalid Completions retry limits.");
    }
}

internal static partial class CompletionsRetryPolicy
{
    internal static bool Retryable(int? status, string? directive = null) => directive switch
    {
        "true" => true, "false" => false, _ => status is null or 408 or 409 or 429 || status >= 500
    };

    internal static string? Header(HttpResponseMessage response, string name, int maximumCharacters)
    {
        if (!response.Headers.TryGetValues(name, out var values) && !response.Content.Headers.TryGetValues(name, out values)) return null;
        var parts = new List<string>(); long length = 0;
        foreach (var value in values)
        {
            length += value.Length + (parts.Count == 0 ? 0 : 2);
            if (length > maximumCharacters) throw new StreamLimitException("Completions retry header exceeds configured limits.");
            parts.Add(value);
        }
        return string.Join(", ", parts).Trim(' ', '\t');
    }

    internal static TimeSpan Delay(CompletionsRetryOptions options, int retryIndex, HttpResponseMessage? response, int maximumHeaderCharacters)
    {
        double delay;
        var milliseconds = response is null ? null : Header(response, "retry-after-ms", maximumHeaderCharacters);
        if (!string.IsNullOrEmpty(milliseconds) && TryFloat(milliseconds, out delay)) return ServerDelay(options, delay);
        var retryAfter = response is null ? null : Header(response, "retry-after", maximumHeaderCharacters);
        if (!string.IsNullOrEmpty(retryAfter))
        {
            if (TryFloat(retryAfter, out var seconds)) delay = seconds * 1000;
            else delay = DateTimeOffset.TryParse(retryAfter, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
                ? (date - options.TimeProvider.GetUtcNow()).TotalMilliseconds : 0;
            return ServerDelay(options, delay);
        }
        return TimeSpan.FromMilliseconds(Math.Min(500 * Math.Pow(2, retryIndex), 8000) * (1 - Random.Shared.NextDouble() * 0.25));
    }

    private static TimeSpan ServerDelay(CompletionsRetryOptions options, double milliseconds)
    {
        if (options.MaxRetryDelayMilliseconds > 0 && milliseconds > options.MaxRetryDelayMilliseconds)
            throw new StreamProtocolException("Completions server retry delay exceeds configured limits.");
        if (double.IsNegativeInfinity(milliseconds)) return TimeSpan.Zero;
        // A native timer cannot safely represent JS's overflowing/infinite timeout behavior.
        if (!double.IsFinite(milliseconds) || milliseconds > int.MaxValue - 1)
            throw new StreamLimitException("Completions retry delay exceeds native timer limits.");
        return TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
    }

    private static bool TryFloat(string value, out double number)
    {
        var match = FloatPrefix().Match(value);
        if (match.Success && match.Value.Trim() is "Infinity" or "+Infinity" or "-Infinity")
        { number = match.Value.Trim()[0] == '-' ? double.NegativeInfinity : double.PositiveInfinity; return true; }
        return double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    [GeneratedRegex(@"^\s*[+-]?(?:Infinity|(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex FloatPrefix();
}
