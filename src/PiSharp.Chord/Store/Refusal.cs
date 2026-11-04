// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/store/refusal.ts
//
// A refused commit as Chord's owners take it (slice 6b D4, §1.4): the store rejects a step it rolled back with the step's own error,
// unchanged (Durable's Storage semantics); Chord classifies it: an `int` column's CHECK (a plugin's value out of range) fails only its
// transaction, any other refusal means memory and disk disagree. Upstream reads SQLite's extended result code from node:sqlite's `errcode`;
// PiSharp.Chord references no SQLite driver, so it reads an exception's SqliteExtendedErrorCode property (Microsoft.Data.Sqlite's
// SqliteException has one) where the exception's type has it, and the message otherwise, as upstream does for a facade without codes.
using System.Collections.Concurrent;
using System.Reflection;
using PiSharp.Chord.Data;

namespace PiSharp.Chord.Store;

/// <summary>How a Chord commit ended that the store rejected (upstream <c>Refusal</c>): <see cref="Check"/> fails the transaction only,
/// <see cref="Broken"/> its object too.</summary>
internal abstract record Refusal(ChordError Error)
{
    /// <summary>A value out of its column's range: <c>invalid/args</c>.</summary>
    internal sealed record Check(ChordError Error) : Refusal(Error);

    /// <summary>Any other refusal: <c>unavailable</c>.</summary>
    internal sealed record Broken(ChordError Error) : Refusal(Error);
}

internal static partial class StoreInternal
{
    /// <summary>SQLite's extended result code <c>SQLITE_CONSTRAINT_CHECK</c> (upstream <c>SQLITE_CONSTRAINT_CHECK</c>).</summary>
    internal const int SqliteConstraintCheck = 275;

    private static readonly ConcurrentDictionary<Type, PropertyInfo?> ExtendedCodes = new();

    /// <summary>The refusal behind <paramref name="error"/>, a store commit's rejection; null when it is the store's own (<c>released</c>
    /// at admission or after close, <c>unavailable/storage_failed</c> once stopped), which the owner rethrows as it is (upstream
    /// <c>refusalOf</c>).</summary>
    internal static Refusal? RefusalOf(Exception error, string label)
    {
        if (error is ChordError { Code: "released" } or ChordError { Code: "unavailable", Reason: "storage_failed" }) return null;
        var message = error.Message;
        if (IsCheck(error, message))
        {
            var refused = $"{label}: a value is out of its column's range ({message})";
            return new Refusal.Check(new ChordError("invalid", refused, "args", cause: error));
        }
        return new Refusal.Broken(new ChordError("unavailable", $"{label}: the store refused the commit ({message})", cause: error));
    }

    /// <summary>Whether a refusal is a <c>CHECK</c> failure: SQLite's extended code where the exception has one, the message otherwise.</summary>
    private static bool IsCheck(Exception error, string message)
    {
        var property = ExtendedCodes.GetOrAdd(error.GetType(), static type =>
            type.GetProperty("SqliteExtendedErrorCode", BindingFlags.Public | BindingFlags.Instance) is { PropertyType: var kind } found && kind == typeof(int)
                ? found
                : null);
        if (property?.GetValue(error) is int code) return code == SqliteConstraintCheck;
        return message.Contains("CHECK constraint failed", StringComparison.Ordinal);
    }
}
