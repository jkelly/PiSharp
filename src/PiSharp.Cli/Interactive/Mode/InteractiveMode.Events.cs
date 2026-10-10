// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/interactive-mode.ts (subscribeToAgent,
// handleEvent, addMessageToChat, renderSessionItems, renderSessionEntries, renderInitialMessages, cache and compaction notices,
// showStatusIndicator/clearStatusIndicator, pending messages and the compaction queue).
using System.Globalization;
using System.Text.Json.Nodes;
using PiSharp.Cli.Interactive.Mode.Components;
using PiSharp.Cli.Interactive.Mode.Utilities;
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Interactive.Mode;

internal sealed partial class InteractiveMode
{
    private static string? S(JsonNode? node) => SessionEntries.Str(node);
    private static bool B(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
    private static double N(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out var number) ? number : 0;

    /// <summary>Events arrive on the RPC thread; they are handled in order on the UI loop.</summary>
    private Task eventChain = Task.CompletedTask;
    private readonly object eventGate = new();

    private void SubscribeToAgent()
    {
        rpc.Event -= OnRpcEvent;
        rpc.Event += OnRpcEvent;
    }

    private void OnRpcEvent(JsonObject e)
    {
        lock (eventGate)
            eventChain = eventChain.ContinueWith(_ => context.Loop.InvokeAsync(() => HandleEventSafelyAsync(e)), TaskScheduler.Default).Unwrap();
    }

    private async Task HandleEventSafelyAsync(JsonObject e)
    {
        try { await HandleEventAsync(e); }
        catch (Exception error) { ShowError(error.Message); }
    }

    private void UpdateStateFromEvent(JsonObject e)
    {
        switch (S(e["type"]))
        {
            case "agent_start": state.IsStreaming = true; break;
            case "agent_end": state.IsStreaming = false; break;
            case "agent_settled": state.IsStreaming = false; state.RetryAttempt = 0; break;
            case "compaction_start": state.IsCompacting = true; break;
            case "compaction_end": state.IsCompacting = false; break;
            case "auto_retry_start": state.IsRetrying = true; state.RetryAttempt = (int)N(e["attempt"]); break;
            case "auto_retry_end": state.IsRetrying = false; break;
            case "queue_update":
                state.SteeringMessages = e["steering"] is JsonArray steering ? [.. steering.Select(S).OfType<string>()] : [];
                state.FollowUpMessages = e["followUp"] is JsonArray followUp ? [.. followUp.Select(S).OfType<string>()] : [];
                break;
            case "session_info_changed": state.SessionName = S(e["name"]); break;
            case "thinking_level_changed": state.ThinkingLevel = S(e["level"]) ?? state.ThinkingLevel; break;
            case "entry_appended" when e["entry"] is JsonObject entry:
                if (SessionEntries.Type(entry) == "model_change") _ = RefreshStateAsync();
                // An entries read (SyncEntriesAsync) may already hold it.
                if (SessionEntries.Id(entry) is { } appendedId && state.Entries.FindLastIndex(known => SessionEntries.Id(known) == appendedId) >= 0) break;
                var copy = (JsonObject)entry.DeepClone();
                state.Entries.Add(copy);
                state.LeafId = SessionEntries.Id(copy) ?? state.LeafId;
                if (SessionEntries.Type(copy) == "message" && S(copy["message"]?["role"]) is "user" or "assistant" or "toolResult") state.MessageCount++;
                break;
        }
    }

    private async Task HandleEventAsync(JsonObject e)
    {
        var type = S(e["type"]);
        if (type == "session_switched")
        {
            await OnSessionSwitchedAsync(e);
            return;
        }
        if (type == "extension_ui_request") { HandleExtensionUiRequest(e); return; }
        if (type == "extension_error")
        {
            ShowExtensionError(S(e["extensionPath"]) ?? "", S(e["error"]) ?? "", null);
            return;
        }
        if (!isInitialized) await InitAsync();
        UpdateStateFromEvent(e);
        footer.Invalidate();
        // The session appends a message after its message_end; these events follow an append (see RequestSessionSync).
        if (type is "message_start" or "message_end" or "tool_execution_start" or "turn_end" or "agent_end" or "entry_appended" or "compaction_end")
            RequestSessionSync();
        programStatus.HandleEvent(e);

        switch (type)
        {
            case "agent_start":
                pendingTools.Clear();
                if (retryEscapeHandler is not null) { defaultEditor.OnEscape = retryEscapeHandler; retryEscapeHandler = null; }
                break;

            case "turn_start":
                if (settings.ShowTerminalProgress) ui.Terminal.SetProgress(true);
                if (workingVisible) { if (activeStatusIndicator?.Kind != StatusIndicatorKind.Working) ShowWorkingStatusIndicator(); }
                else ClearStatusIndicator();
                ui.RequestRender();
                break;

            case "queue_update":
                UpdatePendingMessagesDisplay();
                ui.RequestRender();
                break;

            case "entry_appended":
            {
                if (e["entry"] is not JsonObject entry) break;
                var entryId = SessionEntries.Id(entry) ?? "";
                if (entriesRenderedByBoundaryCompaction.Remove(entryId)) break;
                var entryType = SessionEntries.Type(entry);
                if (entryType == "custom")
                {
                    AddCustomEntryToChat(entry);
                    ui.RequestRender();
                }
                else if (entryType == "usage" && S(entry["kind"]) == "cache_warm")
                {
                    AddCacheWarmingUsage(entry);
                    ui.RequestRender();
                }
                else if (entryType == "custom_message" && B(entry["display"]))
                {
                    AddMessageToChat(SessionEntries.CreateCustomMessage(S(entry["customType"]) ?? "", entry["content"], true, entry["details"], S(entry["timestamp"])));
                    ui.RequestRender();
                }
                else if (entryType == "message" && S(entry["message"]?["role"]) == "assistant" && settings.ShowCacheMissNotices &&
                    S(entry["message"]?["stopReason"]) is not ("aborted" or "error") && context.DetectCacheMissForEntry?.Invoke(entryId) is { } entryMiss)
                {
                    AddCacheMissNotice(entryMiss);
                    ui.RequestRender();
                }
                else if (entryType == "compaction")
                {
                    var entries = SessionEntries.BuildContextEntries(state.Entries, state.LeafId);
                    if (entries.Count == 0 || SessionEntries.Id(entries[0]) != entryId) break;
                    chatContainer.Clear();
                    var branch = SessionEntries.BuildSessionPath(state.Entries, state.LeafId);
                    var compactionIndex = branch.FindIndex(item => SessionEntries.Id(item) == entryId);
                    var after = new HashSet<string>(branch.Skip(compactionIndex + 1).Select(item => SessionEntries.Id(item) ?? ""), StringComparer.Ordinal);
                    var retained = entries.Skip(1).ToList();
                    RenderSessionEntries(retained.Where(item => !after.Contains(SessionEntries.Id(item) ?? "")).ToList());
                    AddMessageToChat(SessionEntries.CreateCompactionSummaryMessage(S(entry["summary"]) ?? "", N(entry["tokensBefore"]), S(entry["timestamp"])));
                    if (entry["usage"] is JsonObject usage) AddCompactionCostNotice("compaction", usage);
                    RenderSessionEntries(retained.Where(item => after.Contains(SessionEntries.Id(item) ?? "")).ToList());
                    foreach (var id in after) entriesRenderedByBoundaryCompaction.Add(id);
                    footer.Invalidate();
                    ui.RequestRender();
                }
                break;
            }

            case "session_info_changed":
                UpdateTerminalTitle();
                footer.Invalidate();
                ui.RequestRender();
                break;

            case "thinking_level_changed":
                footer.Invalidate();
                UpdateEditorBorderColor();
                break;

            case "message_start":
            {
                if (e["message"] is not JsonObject message) break;
                var role = S(message["role"]);
                if (role == "custom")
                {
                    AddMessageToChat(message);
                    ui.RequestRender();
                }
                else if (role == "user")
                {
                    AddMessageToChat(message);
                    UpdatePendingMessagesDisplay();
                    ui.RequestRender();
                }
                else if (role == "assistant")
                {
                    streamingComponent = new AssistantMessageComponent(null, hideThinkingBlock, GetMarkdownThemeWithSettings(), hiddenThinkingLabel, outputPad, GetMarkdownTransformers());
                    streamingMessage = message;
                    chatContainer.AddChild(streamingComponent);
                    streamingComponent.UpdateContent(streamingMessage, true);
                    ui.RequestRender();
                }
                break;
            }

            case "message_update":
            {
                if (streamingComponent is null || e["message"] is not JsonObject message || S(message["role"]) != "assistant") break;
                streamingMessage = message;
                streamingComponent.UpdateContent(streamingMessage, true);
                if (message["content"] is JsonArray content)
                    foreach (var block in content.OfType<JsonObject>())
                    {
                        if (S(block["type"]) != "toolCall") continue;
                        var id = S(block["id"]) ?? "";
                        if (!pendingTools.TryGetValue(id, out var component))
                        {
                            component = CreateToolExecutionComponent(S(block["name"]) ?? "", id, block["arguments"]);
                            component.SetExpanded(toolOutputExpanded);
                            chatContainer.AddChild(component);
                            pendingTools[id] = component;
                        }
                        else component.UpdateArgs(block["arguments"]);
                    }
                ui.RequestRender();
                break;
            }

            case "message_end":
            {
                if (e["message"] is not JsonObject message) break;
                if (S(message["role"]) == "user") break;
                if (streamingComponent is not null && S(message["role"]) == "assistant")
                {
                    streamingMessage = (JsonObject)message.DeepClone();
                    string? errorMessage = null;
                    var stopReason = S(streamingMessage["stopReason"]);
                    if (stopReason == "aborted")
                    {
                        var retryAttempt = state.RetryAttempt;
                        errorMessage = retryAttempt > 0 ? $"Aborted after {retryAttempt} retry attempt{(retryAttempt > 1 ? "s" : "")}" : "Operation aborted";
                        streamingMessage["errorMessage"] = errorMessage;
                    }
                    streamingComponent.UpdateContent(streamingMessage, false);
                    if (stopReason is "aborted" or "error")
                    {
                        errorMessage ??= S(streamingMessage["errorMessage"]) is { Length: > 0 } text ? text : "Error";
                        foreach (var component in pendingTools.Values)
                            component.UpdateResult(new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = errorMessage }), ["isError"] = true });
                        pendingTools.Clear();
                        MaybeSuggestBugReport(streamingMessage);
                    }
                    else
                    {
                        foreach (var component in pendingTools.Values) component.SetArgsComplete();
                        MaybeShowThinkingDropNotice(streamingMessage);
                        MaybeShowCacheMissNotice(streamingMessage);
                    }
                    streamingComponent = null;
                    streamingMessage = null;
                    footer.Invalidate();
                }
                ui.RequestRender();
                break;
            }

