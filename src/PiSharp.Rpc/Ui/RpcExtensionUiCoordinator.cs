using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Rpc.Protocol;

namespace PiSharp.Rpc.Ui;

/// <summary>One RPC connection's bounded UI requests. The dispatcher attaches the actual shared output lease.</summary>
public sealed class RpcExtensionUiCoordinator : IExtensionUiProvider, IAsyncDisposable
{
    private readonly object gate = new();
    private readonly RpcExtensionUiOptions options;
    private readonly TimeProvider time;
    private readonly ExtensionUiCapabilities offered;
    private readonly string nonce = Guid.NewGuid().ToString("N");
    private readonly Dictionary<string, Work> pending = new(StringComparer.Ordinal);
    private readonly HashSet<Work> outstanding = [];
    private readonly CancellationTokenSource connection = new();
    private readonly CancellationToken connectionToken;
    private readonly TaskCompletionSource completion = NewGate();
    private TaskCompletionSource? idle;
    private Func<JsonData, CancellationToken, CancellationToken, Task>? write;
    private Action<RpcDispatchFailure>? fatal;
    private IRpcExtensionUiPresentationObserver? presentation;
    private RpcExtensionUiOptions requestLimits;
    private Task? cleanup;
    private long sequence, retainedBytes;
    private bool closed;
    public RpcExtensionUiOptions Options => options;
    public Task Completion => completion.Task;

    public RpcExtensionUiCoordinator(RpcExtensionUiOptions? options = null, TimeProvider? timeProvider = null,
        long connectionGeneration = 1, long sessionGeneration = 1, ImmutableArray<ExtensionUiFeature>? clientCapabilities = null,
        IRpcExtensionUiPresentationObserver? presentationObserver = null)
    {
        this.options = options ?? new(); this.options.Validate(); requestLimits = this.options;
        if (connectionGeneration <= 0 || sessionGeneration <= 0) throw new ArgumentOutOfRangeException(nameof(connectionGeneration));
        time = timeProvider ?? TimeProvider.System; connectionToken = connection.Token;
        var baseline = ImmutableArray.Create(ExtensionUiFeature.Select, ExtensionUiFeature.Confirm, ExtensionUiFeature.Input,
            ExtensionUiFeature.Editor, ExtensionUiFeature.Notify, ExtensionUiFeature.Status, ExtensionUiFeature.TextWidget,
            ExtensionUiFeature.Title, ExtensionUiFeature.EditorText);
        if (clientCapabilities is { } supplied && (supplied.IsDefault || supplied.Length > 32 || supplied.Any(feature => !Enum.IsDefined(feature))))
            throw new ArgumentException("Invalid explicit UI capability snapshot.", nameof(clientCapabilities));
        var features = clientCapabilities is { } selected ? baseline.Where(selected.Contains).ToImmutableArray() : baseline;
        offered = new(ExtensionUiMode.Rpc, connectionGeneration, sessionGeneration, features);
        presentation = presentationObserver;
    }

