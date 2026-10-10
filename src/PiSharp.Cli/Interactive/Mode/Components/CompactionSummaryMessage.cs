// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/compaction-summary-message.ts.
// The CompactionSummaryMessage is a JsonObject in core/messages.ts's shape: { role: "compactionSummary", summary, tokensBefore,
// timestamp }. Number.prototype.toLocaleString() is rendered with en-US grouping (Node's default ICU locale).
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>
/// Component that renders a compaction message with collapsed/expanded state.
/// Uses same background color as custom messages for visual consistency.
/// </summary>
internal sealed class CompactionSummaryMessageComponent : Box
{
    private bool expanded;
    private readonly JsonObject message;
    private readonly MarkdownTheme markdownTheme;

    public CompactionSummaryMessageComponent(JsonObject message, MarkdownTheme? markdownTheme = null, int outputPad = 1)
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

        var tokenStr = ToLocaleString(message["tokensBefore"]);
        var label = theme.Fg("customMessageLabel", "\u001b[1m[compaction]\u001b[22m");
        content.AddChild(new Text(label, 0, 0));
        content.AddChild(new Spacer(1));

        if (expanded)
        {
            var header = $"**Compacted from {tokenStr} tokens**\n\n";
            content.AddChild(new Markdown(header + (message["summary"]?.ToString() ?? "undefined"), 0, 0, markdownTheme,
                new DefaultTextStyle(Color: text => theme.Fg("customMessageText", text))));
        }
        else
        {
            content.AddChild(new Text(
                theme.Fg("customMessageText", $"Compacted from {tokenStr} tokens (") +
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

    // Number.prototype.toLocaleString() in en-US: grouped integer part, at most three fraction digits.
    private static string ToLocaleString(JsonNode? node)
    {
        if (node is JsonValue value && value.GetValueKind() == JsonValueKind.Number)
        {
            var number = double.Parse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);
            if (double.IsNaN(number)) return "NaN";
            if (double.IsInfinity(number)) return number > 0 ? "∞" : "-∞";
            return number.ToString("#,##0.###", CultureInfo.GetCultureInfo("en-US"));
        }
        return node?.ToString() ?? "undefined";
    }
}
