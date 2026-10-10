// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/extensions/mcp/tools.ts.
// The rendering parts only (createMcpToolRenderers and OUTPUT_PREVIEW_LINES); details are McpToolDetails {server, tool,
// fullOutputPath?}. Tool naming, result conversion and execution live in PiSharp.Cli/Mcp.
using System.Text.Json.Nodes;
using PiSharp.Tui.Pi;

namespace PiSharp.Cli.Interactive.Mode.Components;

internal static class McpToolRenderers
{
    /// <summary>Visual (wrapped) result lines shown before the output is expanded.</summary>
    private const int OutputPreviewLines = 5;

    /// <summary>Renderers of calls to an MCP tool, labeled <c>server/tool</c>, also used before the tool is registered.</summary>
    public static ToolRenderers CreateMcpToolRenderers(string label) => new(
        RenderCall: (args, theme, context) =>
        {
            var component = context.LastComponent as Text ?? new Text("", 0, 0);
            component.SetText(RenderUtils.FormatToolCallWithArgs(label, args, theme, context.Expanded));
            return component;
        },
        RenderResult: (result, options, theme, context) =>
        {
            var component = context.LastComponent as Container ?? new Container();
            component.Clear();
            var output = TextUtils.JsTrim(RenderUtils.GetTextOutput(result, context.ShowImages));
            if (output.Length == 0) return component;
            var color = context.IsError ? "error" : "toolOutput";
            var styled = string.Join("\n", RenderUtils.ReplaceTabs(output).Split('\n').Select(line => theme.Fg(color, line)));
            component.AddChild(new Spacer(1));
            if (options.Expanded) component.AddChild(new Text(styled, 0, 0));
            else
            {
                // Limit wrapped lines, not logical ones: MCP results are often one long JSON line.
                component.AddChild(new VisualLinePreview(new VisualLinePreviewOptions(styled, OutputPreviewLines, VisualTruncateKeep.Start,
                    hidden => theme.Fg("muted", $"... ({hidden} more lines,") + " " + KeybindingHints.KeyHint("app.tools.expand", "to expand") + theme.Fg("muted", ")"))));
                var fullOutputPath = ToolJson.Get(ToolJson.Get(result, "details"), "fullOutputPath");
                if (ToolJson.Truthy(fullOutputPath))
                    component.AddChild(new Text(theme.Fg("muted", $"Full output: {ToolJson.Template(fullOutputPath)}"), 0, 0));
            }
            return component;
        });
}
