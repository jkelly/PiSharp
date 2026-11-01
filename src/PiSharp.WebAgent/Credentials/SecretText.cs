// Decision 0011 (run W2, WU5): the only forms in which a secret's text may leave the credentials area. A page sees a masked hint (a known
// key prefix such as "sk-proj-", an ellipsis and the last 4 characters, and nothing at all for a short value); messages from providers and
// auth servers are scrubbed before they are shown or logged, because an error can echo a key or carry a token in a JSON body.
using System.Text.RegularExpressions;

namespace PiSharp.WebAgent.Credentials;

/// <summary>Masked hints and scrubbed messages.</summary>
public static partial class SecretText
{
    /// <summary>The hint of a value too short to show any part of.</summary>
    public const string Hidden = "••••";

    /// <summary>The longest message <see cref="Scrub"/> returns.</summary>
    public const int MaximumMessageLength = 240;

    // Well-known, non-secret key prefixes (the key's type), longest first so "sk-proj-" wins over "sk-".
    private static readonly string[] Prefixes =
    [
        "github_pat_", "sk-ant-api03-", "sk-ant-", "sk-proj-", "sk-svcacct-", "sk-or-v1-", "sk-or-", "nvapi-", "sk-", "gsk_", "xai-",
        "csk-", "hf_", "fw_", "ghu_", "gho_", "ghp_", "AIza",
    ];

    /// <summary>The masked hint of <paramref name="secret"/>: empty for none, <see cref="Hidden"/> below 12 characters, otherwise a known
    /// prefix (when it is at most a third of the value), an ellipsis and the last 4 characters, such as <c>sk-proj-…9Lw2</c>.</summary>
    public static string Mask(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return "";
        if (secret.Length < 12) return Hidden;
        var prefix = Prefixes.FirstOrDefault(known => secret.StartsWith(known, StringComparison.Ordinal) && known.Length * 3 <= secret.Length) ?? "";
        return prefix + "…" + secret[^4..];
    }

    [GeneratedRegex(@"\{.*\}", RegexOptions.Singleline)]
    private static partial Regex JsonBody();

    // Keys and token segments (JWT parts are split by dots, base64 by slashes); host names and URL paths stay readable.
    [GeneratedRegex(@"[A-Za-z0-9_\-~+=]{24,}")]
    private static partial Regex LongToken();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>A message safe to show and log: every one of <paramref name="secrets"/> replaced, JSON bodies collapsed to <c>{…}</c>,
    /// token-like runs of 24 or more characters replaced by <c>…</c>, whitespace folded, and at most <see cref="MaximumMessageLength"/>
    /// characters.</summary>
    public static string Scrub(string? message, params string?[] secrets)
    {
        var text = message ?? "";
        foreach (var secret in secrets)
            if (!string.IsNullOrEmpty(secret) && secret.Length >= 4) text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
        text = JsonBody().Replace(text, "{…}");
        text = LongToken().Replace(text, "…");
        text = Whitespace().Replace(text, " ").Trim();
        return text.Length <= MaximumMessageLength ? text : text[..(MaximumMessageLength - 1)] + "…";
    }
}
