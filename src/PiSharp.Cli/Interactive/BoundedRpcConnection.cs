using System.Text;
using System.Threading.Channels;
using PiSharp.Contracts;

namespace PiSharp.Cli.Interactive;

/// <summary>Bounded complete-record command bytes and the host's actual shared output lease.</summary>
internal sealed class BoundedRpcConnection : IAsyncDisposable
{
    private const int MaximumFrameBytes = PiSharp.Cli.Commands.PiPayloadBudget.OutputRecordBytes;
    private readonly Channel<byte[]> commands = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
    private readonly SemaphoreSlim slots = new(8, 8);
    private readonly CancellationTokenSource closed = new();
    private readonly CancellationToken stop;
    private readonly object gate = new();
    private readonly Func<JsonData, CancellationToken, ValueTask> observe;
    private readonly Func<CancellationToken, ValueTask>? afterSlotAcquired;
    private readonly Func<CancellationToken, ValueTask>? afterReadStarted;
    private byte[]? current, pending;
    private int position;
    private TaskCompletionSource<int>? reading;
    private TaskCompletionSource? idle;
    private int operations;
    private Task? disposal;
    private bool inputCompleted;
    internal Stream Input { get; }
    internal Stream Output { get; }
    internal BoundedRpcConnection(Func<JsonData, CancellationToken, ValueTask> observe,
        Func<CancellationToken, ValueTask>? afterSlotAcquired = null,
        Func<CancellationToken, ValueTask>? afterReadStarted = null)
    { this.observe = observe; this.afterSlotAcquired = afterSlotAcquired; this.afterReadStarted = afterReadStarted; stop = closed.Token; Input = new InputStream(this); Output = new OutputStream(this); }
    internal async Task SendAsync(JsonData record, CancellationToken token)
    {
        lock (gate) { if (inputCompleted) throw new InvalidOperationException("Interactive RPC input is closed."); Enter(); }
        try
        {
            var raw = Encoding.UTF8.GetBytes(record.ToString() + "\n");
            if (raw.Length > PiSharp.Cli.Commands.PiPayloadBudget.RpcCommandBytes + 1) throw new InvalidOperationException("Interactive command exceeds RPC framing.");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stop);
            try { await slots.WaitAsync(linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException error) when (OwnsAdmissionCancellation(error, linked.Token, token))
            { throw AdmissionCancellation(error, token); }
            try
            {
                // Optional original admission observation is outside local wait/write
                // cancellation classification. Its failures retain their own provenance.
                if (afterSlotAcquired is not null) await afterSlotAcquired(linked.Token).ConfigureAwait(false);
                lock (gate) if (inputCompleted || disposal is not null) throw new InvalidOperationException("Interactive RPC input is closed.");
                try { await commands.Writer.WriteAsync(raw, linked.Token).ConfigureAwait(false); }
                catch (OperationCanceledException error) when (OwnsAdmissionCancellation(error, linked.Token, token))
                { throw AdmissionCancellation(error, token); }
            }
            catch { slots.Release(); throw; }
        }
        finally { Leave(); }
    }
    private bool OwnsAdmissionCancellation(OperationCanceledException error, CancellationToken linked, CancellationToken caller) =>
        error.CancellationToken == linked && linked.IsCancellationRequested && (caller.IsCancellationRequested || stop.IsCancellationRequested);
    private RpcCommandAdmissionCanceledException AdmissionCancellation(OperationCanceledException original, CancellationToken caller)
        => new(original, caller, stop, caller.IsCancellationRequested, stop.IsCancellationRequested);
    internal void CompleteInput()
    { lock (gate) { if (inputCompleted) return; inputCompleted = true; commands.Writer.TryComplete(); } }
    private ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); TaskCompletionSource<int> admitted;
        lock (gate)
        {
            if (disposal is not null) throw new ObjectDisposedException(nameof(BoundedRpcConnection));
            if (destination.IsEmpty) return ValueTask.FromResult(0);
            if (reading is not null) throw new InvalidOperationException("Interactive RPC has one input reader.");
            Enter();
            reading = admitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        _ = ReadCoreAsync(destination, token, admitted); return new(admitted.Task);
    }
    private async Task ReadCoreAsync(Memory<byte> destination, CancellationToken token, TaskCompletionSource<int> admitted)
    {
        var count = 0; Exception? failure = null;
        CancellationToken readLinkedToken = default;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stop);
            readLinkedToken = linked.Token;
            // This original observation is joined within the counted read, outside
            // gates and outside the known channel/ThrowIf cancellation scopes.
            if (afterReadStarted is not null) await afterReadStarted(linked.Token).ConfigureAwait(false);
            if (current is null)
                while (await WaitForCommandsAsync(linked.Token, token).ConfigureAwait(false))
                    if (commands.Reader.TryRead(out current)) { position = 0; break; }
            ThrowIfReadCanceled(linked.Token, token);
            if (current is not null)
            {
                count = Math.Min(destination.Length, current.Length - position);
                current.AsMemory(position, count).CopyTo(destination); position += count;
                if (position == current.Length) { current = null; slots.Release(); }
            }
        }
        catch (OperationCanceledException error) when (error is not RpcCommandReadCanceledException)
        {
            // An arbitrary callback/memory failure is not a known local cancellation,
            // even if it uses the caller or private linked token during shutdown.
            failure = new RpcCommandReadFailedException(error, token, stop, readLinkedToken);
        }
        catch (Exception error) { failure = error; }
        lock (gate) { if (failure is null) admitted.TrySetResult(count); else admitted.TrySetException(failure); reading = null; }
        Leave();
    }
    private async ValueTask<bool> WaitForCommandsAsync(CancellationToken linked, CancellationToken caller)
    {
        try { return await commands.Reader.WaitToReadAsync(linked).ConfigureAwait(false); }
        catch (OperationCanceledException error) when (OwnsAdmissionCancellation(error, linked, caller))
        { throw ReadCancellation(error, linked, caller); }
    }
    private void ThrowIfReadCanceled(CancellationToken linked, CancellationToken caller)
    {
        try { linked.ThrowIfCancellationRequested(); }
        catch (OperationCanceledException error) when (OwnsAdmissionCancellation(error, linked, caller))
        { throw ReadCancellation(error, linked, caller); }
    }
    private RpcCommandReadCanceledException ReadCancellation(OperationCanceledException original, CancellationToken linked, CancellationToken caller)
        => new(this, original, linked, caller, stop, caller.IsCancellationRequested, stop.IsCancellationRequested);
    private bool OwnsReadCancellation(RpcCommandReadCanceledException error) => ReferenceEquals(error.Owner, this) &&
        error.ConnectionToken == stop && ReferenceEquals(error.Original, error.InnerException) &&
        error.Original.CancellationToken == error.LinkedToken && error.LinkedToken.IsCancellationRequested &&
        (error.ConnectionRequested && error.CancellationToken == stop ||
            !error.ConnectionRequested && error.CallerRequested && error.CancellationToken == error.CallerToken);
    private ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (disposal is not null) throw new ObjectDisposedException(nameof(BoundedRpcConnection));
            if (pending is not null || data.Length is < 2 or > MaximumFrameBytes + 1 || data.Span[^1] != '\n')
                throw new InvalidOperationException("Interactive RPC requires one bounded complete LF record.");
            pending = data.ToArray();
        }
        return ValueTask.CompletedTask;
    }
    private async Task FlushAsync(CancellationToken token)
    {
        byte[]? raw; lock (gate) { Enter(); raw = pending; pending = null; }
        try
        {
            if (raw is not null)
            {
                var text = new UTF8Encoding(false, true).GetString(raw, 0, raw.Length - 1);
                // The authoritative host already admitted JSON through its shared writer.
                await observe(JsonData.Parse(text), token).ConfigureAwait(false);
            }
        }
        finally { Leave(); }
    }
    // Called only under gate. This counts waiting admissions as well as active callbacks.
    private void Enter()
    {
        if (disposal is not null) throw new ObjectDisposedException(nameof(BoundedRpcConnection));
        if (operations >= 16) throw new InvalidOperationException("Interactive RPC admissions exceed their bounded profile.");
        if (operations++ == 0) idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private void Leave() { lock (gate) if (--operations == 0) idle!.TrySetResult(); }
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? settlement = null; Task<int>? read = null; Task joined = Task.CompletedTask; Task pendingDisposal;
        lock (gate)
        {
            if (disposal is null)
            { settlement = new(TaskCreationOptions.RunContinuationsAsynchronously); disposal = settlement.Task; read = reading?.Task; joined = operations == 0 ? Task.CompletedTask : idle!.Task; }
            pendingDisposal = disposal;
        }
        if (settlement is not null) _ = CloseAsync(read, joined, settlement);
        return new(pendingDisposal);
    }
    private async Task CloseAsync(Task<int>? read, Task joined, TaskCompletionSource settlement)
    {
        var failures = new List<Exception>();
        // Cancel the actual read source before publishing EOF; otherwise EOF can
        // win and hide abrupt connection closure from an active RPC reader.
        try { closed.Cancel(); } catch (Exception error) { failures.Add(error); }
        CompleteInput();
        await joined.ConfigureAwait(false);
        if (read is not null) try { await read.ConfigureAwait(false); }
            catch (RpcCommandReadCanceledException error) when (OwnsReadCancellation(error)) { }
            catch (Exception error) { failures.Add(error); }
        lock (gate) { current = pending = null; position = 0; while (commands.Reader.TryRead(out _)) { } }
        try { closed.Dispose(); } catch (Exception error) { failures.Add(error); }
        try { slots.Dispose(); } catch (Exception error) { failures.Add(error); }
        if (failures.Count == 0) settlement.TrySetResult();
        else settlement.TrySetException(failures.Count == 1 ? failures[0] : new AggregateException(failures));
    }
    private abstract class PipeStream : Stream
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
    private sealed class InputStream(BoundedRpcConnection owner) : PipeStream
    {
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => owner.ReadAsync(buffer, token);
    }
    private sealed class OutputStream(BoundedRpcConnection owner) : PipeStream
    {
        public override bool CanRead => false;
        public override bool CanWrite => true;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) => owner.WriteAsync(buffer, token);
        public override Task FlushAsync(CancellationToken token) => owner.FlushAsync(token);
    }
}

