// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/utils/overflow.ts (isContextOverflow) over a pi-ai wire assistant
// message. PiSharp.CodingAgent's SessionRecoveryClassifier ports the same function over PiSharp.Contracts messages, but PiSharp.Durable does
// not reference PiSharp.CodingAgent; this port uses its regex translation, and the test project compares the two on shared inputs.
//
// JavaScript semantics kept: /i on these ASCII patterns folds only ASCII letters (the message is ASCII-lowercased instead of IgnoreCase,
// which would fold non-ASCII letters too); `\d`, `\s` and `.` use JavaScript's digit, whitespace and line-terminator sets.
using System.Text.RegularExpressions;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Internal.PiAi;

/// <summary>Context overflow detection (upstream <c>utils/overflow.ts</c>).</summary>
internal static class PiAiOverflow
{
    private const string Space = @"[\u0009-\u000D    -     　﻿]";

    private static readonly Regex[] OverflowPatterns = new[]
    {
        @"prompt (?:is )?too long", // Anthropic and z.ai token overflow
        @"prompt exceeds max length", // z.ai CN endpoint token overflow
        @"request_too_large", // Anthropic request byte-size overflow (HTTP 413)
        @"input is too long for requested model", // Amazon Bedrock
        @"exceeds the context window", // OpenAI (Completions & Responses API)
        @"exceeds (?:the )?(?:model'?s )?maximum context length(?: of [\d,]+ tokens?|\s*\([\d,]+\))", // OpenAI-compatible proxies (LiteLLM)
        @"input token count.*exceeds the maximum", // Google (Gemini)
        @"maximum prompt length is \d+", // xAI (Grok)
        @"reduce the length of the messages", // Groq
        @"maximum context length is \d+ tokens", // OpenRouter (most backends)
        @"exceeds (?:the )?maximum allowed input length of [\d,]+ tokens?", // OpenRouter/Poolside
        @"input \(\d+ tokens\) is longer than the model'?s context length \(\d+ tokens\)", // Together AI
        @"exceeds the limit of \d+", // GitHub Copilot
        @"exceeds the available context size", // llama.cpp server
        @"greater than the context length", // LM Studio
        @"context window exceeds limit", // MiniMax
        @"exceeded model token limit", // Kimi For Coding
        @"too large for model with \d+ maximum context length", // Mistral
        @"prompt has [\d,]+ tokens?, but the configured context size is [\d,]+ tokens?", // DS4 server
        @"model_context_window_exceeded", // z.ai non-standard finish_reason surfaced as error text
        @"prompt too long; exceeded (?:max )?context length", // Ollama explicit overflow error
        @"range of input length should be", // DashScope / Qwen Token Plan
        @"context[_ ]length[_ ]exceeded", // Generic fallback
        @"too many tokens", // Generic fallback
        @"token limit exceeded", // Generic fallback
    }.Select(Compile).ToArray();

    private static readonly Regex CerebrasBodylessOverflow = Compile(@"^4(?:00|13)\s*(?:status code)?\s*\(no body\)");

    /// <summary>Errors that are not overflows even when an overflow pattern also matches (throttling, rate limits).</summary>
    private static readonly Regex[] NonOverflowPatterns =
        new[] { @"^(throttling error|service unavailable):", @"rate limit", @"too many requests" }.Select(Compile).ToArray();

    /// <summary>
    /// Whether an assistant message is a context overflow (upstream <c>isContextOverflow</c>): an error matching a provider pattern, a
    /// silent overflow whose input exceeds <paramref name="contextWindow"/>, or a length stop with no output that fills it.
    /// </summary>
    public static bool IsContextOverflow(JsonObjectValue message, double? contextWindow = null)
    {
        var stopReason = Str(message, "stopReason");
        if (stopReason == "error" && Str(message, "errorMessage") is { Length: > 0 } error)
        {
            var folded = FoldAscii(error);
            if (!NonOverflowPatterns.Any(pattern => pattern.IsMatch(folded)))
            {
                if (OverflowPatterns.Any(pattern => pattern.IsMatch(folded))) return true;
                if (Str(message, "provider") == "cerebras" && CerebrasBodylessOverflow.IsMatch(folded)) return true;
            }
        }
        // `contextWindow &&`: absent, 0 and NaN skip the usage checks.
        if (contextWindow is not { } window || window == 0 || double.IsNaN(window)) return false;
        var usage = message.TryGetValue("usage", out var found) && found is JsonObjectValue value ? value : JsonObjectValue.Empty;
        var input = Number(usage, "input") + Number(usage, "cacheRead");
        if (stopReason == "stop" && input > window) return true;
        return stopReason == "length" && Number(usage, "output") == 0 && input >= window * 0.99;
    }

    private static string FoldAscii(string text) => string.Create(text.Length, text, static (chars, value) =>
    {
        for (var index = 0; index < value.Length; index++) chars[index] = value[index] is >= 'A' and <= 'Z' ? (char)(value[index] + 32) : value[index];
    });

    private static Regex Compile(string pattern) => new(pattern
            .Replace(@"[\d,]", "[0-9,]", StringComparison.Ordinal).Replace(@"\d", "[0-9]", StringComparison.Ordinal)
            .Replace(@"\s", Space, StringComparison.Ordinal).Replace(".*", @"[^\r\n  ]*", StringComparison.Ordinal),
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static string? Str(JsonObjectValue value, string key) => value.TryGetValue(key, out var field) && field is JsonStringValue text ? text.Value : null;

    private static double Number(JsonObjectValue value, string key) =>
        value.TryGetValue(key, out var field) && field is JsonNumberValue number ? number.Value : double.NaN;
}
