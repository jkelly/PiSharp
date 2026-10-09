// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/core/tools/renderers/index.ts.
// Built-in tool renderers, without the tools themselves, plus the renderers the built-in tool definitions carry (edit draws its
// own shell, codemode and MCP tools have their own renderers) and an adapter for extension renderers (async rows).
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Tui.Pi;

namespace PiSharp.Cli.Interactive.Mode.Components;

internal static class BuiltInToolRenderers
{
    /// <summary>Renderers for every built-in tool, keyed by tool name.</summary>
    public static IReadOnlyDictionary<string, ToolRenderers> CreateAllToolRenderers() => new Dictionary<string, ToolRenderers>(StringComparer.Ordinal)
    {
        ["read"] = ReadRenderers.Renderers,
        ["bash"] = BashRenderers.CreateShellRenderers("$"),
        ["powershell"] = BashRenderers.CreateShellRenderers("PS>"),
        ["edit"] = EditRenderers.Renderers,
        ["write"] = WriteRenderers.Renderers,
        ["grep"] = GrepRenderers.Renderers,
        ["find"] = FindRenderers.Renderers,
        ["ls"] = LsRenderers.Renderers,
    };

    /// <summary>
    /// Merge built-in renderers into a tool definition that does not supply its own.
    /// ToolExecutionComponent used to do this lookup itself; callers do it now.
    /// </summary>
    public static ToolRenderers? WithBuiltInRenderers(string toolName, ToolRenderers? definition)
    {
        var builtIn = CreateAllToolRenderers().GetValueOrDefault(toolName);
        if (definition is null) return builtIn;
        if (builtIn is null) return definition;
        return definition with
        {
            RenderCall = definition.RenderCall ?? builtIn.RenderCall,
            RenderResult = definition.RenderResult ?? builtIn.RenderResult,
        };
    }

    /// <summary>
    /// The renderers a registered built-in tool definition carries: the built-in tools (edit.ts adds renderShell "self"), the
    /// codemode tool and MCP tools (<c>mcp__server__tool</c>, labeled <paramref name="mcpLabel"/> or <c>server/tool</c>).
    /// </summary>
    public static ToolRenderers? ForToolDefinition(string toolName, string? mcpLabel = null)
    {
        if (toolName == "edit") return EditRenderers.Renderers with { RenderShell = ToolRenderShell.Self };
        if (CreateAllToolRenderers().TryGetValue(toolName, out var builtIn)) return builtIn;
        if (toolName == CodemodeRenderers.ToolName) return CodemodeRenderers.Renderers;
        if (mcpLabel is not null) return McpToolRenderers.CreateMcpToolRenderers(mcpLabel);
        if (toolName.StartsWith("mcp__", StringComparison.Ordinal))
        {
            var rest = toolName["mcp__".Length..];
            var split = rest.IndexOf("__", StringComparison.Ordinal);
            if (split > 0) return McpToolRenderers.CreateMcpToolRenderers(rest[..split] + "/" + rest[(split + 2)..]);
        }
        return null;
    }

    /// <summary>
    /// The renderers for a tool row: an extension's renderers (resolved by the host's resolver chain) win, a registered definition's
    /// missing slots fall back to the built-in renderers, and unregistered built-in names use the built-in renderers.
    /// </summary>
    public static ToolRenderers? Resolve(string toolName, ToolRenderers? definition = null, ExtensionToolRenderers? extensionRenderers = null)
    {
        if (extensionRenderers is not null)
            return WithBuiltInRenderers(toolName, ExtensionToolRendererAdapter.ToToolRenderers(extensionRenderers));
        return WithBuiltInRenderers(toolName, definition ?? ForToolDefinition(toolName));
    }
}

/// <summary>
/// Adapts <see cref="ExtensionToolRenderers"/> (async, width-dependent rows) to <see cref="ToolRenderers"/>. Each slot is an
/// <see cref="ExtensionRowsComponent"/> that shows the latest completed rows, requests rows for a new width or context, and calls
/// the context's invalidate when they arrive. A failed request throws on the next render call so the row falls back to the
/// generic rendering, like a throwing source renderer.
/// </summary>
internal static class ExtensionToolRendererAdapter
{
    public static ToolRenderers ToToolRenderers(ExtensionToolRenderers renderers)
    {
        ToolCallRenderer? call = renderers.RenderCall is { } renderCall
            ? (args, _, context) => Slot(context, "call", "call", (extensionContext, width, token) => renderCall(extensionContext, width, token))
            : null;
        ToolResultRenderer? result = renderers.RenderResult is { } renderResult
            ? (value, options, _, context) =>
            {
                var json = value.ToJsonString();
                return Slot(context, "result", "result:" + options.Expanded + ":" + options.IsPartial + ":" + json,
                    (extensionContext, width, token) => renderResult(JsonData.Parse(json), extensionContext, width, token));
            }
            : null;
        ToolRenderShell? shell = renderers.RenderShell switch
        {
            ExtensionToolRenderShell.Self => ToolRenderShell.Self,
            ExtensionToolRenderShell.Default => ToolRenderShell.Default,
            _ => null
        };
        return new ToolRenderers(shell, call, result);
    }

