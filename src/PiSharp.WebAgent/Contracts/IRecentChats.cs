// Decision 0011 (run W2, WU4): the sidebar's chat list (Views/Shared/_RecentChats.cshtml). A running chat shows a live status dot: closing a
// tab never stops a run, so the list is where a run that kept going is found again. WU4 ships EmptyRecentChats; the Chat area's ChatService
// replaces it with the durable sessions.
namespace PiSharp.WebAgent.Contracts;

/// <summary>The most recent chats, for the sidebar.</summary>
public interface IRecentChats
{
    /// <summary>At most <paramref name="limit"/> chats, most recently updated first; archived chats are left out.</summary>
    Task<IReadOnlyList<RecentChat>> ListAsync(int limit, CancellationToken cancellationToken = default);
}

/// <summary>A chat in the sidebar.</summary>
/// <param name="Id">The chat id (its route is <c>/chat/{Id}</c>).</param>
/// <param name="Title">The title shown.</param>
/// <param name="Updated">When it last changed.</param>
/// <param name="Running">Whether a run is in progress (the sidebar shows a live dot).</param>
public sealed record RecentChat(string Id, string Title, DateTimeOffset Updated, bool Running);
