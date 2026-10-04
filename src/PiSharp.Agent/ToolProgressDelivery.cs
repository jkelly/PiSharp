namespace PiSharp.Agent;

/// <summary>Explicit native producer pacing over an owned tool-progress delivery receipt.</summary>
public static class ToolProgressDelivery
{
    /// <summary>
    /// Awaits actual delivery for a single framework-owned callback, including nested prepared
    /// scopes. Ordinary source callback invocation remains immediate. Quotas are unchanged and
    /// nested forwarding charges once. Admitted source delivery is joined through abort; a late
    /// source callback remains ignored. Opaque/wrapped or multicast callbacks retain their normal
    /// delegate return semantics: this utility cannot recover an ownership receipt through them.
    /// </summary>
    public static ValueTask ReportAndWaitAsync(ToolProgressCallback callback, ToolResult partialResult,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        // Do not prevalidate the partial or check cancellation: the owned scope decides admission,
        // and closed source callbacks must ignore even invalid/canceled late reports.
        return callback.Target is ToolProgressScope scope && callback.GetInvocationList().Length == 1
            ? scope.ReportReceiptAsync(partialResult, cancellationToken)
            : callback(partialResult, cancellationToken);
    }
}