    private static IComponent Slot(ToolRenderContext context, string slot, string slotKey,
        Func<ExtensionToolRenderContext, int, CancellationToken, Task<ExtensionCustomComponentRows>> render)
    {
        var argsJson = context.Args?.ToJsonString() ?? "null";
        var extensionContext = new ExtensionToolRenderContext(context.ToolName, context.ToolCallId, JsonData.Parse(argsJson), context.Cwd,
            context.ExecutionStarted, context.ArgsComplete, context.IsPartial, context.Expanded, context.ShowImages, context.IsError,
            context.DurationMs is { } duration ? (long)duration : null, context.OutputPad);
        var key = new StringBuilder().Append(slotKey).Append('\u0000').Append(argsJson).Append('\u0000').Append(context.Cwd).Append('\u0000')
            .Append(context.ExecutionStarted).Append(context.ArgsComplete).Append(context.IsPartial).Append(context.Expanded)
            .Append(context.ShowImages).Append(context.IsError).Append('\u0000').Append(context.DurationMs).Append('\u0000').Append(context.OutputPad)
            .ToString();
        // Kept in the row state too, so a failure survives the fallback rendering that replaces the slot component.
        var stateKey = "extensionRows:" + slot;
        var component = context.LastComponent as ExtensionRowsComponent ?? context.State.GetValueOrDefault(stateKey) as ExtensionRowsComponent ?? new ExtensionRowsComponent();
        context.State[stateKey] = component;
        component.Update(key, width => token => render(extensionContext, width, token), context.Invalidate);
        if (component.FailedKey == key) throw new InvalidOperationException("The extension tool renderer failed.");
        return component;
    }
}

/// <summary>Rows produced asynchronously by an extension renderer for the latest context and width.</summary>
internal sealed class ExtensionRowsComponent : IComponent
{
    private readonly object gate = new();
    private string key = "";
    private Func<int, Func<CancellationToken, Task<ExtensionCustomComponentRows>>>? render;
    private Action invalidate = () => { };
    private (string Key, int Width, List<string> Lines)? completed;
    private (string Key, int Width, CancellationTokenSource Cancel)? pending;
    private readonly SynchronizationContext? context = SynchronizationContext.Current;

    public string? FailedKey { get; private set; }

    public void Update(string key, Func<int, Func<CancellationToken, Task<ExtensionCustomComponentRows>>> render, Action invalidate)
    {
        lock (gate)
        {
            this.render = render;
            this.invalidate = invalidate;
            if (this.key == key) return;
            this.key = key;
            if (pending is { } current) { current.Cancel.Cancel(); pending = null; }
        }
    }

    public void Invalidate() { }

    public List<string> Render(int width)
    {
        List<string>? lines;
        lock (gate)
        {
            if (completed is { } done && done.Key == key && done.Width == width) return done.Lines;
            if (FailedKey != key && render is not null && (pending is not { } current || current.Key != key || current.Width != width))
            {
                pending?.Cancel.Cancel();
                var cancel = new CancellationTokenSource();
                pending = (key, width, cancel);
                _ = RequestAsync(key, width, render(width), cancel);
            }
            lines = completed?.Lines;
        }
        return lines is null ? [] : lines.Select(line => TextUtils.VisibleWidth(line) > width ? TextUtils.TruncateToWidth(line, width, "") : line).ToList();
    }

    private async Task RequestAsync(string requestKey, int width, Func<CancellationToken, Task<ExtensionCustomComponentRows>> request, CancellationTokenSource cancel)
    {
        ExtensionCustomComponentRows rows;
        try { rows = await Task.Run(() => request(cancel.Token), cancel.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { return; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            lock (gate)
            {
                if (pending is not { } current || current.Cancel != cancel) return;
                pending = null;
                FailedKey = requestKey;
            }
            Notify();
            return;
        }
        lock (gate)
        {
            if (pending is not { } current || current.Cancel != cancel) return;
            pending = null;
            completed = (requestKey, width, rows.Rows.IsDefault ? [] : rows.Rows.Select(line => TextUtils.VisibleWidth(line) > width ? TextUtils.TruncateToWidth(line, width, "") : line).ToList());
        }
        Notify();
    }

    private void Notify()
    {
        Action callback;
        lock (gate) callback = invalidate;
        if (context is not null) context.Post(_ => callback(), null);
        else callback();
    }
}