            case "tool_execution_start":
            {
                if (e["parentToolCallId"] is not null) break;
                var id = S(e["toolCallId"]) ?? "";
                if (!pendingTools.TryGetValue(id, out var component))
                {
                    component = CreateToolExecutionComponent(S(e["toolName"]) ?? "", id, e["args"]);
                    component.SetExpanded(toolOutputExpanded);
                    chatContainer.AddChild(component);
                    pendingTools[id] = component;
                }
                component.MarkExecutionStarted();
                ui.RequestRender();
                break;
            }

            case "tool_execution_update":
            {
                if (pendingTools.TryGetValue(S(e["toolCallId"]) ?? "", out var component) && e["partialResult"] is JsonObject partial)
                {
                    var result = (JsonObject)partial.DeepClone(); result["isError"] = false;
                    component.UpdateResult(result, true);
                    ui.RequestRender();
                }
                break;
            }

            case "tool_execution_end":
            {
                if (B(e["isError"])) MaybeShowInstallChangeWarning();
                var id = S(e["toolCallId"]) ?? "";
                if (pendingTools.TryGetValue(id, out var component))
                {
                    var result = e["result"] is JsonObject ended ? (JsonObject)ended.DeepClone() : new JsonObject();
                    result["isError"] = B(e["isError"]);
                    if (e["durationMs"] is JsonValue duration) result["durationMs"] = duration.DeepClone();
                    component.UpdateResult(result);
                    pendingTools.Remove(id);
                    ui.RequestRender();
                }
                break;
            }

