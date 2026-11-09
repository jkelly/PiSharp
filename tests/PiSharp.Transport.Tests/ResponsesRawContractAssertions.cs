using System.Runtime.CompilerServices;
using PiSharp.AI;
using PiSharp.Contracts;

// Authored native observation of pinned v1.5.0 openai-responses.ts:251-267 error-frame settlement.
// Do not infer admission from the outer Provider kind: require the retained original native cause and no successful terminal.
internal static class ResponsesRawContractAssertions
{
    internal static StreamError Error(IEnumerable<StreamEvent> frames, Type cause, StopReason reason = StopReason.Error)
    {
        var all = frames.ToArray();
        if (all.OfType<StreamDone>().Any() || all.OfType<StreamTerminalEvent>().Count() != 1 || all.LastOrDefault() is not StreamError error ||
            error.Reason != reason || error.Message.StopReason != reason || error.NativeSourceException is not { } original || !cause.IsInstanceOfType(original))
            throw new InvalidOperationException("Expected exactly one final error frame with retained " + cause.Name + ", no success, and reason " + reason + ".");
        return error;
    }
}
internal sealed class ResponsesRawFrameObserver(IChatTransport inner, List<StreamEvent> frames) : IChatTransport
{
    public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
    {
        await foreach (var frame in inner.StreamAsync(request, token)) { frames.Add(frame); yield return frame; }
    }
}
