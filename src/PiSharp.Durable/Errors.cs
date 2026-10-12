// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/errors.ts. Upstream's Error subclasses (checked by `name`) are
// exception types here; the messages are upstream's.
namespace PiSharp.Durable;

/// <summary>A transaction read a table after its first table write. Read every required row before writing (upstream
/// <c>ReadAfterWrite</c>).</summary>
public sealed class ReadAfterWriteException(string method)
    : InvalidOperationException($"Tx.{method}() cannot read tables after the first table write")
{
    /// <summary>The <c>Tx</c> method that read.</summary>
    public string Method { get; } = method;
}

/// <summary>Storage rejected a batch before any durable effect; the owning Session may continue safely (upstream
/// <c>StorageRejected</c>).</summary>
public sealed class StorageRejectedException : InvalidOperationException
{
    /// <summary>A rejection.</summary>
    public StorageRejectedException(string message) : base(message) { }
    /// <summary>A rejection caused by <paramref name="innerException"/>.</summary>
    public StorageRejectedException(string message, Exception? innerException) : base(message, innerException) { }
}

/// <summary>A submission reached a busy conversation and was not admitted (upstream <c>ConversationBusy</c>).</summary>
public sealed class ConversationBusyException(ConversationId conversationId)
    : InvalidOperationException($"Conversation {conversationId} is busy")
{
    /// <summary>The busy conversation.</summary>
    public ConversationId ConversationId { get; } = conversationId;
}
