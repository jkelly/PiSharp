// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): coding-agent/src/core/agent-session.ts (exportToHtml builds the tool HTML renderer
// over the extension runner's renderers and the tool definitions, with the active theme and the session cwd) and
// core/export-html/tool-renderer.ts (the render context carries keyText and keyHint).
using System.Text.Json.Nodes;
using PiSharp.CodingAgent.Export;
using PiSharp.Tui.Pi;
using ExportRenderContext = PiSharp.CodingAgent.Export.ToolRenderContext;
using ExportRenderers = PiSharp.CodingAgent.Export.ToolRenderers;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>
/// The HTML export's tool renderer over the transcript's renderers: tools the page template does not draw itself are pre-rendered
/// with the renderCall/renderResult the TUI uses (extension renderers, then the built-in ones), the current theme and the configured
/// keys. Extension renderers answer asynchronously, so a first render that has no rows yet leaves the page's own rendering in place.
/// </summary>
internal static class ExportToolHtmlRenderer
{
    public static IToolHtmlRenderer Create(Func<string, ToolRenderers?> resolve, Theme theme, string cwd)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentNullException.ThrowIfNull(theme);
        return new TuiToolHtmlRenderer(new ToolHtmlRendererDependencies(name => name is null ? null : Adapt(name, resolve(name)), theme, cwd)
        {
            KeyText = KeybindingHints.KeyText,
            KeyHint = KeybindingHints.KeyHint
        });
    }

    private static ExportRenderers? Adapt(string toolName, ToolRenderers? renderers)
    {
        if (renderers is null || (renderers.RenderCall is null && renderers.RenderResult is null)) return null;
        Func<object?, object?, ExportRenderContext, IToolRenderComponent>? call = renderers.RenderCall is { } renderCall
            ? (args, theme, context) => new Rendered(renderCall(Json(args), (Theme)theme!, Context(toolName, context)))
            : null;
        Func<ToolRenderResultValue, (bool Expanded, bool IsPartial), object?, ExportRenderContext, IToolRenderComponent>? result =
            renderers.RenderResult is { } renderResult
                ? (value, options, theme, context) => new Rendered(renderResult(
                    new JsonObject { ["content"] = Json(value.Content) ?? new JsonArray(), ["details"] = Json(value.Details) },
                    new ToolRenderResultOptions(options.Expanded, options.IsPartial), (Theme)theme!, Context(toolName, context)))
                : null;
        return new(call, result);
    }

    /// <summary>A JavaScript value of the session data as the JSON the TUI renderers read (undefined is absent).</summary>
    private static JsonNode? Json(object? value) => Js.IsUndefined(value) ? null : JsonNode.Parse(Js.Stringify(value));

    private static ToolRenderContext Context(string toolName, ExportRenderContext context) => new()
    {
        Args = Json(context.Args),
        ToolCallId = context.ToolCallId,
        ToolName = toolName,
        LastComponent = (context.LastComponent as Rendered)?.Component,
        State = context.State,
        Cwd = context.Cwd,
        ExecutionStarted = context.ExecutionStarted,
        ArgsComplete = context.ArgsComplete,
        IsPartial = context.IsPartial,
        Expanded = context.Expanded,
        ShowImages = context.ShowImages,
        IsError = context.IsError,
        DurationMs = context.DurationMs,
        OutputPad = context.OutputPad,
    };

    private sealed class Rendered(IComponent component) : IToolRenderComponent
    {
        public IComponent Component { get; } = component;
        public IReadOnlyList<string> Render(int width) => Component.Render(width);
    }
}
