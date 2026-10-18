// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/experimental/durable/tui.ts (Handlers, class DurableTui).
// The fullscreen view of one conversation: the transcript from the view's entries and the pi.live document, the task panel, the inbox
// queue, notices, the editor and the footer, built from pi's interactive components (AssistantMessageComponent, UserMessageComponent,
// ToolExecutionComponent with the built-in tool renderers, CustomEditor, WorkingStatusIndicator) over PiSharp.Tui's TuiAltScreen.
// Everything here runs on the TUI's UiLoop, as upstream runs on Node's event loop. PiSharp-only since pi-durable 1.2.0 (durable work list
// area l): the nested calls of a tool call as rows under its card, a tool result's diagnostics as plain lines, tool cards with pi's image
// settings, and alerts above the notices for a failed session and for pi.live.compactionFailure.
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Nodes;
using PiSharp.Cli.Interactive.Mode;
using PiSharp.Cli.Interactive.Mode.Components;
using PiSharp.Durable;
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Durable.DurableTuiState;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;
using DurableNumber = PiSharp.Durable.Chord.JsonNumberValue;
using DurableString = PiSharp.Durable.Chord.JsonStringValue;
using TuiKeybindingsManager = PiSharp.Tui.Pi.KeybindingsManager;

namespace PiSharp.Cli.Durable;

/// <summary>What the view's editor asks for (upstream <c>Handlers</c>).</summary>
internal sealed record DurableTuiHandlers(
    Action<string> Submit,
    Action<string> FollowUp,
    Action Abort,
    Action Exit,
    Action SelectModel,
    Action CycleThinking);

/// <summary>Upstream's <c>DurableTui</c> class: renders <see cref="DurableView"/> revisions.</summary>
internal sealed class DurableTuiView
{
    private static readonly IReadOnlyDictionary<string, ToolRenderers> Renderers = BuiltInToolRenderers.CreateAllToolRenderers();

    private readonly TuiAltScreen ui;
    private readonly Container chat = new();
    private readonly Container tasks = new();
    private readonly Container queue = new();
    private readonly Container alerts = new();
    private readonly Container notices = new();
    private readonly Container footer = new();
    private readonly Text footerStats = new("", 1, 0);
    private readonly Text footerHints = new("", 1, 0);
    private readonly Container editorContainer = new();
    private readonly CustomEditor editor;
    private readonly string cwd;
    private readonly ToolExecutionOptions toolOptions;
    /// <summary>The newest card per call ID; provider call IDs may repeat across turns.</summary>
    private readonly Dictionary<string, ToolExecutionComponent> tools = new(StringComparer.Ordinal);
    /// <summary>Every card shown, also older ones whose call ID a later turn reused.</summary>
    private readonly List<ToolExecutionComponent> cards = [];
    /// <summary>Call IDs whose cards the streaming answer created; its entry takes them over.</summary>
    private readonly HashSet<string> streamingCalls = new(StringComparer.Ordinal);
    /// <summary>The group of each card: the card and the rows of its nested calls.</summary>
    private readonly Dictionary<ToolExecutionComponent, DurableToolGroup> groups = new(ReferenceEqualityComparer.Instance);
    /// <summary>Where the nested calls of a tool task go, by task ID: the rows under its model-issued call's card, and the depth of its own
    /// row there (0 for the model-issued call).</summary>
    private readonly Dictionary<long, (DurableNestedCalls Rows, int Depth)> nestedHosts = [];
    /// <summary>Nested calls placed once; after a rebuild only a caller that still runs places them again.</summary>
    private readonly HashSet<long> placedNested = [];
    private readonly List<DurableCompactionComponent> summaries = [];
    /// <summary>Tool output and summaries shown in full; toggled like pi.</summary>
    private bool expanded;
    private List<EntryId> renderedEntryIds = [];
    private AssistantMessageComponent? streaming;
    private StatusIndicator? indicator;
    private string statusText = "";
    /// <summary>Set when the transcript was rebuilt: the next render repaints the screen and shows the end.</summary>
    private bool rebuilt;
    private readonly ScrollView transcript;

