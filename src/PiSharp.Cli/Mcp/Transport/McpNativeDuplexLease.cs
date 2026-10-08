using PiSharp.Extensions.Mcp.Transport;
using PiSharp.Tools.Processes.Mcp;

namespace PiSharp.Cli.Mcp.Transport;

/// <summary>Adapts explicit process admission; does not decide permissions, read credentials or expand environment.</summary>
public sealed class McpNativeDuplexLease(McpProcessAdmission admission) : IMcpAdmittedDuplexLease
{
    private readonly McpProcessLease _owner = new(admission);
    public Stream Input => _owner.Input;
    public Stream Output => _owner.Output;
    public byte[] CapturedStderr => _owner.CapturedStderr;
    public bool StderrTruncated => _owner.StderrTruncated;
    public Task StartAsync(CancellationToken cancellationToken) => _owner.StartAsync(cancellationToken);
    public Task CloseAsync() => _owner.CloseAsync();
}
