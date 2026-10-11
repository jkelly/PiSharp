using System.Collections.Immutable;
using PiSharp.Contracts;
using PiSharp.Sessions.Serialization;

namespace PiSharp.Sessions.Context;

public sealed partial class SessionContextProjector
{
    private sealed record CachedProjection(ImmutableArray<SessionEntry> Entries, SessionContextProjection Projection,
        long InputCharacters, long MessageCharacters, long LlmCharacters, bool HasContextEdits);
    private CachedProjection? _cachedProjection;

    private bool TryAppendProjection(ImmutableArray<SessionEntry> entries, string? leaf, CancellationToken token,
        out SessionContextProjection projection)
    {
        projection = null!;
        var cached = Volatile.Read(ref _cachedProjection);
        if (cached is null) return false;
        if (entries == cached.Entries && leaf == cached.Projection.LeafId)
        { projection = cached.Projection; return true; }
        // Keep full forest/influence validation for navigation, replacement, edits and compaction.
        if (cached.HasContextEdits || entries.Length != cached.Entries.Length + 1) return false;
        var entry = entries[^1]; var previous = cached.Projection;
        if (entry is null || entry.Kind != SessionEntryKind.Message || entry.IsHeader || entry.Id.Length == 0 ||
            entry.ParentId != previous.LeafId || entry.Id != leaf || previous.ById.ContainsKey(entry.Id)) return false;
        for (var i = 0; i < cached.Entries.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(entries[i], cached.Entries[i])) return false;
        }
        var inputCharacters = cached.InputCharacters + entry.WireBody.Value.GetRawText().Length;
        if (inputCharacters > _options.MaximumInputCharacters || previous.Ancestry.Length >= _options.MaximumAncestorSteps) return false;
        var contribution = SessionContextInfluenceProjector.ProjectEntry(entry, null, token);
        var llm = ImmutableArray.CreateBuilder<TranscriptEntry>(contribution.Length);
        var messageCharacters = cached.MessageCharacters; var llmCharacters = cached.LlmCharacters;
        foreach (var message in contribution)
        {
            token.ThrowIfCancellationRequested();
            messageCharacters += message.WireBody.Value.GetRawText().Length;
            var converted = SessionContextInfluenceProjector.ToLlm(message);
            if (converted is not null) { llm.Add(converted); llmCharacters += converted.WireBody.Value.GetRawText().Length; }
        }
        if ((long)previous.Messages.Length + contribution.Length > _options.MaximumOutputMessages ||
            (long)previous.LlmMessages.Length + llm.Count > _options.MaximumOutputMessages ||
            messageCharacters > _options.MaximumOutputCharacters || llmCharacters > _options.MaximumOutputCharacters) return false;
        var model = previous.Model;
        var body = entry.WireBody.Value.GetProperty("message");
        if (body.GetProperty("role").GetString() == "assistant")
            model = new(body.GetProperty("provider").GetString()!, body.GetProperty("model").GetString()!);
        projection = previous with
        {
            LeafId = leaf, ById = previous.ById.Add(entry.Id, entry),
            RootIds = entry.ParentId is null ? previous.RootIds.Add(entry.Id) : previous.RootIds,
            Ancestry = previous.Ancestry.Add(entry), Messages = previous.Messages.AddRange(contribution),
            LlmMessages = previous.LlmMessages.AddRange(llm), Model = model,
            ContextEntries = previous.ContextEntries.Add(new(entry, contribution))
        };
        token.ThrowIfCancellationRequested();
        Volatile.Write(ref _cachedProjection, new(entries, projection, inputCharacters, messageCharacters, llmCharacters, false));
        return true;
    }
}
