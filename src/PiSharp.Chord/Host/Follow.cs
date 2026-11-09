// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/host/follow.ts
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PiSharp.Chord.Data;
using PiSharp.Chord.Delta;
using PiSharp.Chord.State;

namespace PiSharp.Chord.Host;

internal sealed class FollowSource(Attempt attempt, FollowAttach attach) : Source(attempt.Owner.Host.Batch), IFollowObserver
{
    private readonly List<WatchCall<JsonNode?>> waiting = [];
    private JsonNode? value, previous;
    private bool ready, detached, framed, retired;
    private WatchEnd? ended;
    private Action? release;
    private bool attachStarted;
    private IDisposable? lifetime = attempt.Owner.Host.RetainCallback(attach, attempt.Served.DeclaringFacet);
    private IDisposable? detachLifetime;
    private void ReleaseLifetime()
    {
        Interlocked.Exchange(ref detachLifetime, null)?.Dispose();
        Interlocked.Exchange(ref lifetime, null)?.Dispose();
    }
    private void DetachAttachment(Action detach)
    {
        try { using var lease = attempt.Owner.Host.RetainCallback(detach, attempt.Served.DeclaringFacet); detach(); }
        finally { ReleaseLifetime(); }
    }
    public override JsonNode? Value => ready ? value : throw HostErrors.NotReady("follow");
    internal async Task AttachAsync()
    {
        if (detached) { ReleaseLifetime(); return; }
        attachStarted = true;
        FollowAttachment? attachment;
        try
        {
            attachment = await attempt.Owner.Host.InvokeCallbackAsync(attach, () => attach(this, attempt.Self.Context), attempt.Served.DeclaringFacet);
            if (attachment is not null) detachLifetime = attempt.Owner.Host.RetainCallback(attachment.Detach, attempt.Served.DeclaringFacet);
        }
        catch { ReleaseLifetime(); throw; }
        if (attachment is null) { ReleaseLifetime(); throw new ChordError("unavailable", "followed source is absent"); }
        if (detached) { DetachAttachment(attachment.Detach); return; }
        release = attachment.Detach; value = attachment.Value; ready = true;
        if (ended is not null)
        {
            DetachAttachment(attachment.Detach); release = null;
            throw new ChordError("unavailable", "followed source ended during activation");
        }
        var pending = waiting.ToArray(); waiting.Clear();
        foreach (var call in pending) Apply(call);
    }
    public void Advance(WatchCall<JsonNode?> call, ChordContext context) => attempt.Owner.Host.Dispatcher.Invoke(() =>
    {
        if (detached || ended is not null) return;
        if (!ready) { waiting.Add(call); return; }
        if (Batch.Running) Apply(call); else Batch.Soon(() => Apply(call), context);
    });
    private void Apply(WatchCall<JsonNode?> call)
    {
        if (detached) return;
        if (call is WatchFailedCall<JsonNode?> failed)
        {
            attempt.Owner.Failed(attempt, new(FailureKind.Transient, DataInternal.AsChordError(failed.Error, attempt.Owner.Label)));
            return;
        }
        if (call is not WatchValueCall<JsonNode?> change) return;
        if (!framed) { previous = value; framed = true; }
        value = change.Value; retired |= change.Retired; Changed();
    }
    public void End(WatchEnd end) => attempt.Owner.Host.Dispatcher.Invoke(() =>
    {
        if (detached || ended is not null) return;
        ended = end;
        if (!ready) return;
        if (end.Kind == WatchEndKind.Failed || !attempt.Live)
            attempt.Owner.Failed(attempt, new(FailureKind.Transient, DataInternal.AsChordError(end.Error ?? new Exception("followed source ended"), attempt.Owner.Label)));
        else EndWatchesAfterDelivery();
    });
    public override void Deliver(ChordContext context)
    {
        if (!framed || detached) return;
        framed = false;
        var ops = DeltaDiff.DiffRevisions(previous, value);
        if (ops.Length > 0 || retired) Publish(value, ops, retired, context);
        previous = null; retired = false;
    }
    internal void Detach()
    {
        if (detached) return;
        detached = true; var finish = release; release = null; waiting.Clear();
        try
        {
            if (finish is not null) DetachAttachment(finish);
            else if (!attachStarted) ReleaseLifetime();
        }
        finally { EndWatches(); }
    }
}
