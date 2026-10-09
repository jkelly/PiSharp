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

    /// <summary>The current session as a virtual selection's routing target (IMPL-E seam: hosts that admit registerVirtualModel route
    /// through <see cref="PiSharp.Cli.Models.VirtualModelRoutingTransport"/> with this session).</summary>
    internal PiSharp.Cli.Models.IVirtualModelSession? CurrentVirtualModelSession() =>
        Sessions?.Current.Session is { } session ? new VirtualSession(session) : null;
    private sealed class VirtualSession(PiSharp.CodingAgent.PersistentAgentSession session) : PiSharp.Cli.Models.IVirtualModelSession
    {
        public IReadOnlyList<PiSharp.Sessions.Serialization.SessionEntry> Branch => session.Snapshot.Context.Ancestry;
        public Task AppendStateAsync(PiSharp.Contracts.JsonData data, CancellationToken cancellationToken) =>
            session.AppendRunCustomEntryAsync(PiSharp.Cli.Models.VirtualModels.StateEntry, data, cancellationToken);
    }

    /// <summary>Binds the native generation's session-event seams: model objects for model_select and its user_bash handlers
    /// (consulted by the user Bash host before local execution).</summary>
    private void BindSessionEventSeams(NativeExtensionActivation extension)
    {
        extension.ModelWire = descriptor => descriptor == SelectedModel ? SelectedModelWire : _liveModels?.Wire(descriptor);
        if (UserBash is UserBashHost host) host.Handlers = extension.HasUserBashHandlers ? [extension.UserBashHandler] : [];
    }
}
