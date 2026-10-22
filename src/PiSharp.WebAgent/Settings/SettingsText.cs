// Decision 0011 (run W2, WU9): the wording the settings pages share, so a provider row, an account card and a model row say the same thing
// the same way: an account's kind and state, relative times ("in 9 days", "2h ago"), a model's thinking levels ("low → max") and price. Every
// value here is metadata (a masked hint at most); none of it is a secret.
using System.Globalization;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Credentials;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Shell;

namespace PiSharp.WebAgent.Settings;

/// <summary>Short texts for the settings pages.</summary>
public static class SettingsText
{
    /// <summary>The short level names of the Models table's level chips (<see cref="ThinkingLevels.Ordered"/> order).</summary>
    public static IReadOnlyDictionary<string, string> ShortLevels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["off"] = "off", ["minimal"] = "min", ["low"] = "low", ["medium"] = "med", ["high"] = "high", ["xhigh"] = "xhi", ["max"] = "max",
    };

    /// <summary>What an account is, as its chip says: <c>stored key</c>, <c>env · OPENAI_API_KEY</c>, <c>ChatGPT Plus</c>, <c>OAuth · github.com</c>.</summary>
    public static string KindChip(AccountInfo account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return account.Kind switch
        {
            AccountKind.ApiKey => account.Hint.Length == 0 ? "stored settings" : "stored key",
            AccountKind.Environment => "env · " + (account.Source ?? "variable"),
            AccountKind.Ambient => "ambient · " + (account.Source ?? "SDK credentials"),
            _ => account.Details.TryGetValue("plan", out var plan) ? Plan(plan)
                : account.Details.TryGetValue("host", out var host) ? "OAuth · " + host : "signed in",
        };
    }

    /// <summary>The Bootstrap icon of an account's kind.</summary>
    public static string KindIcon(AccountInfo account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return account.Kind switch
        {
            AccountKind.ApiKey => "bi-key",
            AccountKind.Environment => "bi-terminal",
            AccountKind.Ambient => "bi-cloud",
            _ => "bi-person-check",
        };
    }

    /// <summary>The chip tone of an account: <c>ok</c> when usable, <c>warn</c> when expired, <c>outline</c> when unavailable; an
    /// environment or ambient account that works is <c>info</c>.</summary>
    public static string KindTone(AccountInfo account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return account.Status switch
        {
            AccountStatus.Expired => "warn",
            AccountStatus.Unavailable => "outline",
            _ => account.ReadOnly ? "info" : "ok",
        };
    }

    /// <summary>The word for an account's state: connected, expired or unavailable.</summary>
    public static string Status(AccountInfo account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return account.Status switch
        {
            AccountStatus.Expired => "expired",
            AccountStatus.Unavailable => "unavailable",
            _ => "connected",
        };
    }

    /// <summary>The sign-in modal of <paramref name="provider"/>: <c>#modal-chatgpt-signin</c> for openai-codex, <c>#modal-oauth-signin</c>
    /// (the device code modal) otherwise.</summary>
    public static string SignInModal(string provider) => provider == SignInFlows.CodexProvider ? "#modal-chatgpt-signin" : "#modal-oauth-signin";

    /// <summary>The API key modal variant's name: single, azure, bedrock, vertex, cloudflare (or none).</summary>
    public static string FormName(ApiKeyForm form) => form.ToString().ToLowerInvariant();

    /// <summary>The method's name as the API takes it: browser, deviceCode.</summary>
    public static string MethodName(SignInMethod method) => method == SignInMethod.Browser ? "browser" : "deviceCode";

    /// <summary>What connecting <paramref name="provider"/> takes, as the outline chips of an unconnected provider say it.</summary>
    public static IReadOnlyList<string> ConnectChips(ProviderEntry provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var chips = new List<string>();
        switch (provider.KeyForm)
        {
            case ApiKeyForm.Single: chips.Add("API key"); break;
            case ApiKeyForm.Azure: chips.Add("API key + endpoint"); break;
            case ApiKeyForm.Bedrock: chips.Add("AWS credentials"); break;
            case ApiKeyForm.Vertex: chips.Add("GCP credentials"); break;
            case ApiKeyForm.Cloudflare: chips.Add(provider.Id == "cloudflare-ai-gateway" ? "key + account + gateway" : "key + account"); break;
        }
        if (provider.SignInOnly) chips.Add("sign-in only");
        else if (provider.SignsIn) chips.Add(provider.SubscriptionName ?? "subscription");
        return chips;
    }

    /// <summary>A ChatGPT plan name as people say it: <c>plus</c> → <c>ChatGPT Plus</c>.</summary>
    public static string Plan(string plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var trimmed = plan.Trim();
        return trimmed.Length == 0 ? "ChatGPT" : "ChatGPT " + char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }

    /// <summary>A short id for a long account id: <c>3f9c2a71…b0aa</c>.</summary>
    public static string ShortId(string? id) => id is null ? "" : id.Length <= 16 ? id : id[..8] + "…" + id[^4..];

    /// <summary>"just now", "5 min ago", "2h ago", "3 days ago", or the date.</summary>
    public static string Ago(DateTimeOffset? at, DateTimeOffset? now = null)
    {
        if (at is not { } time) return "never";
        var span = (now ?? DateTimeOffset.UtcNow) - time;
        if (span < TimeSpan.FromMinutes(1)) return "just now";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} min ago";
        if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours}h ago";
        if (span < TimeSpan.FromDays(14)) return (int)span.TotalDays == 1 ? "yesterday" : $"{(int)span.TotalDays} days ago";
        return Date(time);
    }

    /// <summary>"in 24 min", "in 5h", "in 9 days", or "expired 2h ago".</summary>
    public static string Until(DateTimeOffset at, DateTimeOffset? now = null)
    {
        var span = at - (now ?? DateTimeOffset.UtcNow);
        if (span <= TimeSpan.Zero) return "expired " + Ago(at, now);
        if (span < TimeSpan.FromHours(1)) return $"in {Math.Max(1, (int)span.TotalMinutes)} min";
        if (span < TimeSpan.FromDays(1)) return $"in {(int)span.TotalHours}h";
        return (int)span.TotalDays == 1 ? "in 1 day" : $"in {(int)span.TotalDays} days";
    }

    /// <summary>A date and time in the server's time zone (the user's machine): <c>Oct 17, 2026 15:20</c>.</summary>
    public static string Date(DateTimeOffset at) => at.ToLocalTime().ToString("MMM d, yyyy HH:mm", CultureInfo.InvariantCulture);

    /// <summary>A model's supported thinking levels as a range: <c>off</c>, <c>low → max</c>, <c>off, low → xhigh</c>.</summary>
    public static string ThinkingRange(IReadOnlyList<string> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);
        var supported = ThinkingLevels.Ordered.Where(level => levels.Contains(level, StringComparer.Ordinal)).ToList();
        if (supported.Count == 0) return "off";
        if (supported.Count == 1) return supported[0];
        // Consecutive runs of the ordered levels, each as "a → b" (or one level alone).
        var runs = new List<string>();
        var start = 0;
        for (var index = 1; index <= supported.Count; index++)
        {
            var contiguous = index < supported.Count &&
                ThinkingLevels.Ordered.IndexOf(supported[index]) == ThinkingLevels.Ordered.IndexOf(supported[index - 1]) + 1;
            if (contiguous) continue;
            runs.Add(index - start == 1 ? supported[start] : supported[start] + " → " + supported[index - 1]);
            start = index;
        }
        return string.Join(", ", runs);
    }

    /// <summary>The levels <paramref name="levels"/> leaves out, for a title: <c>minimal unsupported</c>.</summary>
    public static string? MissingLevels(IReadOnlyList<string> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);
        var missing = ThinkingLevels.Ordered.Where(level => !levels.Contains(level, StringComparer.Ordinal)).ToList();
        return missing.Count == 0 || missing.Count == ThinkingLevels.Ordered.Length - 1 ? null : string.Join(" and ", missing) + " unsupported";
    }

    /// <summary>A model's list price per 1M tokens: <c>$2 / $10</c>; <c>subscription</c>, <c>free</c> or <c>—</c> when there is none.</summary>
    public static string Price(BillingKind billing, CatalogModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (billing == BillingKind.Free) return "free";
        if (model.Cost is not { } cost) return billing == BillingKind.Subscription ? "subscription" : "—";
        if (cost.Input == 0 && cost.Output == 0) return billing == BillingKind.Subscription ? "subscription" : "free";
        return CatalogText.Dollars(cost.Input) + " / " + CatalogText.Dollars(cost.Output);
    }

    /// <summary>Whether a model is free to use (free billing, or a zero price).</summary>
    public static bool IsFree(BillingKind billing, CatalogModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return billing == BillingKind.Free || model.Cost is { Input: 0, Output: 0 };
    }

    /// <summary>A token count with separators: <c>1,000,000</c>.</summary>
    public static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static readonly JsonSerializerOptions Indented = new(SettingsStore.JsonOptions) { WriteIndented = true };

    /// <summary><paramref name="value"/> as the settings store writes it (camelCase, enums as strings), for a data attribute.</summary>
    public static string Json(object value) => JsonSerializer.Serialize(value, SettingsStore.JsonOptions);

    /// <summary><paramref name="value"/> as indented JSON, for a <c>ps-term</c> preview.</summary>
    public static string PrettyJson(object value) => JsonSerializer.Serialize(value, Indented);

    /// <summary>"1 model", "9 models".</summary>
    public static string Plural(int count, string one, string? many = null) =>
        count.ToString(CultureInfo.InvariantCulture) + " " + (count == 1 ? one : many ?? one + "s");
}
