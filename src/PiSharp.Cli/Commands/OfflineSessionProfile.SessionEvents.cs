// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/modes/rpc/rpc-mode.ts ("bash": emitUserBash
// before executeBash) and packages/coding-agent/src/core/agent-session.ts (_emitModelSelect).
using PiSharp.Cli.Extensions;

namespace PiSharp.Cli.Commands;

internal sealed partial class OfflineSessionProfile
{
    /// <summary>Binds the native generation's session-event seams: model objects for model_select and its user_bash handlers
    /// (consulted by the user Bash host before local execution).</summary>
    private void BindSessionEventSeams(NativeExtensionActivation extension)
    {
        extension.ModelWire = descriptor => descriptor == SelectedModel ? SelectedModelWire : null;
        if (UserBash is UserBashHost host) host.Handlers = extension.HasUserBashHandlers ? [extension.UserBashHandler] : [];
    }
}
