using System.Collections.Immutable;
using PiSharp.Contracts;

namespace PiSharp.Extensions;

/// <summary>An optional read-only view captured once when a host admits an extension callback.</summary>
public interface IExtensionSessionContext : IExtensionContext
{
    /// <summary>Null when the host cannot supply an active session. A retained view grants no action authority.</summary>
    ExtensionSessionSnapshot? SessionSnapshot { get; }
}

/// <summary>An immutable active-branch view. Entries retain complete raw session-entry JSON objects,
/// including unknown properties. Generation identifies the host attachment, not a registry owner.</summary>
public sealed record ExtensionSessionSnapshot(
    string SessionId, long Generation, string? SelectedLeafId, ImmutableArray<JsonData> BranchEntries)
{
    public ExtensionSessionPersistence Persistence { get; init; } = ExtensionSessionPersistence.DurableLocalFile;
}
public enum ExtensionSessionPersistence { DurableLocalFile, VolatileMemory, DeferredLocalFile }

/// <summary>Host-only capture capability. Implementations select validated ancestry; the SDK does not
/// reinterpret the session graph. Capture runs outside the registry lock with the actual callback tokens.</summary>
public interface IExtensionSessionViewProvider
{
    ExtensionSessionSnapshot? Capture(IExtensionContext context);
}

/// <summary>Callback-view memory bounds. Pi's runner.ts hands every handler the whole session manager, so these grow with the
/// session as the request budgets do (PiRequestBudget: one million messages, the 64 MiB request payload); they are not count limits
/// a long session reaches. Hosts report an explicit resource error rather than truncate a branch beyond them.</summary>
public static class ExtensionSessionSnapshotLimits
{
    public const string Feature = "session-branch-snapshot";
    /// <summary>PiRequestBudget.RequestMessages.</summary>
    public const int MaximumBranchEntries = 1_000_000;
    /// <summary>PiRequestBudget.RequestPayloadBytes.</summary>
    public const int MaximumCharacters = 64 * 1024 * 1024;
    /// <summary>PiRequestBudget.RequestPayloadBytes.</summary>
    public const int MaximumUtf8Bytes = 64 * 1024 * 1024;
}
