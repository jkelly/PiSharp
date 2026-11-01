// Decision 0011 (run W3, S0): the Spend Guard's stable error codes, so the guard, the pages and the chat agree. A call the guard stops ends
// with an error whose message starts with the code and a sentence and ends with a machine-readable tail naming the op and the cap:
// "spend_blocked: <sentence> [spend op=<op>; cap=<id>]". The chat finds it in a stored errorMessage (Parse, also after words the engine put
// before it), shows the sentence (Display), and opens the cause's pane from the op's decision; the pages' JSON errors use the same codes
// ({error: {code, message}}). spend_blocked offers no "proceed once"; spend_ask and spend_unknown_price do. The tail never holds a secret:
// an op is an operation id, a cap id is the ledger's number.
using System.Globalization;
using System.Text.RegularExpressions;

namespace PiSharp.WebAgent.Spend;

/// <summary>The Spend Guard's error codes and the message format of a stopped call.</summary>
public static partial class SpendErrors
{
    /// <summary>A Block cap (or an unknown price set to Block) stops the request; it can't be sent as it is.</summary>
    public const string Blocked = "spend_blocked";

    /// <summary>An Ask cap needs the user's answer first: proceed once, or cap the output so it fits.</summary>
    public const string Ask = "spend_ask";

    /// <summary>The price is unknown and set to Ask: the user answers first, as for <see cref="Ask"/>.</summary>
    public const string UnknownPrice = "spend_unknown_price";

    /// <summary>400 (pages' JSON): a value is not allowed.</summary>
    public const string Invalid = "invalid";

    /// <summary>404 (pages' JSON): no such cap or reservation.</summary>
    public const string NotFound = "not_found";

    /// <summary>409 (pages' JSON): the command does not fit the row's state (resolving one that is not uncertain).</summary>
    public const string Conflict = "conflict";

    /// <summary>The codes of a call the guard stopped.</summary>
    public static IReadOnlyList<string> GuardCodes { get; } = [Blocked, Ask, UnknownPrice];

