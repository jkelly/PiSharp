// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/utils/overflow.ts.
using System.Text.Json;
using System.Text.RegularExpressions;
using PiSharp.Contracts;

namespace PiSharp.CodingAgent;

/// <summary>Native implementation of the pinned Pi overflow helpers. This class owns no retry or transport.</summary>
public static class SessionRecoveryClassifier
{
    // ASCII folding matches JavaScript non-Unicode /i for these ASCII patterns. Explicit classes
    // retain JavaScript digit, whitespace and dot semantics instead of .NET Unicode defaults.
    private const string Space = @"[\u0009-\u000D\u0020\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]";
    private static readonly Regex[] Overflow = new[]
    {
        @"prompt (?:is )?too long", @"prompt exceeds max length", @"request_too_large", @"input is too long for requested model",
        @"exceeds the context window", @"exceeds (?:the )?(?:model'?s )?maximum context length(?: of [\d,]+ tokens?|\s*\([\d,]+\))",
        @"input token count.*exceeds the maximum", @"maximum prompt length is \d+", @"reduce the length of the messages",
        @"maximum context length is \d+ tokens", @"exceeds (?:the )?maximum allowed input length of [\d,]+ tokens?",
        @"input \(\d+ tokens\) is longer than the model'?s context length \(\d+ tokens\)", @"exceeds the limit of \d+",
        @"exceeds the available context size", @"greater than the context length", @"context window exceeds limit",
        @"exceeded model token limit", @"too large for model with \d+ maximum context length",
        @"prompt has [\d,]+ tokens?, but the configured context size is [\d,]+ tokens?",
        @"model_context_window_exceeded", @"prompt too long; exceeded (?:max )?context length",
        @"range of input length should be", @"context[_ ]length[_ ]exceeded", @"too many tokens", @"token limit exceeded"
    }.Select(Compile).ToArray();
    private static readonly Regex Bodyless = Compile(@"^4(?:00|13)\s*(?:status code)?\s*\(no body\)");
    private static readonly Regex[] NonOverflow = new[] { @"^(throttling error|service unavailable):", @"rate limit", @"too many requests" }.Select(Compile).ToArray();

    public static bool IsContextOverflow(AssistantMessage message, double? contextWindow = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.StopReason == StopReason.Error && message.ExtraProperties?.TryGet("errorMessage", out var error) == true &&
            error!.Value.ValueKind == JsonValueKind.String && error.Value.GetString() is { Length: > 0 } text)
        {
            var folded = string.Create(text.Length, text, (chars, value) =>
            { for (var i=0;i<value.Length;i++) chars[i]=value[i] is >= 'A' and <= 'Z' ? (char)(value[i]+32) : value[i]; });
            if (!NonOverflow.Any(p=>p.IsMatch(folded)) && (Overflow.Any(p=>p.IsMatch(folded)) ||
                message.Provider == "cerebras" && Bodyless.IsMatch(folded))) return true;
        }
        if (contextWindow is not { } window || window == 0 || double.IsNaN(window)) return false;
        var input = (double)message.Usage.Input + message.Usage.CacheRead;
        return message.StopReason == StopReason.Stop && input > window ||
            message.StopReason == StopReason.Length && message.Usage.Output == 0 && input >= window * .99;
    }
    public static bool IsRecoverableLength(AssistantMessage message, double desiredMaxOutput) =>
        message.StopReason == StopReason.Length && desiredMaxOutput > 0 && message.Usage.Output < desiredMaxOutput;
    private static Regex Compile(string pattern) => new(pattern.Replace(@"[\d,]", "[0-9,]", StringComparison.Ordinal).Replace(@"\d", "[0-9]", StringComparison.Ordinal)
        .Replace(@"\s", Space, StringComparison.Ordinal).Replace(".*", @"[^\r\n\u2028\u2029]*", StringComparison.Ordinal),
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
}
