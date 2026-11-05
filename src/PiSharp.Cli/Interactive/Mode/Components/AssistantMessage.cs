// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): coding-agent/src/modes/interactive/components/assistant-message.ts.
// The AssistantMessage is a JsonObject in pi-ai's shape: { role: "assistant", content: [{ type: "text", text } | { type: "thinking",
// thinking } | { type: "toolCall", ... } | ...], stopReason, errorMessage, ... }. QueueStreamingContent is PiSharp's: it keeps only
// the latest streamed update until the next render.
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>Component that renders a complete assistant message</summary>
internal sealed class AssistantMessageComponent : Container
{
    private const string Osc133ZoneStart = "\u001b]133;A\u0007";
    private const string Osc133ZoneEnd = "\u001b]133;B\u0007";
    private const string Osc133ZoneFinal = "\u001b]133;C\u0007";

    private readonly Container contentContainer;
    private bool hideThinkingBlock;
    private readonly MarkdownTheme markdownTheme;
    private string hiddenThinkingLabel;
    private int outputPad;
    private readonly IReadOnlyList<MarkdownTransformer> markdownTransformers;
    private readonly string? messageId;
    private readonly string? timestamp;
    private JsonObject? lastMessage;
    private bool hasToolCalls;
    private bool isStreaming;
    private bool contentUpdatePending;
    private readonly Dictionary<int, bool> thinkingVisibilityOverrides = [];
    /// <summary>The Markdown blocks of the last update. The next update reuses each at the same position for the same kind of content, so
    /// a streamed answer keeps the blocks it already rendered, also once the stream ends.</summary>
    private List<(string Kind, Markdown Markdown)> markdownSlots = [];

    public AssistantMessageComponent(
        JsonObject? message = null,
        bool hideThinkingBlock = false,
        MarkdownTheme? markdownTheme = null,
        string hiddenThinkingLabel = "Thinking...",
        int outputPad = 1,
        IReadOnlyList<MarkdownTransformer>? markdownTransformers = null,
        string? messageId = null,
        string? timestamp = null)
    {
        this.messageId = messageId;
        this.timestamp = timestamp;
        this.hideThinkingBlock = hideThinkingBlock;
        this.markdownTheme = markdownTheme ?? Themes.GetMarkdownTheme();
        this.hiddenThinkingLabel = hiddenThinkingLabel;
        this.outputPad = outputPad;
        this.markdownTransformers = markdownTransformers ?? [];

        // Container for text/thinking content
        contentContainer = new Container();
        AddChild(contentContainer);

        if (message is not null) UpdateContent(message);
    }

    public override void Invalidate()
    {
        base.Invalidate();
        if (lastMessage is not null) UpdateContent(lastMessage);
    }

    public void SetHideThinkingBlock(bool hide)
    {
        hideThinkingBlock = hide;
        thinkingVisibilityOverrides.Clear();
        if (lastMessage is not null) UpdateContent(lastMessage);
    }

    public void SetHiddenThinkingLabel(string label)
    {
        hiddenThinkingLabel = label;
        if (lastMessage is not null) UpdateContent(lastMessage);
    }

    public void SetOutputPad(int padding)
    {
        outputPad = padding;
        // Markdown takes its padding at construction.
        markdownSlots = [];
        if (lastMessage is not null) UpdateContent(lastMessage);
    }

    public override List<string> Render(int width)
    {
        if (contentUpdatePending && lastMessage is not null) UpdateContent(lastMessage);
        var lines = base.Render(width);
        if (hasToolCalls || lines.Count == 0) return lines;

        lines[0] = Osc133ZoneStart + lines[0];
        lines[^1] = Osc133ZoneEnd + Osc133ZoneFinal + lines[^1];
        return lines;
    }

