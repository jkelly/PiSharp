// Decision 0011 (run W2, WU7): the seam between a chat and the provider accounts (Credentials, WU5). A chat binds the account it is created
// with and keeps it: ChatService asks BindAsync once, at creation (and again only when the user picks another provider for the chat), and
// stores the answer in the chat binding, so later changes of the active account never move an existing chat. The Credentials or Routing
// area registers an IChatAccounts over its account store; without one (the faux provider, and until that registration lands) chats bind no
// account and their requests carry none. Only ids and labels cross this seam, never a secret.
namespace PiSharp.WebAgent.Chat;

/// <summary>A provider account as a chat sees it: its id and a label to show, never a secret.</summary>
/// <param name="Id">The account id that the chat binding stores and requests carry as <c>metadata.pisharp.account</c>.</param>
/// <param name="Label">The name shown, such as <c>Personal</c> or <c>Work key</c>.</param>
public sealed record ChatAccount(string Id, string Label);

/// <summary>The accounts chats bind. Register one singleton; ChatService resolves it lazily, so any module may register it in any order.</summary>
public interface IChatAccounts
{
    /// <summary>The account a new chat with <paramref name="provider"/> binds (the provider's active account), or null when the provider
    /// needs none or has none.</summary>
    Task<ChatAccount?> BindAsync(string provider, CancellationToken cancellationToken = default);

    /// <summary>The account <paramref name="accountId"/> of <paramref name="provider"/>, or null when there is no such account (for a label,
    /// and to check an account the user picks explicitly).</summary>
    Task<ChatAccount?> FindAsync(string provider, string accountId, CancellationToken cancellationToken = default);
}

/// <summary>No accounts: chats bind none. The default until an area registers <see cref="IChatAccounts"/>.</summary>
public sealed class NoChatAccounts : IChatAccounts
{
    /// <summary>The shared instance.</summary>
    public static NoChatAccounts Instance { get; } = new();

    /// <inheritdoc/>
    public Task<ChatAccount?> BindAsync(string provider, CancellationToken cancellationToken = default) => Task.FromResult<ChatAccount?>(null);

    /// <inheritdoc/>
    public Task<ChatAccount?> FindAsync(string provider, string accountId, CancellationToken cancellationToken = default) =>
        Task.FromResult<ChatAccount?>(null);
}
