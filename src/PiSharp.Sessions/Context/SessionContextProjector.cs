using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Sessions.Serialization;

namespace PiSharp.Sessions.Context;

public sealed record SessionContextProjectionOptions(int MaximumEntries = 100_000, int MaximumAncestorSteps = 100_000,
    int MaximumInputCharacters = 16_777_216, int MaximumOutputMessages = 100_000, int MaximumOutputCharacters = 16_777_216);
public sealed record SessionContextModel(string Provider, string ModelId);
public sealed record SessionContextContribution(SessionEntry SourceEntry, ImmutableArray<TranscriptEntry> Messages);
public sealed record SessionContextProjection(string? LeafId, ImmutableArray<SessionEntry> Ancestry,
    ImmutableDictionary<string, SessionEntry> ById, ImmutableArray<string> RootIds,
    ImmutableArray<TranscriptEntry> Messages, ImmutableArray<TranscriptEntry> LlmMessages,
    string ThinkingLevel, SessionContextModel? Model, ImmutableArray<SessionContextContribution> ContextEntries = default);
public enum SessionContextProjectionFailure
{
    InvalidEntries, DuplicateId, MissingParent, Cycle, MissingLeaf, ResourceLimit,
    UnsupportedInfluence, UnsupportedMessage, UnsupportedTimestamp
}
public sealed class SessionContextProjectionException : Exception
{
    public SessionContextProjectionFailure Failure { get; }
    internal SessionContextProjectionException(SessionContextProjectionFailure failure) : base(failure switch
    {
        SessionContextProjectionFailure.DuplicateId => "Session tree contains duplicate identities.",
        SessionContextProjectionFailure.MissingParent => "Session tree contains a missing parent.",
        SessionContextProjectionFailure.Cycle => "Session tree contains a cycle.",
        SessionContextProjectionFailure.MissingLeaf => "Selected session leaf does not exist.",
        SessionContextProjectionFailure.ResourceLimit => "Session context projection exceeds configured limits.",
        SessionContextProjectionFailure.UnsupportedInfluence => "Selected branch contains context influence that requires unfinished projection support.",
        SessionContextProjectionFailure.UnsupportedMessage => "Selected branch contains a message conversion that requires unfinished support.",
        SessionContextProjectionFailure.UnsupportedTimestamp => "Session context conversion requires a supported ISO timestamp.",
        _ => "Session context requires initialized validated tree entries with nonempty identities."
    }) => Failure = failure;
}

