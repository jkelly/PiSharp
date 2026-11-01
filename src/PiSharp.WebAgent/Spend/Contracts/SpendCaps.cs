// Decision 0011 (run W3, S0): a Spend Guard cap (mockup settings-spend, _SpendCaps and _SpendCapModal). A cap limits what metered requests
// may cost in one window (a request, a run, a UTC day or a UTC month) for one scope (everything metered, one provider, one account or one
// source), and either blocks a request that does not fit or asks first. Caps live in the ledger's tables (the Ledger unit), not in a
// settings section; their id is the ledger's. A request counts toward every enabled cap it falls under and has to fit all of them.
// Subscription, free and unknown-price requests never count toward a cap (Reservation.CountsTowardCaps).
//
// A run is everything with the same run key (metadata.pisharp.run): a chat's key is its chat id, so a Run cap on chats limits a whole
// conversation (the mockup's "chat cap", pane sc-chat); a Playground, Compare, Bench or eval run has its own key. The request metadata has
// no per-turn key, so there is no separate per-turn window.
using System.Text.Json.Serialization;

namespace PiSharp.WebAgent.Spend;

/// <summary>What a cap applies to. Sent as <c>everything</c>, <c>provider</c>, <c>account</c>, <c>source</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CapScope>))]
public enum CapScope
{
    /// <summary>Every metered request (<see cref="SpendCap.Key"/> is null).</summary>
    [JsonStringEnumMemberName("everything")] Everything,

    /// <summary>Metered requests of one provider (<see cref="SpendCap.Key"/> is the provider id, such as <c>anthropic</c>).</summary>
    [JsonStringEnumMemberName("provider")] Provider,

    /// <summary>Metered requests through one account (<see cref="SpendCap.Key"/> is the account id, <c>AccountInfo.Id</c>).</summary>
    [JsonStringEnumMemberName("account")] Account,

    /// <summary>Metered requests started from one source (<see cref="SpendCap.Key"/> is the source's wire name, such as <c>chat</c>).</summary>
    [JsonStringEnumMemberName("source")] Source,
}

/// <summary>The window a cap's limit applies to. Sent as <c>request</c>, <c>run</c>, <c>day</c>, <c>month</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CapPeriod>))]
public enum CapPeriod
{
    /// <summary>Each request on its own: its worst case must fit the limit.</summary>
    [JsonStringEnumMemberName("request")] Request,

    /// <summary>Everything with the same run key: a whole chat, or one Playground, Compare, Bench or eval run.</summary>
    [JsonStringEnumMemberName("run")] Run,

    /// <summary>The UTC day (<see cref="SpendWindows.DayStart"/>).</summary>
    [JsonStringEnumMemberName("day")] Day,

    /// <summary>The UTC month (<see cref="SpendWindows.MonthStart"/>).</summary>
    [JsonStringEnumMemberName("month")] Month,
}

/// <summary>What happens when a request does not fit a cap. Sent as <c>block</c>, <c>ask</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CapAction>))]
public enum CapAction
{
    /// <summary>The request is not sent (<see cref="SpendErrors.Blocked"/>); Block takes priority over Ask.</summary>
    [JsonStringEnumMemberName("block")] Block,

    /// <summary>The user is asked first (<see cref="SpendErrors.Ask"/>): proceed once, or cap the output so it fits. Cheaper models are only
    /// suggested; the guard never switches the model or the account.</summary>
    [JsonStringEnumMemberName("ask")] Ask,
}

/// <summary>A spend cap.</summary>
/// <param name="Id">The ledger's id; 0 for a cap not stored yet.</param>
/// <param name="Scope">What it applies to.</param>
/// <param name="Key">The provider id, account id or source wire name of <paramref name="Scope"/>; null for <see cref="CapScope.Everything"/>.</param>
/// <param name="Period">The window of <paramref name="LimitMicros"/>.</param>
/// <param name="LimitMicros">The limit in micro-USD, 1 to <see cref="SpendMoney.MaxMicros"/>.</param>
/// <param name="Action">Block or Ask at the limit.</param>
/// <param name="Enabled">Whether requests are checked against it.</param>
public sealed record SpendCap(long Id, CapScope Scope, string? Key, CapPeriod Period, long LimitMicros, CapAction Action, bool Enabled = true)
{
    /// <summary>The longest key accepted.</summary>
    public const int MaxKeyLength = 200;

