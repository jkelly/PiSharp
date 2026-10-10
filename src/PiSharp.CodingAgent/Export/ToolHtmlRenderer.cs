// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/export-html/tool-renderer.ts and the ToolHtmlRenderer
// interface of core/export-html/index.ts.
using System.Text.RegularExpressions;

namespace PiSharp.CodingAgent.Export;

/// <summary>Collapsed/expanded HTML of a tool result; null members are undefined.</summary>
public sealed record ToolHtmlResult(string? Collapsed, string? Expanded);

/// <summary>
/// Renders custom tool calls and results to HTML for the export (index.ts ToolHtmlRenderer). Arguments, content and details are
/// JavaScript values as JSON.parse produced them (see <see cref="Js"/>); null results mean "no custom renderer".
/// </summary>
public interface IToolHtmlRenderer
{
    string? RenderCall(string toolCallId, string? toolName, object? args);
    ToolHtmlResult? RenderResult(string toolCallId, string toolName, object? result, object? details, bool isError);
}

/// <summary>A rendered TUI component: its ANSI lines at a width (pi-tui Component.render).</summary>
public interface IToolRenderComponent
{
    IReadOnlyList<string> Render(int width);
}

/// <summary>ToolRenderContext as the HTML export builds it.</summary>
public sealed record ToolRenderContext(object? Args, string ToolCallId, IToolRenderComponent? LastComponent, Dictionary<string, object?> State,
    string Cwd, bool ExecutionStarted, bool ArgsComplete, bool IsPartial, bool Expanded, bool ShowImages, bool IsError, double? DurationMs, int OutputPad);

/// <summary>The AgentToolResult handed to renderResult.</summary>
public sealed record ToolRenderResultValue(object? Content, object? Details, bool IsError);

/// <summary>A tool's renderers as resolved by extensions and the registered tool (ToolRenderers); the theme is opaque here.</summary>
public sealed record ToolRenderers(
    Func<object?, object?, ToolRenderContext, IToolRenderComponent>? RenderCall,
    Func<ToolRenderResultValue, (bool Expanded, bool IsPartial), object?, ToolRenderContext, IToolRenderComponent>? RenderResult);

/// <summary>createToolHtmlRenderer dependencies; IMPL-I supplies TUI renderers and the active theme.</summary>
public sealed record ToolHtmlRendererDependencies(Func<string?, ToolRenderers?> GetToolRenderers, object? Theme, string Cwd, int Width = 100);

/// <summary>createToolHtmlRenderer: runs a tool's TUI renderers and converts their ANSI lines to HTML.</summary>
public sealed class TuiToolHtmlRenderer : IToolHtmlRenderer
{
    private static readonly Regex AnsiEscape = new(@"\x1b\[[0-9;]*m", RegexOptions.CultureInvariant);
    private readonly ToolHtmlRendererDependencies deps;
    private readonly Dictionary<string, IToolRenderComponent> callComponents = new(StringComparer.Ordinal), resultComponents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, object?>> states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> args = new(StringComparer.Ordinal);

    public TuiToolHtmlRenderer(ToolHtmlRendererDependencies dependencies)
    { ArgumentNullException.ThrowIfNull(dependencies); deps = dependencies; }

    private static bool IsBlank(string line) => Js.Trim(AnsiEscape.Replace(line, "")).Length == 0;

    private static IReadOnlyList<string> TrimRenderedResultLines(IReadOnlyList<string> lines)
    {
        int start = 0, end = lines.Count;
        while (start < end && IsBlank(lines[start])) start++;
        while (end > start && IsBlank(lines[end - 1])) end--;
        return lines.Skip(start).Take(end - start).ToList();
    }

    private Dictionary<string, object?> State(string toolCallId)
    {
        if (!states.TryGetValue(toolCallId, out var state)) states[toolCallId] = state = [];
        return state;
    }

    private ToolRenderContext Context(string toolCallId, IToolRenderComponent? last, bool expanded, bool isPartial, bool isError) =>
        new(args.TryGetValue(toolCallId, out var value) ? value : Js.Undefined, toolCallId, last, State(toolCallId), deps.Cwd, true, true, isPartial, expanded,
            false, isError, null, 1);

    public string? RenderCall(string toolCallId, string? toolName, object? arguments)
    {
        try
        {
            args[toolCallId] = arguments;
            var renderers = deps.GetToolRenderers(toolName);
            if (renderers?.RenderCall is null) return null;
            var component = renderers.RenderCall(arguments, deps.Theme,
                Context(toolCallId, callComponents.TryGetValue(toolCallId, out var last) ? last : null, false, true, false));
            callComponents[toolCallId] = component;
            return AnsiToHtml.LinesToHtml(component.Render(deps.Width));
        }
        catch { return null; }
    }

    public ToolHtmlResult? RenderResult(string toolCallId, string toolName, object? result, object? details, bool isError)
    {
        try
        {
            var renderers = deps.GetToolRenderers(toolName);
            if (renderers?.RenderResult is null) return null;
            var value = new ToolRenderResultValue(result, details, isError);
            var collapsedComponent = renderers.RenderResult(value, (false, false), deps.Theme,
                Context(toolCallId, resultComponents.TryGetValue(toolCallId, out var last) ? last : null, false, false, isError));
            resultComponents[toolCallId] = collapsedComponent;
            var collapsed = AnsiToHtml.LinesToHtml(TrimRenderedResultLines(collapsedComponent.Render(deps.Width)));
            var expandedComponent = renderers.RenderResult(value, (true, false), deps.Theme, Context(toolCallId, resultComponents[toolCallId], true, false, isError));
            resultComponents[toolCallId] = expandedComponent;
            var expanded = AnsiToHtml.LinesToHtml(TrimRenderedResultLines(expandedComponent.Render(deps.Width)));
            return new(collapsed.Length > 0 && collapsed != expanded ? collapsed : null, expanded);
        }
        catch { return null; }
    }
}
