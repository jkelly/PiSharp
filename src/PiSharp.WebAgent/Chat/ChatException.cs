// Decision 0011 (run W2, WU7): the Chat area's errors, each with the HTTP status and code the commands answer ({error: {code, message}}).
// The durable host's own exceptions map onto the same codes (ChatErrors.From), so a page always gets a clear reason and never a stack trace.
using PiSharp.Durable.Hosting;

namespace PiSharp.WebAgent.Chat;

/// <summary>A chat command that cannot be done, with the HTTP status that says why.</summary>
/// <param name="status">400 invalid, 404 not found, 409 busy or already decided.</param>
/// <param name="code">A stable code: <c>invalid</c>, <c>not_found</c>, <c>busy</c>, <c>conflict</c>, <c>already_decided</c>.</param>
/// <param name="message">The reason, for the page.</param>
public sealed class ChatException(int status, string code, string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>The HTTP status.</summary>
    public int Status { get; } = status;

    /// <summary>The stable code.</summary>
    public string Code { get; } = code;

    /// <summary>A 400.</summary>
    public static ChatException Invalid(string message) => new(400, ChatErrors.Invalid, message);

    /// <summary>A 404.</summary>
    public static ChatException NotFound(string message) => new(404, ChatErrors.NotFound, message);

    /// <summary>A 409 for a command that needs the chat idle.</summary>
    public static ChatException Busy(string message) => new(409, ChatErrors.Busy, message);

    /// <summary>A 409 for a stale command (an answer to a question that no longer waits).</summary>
    public static ChatException Conflict(string message) => new(409, ChatErrors.Conflict, message);
}

/// <summary>The error codes, and the mapping of the durable host's exceptions.</summary>
public static class ChatErrors
{
    /// <summary>400: a value is not allowed.</summary>
    public const string Invalid = "invalid";

    /// <summary>404: no such chat, agent or entry.</summary>
    public const string NotFound = "not_found";

    /// <summary>409: the chat is running and the command needs it idle.</summary>
    public const string Busy = "busy";

    /// <summary>409: the command is stale.</summary>
    public const string Conflict = "conflict";

    /// <summary>409: an approval was already decided (the first decision wins).</summary>
    public const string AlreadyDecided = "already_decided";

    /// <summary>The <see cref="ChatException"/> for <paramref name="error"/>, or null when it is not a chat or host error.</summary>
    public static ChatException? From(Exception error) => error switch
    {
        ChatException chat => chat,
        HostNotFoundException notFound => new ChatException(404, NotFound, notFound.Message, notFound),
        HostAmbiguousException ambiguous => new ChatException(400, Invalid, ambiguous.Message, ambiguous),
        HostInvalidException invalid => new ChatException(400, Invalid, invalid.Message, invalid),
        HostValidationException validation => new ChatException(400, Invalid, validation.Message, validation),
        HostTooLargeException tooLarge => new ChatException(400, Invalid, tooLarge.Message, tooLarge),
        HostBusyException busy => new ChatException(409, Busy, busy.Message, busy),
        AppStateConflictException conflict => new ChatException(409, Conflict, conflict.Message, conflict),
        HostConflictException conflict => new ChatException(409, Conflict, conflict.Message, conflict),
        HostLimitException limited => new ChatException(409, Conflict, limited.Message, limited),
        SessionUnavailableException unavailable => new ChatException(409, Conflict, unavailable.Message, unavailable),
        DurableHostException other => new ChatException(400, Invalid, other.Message, other),
        _ => null,
    };
}
