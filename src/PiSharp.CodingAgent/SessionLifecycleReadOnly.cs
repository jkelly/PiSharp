using System.Collections.Immutable;
using System.Text;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Import;
using PiSharp.Sessions.Lifecycle;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;
using PiSharp.Sessions.Tree;

namespace PiSharp.CodingAgent;

public sealed record SessionLifecycleReadOnlyOptions(SessionCopyOptions? CopyOptions = null,
    SessionContextProjectionOptions? ContextOptions = null, SessionTreeQueryOptions? TreeOptions = null);
public enum SessionLifecycleInspectionStatus { Available, InspectionOnly, ProjectionUnavailable }

/// <summary>Owned immutable data, with no writer, session lifetime, provider or extension authority.</summary>
public sealed record SessionLifecycleReadOnlyView(string SourcePath, SessionEntry Header, SessionTreeSnapshot Tree,
    SessionContextProjection Context, long? AttachmentGeneration = null)
{
    public string SessionId => Header.Id;
    public string? SelectedLeafId => Context.LeafId;
    public string? PhysicalLeafId => Tree.PhysicalLeafId;
    public ImmutableArray<SessionEntry> Entries => Tree.Entries;
    public ImmutableArray<SessionEntry> BranchEntries => Context.Ancestry;
}
public sealed record SessionLifecycleInspection(string SourcePath, SessionCopyInspection CopyInspection,
    SessionLifecycleInspectionStatus Status, SessionTreeSnapshot? Tree, SessionLifecycleReadOnlyView? View,
    SessionContextProjectionFailure? ContextFailure = null, SessionTreeQueryFailure? TreeFailure = null)
{
    public ImmutableArray<byte> OriginalBytes => CopyInspection.OriginalBytes;
    public string? SourceSha256 => CopyInspection.SourceSha256;
}
public sealed record SessionSelectedBranchExportRequest(string SourcePath, string DestinationPath,
    SessionCopyFormat Format = SessionCopyFormat.NativeExact, bool UseLatestLeaf = true,
    string? SelectedLeafId = null, ImmutableArray<string> V1EntryIds = default);

/// <summary>The source inspection describes the original file; Copy describes the captured publisher input.
/// NativeExact preserves retained original record byte segments, not the excluded sibling records.
/// No fields are omitted from retained records. Selection never generates a new session or entry identity.</summary>
public sealed record SessionSelectedBranchExportResult(SessionLifecycleInspection SourceInspection,
    SessionCopyResult Copy, string? SelectedLeafId, ImmutableArray<string> ExcludedEntryIds, bool SelectionApplied,
    bool WholeSourceBytesPreserved)
{
    public int OmittedRecords => ExcludedEntryIds.Length;
    public int OmittedFields => Copy.OmittedFields;
}
/// <summary>Exports captured records from a borrowed live or memory-backed view. NativeExact means
/// retained raw JSON record bodies framed with LF; this receipt makes no original-file byte-exact claim.</summary>
public sealed record SessionSelectedViewExportResult(SessionLifecycleReadOnlyView SourceView, SessionCopyResult Copy,
    string? SelectedLeafId, ImmutableArray<string> ExcludedEntryIds)
{
    public int OmittedRecords => ExcludedEntryIds.Length;
    public int OmittedFields => Copy.OmittedFields;
}

/// <summary>Borrowed, provider-free lifecycle inspection and explicit copy/export operations. It creates no
/// active coordinator and owns no default home, ID generator or background work. Every async operation
/// joins its own reader/publication cleanup through the existing SessionCopyService.</summary>
public sealed class SessionLifecycleReadOnly
{
    private readonly SessionCopyOptions copyOptions;
    private readonly ISessionCopyFileSystem files;
    private readonly SessionCopyService copies;
    private readonly SessionContextProjector projector;
    private readonly SessionTreeQueries trees;
    private readonly SessionHistoryProjector histories;
    private readonly SessionEntryCodec codec;
    private readonly SessionCatalog? catalog;
    private readonly int captureLimit;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public SessionLifecycleReadOnly(SessionLifecycleReadOnlyOptions? options = null, ISessionCopyFileSystem? fileSystem = null,
        SessionCatalog? catalog = null)
    {
        var configured = options ?? new(); copyOptions = configured.CopyOptions ?? new();
        var bounds = copyOptions.ReaderOptions ?? new();
        var graph = configured.ContextOptions ?? new(MaximumEntries: bounds.MaximumRecords,
            MaximumInputCharacters: bounds.MaximumInputBytes);
        files = fileSystem ?? new LocalReads(); copies = new(copyOptions, files);
        this.catalog = catalog;
        projector = new(graph); trees = new(configured.TreeOptions ?? new(GraphOptions: graph,
            MaximumQueryEntries: graph.MaximumEntries)); codec = new(bounds.CodecOptions);
        histories = new(graph, configured.TreeOptions);
        captureLimit = bounds.MaximumInputBytes;
    }

