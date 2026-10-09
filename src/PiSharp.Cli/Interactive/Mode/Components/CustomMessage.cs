// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/custom-message.ts.
// The CustomMessage is a JsonObject in core/messages.ts's shape: { role: "custom", customType, content: string | (TextContent |
// ImageContent)[], display, details?, timestamp }. MessageRenderOptions and MessageRenderer are declared in core/extensions/types.ts;
// they live here until the extension runner port declares them.
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <param name="Expanded">Whether the message is expanded.</param>
/// <param name="OutputPad">Horizontal padding configured by the outputPad setting.</param>
internal sealed record MessageRenderOptions(bool Expanded, int OutputPad);

/// <summary>An extension's renderer for a custom message type (registerMessageRenderer); null falls back to the default rendering.</summary>
internal delegate IComponent? MessageRenderer(JsonObject message, MessageRenderOptions options, Theme theme);

/// <summary>
/// Component that renders a custom message entry from extensions.
/// Uses distinct styling to differentiate from user messages.
/// </summary>
internal sealed class CustomMessageComponent : Container
{
    private readonly JsonObject message;
    private readonly MessageRenderer? customRenderer;
    private readonly Box box;
    private IComponent? customComponent;
    private readonly MarkdownTheme markdownTheme;
    private bool expanded;
    private int outputPad;

    public CustomMessageComponent(JsonObject message, MessageRenderer? customRenderer = null, MarkdownTheme? markdownTheme = null, int outputPad = 1)
    {
        this.message = message;
        this.customRenderer = customRenderer;
        this.markdownTheme = markdownTheme ?? Themes.GetMarkdownTheme();
        this.outputPad = outputPad;

        AddChild(new Spacer(1));

        // Create box with purple background (used for default rendering)
        box = new Box(1, 1, t => theme.Bg("customMessageBg", t));

        Rebuild();
    }

    public void SetExpanded(bool expanded)
    {
        if (this.expanded != expanded)
        {
            this.expanded = expanded;
            Rebuild();
        }
    }

    public void SetOutputPad(int outputPad)
    {
        if (this.outputPad != outputPad)
        {
            this.outputPad = outputPad;
            Rebuild();
        }
    }

    public override void Invalidate()
    {
        base.Invalidate();
        Rebuild();
    }

    private void Rebuild()
    {
        // Remove previous content component
        if (customComponent is not null)
        {
            RemoveChild(customComponent);
            customComponent = null;
        }
        RemoveChild(box);

        // Try custom renderer first - it handles its own styling
        if (customRenderer is not null)
        {
            try
            {
                var component = customRenderer(message, new MessageRenderOptions(expanded, outputPad), theme);
                if (component is not null)
                {
                    // Custom renderer provides its own styled component
                    customComponent = component;
                    AddChild(component);
                    return;
                }
            }
            catch
            {
                // Fall through to default rendering
            }
        }

        // Default rendering uses our box
        AddChild(box);
        box.Clear();
        box.SetPaddingX(outputPad);

        // Default rendering: label + content
        var label = theme.Fg("customMessageLabel", $"\u001b[1m[{message["customType"]?.ToString() ?? "undefined"}]\u001b[22m");
        box.AddChild(new Text(label, 0, 0));
        box.AddChild(new Spacer(1));

        // Extract text content
        string text;
        if (message["content"] is JsonValue value && value.GetValueKind() == JsonValueKind.String)
        {
            text = value.GetValue<string>();
        }
        else
        {
            text = string.Join("\n", (message["content"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .Where(c => Str(c, "type") == "text")
                .Select(c => Str(c, "text") ?? ""));
        }

        box.AddChild(new Markdown(text, 0, 0, markdownTheme, new DefaultTextStyle(Color: t => theme.Fg("customMessageText", t))));
    }

    private static string? Str(JsonObject? obj, string key) =>
        obj?[key] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
}
