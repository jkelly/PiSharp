// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/harness/agent.ts. The stored agent (upstream AgentState) is the
// pi.agent document's JSON object, written with upstream's key order: fields appear in the order they were first set, as a JavaScript
// object's do. Composition uses OrderedDictionary, whose replacement keeps a key's position as a JavaScript Map's does.
using System.Collections.Immutable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;

namespace PiSharp.Durable;

/// <summary>The built-in agent document and agent resolution (upstream <c>agent.ts</c>).</summary>
public static class Agents
{
    /// <summary>Upstream <c>DEFAULT_RETRY_POLICY</c>.</summary>
    public static ConversationRetryPolicy DefaultRetryPolicy { get; } = new(true, 3, 2000, 60000);

    /// <summary>Upstream <c>DEFAULT_COMPACTION_POLICY</c>.</summary>
    public static CompactionPolicy DefaultCompactionPolicy { get; } = new(true, 16384, 20000, 32768);

    /// <summary>Upstream <c>DEFAULT_PROGRESS_POLICY</c>.</summary>
    public static ProgressPolicy DefaultProgressPolicy { get; } = new(100, 100);

    /// <summary>The reserved section key of the agent's instructions (upstream <c>INSTRUCTIONS_KEY</c>).</summary>
    public const string InstructionsKey = "instructions";

    /// <summary>Default <c>settings.ContextRetentionMs</c>: ten minutes.</summary>
    private const double DefaultContextRetentionMs = 600_000;

    /// <summary>Built-in agent document; rewindable so forks start from the agent at their fork entry (upstream <c>AgentDoc</c>).</summary>
    public static RewindableConversationDocToken AgentDoc { get; } = Documents.DefineRewindableConversationDoc("pi.agent", 1, DocumentFork.AsOf,
        () => JsonObjectValue.Empty, checkpointWhen: (_, _, _) => true);

    /// <summary>Resolves the host settings: every field over its built-in default, object fields merged (upstream
    /// <c>resolveSettings</c>).</summary>
    public static Settings ResolveSettings(HarnessSettings? settings)
    {
        var extensions = settings?.Extensions;
        var retry = settings?.Retry;
        var compaction = settings?.Compaction;
        var progress = settings?.Progress;
        return new Settings
        {
            Extensions = extensions,
            Stream = settings?.Stream ?? new ConversationStreamOptions(),
            Retry = new ConversationRetryPolicy(retry?.Enabled ?? DefaultRetryPolicy.Enabled, retry?.MaxRetries ?? DefaultRetryPolicy.MaxRetries,
                retry?.BaseDelayMs ?? DefaultRetryPolicy.BaseDelayMs, retry?.MaxAgentDelayMs ?? DefaultRetryPolicy.MaxAgentDelayMs),
            Compaction = new CompactionPolicy(compaction?.Enabled ?? DefaultCompactionPolicy.Enabled,
                compaction?.ReserveTokens ?? DefaultCompactionPolicy.ReserveTokens, compaction?.KeepRecentTokens ?? DefaultCompactionPolicy.KeepRecentTokens,
                compaction?.BackgroundTokens ?? DefaultCompactionPolicy.BackgroundTokens),
            // Field by field, so an unset interval keeps its default.
            Progress = new ProgressPolicy(progress?.PartialIntervalMs ?? DefaultProgressPolicy.PartialIntervalMs,
                progress?.OutputIntervalMs ?? DefaultProgressPolicy.OutputIntervalMs),
            ToolExecution = settings?.ToolExecution ?? ToolExecutionMode.Parallel,
            SteeringMode = settings?.SteeringMode ?? QueueMode.OneAtATime,
            FollowUpMode = settings?.FollowUpMode ?? QueueMode.OneAtATime,
            ContextRetentionMs = settings?.ContextRetentionMs ?? DefaultContextRetentionMs,
            DocumentRetentionMs = Session.DocumentRetention(settings?.DocumentRetentionMs),
        };
    }

