using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;

namespace PiSharp.Sessions.Tree;

public enum SessionForkPosition { Before, At }
public enum SessionBranchPlanFailure { InvalidRequest, MissingEntry, RequiresUserMessage, InvalidGraph, MetadataUnavailable, ResourceLimit }
public sealed class SessionBranchPlanException : Exception
{
    public SessionBranchPlanFailure Failure { get; }
    internal SessionBranchPlanException(SessionBranchPlanFailure failure) : base(failure switch
    {
        // agent-session-runtime.ts fork(): a missing entry, and a "before" fork of anything but a user message, both throw this.
        SessionBranchPlanFailure.MissingEntry or SessionBranchPlanFailure.RequiresUserMessage => "Invalid entry ID for forking",
        SessionBranchPlanFailure.MetadataUnavailable => "Session labels cannot be copied under the supported profile.",
        SessionBranchPlanFailure.ResourceLimit => "Session branch exceeds configured limits.",
        SessionBranchPlanFailure.InvalidGraph => "Session fork requires a complete valid source tree.",
        _ => "Session branch request is invalid."
    }) => Failure = failure;
}
/// <param name="JavaScriptSerialization">Write the new file as session-manager.ts createBranchedSession does, every record
/// <c>JSON.stringify(entry)</c> (see <see cref="Storage.SessionLogStoreOptions"/>); the plan's entries hold the same text.</param>
public sealed record SessionBranchPlanOptions(int MaximumEntries = 100_000, int MaximumOutputBytes = 16_777_216,
    SessionEntryCodecOptions? CodecOptions = null, int MaximumInputCharacters = 16_777_216, bool JavaScriptSerialization = false);
public sealed record SessionForkPlanRequest(SessionEntry SourceHeader, ImmutableArray<SessionEntry> SourceEntries,
    string EntryId, SessionForkPosition Position, string NewSessionId, string Timestamp, string? ParentSession,
    ImmutableArray<string> LabelEntryIds = default);
public sealed record SessionBranchPlan(SessionEntry Header, ImmutableArray<SessionEntry> Entries,
    string? LeafId, string? SourceLeafId, string? SelectedText, bool HasConversation,
    ImmutableArray<byte> JsonlBytes);

/// <summary>Bounded, effect-free current-v3 branch construction. IDs and timestamps belong to the caller.
/// Supplied label IDs are consumed in global label insertion order; unused IDs grant no authority.</summary>
public sealed class SessionBranchPlanner
{
    private readonly SessionBranchPlanOptions options;
    private readonly SessionEntryCodec codec;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public SessionBranchPlanner(SessionBranchPlanOptions? options = null)
    {
        this.options = options ?? new();
        if (this.options.MaximumEntries is < 1 or > 100_000 || this.options.MaximumOutputBytes is < 1 or > PiSharp.Sessions.Storage.SessionBranchPublisher.MaximumInputBytes ||
            this.options.MaximumInputCharacters is < 1 or > PiSharp.Sessions.Storage.SessionBranchPublisher.MaximumInputBytes)
            throw new ArgumentOutOfRangeException(nameof(options));
        codec = new(this.options.CodecOptions);
    }

    public SessionBranchPlan New(string sessionId, string timestamp, string workingDirectory, string? parentSession = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var header = Header(sessionId, timestamp, workingDirectory, parentSession);
        return Finish(header, [], null, null, cancellationToken);
    }

