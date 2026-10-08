using PiSharp.Contracts;

namespace PiSharp.Extensions;

/// <summary>Optional generation-bound writes. A retained branch snapshot alone grants no authority.</summary>
public interface IExtensionSessionActionsContext : IExtensionSessionContext
{
    ValueTask<ExtensionSessionEntryAcknowledgment> AppendSessionEntryAsync(string entryKind, int schemaVersion,
        JsonData data, CancellationToken cancellationToken = default);
}

/// <summary>Session replacement belongs to command callbacks. A successful switch returns a fresh context;
/// its lifetime is owned by the originating admitted callback, including its UI scope.</summary>
public interface IExtensionSessionCommandContext : IExtensionCommandContext, IExtensionSessionActionsContext, IExtensionUiContext
{
    ValueTask<IExtensionSessionCommandContext?> SwitchSessionAsync(string absolutePath, bool useLatestLeaf = true,
        string? selectedLeafId = null, CancellationToken cancellationToken = default);
}

/// <summary>Optional durable native lifecycle. Every successful operation returns a fresh generation-bound
/// context whose UI and action scopes are owned by the admitted originating command.</summary>
public interface IExtensionSessionCreationCommandContext : IExtensionSessionCommandContext
{
    ValueTask<ExtensionSessionCreationResult?> CreateSessionAsync(ExtensionSessionCreationRequest request,
        CancellationToken cancellationToken = default);
}
public enum ExtensionSessionCreationKind { New, ForkBefore, ForkAt, Clone }
public sealed record ExtensionSessionCreationRequest(ExtensionSessionCreationKind Kind, string? EntryId = null, string? ParentSession = null)
{
    public ExtensionNewSessionSetupCallback? Setup { get; init; }
}
public sealed record ExtensionSessionCreationResult(IExtensionSessionCreationCommandContext Context, string? SelectedText);

public sealed record ExtensionSessionEntryAcknowledgment(string SessionId, long Generation, JsonData Entry,
    long Sequence, long ByteOffset, long ByteLength, string? SelectedLeafId)
{
    public ExtensionSessionPersistence Persistence { get; init; } = ExtensionSessionPersistence.DurableLocalFile;
    public bool DurableCheckpointAcknowledged => Persistence == ExtensionSessionPersistence.DurableLocalFile;
}

/// <summary>Host-only broker seam. OpenScope binds the exact captured attachment and active callback owner.</summary>
public interface IExtensionSessionActionProvider : IExtensionSessionViewProvider
{
    IExtensionSessionActionScope OpenScope(IExtensionContext context, ExtensionSessionSnapshot snapshot);
}

public interface IExtensionSessionActionScope : IAsyncDisposable
{
    ExtensionSessionSnapshot Snapshot { get; }
    CancellationToken SessionCancellationToken { get; }
    ValueTask<ExtensionSessionEntryAcknowledgment> AppendAsync(string entryKind, int schemaVersion, JsonData data,
        CancellationToken cancellationToken);
    ValueTask<IExtensionSessionActionScope?> SwitchAsync(string absolutePath, bool useLatestLeaf, string? selectedLeafId,
        CancellationToken cancellationToken);
    ValueTask<IExtensionSessionActionScope?> SwitchAsync(string absolutePath, bool useLatestLeaf, string? selectedLeafId,
        Func<ExtensionSessionSnapshot, CancellationToken, ValueTask> validateStagedSnapshot,
        CancellationToken cancellationToken) => ValueTask.FromException<IExtensionSessionActionScope?>(
            new InvalidOperationException("This session broker does not support staged SDK switch validation."));
}

public sealed record ExtensionSessionSwitchCommittedReceipt(string SessionId, long Generation, string? SelectedLeafId);
public sealed class ExtensionSessionSwitchCommittedException(ExtensionSessionSwitchCommittedReceipt receipt, Exception inner)
    : Exception("Session switch committed, but its returned context could not be admitted; inspect the current session before retrying.", inner)
{
    public ExtensionSessionSwitchCommittedReceipt Receipt { get; } = receipt;
}