    /// <summary>The same cap with its key trimmed (a source key as its wire name, no key for <see cref="CapScope.Everything"/>).</summary>
    /// <exception cref="ArgumentException">A value is not allowed; the message says which, for the page to show.</exception>
    public SpendCap Normalize()
    {
        if (Id < 0) throw new ArgumentException("A cap's id is never negative.");
        if (!Enum.IsDefined(Scope)) throw new ArgumentException($"Unknown cap scope {(int)Scope}.");
        if (!Enum.IsDefined(Period)) throw new ArgumentException($"Unknown cap window {(int)Period}.");
        if (!Enum.IsDefined(Action)) throw new ArgumentException($"Unknown cap action {(int)Action}.");
        if (LimitMicros <= 0) throw new ArgumentException("A cap's limit must be more than $0.00.");
        if (LimitMicros > SpendMoney.MaxMicros) throw new ArgumentException($"A cap's limit can be at most {SpendMoney.Format(SpendMoney.MaxMicros)}.");
        var key = Key?.Trim();
        key = Scope switch
        {
            CapScope.Everything => null,
            CapScope.Source => SpendSources.Parse(key).Wire(),
            _ => string.IsNullOrEmpty(key) ? throw new ArgumentException(Scope == CapScope.Provider ? "Choose a provider for this cap." : "Choose an account for this cap.")
                : key.Length > MaxKeyLength ? throw new ArgumentException($"A cap's key is at most {MaxKeyLength} characters.") : key,
        };
        return this with { Key = key };
    }

    /// <summary>Whether a request of <paramref name="provider"/> through <paramref name="account"/> from <paramref name="source"/> falls under
    /// this cap's scope (whether it is enabled or metered is not checked here).</summary>
    public bool AppliesTo(string provider, string? account, SpendSource source) => Scope switch
    {
        CapScope.Everything => true,
        CapScope.Provider => string.Equals(Key, provider, StringComparison.Ordinal),
        CapScope.Account => account is not null && string.Equals(Key, account, StringComparison.Ordinal),
        CapScope.Source => string.Equals(Key, source.Wire(), StringComparison.Ordinal),
        _ => false,
    };

    /// <summary>Whether <paramref name="request"/> falls under this cap's scope.</summary>
    public bool AppliesTo(SpendRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return AppliesTo(request.Provider, request.Account, request.Source);
    }

    /// <summary>Whether <paramref name="other"/> limits the same thing (scope, key and window), which an edit never changes.</summary>
    public bool SameTarget(SpendCap other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Scope == other.Scope && Period == other.Period && string.Equals(Key, other.Key, StringComparison.Ordinal);
    }

    /// <summary>The window as the pages name it: <c>Per request</c>, <c>Per run</c>, <c>Daily</c>, <c>Monthly</c>.</summary>
    [JsonIgnore]
    public string PeriodLabel => Period switch
    {
        CapPeriod.Request => "Per request",
        CapPeriod.Run => "Per run",
        CapPeriod.Day => "Daily",
        _ => "Monthly",
    };

    /// <summary>The scope as the pages name it: <c>All metered</c>, the provider id, <paramref name="accountLabel"/> (or "One account"), or
    /// the source's label.</summary>
    public string ScopeText(string? accountLabel = null) => Scope switch
    {
        CapScope.Everything => "All metered",
        CapScope.Provider => Key ?? "",
        CapScope.Account => string.IsNullOrWhiteSpace(accountLabel) ? "One account" : accountLabel,
        _ => SpendSources.TryParse(Key, out var source) ? source.Label() : Key ?? "",
    };
}

/// <summary>A cap with what already counts toward it in its current window.</summary>
/// <param name="Cap">The cap.</param>
/// <param name="SpentMicros">What counts toward it now, in micro-USD (uncertain spend included); null for a <see cref="CapPeriod.Request"/>
/// cap, and for a <see cref="CapPeriod.Run"/> cap when no run was named.</param>
/// <param name="WindowStart">The start of its UTC day or month; null for request and run caps.</param>
/// <param name="WindowEnd">The end (exclusive) of its UTC day or month; null for request and run caps.</param>
/// <param name="WarnPercent">The warning threshold (<see cref="SpendSettings.WarnPercent"/>).</param>
public sealed record SpendCapStatus(SpendCap Cap, long? SpentMicros, DateTimeOffset? WindowStart, DateTimeOffset? WindowEnd, int WarnPercent)
{
    /// <summary>The share of the limit used, in percent (exact); null when <see cref="SpentMicros"/> is.</summary>
    public double? Percent => SpentMicros is { } spent ? spent * 100.0 / Cap.LimitMicros : null;

    /// <summary><see cref="Percent"/> rounded to a whole percent, halves up, as the pages show it (<c>37%</c>).</summary>
    public int? PercentShown => Percent is { } percent ? (int)Math.Round(percent, MidpointRounding.AwayFromZero) : null;

    /// <summary>What is left before the limit, never below 0; null when <see cref="SpentMicros"/> is.</summary>
    public long? LeftMicros => SpentMicros is { } spent ? Math.Max(0, Cap.LimitMicros - spent) : null;

    /// <summary>Whether the cap is at or above the warning threshold (the amber chip), compared exactly.</summary>
    public bool Warning => SpentMicros is { } spent && (decimal)spent * 100 >= (decimal)WarnPercent * Cap.LimitMicros;

    /// <summary>Whether nothing is left.</summary>
    public bool AtLimit => SpentMicros is { } spent && spent >= Cap.LimitMicros;
}
