using System.Collections.Immutable;

namespace PiSharp.Extensions;

/// <summary>Optional read-only discovery from explicitly configured host stores. A lifecycle callback may
/// list sessions; replacement remains a command operation with fresh generation-bound authority.</summary>
public interface IExtensionSessionCatalogContext : IExtensionSessionContext
{
    ValueTask<ExtensionSessionCatalogPage> ListSessionsAsync(ExtensionSessionCatalogQuery query,
        CancellationToken cancellationToken = default);
}
public interface IExtensionSessionCatalogCommandContext : IExtensionSessionCreationCommandContext, IExtensionSessionCatalogContext
{
    ValueTask<IExtensionSessionCatalogCommandContext?> ResumeSessionAsync(string catalogKey, bool useLatestLeaf = true,
        string? selectedLeafId = null, CancellationToken cancellationToken = default);
}
public sealed record ExtensionSessionCatalogQuery(int PageSize = 32, string? Cursor = null, string? WorkingDirectory = null);
public sealed record ExtensionSessionCatalogItem(string Key, string StoreId, string FileName, string Path, string SessionId,
    string CreatedTimestamp, string WorkingDirectory, string CwdGroup, string? ParentSessionPath,
    long FileBytes, string ModifiedUtc, bool IsCurrent);
public sealed record ExtensionSessionCatalogPage(ImmutableArray<ExtensionSessionCatalogItem> Items, string? NextCursor,
    int SkippedFiles, int UnavailableStores);
public interface IExtensionSessionCatalogProvider : IExtensionSessionCreationProvider { }
public interface IExtensionSessionCatalogScope : IExtensionSessionCreationScope
{
    ValueTask<ExtensionSessionCatalogPage> ListAsync(ExtensionSessionCatalogQuery query, CancellationToken cancellationToken);
    ValueTask<IExtensionSessionCatalogScope?> ResumeAsync(string catalogKey, bool useLatestLeaf, string? selectedLeafId,
        CancellationToken cancellationToken);
    /// <summary>Validate the actual staged branch under the eventual registry policy before attachment
    /// publication. A broker implementing only the older overload fails before effects.</summary>
    ValueTask<IExtensionSessionCatalogScope?> ResumeAsync(string catalogKey, bool useLatestLeaf, string? selectedLeafId,
        Func<ExtensionSessionSnapshot, CancellationToken, ValueTask> validateStagedSnapshot,
        CancellationToken cancellationToken) => ValueTask.FromException<IExtensionSessionCatalogScope?>(
            new InvalidOperationException("This session broker does not support staged SDK resume validation."));
}

public sealed record ExtensionSessionResumeCommittedReceipt(string SessionId, long Generation, string? SelectedLeafId);
/// <summary>The resumed attachment committed, but its returned context failed admission. Scope cleanup
/// is joined before this observation-only receipt is returned.</summary>
public sealed class ExtensionSessionResumeCommittedException(ExtensionSessionResumeCommittedReceipt receipt, Exception inner)
    : Exception("Session resume committed, but its returned context could not be admitted; inspect the current session before retrying.", inner)
{
    public ExtensionSessionResumeCommittedReceipt Receipt { get; } = receipt;
}