    /// <param name="toolOptions">pi's settings for tool cards (<c>terminal.showImages</c>, <c>terminal.imageWidthCells</c>, <c>outputPad</c>), as
    /// interactive mode passes them; null is the cards' defaults.</param>
    public DurableTuiView(ITerminal terminal, UiLoop loop, string cwd, string agentDir, DurableTuiHandlers handlers,
        ToolExecutionOptions? toolOptions = null)
    {
        this.cwd = cwd;
        this.toolOptions = toolOptions ?? new();
        ui = new TuiAltScreen(terminal, loop, false, agentDir);
        var keybindings = AppKeybindings.Create(agentDir);
        TuiKeybindingsManager.SetGlobal(keybindings.Manager);
        editor = new CustomEditor(ui, Themes.GetEditorTheme(), keybindings.Manager, new CustomEditorOptions(PaddingX: 1, EmbedWorkingStatus: true))
        {
            OnSubmit = handlers.Submit,
            OnEscape = handlers.Abort,
            OnCtrlD = handlers.Exit,
        };
        editor.OnAction("app.clear", handlers.Exit);
        editor.OnAction("app.model.select", handlers.SelectModel);
        editor.OnAction("app.thinking.cycle", handlers.CycleThinking);
        editor.OnAction("app.tools.expand", () =>
        {
            expanded = !expanded;
            foreach (var card in cards)
            {
                card.SetExpanded(expanded);
                if (groups.TryGetValue(card, out var group)) group.Nested.SetExpanded(expanded);
            }
            foreach (var summary in summaries) summary.SetExpanded(expanded);
            ui.RequestRender();
        });
        editor.OnAction("app.message.followUp", () =>
        {
            var text = TextUtils.JsTrim(editor.GetText());
            if (text.Length == 0) return;
            editor.SetText("");
            handlers.FollowUp(text);
        });

        editorContainer.AddChild(editor);
        footer.AddChild(footerStats);
        footer.AddChild(footerHints);
        // One empty line between the transcript and everything below it.
        var content = new Container();
        content.AddChild(chat);
        content.AddChild(new Spacer(1));
        transcript = new ScrollView(content, new ScrollViewOptions(ScrollViewFollow.End, Primary: true, Overscroll: ScrollOverscroll.Chain, Loop: loop));
        var dock = new VStack([
            new StackEntry(tasks, Shrink: 1, MinSize: 0),
            new StackEntry(queue, Shrink: 1, MinSize: 0),
            new StackEntry(alerts, Shrink: 1, MinSize: 0),
            new StackEntry(notices, Shrink: 1, MinSize: 0),
            new StackEntry(editorContainer, Shrink: 1, MinSize: 3),
            new StackEntry(footer, Shrink: 1, MinSize: 0),
        ]);
        foreach (var component in new IComponent[] { chat, tasks, queue, alerts, notices, editorContainer, footer }) ui.AddChild(component);
        ui.SetLayoutRoot(new VStack([
            new StackEntry(transcript, Basis: 0, Grow: 1, Shrink: 1, MinSize: 1),
            new StackEntry(dock, Basis: null, Grow: 0, Shrink: 1, MinSize: 1),
        ]));
        ui.SetFocus(editor);
    }

    public TuiAltScreen Ui => ui;

    /// <summary>The editor (tests read its text and border).</summary>
    internal CustomEditor Editor => editor;

    public void Start() => ui.Start();

    public void Stop()
    {
        DiscardTools();
        indicator?.Dispose();
        ui.Stop();
    }

    /// <summary>Finish every card: a running bash card keeps a timer until it gets a final result.</summary>
    private void DiscardTools()
    {
        foreach (var component in cards) component.UpdateResult(new JsonObject { ["content"] = new JsonArray(), ["isError"] = false }, false);
        cards.Clear();
        tools.Clear();
        streamingCalls.Clear();
        groups.Clear();
        nestedHosts.Clear();
    }

