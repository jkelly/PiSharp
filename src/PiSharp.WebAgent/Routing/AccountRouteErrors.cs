// Decision 0011 (run W2, WU6): the error a chat's request ends with when its account cannot be used: unknown, removed, expired (a revoked
// sign-in, a rejected key), unavailable (an unset variable, nothing stored), or not the model's provider. The request is never sent with
// another account instead. The text is the assistant message's errorMessage, which the chat page shows; it starts with a stable prefix the
// pages match to show the "expired" state (banner, "Not sent: sign-in expired.", Retry), names the reason in parentheses, and holds no
// account id, label, key or provider text. It is worded so the durable agent's retry rules (PiAiRetry: "overloaded", "429", "network
// error", "try again", ...) never match it: retrying would only fail again.
namespace PiSharp.WebAgent.Routing;

/// <summary>The errors a chat's request ends with when its account cannot be used, and how pages recognize them.</summary>
public static class AccountRouteErrors
{
    /// <summary>The prefix of every account error: <c>Account expired (</c>, then the reason and <c>): </c>.</summary>
    public const string Prefix = "Account expired (";

    /// <summary>The account was rejected: a revoked or unrefreshable sign-in, or a key the provider answered 401 for. It is marked expired.</summary>
    public const string Expired = "expired";

    /// <summary>The account was removed (or never existed).</summary>
    public const string Removed = "removed";

    /// <summary>The account has no usable credential now: an unset variable, nothing stored, a file that cannot be read.</summary>
    public const string Unavailable = "unavailable";

    /// <summary>The chat has no account (its provider had none when it was created).</summary>
    public const string Unbound = "none";

    /// <summary>The chat's account belongs to another provider than the request's model.</summary>
    public const string Mismatch = "mismatch";

    /// <summary>Whether <paramref name="errorMessage"/> is an account error.</summary>
    public static bool IsAccountError(string? errorMessage) => errorMessage is not null && errorMessage.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>The reason of an account error (<see cref="Expired"/>, <see cref="Removed"/>, <see cref="Unavailable"/>, <see cref="Unbound"/>,
    /// <see cref="Mismatch"/>), or null for another error.</summary>
    public static string? Reason(string? errorMessage)
    {
        if (!IsAccountError(errorMessage)) return null;
        var end = errorMessage!.IndexOf(')', Prefix.Length);
        return end < 0 ? null : errorMessage[Prefix.Length..end];
    }

    /// <summary>The sentence after the prefix, for a page that shows its own heading; null for another error.</summary>
    public static string? Detail(string? errorMessage)
    {
        if (!IsAccountError(errorMessage)) return null;
        var end = errorMessage!.IndexOf("): ", Prefix.Length, StringComparison.Ordinal);
        return end < 0 ? null : errorMessage[(end + 3)..];
    }

    /// <summary>The error text of <paramref name="reason"/> for a chat of <paramref name="providerName"/>.</summary>
    internal static string Message(string reason, string providerName) => Prefix + reason + "): " + reason switch
    {
        Expired => $"the {providerName} account this chat uses was rejected. Sign in again or replace its key in Settings, then send the message again.",
        Removed => $"the {providerName} account this chat uses was removed. Add it again in Settings, or start a new chat with another account.",
        Unavailable => $"the {providerName} account this chat uses has no usable credential now. Check it in Settings, then send the message again.",
        Unbound => $"this chat has no {providerName} account. Add one in Settings, then start a new chat.",
        Mismatch => $"this chat's account is not a {providerName} account, so the request was not sent.",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    /// <summary>The error text of <paramref name="reason"/> for a custom provider (W3, CP): its key is a vault account, the file's
    /// <paramref name="variable"/> reference, or none, and it is changed in Settings › Custom providers.</summary>
    internal static string CustomMessage(string reason, string providerName, string? variable) => Prefix + reason + "): " + reason switch
    {
        Expired => $"{providerName} rejected its key. Replace the key in Settings › Custom providers, then send the message again.",
        Unavailable when variable is not null =>
            $"{providerName} reads its key from the {variable} environment variable, which is not set on this machine. Set it, or change the key in Settings › Custom providers, then send the message again.",
        Unavailable => $"{providerName} has no usable key now. Check it in Settings › Custom providers, then send the message again.",
        _ => Message(reason, providerName),
    };

    /// <summary>A sign-in refresh that failed on the network: retryable (it names a network error), and the account stays connected.</summary>
    internal static string RefreshNetwork(string providerName) =>
        $"Network error: the {providerName} sign-in could not be refreshed. The account is unchanged.";
}

/// <summary>A request that cannot be sent with its chat's account; its message is an <see cref="AccountRouteErrors"/> text.</summary>
public sealed class AccountRouteException(string reason, string message) : InvalidOperationException(message)
{
    /// <summary>The reason (<see cref="AccountRouteErrors.Expired"/>, ...).</summary>
    public string Reason { get; } = reason;
}
