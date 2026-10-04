using System.Globalization;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.ExtensionHost.Protocol;
using PiSharp.ExtensionHost.Supervision;
using PiSharp.Extensions;

namespace PiSharp.Compatibility.Node;

/// <summary>One owner handle. Active tickets borrow the registry's actual UI context; they grant no tool/process access.</summary>
public sealed class NodeExtensionUiBroker : IDisposable
{
    private readonly NodeWorkerSupervisor worker;
    private readonly string ownerId; private readonly long generation;
    private readonly object gate = new();
    private readonly Dictionary<string, IExtensionContext> contexts = new(StringComparer.Ordinal);
    private long next; private bool closed;
    public WorkerCallbackHandle Handle { get; }
    public int ActiveContexts { get { lock (gate) return contexts.Count; } }
    internal NodeExtensionUiBroker(NodeWorkerSupervisor worker, string ownerId, long generation)
    { this.worker = worker; this.ownerId = ownerId; this.generation = generation;
      Handle = worker.RegisterCallback(ownerId, generation, PublishAsync); }
    internal Lease Borrow(IExtensionContext context)
    {
        if (context.OwnerId != ownerId || context.OwnerGeneration != generation) throw new InvalidOperationException("Foreign native context.");
        lock (gate)
        {
            if (closed || contexts.Count >= 8 || next == long.MaxValue) throw new InvalidOperationException("Bridge context admission limit.");
            var id = "op" + (++next).ToString(CultureInfo.InvariantCulture); contexts.Add(id, context); return new(this, id,
                context is IExtensionUiContext ui && ui.Ui.Capabilities.Supports(ExtensionUiFeature.Notify));
        }
    }
    private async ValueTask<WorkerValue> PublishAsync(WorkerRequestContext request, CancellationToken cancellationToken)
    {
        if (request.Method != "ui.notify" || request.Value.Presence != WorkerValuePresence.Json)
            throw new InvalidOperationException("Unsupported native broker operation.");
        var value = request.Value.Json!.Value;
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 3 ||
            !value.TryGetProperty("operationId", out var operation) || operation.ValueKind != JsonValueKind.String ||
            !value.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String ||
            !value.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("Invalid notification callback.");
        IExtensionContext context;
        lock (gate) { if (closed || !contexts.TryGetValue(operation.GetString()!, out context!))
                throw new InvalidOperationException("Stale native context."); }
        if (context is not IExtensionUiContext ui || !ui.Ui.Capabilities.Supports(ExtensionUiFeature.Notify))
            throw new InvalidOperationException("Notify capability unavailable.");
        var text = message.GetString()!; if (text.Length > 65_536) throw new InvalidOperationException("Notification limit.");
        var severity = kind.GetString() switch { "info" => ExtensionUiNotifyKind.Info, "warning" => ExtensionUiNotifyKind.Warning,
            "error" => ExtensionUiNotifyKind.Error, _ => throw new InvalidOperationException("Invalid notification kind.") };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.OperationCancellationToken,
            context.SessionCancellationToken, context.ExtensionLifetimeCancellationToken);
        stop.Token.ThrowIfCancellationRequested();
        var result = await ui.Ui.PublishAsync(new ExtensionUiNotify(text, severity), stop.Token).ConfigureAwait(false);
        if (result.Kind != ExtensionUiOutcomeKind.Value || result.Value != ExtensionUiPublication.Published)
            throw new InvalidOperationException("Native notification did not publish.");
        stop.Token.ThrowIfCancellationRequested();
        return WorkerValue.FromJson(JsonData.Parse("{\"published\":true}"));
    }
    public void Dispose()
    {
        lock (gate) { if (closed) return; if (contexts.Count != 0) throw new InvalidOperationException("Native callbacks have not settled."); closed = true; }
        worker.RevokeOwner(ownerId, generation);
    }
    internal sealed class Lease(NodeExtensionUiBroker owner, string operationId, bool hasUi) : IDisposable
    {
        private int disposed;
        internal string OperationId => operationId;
        internal bool HasUi => hasUi;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) lock (owner.gate) owner.contexts.Remove(operationId); }
    }
}
