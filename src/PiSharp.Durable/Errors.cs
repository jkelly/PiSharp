// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/src/errors.ts. Upstream's Error subclasses (checked by `name`) are
// exception types here; the messages are upstream's. errorMessage() is DurableErrors.MessageOf: a .NET throw is always an Exception, but a
// derived exception's Message getter can throw as JavaScript's String() can, and its fallback is the type name where upstream's is
// Object.prototype.toString(). Pi v1.5.0 sync (area harness-options): ReadOnlySession (upstream be14d9732). Area admission:
// ConversationIdle (6a2825113, #11149), and ReadAfterWrite names the operation (cc9c52310), so submit(tx, draft) after a table write
// reports itself.
namespace PiSharp.Durable;

/// <summary>Error formatting that never throws (upstream <c>errorMessage</c>).</summary>
internal static class DurableErrors
{
    /// <summary>The message of <paramref name="error"/>, without throwing itself: code that formats an error must not fail for the error it
    /// formats.</summary>
    public static string MessageOf(Exception error)
    {
        try
        {
            return error.Message;
        }
        catch (Exception)
        {
            return $"[object {error.GetType().Name}]";
        }
    }
}

/// <summary>A transaction read a table after its first table write, or admitted a submission (<c>SubmitAsync(tx, draft)</c>) after it. Read
/// every required row, and admit, before writing (upstream <c>ReadAfterWrite</c>).</summary>
/// <param name="operation">The call, such as <c>Tx.task()</c> or <c>submit(tx, draft)</c>.</param>
public sealed class ReadAfterWriteException(string operation)
    : InvalidOperationException($"{operation} cannot read tables after the first table write")
{
    /// <summary>The call that read, such as <c>Tx.task()</c>.</summary>
    public string Operation { get; } = operation;
}

/// <summary>
/// A Storage read rejected an invalid request, such as an unknown conversation or a cursor from another scan, with no durable effect
/// (upstream <c>StorageRequestError</c>). Unlike any other exception a Storage throws, it fails only that read, not the Session; from
/// <see cref="IStorage.CommitAsync"/> it is fatal like any other.
/// </summary>
public sealed class StorageRequestException(string message) : InvalidOperationException(message);

/// <summary>
/// The Session failed, so nothing runs or commits any more (upstream <c>SessionFailed</c>). It fails when a Storage method throws, a commit
/// cannot be adopted, or the Harness's own bookkeeping throws (a scheduler commit, an internal commit listener, a registry whose
/// <c>Snapshot()</c> throws). <see cref="Exception.InnerException"/> (upstream's <c>cause</c>) is that first error. Reopen the Session;
/// reopening recovers from what was committed.
/// </summary>
public sealed class SessionFailedException(Exception cause)
    : InvalidOperationException("Session failed after a storage error; close and reopen it", cause)
{
    /// <summary>The error that failed the Session (upstream <c>cause</c>).</summary>
    public Exception Cause { get; } = cause;
}

/// <summary>
/// The Harness was opened read-only (<see cref="HarnessOptions.ReadOnly"/>): a commit with writes, or a call that would run tasks, was
/// refused (upstream <c>ReadOnlySession</c>). Nothing was written, and the Harness stays usable for reads.
/// </summary>
public sealed class ReadOnlySessionException(string what) : InvalidOperationException($"Read-only Harness: {what} refused");

/// <summary>A submission reached a busy conversation and was not admitted (upstream <c>ConversationBusy</c>).</summary>
public sealed class ConversationBusyException(ConversationId conversationId)
    : InvalidOperationException($"Conversation {conversationId} is busy")
{
    /// <summary>The busy conversation.</summary>
    public ConversationId ConversationId { get; } = conversationId;
}

/// <summary>An input with <see cref="WhenIdle.Reject"/> reached an idle conversation and was not admitted (upstream
/// <c>ConversationIdle</c>).</summary>
public sealed class ConversationIdleException(ConversationId conversationId)
    : InvalidOperationException($"Conversation {conversationId} is idle")
{
    /// <summary>The idle conversation.</summary>
    public ConversationId ConversationId { get; } = conversationId;
}
