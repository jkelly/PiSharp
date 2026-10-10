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
    PiSharp.Cli.Interactive.Mode.Components.FooterRoutedModel? PiSharp.Cli.Interactive.Mode.Components.IFooterSession.RoutedModel => RoutedModel;
    object? PiSharp.Cli.Interactive.Mode.Components.IFooterSession.ContextUsageSource => Stats;

    private (List<JsonObject> Entries, int Count, string? LeafId, JsonObject? Model, List<JsonObject> Models, PiSharp.Cli.Interactive.Mode.Components.FooterRoutedModel? Routed)? routedCache;

    /// <summary>
    /// Source AgentSession.routedModel: for a virtual model (api <c>pi-virtual</c>), the physical model of the latest successful
    /// response in the session context (findLatestResponse over the agent messages) and its thinking level. Cached on the inputs, so
    /// the footer's stats cache sees the same model object between changes.
    /// </summary>
    public PiSharp.Cli.Interactive.Mode.Components.FooterRoutedModel? RoutedModel
    {
        get
        {
            if (routedCache is { } cached && ReferenceEquals(cached.Entries, Entries) && cached.Count == Entries.Count && cached.LeafId == LeafId &&
                ReferenceEquals(cached.Model, Model) && ReferenceEquals(cached.Models, AvailableModels)) return cached.Routed;
            PiSharp.Cli.Interactive.Mode.Components.FooterRoutedModel? routed = null;
            if (SessionEntries.Str(Model?["api"]) == "pi-virtual")
            {
                var latest = SessionEntries.BuildContextEntries(Entries, LeafId).Select(entry => SessionEntries.Type(entry) == "message" ? entry["message"] as JsonObject : null)
                    .LastOrDefault(message => SessionEntries.Str(message?["role"]) == "assistant" && SessionEntries.Str(message!["stopReason"]) is not ("error" or "aborted"));
                var physical = latest is null ? null : AvailableModels.FirstOrDefault(model => SessionEntries.Str(model["api"]) != "pi-virtual" &&
                    SessionEntries.Str(model["provider"]) == SessionEntries.Str(latest["provider"]) && SessionEntries.Str(model["id"]) == SessionEntries.Str(latest["model"]));
                if (physical is not null) routed = new(physical, SessionEntries.Str(latest!["thinkingLevel"]));
            }
            routedCache = (Entries, Entries.Count, LeafId, Model, AvailableModels, routed);
            return routed;
        }
    }

    public PiSharp.Cli.Interactive.Mode.Components.FooterContextUsage? GetContextUsage()
    {
        if (Stats?["contextUsage"] is not JsonObject usage || usage["contextWindow"] is not JsonValue window || !window.TryGetValue<double>(out var contextWindow)) return null;
        double? tokens = usage["tokens"] is JsonValue t && t.TryGetValue<double>(out var count) ? count : null;
        double? percent = usage["percent"] is JsonValue p && p.TryGetValue<double>(out var value) ? value : null;
        // _limitsModel(): a virtual model's context window is the routed physical model's (the host reports the selected model's).
        if (RoutedModel?.Model["contextWindow"] is JsonValue routedWindow && routedWindow.TryGetValue<double>(out var physicalWindow) && physicalWindow > 0)
            return new(tokens, physicalWindow, tokens is { } used ? used / physicalWindow * 100 : null);
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

    private readonly List<(long Generation, TaskCompletionSource Done)> generationWaiters = [];
    /// <summary>The session generation the transcript shows (set once a switch finished rendering).</summary>
    private long renderedGeneration;

    /// <summary>Sends a session-replacing command (new, fork, clone, switch) and waits until the mode rebound to the new session,
    /// as runtimeHost.newSession()/fork()/switchSession() resolve after the rebind.</summary>
    private async Task<JsonObject?> ReplaceSessionAsync(JsonObject command)
    {
        var data = await rpc.RequestAsync(command) as JsonObject;
        if (data is null || B(data["cancelled"])) return data;
        if (data["generation"] is JsonValue value && value.TryGetValue<long>(out var generation) && generation > renderedGeneration)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            generationWaiters.Add((generation, done));
            await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        return data;
    }

    private void CompleteGenerationWaiters()
    {
        foreach (var waiter in generationWaiters.Where(waiter => waiter.Generation <= renderedGeneration).ToList())
        {
            generationWaiters.Remove(waiter);
            waiter.Done.TrySetResult();
        }
    }

    private async Task RefreshStateAsync()
    {
        if (await rpc.RequestAsync(new JsonObject { ["type"] = "get_state" }) is not JsonObject data) return;
        var model = state.Model;
        state.ApplyState(data);
        if (JsonNode.DeepEquals(model, state.Model)) return;
        // The footer's context window and usage follow the model (getContextUsage reads the limits model).
        RequestSessionSync();
        // _runAutoCompaction checks the current model's window; the host fixes it when automatic compaction is configured.
        if (model is not null && state.AutoCompactionEnabled) rpc.Post(new JsonObject { ["type"] = "set_auto_compaction", ["enabled"] = true });
    }

    /// <summary>Entries at the start of <see cref="SessionState.Entries"/> that came from get_entries, in log order; the rest arrived
    /// as entry_appended events after the last read.</summary>
    private int syncedEntryCount;
    /// <summary>Bumped by every full entries read, so an incremental read that started before it is dropped.</summary>
    private long entriesEpoch;
    private bool sessionSyncRunning, sessionSyncAgain;
    private TaskCompletionSource? sessionSyncWaiter;

    private async Task RefreshEntriesAsync()
    {
        var epoch = ++entriesEpoch;
        if (await rpc.RequestAsync(new JsonObject { ["type"] = "get_entries" }) is JsonObject data && epoch == entriesEpoch)
        {
            state.Entries = data["entries"] is JsonArray entries ? [.. entries.OfType<JsonObject>().Select(entry => (JsonObject)entry.DeepClone())] : [];
            state.LeafId = SessionEntries.Str(data["leafId"]);
            syncedEntryCount = state.Entries.Count;
            entriesEpoch++; // An incremental read that started before this one finished read against the replaced list.
        }
    }

    /// <summary>
    /// Upstream's footer, /session and selectors read sessionManager live: an entry counts from the moment the session appends it.
    /// The mode mirrors the session over RPC, where message entries are appended after their message_end (as in agent-session.ts)
    /// and have no event of their own, so the mirror reads the entries appended since its last read (get_entries since) and the
    /// stats (get_session_stats), then re-renders the footer. Requests coalesce: one read runs at a time, and a request made while it
    /// runs reads again after it.
    /// </summary>
    private void RequestSessionSync()
    {
        if (sessionSyncRunning) { sessionSyncAgain = true; return; }
        sessionSyncRunning = true;
        _ = RunSessionSyncAsync();
    }

    /// <summary>A read that starts after this call (for commands that show the session as it is now, like /session).</summary>
    private Task SyncSessionAsync()
    {
        var waiter = sessionSyncWaiter ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
        RequestSessionSync();
        return waiter.Task;
    }

    private async Task RunSessionSyncAsync()
    {
        TaskCompletionSource? waiter = null;
        try
        {
            do
            {
                sessionSyncAgain = false;
                waiter = sessionSyncWaiter; sessionSyncWaiter = null;
                if (!await SyncEntriesAsync()) sessionSyncAgain = true;
                await RefreshStatsAsync();
                footer.Invalidate();
                ui.RequestRender();
                waiter?.TrySetResult(); waiter = null;
            }
            while (sessionSyncAgain);
        }
        catch (RpcCommandFailedException) { }
        catch (InvalidOperationException) { } // The session closed.
        catch (TimeoutException) { }
        finally
        {
            sessionSyncRunning = false;
            waiter?.TrySetResult();
            if (sessionSyncWaiter is { } pending) { sessionSyncWaiter = null; pending.TrySetResult(); }
        }
    }

    /// <summary>Reads the entries appended since the last read; false when a full read or a session switch overtook it.</summary>
    private async Task<bool> SyncEntriesAsync()
    {
        var epoch = entriesEpoch; var generation = state.Generation;
        var since = syncedEntryCount > 0 ? SessionEntries.Id(state.Entries[syncedEntryCount - 1]) : null;
        var command = new JsonObject { ["type"] = "get_entries" };
        if (since is not null) command["since"] = since;
        JsonObject? data;
        try { data = await rpc.RequestAsync(command) as JsonObject; }
        catch (RpcCommandFailedException) when (since is not null)
        {
            // The last read entry is not in this session (it was replaced): read it whole.
            if (epoch == entriesEpoch && generation == state.Generation) await RefreshEntriesAsync();
            return true;
        }
        if (data is null) return true;
        if (epoch != entriesEpoch || generation != state.Generation) return false;
        var fresh = data["entries"] is JsonArray entries ? entries.OfType<JsonObject>().Select(entry => (JsonObject)entry.DeepClone()).ToList() : [];
        var ids = new HashSet<string>(fresh.Select(SessionEntries.Id).OfType<string>(), StringComparer.Ordinal);
        var known = new HashSet<string>(state.Entries.Skip(syncedEntryCount).Select(SessionEntries.Id).OfType<string>(), StringComparer.Ordinal);
        // Entries that arrived as events after this read's snapshot stay after it, in their order.
        var later = state.Entries.Skip(syncedEntryCount).Where(entry => SessionEntries.Id(entry) is not { } id || !ids.Contains(id)).ToList();
        state.MessageCount += fresh.Count(entry => SessionEntries.Id(entry) is { } id && !known.Contains(id) && SessionEntries.Type(entry) == "message" &&
            SessionEntries.Str(entry["message"]?["role"]) is "user" or "assistant" or "toolResult");
        state.Entries = [.. state.Entries.Take(syncedEntryCount), .. fresh, .. later];
        syncedEntryCount += fresh.Count;
        state.LeafId = later.Count > 0 ? SessionEntries.Id(later[^1]) ?? state.LeafId : SessionEntries.Str(data["leafId"]);
        return true;
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

    /// <summary>
    /// agent-session.ts: a session's autoCompactionEnabled is settingsManager.getCompactionEnabled() (compaction.enabled, default
    /// true), and the footer shows it (setAutoCompactEnabled in applyRuntimeSettings, on startup and every session rebind). The RPC
    /// host starts each session, including a replacement, without automatic compaction, so the mode applies the setting; where the
    /// host cannot (no summary transport), the footer shows the session's actual state.
    /// </summary>
    private async Task ApplyAutoCompactionSettingAsync()
    {
        var enabled = settings.CompactionEnabled;
        if (state.AutoCompactionEnabled != enabled)
        {
            try
            {
                await rpc.RequestAsync(new JsonObject { ["type"] = "set_auto_compaction", ["enabled"] = enabled });
                state.AutoCompactionEnabled = enabled;
            }
            catch (RpcCommandFailedException) { }
        }
        footer.SetAutoCompactEnabled(state.AutoCompactionEnabled);
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
