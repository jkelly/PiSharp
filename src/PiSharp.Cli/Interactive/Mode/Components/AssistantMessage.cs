// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/assistant-message.ts.
// The AssistantMessage is a JsonObject in pi-ai's shape: { role: "assistant", content: [{ type: "text", text } | { type: "thinking",
// thinking } | { type: "toolCall", ... } | ...], stopReason, errorMessage, ... }.
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
    private JsonObject? lastMessage;
    private bool hasToolCalls;
    private bool isStreaming;
    private readonly Dictionary<int, bool> thinkingVisibilityOverrides = [];

    public AssistantMessageComponent(
        JsonObject? message = null,
        bool hideThinkingBlock = false,
        MarkdownTheme? markdownTheme = null,
        string hiddenThinkingLabel = "Thinking...",
        int outputPad = 1,
        IReadOnlyList<MarkdownTransformer>? markdownTransformers = null)
    {
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
        if (lastMessage is not null) UpdateContent(lastMessage);
    }

    public override List<string> Render(int width)
    {
        var lines = base.Render(width);
        if (hasToolCalls || lines.Count == 0) return lines;

        lines[0] = Osc133ZoneStart + lines[0];
        lines[^1] = Osc133ZoneEnd + Osc133ZoneFinal + lines[^1];
        return lines;
    }

    public void UpdateContent(JsonObject message, bool? isStreaming = null)
    {
        lastMessage = message;
        this.isStreaming = isStreaming ?? this.isStreaming;

        // Clear content container
        contentContainer.Clear();

        var content = Content(message);
        var hasVisibleContent = content.Any(IsVisible);

        if (hasVisibleContent) contentContainer.AddChild(new Spacer(1));

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
                contentContainer.AddChild(new Markdown(TextUtils.JsTrim(Str(block, "text") ?? ""), outputPad, 0, markdownTheme, null,
                    new MarkdownOptions(Transform: MarkdownTransform.CreateMarkdownTransform(MarkdownMessageType.Assistant, this.isStreaming, markdownTransformers))));
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

                // Add spacing only when another visible assistant content block follows.
                // This avoids a superfluous blank line before separately-rendered tool execution blocks.
                var hasVisibleContentAfter = content.Skip(i + 1).Any(IsVisible);

                var runIndex = thinkingRunIndex++;
                var hidden = thinkingVisibilityOverrides.TryGetValue(runIndex, out var overridden) ? overridden : hideThinkingBlock;
                IComponent thinkingComponent = hidden
                    ? new Text(theme.Italic(theme.Fg("thinkingText", hiddenThinkingLabel)), outputPad, 0)
                    : new Markdown(
                        string.Join("\n\n", thinkingBlocks),
                        outputPad,
                        0,
                        markdownTheme,
                        new DefaultTextStyle(Color: text => theme.Fg("thinkingText", text), Italic: true),
                        new MarkdownOptions(Transform: MarkdownTransform.CreateMarkdownTransform(MarkdownMessageType.AssistantThinking, this.isStreaming, markdownTransformers)));
                contentContainer.AddChild(new MouseRegion(thinkingComponent, mouseEvent =>
                {
                    if (mouseEvent.Type != TuiMouseEventType.Click || mouseEvent.Button != TuiMouseButton.Left) return null;
                    thinkingVisibilityOverrides[runIndex] = !hidden;
                    if (lastMessage is not null) UpdateContent(lastMessage);
                    return new TuiMouseEventResult(Handled: true);
                }));
                if (hasVisibleContentAfter) contentContainer.AddChild(new Spacer(1));
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

    private static bool IsVisible(JsonObject? c) =>
        Str(c, "type") switch
        {
            "text" => TextUtils.JsTrim(Str(c, "text") ?? "").Length > 0,
            "thinking" => TextUtils.JsTrim(Str(c, "thinking") ?? "").Length > 0,
            _ => false
        };

    private static List<JsonObject?> Content(JsonObject message) =>
        message["content"] is JsonArray array ? [.. array.Select(node => node as JsonObject)] : [];

    private static string? Str(JsonObject? obj, string key) =>
        obj?[key] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
}
