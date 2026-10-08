using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Facade.Context;

namespace PiSharp.Cli.Extensions;

public sealed partial class NativeExtensionContextFacadeHost : IExtensionSystemPromptOptionsReadHost
{
    private Func<AgentSessionAttachment, JsonData>? originalPromptCapture;
    internal void ConfigureOriginalSystemPromptReads(Func<AgentSessionAttachment, JsonData> capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (capture.GetInvocationList().Length != 1 || Volatile.Read(ref owner) is not null)
            throw new InvalidOperationException("One original prompt capture must be bound before attachment.");
        if (Interlocked.CompareExchange(ref originalPromptCapture, capture, null) is not null)
            throw new InvalidOperationException("Original prompt capture is already bound.");
    }
    public JsonData GetSystemPromptOptions(IExtensionContext context) => Read(context, attached =>
        JsonData.FromElement((Volatile.Read(ref originalPromptCapture) ??
            throw new NotSupportedException("No actual original prompt inputs have been admitted."))(attached).Value));
}
internal sealed partial class NativeExtensionActivation
{
    internal void ConfigureOriginalSystemPromptReads(Func<AgentSessionAttachment, JsonData> capture)
        => _facadeHost.ConfigureOriginalSystemPromptReads(capture);
}
