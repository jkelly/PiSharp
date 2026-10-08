using PiSharp.CodingAgent;

namespace PiSharp.Rpc.Protocol;

public sealed partial class RpcSessionDispatcher
{
    private async ValueTask ObserveMetadataOrOperationAsync(SessionOperationEvent observation)
    {
        if (observation is not SessionInfoChanged changed)
        {
            await ObserveOperationAsync(observation).ConfigureAwait(false); return;
        }
        var previous = _inCallback.Value; _inCallback.Value = true;
        try
        {
            await WriteAsync(RpcCommandCodec.Event("session_info_changed", writer =>
            {
                if (changed.Name is not null) writer.WriteString("name", changed.Name);
            }, _options)).ConfigureAwait(false);
        }
        catch (Exception error)
        { SignalFatal(error is RpcDispatchException dispatch ? dispatch.Failure : RpcDispatchFailure.SessionRunFailed, error); throw; }
        finally { _inCallback.Value = previous; }
    }
}
