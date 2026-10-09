// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/user-message.ts.
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>Component that renders a user message</summary>
internal sealed class UserMessageComponent : Container
{
    private const string Osc133ZoneStart = "\u001b]133;A\u0007";
    private const string Osc133ZoneEnd = "\u001b]133;B\u0007";
    private const string Osc133ZoneFinal = "\u001b]133;C\u0007";

    private readonly string text;
    private readonly MarkdownTheme markdownTheme;
    private int outputPad;
    private readonly IReadOnlyList<MarkdownTransformer> markdownTransformers;

    public UserMessageComponent(string text, MarkdownTheme? markdownTheme = null, int outputPad = 1, IReadOnlyList<MarkdownTransformer>? markdownTransformers = null)
    {
        this.text = text;
        this.markdownTheme = markdownTheme ?? Themes.GetMarkdownTheme();
        this.outputPad = outputPad;
        this.markdownTransformers = markdownTransformers ?? [];
        Rebuild();
    }

    public void SetOutputPad(int padding)
    {
        outputPad = padding;
        Rebuild();
    }

    private void Rebuild()
    {
        Clear();
        // The Markdown pads and colors its own background: a Box around it would keep a second full-width copy of every
        // line, with identical output.
        AddChild(new Markdown(
            text,
            outputPad,
            1,
            markdownTheme,
            new DefaultTextStyle(Color: content => theme.Fg("userMessageText", content), BgColor: content => theme.Bg("userMessageBg", content)),
            new MarkdownOptions(
                PreserveOrderedListMarkers: true,
                PreserveBackslashEscapes: true,
                Transform: MarkdownTransform.CreateMarkdownTransform(MarkdownMessageType.User, false, markdownTransformers))));
    }

    public override List<string> Render(int width)
    {
        var lines = base.Render(width);
        if (lines.Count == 0) return lines;

        lines[0] = Osc133ZoneStart + lines[0];
        lines[^1] = Osc133ZoneEnd + Osc133ZoneFinal + lines[^1];
        return lines;
    }
}
