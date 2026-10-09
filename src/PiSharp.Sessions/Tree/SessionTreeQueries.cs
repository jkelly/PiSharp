using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;

namespace PiSharp.Sessions.Tree;

public sealed record SessionTreeQueryOptions(SessionContextProjectionOptions? GraphOptions = null,
    int MaximumQueryEntries = 100_000, int MaximumMetadataDiagnostics = 256)
{
    /// <summary>Tree queries under a session's own context bounds (its entry count and characters), not the profile defaults.</summary>
    public static SessionTreeQueryOptions For(SessionContextProjectionOptions? graph) =>
        new(graph, graph?.MaximumEntries ?? 100_000);
}
public enum SessionTreeQueryFailure { ResourceLimit, MissingEntry, MetadataUnavailable }
public sealed class SessionTreeQueryException : Exception
{
    public SessionTreeQueryFailure Failure { get; }
    internal SessionTreeQueryException(SessionTreeQueryFailure failure) : base(failure switch
    {
        SessionTreeQueryFailure.ResourceLimit => "Session tree query exceeds configured limits.",
        SessionTreeQueryFailure.MissingEntry => "Selected session tree entry does not exist.",
        _ => "Session tree metadata is unavailable under the supported profile."
    }) => Failure = failure;
}
public enum SessionTreeDiagnosticCode { UnsupportedTimestamp, InvalidLabel, InvalidSessionName }
public sealed record SessionTreeDiagnostic(SessionTreeDiagnosticCode Code, int RecordIndex)
{
    public string Message => Code switch
    {
        SessionTreeDiagnosticCode.UnsupportedTimestamp => "Chronological ordering requires supported ISO timestamps.",
        SessionTreeDiagnosticCode.InvalidLabel => "Session label metadata requires string, null or absent values.",
        _ => "Session name metadata requires a string, null or absent value."
    };
}
public sealed record SessionResolvedLabel(string Label, string Timestamp);
public sealed record SessionTreeNodeView(SessionEntry Entry, ImmutableArray<string> ChildIds,
    SessionResolvedLabel? ResolvedLabel);
public enum SessionTreeOrderStatus { Completed, UnsupportedTimestamp }
public sealed record SessionTreeOrderResult(SessionTreeOrderStatus Status, ImmutableArray<SessionEntry> Entries,
    ImmutableArray<SessionTreeDiagnostic> Diagnostics);

/// <summary>Immutable forest queries. Physical history and raw wire entries remain authoritative.</summary>
public sealed class SessionTreeSnapshot
{
    private readonly int _queryLimit;
    private readonly ImmutableDictionary<string, int> _positions;
    public ImmutableArray<SessionEntry> Entries { get; }
    public ImmutableDictionary<string, SessionTreeNodeView> ById { get; }
    public ImmutableArray<string> RootIds { get; }
    public ImmutableArray<string> StructuralLeafIds { get; }
    public ImmutableArray<string> BranchPointIds { get; }
    public string? PhysicalLeafId { get; }
    public ImmutableDictionary<string, SessionResolvedLabel> Labels { get; }
    public bool LabelsAvailable { get; }
    public string? SessionName { get; }
    public bool SessionNameAvailable { get; }
    public ImmutableArray<SessionTreeDiagnostic> MetadataDiagnostics { get; }
    internal SessionTreeSnapshot(ImmutableArray<SessionEntry> entries, ImmutableDictionary<string, SessionTreeNodeView> byId,
        ImmutableArray<string> roots, ImmutableArray<string> leaves, ImmutableArray<string> branchPoints,
        ImmutableDictionary<string, SessionResolvedLabel> labels, bool labelsAvailable, string? name, bool nameAvailable,
        ImmutableArray<SessionTreeDiagnostic> diagnostics, ImmutableDictionary<string, int> positions, int queryLimit)
    {
        Entries = entries; ById = byId; RootIds = roots; StructuralLeafIds = leaves; BranchPointIds = branchPoints;
        PhysicalLeafId = entries.IsEmpty ? null : entries[^1].Id; Labels = labels; LabelsAvailable = labelsAvailable;
        SessionName = name; SessionNameAvailable = nameAvailable; MetadataDiagnostics = diagnostics;
        _positions = positions; _queryLimit = queryLimit;
    }