    public void Mount(IComponent component)
    {
        editorContainer.Clear();
        editorContainer.AddChild(component);
        ui.SetFocus(component);
        ui.RequestRender();
    }

    public void RestoreEditor() => Mount(editor);

    public void Apply(DurableView view)
    {
        var live = Doc(view.Conversation, "pi.live", static () => []);
        SyncTranscript(view.Conversation.Entries);
        var message = Obj(Obj(live, "generation"), "message");
        // A partial without its entry was dropped, for example by a retry: render the transcript again.
        if (message is null && streaming is not null) Rebuild(view.Conversation.Entries);
        if (message is not null) SyncStreaming(message);
        foreach (var slot in Arr(live, "tools"))
        {
            var status = Str(slot, "status");
            if (status == "pending") continue;
            var component = Tool(Str(slot, "name") ?? "", Str(slot, "callId") ?? "");
            // Its nested calls go under its card (a slot has its task ID since pi-durable 1.2.0).
            if (Num(slot, "taskId") is { } taskId && groups.TryGetValue(component, out var group)) nestedHosts[(long)taskId] = (group.Nested, 0);
            component.SetArgsComplete();
            if (status != "running") continue;
            component.MarkExecutionStarted();
            var details = slot is JsonObject slotObject ? slotObject["details"] : null;
            var child = Num(details, "conversationId");
            var output = Str(slot, "output");
            if (output is null && child is not null)
            {
                var text = $"Subagent {JsNumber(child.Value)} is working. /agents switches to it.";
                component.UpdateResult(ToolOutput(text, details), true);
            }
            else if (output is not null) component.UpdateResult(ToolOutput(output, details), true);
        }
        SyncNested(view.NestedCalls);
        SyncTasks(view.Tasks);
        SyncQueue(Doc(view.Conversation, "pi.inbox", static () => new JsonObject { ["items"] = new JsonArray() }));
        SyncAlerts(view, live);
        SyncNotices(view);
        editor.BorderColor = theme.GetThinkingBorderColor(AgentOf(view.Conversation).ThinkingLevel ?? "off");
        // A failed session runs nothing any more: what pi.live last said is not happening.
        SyncStatus(view.Failure is null ? live : new JsonObject());
        SyncFooter(view);
        if (rebuilt) transcript.ScrollToEnd();
        ui.RequestRender(rebuilt);
        rebuilt = false;
    }

