using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.ExtensionHost.Protocol;

namespace PiSharp.Compatibility.Node;

public sealed partial class NodeCommandInputExtension
{
    private async Task RetireContextAsync(string operation, Exception? primary)
    {
        BorrowedOperation? borrowed; lock (gate) contexts.TryGetValue(operation, out borrowed);
        var faults = new List<Exception>();
        Task? original = null;
        if (borrowed?.Dialogs is { } dialogs)
        {
            try { original = dialogs.CloseAsync(); await original.ConfigureAwait(false); }
            catch (Exception error)
            { faults.Add(original is null ? error : dialogs.CaptureRetirementFailure(original, error)); }
        }
        try { borrowed?.Progress?.Close(); } catch (Exception error) { faults.Add(error); }
        finally { lock (gate) contexts.Remove(operation); }
        if (faults.Count != 0)
        {
            if (primary is not null) faults.Insert(0, primary);
            throw new AggregateException("Borrowed source callback and all dialog retirement originals failed.", faults);
        }
    }
    private static async Task<WorkerValue> DispatchDialogAsync(string method, JsonElement value,
        BorrowedOperation borrowed, CancellationToken caller)
    {
        var dialogs = borrowed.Dialogs ?? throw new InvalidOperationException("Actual borrowed dialog owner is missing.");
        if (method == "ui.dialog.reserve")
        {
            Exact(value, "operationId", "dialogId", "method");
            caller.ThrowIfCancellationRequested();
            return dialogs.Reserve(value.GetProperty("dialogId").GetString()!, value.GetProperty("method").GetString()!);
        }
        if (method is "ui.dialog.cancel" or "ui.dialog.retire")
            Exact(value, "operationId", "dialogId", "scopeId", "nativeSessionGeneration");
        else Exact(value, "operationId", "dialogId", "scopeId", "nativeSessionGeneration", "suppliedArgumentsJson", "optionsPresent");
        var id = value.GetProperty("dialogId").GetString()!;
        var scope = value.GetProperty("scopeId").GetString()!;
        var session = value.GetProperty("nativeSessionGeneration").GetInt64();
        // Cancel and retire authenticate an already-owned child after parent cancellation; they
        // do not create new admission or cancel the enclosing protocol Incoming token.
        if (method == "ui.dialog.cancel") return dialogs.Cancel(id, scope, session);
        if (method == "ui.dialog.retire") return await dialogs.Retire(id, scope, session).ConfigureAwait(false);
        caller.ThrowIfCancellationRequested();
        return await dialogs.Open(id, scope, session, method,
            JsonData.Parse(value.GetProperty("suppliedArgumentsJson").GetString()!),
            value.GetProperty("optionsPresent").GetBoolean(), caller).ConfigureAwait(false);
    }
}
