// Decision 0011 (run W4, fix 2): read-only views of durable sessions that have no chat binding. An area that runs sessions on the app's
// device without a chat (agent compare's models, until one is opened as a chat) registers an ISessionViewSource that says what such a
// session is: its title, provider, model, account id and thinking level (never a credential). ChatStreamMux's read-only keys
// (session/{session}/{agent}) and AgentViewHub's read-only feeds ask SessionViews, which answers from the chat binding when the session has
// one and from the sources otherwise; a session neither knows cannot be watched. A read-only view renders the same transcript items as the
// chat page with no actions (no fork, edit, retry, approval buttons) and no interactive panels.
namespace PiSharp.WebAgent.Chat;

/// <summary>What a read-only view of a durable session without a chat binding shows (never a credential).</summary>
/// <param name="SessionId">The durable session.</param>
/// <param name="Title">Its title.</param>
/// <param name="Provider">The provider its requests use.</param>
/// <param name="Model">The model id.</param>
/// <param name="AccountId">The account its requests use, or null.</param>
/// <param name="Thinking">The thinking level.</param>
/// <param name="AgentId">The agent to show by default.</param>
public sealed record SessionView(string SessionId, string Title, string Provider, string Model, string? AccountId, string Thinking, long AgentId = 1)
{
    /// <summary>The view as a chat binding (not stored; the render path of a chat reads one).</summary>
    public ChatBinding ToBinding(DateTimeOffset now) => new(SessionId, Title, Provider, Model, AccountId, Thinking, null, AgentId, now, now, null, null);
}

/// <summary>Says what a durable session without a chat binding is, for its read-only view. Registered by the area that runs it.</summary>
public interface ISessionViewSource
{
    /// <summary>The view of <paramref name="sessionId"/>, or null when this source does not run it.</summary>
    SessionView? Find(string sessionId);
}

/// <summary>The read-only views: the chat binding of a session, else what a source says. A singleton.</summary>
public sealed class SessionViews(ChatBindings bindings, IEnumerable<ISessionViewSource> sources)
{
    private readonly ISessionViewSource[] sources = [.. sources];

    /// <summary>The binding a read-only view of <paramref name="sessionId"/> renders with: its chat's, or one made from a source's view; null
    /// when neither knows the session.</summary>
    public ChatBinding? Find(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;
        if (bindings.Get(sessionId) is { } binding) return binding;
        foreach (var source in sources)
            if (source.Find(sessionId) is { } view) return view.ToBinding(DateTimeOffset.UtcNow);
        return null;
    }

    /// <summary>Whether a read-only view of <paramref name="sessionId"/> can be watched.</summary>
    public bool Knows(string sessionId) => Find(sessionId) is not null;
}
