// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/harness/prompt.ts. Section maps are insertion-ordered as
// JavaScript Maps are (set in place, delete, re-adding appends); a section patch is a JSON object, so its keys enumerate in JavaScript's
// object order, exactly as upstream's Object.fromEntries. Tools are pi-ai wire declarations.
using System.Collections.Immutable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Internal.PiAi;

namespace PiSharp.Durable;

/// <summary>System prompt sections and tool loadout as positional <c>pi.system</c> entries (upstream <c>prompt.ts</c>, spec §7.4).</summary>
internal static class HarnessPrompt
{
    /// <summary>Sections in effect after replaying system messages in order: set in place, <c>null</c> deletes, re-adding appends (upstream
    /// <c>replaySections</c>).</summary>
    public static OrderedDictionary<string, string> ReplaySections(IEnumerable<JsonValue> messages)
    {
        var shown = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (message is not JsonObjectValue value || !IsRole(value, "system") || !value.TryGetValue("sections", out var sections) ||
                sections is not JsonObjectValue keyed) continue;
            foreach (var (key, text) in keyed)
            {
                if (text is JsonStringValue section) shown[key] = section.Value;
                else shown.Remove(key);
            }
        }
        return shown;
    }

    /// <summary>
    /// Renders the agent's sections in order (upstream <c>renderSections</c>). A null text omits a section; tagged text is wrapped as
    /// <c>&lt;key&gt;\n...\n&lt;/key&gt;</c>. A section that throws keeps its shown text, if any, and is reported; errors after
    /// <paramref name="context"/> is cancelled propagate.
    /// </summary>
    public static async Task<OrderedDictionary<string, string>> RenderSectionsAsync(IReadOnlyList<PromptSection> sections, PromptInput input,
        IReadOnlyDictionary<string, string> shown, Action<Exception> report, DurableContext context)
    {
        var desired = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var section in sections)
        {
            string? text;
            try
            {
                text = await section.Render(input, context).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                if (context.IsCancellationRequested) throw;
                report(error);
                if (shown.TryGetValue(section.Key, out var kept)) desired[section.Key] = kept;
                continue;
            }
            if (text is null) continue;
            desired[section.Key] = section.Tag == false ? text : $"<{section.Key}>\n{text}\n</{section.Key}>";
        }
        return desired;
    }

    /// <summary><see cref="PlanSystemEntries(ContextView, IEnumerable{KeyValuePair{string, string}}, IReadOnlyList{JsonObjectValue}, double)"/>
    /// for registered tools, of which only the pi-ai declaration enters the transcript.</summary>
    public static List<TypedEntryDraft> PlanSystemEntries(ContextView view, IEnumerable<KeyValuePair<string, string>> desired,
        IEnumerable<ToolRegistration> tools, double timestamp) =>
        PlanSystemEntries(view, desired, [.. tools.Select(PiAiTranscript.ToToolDeclaration)], timestamp);

    /// <summary>
    /// Plans the <c>pi.system</c> entries that make the replayed sections and tools of <paramref name="view"/> equal
    /// <paramref name="desired"/> and <paramref name="tools"/> in values and order (upstream <c>planSystemEntries</c>):
    /// <list type="bullet">
    /// <item>A head marker with no later <c>pi.system</c> entry in context: one complete baseline that omits every retained earlier
    /// <c>pi.system</c> entry, written even when it restates the replayed values.</item>
    /// <item>Otherwise, when a minimal section patch would leave a different order: remove every shown section, then re-add every desired
    /// section in order.</item>
    /// <item>Otherwise the minimal patch of changed values and <c>null</c> removals, or nothing.</item>
    /// </list>
    /// Tool changes ride on the last planned entry, or on one entry of their own.
    /// </summary>
    public static List<TypedEntryDraft> PlanSystemEntries(ContextView view, IEnumerable<KeyValuePair<string, string>> desired,
        IReadOnlyList<JsonObjectValue> tools, double timestamp)
    {
        var wanted = desired.ToList();
        if (view.Head is { } head && !view.Entries.Any(entry => Entries.SystemEntry.Is(entry) && entry.Id.Value > head.Id.Value))
        {
            var edits = view.Entries.Where(entry => Entries.SystemEntry.Is(entry)).Select(entry => ContextEdit.Omit(entry.Id)).ToImmutableArray();
            var baseline = SystemEntry(Patch(wanted.Select(pair => KeyValuePair.Create(pair.Key, (string?)pair.Value))),
                new ToolChanges([], [.. tools.Select(PiAiTranscript.ToToolDeclaration)]), timestamp);
            return [edits.IsEmpty ? baseline : baseline with { Edits = edits }];
        }
        var sections = PlanSections(ReplaySections(view.Messages), wanted);
        var changes = PlanTools(PiAiTranscript.GetCurrentTools(view.Messages), tools);
        if (changes.Removed.Count == 0 && changes.Added.Count == 0) return [.. sections.Select(patch => SystemEntry(patch, null, timestamp))];
        if (sections.Count == 0) return [SystemEntry(null, changes, timestamp)];
        return [.. sections.Select((patch, index) => SystemEntry(patch, index == sections.Count - 1 ? changes : null, timestamp))];
    }

    private sealed record ToolChanges(List<JsonObjectValue> Removed, List<JsonObjectValue> Added);

    /// <summary>
    /// Tool changes from <paramref name="offered"/> to <paramref name="desired"/> (upstream <c>planTools</c>). A changed declaration is
    /// removed and re-added. Replay keeps retained tools in place and appends additions; when that would not yield the desired order, every
    /// offered tool is removed and every desired tool re-added in order.
    /// </summary>
    private static ToolChanges PlanTools(List<JsonObjectValue> offered, IReadOnlyList<JsonObjectValue> desired)
    {
        var wanted = new Dictionary<string, JsonObjectValue>(StringComparer.Ordinal);
        foreach (var tool in desired) wanted[Name(tool)] = tool;
        var kept = offered.Where(tool => wanted.TryGetValue(Name(tool), out var next) && PiAiTranscript.DeclarationsEqual(tool, next)).ToList();
        var keptNames = kept.Select(Name).ToHashSet(StringComparer.Ordinal);
        var added = desired.Where(tool => !keptNames.Contains(Name(tool))).ToList();
        var replayed = kept.Concat(added).ToList();
        if (replayed.Where((tool, index) => index >= desired.Count || Name(tool) != Name(desired[index])).Any())
            return new([.. offered.Select(Reference)], [.. desired.Select(PiAiTranscript.ToToolDeclaration)]);
        return new([.. offered.Where(tool => !keptNames.Contains(Name(tool))).Select(Reference)], [.. added.Select(PiAiTranscript.ToToolDeclaration)]);
    }

    /// <summary>Section patches: none, the minimal patch, or a remove-all/re-add-all pair when the order would differ (upstream
    /// <c>planSections</c>).</summary>
    private static List<JsonObjectValue> PlanSections(OrderedDictionary<string, string> shown, List<KeyValuePair<string, string>> desired)
    {
        var desiredMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in desired) desiredMap[key] = value;
        var patchedOrder = shown.Keys.Where(desiredMap.ContainsKey).Concat(desired.Select(pair => pair.Key).Where(key => !shown.ContainsKey(key))).ToList();
        var desiredOrder = desired.Select(pair => pair.Key).ToList();
        if (patchedOrder.Where((key, index) => index >= desiredOrder.Count || key != desiredOrder[index]).Any())
            return [Patch(shown.Keys.Select(key => KeyValuePair.Create(key, (string?)null))),
                Patch(desired.Select(pair => KeyValuePair.Create(pair.Key, (string?)pair.Value)))];
        var patch = new List<KeyValuePair<string, string?>>();
        foreach (var (key, value) in shown)
        {
            var next = desiredMap.GetValueOrDefault(key);
            if (next != value) patch.Add(KeyValuePair.Create(key, next));
        }
        foreach (var (key, value) in desired) if (!shown.ContainsKey(key)) patch.Add(KeyValuePair.Create(key, (string?)value));
        return patch.Count == 0 ? [] : [Patch(patch)];
    }

    /// <summary><c>Object.fromEntries(entries)</c> of a section patch: a null text removes the section.</summary>
    private static JsonObjectValue Patch(IEnumerable<KeyValuePair<string, string?>> entries) =>
        JsonValue.Object(entries.Select(pair => KeyValuePair.Create(pair.Key, pair.Value is null ? (JsonValue)JsonValue.Null : pair.Value)));

    private static TypedEntryDraft SystemEntry(JsonObjectValue? sections, ToolChanges? tools, double timestamp)
    {
        var message = new List<KeyValuePair<string, JsonValue>>
        {
            KeyValuePair.Create("role", (JsonValue)"system"),
            KeyValuePair.Create("content", (JsonValue)""),
        };
        if (sections is not null) message.Add(KeyValuePair.Create("sections", (JsonValue)sections));
        if (tools is { Removed.Count: > 0 }) message.Add(KeyValuePair.Create("toolsRemoved", (JsonValue)JsonValue.Array(tools.Removed)));
        if (tools is { Added.Count: > 0 }) message.Add(KeyValuePair.Create("toolsAdded", (JsonValue)JsonValue.Array(tools.Added)));
        message.Add(KeyValuePair.Create("timestamp", (JsonValue)timestamp));
        return new TypedEntryDraft { Model = JsonValue.Array(JsonValue.Object(message)) };
    }

    private static JsonObjectValue Reference(JsonObjectValue tool) => JsonValue.Object([KeyValuePair.Create("name", tool.TryGetValue("name", out var name) ? name : JsonValue.Null)]);

    private static string Name(JsonObjectValue tool) => tool.TryGetValue("name", out var name) && name is JsonStringValue text ? text.Value : "undefined";

    private static bool IsRole(JsonObjectValue message, string role) => message.TryGetValue("role", out var value) && value is JsonStringValue text && text.Value == role;
}