    private static JsonObject ToolOutput(string text, JsonNode? details) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["details"] = details?.DeepClone(),
        ["isError"] = false,
    };

    /// <summary>
    /// The rows of the nested calls (pi.live.nestedTools, gathered by the runtime), each under the card of the model-issued call it belongs
    /// to and indented under the nested call that made it. A call is placed by its caller's task ID, or, when the TUI never saw its
    /// model-issued caller's slot, by the caller's call ID; it stays where it was placed. A call whose caller has no card is not shown. A
    /// rebuilt transcript drops the rows; only the calls of a model-issued call that still runs are placed again, by its task ID, as call IDs
    /// may repeat across turns.
    /// </summary>
    private void SyncNested(ImmutableSortedDictionary<long, DurableNestedCall>? calls)
    {
        if (calls is null) return;
        // Ascending task IDs: a caller's row comes before its calls'.
        foreach (var (taskId, call) in calls)
        {
            if (!nestedHosts.TryGetValue(taskId, out var host))
            {
                if (call.Slot.TryGetValue("parentTaskId", out var parent) && parent is DurableNumber caller &&
                    nestedHosts.TryGetValue((long)caller.Value, out var callerHost)) host = (callerHost.Rows, callerHost.Depth + 1);
                else if (!placedNested.Contains(taskId) && call.Slot.TryGetValue("parentCallId", out var parentCall) &&
                    parentCall is DurableString callId && tools.TryGetValue(callId.Value, out var card) && groups.TryGetValue(card, out var group))
                    host = (group.Nested, 1);
                else continue;
                nestedHosts[taskId] = host;
                placedNested.Add(taskId);
            }
            host.Rows.Set(taskId, NestedRow(call), host.Depth);
        }
    }

    /// <summary>What must stay on screen until it changes: the session failed, or the newest compaction failed (pi.live.compactionFailure,
    /// kept until a compaction produces a summary).</summary>
    private void SyncAlerts(DurableView view, JsonObject live)
    {
        alerts.Clear();
        if (view.Failure is { } failure) alerts.AddChild(new Text(theme.Fg("error", DurableFailure.Message(failure)), 1, 0));
        if (Obj(live, "compactionFailure") is { } compaction)
            alerts.AddChild(new TruncatedText(theme.Fg("error",
                $"Compaction failed ({Str(compaction, "reason") ?? "undefined"}): {Str(compaction, "error") ?? "undefined"}"), 1, 0));
    }

    private void SyncTasks(TaskGraph? graph)
    {
        tasks.Clear();
        if (graph is null) return;
        var nodes = graph.Tasks.Values.ToList();
        var lines = new List<string> { theme.Fg("accent", $"Tasks ({nodes.Count} live, /tasks to hide)") };
        // A conversation-owned task sits under the task that owns its conversation, when that task is live.
        var owned = nodes.SelectMany(node => node.Conversations).ToHashSet();
        IEnumerable<TaskGraphNode> Children(TaskGraphNode node) => nodes.Where(candidate =>
            candidate.Owner == node.Id || candidate.Owner is null && node.Conversations.Contains(candidate.ConversationId));
        void Visit(TaskGraphNode node, int depth)
        {
            lines.Add($"{string.Concat(Enumerable.Repeat("  ", depth + 1))}{DescribeTask(node)}");
            foreach (var child in Children(node)) Visit(child, depth + 1);
        }
        foreach (var node in nodes)
        {
            if (node.Owner is null && !owned.Contains(node.ConversationId)) Visit(node, 0);
        }
        foreach (var line in lines) tasks.AddChild(new TruncatedText(theme.Fg("muted", line), 1, 0));
    }

    private void SyncQueue(JsonObject inbox)
    {
        queue.Clear();
        foreach (var item in Arr(inbox, "items"))
        {
            var mode = Str(item, "mode") ?? "undefined";
            var text = mode == "write"
                ? $"<{Str(Obj(item, "entry"), "kind") ?? "undefined"}>"
                : UserText(item is JsonObject itemObject ? itemObject["content"] : null);
            queue.AddChild(new TruncatedText(theme.Fg("muted", $"[{mode}] {text}"), 1, 0));
        }
    }

    private void SyncNotices(DurableView view)
    {
        notices.Clear();
        foreach (var item in view.Notices.Skip(Math.Max(0, view.Notices.Length - 4)))
        {
            var color = item.Level switch
            {
                DurableNoticeLevel.Error => "error",
                DurableNoticeLevel.Warning => "warning",
                _ => "muted",
            };
            notices.AddChild(new TruncatedText(theme.Fg(color, item.Message), 1, 0));
        }
    }

    private void SyncStatus(JsonObject live)
    {
        var generation = Obj(live, "generation");
        var compaction = Arr(live, "compactions").FirstOrDefault();
        var runningTool = Arr(live, "tools").FirstOrDefault(slot => Str(slot, "status") == "running");
        var text = "";
        if (Obj(generation, "retry") is { } retry)
            text = $"Retrying (attempt {JsNumber((Num(generation, "attempt") ?? double.NaN) + 1)}): {Str(retry, "error") ?? "undefined"}";
        else if (Obj(generation, "deferred") is not null) text = "Waiting for deferred response...";
        else if (compaction is not null)
        {
            var reason = Str(compaction, "reason") ?? "undefined";
            text = Obj(compaction, "retry") is not null
                ? $"Retrying {reason} compaction (attempt {JsNumber((Num(compaction, "attempt") ?? double.NaN) + 1)})..."
                : $"Compacting ({reason})...";
        }
        else if (runningTool is not null) text = $"Running {Str(runningTool, "name") ?? "undefined"}... (esc to abort)";
        else if (live["run"] is not null) text = "Working... (esc to abort)";
        if (text == statusText) return;
        statusText = text;
        indicator?.Dispose();
        indicator = text.Length > 0 ? new WorkingStatusIndicator(ui, text, null, part => (editor.BorderColor ?? (value => value))(part)) : null;
        editor.SetWorkingStatusIndicator(indicator);
    }

    private void SyncFooter(DurableView view)
    {
        var agent = AgentOf(view.Conversation);
        var usage = TotalUsage(Doc(view.Conversation, "pi.usage", static () => new JsonObject { ["models"] = new JsonObject(), ["tools"] = new JsonObject() }));
        var stats = new List<string>();
        if (usage.Input != 0) stats.Add($"↑{FooterComponent.FormatTokens(usage.Input)}");
        if (usage.Output != 0) stats.Add($"↓{FooterComponent.FormatTokens(usage.Output)}");
        if (usage.CacheRead != 0) stats.Add($"R{FooterComponent.FormatTokens(usage.CacheRead)}");
        if (usage.CacheWrite != 0) stats.Add($"W{FooterComponent.FormatTokens(usage.CacheWrite)}");
        stats.Add($"${usage.Cost.ToString("F3", CultureInfo.InvariantCulture)}");
        var contextWindow = view.Models.FirstOrDefault(model => agent.Model is { } current && model.Provider == current.Provider &&
            model.ModelId == current.ModelId)?.ContextWindow ?? 0;
        if (contextWindow > 0)
        {
            var tokens = ContextTokens(view.Conversation.Entries);
            double? percent = tokens is null ? null : tokens.Value / contextWindow * 100;
            var text = $"{(percent is null ? "?" : percent.Value.ToString("F1", CultureInfo.InvariantCulture))}%/{FooterComponent.FormatTokens(contextWindow)}";
            stats.Add(percent is > 90 ? theme.Fg("error", text) : text);
        }
        footerStats.SetText(theme.Fg("dim", $"{string.Join(" ", stats)}  {view.Session.Cwd}"));
        var model = agent.Model is null ? "no model" : $"{agent.Model.Provider}/{agent.Model.ModelId}";
        var shown = view.Conversations.FirstOrDefault(candidate => candidate.Id == view.Conversation.Conversation.Id);
        var label = shown?.Label ?? $"conversation {view.Conversation.Conversation.Id.Value.ToString(CultureInfo.InvariantCulture)}";
        footerHints.SetText(
            theme.Fg(label == "main" ? "dim" : "accent", label) + theme.Fg("dim",
                $" · {model} · thinking:{agent.ThinkingLevel ?? "off"} ({KeybindingHints.KeyText("app.thinking.cycle")}) · " +
                (DurableTuiState.ServiceTierOf(view.Conversation) is { } tier ? $"tier:{tier} (/tier) · " : "") +
                $"{KeybindingHints.KeyText("app.model.select")} or /model · /agents · /compact · /tasks · " +
                $"{KeybindingHints.KeyText("app.message.followUp")} follow-up · {KeybindingHints.KeyText("app.clear")} exit"));
    }

    private void SyncTranscript(IReadOnlyList<EntryRecord> entries)
    {
        // Compaction and resets replace the head of the active transcript.
        if (renderedEntryIds.Where((id, index) => index >= entries.Count || entries[index].Id != id).Any()) Rebuild(entries);
        foreach (var entry in entries.Skip(renderedEntryIds.Count))
        {
            AddEntry(entry);
            renderedEntryIds.Add(entry.Id);
        }
    }

    private void Rebuild(IReadOnlyList<EntryRecord> entries)
    {
        chat.Clear();
        DiscardTools();
        summaries.Clear();
        rebuilt = true;
        renderedEntryIds = [];
        streaming = null;
        foreach (var entry in entries)
        {
            AddEntry(entry);
            renderedEntryIds.Add(entry.Id);
        }
    }

    private void AddEntry(EntryRecord entry)
    {
        var message = Message(entry);
        var role = Str(message, "role");
        if (entry.Kind == "pi.user" && role == "user")
        {
            chat.AddChild(new Spacer(1));
            chat.AddChild(new UserMessageComponent(UserText(message!["content"])));
        }
        else if (entry.Kind == "pi.assistant" && role == "assistant")
        {
            var component = streaming ?? new AssistantMessageComponent();
            if (streaming is null) chat.AddChild(component);
            streaming = null;
            component.UpdateContent(message!, false);
            // Only a tool-calling answer runs its calls; an aborted, failed, or truncated one never does.
            var ran = Str(message, "stopReason") == "toolUse";
            foreach (var content in Arr(message, "content"))
            {
                if (Str(content, "type") != "toolCall") continue;
                var id = Str(content, "id") ?? "";
                var streamed = streamingCalls.Contains(id);
                // Only cards the stream already showed are kept for calls that never run.
                if (!ran && !streamed) continue;
                var card = Tool(Str(content, "name") ?? "", id, ((JsonObject)content!)["arguments"]?.DeepClone() ?? new JsonObject(), !streamed);
                card.SetArgsComplete();
                if (!ran)
                {
                    card.UpdateResult(new JsonObject
                    {
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Not run: the answer was interrupted." }),
                        ["isError"] = true,
                    }, false);
                }
            }
            streamingCalls.Clear();
        }
        else if (entry.Kind == "pi.tool-result" && role == "toolResult")
        {
            Tool(Str(message, "toolName") ?? "", Str(message, "toolCallId") ?? "").UpdateResult(DisplayResult(entry, message!));
        }
        else if (entry.Kind == "pi.compaction")
        {
            var summary = new DurableCompactionComponent(role == "user" ? UserText(message!["content"]) : "", expanded);
            summaries.Add(summary);
            chat.AddChild(new Spacer(1));
            chat.AddChild(summary);
        }
        else if (entry.Kind == "pi.reset") AddText("[new context]");
    }

    private void SyncStreaming(JsonObject message)
    {
        if (streaming is null)
        {
            streaming = new AssistantMessageComponent();
            chat.AddChild(streaming);
        }
        streaming.UpdateContent(message, true);
        foreach (var content in Arr(message, "content"))
        {
            if (Str(content, "type") != "toolCall") continue;
            var id = Str(content, "id") ?? "";
            Tool(Str(content, "name") ?? "", id, ((JsonObject)content!)["arguments"]?.DeepClone() ?? new JsonObject(), !streamingCalls.Contains(id));
            streamingCalls.Add(id);
        }
    }

    private void AddText(string text)
    {
        chat.AddChild(new Spacer(1));
        chat.AddChild(new Text(theme.Fg("muted", text), 1, 0));
    }

    /// <summary>The card of a call; <paramref name="fresh"/> starts a new one for a call ID an earlier turn used.</summary>
    private ToolExecutionComponent Tool(string name, string callId, JsonNode? args = null, bool fresh = false)
    {
        var existing = fresh ? null : tools.GetValueOrDefault(callId);
        if (existing is not null)
        {
            if (args is not null) existing.UpdateArgs(args);
            return existing;
        }
        var component = new ToolExecutionComponent(name, callId, args ?? new JsonObject(), toolOptions, Renderers.GetValueOrDefault(name), ui, cwd);
        component.SetExpanded(expanded);
        var group = new DurableToolGroup(component);
        group.Nested.SetExpanded(expanded);
        groups[component] = group;
        chat.AddChild(group);
        cards.Add(component);
        tools[callId] = component;
        return component;
    }
}
