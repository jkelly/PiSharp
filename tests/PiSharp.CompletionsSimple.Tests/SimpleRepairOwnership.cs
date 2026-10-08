using System.Text.Json;

internal sealed record SimpleRepairOriginalTask(string Operation, int TaskId, string Status);
internal sealed record SimpleRepairProgress(string CaseId, string Kind, string Operation, string Status,
    bool Incomplete, bool Nonpassing, IReadOnlyList<SimpleRepairOriginalTask> RetainedOriginalTasks,
    IReadOnlyList<object> Observations, string? Error);

/// <summary>A diagnostic deadline never discharges the actual task or its owning scopes.</summary>
internal sealed class SimpleRepairOwnership(string caseId, Func<SimpleRepairProgress, Task> persist,
    TimeSpan? diagnosticDeadline = null) : List<object>
{
    private readonly object gate = new();
    private readonly Dictionary<Task, string> retained = [];
    private readonly TimeSpan joinDeadline = diagnosticDeadline ?? TimeSpan.FromSeconds(5);
    private bool exceeded;
    private Exception? journalFailure;
    internal bool StopLaterCases { get { lock (gate) return exceeded || journalFailure is not null; } }
    internal bool DiagnosticDeadlineExceeded { get { lock (gate) return exceeded; } }
    internal Exception? JournalFailure { get { lock (gate) return journalFailure; } }
    internal int RetainedOriginalTaskCount { get { lock (gate) return retained.Count; } }
    internal string CaseId => caseId;

    public new void Add(object value) { lock (gate) base.Add(value); }
    internal object[] Snapshot() { lock (gate) return base.ToArray(); }
    internal Exception? CancelOwner(CancellationTokenSource cancellation, string operation)
    {
        try { cancellation.Cancel(); return null; }
        catch (Exception error)
        { Add(new { kind = "owned-cancellation-failed", operation, error = error.ToString() }); return error; }
    }

    internal async Task<Exception?> JoinAsync(Task original, string operation)
    {
        ArgumentNullException.ThrowIfNull(original);
        lock (gate) retained.Add(original, operation);
        try
        {
            using var diagnostic = new CancellationTokenSource(joinDeadline);
            try { await original.WaitAsync(diagnostic.Token).ConfigureAwait(false); }
            catch (OperationCanceledException error) when (diagnostic.IsCancellationRequested && error.CancellationToken == diagnostic.Token)
            {
                lock (gate) exceeded = true;
                // Persist BEFORE the continued join. The original remains strongly held
                // and every surrounding run/client/handler/deadline scope remains alive.
                await ReportAsync("original-join-deadline", operation, original.Status.ToString(), incomplete: !original.IsCompleted,
                    "Diagnostic deadline exceeded; ownership retained until original settlement.").ConfigureAwait(false);
            }
            catch (Exception) { } // Observe the original fault/cancellation below, not just a timed wrapper.

            Exception? failure = null;
            try { await original.ConfigureAwait(false); }
            catch (Exception error) { failure = error; }
            lock (gate) retained.Remove(original);
            await ReportAsync("original-task-settled", operation, original.Status.ToString(), incomplete: false,
                failure?.ToString()).ConfigureAwait(false);
            return failure;
        }
        finally
        {
            // No path, including evidence I/O failure, can unwind an incomplete task.
            if (!original.IsCompleted)
                try { await original.ConfigureAwait(false); } catch (Exception) { }
            lock (gate) retained.Remove(original);
        }
    }

    internal async Task ReportAsync(string kind, string operation, string status, bool incomplete, string? error = null)
    {
        SimpleRepairProgress record;
        lock (gate)
        {
            base.Add(new { kind, operation, status, incomplete, nonpassing = exceeded || journalFailure is not null || error is not null, error });
            record = new(caseId, kind, operation, status, incomplete, exceeded || journalFailure is not null || error is not null,
                retained.Select(item => new SimpleRepairOriginalTask(item.Value, item.Key.Id, item.Key.Status.ToString())).ToArray(),
                base.ToArray(), error);
        }
        try { await persist(record).ConfigureAwait(false); }
        catch (Exception failure)
        {
            // Journal failure also stops admission and fails the case, after physical joining.
            lock (gate) { journalFailure ??= failure; base.Add(new { kind = "progress-journal-failed", error = failure.ToString() }); }
        }
    }
}

/// <summary>One new JSONL journal. Each receipt is flushed to disk before its observer returns.</summary>
internal sealed class SimpleRepairProgressJournal : IAsyncDisposable
{
    private readonly FileStream stream;
    private readonly SemaphoreSlim serial = new(1, 1);
    internal SimpleRepairProgressJournal(string path) => stream = new(path, FileMode.CreateNew, FileAccess.Write,
        FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);

    internal async Task WriteAsync(SimpleRepairProgress record)
    {
        await serial.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(record);
            await stream.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(false);
            await stream.WriteAsync(new byte[] { 10 }, CancellationToken.None).ConfigureAwait(false);
            await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        finally { serial.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await serial.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try { await stream.DisposeAsync().ConfigureAwait(false); }
        finally { serial.Release(); serial.Dispose(); }
    }
}