            case "agent_end":
                if (settings.ShowTerminalProgress) ui.Terminal.SetProgress(false);
                ClearStatusIndicator(StatusIndicatorKind.Working);
                if (streamingComponent is not null)
                {
                    chatContainer.RemoveChild(streamingComponent);
                    streamingComponent = null;
                    streamingMessage = null;
                }
                pendingTools.Clear();
                ui.RequestRender();
                break;

            case "agent_settled":
                await CheckShutdownRequestedAsync();
                break;

            case "compaction_start":
                if (settings.ShowTerminalProgress) ui.Terminal.SetProgress(true);
                autoCompactionEscapeHandler = defaultEditor.OnEscape;
                defaultEditor.OnEscape = AbortCompaction;
                ShowStatusIndicator(new CompactionStatusIndicator(ui, ToCompactionReason(S(e["reason"]))));
                ui.RequestRender();
                break;

            case "compaction_end":
            {
                if (settings.ShowTerminalProgress) ui.Terminal.SetProgress(false);
                if (autoCompactionEscapeHandler is not null) { defaultEditor.OnEscape = autoCompactionEscapeHandler; autoCompactionEscapeHandler = null; }
                ClearStatusIndicator(StatusIndicatorKind.Compaction);
                var reason = S(e["reason"]);
                if (B(e["aborted"]))
                {
                    if (reason == "manual") ShowError("Compaction cancelled");
                    else ShowStatus("Auto-compaction cancelled");
                }
                else if (e["result"] is JsonObject result)
                {
                    await RefreshEntriesAsync();
                    var entries = SessionEntries.BuildContextEntries(state.Entries, state.LeafId);
                    if (entries.Count == 0 || SessionEntries.Type(entries[0]) != "compaction")
                        throw new InvalidOperationException("Completed compaction is missing from the session context");
                    chatContainer.Clear();
                    RenderSessionEntries(entries.Skip(1).ToList());
                    AddMessageToChat(SessionEntries.CreateCompactionSummaryMessage(S(result["summary"]) ?? "", N(result["tokensBefore"]),
                        DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture)));
                    if (result["usage"] is JsonObject usage) AddCompactionCostNotice("compaction", usage);
                    footer.Invalidate();
                }
                else if (S(e["errorMessage"]) is { Length: > 0 } errorMessage)
                {
                    if (reason == "manual") ShowError(errorMessage);
                    else
                    {
                        chatContainer.AddChild(new Spacer(1));
                        chatContainer.AddChild(new ThemedText(() => theme.Fg("error", errorMessage), 1, 0));
                    }
                }
                _ = FlushCompactionQueueAsync(B(e["willRetry"]));
                ui.RequestRender();
                break;
            }

            case "auto_retry_start":
                retryEscapeHandler = defaultEditor.OnEscape;
                defaultEditor.OnEscape = AbortRetry;
                ShowStatusIndicator(new RetryStatusIndicator(ui, (int)N(e["attempt"]), (int)N(e["maxAttempts"]), N(e["delayMs"])));
                ui.RequestRender();
                break;

            case "auto_retry_end":
                if (retryEscapeHandler is not null) { defaultEditor.OnEscape = retryEscapeHandler; retryEscapeHandler = null; }
                ClearStatusIndicator(StatusIndicatorKind.Retry);
                if (!B(e["success"])) ShowError($"Retry failed after {(int)N(e["attempt"])} attempts: {(S(e["finalError"]) is { Length: > 0 } final ? final : "Unknown error")}");
                ui.RequestRender();
                break;

            case "summarization_retry_scheduled":
                ShowError(S(e["errorMessage"]) ?? "");
                ShowStatusIndicator(new RetryStatusIndicator(ui, (int)N(e["attempt"]), (int)N(e["maxAttempts"]), N(e["delayMs"])));
                ui.RequestRender();
                break;

            case "summarization_retry_attempt_start":
                ClearStatusIndicator(StatusIndicatorKind.Retry);
                if (S(e["source"]) == "branchSummary") ShowStatusIndicator(new BranchSummaryStatusIndicator(ui));
                else ShowStatusIndicator(new CompactionStatusIndicator(ui, ToCompactionReason(S(e["reason"]))));
                ui.RequestRender();
                break;

            case "summarization_retry_finished":
                ClearStatusIndicator(StatusIndicatorKind.Retry);
                ui.RequestRender();
                break;

            case "bash_execution_update":
                if (bashComponent is not null && S(e["delta"]) is { } delta)
                {
                    bashComponent.AppendOutput(delta);
                    ui.RequestRender();
                }
                break;
        }
    }

    private static CompactionStatusReason ToCompactionReason(string? reason) => reason switch
    {
        "threshold" => CompactionStatusReason.Threshold, "overflow" => CompactionStatusReason.Overflow, _ => CompactionStatusReason.Manual
    };

    private async Task OnSessionSwitchedAsync(JsonObject e)
    {
        if (e["generation"] is JsonValue generation && generation.TryGetValue<long>(out var value) && value <= state.Generation) return;
        ResetExtensionUI();
        programStatus.Reset();
        state.Generation = e["generation"] is JsonValue g && g.TryGetValue<long>(out var next) ? next : state.Generation + 1;
        state.SessionFile = S(e["sessionFile"]) ?? state.SessionFile;
        state.SessionId = S(e["sessionId"]) ?? state.SessionId;
        state.IsStreaming = false; state.IsCompacting = false; state.SteeringMessages = []; state.FollowUpMessages = [];
        if (state.SessionFile is { } file && ReadSessionCwd(file) is { } cwd && cwd != state.Cwd)
        {
            state.Cwd = cwd;
            footerDataProvider.SetCwd(cwd);
        }
        await RefreshSessionAsync();
        // rebindCurrentSession -> applyRuntimeSettings: footer.setAutoCompactEnabled(session.autoCompactionEnabled).
        footer.SetAutoCompactEnabled(state.AutoCompactionEnabled);
        await RefreshCommandsAsync();
        RenderCurrentSessionState();
        SetupAutocompleteProvider();
        // rebindCurrentSession: updateAvailableProviderCount() after the new session's extensions bound (they can add providers).
        await RefreshAvailableModelsAsync();
        UpdateAvailableProviderCount();
        UpdateEditorBorderColor();
        UpdateTerminalTitle();
        footer.Invalidate();
        ui.RequestRender();
        renderedGeneration = Math.Max(renderedGeneration, state.Generation);
        CompleteGenerationWaiters();
    }

    private static string? ReadSessionCwd(string sessionFile)
    {
        try
        {
            using var reader = new StreamReader(sessionFile);
            var first = reader.ReadLine();
            return first is null ? null : S((JsonNode.Parse(first) as JsonObject)?["cwd"]);
        }
        catch { return null; }
    }

    private ToolExecutionComponent CreateToolExecutionComponent(string toolName, string toolCallId, JsonNode? args) =>
        new(toolName, toolCallId, args, new ToolExecutionOptions(settings.ShowImages, settings.ImageWidthCells, outputPad),
            GetRegisteredToolDefinition(toolName), ui, Cwd);

    /// <summary>Extension-registered renderers, falling back to the built-in ones.</summary>
    private ToolRenderers? GetRegisteredToolDefinition(string toolName) =>
        context.ResolveToolRenderers?.Invoke(toolName) ?? BuiltInToolRenderers.Resolve(toolName);

    /// <summary>A `{ truncated: true }` truncation result for bash output (interactive-mode.ts).</summary>
    private static PiSharp.Agent.Tools.ToolOutputTruncationResult TruncatedOutput(string? content) => new(content ?? "", true, null, 0, 0, 0, 0, false, false, 0, 0);

    // interactive-mode.ts getMarkdownTransformers: the Mermaid transformer, then the extensions' transformers.
    private MarkdownTransformer? mermaidMarkdownTransformer;
    private IReadOnlyList<MarkdownTransformer> GetMarkdownTransformers() =>
    [
        mermaidMarkdownTransformer ??= MermaidTransformer.Create(() => settings.MermaidRenderingMode, () => Themes.IsInitialized ? Themes.Current : null),
        .. context.MarkdownTransformers?.Invoke() ?? []
    ];

    // =========================================================================
    // Chat rendering
    // =========================================================================

    private void AddCustomEntryToChat(JsonObject entry)
    {
        var renderer = context.GetEntryRenderer?.Invoke(S(entry["customType"]) ?? "");
        if (renderer is null) return;
        var component = new CustomEntryComponent(entry, renderer, outputPad);
        component.SetExpanded(toolOutputExpanded);
        if (!component.HasContent()) return;
        if (streamingComponent is not null)
        {
            var index = chatContainer.Children.IndexOf(streamingComponent);
            if (index >= 0) { chatContainer.Children.Insert(index, component); return; }
        }
        chatContainer.AddChild(component);
    }

    private void AddMessageToChat(JsonObject message, bool populateHistory = false)
    {
        switch (S(message["role"]))
        {
            case "bashExecution":
            {
                var component = new BashExecutionComponent(S(message["command"]) ?? "", ui, B(message["excludeFromContext"]), outputPad);
                if (S(message["output"]) is { Length: > 0 } output) component.AppendOutput(output);
                component.SetComplete(message["exitCode"] is JsonValue code && code.TryGetValue<int>(out var exit) ? exit : null, B(message["cancelled"]),
                    B(message["truncated"]) ? TruncatedOutput(null) : null, S(message["fullOutputPath"]));
                chatContainer.AddChild(component);
                break;
            }
            case "custom":
                if (B(message["display"]))
                {
                    var renderer = context.GetMessageRenderer?.Invoke(S(message["customType"]) ?? "");
                    var component = new CustomMessageComponent(message, renderer, GetMarkdownThemeWithSettings(), outputPad);
                    component.SetExpanded(toolOutputExpanded);
                    chatContainer.AddChild(component);
                }
                break;
            case "compactionSummary":
            {
                chatContainer.AddChild(new Spacer(1));
                var component = new CompactionSummaryMessageComponent(message, GetMarkdownThemeWithSettings(), outputPad);
                component.SetExpanded(toolOutputExpanded);
                chatContainer.AddChild(component);
                break;
            }
            case "branchSummary":
            {
                chatContainer.AddChild(new Spacer(1));
                var component = new BranchSummaryMessageComponent(message, GetMarkdownThemeWithSettings(), outputPad);
                component.SetExpanded(toolOutputExpanded);
                chatContainer.AddChild(component);
                break;
            }
            case "user":
            {
                var textContent = SessionEntries.GetUserMessageText(message);
                if (textContent.Length == 0) break;
                if (chatContainer.Children.Count > 0) chatContainer.AddChild(new Spacer(1));
                if (SessionEntries.ParseSkillBlock(textContent) is { } skillBlock)
                {
                    var component = new SkillInvocationMessageComponent(skillBlock, GetMarkdownThemeWithSettings(), outputPad);
                    component.SetExpanded(toolOutputExpanded);
                    chatContainer.AddChild(component);
                    if (skillBlock.UserMessage is { } userMessage)
                    {
                        chatContainer.AddChild(new Spacer(1));
                        chatContainer.AddChild(new UserMessageComponent(userMessage, GetMarkdownThemeWithSettings(), outputPad, GetMarkdownTransformers()));
                    }
                }
                else chatContainer.AddChild(new UserMessageComponent(textContent, GetMarkdownThemeWithSettings(), outputPad, GetMarkdownTransformers()));
                if (populateHistory) editor.AddToHistory(textContent);
                break;
            }
            case "assistant":
                chatContainer.AddChild(new AssistantMessageComponent(message, hideThinkingBlock, GetMarkdownThemeWithSettings(), hiddenThinkingLabel, outputPad, GetMarkdownTransformers()));
                break;
        }
    }

    /// <summary>A session item: a context message, a custom/usage entry, or a compaction cost notice.</summary>
    private abstract record RenderSessionItem;
    private sealed record MessageItem(JsonObject Message) : RenderSessionItem;
    private sealed record EntryItem(JsonObject Entry) : RenderSessionItem;
    private sealed record CostItem(string Kind, JsonObject Usage) : RenderSessionItem;

    private void RenderSessionItems(IReadOnlyList<RenderSessionItem> items, bool updateFooter = false, bool populateHistory = false)
    {
        pendingTools.Clear();
        var renderedPendingTools = new Dictionary<string, ToolExecutionComponent>(StringComparer.Ordinal);
        var cacheMisses = settings.ShowCacheMissNotices ? context.CollectCacheMisses?.Invoke(state.Entries) : null;
        if (updateFooter)
        {
            footer.Invalidate();
            UpdateEditorBorderColor();
        }
        foreach (var item in items)
        {
            switch (item)
            {
                case EntryItem { Entry: var entry } when SessionEntries.Type(entry) == "custom":
                    AddCustomEntryToChat(entry); continue;
                case EntryItem { Entry: var entry }:
                    AddCacheWarmingUsage(entry); continue;
                case CostItem cost:
                    AddCompactionCostNotice(cost.Kind, cost.Usage); continue;
            }
            var message = ((MessageItem)item).Message;
            var role = S(message["role"]);
            if (role == "assistant")
            {
                AddMessageToChat(message);
                var stopReason = S(message["stopReason"]);
                if (message["content"] is JsonArray content)
                    foreach (var block in content.OfType<JsonObject>().Where(block => S(block["type"]) == "toolCall"))
                    {
                        var id = S(block["id"]) ?? "";
                        var component = CreateToolExecutionComponent(S(block["name"]) ?? "", id, block["arguments"]);
                        component.SetExpanded(toolOutputExpanded);
                        chatContainer.AddChild(component);
                        if (stopReason is "aborted" or "error")
                        {
                            string errorMessage;
                            if (stopReason == "aborted")
                            {
                                var retryAttempt = state.RetryAttempt;
                                errorMessage = retryAttempt > 0 ? $"Aborted after {retryAttempt} retry attempt{(retryAttempt > 1 ? "s" : "")}" : "Operation aborted";
                            }
                            else errorMessage = S(message["errorMessage"]) is { Length: > 0 } text ? text : "Error";
                            component.UpdateResult(new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = errorMessage }), ["isError"] = true });
                        }
                        else renderedPendingTools[id] = component;
                    }
                if (stopReason is not ("aborted" or "error") && cacheMisses is not null && cacheMisses.TryGetValue(CacheMissNotice.Key(message), out var miss)) AddCacheMissNotice(miss);
            }
            else if (role == "toolResult")
            {
                var id = S(message["toolCallId"]) ?? "";
                if (renderedPendingTools.Remove(id, out var component)) component.UpdateResult(message);
            }
            else AddMessageToChat(message, populateHistory);
        }
        foreach (var (id, component) in renderedPendingTools) pendingTools[id] = component;
        ui.RequestRender();
    }

    private void RenderSessionEntries(IReadOnlyList<JsonObject> entries, bool updateFooter = false, bool populateHistory = false)
    {
        if (renderer is TuiAltScreen alt) alt.ResetTextSelection();
        var items = new List<RenderSessionItem>();
        foreach (var entry in entries)
        {
            var type = SessionEntries.Type(entry);
            if (type == "custom" || type == "usage" && S(entry["kind"]) == "cache_warm") { items.Add(new EntryItem(entry)); continue; }
            var messages = SessionEntries.SessionEntryToContextMessages(entry);
            items.AddRange(messages.Select(message => new MessageItem(message)));
            if (type is "compaction" or "branch_summary" && entry["usage"] is JsonObject usage && messages.Count > 0) items.Add(new CostItem(type, usage));
        }
        RenderSessionItems(items, updateFooter, populateHistory);
    }

    private void AddCacheWarmingUsage(JsonObject entry)
    {
        if (!settings.ShowCacheMissNotices) return;
        chatContainer.AddChild(new Spacer(1));
        var usage = context.FormatCacheWarmingUsage?.Invoke(entry) ?? "";
        chatContainer.AddChild(new ThemedText(() => theme.Fg("dim", usage), 1, 0));
    }

    private void AddCompactionCostNotice(string kind, JsonObject usage)
    {
        if (!settings.ShowCacheMissNotices) return;
        var tokens = N(usage["input"]) + N(usage["output"]) + N(usage["cacheRead"]) + N(usage["cacheWrite"]);
        var total = N(usage["cost"]?["total"]);
        var cost = total >= 0.01 ? $" (~${total.ToString("F2", CultureInfo.InvariantCulture)})" : "";
        var label = kind == "compaction" ? "Compaction" : "Branch summary";
        chatContainer.AddChild(new Spacer(1));
        chatContainer.AddChild(new ThemedText(() => theme.Fg("warning", $"{label}: {FooterComponent.FormatTokens(tokens)} tokens billed{cost}"), 1, 0));
    }

    private static int CountDroppedThinkingBlocks(JsonObject message)
    {
        var count = 0;
        if (message["diagnostics"] is not JsonArray diagnostics) return 0;
        foreach (var diagnostic in diagnostics.OfType<JsonObject>())
        {
            if (S(diagnostic["type"]) != "anthropic_input_transformations") continue;
            if (diagnostic["details"]?["transformations"] is not JsonArray transformations) continue;
            count += transformations.OfType<JsonObject>().Count(transformation => S(transformation["type"]) == "thinking_dropped");
        }
        return count;
    }

    private void MaybeShowThinkingDropNotice(JsonObject message)
    {
        if (!settings.ShowCacheMissNotices) return;
        var droppedCount = CountDroppedThinkingBlocks(message);
        if (droppedCount == 0) return;
        var previousDroppedCount = 0;
        var branch = SessionEntries.BuildSessionPath(state.Entries, state.LeafId);
        for (var i = branch.Count - 1; i >= 0; i--)
        {
            var entry = branch[i];
            if (SessionEntries.Type(entry) == "message" && entry["message"] is JsonObject previous && S(previous["role"]) == "assistant")
            {
                previousDroppedCount = CountDroppedThinkingBlocks(previous);
                break;
            }
        }
        if (droppedCount <= previousDroppedCount) return;
        var noun = droppedCount == 1 ? "thinking block" : "thinking blocks";
        chatContainer.AddChild(new Spacer(1));
        chatContainer.AddChild(new ThemedText(() => theme.Fg("warning", $"Anthropic dropped {droppedCount} {noun} (details in session)"), 1, 0));
    }

    private void MaybeShowCacheMissNotice(JsonObject message)
    {
        if (!settings.ShowCacheMissNotices) return;
        if (context.DetectCacheMiss?.Invoke(state.Entries, message) is { } miss) AddCacheMissNotice(miss);
    }

    private void AddCacheMissNotice(CacheMissNotice miss)
    {
        if (miss.MissedTokens < 20_000 && miss.MissedCost < 0.1) return;
        var cost = miss.MissedCost >= 0.01 ? $" (~${miss.MissedCost.ToString("F2", CultureInfo.InvariantCulture)})" : "";
        var reBilled = $"{FooterComponent.FormatTokens(miss.MissedTokens)} tokens re-billed{cost}";
        var label = "Cache miss";
        if (miss.ModelChanged) label = "Cache miss after model switch";
        else if (miss.IdleMs >= CacheMissNotice.CacheTtlMs) label = $"Cache miss after {Math.Round(miss.IdleMs / 60_000, MidpointRounding.AwayFromZero)}m idle";
        chatContainer.AddChild(new Spacer(1));
        chatContainer.AddChild(new ThemedText(() => theme.Fg("warning", $"{label}: {reBilled}"), 1, 0));
    }

    public void RenderInitialMessages()
    {
        var entries = SessionEntries.BuildContextEntries(state.Entries, state.LeafId);
        RenderSessionEntries(entries, updateFooter: true, populateHistory: true);
        RenderProjectTrustWarningIfNeeded();
        var compactionCount = state.Entries.Count(entry => SessionEntries.Type(entry) == "compaction");
        if (compactionCount > 0)
        {
            var times = compactionCount == 1 ? "1 time" : $"{compactionCount} times";
            ShowStatus($"Session compacted {times}");
        }
    }

    private void RenderProjectTrustWarningIfNeeded()
    {
        if (settings.IsProjectTrusted || !context.HasTrustRequiringProjectResources(Cwd)) return;
        if (chatContainer.Children.Count > 0) chatContainer.AddChild(new Spacer(1));
        chatContainer.AddChild(new ThemedText(() => theme.Fg("warning",
            $"This project is not trusted. Project {PiSharp.Cli.Pi.PiConfig.ConfigDirName} resources and packages are ignored. Use /trust to save a trust decision, then restart pi."), 1, 0));
    }

    private Task<string?> GetUserInputAsync(CancellationToken token)
    {
        if (pendingUserInputs.TryDequeue(out var queued)) return Task.FromResult<string?>(queued);
        var waiter = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        token.Register(() => waiter.TrySetResult(null));
        onInputCallback = text => { onInputCallback = null; waiter.TrySetResult(text); };
        return waiter.Task;
    }

    private void RebuildChatFromMessages()
    {
        chatContainer.Clear();
        RenderSessionEntries(SessionEntries.BuildContextEntries(state.Entries, state.LeafId));
    }

    private void RenderCurrentSessionState()
    {
        loadedResourcesContainer.Clear();
        chatContainer.Clear();
        pendingMessagesContainer.Clear();
        compactionQueuedMessages = [];
        streamingComponent = null;
        streamingMessage = null;
        pendingTools.Clear();
        RenderInitialMessages();
    }

    // =========================================================================
    // Status indicators
    // =========================================================================

    private bool SetEditorWorkingStatusIndicator(StatusIndicator? indicator)
    {
        defaultEditor.SetWorkingStatusIndicator(null);
        if (editor is not CustomEditor { EmbedWorkingStatus: true } working) return false;
        working.SetWorkingStatusIndicator(indicator);
        return true;
    }

    private void ShowStatusIndicator(StatusIndicator indicator)
    {
        activeStatusIndicator?.Dispose();
        activeStatusIndicator = indicator;
        activeWorkingIndicatorEmbedded = false;
        statusContainer.Clear();
        SetEditorWorkingStatusIndicator(null);
        if (SetEditorWorkingStatusIndicator(indicator))
        {
            activeWorkingIndicatorEmbedded = true;
            return;
        }
        statusContainer.AddChild(indicator);
    }

    private void ClearStatusIndicator(StatusIndicatorKind? kind = null)
    {
        if (kind is not null && activeStatusIndicator?.Kind != kind) return;
        var cleared = activeStatusIndicator;
        var wasEmbedded = activeWorkingIndicatorEmbedded;
        cleared?.Dispose();
        activeStatusIndicator = null;
        activeWorkingIndicatorEmbedded = false;
        statusContainer.Clear();
        SetEditorWorkingStatusIndicator(null);
        if (cleared is not null && !wasEmbedded && options.TuiMode == "regular" && ui.ClearOnShrink) statusContainer.AddChild(idleStatus);
    }

    private void ShowWorkingStatusIndicator()
    {
        Func<string, string>? colorFn = editor is CustomEditor { EmbedWorkingStatus: true }
            ? text => (editor.BorderColor ?? theme.GetThinkingBorderColor(state.ThinkingLevel ?? "off"))(text) : null;
        ShowStatusIndicator(new WorkingStatusIndicator(ui, workingMessage ?? DefaultWorkingMessage, workingIndicatorOptions, colorFn));
    }

    private void SetWorkingVisible(bool visible)
    {
        workingVisible = visible;
        if (!visible)
        {
            ClearStatusIndicator(StatusIndicatorKind.Working);
            ui.RequestRender();
            return;
        }
        if (state.IsStreaming && activeStatusIndicator?.Kind != StatusIndicatorKind.Working) ShowWorkingStatusIndicator();
        ui.RequestRender();
    }

    private void SetWorkingIndicator(LoaderIndicatorOptions? indicator = null)
    {
        workingIndicatorOptions = indicator;
        if (activeStatusIndicator?.Kind == StatusIndicatorKind.Working) activeStatusIndicator.SetIndicator(indicator);
        ui.RequestRender();
    }

    private void SetHiddenThinkingLabel(string? label = null)
    {
        hiddenThinkingLabel = label ?? DefaultHiddenThinkingLabel;
        foreach (var child in chatContainer.Children) if (child is AssistantMessageComponent assistant) assistant.SetHiddenThinkingLabel(hiddenThinkingLabel);
        streamingComponent?.SetHiddenThinkingLabel(hiddenThinkingLabel);
        ui.RequestRender();
    }

    // =========================================================================
    // Queues
    // =========================================================================

    private (List<string> Steering, List<string> FollowUp) GetAllQueuedMessages() =>
        ([.. state.SteeringMessages, .. compactionQueuedMessages.Where(message => message.Mode == "steer").Select(message => message.Text)],
         [.. state.FollowUpMessages, .. compactionQueuedMessages.Where(message => message.Mode == "followUp").Select(message => message.Text)]);

    private async Task<(List<string> Steering, List<string> FollowUp)> ClearAllQueuesAsync()
    {
        var (steering, followUp) = await ClearSessionQueueAsync();
        var compactionSteering = compactionQueuedMessages.Where(message => message.Mode == "steer").Select(message => message.Text).ToList();
        var compactionFollowUp = compactionQueuedMessages.Where(message => message.Mode == "followUp").Select(message => message.Text).ToList();
        compactionQueuedMessages = [];
        return ([.. steering, .. compactionSteering], [.. followUp, .. compactionFollowUp]);
    }

    private void UpdatePendingMessagesDisplay()
    {
        pendingMessagesContainer.Clear();
        var (steering, followUp) = GetAllQueuedMessages();
        if (steering.Count == 0 && followUp.Count == 0) return;
        pendingMessagesContainer.AddChild(new Spacer(1));
        foreach (var message in steering) pendingMessagesContainer.AddChild(new TruncatedText(theme.Fg("dim", $"Steering: {message}"), 1, 0));
        foreach (var message in followUp) pendingMessagesContainer.AddChild(new TruncatedText(theme.Fg("dim", $"Follow-up: {message}"), 1, 0));
        var dequeueHint = KeybindingHints.KeyDisplayText("app.message.dequeue");
        pendingMessagesContainer.AddChild(new TruncatedText(theme.Fg("dim", $"↳ {dequeueHint} to edit all queued messages"), 1, 0));
    }

    private async Task<int> RestoreQueuedMessagesToEditorAsync(bool abort = false, string? currentText = null)
    {
        var (steering, followUp) = await ClearAllQueuesAsync();
        var allQueued = steering.Concat(followUp).ToList();
        if (allQueued.Count == 0)
        {
            UpdatePendingMessagesDisplay();
            if (abort) _ = AbortAsync();
            return 0;
        }
        var queuedText = string.Join("\n\n", allQueued);
        currentText ??= editor.GetText();
        var combined = string.Join("\n\n", new[] { queuedText, currentText }.Where(text => TextUtils.JsTrim(text).Length > 0));
        editor.SetText(combined);
        UpdatePendingMessagesDisplay();
        if (abort) _ = AbortAsync();
        return allQueued.Count;
    }

    private void QueueCompactionMessage(string text, string mode)
    {
        compactionQueuedMessages.Add(new(text, mode));
        editor.AddToHistory(text);
        editor.SetText("");
        UpdatePendingMessagesDisplay();
        ShowStatus("Queued message for after compaction");
    }

    private async Task FlushCompactionQueueAsync(bool willRetry)
    {
        if (compactionQueuedMessages.Count == 0) return;
        var queued = compactionQueuedMessages.ToList();
        compactionQueuedMessages = [];
        UpdatePendingMessagesDisplay();
        void RestoreQueue(Exception error)
        {
            _ = ClearSessionQueueAsync();
            compactionQueuedMessages = queued;
            UpdatePendingMessagesDisplay();
            ShowError($"Failed to send queued message{(queued.Count > 1 ? "s" : "")}: {error.Message}");
        }
        try
        {
            if (willRetry)
            {
                foreach (var message in queued)
                {
                    if (IsExtensionCommand(message.Text)) await PromptAsync(message.Text);
                    else if (message.Mode == "followUp") await FollowUpAsync(message.Text);
                    else await SteerAsync(message.Text);
                }
                UpdatePendingMessagesDisplay();
                return;
            }
            var firstPromptIndex = queued.FindIndex(message => !IsExtensionCommand(message.Text));
            if (firstPromptIndex == -1)
            {
                foreach (var message in queued) await PromptAsync(message.Text);
                return;
            }
            foreach (var message in queued.Take(firstPromptIndex)) await PromptAsync(message.Text);
            var firstPrompt = queued[firstPromptIndex];
            var promptTask = PromptAsync(firstPrompt.Text, streamingBehavior: firstPrompt.Mode).ContinueWith(task =>
            {
                if (task.Exception is { } failure) context.Loop.Post(() => RestoreQueue(failure.InnerException ?? failure));
            }, TaskScheduler.Default);
            foreach (var message in queued.Skip(firstPromptIndex + 1))
            {
                if (IsExtensionCommand(message.Text)) await PromptAsync(message.Text);
                else if (message.Mode == "followUp") await FollowUpAsync(message.Text);
                else await SteerAsync(message.Text);
            }
            UpdatePendingMessagesDisplay();
            _ = promptTask;
        }
        catch (Exception error) { RestoreQueue(error); }
    }

    private void FlushPendingBashComponents()
    {
        foreach (var component in pendingBashComponents)
        {
            pendingMessagesContainer.RemoveChild(component);
            chatContainer.AddChild(component);
        }
        pendingBashComponents.Clear();
    }

    // =========================================================================
    // Bug-report hints
    // =========================================================================

    private void SuggestBugReport()
    {
        if (bugReportHintShown) return;
        bugReportHintShown = true;
        chatContainer.AddChild(new ThemedText(() => theme.Fg("muted", "If this looks like a PiSharp bug, /bug prepares a report and a GitHub issue."), outputPad, 0));
        ui.RequestRender();
    }

    private void MaybeSuggestBugReport(JsonObject message)
    {
        if (S(message["stopReason"]) != "error" || context.IsRetryableAssistantError(message)) return;
        if (System.Text.RegularExpressions.Regex.IsMatch(S(message["errorMessage"]) ?? "", @"\b(?:abort(?:ed)?|cancel(?:l?ed)?)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return;
        if (MaybeShowInstallChangeWarning()) return;
        SuggestBugReport();
    }

    /// <summary>After an error, warn when an update replaced or removed this install while the session ran.</summary>
    private bool MaybeShowInstallChangeWarning()
    {
        if (installChangeWarningShown) return true;
        if (context.DetectInstallChange() is not { } change) return false;
        installChangeWarningShown = true;
        var cause = change.Kind == "updated"
            ? $"{AppName} was updated to {change.Version} while this session was running ({version})"
            : $"The {AppName} installation this session runs from was removed or replaced";
        var resumeCommand = FormatResumeCommand();
        var restart = resumeCommand is not null ? $"Restart with `{resumeCommand}` to continue this session." : $"Restart {AppName}.";
        ShowWarning($"{cause}. Features that load code on demand can fail until restart. {restart}");
        return true;
    }
}
