// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/interactive-mode.ts (the AgentSession calls the
// mode makes: prompt, steer, followUp, abort, queues, model and thinking selection, compaction, bash), here as RPC commands.
using System.Text.Json.Nodes;

namespace PiSharp.Cli.Interactive.Mode;

internal sealed record ScopedModel(JsonObject Model, string? ThinkingLevel);

/// <summary>The session as the interactive mode sees it: the last <c>get_state</c> plus the events since.</summary>
internal sealed class SessionState(string cwd) : PiSharp.Cli.Interactive.Mode.Components.IFooterSession
{
    /// <summary>modelRuntime.isUsingSubscription(provider) (OAuth subscription credentials).</summary>
    public Func<string, bool> UsingSubscription { get; set; } = _ => false;
    JsonObject? PiSharp.Cli.Interactive.Mode.Components.IFooterSession.StateModel => Model;
    string? PiSharp.Cli.Interactive.Mode.Components.IFooterSession.StateThinkingLevel => ThinkingLevel;
    PiSharp.Cli.Interactive.Mode.Components.FooterRoutedModel? PiSharp.Cli.Interactive.Mode.Components.IFooterSession.RoutedModel => null;
    public PiSharp.Cli.Interactive.Mode.Components.FooterContextUsage? GetContextUsage()
    {
        if (Stats?["contextUsage"] is not JsonObject usage || usage["contextWindow"] is not JsonValue window || !window.TryGetValue<double>(out var contextWindow)) return null;
        double? tokens = usage["tokens"] is JsonValue t && t.TryGetValue<double>(out var count) ? count : null;
        double? percent = usage["percent"] is JsonValue p && p.TryGetValue<double>(out var value) ? value : null;
        return new(tokens, contextWindow, percent);
    }
    public bool IsUsingSubscription(string provider) => UsingSubscription(provider);
    public int GetEntryCount() => Entries.Count;
    public string GetSessionId() => SessionId ?? "";
    public string? GetLeafId() => LeafId;
    public IReadOnlyList<JsonObject> GetEntries() => Entries;
    public string GetCwd() => Cwd;
    public string? GetSessionName() => SessionName;

    public string Cwd { get; set; } = cwd;
    public JsonObject? Model { get; set; }
    public string? ThinkingLevel { get; set; }
    public bool IsStreaming { get; set; }
    public bool IsCompacting { get; set; }
    public bool IsRetrying { get; set; }
    public bool AutoCompactionEnabled { get; set; } = true;
    public bool AutoRetryEnabled { get; set; } = true;
    public string SteeringMode { get; set; } = "one-at-a-time";
    public string FollowUpMode { get; set; } = "one-at-a-time";
    public string? SessionFile { get; set; }
    public string? SessionId { get; set; }
    public string? SessionName { get; set; }
    public long Generation { get; set; } = 1;
    public int MessageCount { get; set; }
    public int RetryAttempt { get; set; }
    public bool IsBashRunning { get; set; }
    public IReadOnlyList<string> SteeringMessages { get; set; } = [];
    public IReadOnlyList<string> FollowUpMessages { get; set; } = [];
    public List<JsonObject> Entries { get; set; } = [];
    public string? LeafId { get; set; }
    public List<JsonObject> AvailableModels { get; set; } = [];
    public List<ScopedModel> ScopedModels { get; set; } = [];
    public List<string> AvailableThinkingLevels { get; set; } = [];
    public JsonArray Commands { get; set; } = [];
    public JsonObject? Stats { get; set; }
    public int PendingMessageCount => SteeringMessages.Count + FollowUpMessages.Count;
    public bool IsIdle => !IsStreaming && !IsCompacting;

    public void ApplyState(JsonObject data)
    {
        Model = data["model"] as JsonObject ?? Model;
        ThinkingLevel = SessionEntries.Str(data["thinkingLevel"]) ?? ThinkingLevel;
        IsStreaming = Bool(data["isStreaming"]) ?? IsStreaming;
        IsCompacting = Bool(data["isCompacting"]) ?? IsCompacting;
        IsRetrying = Bool(data["isRetrying"]) ?? IsRetrying;
        AutoRetryEnabled = Bool(data["autoRetryEnabled"]) ?? AutoRetryEnabled;
        AutoCompactionEnabled = Bool(data["autoCompactionEnabled"]) ?? AutoCompactionEnabled;
        SteeringMode = SessionEntries.Str(data["steeringMode"]) ?? SteeringMode;
        FollowUpMode = SessionEntries.Str(data["followUpMode"]) ?? FollowUpMode;
        SessionFile = SessionEntries.Str(data["sessionFile"]) ?? SessionFile;
        SessionId = SessionEntries.Str(data["sessionId"]) ?? SessionId;
        SessionName = SessionEntries.Str(data["sessionName"]);
        if (data["pisharpGeneration"] is JsonValue generation && generation.TryGetValue<long>(out var value)) Generation = value;
        if (data["messageCount"] is JsonValue count && count.TryGetValue<int>(out var messages)) MessageCount = messages;
    }

