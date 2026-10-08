using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.ExceptionServices;
using PiSharp.Compatibility.Node;
using PiSharp.Contracts;
using PiSharp.ExtensionHost.Protocol;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;

// Actual registry/typed native UI integration. Reflection accesses the production internal
// operation owner without adding a public testing facade or synthesizing an operation Task.
internal static class NativeDialogRegistryCancellationTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [ ("original-ui.registry-child-cancel-joins-held-normal-register-and-native-input", Held),
      ("original-ui.registry-dialog-authentication-tombstones-and-budget", Refusal) ];
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value) { if (!value) throw new IOException("Actual dialog registry control failed."); }
    private static readonly Type Operation = typeof(NodeCommandInputExtension).Assembly.GetType("PiSharp.Compatibility.Node.NativeDialogOperation", true)!;
    private static object Create(IExtensionContext context) => Activator.CreateInstance(Operation,
        BindingFlags.Instance | BindingFlags.NonPublic, null, [context, "dialog-test-operation"], null)!;
    private static object Call(object owner, string method, params object[] args)
    {
        try { return Operation.GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, args)!; }
        catch (TargetInvocationException error) when (error.InnerException is { } direct)
        { ExceptionDispatchInfo.Capture(direct).Throw(); throw; }
    }
    private static string Scope(object owner) => (string)Operation.GetProperty("ScopeId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    internal sealed record CapturedOriginal(string Phase, Task Original, AggregateException? Aggregate, Exception? Direct);
    private static readonly List<CapturedOriginal> evidence = [];
    internal static CapturedOriginal[] RawCapturedOriginals { get { lock (evidence) return evidence.ToArray(); } }
    private sealed class Record(string phase, Task original)
    {
        internal readonly string Phase = phase; internal readonly Task Original = original;
        internal AggregateException? Aggregate; internal Exception? Direct; internal bool Joined, Captured;
    }
    private sealed class Ledger
    {
        private readonly Dictionary<Task, Record> records = new(ReferenceEqualityComparer.Instance);
        internal Task Add(string phase, Task original) { lock (records) records.TryAdd(original, new(phase, original)); return original; }
        internal async Task Join(Task original)
        {
            Record record; lock (records) { record = records[original]; if (record.Joined) return; }
            try { await original.ConfigureAwait(false); }
            catch (Exception error)
            {
                lock (records)
                {
                    record.Direct ??= error;
                    if (!record.Captured) { record.Captured = true; if (original.IsFaulted) record.Aggregate = original.Exception; }
                }
            }
            finally { lock (records) record.Joined = true; }
        }
        internal async Task Finish(List<Exception> errors, Task? expectedCancelled, CancellationToken expectedToken)
        {
            while (true)
            {
                Task[] pending; lock (records) pending = records.Values.Where(value => !value.Joined).Select(value => value.Original).ToArray();
                if (pending.Length == 0) break;
                foreach (var original in pending) await Join(original);
            }
            lock (records)
            {
                foreach (var record in records.Values)
                {
                    lock (evidence) evidence.Add(new(record.Phase, record.Original, record.Aggregate, record.Direct));
                    if (record.Direct is null) continue;
                    if (ReferenceEquals(record.Original, expectedCancelled) && record.Original.IsCanceled &&
                        record.Direct is OperationCanceledException cancellation && cancellation.CancellationToken == expectedToken &&
                        expectedToken.IsCancellationRequested && errors.Count == 0) continue;
                    errors.Add(record.Aggregate ?? record.Direct);
                }
            }
        }
    }
    private static async Task Held()
    {
        var ledger = new Ledger(); var errors = new List<Exception>();
        var provider = new Provider(ledger); var registry = new ExtensionRegistry(null, provider);
        RegistrationScope? registration = null; object? owner = null;
        var ready = Gate(); var releaseOwner = Gate();
        Task? input = null; bool canceledPredicatePassed = false;
        provider.OnCancel = () =>
        {
            try { _ = Call(owner!, "CloseAsync"); throw new IOException("Cancellation handler was allowed to join its own dialog."); }
            catch (ExtensionRegistrationException error) when (error.Failure == ExtensionRegistrationFailure.ReentrantDisposal)
            { provider.ReentryRefused = true; }
        };
        try
        {
            var activation = registry.ActivateAsync("actual-dialog-owner", new Extension(entries => entries.RegisterCommand(new("dialogs", "dialogs", "",
                async (_, context, _) => { owner = Create(context); ready.TrySetResult(); await releaseOwner.Task; }))));
            _ = ledger.Add("activation", activation); registration = await activation;
            var invocation = registry.InvokeCommandAsync(registry.CaptureSnapshot(), "dialogs", JsonData.EmptyObject).AsTask(); _ = ledger.Add("registry-command", invocation);
            await Stage(ready.Task, invocation);
            var reserved = (WorkerValue)Call(owner!, "Reserve", "dialog-1", "ui.input");
            Check(reserved.Json!.Value.GetProperty("reserved").GetBoolean());
            var open = (Task<WorkerValue>)Call(owner!, "Open", "dialog-1", Scope(owner!), 7L, "ui.input", JsonData.Parse("[\"actual\",\"placeholder\",{\"timeout\":1234}]"), true, CancellationToken.None);
            _ = ledger.Add("dialog-open", open); await Stage(provider.Entered.Task, open);
            input = provider.InputOriginal; Check(input is not null && !input.IsCompleted && provider.Timeout == 1234);
            var cancelled = (WorkerValue)Call(owner!, "Cancel", "dialog-1", Scope(owner!), 7L);
            Check(cancelled.Json!.Value.GetProperty("cancelRequested").GetBoolean());
            await Stage(provider.CancelEntered.Task, open); Check(provider.ReentryRefused);
            provider.ReleaseInput.TrySetResult();
            var retirement = (Task<WorkerValue>)Call(owner!, "Retire", "dialog-1", Scope(owner!), 7L); _ = ledger.Add("dialog-retire", retirement);
            Check(!retirement.IsCompleted && !provider.RegistrationWorker!.IsFaulted);
            provider.ReleaseCancel.TrySetResult(); await open; await retirement;
            Check(open.Result.Json!.Value.GetProperty("outcome").GetString() == "cancelled" &&
                open.Result.Json.Value.GetProperty("presence").GetString() == "undefined" && input!.IsCanceled && provider.Token.IsCancellationRequested);
            canceledPredicatePassed = true;
        }
        catch (Exception error) { errors.Add(error); }
        finally
        {
            provider.ReleaseInput.TrySetResult(); provider.ReleaseCancel.TrySetResult();
            try { if (owner is not null) { var close = (Task)Call(owner, "CloseAsync"); _ = ledger.Add("operation-retire", close); await ledger.Join(close); } }
            catch (Exception error) { errors.Add(error); }
            releaseOwner.TrySetResult();
            await ledger.Finish(errors, canceledPredicatePassed ? input : null, provider.Token);
            try { if (registration is not null) { var close = registration.DisposeAsync().AsTask(); _ = ledger.Add("scope-retire", close); await ledger.Join(close); } }
            catch (Exception error) { errors.Add(error); }
            try { var close = registry.DisposeAsync().AsTask(); _ = ledger.Add("registry-retire", close); await ledger.Join(close); }
            catch (Exception error) { errors.Add(error); }
            await ledger.Finish(errors, canceledPredicatePassed ? input : null, provider.Token);
        }
        if (errors.Count != 0) throw new AggregateException("Actual dialog criteria and original fault inventory.", errors);
    }
    private static async Task Refusal()
    {
        var ledger = new Ledger(); var errors = new List<Exception>(); var registry = new ExtensionRegistry();
        RegistrationScope? registration = null; object? owner = null;
        try
        {
            var activation = registry.ActivateAsync("dialog-budget", new Extension(entries => entries.RegisterCommand(new("allocate", "allocate", "",
                async (_, context, _) =>
                {
                    owner = Create(context); var scope = Scope(owner);
                    for (var index = 1; index <= 16; index++) _ = Call(owner, "Reserve", "dialog-" + index, "ui.input");
                    Refused(() => Call(owner, "Reserve", "dialog-17", "ui.input"));
                    Refused(() => Call(owner, "Reserve", "dialog-1", "ui.input"));
                    Refused(() => Call(owner, "Cancel", "dialog-1", "foreign-scope", 0L));
                    Refused(() => Call(owner, "Cancel", "dialog-1", scope, 1L));
                    var retire = (Task)Call(owner, "Retire", "dialog-1", scope, 0L); _ = ledger.Add("entry-retire", retire); await retire;
                    Refused(() => Call(owner, "Cancel", "dialog-1", scope, 0L));
                    Refused(() => Call(owner, "Reserve", "dialog-1", "ui.input"));
                }))));
            _ = ledger.Add("activation", activation); registration = await activation;
            var invocation = registry.InvokeCommandAsync(registry.CaptureSnapshot(), "allocate", JsonData.EmptyObject).AsTask(); _ = ledger.Add("registry-command", invocation); await invocation;
        }
        catch (Exception error) { errors.Add(error); }
        finally
        {
            try { if (owner is not null) { var close = (Task)Call(owner, "CloseAsync"); _ = ledger.Add("operation-retire", close); await ledger.Join(close); } } catch (Exception error) { errors.Add(error); }
            try { if (registration is not null) { var close = registration.DisposeAsync().AsTask(); _ = ledger.Add("scope-retire", close); await ledger.Join(close); } } catch (Exception error) { errors.Add(error); }
            try { var close = registry.DisposeAsync().AsTask(); _ = ledger.Add("registry-retire", close); await ledger.Join(close); } catch (Exception error) { errors.Add(error); }
            await ledger.Finish(errors, null, default);
        }
        if (errors.Count != 0) throw new AggregateException(errors);
    }
    private static void Refused(Func<object> action)
    { try { _ = action(); } catch (InvalidOperationException) { return; } throw new IOException("Dialog authority or tombstone check failed."); }
    private static async Task Stage(Task entered, Task original)
    { var winner = await Task.WhenAny(entered, original).WaitAsync(TimeSpan.FromSeconds(10)); Check(ReferenceEquals(winner, entered)); await entered; }
    private sealed class Extension(Action<IExtensionRegistry> initialize) : IPiSharpExtension
    { public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) { initialize(registry); return ValueTask.CompletedTask; } public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    private sealed class Provider(Ledger sourceLedger) : IExtensionUiProvider
    {
        private readonly Ledger ledger = sourceLedger;
        internal readonly TaskCompletionSource Entered = Gate(), ReleaseInput = Gate(), CancelEntered = Gate(), ReleaseCancel = Gate();
        internal Task? InputOriginal, RegistrationWorker; internal CancellationToken Token;
        internal double? Timeout; internal bool ReentryRefused; internal Action OnCancel = () => { };
        public IExtensionUiScope OpenScope(IExtensionContext context) => new Scope(this);
        private async Task<ExtensionUiOutcome<string>> Input(CancellationToken token)
        {
            Token = token; CancellationTokenRegistration registration = default;
            try
            {
                Task<CancellationTokenRegistration> worker;
                using (ExecutionContext.SuppressFlow()) worker = Task.Run(() => token.Register(() => { OnCancel(); CancelEntered.TrySetResult(); ReleaseCancel.Task.GetAwaiter().GetResult(); }));
                RegistrationWorker = worker; _ = ledger.Add("frame-free-normal-register", worker); registration = await worker;
                Entered.TrySetResult(); await ReleaseInput.Task; token.ThrowIfCancellationRequested();
                return ExtensionUiOutcome<string>.FromValue("unexpected uncanceled UI");
            }
            finally { registration.Dispose(); }
        }
        private sealed class Scope(Provider owner) : IExtensionUiScope
        {
            public ExtensionUiCapabilities Capabilities => new(ExtensionUiMode.Tui, 1, 7, [ExtensionUiFeature.Input]);
            public ValueTask<ExtensionUiOutcome<string>> InputAsync(string title, string? placeholder = null, ExtensionUiDialogOptions? options = null, CancellationToken cancellationToken = default)
            { owner.Timeout = options?.TimeoutMilliseconds; var original = owner.Input(cancellationToken); owner.InputOriginal = original; _ = owner.ledger.Add("actual-ui-input", original); return new(original); }
            public ValueTask<ExtensionUiOutcome<string>> SelectAsync(string title, ImmutableArray<string> choices, ExtensionUiDialogOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public ValueTask<ExtensionUiOutcome<bool>> ConfirmAsync(string title, string message, ExtensionUiDialogOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public ValueTask<ExtensionUiOutcome<string>> EditorAsync(string title, string? prefill = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public ValueTask<ExtensionUiOutcome<ExtensionUiPublication>> PublishAsync(ExtensionUiNotification notification, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
