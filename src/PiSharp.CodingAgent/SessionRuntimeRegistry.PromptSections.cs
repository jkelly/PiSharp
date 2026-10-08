using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;

namespace PiSharp.CodingAgent;

public sealed partial class SessionRuntimeRegistry
{
    internal bool HasPromptSectionPreparation => _options.PreparePromptSections is not null;
    public bool UsesPromptSectionPreparation(Func<SessionPromptSectionRequest, SessionPromptSectionPreparation?> preparation)
        => _options.PreparePromptSections == preparation;

    public SessionRuntimeRegistry WithPromptSectionPreparation(Func<SessionPromptSectionRequest, SessionPromptSectionPreparation?> preparation)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        if (preparation.GetInvocationList().Length != 1) throw new ArgumentException("One pure prompt admission required.");
        return new(this, preparation);
    }
    private SessionRuntimeRegistry(SessionRuntimeRegistry source, Func<SessionPromptSectionRequest, SessionPromptSectionPreparation?> preparation)
        : this(source._modelCatalog.Read().Bindings, source._registeredTools, source._policy,
            source._options with { PreparePromptSections = preparation }, source._modelCatalog)
    {
        _catalogReplacementSource = source._catalogReplacementSource;
        _loadoutDrain = source._loadoutDrain; _inLoadoutDrain = source._inLoadoutDrain;
    }

    internal (TranscriptEntry? Message, SessionPromptSectionPreparation? Preparation) PreparePromptSectionMessage(
        ImmutableArray<string> names, ImmutableArray<TranscriptEntry> prior, TranscriptEntry? toolDelta,
        long timestamp, CancellationToken token)
    {
        if (_options.PreparePromptSections is not { } provider) return (toolDelta, null);
        if (provider.GetInvocationList().Length != 1) throw new ArgumentException("One pure prompt admission required.");
        token.ThrowIfCancellationRequested();
        var normalized = NormalizeActiveTools(names, token);
        var system = new SessionSystemReplay().Replay(prior, token).CurrentMessage;
        var prepared = provider(new(normalized, system?.WireBody));
        if (prepared is null) return (toolDelta, null);
        ArgumentNullException.ThrowIfNull(prepared.Revision); ArgumentNullException.ThrowIfNull(prepared.ValidateSource);
        if (prepared.Sections.IsDefault || prepared.Sections.Length > 128 || prepared.ValidateSource.GetInvocationList().Length != 1)
            throw new ArgumentException("Invalid prompt preparation.");
        var desired = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in prepared.Sections)
        {
            if (string.IsNullOrEmpty(pair.Key) || pair.Key.Length > 128 || pair.Value is null || !desired.TryAdd(pair.Key, pair.Value))
                throw new ArgumentException("Invalid prompt section.");
        }
        var previous = new Dictionary<string, string>(StringComparer.Ordinal);
        if (system is not null && system.WireBody.Value.TryGetProperty("sections", out var sections))
        {
            foreach (var property in sections.EnumerateObject()) previous.Add(property.Name, property.Value.GetString()!);
        }
        var diff = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in desired)
            if (!previous.TryGetValue(pair.Key, out var before) || before != pair.Value) diff.Add(pair.Key, pair.Value);
        foreach (var pair in previous) if (!desired.ContainsKey(pair.Key)) diff.Add(pair.Key, null);
        if (diff.Count == 0) return (toolDelta, prepared);
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (toolDelta is not null)
            foreach (var property in toolDelta.WireBody.Value.EnumerateObject()) fields.Add(property.Name, property.Value.Clone());
        else { fields.Add("role", "system"); fields.Add("content", ""); fields.Add("timestamp", timestamp); }
        fields["sections"] = diff;
        var result = new TranscriptEntry("system", JsonData.Parse(JsonSerializer.Serialize(fields)));
        if (result.WireBody.ToString().Length > _options.MaximumCharacters) throw Error(SessionRuntimeRegistryFailure.ResourceLimit);
        token.ThrowIfCancellationRequested();
        return (result, prepared);
    }
}
