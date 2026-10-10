// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/core/extensions/types.ts.
// The rendering subset of ToolDefinition: ToolRenderResultOptions, ToolRenderContext, renderShell, renderCall, renderResult and
// ToolRenderers. Arguments and results are JSON in Pi's shapes: a result is {content:[{type:"text",text}|{type:"image",data,
// mimeType}], details}.
using System.Text.Json.Nodes;
using PiSharp.Tui.Pi;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>renderShell: "default" draws the host's box around the renderers' components; "self" lets the tool draw its own shell.</summary>
internal enum ToolRenderShell { Default, Self }

/// <param name="Expanded">Whether the result view is expanded</param>
/// <param name="IsPartial">Whether this is a partial/streaming result</param>
internal sealed record ToolRenderResultOptions(bool Expanded, bool IsPartial);

/// <summary>Context passed to tool renderers.</summary>
internal sealed class ToolRenderContext
{
    /// <summary>Current tool call arguments. Shared across call/result renders for the same tool call.</summary>
    public JsonNode? Args { get; init; }
    /// <summary>Unique id for this tool execution. Stable across call/result renders for the same tool call.</summary>
    public string ToolCallId { get; init; } = "";
    /// <summary>Invalidate just this tool execution component for redraw.</summary>
    public Action Invalidate { get; init; } = () => { };
    /// <summary>Previously returned component for this render slot, if any.</summary>
    public IComponent? LastComponent { get; init; }
    /// <summary>Shared renderer state for this tool row. Initialized by tool-execution.ts.</summary>
    public Dictionary<string, object?> State { get; init; } = new(StringComparer.Ordinal);
    /// <summary>Working directory for this tool execution.</summary>
    public string Cwd { get; init; } = "";
    /// <summary>Whether the tool execution has started.</summary>
    public bool ExecutionStarted { get; init; }
    /// <summary>Whether the tool call arguments are complete.</summary>
    public bool ArgsComplete { get; init; }
    /// <summary>Whether the tool result is partial/streaming.</summary>
    public bool IsPartial { get; init; }
    /// <summary>Whether the result view is expanded.</summary>
    public bool Expanded { get; init; }
    /// <summary>Whether inline images are currently shown in the TUI.</summary>
    public bool ShowImages { get; init; }
    /// <summary>Whether the current result is an error.</summary>
    public bool IsError { get; init; }
    /// <summary>Milliseconds the tool's execution took, from the final result; null while it runs, when it did not run, or for
    /// results stored before durations were recorded.</summary>
    public double? DurationMs { get; init; }
    /// <summary>Horizontal padding configured by the outputPad setting. Renderers with renderShell "self" apply it themselves.</summary>
    public int OutputPad { get; init; }
    /// <summary>The tool's name (not part of the source context; used by the extension renderer adapter).</summary>
    public string ToolName { get; init; } = "";
}

/// <summary>renderCall: custom rendering for the tool call display.</summary>
internal delegate IComponent ToolCallRenderer(JsonNode? args, Theme theme, ToolRenderContext context);

/// <summary>renderResult: custom rendering for the tool result display. <paramref name="result"/> is {content, details}.</summary>
internal delegate IComponent ToolResultRenderer(JsonObject result, ToolRenderResultOptions options, Theme theme, ToolRenderContext context);

/// <summary>ToolRenderers: Pick&lt;ToolDefinition, "renderShell" | "renderCall" | "renderResult"&gt;. A null slot is absent.</summary>
internal sealed record ToolRenderers(ToolRenderShell? RenderShell = null, ToolCallRenderer? RenderCall = null, ToolResultRenderer? RenderResult = null);
