// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/branch-summary-message.ts.
// The BranchSummaryMessage is a JsonObject in core/messages.ts's shape: { role: "branchSummary", summary, fromId, timestamp }.
using System.Text.Json.Nodes;
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>
/// Component that renders a branch summary message with collapsed/expanded state.
/// Uses same background color as custom messages for visual consistency.
/// </summary>
internal sealed class BranchSummaryMessageComponent : Box
{
    private bool expanded;
    private readonly JsonObject message;
    private readonly MarkdownTheme markdownTheme;

    public BranchSummaryMessageComponent(JsonObject message, MarkdownTheme? markdownTheme = null, int outputPad = 1)
        : base(outputPad, 1, t => theme.Bg("customMessageBg", t))
    {
        this.message = message;
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

        var label = theme.Fg("customMessageLabel", "\u001b[1m[branch]\u001b[22m");
        content.AddChild(new Text(label, 0, 0));
        content.AddChild(new Spacer(1));

        if (expanded)
        {
            var header = "**Branch Summary**\n\n";
            content.AddChild(new Markdown(header + (message["summary"]?.ToString() ?? "undefined"), 0, 0, markdownTheme,
                new DefaultTextStyle(Color: text => theme.Fg("customMessageText", text))));
        }
        else
        {
            content.AddChild(new Text(
                theme.Fg("customMessageText", "Branch summary (") +
                theme.Fg("dim", KeybindingHints.KeyText("app.tools.expand")) +
                theme.Fg("customMessageText", " to expand)"),
                0,
                0));
        }

        AddChild(new MouseRegion(content, mouseEvent =>
        {
            if (mouseEvent.Type != TuiMouseEventType.Click || mouseEvent.Button != TuiMouseButton.Left) return null;
            SetExpanded(!expanded);
            return new TuiMouseEventResult(Handled: true);
        }));
    }
}