    /// <summary>Applies one change to <c>pi.agent</c>: a given field replaces the stored one, a clear removes it, an absent field changes
    /// nothing (upstream <c>configure</c>).</summary>
    public static async Task ConfigureAsync(ITx tx, ConversationId conversationId, AgentChange change)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(change);
        var state = await tx.DocAsync(AgentDoc, conversationId).ConfigureAwait(false);
        ApplyChange(state, change);
    }

    /// <summary>
    /// <c>addTools</c> of a tool round (upstream <c>addTools</c>): an array gets each name it lacks appended, <c>{remove}</c> loses the names,
    /// and unset tools already offer every tool, so nothing is written.
    /// </summary>
    public static async Task AddToolsAsync(ITx tx, ConversationId conversationId, IReadOnlyList<string> added)
    {
        var state = await tx.DocAsync(AgentDoc, conversationId).ConfigureAwait(false);
        if (!state.ContainsKey("tools")) return;
        var tools = state.Get("tools");
        if (tools is JsonArrayValue)
        {
            var list = state.GetArray("tools");
            foreach (var name in added)
                if (!Enumerable.Range(0, list.Count).Any(index => list.Get(index) is JsonStringValue item && item.Value == name)) list.Add(name);
        }
        else if (tools is JsonObjectValue filter && filter.TryGetValue("remove", out var removeValue) && removeValue is JsonArrayValue remove)
        {
            if (remove.Any(name => name is JsonStringValue text && added.Contains(text.Value)))
            {
                var kept = remove.Where(name => name is not JsonStringValue text || !added.Contains(text.Value));
                state.Set("tools", JsonValue.Object([KeyValuePair.Create("remove", (JsonValue)JsonValue.Array(kept))]));
            }
        }
    }

    private static void ApplyChange(DraftObject state, AgentChange change)
    {
        void Set<T>(string key, Patch<T>? patch, Func<T, JsonValue> write) where T : notnull
        {
            if (patch is not { } given) return;
            if (given.IsClear) state.Delete(key);
            else state.Set(key, write(given.Value));
        }
        Set("model", change.Model, model => model.ToJson());
        Set("thinkingLevel", change.ThinkingLevel, level => level);
        Set("extensions", change.Extensions, selection => selection.Exactly is { } exactly
            ? Names(exactly.Select(extension => extension.Name))
            : JsonValue.Object(Fields(("add", selection.Add?.Select(extension => extension.Name)), ("remove", selection.Remove?.Select(extension => extension.Name)))));
        Set("tools", change.Tools, selection => selection.Exactly is { } exactly
            ? Names(exactly.Select(tool => tool.Name))
            : JsonValue.Object(Fields(("remove", selection.Remove!.Value.Select(tool => tool.Name)))));
        Set("instructions", change.Instructions, text => text);
        Set("cwd", change.Cwd, text => text);
    }

    private static JsonArrayValue Names(IEnumerable<string> names) => JsonValue.Array(names.Select(name => (JsonValue)name));

    private static IEnumerable<KeyValuePair<string, JsonValue>> Fields(params (string Key, IEnumerable<string>? Names)[] fields) =>
        fields.Where(field => field.Names is not null).Select(field => KeyValuePair.Create(field.Key, (JsonValue)Names(field.Names!)));

    /// <summary>
    /// Built-in part of every Harness commit that creates or forks a conversation, for <c>pi.agent</c> (upstream <c>createAgent</c>): a fork
    /// keeps its <c>asOf</c> copy; a new task-owned conversation copies the stored agent of its owner task's conversation; a new ownerless one
    /// starts empty.
    /// </summary>
    public static async Task CreateAgentAsync(ITx tx, ConversationRecord conversation)
    {
        if (conversation.Parent is not null) return;
        var agent = await tx.DocAsync(AgentDoc, conversation.Id).ConfigureAwait(false);
        if (conversation.Owner is null) return;
        var owner = await tx.DocAsync(AgentDoc, conversation.Owner.ConversationId).ConfigureAwait(false);
        foreach (var (key, value) in (JsonObjectValue)owner.ToValue()) agent.Set(key, value);
    }

    /// <summary>Handlers of the selected extensions' hooks for a task name, in extension order (upstream <c>agentHooks</c>).</summary>
    public static IReadOnlyList<object> AgentHooks(Agent agent, string taskName)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var handlers = new List<object>();
        foreach (var extension in agent.Extensions)
            foreach (var hook in extension.Hooks ?? [])
                if (hook.Task == taskName) handlers.Add(hook.Handlers);
        return handlers;
    }

    /// <summary>
    /// Resolves an agent from its stored state (null: every field unset), a registry snapshot and resolved settings (upstream
    /// <c>resolveAgent</c>). A wrapper that throws or renames drops its target and is reported; a wrapper without a target does nothing.
    /// </summary>
    public static Agent ResolveAgent(JsonObjectValue? state, IRegistrySnapshot snapshot, Settings settings, Action<Exception> report)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(report);
        var extensions = SelectExtensions(Field(state, "extensions"), snapshot, settings);

        var composed = new OrderedDictionary<string, ToolRegistration>(StringComparer.Ordinal);
        foreach (var extension in extensions) foreach (var tool in extension.Tools ?? []) composed[tool.Name] = tool;
        var sections = new OrderedDictionary<string, PromptSection>(StringComparer.Ordinal);
        foreach (var extension in extensions) foreach (var section in extension.Sections ?? []) sections[section.Key] = section;
        foreach (var extension in extensions)
        {
            foreach (var wrap in extension.Wraps ?? [])
            {
                if (wrap is ToolWrap tool) ApplyWrap(composed, tool.Tool, tool.Apply, item => item.Name, report);
                else if (wrap is SectionWrap section) ApplyWrap(sections, section.Section, section.Apply, item => item.Key, report);
            }
        }

        var filter = Field(state, "tools");
        List<ToolRegistration> tools;
        if (filter is null) tools = [.. composed.Values];
        else if (filter is JsonArrayValue names)
        {
            tools = [];
            foreach (var name in Strings(names).Distinct(StringComparer.Ordinal))
                if (composed.TryGetValue(name, out var tool)) tools.Add(tool);
        }
        else
        {
            var removed = Strings(Field(filter as JsonObjectValue, "remove") as JsonArrayValue).ToHashSet(StringComparer.Ordinal);
            tools = [.. composed.Values.Where(tool => !removed.Contains(tool.Name))];
        }

        var instructions = (Field(state, "instructions") as JsonStringValue)?.Value;
        var agentSections = sections.Values.ToList();
        if (instructions is not null)
            agentSections.Add(new PromptSection { Key = InstructionsKey, Render = (_, _) => ValueTask.FromResult<string?>(instructions) });

        return new Agent
        {
            Model = ModelRef.FromJson(Field(state, "model")),
            ThinkingLevel = (Field(state, "thinkingLevel") as JsonStringValue)?.Value ?? "off",
            Extensions = [.. extensions],
            Tools = [.. tools],
            Sections = [.. agentSections],
            Instructions = instructions,
            Cwd = (Field(state, "cwd") as JsonStringValue)?.Value,
        };
    }

    /// <summary>Selected installed extensions: the stored array, or the default selection edited by <c>{add, remove}</c>.</summary>
    private static List<Extension> SelectExtensions(JsonValue? stored, IRegistrySnapshot snapshot, Settings settings)
    {
        List<string> selected;
        if (stored is JsonArrayValue array) selected = [.. Strings(array)];
        else
        {
            var defaults = settings.Extensions?.Select(extension => extension.Name) ?? snapshot.Installed().Select(extension => extension.Name);
            var edit = stored as JsonObjectValue;
            var removed = Strings(Field(edit, "remove") as JsonArrayValue).ToHashSet(StringComparer.Ordinal);
            selected = [.. defaults.Concat(Strings(Field(edit, "add") as JsonArrayValue)).Where(name => !removed.Contains(name))];
        }
        var extensions = new List<Extension>();
        foreach (var name in selected.Distinct(StringComparer.Ordinal))
            if (snapshot.GetExtension(name) is { } extension) extensions.Add(extension);
        return extensions;
    }

    private static void ApplyWrap<T>(OrderedDictionary<string, T> items, string target, Func<T, T> wrap, Func<T, string> nameOf,
        Action<Exception> report)
    {
        if (!items.TryGetValue(target, out var item)) return;
        try
        {
            var wrapped = wrap(item);
            if (nameOf(wrapped) != target) throw new InvalidOperationException($"Wrapper renamed {target} to {nameOf(wrapped)}");
            items[target] = wrapped;
        }
        catch (Exception error)
        {
            items.Remove(target);
            report(error);
        }
    }

    private static JsonValue? Field(JsonObjectValue? value, string key) => value is not null && value.TryGetValue(key, out var field) ? field : null;

    private static IEnumerable<string> Strings(JsonArrayValue? array) =>
        array is null ? [] : array.OfType<JsonStringValue>().Select(item => item.Value);
}