    public SessionEntry? GetEntry(string id)
    { ArgumentNullException.ThrowIfNull(id); return ById.TryGetValue(id, out var node) ? node.Entry : null; }
    public ImmutableArray<SessionEntry> GetChildren(string parentId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parentId); cancellationToken.ThrowIfCancellationRequested();
        if (!ById.TryGetValue(parentId, out var parent)) return [];
        if (parent.ChildIds.Length > _queryLimit) throw Failure(SessionTreeQueryFailure.ResourceLimit);
        var children = ImmutableArray.CreateBuilder<SessionEntry>(parent.ChildIds.Length);
        foreach (var id in parent.ChildIds) { cancellationToken.ThrowIfCancellationRequested(); children.Add(ById[id].Entry); }
        cancellationToken.ThrowIfCancellationRequested(); return children.MoveToImmutable();
    }
    public ImmutableArray<SessionEntry> GetBranch(string? entryId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); if (entryId is null) return [];
        if (!ById.TryGetValue(entryId, out var node)) throw Failure(SessionTreeQueryFailure.MissingEntry);
        var branch = ImmutableArray.CreateBuilder<SessionEntry>();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (branch.Count >= _queryLimit) throw Failure(SessionTreeQueryFailure.ResourceLimit);
            branch.Add(node.Entry); var parentId = node.Entry.ParentId; if (parentId is null) break; node = ById[parentId];
        }
        branch.Reverse(); cancellationToken.ThrowIfCancellationRequested(); return branch.ToImmutable();
    }
    public ImmutableArray<SessionEntry> GetLatestBranch(CancellationToken cancellationToken = default) =>
        GetBranch(PhysicalLeafId, cancellationToken);
    public SessionResolvedLabel? GetLabel(string id)
    {
        ArgumentNullException.ThrowIfNull(id); if (!LabelsAvailable) throw Failure(SessionTreeQueryFailure.MetadataUnavailable);
        return Labels.TryGetValue(id, out var label) ? label : null;
    }
    public SessionTreeOrderResult GetChronologicalChildren(string parentId, CancellationToken cancellationToken = default)
    {
        var children = GetChildren(parentId, cancellationToken);
        // Sorting zero/one elements never compares dates in the pinned implementation.
        if (children.Length < 2) return new(SessionTreeOrderStatus.Completed, children, []);
        var keys = new (SessionEntry Entry, long Milliseconds, int Position)[children.Length];
        for (var index = 0; index < children.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryTimestamp(children[index].Timestamp, out var milliseconds))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new(SessionTreeOrderStatus.UnsupportedTimestamp, [],
                    [new(SessionTreeDiagnosticCode.UnsupportedTimestamp, _positions[children[index].Id])]);
            }
            keys[index] = (children[index], milliseconds, index);
        }
        // Stable physical ties are explicit; no caller callback runs inside sorting.
        Array.Sort(keys, static (left, right) => left.Milliseconds == right.Milliseconds
            ? left.Position.CompareTo(right.Position) : left.Milliseconds.CompareTo(right.Milliseconds));
        var ordered = ImmutableArray.CreateBuilder<SessionEntry>(keys.Length);
        foreach (var key in keys) { cancellationToken.ThrowIfCancellationRequested(); ordered.Add(key.Entry); }
        cancellationToken.ThrowIfCancellationRequested();
        return new(SessionTreeOrderStatus.Completed, ordered.MoveToImmutable(), []);
    }
    private static bool TryTimestamp(string value, out long milliseconds)
    {
        string[] formats = ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
            "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"];
        if (DateTimeOffset.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp))
        { milliseconds = timestamp.ToUnixTimeMilliseconds(); return true; }
        milliseconds = 0; return false;
    }
    private static SessionTreeQueryException Failure(SessionTreeQueryFailure failure) => new(failure);
}