/// <summary>Pure selected-ancestry projection. Owns no session file, provider, tool or run lifecycle.</summary>
public sealed class SessionContextProjector
{
    private readonly SessionContextProjectionOptions _options;
    /// <summary>The bounds this projector admits a branch under.</summary>
    public SessionContextProjectionOptions Options => _options;
    public SessionContextProjector(SessionContextProjectionOptions? options = null)
    {
        _options = options ?? new();
        if (_options.MaximumEntries <= 0 || _options.MaximumAncestorSteps <= 0 || _options.MaximumInputCharacters <= 0 ||
            _options.MaximumOutputMessages <= 0 || _options.MaximumOutputCharacters <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Invalid session context limits.");
    }

    /// <summary>Agent history retains custom metadata until request conversion; other source-only roles use existing LLM projection.</summary>
    public static ImmutableArray<TranscriptEntry> AgentMessages(SessionContextProjection context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Messages.Select(message => message.Role == "custom" ? message : SessionContextInfluenceProjector.ToLlm(message))
            .Where(message => message is not null).Select(message => message!).ToImmutableArray();
    }

    public SessionContextProjection Project(ImmutableArray<SessionEntry> entries, string? selectedLeafId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (entries.IsDefault) throw Failure(SessionContextProjectionFailure.InvalidEntries);
        if (entries.Length > _options.MaximumEntries) throw Failure(SessionContextProjectionFailure.ResourceLimit);
        var byId = ImmutableDictionary.CreateBuilder<string, SessionEntry>(StringComparer.Ordinal);
        var roots = ImmutableArray.CreateBuilder<string>(); long inputCharacters = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry is null || entry.IsHeader || entry.Id.Length == 0 || entry.ParentId is { Length: 0 })
                throw Failure(SessionContextProjectionFailure.InvalidEntries);
            inputCharacters += entry.WireBody.Value.GetRawText().Length;
            if (inputCharacters > _options.MaximumInputCharacters) throw Failure(SessionContextProjectionFailure.ResourceLimit);
            if (!byId.TryAdd(entry.Id, entry)) throw Failure(SessionContextProjectionFailure.DuplicateId);
            if (entry.ParentId is null) roots.Add(entry.Id);
        }
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.ParentId is { } parent && !byId.ContainsKey(parent)) throw Failure(SessionContextProjectionFailure.MissingParent);
        }
        // Each identity is visited once across completed walks. Validate disconnected siblings too.
        var completed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (completed.Contains(entry.Id)) continue;
            var walk = new HashSet<string>(StringComparer.Ordinal); SessionEntry? current = entry;
            while (current is not null && !completed.Contains(current.Id))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!walk.Add(current.Id)) throw Failure(SessionContextProjectionFailure.Cycle);
                current = current.ParentId is { } parent ? byId[parent] : null;
            }
            completed.UnionWith(walk);
        }
        var ancestry = ImmutableArray.CreateBuilder<SessionEntry>();
        if (selectedLeafId is not null)
        {
            if (!byId.TryGetValue(selectedLeafId, out var leaf)) throw Failure(SessionContextProjectionFailure.MissingLeaf);
            SessionEntry? current = leaf;
            while (current is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ancestry.Count >= _options.MaximumAncestorSteps) throw Failure(SessionContextProjectionFailure.ResourceLimit);
                ancestry.Add(current); current = current.ParentId is { } parent ? byId[parent] : null;
            }
            ancestry.Reverse();
        }
        var messages = ImmutableArray.CreateBuilder<TranscriptEntry>();
        var llmMessages = ImmutableArray.CreateBuilder<TranscriptEntry>();
        long messageCharacters = 0; long llmCharacters = 0;
        var thinkingLevel = "off"; SessionContextModel? model = null;
        foreach (var entry in ancestry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = entry.WireBody.Value;
            if (entry.Kind == SessionEntryKind.ThinkingLevelChange) thinkingLevel = body.GetProperty("thinkingLevel").GetString()!;
            else if (entry.Kind == SessionEntryKind.ModelChange) model = new(body.GetProperty("provider").GetString()!, body.GetProperty("modelId").GetString()!);
            else if (entry.Kind == SessionEntryKind.Message && body.GetProperty("message").GetProperty("role").GetString() == "assistant")
            {
                var assistant = body.GetProperty("message");
                model = new(assistant.GetProperty("provider").GetString()!, assistant.GetProperty("model").GetString()!);
            }
        }
        var active = SessionContextInfluenceProjector.SelectEntries(ancestry.ToImmutable(), cancellationToken);
        var edits = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var entry in active)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Kind == SessionEntryKind.ContextEdit)
                edits[entry.WireBody.Value.GetProperty("targetId").GetString()!] = entry.WireBody.Value.GetProperty("replacement");
        }
        var contributions = ImmutableArray.CreateBuilder<SessionContextContribution>(active.Length);
        for (var index = 0; index < active.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = active[index];
            var contribution = entry.Kind == SessionEntryKind.Compaction && index > 0 ? ImmutableArray<TranscriptEntry>.Empty :
                SessionContextInfluenceProjector.ProjectEntry(entry, edits.TryGetValue(entry.Id, out var replacement) ? replacement : null, cancellationToken);
            contributions.Add(new(entry, contribution));
            foreach (var message in contribution)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Add(messages, message, ref messageCharacters);
                var converted = SessionContextInfluenceProjector.ToLlm(message);
                if (converted is not null) Add(llmMessages, converted, ref llmCharacters);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(selectedLeafId, ancestry.ToImmutable(), byId.ToImmutable(), roots.ToImmutable(), messages.ToImmutable(),
            llmMessages.ToImmutable(), thinkingLevel, model, contributions.ToImmutable());

        void Add(ImmutableArray<TranscriptEntry>.Builder target, TranscriptEntry message, ref long characters)
        {
            var length = message.WireBody.Value.GetRawText().Length;
            if (target.Count >= _options.MaximumOutputMessages || length > _options.MaximumOutputCharacters - characters)
                throw Failure(SessionContextProjectionFailure.ResourceLimit);
            characters += length; target.Add(message);
        }
    }

    public SessionContextProjection ProjectLatest(ImmutableArray<SessionEntry> entries, CancellationToken cancellationToken = default) =>
        Project(entries, entries.IsDefaultOrEmpty || entries[^1] is null ? null : entries[^1].Id, cancellationToken);

    private static SessionContextProjectionException Failure(SessionContextProjectionFailure failure) => new(failure);
}