    public void UpdateContent(JsonObject message, bool? isStreaming = null)
    {
        contentUpdatePending = false;
        lastMessage = message;
        this.isStreaming = isStreaming ?? this.isStreaming;

        // Clear content container
        contentContainer.Clear();
        var previousSlots = markdownSlots;
        markdownSlots = [];
        Markdown MarkdownBlock(string kind, string text, Func<Markdown> create)
        {
            var index = markdownSlots.Count;
            var markdown = index < previousSlots.Count && previousSlots[index].Kind == kind ? previousSlots[index].Markdown : create();
            markdown.SetText(text);
            markdownSlots.Add((kind, markdown));
            return markdown;
        }

        // Spacers are added lazily, right before the next visible content block. This puts a blank line before the first visible block
        // and after each visible thinking run that is followed by more visible content, while hidden thinking runs without a label
        // contribute nothing.
        var needsSpacer = true;
        void AddVisibleChild(IComponent child)
        {
            if (needsSpacer)
            {
                contentContainer.AddChild(new Spacer(1));
                needsSpacer = false;
            }
            contentContainer.AddChild(child);
        }

        var content = Content(message);

        // Render content in order
        var thinkingRunIndex = 0;
        for (var i = 0; i < content.Count; i++)
        {
            var block = content[i];
            var type = Str(block, "type");
            if (type == "text" && TextUtils.JsTrim(Str(block, "text") ?? "").Length > 0)
            {
                // Assistant text messages with no background - trim the text
                // Set paddingY=0 to avoid extra spacing before tool executions
                var text = TextUtils.JsTrim(Str(block, "text") ?? "");
                AddVisibleChild(MarkdownBlock("text", text, () => new Markdown(text, outputPad, 0, markdownTheme, null,
                    new MarkdownOptions(Transform: MarkdownTransform.CreateMarkdownTransform(MarkdownMessageType.Assistant, () => this.isStreaming, markdownTransformers, messageId, timestamp)))));
            }
            else if (type == "thinking")
            {
                var thinkingBlocks = new List<string>();
                for (; i < content.Count; i++)
                {
                    var thinkingContent = content[i];
                    if (Str(thinkingContent, "type") != "thinking") break;
                    var thinking = TextUtils.JsTrim(Str(thinkingContent, "thinking") ?? "");
                    if (thinking.Length > 0) thinkingBlocks.Add(thinking);
                }
                i--;

                if (thinkingBlocks.Count == 0) continue;

                var runIndex = thinkingRunIndex++;
                var hidden = thinkingVisibilityOverrides.TryGetValue(runIndex, out var overridden) ? overridden : hideThinkingBlock;
                // A hidden run with an empty label renders nothing, including no spacing.
                if (hidden && TextUtils.JsTrim(hiddenThinkingLabel).Length == 0) continue;
                var joinedThinking = string.Join("\n\n", thinkingBlocks);
                IComponent thinkingComponent = hidden
                    ? new Text(theme.Italic(theme.Fg("thinkingText", hiddenThinkingLabel)), outputPad, 0)
                    : MarkdownBlock("thinking", joinedThinking, () => new Markdown(
                        joinedThinking,
                        outputPad,
                        0,
                        markdownTheme,
                        new DefaultTextStyle(Color: text => theme.Fg("thinkingText", text), Italic: true),
                        new MarkdownOptions(Transform: MarkdownTransform.CreateMarkdownTransform(MarkdownMessageType.AssistantThinking, () => this.isStreaming, markdownTransformers, messageId, timestamp))));
                AddVisibleChild(new MouseRegion(thinkingComponent, mouseEvent =>
                {
                    if (mouseEvent.Type != TuiMouseEventType.Click || mouseEvent.Button != TuiMouseButton.Left) return null;
                    thinkingVisibilityOverrides[runIndex] = !hidden;
                    if (lastMessage is not null) UpdateContent(lastMessage);
                    return new TuiMouseEventResult(Handled: true);
                }));
                // Add spacing only when another visible assistant content block follows.
                // This avoids a superfluous blank line before separately-rendered tool execution blocks.
                needsSpacer = true;
            }
        }

        // Check if incomplete/failed - show after partial content.
        // For aborted/error tool calls, tool execution components show the error.
        // Length stops can happen before a tool call is complete, so surface them here too.
        var anyToolCalls = content.Any(c => Str(c, "type") == "toolCall");
        hasToolCalls = anyToolCalls;
        var stopReason = Str(message, "stopReason");
        var errorMessage = Str(message, "errorMessage");
        if (stopReason == "length")
        {
            contentContainer.AddChild(new Spacer(1));
            contentContainer.AddChild(new Text(theme.Fg("error", "Response was truncated before completion."), outputPad, 0));
        }
        else if (!anyToolCalls)
        {
            if (stopReason == "aborted")
            {
                var abortMessage = !string.IsNullOrEmpty(errorMessage) && errorMessage != "Request was aborted" ? errorMessage : "Operation aborted";
                contentContainer.AddChild(new Spacer(1));
                contentContainer.AddChild(new Text(theme.Fg("error", abortMessage), outputPad, 0));
            }
            else if (stopReason == "error")
            {
                var errorMsg = string.IsNullOrEmpty(errorMessage) ? "Unknown error" : errorMessage;
                contentContainer.AddChild(new Spacer(1));
                contentContainer.AddChild(new Text(theme.Fg("error", $"Error: {errorMsg}"), outputPad, 0));
            }
        }
    }

    /// <summary>Retain only the latest visual update until the TUI's scheduled render.
    /// Ordered session events and authoritative final messages are still processed immediately.</summary>
    internal void QueueStreamingContent(JsonObject message)
    {
        lastMessage = message;
        isStreaming = true;
        contentUpdatePending = true;
    }

    private static List<JsonObject?> Content(JsonObject message) =>
        message["content"] is JsonArray array ? [.. array.Select(node => node as JsonObject)] : [];

    private static string? Str(JsonObject? obj, string key) =>
        obj?[key] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
}
