// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/telemetry-ingest/sender.ts.
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Chord.Host;

namespace PiSharp.Chord.TelemetryIngest;

public static class TelemetrySenders
{
    public static TelemetrySender SendPageTelemetry(ChordHost host, TelemetryRecorder recorder, ChordContext context) =>
        Create(host.Hold(TelemetryIngest.PageTelemetry, new(), context), recorder, false);
    public static TelemetrySender SendWorkerTelemetry(ChordHost host, TelemetryRecorder recorder, ChordContext context) =>
        Create(host.Hold(TelemetryIngest.WorkerTelemetry, new(), context), recorder, true);
    private static TelemetrySender Create(IReference held, TelemetryRecorder recorder, bool worker) =>
        new(recorder, worker, batch =>
        {
            if (held.State.Value.Kind != "live" || (worker && held.Variant != "worker")) return null;
            var arguments = new JsonObject { ["spans"] = JsonSerializer.SerializeToNode(batch.Spans) };
            if (worker) arguments["metrics"] = JsonSerializer.SerializeToNode(batch.Metrics.Take(TelemetryIngest.WorkerBatchRows).ToArray());
            return held.CallAsync("ingest", arguments, ChordContext.Background);
        }, held.Release);
}

/// <summary>Serializes best-effort sends. A null transport task means unbound, so the batch is requeued.</summary>
public sealed class TelemetrySender
{
    private readonly object gate = new();
    private readonly TelemetryRecorder recorder;
    private readonly bool worker;
    private readonly Func<TelemetryBatch, Task?> send;
    private readonly Action release;
    private Task sending = Task.CompletedTask;
    private bool stopped;
    /// <summary>The transport is invoked only with an owned batch. Returning null retains the batch for a later send.</summary>
    public TelemetrySender(TelemetryRecorder recorder, bool worker, Func<TelemetryBatch, Task?> send, Action? release = null)
    {
        ArgumentNullException.ThrowIfNull(recorder); ArgumentNullException.ThrowIfNull(send);
        this.recorder = recorder; this.worker = worker; this.send = send; this.release = release ?? (() => { });
    }
    public Task SendAsync(bool partial = false)
    {
        lock (gate)
        {
            if (stopped) return Task.CompletedTask;
            return sending = RunAfter(sending, partial);
        }
    }
    private async Task RunAfter(Task previous, bool partial)
    {
        await previous.ConfigureAwait(false);
        lock (gate) if (stopped) return;
        if (!(worker ? recorder.Pending(partial) : recorder.QueuedSpans > 0)) return;
        var batch = recorder.Take(partial, worker ? TelemetryIngest.WorkerBatchRows : TelemetryIngest.PageBatchSpans);
        try
        {
            var payload = new TelemetryBatch(batch.Spans,
                worker ? batch.Metrics.Take(TelemetryIngest.WorkerBatchRows).ToArray() : []);
            var task = send(payload);
            if (task is null)
            {
                recorder.Release();
                recorder.Ingest(batch);
                return;
            }
            await task.ConfigureAwait(false);
        }
        catch { /* A refused, failed, or lost batch is deliberately given up. */ }
        finally { recorder.Release(); }
    }
    public void Stop()
    {
        lock (gate) { if (stopped) return; stopped = true; }
        release();
    }
}
