using PiSharp.Contracts;

namespace PiSharp.Rpc.Protocol;

/// <summary>A borrowed host-owned command revision. RPC never creates executable registrations.</summary>
public interface IRpcExtensionCommandCatalog
{
    JsonData CommandCatalog { get; }
    ValueTask<JsonData> CompleteCommandAsync(string name, string prefix, CancellationToken cancellationToken);
}
