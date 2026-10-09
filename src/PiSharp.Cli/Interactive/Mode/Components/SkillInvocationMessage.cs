// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/skill-invocation-message.ts.
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

    public SkillInvocationMessageComponent(ParsedSkillBlock skillBlock, MarkdownTheme? markdownTheme = null, int outputPad = 1)
        : base(outputPad, 1, t => theme.Bg("customMessageBg", t))
    {
        this.skillBlock = skillBlock;
        this.markdownTheme = markdownTheme ?? Themes.GetMarkdownTheme();
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
            content.AddChild(new Markdown(header + skillBlock.Content, 0, 0, markdownTheme, new DefaultTextStyle(Color: text => theme.Fg("customMessageText", text))));
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
