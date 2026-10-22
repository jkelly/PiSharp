// Decision 0011 (run W2, WU8): how the chat pages tell a failed answer's kind from what the durable engine stored. A provider failure becomes
// an assistant entry with stopReason "error" and the exception's message (errorMessage); a dropped stream or a Stop becomes one with
// stopReason "aborted". The `expired` state (a sign-in that expired, or an account that was removed or revoked) has a stable marker: routing
// (WU6's AccountRouter) fails such a request with an exception whose message starts with "account_expired:" (AccountExpired builds it), and
// OAuth refresh failures that mean the same (invalid_grant, refresh_token_reused) count too. The marker is for the page only; Display strips it.
using System.Text.RegularExpressions;

namespace PiSharp.WebAgent.Chat;

/// <summary>What kind of failure an answer ended with.</summary>
public enum ChatFailureKind
{
    /// <summary>The answer finished.</summary>
    None,

    /// <summary>Stopped (the user's Stop, or a stream a restart cut off); the partial text stays.</summary>
    Aborted,

    /// <summary>The chat's account could not be used: its sign-in expired, or it was removed or revoked. Nothing was sent.</summary>
    Expired,

    /// <summary>The provider request failed.</summary>
    Error,
}

/// <summary>Classifies stored failures, and builds the expired marker that routing uses.</summary>
public static partial class ChatFailures
{
    /// <summary>The code of an account that can no longer be used.</summary>
    public const string AccountExpiredCode = "account_expired";

    /// <summary>The prefix of an <see cref="ChatFailureKind.Expired"/> failure's message.</summary>
    public const string AccountExpiredPrefix = AccountExpiredCode + ":";

    /// <summary>The message routing fails a request with when the chat's account can't be used (never another account instead):
    /// <c>account_expired: &lt;detail&gt;</c>.</summary>
    /// <param name="providerId">The provider, such as <c>openai-codex</c>.</param>
    /// <param name="detail">Why, without any secret (such as <c>refresh_token_reused</c>, or "the account was removed").</param>
    public static string AccountExpired(string providerId, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerId);
        return $"{AccountExpiredPrefix} The sign-in for {providerId} expired or was removed{(string.IsNullOrWhiteSpace(detail) ? "" : $" ({detail.Trim()})")}. Sign in again, then Retry.";
    }

    /// <summary>The kind of an assistant message's ending.</summary>
    public static ChatFailureKind Classify(string? stopReason, string? errorMessage)
    {
        if (stopReason == "aborted") return ChatFailureKind.Aborted;
        if (stopReason != "error" && string.IsNullOrEmpty(errorMessage)) return ChatFailureKind.None;
        return IsExpired(errorMessage) ? ChatFailureKind.Expired : ChatFailureKind.Error;
    }

    /// <summary>Whether a failure says the account can't be used: the marker (also where the engine put words before it), or an OAuth refresh
    /// that was refused.</summary>
    public static bool IsExpired(string? errorMessage) =>
        errorMessage is { Length: > 0 } text && (text.Contains(AccountExpiredPrefix, StringComparison.Ordinal) || RefusedRefresh().IsMatch(text));

    /// <summary>A failure's message for display: from after the marker when it has one, trimmed.</summary>
    public static string Display(string? errorMessage)
    {
        var text = (errorMessage ?? "").Trim();
        var at = text.IndexOf(AccountExpiredPrefix, StringComparison.Ordinal);
        if (at >= 0) text = text[(at + AccountExpiredPrefix.Length)..].Trim();
        return text;
    }

    [GeneratedRegex(@"\b(invalid_grant|refresh_token_reused|refresh_token_expired)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RefusedRefresh();
}
