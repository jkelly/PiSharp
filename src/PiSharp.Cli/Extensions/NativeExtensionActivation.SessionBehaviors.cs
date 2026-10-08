using PiSharp.CodingAgent;

namespace PiSharp.Cli.Extensions;

internal sealed partial class NativeExtensionActivation
{
    internal void ConfigureSessionBehaviorCompaction(Func<AgentSessionAttachment,string?,CancellationToken,Task<SessionSummaryCheckpointReceipt?>> supplier)
        =>_facadeHost.ConfigureSessionBehaviorCompaction(supplier);
}