    internal void Attach(Func<JsonData, CancellationToken, CancellationToken, Task> writer,
        Action<RpcDispatchFailure> reportFailure, int maximumOutputBytes)
    {
        lock (gate)
        {
            if (closed || write is not null) throw new InvalidOperationException("RPC UI coordinator can attach to one live dispatcher.");
            requestLimits = options with { MaximumRequestBytes = Math.Min(options.MaximumRequestBytes, maximumOutputBytes) };
            write = writer; fatal = reportFailure;
        }
    }
    public IExtensionUiScope OpenScope(IExtensionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrEmpty(context.OwnerId) || context.OwnerId.Length > 128 || context.OwnerGeneration <= 0)
            throw new ArgumentException("UI requires an admitted extension owner.", nameof(context));
        var capabilities = context is IExtensionSessionContext { SessionSnapshot: { } snapshot }
            ? offered with { SessionGeneration = snapshot.Generation } : offered;
        lock (gate) return new Scope(this, context, write is null || closed ? capabilities with { Features = [] } : capabilities);
    }

    internal bool AcceptResponse(JsonData raw)
    {
        if (!RpcExtensionUiCodec.IsResponse(raw)) return false;
        var reply = RpcExtensionUiCodec.Decode(raw, options);
        Work? work;
        lock (gate)
        {
            if (closed || reply.Id is null || !pending.TryGetValue(reply.Id, out work)) return true;
        }
        Resolution result;
        if (reply.Cancelled) result = new(ExtensionUiOutcomeKind.Cancelled);
        else if (reply.Invalid) result = Unavailable(ExtensionUiUnavailableReason.InvalidResponse);
        else if (work.Feature == ExtensionUiFeature.Confirm && reply.Confirmed is { } confirmed)
            result = new(ExtensionUiOutcomeKind.Value, Confirmed: confirmed);
        else if (work.Feature != ExtensionUiFeature.Confirm && reply.Text is { } text)
            result = new(ExtensionUiOutcomeKind.Value, Text: text);
        else result = Unavailable(ExtensionUiUnavailableReason.InvalidResponse);
        Resolve(work, result); return true;
    }

    private string NextId()
    {
        lock (gate)
        {
            if (sequence == long.MaxValue) throw new RpcDispatchException(RpcDispatchFailure.ResourceLimit);
            return nonce + "-" + (++sequence).ToString("x16", CultureInfo.InvariantCulture);
        }
    }
    private void PoisonLimit()
    { Action<RpcDispatchFailure>? report; lock (gate) report = fatal; report?.Invoke(RpcDispatchFailure.ResourceLimit); }
    private Task<Resolution> RequestAsync(Scope scope, ExtensionUiFeature feature, JsonData record,
        double? timeout, CancellationToken cancellationToken, bool dialog)
    {
        Work? work = null; Action<RpcDispatchFailure>? poison = null;
        var bytes = Encoding.UTF8.GetByteCount(record.ToString());
        lock (gate)
        {
            if (scope.Closed) return Task.FromResult(Unavailable(ExtensionUiUnavailableReason.StaleContext));
            if (closed) return Task.FromResult(Unavailable(ExtensionUiUnavailableReason.Disconnected));
            if (write is null) return Task.FromResult(Unavailable(ExtensionUiUnavailableReason.NotConnected));
            if (!scope.Capabilities.Supports(feature)) return Task.FromResult(Unavailable(ExtensionUiUnavailableReason.UnsupportedCapability));
            if (scope.Context.OperationCancellationToken.IsCancellationRequested || scope.Context.SessionCancellationToken.IsCancellationRequested ||
                scope.Context.ExtensionLifetimeCancellationToken.IsCancellationRequested || cancellationToken.IsCancellationRequested)
                return Task.FromResult(new Resolution(ExtensionUiOutcomeKind.Cancelled));
            if (outstanding.Count >= options.MaximumOutstandingRequests || retainedBytes + bytes > options.MaximumRetainedBytes)
                poison = fatal;
            else
            {
                if (outstanding.Count == 0) idle = NewGate();
                work = new(scope, feature, record, bytes, dialog);
                work.Execution = ExecuteAsync(work, record, timeout, cancellationToken);
                outstanding.Add(work); scope.Work.Add(work); retainedBytes += bytes;
                if (dialog) pending.Add(work.Id, work);
            }
        }
        if (work is null)
        { poison?.Invoke(RpcDispatchFailure.ResourceLimit); return Task.FromResult(Unavailable(ExtensionUiUnavailableReason.ResourceLimit)); }
        work.Start.TrySetResult(); return work.Execution;
    }
    private async Task<Resolution> ExecuteAsync(Work work, JsonData record, double? timeout, CancellationToken cancellationToken)
    {
        // Every admitted entry has its actual task installed before any response/close can capture it.
        await work.Start.Task.ConfigureAwait(false);
        var scope = work.Scope;
        ITimer? timer = null; CancellationTokenRegistration registration = default;
        CancellationTokenSource? linked = null;
        Resolution? outcome = null;
        var observer = work.Dialog ? presentation : null;
        var publicationSucceeded = false; var presentationEntered = false;
        try
        {
            linked = CancellationTokenSource.CreateLinkedTokenSource(scope.Context.OperationCancellationToken,
                scope.Context.SessionCancellationToken, scope.Context.ExtensionLifetimeCancellationToken, cancellationToken);
            registration = linked.Token.UnsafeRegister(_ => Resolve(work, new(ExtensionUiOutcomeKind.Cancelled)), null);
            if (work.Dialog && timeout is { } milliseconds && milliseconds != 0)
            {
                // Node timer delay: finite negative/too-large delays clamp to 1 ms; positive fractions truncate, with a 1 ms minimum.
                var delay = milliseconds < 1 || milliseconds > int.MaxValue ? 1 : Math.Truncate(milliseconds);
                timer = time.CreateTimer(_ => Resolve(work, new(ExtensionUiOutcomeKind.TimedOut)), null,
                    TimeSpan.FromMilliseconds(delay), Timeout.InfiniteTimeSpan);
            }
            if (!work.Chosen || work.Selected?.Kind == ExtensionUiOutcomeKind.Value)
            {
                Func<JsonData, CancellationToken, CancellationToken, Task> writer;
                lock (gate) writer = write!;
                try
                {
                    // Cancellation can suppress a queued record. An entered write is joined under connection ownership.
                    await writer(record, work.Suppress.Token, connectionToken).ConfigureAwait(false);
                    publicationSucceeded = true;
                }
                catch (OperationCanceledException) when (work.Suppress.IsCancellationRequested || connectionToken.IsCancellationRequested)
                {
                    if (connectionToken.IsCancellationRequested) return outcome = Unavailable(ExtensionUiUnavailableReason.Disconnected);
                    Resolve(work, new(ExtensionUiOutcomeKind.Cancelled));
                }
                catch (Exception)
                {
                    Action<RpcDispatchFailure>? report; lock (gate) report = fatal;
                    report?.Invoke(RpcDispatchFailure.OutputFailed);
                    return outcome = Unavailable(ExtensionUiUnavailableReason.OutputFailed); // A response cannot approve a failed publication.
                }
                if (observer is not null && !work.Suppress.IsCancellationRequested)
                {
                    // The writer has released its actual shared lease, including the flush barrier.
                    // Observer work stays charged to this admitted request until all entered callbacks settle.
                    using var observing = CancellationTokenSource.CreateLinkedTokenSource(work.Suppress.Token, connectionToken);
                    presentationEntered = true;
                    try { await observer.PublishedAsync(new(work.Identity, record), observing.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (work.Suppress.IsCancellationRequested || connectionToken.IsCancellationRequested)
                    {
                        return outcome = connectionToken.IsCancellationRequested ? Unavailable(ExtensionUiUnavailableReason.Disconnected) :
                            await work.Decision.Task.ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        Action<RpcDispatchFailure>? report; lock (gate) report = fatal;
                        report?.Invoke(RpcDispatchFailure.OutputFailed);
                        return outcome = Unavailable(ExtensionUiUnavailableReason.OutputFailed);
                    }
                }
            }
            if (!work.Dialog) Resolve(work, new(ExtensionUiOutcomeKind.Value));
            return outcome = await work.Decision.Task.ConfigureAwait(false);
        }
        finally
        {
            Exception? cleanupFailure = null;
            if (observer is not null)
            {
                var terminal = outcome ?? Unavailable(ExtensionUiUnavailableReason.OutputFailed);
                try { await observer.RetiredAsync(new(work.Identity, terminal.Kind, terminal.Reason,
                    publicationSucceeded, presentationEntered), connectionToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (connectionToken.IsCancellationRequested) { }
                catch (Exception error) { cleanupFailure = error; }
            }
            try { if (timer is not null) await timer.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { cleanupFailure = error; }
            try { await registration.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { cleanupFailure ??= error; }
            try { linked?.Dispose(); } catch (Exception error) { cleanupFailure ??= error; }
            try { work.Suppress.Dispose(); } catch (Exception error) { cleanupFailure ??= error; }
            if (cleanupFailure is not null)
            { Action<RpcDispatchFailure>? report; lock (gate) report = fatal; report?.Invoke(RpcDispatchFailure.CleanupFailed); }
            lock (gate)
            {
                pending.Remove(work.Id); outstanding.Remove(work); scope.Work.Remove(work); retainedBytes -= work.Bytes;
                if (outstanding.Count == 0) idle!.TrySetResult();
                if (scope.Closed && scope.Work.Count == 0) scope.Idle.TrySetResult();
            }
            if (cleanupFailure is not null) throw new IOException("RPC UI request cleanup failed.", cleanupFailure);
        }
    }
    private void Resolve(Work work, Resolution result)
    {
        Action<RpcDispatchFailure>? poison = null;
        lock (gate)
        {
            if (work.Chosen) return;
            var responseBytes = result.Text is null ? 0 : Encoding.UTF8.GetByteCount(result.Text);
            if (retainedBytes + responseBytes > options.MaximumRetainedBytes)
            { result = Unavailable(ExtensionUiUnavailableReason.ResourceLimit); poison = fatal; responseBytes = 0; }
            work.Selected = result; work.Chosen = true; pending.Remove(work.Id); work.Bytes += responseBytes; retainedBytes += responseBytes;
        }
        work.Decision.TrySetResult(result);
        if (result.Kind != ExtensionUiOutcomeKind.Value) { try { work.Suppress.Cancel(); } catch (Exception) { /* Internal cancellation listeners only. */ } }
        poison?.Invoke(RpcDispatchFailure.ResourceLimit);
    }
    private Task CloseScope(Scope scope)
    {
        Work[] work;
        lock (gate)
        {
            if (scope.Closed) return scope.Closing!;
            scope.Closed = true; work = scope.Work.ToArray(); if (work.Length == 0) scope.Idle.TrySetResult();
            scope.Closing = Task.WhenAll(work.Select(item => (Task)item.Execution).Append(scope.Idle.Task));
        }
        foreach (var item in work) Resolve(item, new(ExtensionUiOutcomeKind.Cancelled));
        return scope.Closing!;
    }
    internal Task BeginDisconnect()
    {
        Work[] work; Task settle;
        lock (gate)
        {
            if (cleanup is not null) return cleanup;
            closed = true; cleanup = completion.Task; work = outstanding.ToArray(); settle = idle?.Task ?? Task.CompletedTask;
        }
        foreach (var item in work) Resolve(item, Unavailable(ExtensionUiUnavailableReason.Disconnected));
        Exception? failure = null;
        try { connection.Cancel(); } catch (Exception error) { failure = error; }
        _ = CleanupAsync(settle, work.Select(item => (Task)item.Execution).ToArray(), failure); return completion.Task;
    }
    private async Task CleanupAsync(Task settle, Task[] admitted, Exception? failure)
    {
        try
        {
            try { await Task.WhenAll(admitted).ConfigureAwait(false); } catch (Exception error) { failure ??= error; }
            await settle.ConfigureAwait(false);
            lock (gate) { write = null; fatal = null; presentation = null; }
            connection.Dispose();
            if (failure is null) completion.TrySetResult(); else completion.TrySetException(new IOException("RPC UI cancellation cleanup failed.", failure));
        }
        catch (Exception error) { completion.TrySetException(new IOException("RPC UI cleanup failed.", error)); }
    }
    public ValueTask DisposeAsync() => new(BeginDisconnect());
    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Resolution Unavailable(ExtensionUiUnavailableReason reason) => new(ExtensionUiOutcomeKind.Unavailable, Reason: reason);
    private sealed record Resolution(ExtensionUiOutcomeKind Kind, string? Text = null, bool Confirmed = false,
        ExtensionUiUnavailableReason? Reason = null);
    private sealed class Work(Scope scope, ExtensionUiFeature feature, JsonData record, int bytes, bool dialog)
    {
        internal readonly Scope Scope = scope;
        internal readonly ExtensionUiFeature Feature = feature;
        internal readonly string Id = record.Value.GetProperty("id").GetString()!;
        internal readonly bool Dialog = dialog;
        internal readonly RpcExtensionUiPresentationIdentity Identity = new(record.Value.GetProperty("id").GetString()!,
            scope.Capabilities.ConnectionGeneration, scope.Capabilities.SessionGeneration, scope.Context.OwnerId, scope.Context.OwnerGeneration);
        internal readonly TaskCompletionSource Start = NewGate();
        internal Task<Resolution> Execution = null!;
        internal long Bytes = bytes;
        internal volatile bool Chosen;
        internal Resolution? Selected;
        internal readonly CancellationTokenSource Suppress = new();
        internal readonly TaskCompletionSource<Resolution> Decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Scope(RpcExtensionUiCoordinator owner, IExtensionContext context, ExtensionUiCapabilities capabilities) : IExtensionUiScope
    {
        private RpcExtensionUiCoordinator? owner = owner;
        internal readonly IExtensionContext Context = context;
        internal readonly HashSet<Work> Work = [];
        internal readonly TaskCompletionSource Idle = NewGate();
        internal bool Closed;
        internal Task? Closing;
        private Task? disposal;
        public ExtensionUiCapabilities Capabilities { get; } = capabilities;
        public ValueTask<ExtensionUiOutcome<string>> SelectAsync(string title, ImmutableArray<string> choices,
            ExtensionUiDialogOptions? options = null, CancellationToken cancellationToken = default) => TextAsync(ExtensionUiFeature.Select, title, null, choices, options, cancellationToken);
        public async ValueTask<ExtensionUiOutcome<bool>> ConfirmAsync(string title, string message,
            ExtensionUiDialogOptions? options = null, CancellationToken cancellationToken = default)
        {
            var result = await DialogAsync(ExtensionUiFeature.Confirm, title, message, [], options, cancellationToken).ConfigureAwait(false);
            return Convert(result, result.Confirmed);
        }
        public ValueTask<ExtensionUiOutcome<string>> InputAsync(string title, string? placeholder = null,
            ExtensionUiDialogOptions? options = null, CancellationToken cancellationToken = default) => TextAsync(ExtensionUiFeature.Input, title, placeholder, [], options, cancellationToken);
        public ValueTask<ExtensionUiOutcome<string>> EditorAsync(string title, string? prefill = null,
            CancellationToken cancellationToken = default) => TextAsync(ExtensionUiFeature.Editor, title, prefill, [], null, cancellationToken);
        private async ValueTask<ExtensionUiOutcome<string>> TextAsync(ExtensionUiFeature feature, string title, string? extra,
            ImmutableArray<string> choices, ExtensionUiDialogOptions? timing, CancellationToken token)
        { var result = await DialogAsync(feature, title, extra, choices, timing, token).ConfigureAwait(false); return Convert(result, result.Text!); }
        private Task<Resolution> DialogAsync(ExtensionUiFeature feature, string title, string? extra,
            ImmutableArray<string> choices, ExtensionUiDialogOptions? timing, CancellationToken token)
        {
            var coordinator = Volatile.Read(ref owner);
            if (coordinator is null) return Task.FromResult(Unavailable(ExtensionUiUnavailableReason.StaleContext));
            try
            {
                var record = RpcExtensionUiCodec.Dialog(coordinator.NextId(), feature, title, extra, choices, timing, coordinator.requestLimits);
                return coordinator.RequestAsync(this, feature, record, timing?.TimeoutMilliseconds, token, dialog: true);
            }
            catch (RpcDispatchException error) when (error.Failure == RpcDispatchFailure.ResourceLimit)
            { coordinator.PoisonLimit(); return Task.FromResult(Unavailable(ExtensionUiUnavailableReason.ResourceLimit)); }
        }
        public async ValueTask<ExtensionUiOutcome<ExtensionUiPublication>> PublishAsync(ExtensionUiNotification notification, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(notification);
            var coordinator = Volatile.Read(ref owner);
            if (coordinator is null) return ExtensionUiOutcome<ExtensionUiPublication>.Unavailable(ExtensionUiUnavailableReason.StaleContext);
            ExtensionUiFeature feature; JsonData record;
            try { (feature, record) = RpcExtensionUiCodec.Notification(coordinator.NextId(), notification, coordinator.requestLimits); }
            catch (RpcDispatchException error) when (error.Failure == RpcDispatchFailure.ResourceLimit)
            { coordinator.PoisonLimit(); return ExtensionUiOutcome<ExtensionUiPublication>.Unavailable(ExtensionUiUnavailableReason.ResourceLimit); }
            var result = await coordinator.RequestAsync(this, feature, record, null, cancellationToken, dialog: false).ConfigureAwait(false);
            return Convert(result, ExtensionUiPublication.Published);
        }
        public ValueTask DisposeAsync()
        {
            lock (Idle)
            {
                if (disposal is not null) return new(disposal);
                var coordinator = owner;
                disposal = coordinator is null ? Task.CompletedTask : DisposeCoreAsync(coordinator);
                return new(disposal);
            }
        }
        private async Task DisposeCoreAsync(RpcExtensionUiCoordinator coordinator)
        { try { await coordinator.CloseScope(this).ConfigureAwait(false); } finally { Volatile.Write(ref owner, null); } }
        private static ExtensionUiOutcome<T> Convert<T>(Resolution result, T value) => result.Kind switch
        {
            ExtensionUiOutcomeKind.Value => ExtensionUiOutcome<T>.FromValue(value),
            ExtensionUiOutcomeKind.Cancelled => ExtensionUiOutcome<T>.Cancelled(),
            ExtensionUiOutcomeKind.TimedOut => ExtensionUiOutcome<T>.TimedOut(),
            _ => ExtensionUiOutcome<T>.Unavailable(result.Reason ?? ExtensionUiUnavailableReason.Disconnected)
        };
    }
}
