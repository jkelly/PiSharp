// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): coding-agent/src/modes/interactive/components/skill-invocation-message.ts.
// ParsedSkillBlock is declared in core/agent-session.ts; it lives here until the session port declares it.
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>A skill block parsed from message text (agent-session.ts parseSkillBlock).</summary>
internal sealed record ParsedSkillBlock(string Name, string Location, string Content, string? UserMessage);

/// <summary>
/// Component that renders a skill invocation message with collapsed/expanded state.
/// Uses same background color as custom messages for visual consistency.
/// Only renders the skill block itself - user message is rendered separately.
/// </summary>
internal sealed class SkillInvocationMessageComponent : Box
{
    private bool expanded;
    private readonly ParsedSkillBlock skillBlock;
    private readonly MarkdownTheme markdownTheme;
    private readonly IReadOnlyList<MarkdownTransformer> markdownTransformers;
    private readonly string? messageId;
    private readonly string? timestamp;

    public SkillInvocationMessageComponent(ParsedSkillBlock skillBlock, MarkdownTheme? markdownTheme = null, int outputPad = 1,
        IReadOnlyList<MarkdownTransformer>? markdownTransformers = null, string? messageId = null, string? timestamp = null)
        : base(outputPad, 1, t => theme.Bg("customMessageBg", t))
    {
        this.skillBlock = skillBlock;
        this.markdownTheme = markdownTheme ?? Themes.GetMarkdownTheme();
        this.markdownTransformers = markdownTransformers ?? [];
        this.messageId = messageId;
        this.timestamp = timestamp;
        UpdateDisplay();
    }

    public void SetExpanded(bool expanded)
    {
        this.expanded = expanded;
        UpdateDisplay();
    }

    public void SetOutputPad(int outputPad) => SetPaddingX(outputPad);

    public override void Invalidate()
    {
        base.Invalidate();
        UpdateDisplay();
    }

    private void UpdateDisplay()
    {
        Clear();
        var content = new Container();

        if (expanded)
        {
            // Expanded: label + skill name header + full content
            var label = theme.Fg("customMessageLabel", "\u001b[1m[skill]\u001b[22m");
            content.AddChild(new Text(label, 0, 0));
            var header = $"**{skillBlock.Name}**\n\n";
            var transform = MarkdownTransform.CreateMarkdownTransform(MarkdownMessageType.Custom, () => false, markdownTransformers, messageId, timestamp);
            content.AddChild(new Markdown(skillBlock.Content, 0, 0, markdownTheme, new DefaultTextStyle(Color: text => theme.Fg("customMessageText", text)),
                new MarkdownOptions(Transform: (markdown, width) => header + transform(markdown, width))));
        }
        else
        {
            // Collapsed: single line - [skill] name (hint to expand)
            var line =
                theme.Fg("customMessageLabel", "\u001b[1m[skill]\u001b[22m ") +
                theme.Fg("customMessageText", skillBlock.Name) +
                theme.Fg("dim", $" ({KeybindingHints.KeyText("app.tools.expand")} to expand)");
            content.AddChild(new Text(line, 0, 0));
        }

        AddChild(new MouseRegion(content, mouseEvent =>
        {
            if (mouseEvent.Type != TuiMouseEventType.Click || mouseEvent.Button != TuiMouseButton.Left) return null;
            SetExpanded(!expanded);
            return new TuiMouseEventResult(Handled: true);
        }));
    }
}
