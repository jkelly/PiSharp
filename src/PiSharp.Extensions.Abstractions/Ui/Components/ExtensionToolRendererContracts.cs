// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/extensions/types.ts
// (ToolRenderContext, ToolRenderers, ToolRendererResolver, ExtensionAPI.registerToolRenderer).
using PiSharp.Contracts;

namespace PiSharp.Extensions;

/// <summary>Source renderShell: "default" draws the host's row shell around the renderer's rows; "self" draws its own.</summary>
public enum ExtensionToolRenderShell { Default, Self }

/// <summary>
/// Native data members of the source ToolRenderContext. Components, theme, invalidation and per-row state stay in the
/// host. DurationMs is the final result's execution time: null while the tool runs, when it did not run, or for results
/// stored before durations were recorded. OutputPad is the configured horizontal padding, which renderers drawing their
/// own shell apply themselves.
/// </summary>
public sealed record ExtensionToolRenderContext(string ToolName, string ToolCallId, JsonData Arguments, string WorkingDirectory,
    bool ExecutionStarted, bool ArgumentsComplete, bool IsPartial, bool Expanded, bool ShowImages, bool IsError,
    long? DurationMs, int OutputPad);

public delegate Task<ExtensionCustomComponentRows> ExtensionToolCallRenderer(ExtensionToolRenderContext context, int width,
    CancellationToken cancellationToken);
public delegate Task<ExtensionCustomComponentRows> ExtensionToolResultRenderer(JsonData result, ExtensionToolRenderContext context,
    int width, CancellationToken cancellationToken);

/// <summary>Source ToolRenderers: the renderShell/renderCall/renderResult subset of a tool definition.</summary>
public sealed record ExtensionToolRenderers(ExtensionToolRenderShell? RenderShell = null,
    ExtensionToolCallRenderer? RenderCall = null, ExtensionToolResultRenderer? RenderResult = null);

/// <summary>
/// Chooses how calls to a tool are drawn, including tools that are not registered yet. <paramref name="next"/> returns the
/// renderers the remaining resolvers (in extension load order), then the registered tool, would use, so
/// <c>next() ?? mine</c> only fills in. Resolvers are synchronous and must not retain <paramref name="next"/>.
/// </summary>
public delegate ExtensionToolRenderers? ExtensionToolRendererResolver(string toolName, Func<ExtensionToolRenderers?> next);

public sealed record ExtensionToolRendererDescriptor(string RegistrationId, ExtensionToolRendererResolver Resolve);

/// <summary>Source pi.registerToolRenderer. Removing the registration withdraws its resolver.</summary>
public interface IExtensionToolRendererRegistry : IExtensionRegistry
{
    IExtensionRegistration RegisterToolRenderer(ExtensionToolRendererDescriptor descriptor);
}

public static class ExtensionToolRendererFeatures
{ public const string Feature = "tool-renderer-resolvers"; }