    /// <summary>The error message of a call stopped by <paramref name="decision"/>: <c>code: sentence [spend op=…; cap=…]</c>.</summary>
    /// <exception cref="ArgumentException">The decision allows the request.</exception>
    public static string Message(SpendDecision decision, string? op = null)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var code = decision.ErrorCode ?? throw new ArgumentException("An allowed request has no spend error.", nameof(decision));
        var cap = decision.Causes.FirstOrDefault(cause => cause.Cap is not null)?.Cap;
        var tail = new List<string>();
        if (!string.IsNullOrWhiteSpace(op)) tail.Add("op=" + Clean(op));
        if (cap is { Id: > 0 }) tail.Add("cap=" + cap.Id.ToString(CultureInfo.InvariantCulture));
        return $"{code}: {Describe(decision)}" + (tail.Count == 0 ? "" : " [spend " + string.Join("; ", tail) + "]");
    }

    /// <summary>The sentence that says why <paramref name="decision"/> stops a request, from its first cause.</summary>
    public static string Describe(SpendDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var estimate = decision.Estimate;
        var cause = decision.Causes.FirstOrDefault();
        if (cause is null) return "This request is allowed.";
        if (cause.Kind == SpendCauseKind.UnknownPrice)
            return $"{estimate.Provider}/{estimate.Model} has no price, so it can't be checked against your caps. Unknown prices are set to " +
                (cause.Action == CapAction.Block ? "Block." : "Ask: proceed once to send it.");
        var cap = cause.Cap!;
        var worst = estimate.WorstMicros is { } micros ? SpendMoney.Format(micros, null) : "an unknown amount";
        var limit = SpendMoney.Format(cap.LimitMicros);
        var window = cap.Period switch
        {
            CapPeriod.Request => "per-request cap",
            CapPeriod.Run => "run cap",
            CapPeriod.Day => "daily cap",
            _ => "monthly cap",
        };
        var scope = cap.Scope switch
        {
            CapScope.Everything => cap.Period == CapPeriod.Request ? "" : " for everything metered",
            CapScope.Account => " for one account",
            _ => " for " + cap.ScopeText(),
        };
        var used = cap.Period == CapPeriod.Request ? "" : $" ({SpendMoney.Format(cause.SpentMicros)} used, {SpendMoney.Format(cause.LeftMicros ?? 0)} left)";
        var action = cap.Action == CapAction.Block ? "It is set to Block, so the request can't be sent as it is." : "It is set to Ask: proceed once, or cap the output so it fits.";
        return $"This request could cost up to {worst}, over the {window} of {limit}{scope}{used}. {action}";
    }

    /// <summary>The spend error in <paramref name="errorMessage"/> (also after words before it), or null when there is none.</summary>
    public static SpendErrorInfo? Parse(string? errorMessage)
    {
        if (string.IsNullOrEmpty(errorMessage)) return null;
        var match = Pattern().Match(errorMessage);
        if (!match.Success) return null;
        long? cap = match.Groups["cap"].Success && long.TryParse(match.Groups["cap"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
        return new SpendErrorInfo(match.Groups["code"].Value, match.Groups["text"].Value.Trim(), match.Groups["op"].Success ? match.Groups["op"].Value : null, cap);
    }

    /// <summary>Whether <paramref name="errorMessage"/> holds a spend error.</summary>
    public static bool IsSpend(string? errorMessage) => Parse(errorMessage) is not null;

    /// <summary>The sentence of a spend error for display (no code, no tail); other text comes back trimmed.</summary>
    public static string Display(string? errorMessage) => Parse(errorMessage)?.Text ?? (errorMessage ?? "").Trim();

    private static string Clean(string value) => new([.. value.Trim().Where(character => character is not (';' or ']' or '[') && !char.IsControl(character))]);

    [GeneratedRegex(@"\b(?<code>spend_blocked|spend_ask|spend_unknown_price): (?<text>.*?)(?: \[spend(?: op=(?<op>[^;\]]+))?(?:;? ?cap=(?<cap>\d+))?\])?\s*$", RegexOptions.Singleline)]
    private static partial Regex Pattern();
}

/// <summary>A spend error found in a message.</summary>
/// <param name="Code"><see cref="SpendErrors.Blocked"/>, <see cref="SpendErrors.Ask"/> or <see cref="SpendErrors.UnknownPrice"/>.</param>
/// <param name="Text">The sentence, for display.</param>
/// <param name="Op">The op of the stopped call, or null.</param>
/// <param name="CapId">The id of the cap that stopped it, or null.</param>
public sealed record SpendErrorInfo(string Code, string Text, string? Op, long? CapId)
{
    /// <summary>Whether the user may answer it (proceed once, or cap the output): not for <see cref="SpendErrors.Blocked"/>.</summary>
    public bool CanProceed => Code != SpendErrors.Blocked;
}

/// <summary>A Spend Guard command that cannot be done, with the HTTP status and code the pages' JSON answers with.</summary>
/// <param name="status">400, 404 or 409.</param>
/// <param name="code"><see cref="SpendErrors.Invalid"/>, <see cref="SpendErrors.NotFound"/> or <see cref="SpendErrors.Conflict"/>.</param>
/// <param name="message">What to show (no secret).</param>
/// <param name="inner">The cause, or null.</param>
public sealed class SpendException(int status, string code, string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>The HTTP status.</summary>
    public int Status { get; } = status;

    /// <summary>The stable code.</summary>
    public string Code { get; } = code;

    /// <summary>404: no such cap or reservation.</summary>
    public static SpendException NotFound(string message) => new(404, SpendErrors.NotFound, message);

    /// <summary>409: the row's state does not allow it.</summary>
    public static SpendException Conflict(string message) => new(409, SpendErrors.Conflict, message);

    /// <summary>400: a value is not allowed.</summary>
    public static SpendException Invalid(string message) => new(400, SpendErrors.Invalid, message);
}