    public async Task<SessionLifecycleInspection> InspectAsync(string sourcePath, bool useLatestLeaf = true,
        string? selectedLeafId = null, ImmutableArray<string> v1EntryIds = default, CancellationToken cancellationToken = default)
    {
        ValidateSelection(useLatestLeaf, selectedLeafId); cancellationToken.ThrowIfCancellationRequested();
        var inspected = await copies.InspectAsync(sourcePath, v1EntryIds, cancellationToken).ConfigureAwait(false);
        if (!inspected.CanPublishCurrent)
            return new(sourcePath, inspected, SessionLifecycleInspectionStatus.InspectionOnly, null, null);
        var records = inspected.CurrentRecords; var entries = records.RemoveAt(0); SessionTreeSnapshot tree;
        try { tree = trees.Build(entries, cancellationToken); }
        catch (SessionTreeQueryException error)
        { return new(sourcePath, inspected, SessionLifecycleInspectionStatus.ProjectionUnavailable, null, null, TreeFailure: error.Failure); }
        catch (SessionContextProjectionException error)
        { return new(sourcePath, inspected, SessionLifecycleInspectionStatus.ProjectionUnavailable, null, null, error.Failure); }
        var leaf = useLatestLeaf ? tree.PhysicalLeafId : selectedLeafId;
        try
        {
            var context = projector.Project(entries, leaf, cancellationToken);
            return new(sourcePath, inspected, SessionLifecycleInspectionStatus.Available, tree,
                new(sourcePath, records[0], tree, context));
        }
        catch (SessionContextProjectionException error)
        { return new(sourcePath, inspected, SessionLifecycleInspectionStatus.ProjectionUnavailable, tree, null, error.Failure); }
    }

