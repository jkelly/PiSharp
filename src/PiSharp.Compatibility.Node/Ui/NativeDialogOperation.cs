using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.ExtensionHost.Protocol;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;

namespace PiSharp.Compatibility.Node;

// One table belongs to one actual borrowed operation. IDs remain tombstoned until operation
// retirement, so late cancel cannot address another invocation. No UI is acquired by Reserve.
internal sealed class NativeDialogOperation
{
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly ExtensionRegistry.RegisteredExtensionDialog participant;
    private Task? closing;
    private AggregateException? closingFault;
    private bool fenced;
    internal string ScopeId => participant.ScopeId;
    internal long SessionGeneration { get; }

    internal NativeDialogOperation(IExtensionContext context, string operationId)
    {
        SessionGeneration = (context as IExtensionUiContext)?.Ui.Capabilities.SessionGeneration ?? 0;
        participant = ExtensionRegistry.BindDialogContext(context, operationId,
            Fence, _ => JoinEntriesAsync());
    }
    private Task Fence()
    {
        Entry[] originals;
        lock (gate) { fenced = true; originals = entries.Values.ToArray(); }
        var faults = new List<Exception>();
        foreach (var entry in originals)
            try { lock (gate) if (entry.Retirement is null) entry.CancelOriginal ??= entry.Lifetime.RequestStop(); }
            catch (Exception error) { faults.Add(error); }
        return faults.Count == 0 ? Task.CompletedTask : Task.FromException(new AggregateException(faults));
    }
    internal WorkerValue Reserve(string id, string method)
    {
        ValidateId(id);
        if (method is not ("ui.select" or "ui.confirm" or "ui.input")) throw new InvalidOperationException("Dialog reservation method differs.");
        lock (gate)
        {
            if (fenced || closing is not null || entries.Count >= 16 || entries.ContainsKey(id))
                throw new InvalidOperationException("Dialog reservation is stale, duplicated or exhausted.");
            entries.Add(id, new(method, new NativeDialogCancellationLifetime(participant)));
        }
        return Json(new { reserved = true, dialogId = id, scopeId = ScopeId, nativeSessionGeneration = SessionGeneration });
    }
    internal WorkerValue Cancel(string id, string scope, long session)
    {
        Entry entry = Find(id, scope, session);
        lock (gate)
        {
            if (entry.Retirement is not null) throw new InvalidOperationException("Retired dialog cancellation.");
            // The exact cancellation Task is owned by this entry and joined by retirement, not
            // by this signal acknowledgement (a real cancellation handler may await that ACK).
            entry.CancelOriginal ??= entry.Lifetime.RequestStop();
            entry.AbortRequested = true;
        }
        return Json(new { cancelRequested = true, dialogId = id });
    }
    internal Task<WorkerValue> Open(string id, string scope, long session, string method,
        JsonData arguments, bool optionsPresent, CancellationToken caller)
    {
        Entry entry = Find(id, scope, session);
        lock (gate)
        {
            if (entry.Method != method || entry.OpenOriginal is not null || entry.Retirement is not null)
                throw new InvalidOperationException("Dialog open identity is stale or already consumed.");
            return entry.OpenOriginal = participant.InvokeAsync(async (fresh, callbackToken) =>
            {
                var ui = (fresh as IExtensionUiContext)?.Ui;
                if ((ui?.Capabilities.SessionGeneration ?? 0) != SessionGeneration)
                    throw new InvalidOperationException("Dialog native session generation changed.");
                using var child = CancellationTokenSource.CreateLinkedTokenSource(callbackToken, entry.Lifetime.Token);
                Task<WorkerValue>? original = null;
                try
                {
                    original = OriginalUiDialogBroker.InvokeAsync(method, arguments, optionsPresent, ui, child.Token);
                    return await original.ConfigureAwait(false);
                }
                catch (OperationCanceledException error) when (original is { IsCanceled: true } &&
                    child.Token.IsCancellationRequested &&
                    error.CancellationToken == child.Token)
                {
                    // Preserve owning callback cancellation as cancellation of the real registry
                    // wrapper. Its token is the exact ancestor that canceled our linked child.
                    if (callbackToken.IsCancellationRequested)
                        throw new OperationCanceledException("Owned dialog callback canceled.", error, callbackToken);
                    lock (gate) if (!entry.AbortRequested && entry.Retirement is null && !fenced) throw;
                    return Json(new { outcome = "cancelled", presence = "undefined" });
                }
            }, caller);
        }
    }
    internal Task<WorkerValue> Retire(string id, string scope, long session)
    {
        participant.AssertExternalObservation();
        Entry entry = Find(id, scope, session);
        entry.Lifetime.AssertExternalObservation();
        lock (gate) return entry.Retirement ??= RetireEntryAsync(entry, id);
    }
    private async Task<WorkerValue> RetireEntryAsync(Entry entry, string id)
    {
        await Task.Yield();
        var faults = new List<Exception>();
        Task? stop = null;
        try { stop = entry.Lifetime.RequestStop(); }
        catch (Exception error) { faults.Add(error); }
        // Stop every acquired original before joining a held native dialog.
        if (entry.OpenOriginal is { } open)
            try { await open.ConfigureAwait(false); }
            // The registry wrapper only cancels after proving its original canceled with its
            // exact owned linked token. Unrelated-token and task-faulted OCE stay faulted.
            catch (OperationCanceledException) when (open.IsCanceled && entry.Lifetime.Token.IsCancellationRequested) { }
            catch (Exception error) { faults.Add(participant.CaptureOwnedFailure(open, error)); }
        // Lifetime retirement itself directly awaits the same actual stop task once and
        // retains its raw aggregate. Do not reread Task.Exception through a second observer.
        Task? dispose = null;
        try { dispose = entry.Lifetime.RetireAndJoinStopAsync(); await dispose.ConfigureAwait(false); }
        catch (Exception error) { faults.Add(dispose?.Exception ?? error); }
        if (faults.Count != 0) throw new AggregateException("Dialog open/cancellation/retirement originals failed.", faults);
        return Json(new { retired = true, dialogId = id });
    }
    internal Task CloseAsync()
    {
        AssertExternalObservation();
        lock (gate) { fenced = true; return closing ??= CloseCoreAsync(); }
    }
    internal void AssertExternalObservation()
    {
        participant.AssertExternalObservation();
        Entry[] originals; lock (gate) originals = entries.Values.ToArray();
        foreach (var entry in originals) entry.Lifetime.AssertExternalObservation();
    }
    internal Exception CaptureRetirementFailure(Task original, Exception direct)
    {
        lock (gate)
        {
            if (!ReferenceEquals(original, closing)) throw new InvalidOperationException("Foreign dialog retirement original.");
            return original.IsFaulted ? closingFault ??= original.Exception! : direct;
        }
    }
    private async Task CloseCoreAsync()
    {
        await Task.Yield();
        Task? original = null;
        try { original = participant.DisposeAsync().AsTask(); await original.ConfigureAwait(false); }
        catch (Exception error) { throw new AggregateException("Dialog registry retirement failed.", original?.Exception ?? error); }
    }
    private async Task JoinEntriesAsync()
    {
        Entry[] originals;
        lock (gate) { fenced = true; originals = entries.Values.ToArray(); }
        var faults = new List<Exception>();
        var joins = new List<Task>();
        foreach (var entry in originals)
            try { lock (gate) joins.Add(entry.Retirement ??= RetireEntryAsync(entry, "owner-retirement")); }
            catch (Exception error) { faults.Add(error); }
        foreach (var original in joins)
            try { await original.ConfigureAwait(false); } catch (Exception error) { faults.Add(original.Exception ?? error); }
        if (faults.Count != 0) throw new AggregateException("All dialog entries retired with original faults.", faults);
    }
    private Entry Find(string id, string scope, long session)
    {
        ValidateId(id);
        lock (gate)
        {
            if (fenced || closing is not null || scope != ScopeId || session != SessionGeneration || !entries.TryGetValue(id, out var entry))
                throw new InvalidOperationException("Foreign, stale or fenced dialog correlation.");
            return entry;
        }
    }
    private static void ValidateId(string id)
    {
        if (id.Length is < 8 or > 32 || !id.StartsWith("dialog-", StringComparison.Ordinal) || id[7] is < '1' or > '9' ||
            id.Skip(7).Any(character => character is < '0' or > '9')) throw new InvalidOperationException("Bounded dialog identity required.");
    }
    private static WorkerValue Json<T>(T value) => WorkerValue.FromJson(JsonData.Parse(JsonSerializer.Serialize(value)));
    private sealed class Entry(string method, NativeDialogCancellationLifetime lifetime)
    {
        internal readonly string Method = method;
        internal readonly NativeDialogCancellationLifetime Lifetime = lifetime;
        internal bool AbortRequested;
        internal Task? CancelOriginal;
        internal Task<WorkerValue>? OpenOriginal, Retirement;
    }
}
