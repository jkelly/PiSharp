// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/data/errors.ts
//
// ChordError: Chord's one error type, its codes and reasons (link protocol) and its wire form; and PluginError, what a plugin throws to give
// its callers a code of its own. Upstream's ChordError is a tagged union whose wrong pairings fail to type; C# has no literal types, so a
// code and a reason are strings and a ChordError refuses an unknown code and a wrong pairing when it is made (ArgumentException), as
// declarations are checked when they run. Upstream's cause may be any value: Cause keeps it, and InnerException is it when it is an
// Exception. A JsonNode has one parent, so ToJson copies the details; Details is kept by reference. Absent details and JSON null details
// stay apart, as undefined and null do upstream.
using System.Text.Json.Nodes;

namespace PiSharp.Chord.Data;

/// <summary>A Chord failure (upstream <c>ChordError</c>): a code, a reason the code allows (required for <c>invalid</c>), a message and
/// JSON details. Only these cross a link (<see cref="ToJson"/>): never a stack or a cause.</summary>
public sealed class ChordError : Exception
{
    /// <summary>A ChordError: <c>new ChordError("invalid", "args/text: missing", reason: "args")</c>. Throws an
    /// <see cref="ArgumentException"/> for an unknown code or a reason the code does not allow (upstream's type errors).</summary>
    public ChordError(string code, string message, string? reason = null, JsonNode? details = null, object? cause = null)
        : this(code, message, reason, details, details is not null, cause)
    {
    }

    internal ChordError(string code, string message, string? reason, JsonNode? details, bool hasDetails, object? cause)
        : base(message, cause as Exception)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(message);
        if (!DataInternal.ChordErrorReasons.TryGetValue(code, out var reasons)) throw new ArgumentException($"ChordError: unknown code {ChordJson.Quote(code)}");
        if (reason is null ? code == "invalid" : !reasons.Contains(reason, StringComparer.Ordinal))
            throw new ArgumentException($"ChordError: reason {ChordJson.Quote(reason)} not allowed for {code}");
        Code = code;
        Reason = reason;
        Details = details;
        HasDetails = hasDetails;
        Cause = cause;
    }

    /// <summary>The code (link protocol): one of <c>DataInternal.ChordErrorReasons</c>' keys.</summary>
    public string Code { get; }

    /// <summary>The reason, one the code allows; null when the code carries none.</summary>
    public string? Reason { get; }

    /// <summary>The JSON details, as given; null when there are none (or when they are JSON null: see <see cref="HasDetails"/>).</summary>
    public JsonNode? Details { get; }

    /// <summary>Whether the error has details (upstream <c>details !== undefined</c>), JSON null included.</summary>
    public bool HasDetails { get; }

    /// <summary>The cause, any value (upstream's <c>cause</c>); never crosses a link.</summary>
    public object? Cause { get; }

    /// <summary>The wire form <c>{ code, reason?, message, details? }</c> (upstream <c>toJSON</c>); stack and cause never cross a link.</summary>
    public JsonObject ToJson()
    {
        var json = new JsonObject { ["code"] = Code };
        if (Reason is not null) json["reason"] = Reason;
        json["message"] = Message;
        if (HasDetails) json["details"] = Details?.DeepClone();
        return json;
    }

    /// <summary>Whether <paramref name="error"/> is a ChordError, optionally with the code <paramref name="code"/> (upstream
    /// <c>isChordError</c>).</summary>
    public static bool Is(object? error, string? code = null) => error is ChordError chord && (code is null || chord.Code == code);
}

/// <summary>An error a plugin throws for its callers (upstream <c>PluginError</c>): its own code, a message and JSON details. Only these
/// cross a link (never a stack or a cause); a consumer rethrows it as a PluginError.</summary>
public class PluginError : Exception
{
    /// <summary>A plugin error with <paramref name="code"/>, <paramref name="message"/> and, optionally, JSON <paramref name="details"/>.</summary>
    public PluginError(string code, string message, JsonNode? details = null)
        : base(message)
    {
        ArgumentNullException.ThrowIfNull(code);
        Code = code;
        Details = details;
    }

    /// <summary>The plugin's own code.</summary>
    public string Code { get; }

