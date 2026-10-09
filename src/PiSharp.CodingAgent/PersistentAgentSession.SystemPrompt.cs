// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/agent-session.ts (systemPrompt, _rebuildSystemPrompt,
// emitBeforeAgentStart with _baseSystemPromptOptions, exportToHtml with agent.state.tools).
using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;

namespace PiSharp.CodingAgent;

public sealed partial class PersistentAgentSession
{
    /// <summary>
    /// Source _rebuildSystemPrompt: a selection, a catalog change or a restored loadout rebuilds the prompt in memory at once, and the
    /// next request records it. <paramref name="messages"/> (the transcript, or the agent's history) with the record that request
    /// would write appended: the loadout's tool changes and the prompt sections they change. Unchanged when nothing awaits a record.
    /// </summary>
    public ImmutableArray<TranscriptEntry> WithPendingSystemRecord(ImmutableArray<TranscriptEntry> messages, CancellationToken cancellationToken = default)
    {
        SessionRuntimeRegistry? registry; PendingActivation? pending; bool unrecorded; ImmutableArray<string> current;
        lock (_gate)
        {
            registry = _registry; pending = _pendingActivation; unrecorded = _unrecordedLoadout;
            current = _configuration.Tools.Select(tool => tool.Name).ToImmutableArray();
        }
        if (registry is null || messages.IsDefault) return messages;
        var names = pending?.Names ?? current;
        var tools = pending is not null || unrecorded ? registry.CreateToolChangeMessage(messages, names, 0, cancellationToken) : null;
        TranscriptEntry? record;
        var prior = _activationPreparation.Value;
        _activationPreparation.Value = true;
        try { (record, _) = registry.PreparePromptSectionMessage(names, messages, tools, 0, cancellationToken, pending?.Presentation); }
        finally { _activationPreparation.Value = prior; }
        return record is null ? messages : messages.Add(record);
    }

    /// <summary>Source AgentSession.systemPrompt (getSystemPrompt): the prompt of the in-memory loadout, which the next request
    /// records; between runs it reflects selections and catalog changes made since the last one.</summary>
    public string GetSystemPrompt(CancellationToken cancellationToken = default)
    {
        ImmutableArray<TranscriptEntry> messages;
        lock (_gate) messages = _agent.Snapshot.Messages;
        return new SessionSystemReplay().Replay(WithPendingSystemRecord(messages, cancellationToken), cancellationToken).Prompt;
    }

    /// <summary>Source agent.state.tools (exportToHtml): the declarations of the in-memory loadout, in its order, with the descriptions
    /// that prepareLoadout hooks give them.</summary>
    public ImmutableArray<JsonData> GetActiveToolDeclarations(CancellationToken cancellationToken = default)
    {
        SessionRuntimeRegistry? registry; ImmutableArray<string> names;
        lock (_gate)
        {
            registry = _registry;
            names = _pendingActivation?.Names ?? _configuration.Tools.Select(tool => tool.Name).ToImmutableArray();
        }
        // Without runtime bindings the transcript's declarations are the loadout.
        if (registry is null)
        {
            ImmutableArray<TranscriptEntry> messages;
            lock (_gate) messages = _context.Messages;
            return new SessionSystemReplay().Replay(messages, cancellationToken).Tools;
        }
        var declarations = registry.CreateActivationMessage(names, [], 0, cancellationToken, replaceDeclarations: true)!;
        var prior = _activationPreparation.Value;
        _activationPreparation.Value = true;
        ToolLoadoutPresentation? presentation;
        try { presentation = registry.PrepareActiveLoadout(names, cancellationToken, report: false); }
        finally { _activationPreparation.Value = prior; }
        var original = declarations.WireBody.Value.GetProperty("toolsAdded").EnumerateArray().Select(JsonData.FromElement).ToImmutableArray();
        if (presentation is null) return original;
        // The projection also drops hidden declarations from requests; the loadout still holds them (_applyToolLoadout).
        var described = presentation.Project([declarations]).Single().WireBody.Value.TryGetProperty("toolsAdded", out var added)
            ? added.EnumerateArray().ToDictionary(tool => tool.GetProperty("name").GetString()!, JsonData.FromElement, StringComparer.Ordinal)
            : new Dictionary<string, JsonData>(StringComparer.Ordinal);
        return [.. original.Select(tool => described.TryGetValue(tool.Value.GetProperty("name").GetString()!, out var projected) ? projected : tool)];
    }
}
