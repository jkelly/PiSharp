using PiSharp.CodingAgent;
using PiSharp.CodingAgent.Configuration;

namespace PiSharp.Cli.Extensions;

internal sealed partial class NativeExtensionActivation
{
    internal void ConfigureSettingsThinkingReads(Func<AgentSessionAttachment, StartupSettingsSnapshot?> capture)
        => _facadeHost.ConfigureSettingsThinkingReads(capture);
    internal void ConfigureModelSwitchThinking(Func<PiSharp.Contracts.ModelDescriptor, string?> resolve)
        => _facadeHost.ConfigureModelSwitchThinking(resolve);
}