    /// <summary>Validates the source and counts only labels whose original targets survive the selected path.
    /// Hosts can acquire exactly that many fresh IDs after a lifecycle veto, without generating unused IDs.</summary>
    public int GetRequiredLabelCount(ImmutableArray<SessionEntry> entries, string entryId, SessionForkPosition position,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (entries.IsDefault || string.IsNullOrEmpty(entryId) || !Enum.IsDefined(position)) throw Error(SessionBranchPlanFailure.InvalidRequest);
        var tree = BuildTree(entries, cancellationToken);
        var selected = tree.GetEntry(entryId) ?? throw Error(SessionBranchPlanFailure.MissingEntry);
        if (position == SessionForkPosition.Before && (selected.Kind != SessionEntryKind.Message ||
            selected.WireBody.Value.GetProperty("message").GetProperty("role").GetString() != "user"))
            throw Error(SessionBranchPlanFailure.RequiresUserMessage);
        var leaf = position == SessionForkPosition.Before ? selected.ParentId : selected.Id;
        if (leaf is null) return 0;
        if (!tree.LabelsAvailable) throw Error(SessionBranchPlanFailure.MetadataUnavailable);
        var retained = tree.GetBranch(leaf, cancellationToken).Where(entry => entry.Kind != SessionEntryKind.Label)
            .Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        var count = 0;
        foreach (var target in tree.Labels.Keys) { cancellationToken.ThrowIfCancellationRequested(); if (retained.Contains(target)) count++; }
        return count;
    }

