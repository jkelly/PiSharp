// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/errors.ts. Upstream's Error subclasses (checked by `name`) are
// exception types here; the messages are upstream's. errorMessage() is DurableErrors.MessageOf: a .NET throw is always an Exception, but a
// derived exception's Message getter can throw as JavaScript's String() can, and its fallback is the type name where upstream's is
// Object.prototype.toString().
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

/// <summary>A transaction read a table after its first table write. Read every required row before writing (upstream
/// <c>ReadAfterWrite</c>).</summary>
public sealed class ReadAfterWriteException(string method)
    : InvalidOperationException($"Tx.{method}() cannot read tables after the first table write")
{
    /// <summary>The <c>Tx</c> method that read.</summary>
    public string Method { get; } = method;
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

/// <summary>A submission reached a busy conversation and was not admitted (upstream <c>ConversationBusy</c>).</summary>
public sealed class ConversationBusyException(ConversationId conversationId)
    : InvalidOperationException($"Conversation {conversationId} is busy")
{
    /// <summary>The busy conversation.</summary>
    public ConversationId ConversationId { get; } = conversationId;
}