/// <summary>Builds immutable navigation and global metadata using the existing complete-forest admission.</summary>
public sealed class SessionTreeQueries
{
    private readonly SessionTreeQueryOptions _options;
    private readonly SessionContextProjector _graphAdmission;
    public SessionTreeQueries(SessionTreeQueryOptions? options = null)
    {
        _options = options ?? new();
        if (_options.MaximumQueryEntries <= 0 || _options.MaximumMetadataDiagnostics <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Invalid session tree query limits.");
        _graphAdmission = new(_options.GraphOptions);
    }
    public SessionTreeSnapshot Build(ImmutableArray<SessionEntry> entries, CancellationToken cancellationToken = default)
    {
        // Explicit null performs complete graph validation, with no selected message/date conversion.
        var admitted = _graphAdmission.Project(entries, selectedLeafId: null, cancellationToken);
        var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var positions = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
        var labels = ImmutableDictionary.CreateBuilder<string, SessionResolvedLabel>(StringComparer.Ordinal);
        var invalidLabels = new Dictionary<string, int>(StringComparer.Ordinal);
        var latestInfoIndex = -1;
        for (var index = 0; index < entries.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested(); var entry = entries[index];
            positions.Add(entry.Id, index); children.Add(entry.Id, []);
            if (entry.Kind == SessionEntryKind.Label)
            {
                var body = entry.WireBody.Value; var target = body.GetProperty("targetId").GetString()!;
                if (!body.TryGetProperty("label", out var label) || label.ValueKind == JsonValueKind.Null ||
                    (label.ValueKind == JsonValueKind.String && label.GetString()!.Length == 0))
                { labels.Remove(target); invalidLabels.Remove(target); }
                else if (label.ValueKind == JsonValueKind.String)
                { labels[target] = new(label.GetString()!, entry.Timestamp); invalidLabels.Remove(target); }
                else { labels.Remove(target); invalidLabels[target] = index; }
            }
            else if (entry.Kind == SessionEntryKind.SessionInfo) latestInfoIndex = index;
        }
        foreach (var entry in entries)
        { cancellationToken.ThrowIfCancellationRequested(); if (entry.ParentId is { } parent) children[parent].Add(entry.Id); }
        var diagnostics = ImmutableArray.CreateBuilder<SessionTreeDiagnostic>();
        if (invalidLabels.Count > _options.MaximumMetadataDiagnostics)
            throw new SessionTreeQueryException(SessionTreeQueryFailure.ResourceLimit);
        foreach (var invalid in invalidLabels.Values.Order())
        { cancellationToken.ThrowIfCancellationRequested(); AddDiagnostic(new(SessionTreeDiagnosticCode.InvalidLabel, invalid)); }
        var nameAvailable = true; string? name = null;
        if (latestInfoIndex >= 0)
        {
            var body = entries[latestInfoIndex].WireBody.Value;
            if (body.TryGetProperty("name", out var value) && value.ValueKind != JsonValueKind.Null)
            {
                if (value.ValueKind != JsonValueKind.String)
                { nameAvailable = false; AddDiagnostic(new(SessionTreeDiagnosticCode.InvalidSessionName, latestInfoIndex)); }
                else { var trimmed = TrimEcma(value.GetString()!); name = trimmed.Length == 0 ? null : trimmed; }
            }
        }
        var labelsAvailable = invalidLabels.Count == 0;
        var resolved = labelsAvailable ? labels.ToImmutable() : ImmutableDictionary<string, SessionResolvedLabel>.Empty;
        var nodes = ImmutableDictionary.CreateBuilder<string, SessionTreeNodeView>(StringComparer.Ordinal);
        var leaves = ImmutableArray.CreateBuilder<string>(); var branchPoints = ImmutableArray.CreateBuilder<string>();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested(); var childIds = children[entry.Id].ToImmutableArray();
            if (childIds.IsEmpty) leaves.Add(entry.Id); else if (childIds.Length > 1) branchPoints.Add(entry.Id);
            nodes.Add(entry.Id, new(entry, childIds, resolved.TryGetValue(entry.Id, out var label) ? label : null));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(entries, nodes.ToImmutable(), admitted.RootIds, leaves.ToImmutable(), branchPoints.ToImmutable(),
            resolved, labelsAvailable, name, nameAvailable, diagnostics.ToImmutable(), positions.ToImmutable(), _options.MaximumQueryEntries);
        void AddDiagnostic(SessionTreeDiagnostic diagnostic)
        {
            if (diagnostics.Count >= _options.MaximumMetadataDiagnostics) throw new SessionTreeQueryException(SessionTreeQueryFailure.ResourceLimit);
            diagnostics.Add(diagnostic);
        }
    }
    private static string TrimEcma(string value)
    {
        var start = 0; var end = value.Length;
        while (start < end && IsTrim(value[start])) start++;
        while (end > start && IsTrim(value[end - 1])) end--;
        return value[start..end];
    }
    private static bool IsTrim(char value) => value is '\u0009' or '\u000B' or '\u000C' or '\u0020' or '\u00A0' or '\uFEFF' or
        '\u000A' or '\u000D' or '\u2028' or '\u2029' or '\u1680' or '\u202F' or '\u205F' or '\u3000' or >= '\u2000' and <= '\u200A';
}