    public static bool? Bool(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;
}

internal sealed partial class InteractiveMode
{
    private readonly SessionState state;

    /// <summary>The session state (footer, selectors and tests read it).</summary>
    internal SessionState Session => state;

    private async Task RefreshStateAsync()
    {
        if (await rpc.RequestAsync(new JsonObject { ["type"] = "get_state" }) is JsonObject data) state.ApplyState(data);
    }

    private async Task RefreshEntriesAsync()
    {
        if (await rpc.RequestAsync(new JsonObject { ["type"] = "get_entries" }) is JsonObject data)
        {
            state.Entries = data["entries"] is JsonArray entries ? [.. entries.OfType<JsonObject>().Select(entry => (JsonObject)entry.DeepClone())] : [];
            state.LeafId = SessionEntries.Str(data["leafId"]);
        }
    }

    private async Task RefreshAvailableModelsAsync()
    {
        try
        {
            if (await rpc.RequestAsync(new JsonObject { ["type"] = "get_available_models" }) is JsonObject data && data["models"] is JsonArray models)
                state.AvailableModels = [.. models.OfType<JsonObject>().Select(model => (JsonObject)model.DeepClone())];
        }
        catch (RpcCommandFailedException) { }
    }

    private async Task RefreshThinkingLevelsAsync()
    {
        try
        {
            if (await rpc.RequestAsync(new JsonObject { ["type"] = "get_available_thinking_levels" }) is JsonObject data && data["levels"] is JsonArray levels)
                state.AvailableThinkingLevels = [.. levels.Select(level => SessionEntries.Str(level)).OfType<string>()];
        }
        catch (RpcCommandFailedException) { }
    }

    private async Task RefreshCommandsAsync()
    {
        try
        {
            if (await rpc.RequestAsync(new JsonObject { ["type"] = "get_commands" }) is JsonObject data && data["commands"] is JsonArray commands)
                state.Commands = (JsonArray)commands.DeepClone();
        }
        catch (RpcCommandFailedException) { }
    }

    private async Task RefreshStatsAsync()
    {
        try
        {
            if (await rpc.RequestAsync(new JsonObject { ["type"] = "get_session_stats" }) is JsonObject data) state.Stats = data;
        }
        catch (RpcCommandFailedException) { }
    }

    /// <summary>Refresh everything a new or switched session changes.</summary>
    private async Task RefreshSessionAsync()
    {
        await RefreshStateAsync();
        await RefreshEntriesAsync();
        await RefreshThinkingLevelsAsync();
        await RefreshStatsAsync();
    }

    private Task PromptAsync(string text, JsonArray? images = null, string? streamingBehavior = null)
    {
        var command = new JsonObject { ["type"] = "prompt", ["message"] = text };
        if (images is { Count: > 0 }) command["images"] = images.DeepClone();
        if (streamingBehavior is not null) command["streamingBehavior"] = streamingBehavior;
        return rpc.RequestAsync(command);
    }

    private Task SteerAsync(string text) => rpc.RequestAsync(new JsonObject { ["type"] = "steer", ["message"] = text });
    private Task FollowUpAsync(string text) => rpc.RequestAsync(new JsonObject { ["type"] = "follow_up", ["message"] = text });
    private Task AbortAsync() => rpc.RequestAsync(new JsonObject { ["type"] = "abort" });
    private void AbortRetry() => rpc.Post(new JsonObject { ["type"] = "abort_retry" });
    private void AbortBash() => rpc.Post(new JsonObject { ["type"] = "abort_bash" });
    private void AbortCompaction() => rpc.Post(new JsonObject { ["type"] = "abort" });
    private void AbortBranchSummary() => rpc.Post(new JsonObject { ["type"] = "abort" });

    /// <summary>session.clearQueue(): the removed messages; the local view clears immediately.</summary>
    private async Task<(List<string> Steering, List<string> FollowUp)> ClearSessionQueueAsync()
    {
        var steering = state.SteeringMessages.ToList(); var followUp = state.FollowUpMessages.ToList();
        state.SteeringMessages = []; state.FollowUpMessages = [];
        try
        {
            if (await rpc.RequestAsync(new JsonObject { ["type"] = "clear_queue" }) is JsonObject data)
            {
                steering = data["steering"] is JsonArray s ? [.. s.Select(SessionEntries.Str).OfType<string>()] : steering;
                followUp = data["followUp"] is JsonArray f ? [.. f.Select(SessionEntries.Str).OfType<string>()] : followUp;
            }
        }
        catch (RpcCommandFailedException) { }
        return (steering, followUp);
    }

    private bool IsExtensionCommand(string text)
    {
        if (!text.StartsWith('/')) return false;
        var space = text.IndexOf(' ');
        var name = space == -1 ? text[1..] : text[1..space];
        return state.Commands.OfType<JsonObject>().Any(command => SessionEntries.Str(command["source"]) == "extension" && SessionEntries.Str(command["name"]) == name);
    }