    /// <summary>Captures acknowledged current data and validates its attachment before and after capture.
    /// The owner and its live or memory-backed writer remain borrowed and usable.</summary>
    public SessionLifecycleReadOnlyView Capture(ReplaceableAgentSession owner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner); cancellationToken.ThrowIfCancellationRequested();
        var attached = owner.Current; owner.ValidateAttachment(attached); var snapshot = attached.Session.Snapshot;
        if (snapshot.IsDisposed || snapshot.IsRetired || snapshot.Fault is not null)
            throw new InvalidOperationException("Current session read-only view is unavailable.");
        var tree = trees.Build(snapshot.Log.Entries, cancellationToken);
        var context = projector.Project(snapshot.Log.Entries, snapshot.Context.LeafId, cancellationToken);
        owner.ValidateAttachment(attached); cancellationToken.ThrowIfCancellationRequested();
        return new(attached.Session.Path, snapshot.Log.Header, tree, context, attached.Generation);
    }
    public SessionLifecycleReadOnlyView SelectBranch(SessionLifecycleReadOnlyView view, string? selectedLeafId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(view);
        var tree = trees.Build(view.Entries, cancellationToken);
        var context = projector.Project(view.Entries, selectedLeafId, cancellationToken);
        return new(view.SourcePath, view.Header, tree, context, view.AttachmentGeneration);
    }

    /// <summary>Independent raw/display/model/accounting views of captured data; the owner remains borrowed.</summary>
    public SessionHistoryProjection ProjectHistory(SessionLifecycleReadOnlyView view,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(view);
        return histories.Project(view.Entries, view.SelectedLeafId, cancellationToken);
    }

    public Task<SessionCopyResult> ImportAsync(SessionCopyRequest request, CancellationToken cancellationToken = default)
        => copies.CopyAsync(request, cancellationToken);
    public Task<SessionCopyResult> ExportAsync(SessionCopyRequest request, CancellationToken cancellationToken = default)
        => copies.CopyAsync(request, cancellationToken);
    /// <summary>Lists only explicitly configured borrowed stores; absence never selects a default home.</summary>
    public Task<SessionCatalogPage> ListAsync(SessionCatalogQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query); cancellationToken.ThrowIfCancellationRequested();
        return (catalog ?? throw new InvalidOperationException("Read-only session catalog is unavailable."))
            .ListAsync(query, cancellationToken);
    }

    public async Task<SessionSelectedBranchExportResult> ExportSelectedBranchAsync(SessionSelectedBranchExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Format)) throw new ArgumentException("Session export format is invalid.", nameof(request));
        ValidateSelection(request.UseLatestLeaf, request.SelectedLeafId); cancellationToken.ThrowIfCancellationRequested();
        var source = await InspectAsync(request.SourcePath, request.UseLatestLeaf, request.SelectedLeafId,
            request.V1EntryIds, cancellationToken).ConfigureAwait(false);
        if (!source.CopyInspection.CanPublishCurrent || request.Format == SessionCopyFormat.NativeExact && source.CopyInspection.SourceVersion != 3)
        {
            var blocked = await CapturedCopies(request.SourcePath, source.OriginalBytes).CopyAsync(new(request.SourcePath,
                request.DestinationPath, request.Format, request.V1EntryIds), cancellationToken).ConfigureAwait(false);
            return new(source, blocked, request.SelectedLeafId, [], false, false);
        }
        // Graph admission is independent of executable selected-context conversion. Unsupported selected
        // message influence remains inspectable/exportable data and never acquires provider authority.
        var tree = source.Tree ?? trees.Build(source.CopyInspection.CurrentRecords.RemoveAt(0), cancellationToken);
        var leaf = request.UseLatestLeaf ? tree.PhysicalLeafId : request.SelectedLeafId;
        var branch = tree.GetBranch(leaf, cancellationToken);
        var retained = branch.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        var excluded = tree.Entries.Where(entry => !retained.Contains(entry.Id)).Select(entry => entry.Id).ToImmutableArray();
        var bytes = request.Format == SessionCopyFormat.NativeExact
            ? ExactBranchBytes(source.CopyInspection, retained, excluded.IsEmpty, cancellationToken)
            : CompatibleBranchBytes(source.CopyInspection.CurrentRecords[0], branch, cancellationToken);
        var copied = await CapturedCopies(request.SourcePath, bytes).CopyAsync(new(request.SourcePath,
            request.DestinationPath, request.Format), cancellationToken).ConfigureAwait(false);
        // Publication has an owned receipt. A late caller cancellation must retain its known disposition.
        return new(source, copied with { OmittedRecords = excluded.Length }, leaf, excluded, true,
            copied.Published && request.Format == SessionCopyFormat.NativeExact && excluded.IsEmpty);
    }

    /// <summary>Publishes the captured selected branch without reading or opening its source path.
    /// This also supports a memory-only session whose source path has never existed.</summary>
    public async Task<SessionSelectedViewExportResult> ExportSelectedBranchAsync(SessionLifecycleReadOnlyView view,
        string destinationPath, SessionCopyFormat format = SessionCopyFormat.NativeExact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (!Enum.IsDefined(format) || string.IsNullOrEmpty(destinationPath) || !Path.IsPathFullyQualified(destinationPath))
            throw new ArgumentException("Session view export requires an explicit format and absolute destination.");
        cancellationToken.ThrowIfCancellationRequested();
        var tree = trees.Build(view.Entries, cancellationToken); var branch = tree.GetBranch(view.SelectedLeafId, cancellationToken);
        var retained = branch.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        var excluded = tree.Entries.Where(entry => !retained.Contains(entry.Id)).Select(entry => entry.Id).ToImmutableArray();
        var bytes = CompatibleBranchBytes(view.Header, branch, cancellationToken, compact: format == SessionCopyFormat.CompatibleCurrentJsonl);
        var virtualSource = Path.Combine(Path.GetDirectoryName(destinationPath)!, ".pisharp-readonly-view-source.jsonl");
        var copied = await CapturedCopies(virtualSource, bytes).CopyAsync(new(virtualSource, destinationPath, format), cancellationToken).ConfigureAwait(false);
        return new(view, copied with { OmittedRecords = excluded.Length }, view.SelectedLeafId, excluded);
    }

    private SessionCopyService CapturedCopies(string sourcePath, ImmutableArray<byte> bytes) =>
        new(copyOptions, new CapturedRead(sourcePath, bytes, files));
    private ImmutableArray<byte> ExactBranchBytes(SessionCopyInspection inspection, HashSet<string> retained,
        bool allRecords, CancellationToken token)
    {
        if (allRecords) { token.ThrowIfCancellationRequested(); return inspection.OriginalBytes; }
        using var output = new MemoryStream();
        foreach (var record in inspection.Log.ValidatedPrefix)
        {
            token.ThrowIfCancellationRequested(); if (!record.Entry.IsHeader && !retained.Contains(record.Entry.Id)) continue;
            var start = record.Entry.IsHeader ? 0 : record.ByteOffset;
            var length = record.ByteOffset + record.PhysicalByteLength - start;
            if (length > captureLimit - output.Length) throw new InvalidOperationException("Selected session export exceeds configured capture limits.");
            output.Write(inspection.OriginalBytes.AsSpan(start, length));
        }
        token.ThrowIfCancellationRequested(); return output.ToArray().ToImmutableArray();
    }
    private ImmutableArray<byte> CompatibleBranchBytes(SessionEntry header, ImmutableArray<SessionEntry> branch, CancellationToken token, bool compact = true)
    {
        using var output = new MemoryStream(); Append(header);
        foreach (var entry in branch) Append(entry);
        token.ThrowIfCancellationRequested(); return output.ToArray().ToImmutableArray();
        void Append(SessionEntry entry)
        {
            token.ThrowIfCancellationRequested(); var validated = codec.Read(entry.WireBody.Value);
            var bytes = Utf8.GetBytes(compact ? codec.Serialize(validated) : validated.WireBody.ToString());
            if ((long)bytes.Length + 1 > captureLimit - output.Length) throw new InvalidOperationException("Selected session export exceeds configured capture limits.");
            output.Write(bytes); output.WriteByte((byte)'\n');
        }
    }
    private static void ValidateSelection(bool latest, string? leaf)
    {
        if (latest && leaf is not null || leaf is { Length: 0 })
            throw new ArgumentException("Session view requires latest, a selected entry, or an explicit empty root.");
    }
    private sealed class CapturedRead(string sourcePath, ImmutableArray<byte> bytes, ISessionCopyFileSystem actual) : ISessionCopyFileSystem
    {
        public bool FileExists(string path) => actual.FileExists(path);
        public bool DirectoryExists(string path) => actual.DirectoryExists(path);
        public SessionLogStorageDurability GetDurability(string path) => actual.GetDurability(path);
        public ValueTask<Stream> OpenReadAsync(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!string.Equals(path, sourcePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Captured session copy source changed.");
            return ValueTask.FromResult<Stream>(new MemoryStream(bytes.ToArray(), writable: false));
        }
        public ValueTask<Stream> CreateNewTemporaryAsync(string path) => actual.CreateNewTemporaryAsync(path);
        public ValueTask PublishNewAsync(string temporary, string destination) => actual.PublishNewAsync(temporary, destination);
        public ValueTask DeleteTemporaryAsync(string path) => actual.DeleteTemporaryAsync(path);
    }
    private sealed class LocalReads : ISessionCopyFileSystem
    {
        public ValueTask<Stream> OpenReadAsync(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan));
        }
        public ValueTask<Stream> CreateNewTemporaryAsync(string path) => SessionCopyService.LocalFileSystem.CreateNewTemporaryAsync(path);
        public ValueTask PublishNewAsync(string temporary, string destination) => SessionCopyService.LocalFileSystem.PublishNewAsync(temporary, destination);
        public ValueTask DeleteTemporaryAsync(string path) => SessionCopyService.LocalFileSystem.DeleteTemporaryAsync(path);
    }
}
