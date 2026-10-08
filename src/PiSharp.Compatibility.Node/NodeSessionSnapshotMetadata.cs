using System.Text;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions;

namespace PiSharp.Compatibility.Node;

/// <summary>Read-only active ancestry from the actual admitted callback. No full tree, file,
/// header, mutation or substitute SessionManager is inferred from this narrow host view.</summary>
public static class NodeSessionSnapshotMetadata
{
    public const int MaximumSnapshotBytes = 131_072;
    public static void AddTo(Dictionary<string, object?> payload, IExtensionContext context)
    {
        ArgumentNullException.ThrowIfNull(payload); ArgumentNullException.ThrowIfNull(context);
        context.OperationCancellationToken.ThrowIfCancellationRequested();
        context.SessionCancellationToken.ThrowIfCancellationRequested();
        context.ExtensionLifetimeCancellationToken.ThrowIfCancellationRequested();
        if (context is not IExtensionSessionContext session)
        { payload.Add("sessionSnapshotPresence", "unavailable"); return; }
        var snapshot = session.SessionSnapshot;
        if (snapshot is null) { payload.Add("sessionSnapshotPresence", "none"); return; }
        if (snapshot.Generation < 1 || string.IsNullOrEmpty(snapshot.SessionId) || snapshot.SessionId.Length > 128 ||
            snapshot.SelectedLeafId is { Length: > 128 } || snapshot.BranchEntries.IsDefault ||
            snapshot.BranchEntries.Length > ExtensionSessionSnapshotLimits.MaximumBranchEntries || !Enum.IsDefined(snapshot.Persistence))
            throw new InvalidOperationException("Actual Node callback session snapshot is invalid.");
        long retained = Encoding.UTF8.GetByteCount(snapshot.SessionId) +
            Encoding.UTF8.GetByteCount(snapshot.SelectedLeafId ?? "");
        foreach (var entry in snapshot.BranchEntries)
        {
            if (entry is null) throw new InvalidOperationException("Actual Node branch contains no entry.");
            retained += Encoding.UTF8.GetByteCount(entry.ToString());
            if (retained > MaximumSnapshotBytes) throw new IOException("Actual Node branch exceeds its finite transport bound.");
        }
        var json = JsonSerializer.Serialize(new
        {
            sessionId = snapshot.SessionId, generation = snapshot.Generation, selectedLeafId = snapshot.SelectedLeafId,
            branchEntries = snapshot.BranchEntries.Select(entry => entry.Value).ToArray(),
            persistence = snapshot.Persistence.ToString()
        });
        if (json.Length > MaximumSnapshotBytes || Encoding.UTF8.GetByteCount(json) > MaximumSnapshotBytes)
            throw new IOException("Actual Node active branch exceeds its finite transport bound; no truncation permitted.");
        context.OperationCancellationToken.ThrowIfCancellationRequested();
        context.SessionCancellationToken.ThrowIfCancellationRequested();
        context.ExtensionLifetimeCancellationToken.ThrowIfCancellationRequested();
        payload.Add("sessionSnapshotPresence", "json"); payload.Add("sessionSnapshotJson", json);
    }
}