/// <summary>Host capability marker; this broker is configured with actual creation services.</summary>
public interface IExtensionSessionCreationProvider : IExtensionSessionActionProvider { }
/// <summary>Trusted host marker for bounded strict JSON session records whose opaque numeric tokens
/// are retained without conversion. This changes no validation of executable plugin input or metadata.</summary>
public interface IExtensionSessionOpaqueViewProvider : IExtensionSessionViewProvider { }
public interface IExtensionSessionCreationScope : IExtensionSessionActionScope
{
    ValueTask<ExtensionSessionCreationScopeResult?> CreateAsync(ExtensionSessionCreationRequest request,
        CancellationToken cancellationToken);
    /// <summary>The required returned snapshot must pass the eventual registry policy while the actual
    /// target is staged and reserved, before retiring the source or publishing the attachment. Older
    /// brokers fail closed rather than executing an operation without this preflight.</summary>
    ValueTask<ExtensionSessionCreationScopeResult?> CreateAsync(ExtensionSessionCreationRequest request,
        Func<ExtensionSessionSnapshot, CancellationToken, ValueTask> validateStagedSnapshot,
        CancellationToken cancellationToken) => ValueTask.FromException<ExtensionSessionCreationScopeResult?>(
            new InvalidOperationException("This session broker does not support staged SDK snapshot validation."));
}
public sealed record ExtensionSessionCreationScopeResult(IExtensionSessionCreationScope Scope, string? SelectedText);
public sealed record ExtensionSessionCreationCommittedReceipt(string SessionId, long Generation, string? SelectedLeafId);
/// <summary>Creation has committed, but admitting the returned context failed after staging validation.
/// The actual scope cleanup is joined; this receipt is observation and grants no mutation authority.</summary>
public sealed class ExtensionSessionCreationCommittedException(ExtensionSessionCreationCommittedReceipt receipt, Exception inner)
    : Exception("Session creation committed, but its returned context could not be admitted; inspect the current session before retrying.", inner)
{
    public ExtensionSessionCreationCommittedReceipt Receipt { get; } = receipt;
}

public static class ExtensionSessionActionFeatures
{
    public const string DurableEntries = "session-durable-entries";
    public const string Replacement = "session-replacement";
    public const string Creation = "session-creation";
    public const string OpaqueRecords = "session-opaque-records";
    public const string Catalog = "session-catalog";
    public const string ContextEdits = "session-context-edits";
}

public interface IExtensionSessionCreationRegistry : IExtensionSessionLifecycleRegistry
{
    IExtensionRegistration RegisterSessionCreationHandler(ExtensionSessionCreationHandlerDescriptor descriptor);
}
/// <summary>Pre-effect proposal: no generated target identity, filename or provisional file exists yet.</summary>
public sealed record ExtensionSessionCreationEvent(string PreviousSessionId, ExtensionSessionCreationKind Kind,
    string? EntryId, string? ParentSession);
public delegate ValueTask<ExtensionSessionSwitchDecision> ExtensionSessionCreationCallback(ExtensionSessionCreationEvent proposal,
    IExtensionContext context, CancellationToken cancellationToken);
public sealed record ExtensionSessionCreationHandlerDescriptor(string RegistrationId, ExtensionSessionCreationCallback HandleAsync);

public interface IExtensionSessionLifecycleRegistry : IExtensionRegistry
{
    IExtensionRegistration RegisterSessionSwitchHandler(ExtensionSessionSwitchHandlerDescriptor descriptor);
}
public enum ExtensionSessionSwitchDecision { Continue, Cancel }
public sealed record ExtensionSessionSwitchEvent(string PreviousSessionId, string TargetSessionId, string TargetPath, string? SelectedLeafId);
public delegate ValueTask<ExtensionSessionSwitchDecision> ExtensionSessionSwitchCallback(ExtensionSessionSwitchEvent proposal,
    IExtensionContext context, CancellationToken cancellationToken);
public sealed record ExtensionSessionSwitchHandlerDescriptor(string RegistrationId, ExtensionSessionSwitchCallback HandleAsync);
