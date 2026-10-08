using System.Text;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Rpc;
using PiSharp.Rpc.Protocol;
using PiSharp.Rpc.Ui;

internal static class RpcUiPresentationRetirementTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("rpc.native-retirement-held-real-flush-timeout-and-late-reply", PublicationTimeout);
        yield return ("rpc.native-retirement-success-awaits-flush-and-observers-release-writer-and-state", PublicationValue);
        yield return ("rpc.native-retirement-caller-operation-session-owner-and-scope-cancellation", CancellationOwnership);
        yield return ("rpc.native-retirement-owner-replacement-duplicate-and-late-replies", OwnerReplacement);
        yield return ("rpc.native-retirement-entered-callback-bounds-and-shared-disconnect-join", CallbackJoin);
        yield return ("rpc.native-retirement-publication-observer-write-flush-and-retirement-faults", Faults);
    }

    private static async Task PublicationTimeout()
    {
        var clock = new ManualClock(); var observer = new Observer();
        await using var fixture = new Fixture(observer, clock: clock, holdFlush: true);
        await using var scope = fixture.Ui.OpenScope(new Context());
        var answer = scope.ConfirmAsync("held timeout", "denied", new(12.75)).AsTask();
        await fixture.Output.FlushEntered.Task.WaitAsync(Deadline);
        var record = fixture.Output.Written!; var id = Id(record);
        Equal(6, record.Value.EnumerateObject().Count());
        Equal(12.75, record.Value.GetProperty("timeout").GetDouble());
        Check(!record.Value.TryGetProperty("generation", out _), "Native metadata changed baseline bytes.");
        clock.Fire(); fixture.Reply(id, confirmed: true); fixture.Reply(id, confirmed: true);
        Check(!answer.IsCompleted && observer.Published.Count == 0 && observer.Retired.Count == 0,
            "Timeout/late reply retired or completed an entered publication before its actual flush settled.");
        fixture.Output.FlushRelease.TrySetResult();
        var result = await answer.WaitAsync(Deadline); Equal(ExtensionUiOutcomeKind.TimedOut, result.Kind);
        Check(!ExtensionUiSourceDefaults.Confirmation(result), "Publication-held timeout became approval.");
        Equal(0, observer.Published.Count); Equal(1, observer.Retired.Count); Equal(0, clock.ActiveTimers);
        Identity(observer.Retired[0].Identity, id, 1); Equal(ExtensionUiOutcomeKind.TimedOut, observer.Retired[0].Outcome);
        Equal(null, observer.Retired[0].UnavailableReason);
        Check(observer.Retired[0].PublicationSucceeded && !observer.Retired[0].PresentationEntered, "Held timeout exposure was guessed.");
        fixture.Reply(id, confirmed: true); Equal(1, observer.Retired.Count);
    }

    private static async Task PublicationValue()
    {
        var observer = new Observer(); await using var fixture = new Fixture(observer, holdFlush: true);
        await using var scope = fixture.Ui.OpenScope(new Context());
        observer.OnPublished = async (presentation, _) =>
        {
            // The same actual writer and coordinator are usable from an entered native callback.
            await fixture.Writer.WriteAsync(JsonData.Parse("""{"type":"native-test-probe"}"""));
            await using var probe = await Task.Run(() => fixture.Ui.OpenScope(new Context(OwnerGeneration: 9)));
            Equal(7L, probe.Capabilities.ConnectionGeneration); Identity(presentation.Identity, Id(presentation.Request), 1);
        };
        var answer = scope.ConfirmAsync("early answer", "value").AsTask();
        await fixture.Output.FlushEntered.Task.WaitAsync(Deadline); var id = Id(fixture.Output.Written!);
        fixture.Reply(id, confirmed: false);
        Check(!answer.IsCompleted && observer.Retired.Count == 0, "An early response bypassed actual publication.");
        fixture.Output.FlushRelease.TrySetResult();
        var result = await answer.WaitAsync(Deadline); Equal(ExtensionUiOutcomeKind.Value, result.Kind); Equal(false, result.Value);
        Equal(1, observer.Published.Count); Equal(1, observer.Retired.Count);
        Equal(observer.Published[0].Identity, observer.Retired[0].Identity); Equal(ExtensionUiOutcomeKind.Value, observer.Retired[0].Outcome);
        Check(observer.Retired[0].PublicationSucceeded && observer.Retired[0].PresentationEntered, "Successful presentation exposure was lost.");
        fixture.Reply(id, confirmed: true); Equal(false, result.Value); Equal(1, observer.Retired.Count);
    }

    private static async Task CancellationOwnership()
    {
        foreach (var source in new[] { "caller", "operation", "session", "owner", "scope" })
        {
            using var caller = new CancellationTokenSource(); using var operation = new CancellationTokenSource();
            using var session = new CancellationTokenSource(); using var owner = new CancellationTokenSource();
            var observer = new Observer(); await using var fixture = new Fixture(observer);
            await using var scope = fixture.Ui.OpenScope(new Context(OperationCancellationToken: operation.Token,
                SessionCancellationToken: session.Token, ExtensionLifetimeCancellationToken: owner.Token));
            var answer = scope.ConfirmAsync(source, "never approve", cancellationToken: caller.Token).AsTask();
            await observer.Publication.Task.WaitAsync(Deadline); var id = observer.Published[0].Identity.RequestId;
            Task? closing = null;
            switch (source)
            {
                case "caller": caller.Cancel(); break;
                case "operation": operation.Cancel(); break;
                case "session": session.Cancel(); break;
                case "owner": owner.Cancel(); break;
                case "scope": closing = scope.DisposeAsync().AsTask(); break;
            }
            var result = await answer.WaitAsync(Deadline); if (closing is not null) await closing.WaitAsync(Deadline);
            Equal(ExtensionUiOutcomeKind.Cancelled, result.Kind); Check(!ExtensionUiSourceDefaults.Confirmation(result), "Cancellation approved.");
            Equal(1, observer.Retired.Count); Identity(observer.Retired[0].Identity, id, 1);
            Equal(ExtensionUiOutcomeKind.Cancelled, observer.Retired[0].Outcome);
            fixture.Reply(id, confirmed: true); Equal(1, observer.Retired.Count);
        }
    }

    private static async Task OwnerReplacement()
    {
        var observer = new Observer(); await using var fixture = new Fixture(observer);
        var oldScope = fixture.Ui.OpenScope(new Context()); var old = oldScope.ConfirmAsync("old", "denied").AsTask();
        await observer.Publication.Task.WaitAsync(Deadline); var oldId = observer.Published[0].Identity.RequestId;
        await oldScope.DisposeAsync().AsTask().WaitAsync(Deadline); Equal(ExtensionUiOutcomeKind.Cancelled, (await old).Kind);
        Equal(ExtensionUiUnavailableReason.StaleContext, (await oldScope.ConfirmAsync("stale", "denied")).UnavailableReason);
        await using var nextScope = fixture.Ui.OpenScope(new Context(OwnerGeneration: 2));
        observer.Publication = Signal(); var next = nextScope.ConfirmAsync("replacement", "explicit").AsTask();
        await observer.Publication.Task.WaitAsync(Deadline); var nextId = observer.Published[1].Identity.RequestId;
        Check(oldId != nextId, "Replacement reused the correlation ID.");
        fixture.Reply(oldId, confirmed: true); Check(!next.IsCompleted, "Old owner response resolved a replacement dialog.");
        fixture.Reply(nextId, confirmed: false); fixture.Reply(nextId, confirmed: true);
        var result = await next.WaitAsync(Deadline); Equal(false, result.Value); Equal(2, observer.Retired.Count);
        Identity(observer.Retired[1].Identity, nextId, 2);
    }

    private static async Task CallbackJoin()
    {
        var observer = new Observer(); var entered = Signal(); var release = Signal();
        observer.OnRetired = async (_, _) => { entered.TrySetResult(); await release.Task; };
        await using var fixture = new Fixture(observer, options: new(MaximumOutstandingRequests: 1));
        var scope = fixture.Ui.OpenScope(new Context()); var answer = scope.ConfirmAsync("held retirement", "deny").AsTask();
        await observer.Publication.Task.WaitAsync(Deadline);
        var scopeClose = scope.DisposeAsync().AsTask(); await entered.Task.WaitAsync(Deadline);
        Check(!answer.IsCompleted && !scopeClose.IsCompleted, "Scope skipped an entered observer join.");
        await using var other = fixture.Ui.OpenScope(new Context(OwnerGeneration: 2));
        Equal(ExtensionUiUnavailableReason.ResourceLimit, (await other.InputAsync("bounded")).UnavailableReason);
        Equal(1, observer.Retired.Count); Check(fixture.Failures.Contains(RpcDispatchFailure.ResourceLimit), "Observer retention escaped admission bounds.");
        var first = fixture.Ui.DisposeAsync().AsTask(); var second = fixture.Ui.DisposeAsync().AsTask();
        Check(ReferenceEquals(first, second) && !first.IsCompleted, "Disconnect did not share and join actual callback cleanup.");
        release.TrySetResult(); await Task.WhenAll(answer, scopeClose, first, second).WaitAsync(Deadline);
        Equal(1, observer.Retired.Count); Equal(ExtensionUiOutcomeKind.Cancelled, (await answer).Kind);

        var disconnected = new Observer(); await using var next = new Fixture(disconnected);
        await using var nextScope = next.Ui.OpenScope(new Context()); var pending = nextScope.ConfirmAsync("disconnect", "deny").AsTask();
        await disconnected.Publication.Task.WaitAsync(Deadline); await next.Ui.DisposeAsync();
        Equal(ExtensionUiUnavailableReason.Disconnected, (await pending).UnavailableReason);
        Equal(ExtensionUiUnavailableReason.Disconnected, disconnected.Retired.Single().UnavailableReason);
    }

    private static async Task Faults()
    {
        foreach (var fault in new[] { "write", "flush", "published", "retired" })
        {
            var observer = new Observer(); var clock = new ManualClock();
            if (fault == "published") observer.OnPublished = (_, _) => throw new IOException("private native observer failure");
            if (fault == "retired") observer.OnRetired = (_, _) => throw new IOException("private native retirement failure");
            await using var fixture = new Fixture(observer, clock: clock, fault: fault);
            var scope = fixture.Ui.OpenScope(new Context()); var answer = scope.ConfirmAsync(fault, "deny", new(20)).AsTask();
            if (fault == "retired")
            {
                await observer.Publication.Task.WaitAsync(Deadline); fixture.Reply(observer.Published[0].Identity.RequestId, confirmed: true);
                try { await answer.WaitAsync(Deadline); throw new InvalidOperationException("Retirement fault supplied approval."); }
                catch (IOException error) { Check(!error.Message.Contains("private", StringComparison.Ordinal), "Observer failure leaked private diagnostics."); }
            }
            else Equal(ExtensionUiUnavailableReason.OutputFailed, (await answer.WaitAsync(Deadline)).UnavailableReason);
            Equal(1, observer.Retired.Count); Equal(0, clock.ActiveTimers);
            Equal(fault is "published" or "retired", observer.Retired[0].PublicationSucceeded);
            Equal(fault is "published" or "retired", observer.Retired[0].PresentationEntered);
            Check(fixture.Failures.Contains(fault == "retired" ? RpcDispatchFailure.CleanupFailed : RpcDispatchFailure.OutputFailed), "Callback/publication fault did not poison the host.");
            await scope.DisposeAsync();
        }
    }

    private sealed record Context(string OwnerId = "native-presentation-owner", long OwnerGeneration = 1,
        CancellationToken OperationCancellationToken = default, CancellationToken SessionCancellationToken = default,
        CancellationToken ExtensionLifetimeCancellationToken = default) : IExtensionContext;
    private sealed class Observer : IRpcExtensionUiPresentationObserver
    {
        public readonly List<RpcExtensionUiPresentation> Published = [];
        public readonly List<RpcExtensionUiRetirement> Retired = [];
        public TaskCompletionSource Publication = Signal();
        public Func<RpcExtensionUiPresentation, CancellationToken, Task>? OnPublished;
        public Func<RpcExtensionUiRetirement, CancellationToken, Task>? OnRetired;
        public async ValueTask PublishedAsync(RpcExtensionUiPresentation presentation, CancellationToken token)
        { Published.Add(presentation); if (OnPublished is not null) await OnPublished(presentation, token); Publication.TrySetResult(); }
        public async ValueTask RetiredAsync(RpcExtensionUiRetirement retirement, CancellationToken token)
        { Retired.Add(retirement); if (OnRetired is not null) await OnRetired(retirement, token); }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public readonly RpcExtensionUiCoordinator Ui;
        public readonly JsonlWriter Writer;
        public readonly Output Output;
        public readonly List<RpcDispatchFailure> Failures = [];
        public Fixture(Observer observer, TimeProvider? clock = null, bool holdFlush = false, string? fault = null, RpcExtensionUiOptions? options = null)
        {
            Output = new(holdFlush, fault); Writer = new(Output);
            Ui = new(options, clock, 7, 11, presentationObserver: observer);
            Ui.Attach(async (record, suppress, connection) =>
            {
                suppress.ThrowIfCancellationRequested();
                await Writer.WriteAsync(record, connection);
            }, failure => Failures.Add(failure), 1_048_576);
        }
        public void Reply(string id, bool confirmed) => Ui.AcceptResponse(JsonData.Parse(JsonSerializer.Serialize(new { type = "extension_ui_response", id, confirmed })));
        public async ValueTask DisposeAsync()
        {
            Output.FlushRelease.TrySetResult();
            try { await Ui.DisposeAsync(); } catch (IOException) { /* Fault fixtures assert the actual fault above. */ }
            await Writer.DisposeAsync();
        }
    }
    private sealed class Output(bool holdFlush, string? fault) : Stream
    {
        public JsonData? Written;
        public readonly TaskCompletionSource FlushEntered = Signal(), FlushRelease = Signal();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); if (fault == "write") throw new IOException("private actual write fault");
            Written = JsonData.Parse(Encoding.UTF8.GetString(buffer.Span)); return ValueTask.CompletedTask;
        }
        public override async Task FlushAsync(CancellationToken token)
        { FlushEntered.TrySetResult(); if (holdFlush) await FlushRelease.Task.WaitAsync(token); if (fault == "flush") throw new IOException("private actual flush fault"); }
        public override bool CanRead => false; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException(); public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
    }
    private sealed class ManualClock : TimeProvider
    {
        private Timer? timer;
        public int ActiveTimers => timer is { Disposed: false } ? 1 : 0;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { Check(period == Timeout.InfiniteTimeSpan, "Native observation invented a recurring timer."); return timer = new(callback, state); }
        public void Fire() { Check(timer is { Disposed: false }, "Actual coordinator timer was absent."); timer!.Fire(); }
        private sealed class Timer(TimerCallback callback, object? state) : ITimer
        {
            public bool Disposed; private bool fired;
            public void Fire() { if (!fired && !Disposed) { fired = true; callback(state); } }
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => Disposed = true; public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
    private static string Id(JsonData record) => record.Value.GetProperty("id").GetString()!;
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Identity(RpcExtensionUiPresentationIdentity actual, string id, long owner)
    { Equal(id, actual.RequestId); Equal(7L, actual.ConnectionGeneration); Equal(11L, actual.SessionGeneration); Equal("native-presentation-owner", actual.OwnerId); Equal(owner, actual.OwnerGeneration); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}, actual {actual}.");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
