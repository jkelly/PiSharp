// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/modes/rpc/rpc-mode.ts ("bash": emitUserBash
// before executeBash), packages/coding-agent/src/core/agent-session.ts (_emitModelSelect, _afterToolCall image normalization)
// and packages/coding-agent/src/core/sdk.ts (convertToLlmWithBlockImages).
using PiSharp.Agent;
using PiSharp.Cli.Extensions;
using PiSharp.Tools;

namespace PiSharp.Cli.Commands;

internal sealed partial class OfflineSessionProfile
{
    /// <summary>The session's image settings (<c>images.autoResize</c>, <c>images.blockImages</c>), read per request.</summary>
    internal BuiltinToolSettings ImageSettings { get; set; } = BuiltinToolSettings.Default;

    /// <summary>Tool results' image blocks are normalized after the extension tool_result hook, as Pi does.</summary>
    private IPreparedToolHooks NormalizedToolHooks(IPreparedToolHooks? extensionHooks) =>
        new ImageNormalizingToolHooks(extensionHooks, () => ImageSettings.AutoResizeImages, PiSharp.Tools.Skia.SkiaImageCodec.Instance);

    /// <summary>Binds the native generation's session-event seams: model objects for model_select and its user_bash handlers
    /// (consulted by the user Bash host before local execution).</summary>
    private void BindSessionEventSeams(NativeExtensionActivation extension)
    {
        extension.ModelWire = descriptor => descriptor == SelectedModel ? SelectedModelWire : null;
        if (UserBash is UserBashHost host) host.Handlers = extension.HasUserBashHandlers ? [extension.UserBashHandler] : [];
    }
}