    public SessionBranchPlan Fork(SessionForkPlanRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); cancellationToken.ThrowIfCancellationRequested();
        if (request.SourceHeader is null || !request.SourceHeader.IsHeader || request.SourceEntries.IsDefault ||
            !Enum.IsDefined(request.Position) || string.IsNullOrEmpty(request.EntryId))
            throw Error(SessionBranchPlanFailure.InvalidRequest);
        if (request.SourceEntries.Length > options.MaximumEntries ||
            !request.LabelEntryIds.IsDefault && request.LabelEntryIds.Length > options.MaximumEntries)
            throw Error(SessionBranchPlanFailure.ResourceLimit);
        var sourceHeader = codec.Read(request.SourceHeader.WireBody.Value);
        var header = Header(request.NewSessionId, request.Timestamp,
            sourceHeader.WireBody.Value.GetProperty("cwd").GetString()!, request.ParentSession);
        var tree = BuildTree(request.SourceEntries, cancellationToken);
        if (header.Id == sourceHeader.Id || tree.ById.ContainsKey(header.Id)) throw Error(SessionBranchPlanFailure.InvalidRequest);
        var selected = tree.GetEntry(request.EntryId) ?? throw Error(SessionBranchPlanFailure.MissingEntry);
        string? selectedText = null; string? leaf = selected.Id;
        if (request.Position == SessionForkPosition.Before)
        {
            if (selected.Kind != SessionEntryKind.Message || selected.WireBody.Value.GetProperty("message")
                .GetProperty("role").GetString() != "user") throw Error(SessionBranchPlanFailure.RequiresUserMessage);
            selectedText = UserText(selected.WireBody.Value.GetProperty("message").GetProperty("content"), cancellationToken);
            leaf = selected.ParentId;
        }
        // Forking before a root user entry creates a new empty session, without historical labels.
        if (leaf is null) return Finish(header, [], null, selectedText, cancellationToken);
        if (!tree.LabelsAvailable) throw Error(SessionBranchPlanFailure.MetadataUnavailable);
        return CopyPath(request, tree, header, leaf, selectedText, cancellationToken);
    }

    private SessionTreeSnapshot BuildTree(ImmutableArray<SessionEntry> entries, CancellationToken token)
    {
        if (entries.Length > options.MaximumEntries) throw Error(SessionBranchPlanFailure.ResourceLimit);
        try
        {
            return new SessionTreeQueries(new(new(MaximumEntries: options.MaximumEntries,
                MaximumInputCharacters: options.MaximumInputCharacters), MaximumQueryEntries: options.MaximumEntries))
                .Build(entries, token);
        }
        catch (SessionContextProjectionException error)
        { throw Error(error.Failure == SessionContextProjectionFailure.ResourceLimit ? SessionBranchPlanFailure.ResourceLimit : SessionBranchPlanFailure.InvalidGraph); }
        catch (SessionTreeQueryException error) when (error.Failure == SessionTreeQueryFailure.ResourceLimit)
        { throw Error(SessionBranchPlanFailure.ResourceLimit); }
    }
    private SessionBranchPlan CopyPath(SessionForkPlanRequest request, SessionTreeSnapshot tree, SessionEntry header,
        string leaf, string? selectedText, CancellationToken cancellationToken)
    {
        var path = tree.GetBranch(leaf, cancellationToken);
        var retained = ImmutableArray.CreateBuilder<SessionEntry>();
        var pendingLabels = new List<string>();
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        string? parent = null;
        foreach (var entry in path)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Kind == SessionEntryKind.Label) { pendingLabels.Add(entry.Id); continue; }
            foreach (var labelId in pendingLabels) replacements.Add(labelId, entry.Id);
            pendingLabels.Clear();
            string? firstKept = null;
            if (entry.Kind == SessionEntryKind.Compaction)
            {
                var previousKept = entry.WireBody.Value.GetProperty("firstKeptEntryId").GetString()!;
                firstKept = previousKept == entry.Id ? previousKept : replacements.GetValueOrDefault(previousKept, previousKept);
            }
            retained.Add(Rewrite(entry, parent, firstKept)); parent = entry.Id;
        }
        // Dictionary overwrites retain insertion order; removals and subsequent re-additions move to the end,
        // exactly as the pinned JavaScript Map. Keep the order explicit instead of relying on .NET enumeration.
        var labelOrder = new LinkedList<string>();
        var labelNodes = new Dictionary<string, LinkedListNode<string>>(StringComparer.Ordinal);
        foreach (var entry in request.SourceEntries)
        {
            cancellationToken.ThrowIfCancellationRequested(); if (entry.Kind != SessionEntryKind.Label) continue;
            var body = entry.WireBody.Value; var target = body.GetProperty("targetId").GetString()!;
            var clear = !body.TryGetProperty("label", out var label) || label.ValueKind == JsonValueKind.Null ||
                label.ValueKind == JsonValueKind.String && label.GetString()!.Length == 0;
            if (clear)
            { if (labelNodes.Remove(target, out var node)) labelOrder.Remove(node); }
            else if (!labelNodes.ContainsKey(target)) labelNodes.Add(target, labelOrder.AddLast(target));
        }
        var pathIds = retained.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        var usedIds = new HashSet<string>(pathIds, StringComparer.Ordinal) { header.Id };
        var labelIndex = 0;
        foreach (var target in labelOrder)
        {
            cancellationToken.ThrowIfCancellationRequested(); if (!pathIds.Contains(target)) continue;
            if (request.LabelEntryIds.IsDefault || labelIndex >= request.LabelEntryIds.Length) throw Error(SessionBranchPlanFailure.InvalidRequest);
            var id = request.LabelEntryIds[labelIndex++]; ValidateId(id);
            if (!usedIds.Add(id)) throw Error(SessionBranchPlanFailure.InvalidRequest);
            var resolved = tree.Labels[target];
            retained.Add(Encode(writer =>
            {
                writer.WriteString("type", "label"); writer.WriteString("id", id); writer.WriteString("parentId", parent);
                writer.WriteString("timestamp", resolved.Timestamp); writer.WriteString("targetId", target);
                writer.WriteString("label", resolved.Label);
            }));
            parent = id;
        }
        return Finish(header, retained.ToImmutable(), leaf, selectedText, cancellationToken);
    }

    private SessionEntry Header(string id, string timestamp, string cwd, string? parentSession)
    {
        ValidateId(id);
        if (string.IsNullOrWhiteSpace(timestamp) || timestamp.Length > 128 || string.IsNullOrWhiteSpace(cwd) || cwd.Length > 4096 ||
            parentSession is { Length: > 4096 } || parentSession is not null && string.IsNullOrWhiteSpace(parentSession))
            throw Error(SessionBranchPlanFailure.InvalidRequest);
        ValidateUnicode(timestamp); ValidateUnicode(cwd); if (parentSession is not null) ValidateUnicode(parentSession);
        return Encode(writer =>
        {
            writer.WriteString("type", "session"); writer.WriteNumber("version", SessionEntryCodec.CurrentVersion);
            writer.WriteString("id", id); writer.WriteString("timestamp", timestamp); writer.WriteString("cwd", cwd);
            if (parentSession is not null) writer.WriteString("parentSession", parentSession);
        });
    }
    private SessionEntry Rewrite(SessionEntry source, string? parent, string? firstKept) => Encode(writer =>
    {
        foreach (var property in source.WireBody.Value.EnumerateObject())
        {
            writer.WritePropertyName(property.Name);
            if (property.Name == "parentId") { if (parent is null) writer.WriteNullValue(); else writer.WriteStringValue(parent); }
            else if (property.Name == "firstKeptEntryId" && firstKept is not null) writer.WriteStringValue(firstKept);
            else writer.WriteRawValue(property.Value.GetRawText(), skipInputValidation: true);
        }
    });
    private SessionEntry Encode(Action<Utf8JsonWriter> fields)
    {
        using var stream = new MemoryStream(); using (var writer = new Utf8JsonWriter(stream))
        { writer.WriteStartObject(); fields(writer); writer.WriteEndObject(); }
        return codec.ParseUtf8(stream.ToArray());
    }
    private SessionBranchPlan Finish(SessionEntry header, ImmutableArray<SessionEntry> entries, string? sourceLeaf,
        string? selectedText, CancellationToken token)
    {
        if (entries.Length > options.MaximumEntries) throw Error(SessionBranchPlanFailure.ResourceLimit);
        if (options.JavaScriptSerialization)
        {
            header = Stringify(header);
            entries = entries.Select(entry => { token.ThrowIfCancellationRequested(); return Stringify(entry); }).ToImmutableArray();
        }
        using var bytes = new MemoryStream(); Append(header);
        foreach (var entry in entries) { token.ThrowIfCancellationRequested(); Append(entry); }
        token.ThrowIfCancellationRequested();
        return new(header, entries, entries.IsEmpty ? null : entries[^1].Id, sourceLeaf, selectedText,
            entries.Any(entry => entry.Kind == SessionEntryKind.Message &&
                entry.WireBody.Value.GetProperty("message").GetProperty("role").GetString() is "user" or "assistant"),
            bytes.ToArray().ToImmutableArray());
        SessionEntry Stringify(SessionEntry entry)
        {
            try { return SessionJavaScriptJson.Stringify(codec, entry); }
            catch (Exception error) when (error is PiSharp.Contracts.Compatibility.EcmaScriptJsonProjectionException or SessionEntryCodecException)
            { throw Error(SessionBranchPlanFailure.ResourceLimit); }
        }
        void Append(SessionEntry entry)
        {
            var encoded = Utf8.GetBytes(codec.Serialize(entry));
            if (encoded.Length + 1L > options.MaximumOutputBytes - bytes.Length) throw Error(SessionBranchPlanFailure.ResourceLimit);
            bytes.Write(encoded); bytes.WriteByte((byte)'\n');
        }
    }
    private static string UserText(JsonElement content, CancellationToken token)
    {
        if (content.ValueKind == JsonValueKind.String) return content.GetString()!;
        var text = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            if (block.TryGetProperty("type", out var type) && type.GetString() == "text" &&
                block.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String) text.Append(value.GetString());
        }
        return text.ToString();
    }
    private static void ValidateId(string id)
    { if (string.IsNullOrWhiteSpace(id) || id.Length > 128) throw Error(SessionBranchPlanFailure.InvalidRequest); ValidateUnicode(id); }
    private static void ValidateUnicode(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            { if (++index >= value.Length || !char.IsLowSurrogate(value[index])) throw Error(SessionBranchPlanFailure.InvalidRequest); }
            else if (char.IsLowSurrogate(value[index])) throw Error(SessionBranchPlanFailure.InvalidRequest);
        }
    }
    private static SessionBranchPlanException Error(SessionBranchPlanFailure failure) => new(failure);
}