    private JsonObject? FindModel(string provider, string id) =>
        state.AvailableModels.FirstOrDefault(model => SessionEntries.Str(model["provider"]) == provider && SessionEntries.Str(model["id"]) == id);

    private async Task SetModelAsync(JsonObject model, bool persist)
    {
        var provider = SessionEntries.Str(model["provider"])!; var id = SessionEntries.Str(model["id"])!;
        await rpc.RequestAsync(new JsonObject { ["type"] = "set_model", ["provider"] = provider, ["modelId"] = id });
        if (persist)
        {
            settings.SetDefaultModelAndProvider(provider, id);
        }
        await RefreshStateAsync();
        await RefreshThinkingLevelsAsync();
        // A per-model thinking override applies when the model changes (settings modelThinkingLevels).
        if (settings.ModelThinkingLevel(provider, id) is { } level && level != state.ThinkingLevel)
        {
            try { await SetThinkingLevelAsync(level, persist: false); } catch (RpcCommandFailedException) { }
        }
    }

    private async Task SetThinkingLevelAsync(string level, bool persist)
    {
        await rpc.RequestAsync(new JsonObject { ["type"] = "set_thinking_level", ["level"] = level });
        if (persist) settings.SetDefaultThinkingLevel(level);
        await RefreshStateAsync();
    }

    private async Task<string?> CycleThinkingLevelRpcAsync()
    {
        var data = await rpc.RequestAsync(new JsonObject { ["type"] = "cycle_thinking_level" });
        await RefreshStateAsync();
        return data is JsonObject result ? SessionEntries.Str(result["level"]) : null;
    }

    private async Task<(JsonObject Model, string ThinkingLevel)?> CycleModelRpcAsync(string direction)
    {
        if (state.ScopedModels.Count > 0)
        {
            if (state.ScopedModels.Count <= 1) return null;
            var index = state.ScopedModels.FindIndex(scoped => SameModel(scoped.Model, state.Model));
            var step = direction == "backward" ? -1 : 1;
            var next = state.ScopedModels[((index < 0 ? 0 : index) + step + state.ScopedModels.Count) % state.ScopedModels.Count];
            await SetModelAsync(next.Model, persist: false);
            if (next.ThinkingLevel is { } level) await SetThinkingLevelAsync(level, persist: false);
            return (state.Model ?? next.Model, state.ThinkingLevel ?? "off");
        }
        if (state.AvailableModels.Count <= 1)
        {
            var data = await rpc.RequestAsync(new JsonObject { ["type"] = "cycle_model" });
            if (data is not JsonObject result || result["model"] is not JsonObject model) return null;
            await RefreshStateAsync();
            return (model, SessionEntries.Str(result["thinkingLevel"]) ?? "off");
        }
        var currentIndex = state.AvailableModels.FindIndex(model => SameModel(model, state.Model));
        var delta = direction == "backward" ? -1 : 1;
        var target = state.AvailableModels[((currentIndex < 0 ? 0 : currentIndex) + delta + state.AvailableModels.Count) % state.AvailableModels.Count];
        await SetModelAsync(target, persist: false);
        return (state.Model ?? target, state.ThinkingLevel ?? "off");
    }

    internal static bool SameModel(JsonObject? left, JsonObject? right) => left is not null && right is not null &&
        SessionEntries.Str(left["provider"]) == SessionEntries.Str(right["provider"]) && SessionEntries.Str(left["id"]) == SessionEntries.Str(right["id"]);

    private async Task CompactAsync(string? customInstructions)
    {
        var command = new JsonObject { ["type"] = "compact" };
        if (customInstructions is { Length: > 0 }) command["customInstructions"] = customInstructions;
        await rpc.RequestAsync(command);
    }

    private async Task SetAutoCompactionEnabledAsync(bool enabled)
    {
        await rpc.RequestAsync(new JsonObject { ["type"] = "set_auto_compaction", ["enabled"] = enabled });
        state.AutoCompactionEnabled = enabled;
        settings.SetCompactionEnabled(enabled);
    }

    private async Task SetSessionNameAsync(string name)
    {
        await rpc.RequestAsync(new JsonObject { ["type"] = "set_session_name", ["name"] = name });
        await RefreshStateAsync();
    }

    private async Task<string?> GetLastAssistantTextAsync() =>
        await rpc.RequestAsync(new JsonObject { ["type"] = "get_last_assistant_text" }) is JsonObject data ? SessionEntries.Str(data["text"]) : null;

    private async Task<List<(string EntryId, string Text)>> GetUserMessagesForForkingAsync()
    {
        var data = await rpc.RequestAsync(new JsonObject { ["type"] = "get_fork_messages" });
        return data is JsonObject result && result["messages"] is JsonArray messages
            ? [.. messages.OfType<JsonObject>().Select(message => (SessionEntries.Str(message["entryId"]) ?? "", SessionEntries.Str(message["text"]) ?? ""))]
            : [];
    }
}