/// <summary>Proof from the actual connection's local semaphore/channel admission only.
/// Closure is fatal even when caller cancellation contributes too. Original linked-token
/// exception remains a cause; arbitrary callback errors never acquire this proof.</summary>
internal sealed class RpcCommandAdmissionCanceledException : OperationCanceledException
{
    internal RpcCommandAdmissionCanceledException(OperationCanceledException original, CancellationToken caller,
        CancellationToken connection, bool callerRequested, bool connectionRequested)
        : base(original.Message, original, connectionRequested ? connection : caller)
    { Original = original; CallerToken = caller; ConnectionToken = connection; CallerRequested = callerRequested; ConnectionRequested = connectionRequested; }
    internal OperationCanceledException Original { get; }
    internal CancellationToken CallerToken { get; }
    internal CancellationToken ConnectionToken { get; }
    internal bool CallerRequested { get; }
    internal bool ConnectionRequested { get; }
}

/// <summary>Proof only from this connection's actual local channel wait/check.
/// Caller cancellation maps to the JsonlReader-supplied token; closure maps to the
/// connection token and remains foreign/fatal to an active upstream reader.</summary>
internal sealed class RpcCommandReadCanceledException : OperationCanceledException
{
    internal RpcCommandReadCanceledException(BoundedRpcConnection owner, OperationCanceledException original,
        CancellationToken linked, CancellationToken caller, CancellationToken connection, bool callerRequested, bool connectionRequested)
        : base(original.Message, original, connectionRequested ? connection : caller)
    { Owner = owner; Original = original; LinkedToken = linked; CallerToken = caller; ConnectionToken = connection;
        CallerRequested = callerRequested; ConnectionRequested = connectionRequested; }
    internal BoundedRpcConnection Owner { get; }
    internal OperationCanceledException Original { get; }
    internal CancellationToken LinkedToken { get; }
    internal CancellationToken CallerToken { get; }
    internal CancellationToken ConnectionToken { get; }
    internal bool CallerRequested { get; }
    internal bool ConnectionRequested { get; }
}

/// <summary>A foreign read cancellation cannot acquire upstream owned-cancel
/// authority by throwing a matching token from a callback. Its original cause
/// and actual contributing-token states remain available after original joins.</summary>
internal sealed class RpcCommandReadFailedException : IOException
{
    internal RpcCommandReadFailedException(OperationCanceledException original, CancellationToken caller,
        CancellationToken connection, CancellationToken linked)
        : base("Interactive RPC read failed outside its owned cancellation boundary.", original)
    { Original = original; CallerToken = caller; ConnectionToken = connection; LinkedToken = linked;
        CallerRequested = caller.IsCancellationRequested; ConnectionRequested = connection.IsCancellationRequested; }
    internal OperationCanceledException Original { get; }
    internal CancellationToken CallerToken { get; }
    internal CancellationToken ConnectionToken { get; }
    internal CancellationToken LinkedToken { get; }
    internal bool CallerRequested { get; }
    internal bool ConnectionRequested { get; }
}