    /// <summary>The JSON details; null when there are none.</summary>
    public JsonNode? Details { get; }
}

internal static partial class DataInternal
{
    /// <summary>Version of the error table below on links; adding or moving a code or reason bumps it (upstream <c>PROTOCOL_VERSION</c>).</summary>
    internal const int ProtocolVersion = 1;

    /// <summary>Every code with the reasons it may carry, in upstream's order (<c>CHORD_ERROR_REASONS</c>). <c>invalid</c> requires a
    /// reason; every other code may omit it.</summary>
    internal static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> ChordErrorReasons = new OrderedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        ["invalid"] = ["args", "params", "member", "host_address", "reentrant", "read_after_bulk", "cycle"],
        ["contract_mismatch"] = ["updating"],
        ["forbidden"] = ["revoked"],
        ["gone"] = [],
        ["not_ready"] = [],
        // No reason: released by its holder. `ended`: its object ended; `closed`: its host, store or native closed; `settled`: used after
        // the call or run it belonged to settled; `version`: an old handle after a migration; `discarded`: a transaction cut at the seal.
        ["released"] = ["ended", "closed", "settled", "version", "discarded"],
        ["too_large"] = ["part"],
        // `undeclared`: a registry no plugin serves on this host.
        ["unavailable"] = ["locked", "park_timeout", "busy", "storage_failed", "undeclared"],
        ["aborted"] = [],
    };

    /// <summary>An <c>invalid</c> error with the given reason (upstream <c>invalid</c>).</summary>
    internal static ChordError Invalid(string reason, string message, JsonNode? details = null) => new("invalid", message, reason, details);

    /// <summary><paramref name="error"/> itself when it is a ChordError, otherwise an <c>unavailable</c> error naming
    /// <paramref name="label"/>, with <paramref name="error"/> as cause (upstream <c>asChordError</c>).</summary>
    internal static ChordError AsChordError(object? error, string label)
    {
        if (error is ChordError chord) return chord;
        return new ChordError("unavailable", $"{label}: {MessageOf(error)}", cause: error);
    }

    /// <summary>Rebuilds a ChordError from a link (upstream <c>chordErrorFromJson</c>). Checks the envelope only: a known code, a reason
    /// allowed for it (required for <c>invalid</c>), a string message, no other keys. <c>details</c> is kept by reference.</summary>
    internal static ChordError ChordErrorFromJson(JsonNode? raw)
    {
        // Trust boundary: `raw` is fresh JSON from the link decoder.
        if ((raw as JsonObject ?? ChordJson.AsObject(raw)) is not { } json) throw EnvelopeError("not an object");
        foreach (var (key, _) in json) if (!EnvelopeKeys.Contains(key)) throw EnvelopeError($"unknown key \"{key}\"");
        var code = ChordJson.AsString(json["code"]);
        if (code is null || !ChordErrorReasons.TryGetValue(code, out var reasons)) throw EnvelopeError("unknown code");
        var hasReason = json.TryGetPropertyValue("reason", out var reasonNode);
        var reason = ChordJson.AsString(reasonNode);
        if (!hasReason ? code == "invalid" : reason is null || !reasons.Contains(reason, StringComparer.Ordinal))
            throw EnvelopeError($"reason not allowed for {code}");
        if (ChordJson.AsString(json["message"]) is not { } message) throw EnvelopeError("message is not a string");
        var hasDetails = json.TryGetPropertyValue("details", out var details);
        // The checks above establish the pairing.
        return new ChordError(code, message, reason, details, hasDetails, cause: null);
    }

    private static readonly HashSet<string> EnvelopeKeys = new(["code", "reason", "message", "details"], StringComparer.Ordinal);

    private static ArgumentException EnvelopeError(string problem) => new($"Invalid ChordError JSON: {problem}");

    /// <summary>A thrown value's message: an exception's, otherwise JavaScript's <c>String(value)</c> of it.</summary>
    internal static string MessageOf(object? error) => error switch
    {
        Exception exception => exception.Message,
        null => "null",
        JsonNode node => ChordJson.ToJavaScriptString(node),
        _ => error.ToString() ?? "",
    };
}
