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
        long timestamp, CancellationToken token, ToolLoadoutPresentation? presentation = null)
    {
        if (_options.PreparePromptSections is not { } provider) return (toolDelta, null);
        if (provider.GetInvocationList().Length != 1) throw new ArgumentException("One pure prompt admission required.");
        token.ThrowIfCancellationRequested();
        var normalized = NormalizeActiveTools(names, token);
        var system = new SessionSystemReplay().Replay(prior, token).CurrentMessage;
        // Pi 1.0.4: the tool list, rules and skills hint match the declarations the request carries. A loadout the
        // request already prepared is reused; otherwise its diagnostics are not reported again here.
        var hidden = (presentation ?? PrepareActiveLoadout(normalized, token, report: false))?.HiddenDeclarations;
        var prepared = provider(new(normalized, system?.WireBody) { HiddenTools = hidden is null || hidden.IsEmpty ? [] :
            _registeredTools.Select(tool => Name(tool.Declaration.Value)).Where(hidden.Contains).ToImmutableArray() });
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
        // Source prompt/_preparePromptAndToolLoadout then declareToolChanges (withToolChanges): { role, content, sections, timestamp }
        // with the tool changes after them.
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject(); writer.WriteString("role", "system"); writer.WriteString("content", "");
            writer.WritePropertyName("sections"); writer.WriteStartObject();
            foreach (var pair in diff) { if (pair.Value is null) writer.WriteNull(pair.Key); else writer.WriteString(pair.Key, (string)pair.Value); }
            writer.WriteEndObject();
            if (toolDelta?.WireBody.Value.TryGetProperty("timestamp", out var time) == true) { writer.WritePropertyName("timestamp"); writer.WriteRawValue(time.GetRawText(), skipInputValidation: true); }
            else writer.WriteNumber("timestamp", timestamp);
            foreach (var field in new[] { "toolsAdded", "toolsRemoved" })
                if (toolDelta?.WireBody.Value.TryGetProperty(field, out var tools) == true) { writer.WritePropertyName(field); writer.WriteRawValue(tools.GetRawText(), skipInputValidation: true); }
            writer.WriteEndObject();
        }
        var result = new TranscriptEntry("system", JsonData.Parse(System.Text.Encoding.UTF8.GetString(output.ToArray())));
        if (result.WireBody.ToString().Length > _options.MaximumCharacters) throw Error(SessionRuntimeRegistryFailure.ResourceLimit);
        token.ThrowIfCancellationRequested();
        return (result, prepared);
    }
}
