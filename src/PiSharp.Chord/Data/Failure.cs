// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/data/failure.ts
//
// Failure causes: transient (the object's own effect failed: retry later), broken (its own code, a cycle or authority: retry by hand) or
// waiting (not its fault: no provider, no fitting version, a dependency that failed; woken by an event). The failing stage decides, never
// the error code. Upstream's FailureCause is a union of three { kind, error } shapes; here it is one record with a FailureKind. Message
// lengths count UTF-16 code units, as JavaScript's do.
namespace PiSharp.Chord.Data;

/// <summary>The kind of a failure cause.</summary>
public enum FailureKind
{
    /// <summary>The object's own effect failed: retry later.</summary>
    Transient,

    /// <summary>Its own code, a cycle or authority: retry by hand.</summary>
    Broken,

    /// <summary>Not its fault (no provider, no fitting version, a dependency that failed): woken by an event.</summary>
    Waiting,
}

/// <summary>Why an object, or a request, is failed (upstream <c>FailureCause</c>); the kind comes from the failing stage, never from the
/// error code.</summary>
public sealed record FailureCause(FailureKind Kind, ChordError Error);

/// <summary>How a plugin's open reports a broken configuration (upstream <c>broken</c>).</summary>
public static class Failure
{
    /// <summary>Marks an open's failure as broken (bad configuration): <c>throw Failure.Broken(error)</c>. Its message is the cause's.</summary>
    public static Exception Broken(object? cause) => new BrokenOpen(cause);
}

/// <summary>The value <see cref="Failure.Broken"/> throws: its message is the cause's, its cause the value given.</summary>
internal sealed class BrokenOpen(object? cause) : Exception(DataInternal.MessageOf(cause), cause as Exception)
{
    internal object? Cause { get; } = cause;
}

internal static partial class DataInternal
{
    /// <summary>The cause of a value thrown by an open: broken when it came from <see cref="Failure.Broken"/>, transient otherwise (upstream
    /// <c>openFailure</c>).</summary>
    internal static FailureCause OpenFailure(object? thrown, string label)
    {
        if (thrown is BrokenOpen broken) return new FailureCause(FailureKind.Broken, AsChordError(broken.Cause, label));
        return new FailureCause(FailureKind.Transient, AsChordError(thrown, label));
    }

    /// <summary>The longest message a <c>waiting</c> cause of a dependent carries (telemetry's rule for error messages).</summary>
    private const int MessageLimit = 500;

    /// <summary>How a dependent's message names its blocker: the nearest one first.</summary>
    private const string WaitingPrefix = "waiting for ";

    /// <summary>The separator after a blocker's label.</summary>
    private const string WaitingSeparator = ": ";

    /// <summary>The marker of a clipped label or root.</summary>
    private const string Clipped = "…";

    /// <summary>The most of the 500 characters the root cause takes when both are long: the nearest blocker keeps the rest.</summary>
    private const int RootLimit = 320;

    /// <summary>The <c>waiting</c> cause of a dependent parked on <paramref name="blocker"/>, which failed with <paramref name="error"/>
    /// (upstream <c>waitingFor</c>): <c>unavailable</c>, its message always "waiting for &lt;blocker&gt;: &lt;root cause&gt;" (the root found
    /// through any "waiting for …: " of the error's message), so a ring of waiting attempts passing causes round reaches the same message
    /// after one round; within 500 characters, the label clipped first, the root only past its share. The error is its cause.</summary>
    internal static FailureCause WaitingFor(string blocker, ChordError error)
    {
        var root = RootMessage(error.Message);
        var label = blocker;
        var room = MessageLimit - WaitingPrefix.Length - WaitingSeparator.Length;
        if (label.Length + root.Length > room)
        {
            var rootRoom = Math.Max(RootLimit, room - label.Length);
            if (root.Length > rootRoom) root = Clip(root, rootRoom);
            if (label.Length + root.Length > room) label = Clip(label, room - root.Length);
        }
        var message = $"{WaitingPrefix}{label}{WaitingSeparator}{root}";
        return new FailureCause(FailureKind.Waiting, new ChordError("unavailable", message, cause: error));
    }

    /// <summary><paramref name="text"/> within <paramref name="length"/> characters, clipped with "…" when longer.</summary>
    private static string Clip(string text, int length) => text.Length <= length ? text : $"{text[..(length - Clipped.Length)]}{Clipped}";

    /// <summary>The root cause in a "waiting for X: root" message: past every leading "waiting for …: ".</summary>
    private static string RootMessage(string message)
    {
        var rest = message;
        while (rest.StartsWith(WaitingPrefix, StringComparison.Ordinal))
        {
            var end = rest.IndexOf(WaitingSeparator, WaitingPrefix.Length, StringComparison.Ordinal);
            if (end < 0) break;
            rest = rest[(end + WaitingSeparator.Length)..];
        }
        return rest;
    }

    /// <summary>Whether two failure causes say the same (a wake compares causes by value): kind, code, reason and message (upstream
    /// <c>sameCause</c>).</summary>
    internal static bool SameCause(FailureCause a, FailureCause b) =>
        a.Kind == b.Kind && a.Error.Code == b.Error.Code && a.Error.Reason == b.Error.Reason && a.Error.Message == b.Error.Message;
}
