namespace PiSharp.Chord.Legacy;

/// <summary>A protocol-4/runtime error from the legacy Chord API.</summary>
public sealed class ChordException : Exception
{
    public ChordException(string code, string message, string? reason = null, object? details = null, Exception? cause = null)
        : base($"{code}{(reason is null ? "" : "/" + reason)}: {message}", cause)
    { Code = code; Reason = reason; Details = details; }
    public string Code { get; }
    public string? Reason { get; }
    public object? Details { get; }
    public bool Retryable => Code is "superseded" or "upgrade_conflict";
    public static ChordException Invalid(string reason, string message, object? details = null) => new("invalid", message, reason, details);
    public static ChordException Forbidden(string message = "forbidden") => new("forbidden", message);
    internal static ChordException Wrap(Exception error, string context) => error as ChordException ?? new("unavailable", context + ": " + error.Message, cause: error);
}
