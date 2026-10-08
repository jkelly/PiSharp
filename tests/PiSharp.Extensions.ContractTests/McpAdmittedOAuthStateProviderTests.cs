using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Authentication;
using PiSharp.Extensions.Runtime.Mcp.Authentication;

// Source-only controls. Root owns Cases registration and every execution.
internal static class McpAdmittedOAuthStateProviderTests
{
    internal const string Prefix = "mcp-oauth-state.";
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        (Prefix + "exact-server-isolation-and-foreign-state-replacement", Isolation),
        (Prefix + "token-expiry-capture-and-absent-expiry-reset", Expiry),
        (Prefix + "selective-invalidation-preserves-unrelated-values", Invalidation),
        (Prefix + "held-write-fences-read-and-preserves-ordered-updates", Ordering),
        (Prefix + "write-failure-is-sticky-and-retains-complete-original", StickyFailure),
        (Prefix + "missing-verifier-and-synchronous-store-failure", SynchronousFailure)
    ];

    private const string Url = "https://inert.invalid/mcp";
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("OAuth state control failed."); }
    private static McpAdmittedOAuthStateProvider Provider(Store store, Func<double>? clock = null) =>
        new(new Uri(Url), store, clock ?? (() => 1000));
    private static async Task<T> Failure<T>(Task task) where T : Exception
    { try { await task; } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }

    private sealed class Store : IMcpAdmittedOAuthStateStore
    {
        internal McpOAuthState? Value;
        internal int Loads;
        internal int Saves;
        internal Func<ValueTask<McpOAuthState?>>? Load;
        internal Func<McpOAuthState, ValueTask>? Save;
        public ValueTask<McpOAuthState?> LoadAsync() { Loads++; return Load?.Invoke() ?? ValueTask.FromResult(Value); }
        public ValueTask SaveAsync(McpOAuthState state)
        { Saves++; if (Save is not null) return Save(state); Value = state; return ValueTask.CompletedTask; }
    }

    private static async Task Isolation()
    {
        var foreign = new McpOAuthState(Url + "/", Tokens: new("synthetic-foreign", "Bearer"), CodeVerifier: "foreign");
        var store = new Store { Value = foreign }; var provider = Provider(store);
        Require((await provider.ReadAsync()) == new McpOAuthState(Url));
        Require(ReferenceEquals(store.Value, foreign));
        await provider.SaveCodeVerifierAsync("synthetic-local");
        Require(store.Value?.ServerUrl == Url && store.Value.Tokens is null && store.Value.CodeVerifier == "synthetic-local");
    }

    private static async Task Expiry()
    {
        var now = 1234d; var store = new Store(); var provider = Provider(store, () => now);
        await provider.SaveTokensAsync(new("synthetic-a", "Bearer", 2.5));
        Require((await provider.ReadAsync()).TokensExpireAt == 3734);
        now = 9000; await provider.SaveTokensAsync(new("synthetic-b", "Bearer"));
        var state = await provider.ReadAsync();
        Require(state.TokensExpireAt is null && state.Tokens?.AccessToken == "synthetic-b");
        Require(!state.ToString().Contains("synthetic", StringComparison.Ordinal));
    }

    private static async Task Invalidation()
    {
        var metadata = JsonData.Parse("{\"unknown\":9007199254740993}");
        var initial = new McpOAuthState(Url, metadata, new("synthetic", "Bearer"), 4000, "verifier", "state", metadata);
        foreach (var kind in Enum.GetValues<McpOAuthInvalidation>())
        {
            var store = new Store { Value = initial }; var provider = Provider(store);
            await provider.InvalidateAsync(kind); var actual = await provider.ReadAsync();
            var expected = kind switch
            {
                McpOAuthInvalidation.All => new McpOAuthState(Url),
                McpOAuthInvalidation.Client => initial with { ClientInformation = null },
                McpOAuthInvalidation.Tokens => initial with { Tokens = null, TokensExpireAt = null },
                McpOAuthInvalidation.Verifier => initial with { CodeVerifier = null },
                McpOAuthInvalidation.Discovery => initial with { Discovery = null },
                _ => throw new InvalidOperationException()
            };
            Require(actual == expected);
        }
    }

    private static async Task Ordering()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new Store(); var physicalOriginals = new List<Task>();
        async Task PhysicalSave(McpOAuthState state)
        { if (store.Saves == 1) { entered.TrySetResult(); await release.Task; } store.Value = state; }
        store.Save = state => { var original = PhysicalSave(state); physicalOriginals.Add(original); return new(original); };
        var provider = Provider(store); Task? first = null; Task? second = null; Task<McpOAuthState>? read = null;
        Exception? primary = null;
        try
        {
            first = provider.SaveCodeVerifierAsync("synthetic-held");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            second = provider.SaveDiscoveryAsync(JsonData.Parse("{\"revision\":2}")); read = provider.ReadAsync();
            Require(!first.IsCompleted && !second.IsCompleted && !read.IsCompleted && store.Saves == 1);
            release.TrySetResult(); await first; await second; var state = await read;
            Require(state.CodeVerifier == "synthetic-held" && state.Discovery?.Value.GetProperty("revision").GetInt32() == 2);
        }
        catch (Exception error) { primary = error; }
        finally { release.TrySetResult(); }
        // Joining the requests first settles every physical store invocation before its list is read.
        var failures = await CollectJoined(primary, new Task?[] { first, second, read }.OfType<Task>());
        failures.AddRange(await CollectJoined(null, physicalOriginals));
        Rethrow(failures);
        Require(physicalOriginals.Count == 2 && physicalOriginals.All(task => task.IsCompletedSuccessfully));
        await JoinFailureEvidence();
    }

    private static async Task<List<Exception>> CollectJoined(Exception? primary, IEnumerable<Task> originals)
    {
        var failures = new List<Exception>(); if (primary is not null) failures.Add(primary);
        foreach (var original in originals)
        {
            try { await original; }
            catch (Exception observed) { failures.Add(original.IsFaulted ? original.Exception! : observed); }
        }
        return failures;
    }
    private static void Rethrow(List<Exception> failures)
    {
        if (failures.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException("OAuth control and joined originals failed.", failures);
    }
    private static async Task JoinFailureEvidence()
    {
        var primary = new InvalidOperationException("synthetic primary assertion");
        var shared = new IOException("synthetic shared store fault");
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.SetException(new Exception[] { shared, shared }); second.SetException(new Exception[] { primary, shared });
        var failures = await CollectJoined(primary, new[] { first.Task, second.Task });
        Require(failures.Count == 3 && ReferenceEquals(failures[0], primary) && first.Task.IsFaulted && second.Task.IsFaulted);
        var firstInventory = (AggregateException)failures[1]; var secondInventory = (AggregateException)failures[2];
        Require(firstInventory.InnerExceptions.Count == 2 && ReferenceEquals(firstInventory.InnerExceptions[0], shared) &&
            ReferenceEquals(firstInventory.InnerExceptions[1], shared) && secondInventory.InnerExceptions.Count == 2 &&
            ReferenceEquals(secondInventory.InnerExceptions[0], primary) && ReferenceEquals(secondInventory.InnerExceptions[1], shared));
        try { Rethrow(failures); }
        catch (AggregateException combined)
        { Require(combined.InnerExceptions.Count == 3 && ReferenceEquals(combined.InnerExceptions[0], primary) &&
            ReferenceEquals(combined.InnerExceptions[1], firstInventory) && ReferenceEquals(combined.InnerExceptions[2], secondInventory)); return; }
        throw new InvalidOperationException("Expected retained join evidence.");
    }

    private static async Task StickyFailure()
    {
        var nested = new AggregateException(new InvalidOperationException("synthetic-inner"));
        var original = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        original.SetException(new Exception[] { nested, nested });
        var store = new Store { Save = _ => new ValueTask(original.Task) }; var provider = Provider(store);
        var failure = await Failure<McpOAuthStoreException>(provider.SaveCodeVerifierAsync("synthetic"));
        Require(ReferenceEquals(failure.Original, original.Task));
        var evidence = (AggregateException)failure.Evidence;
        Require(evidence.InnerExceptions.Count == 2 && ReferenceEquals(evidence.InnerExceptions[0], nested) &&
            ReferenceEquals(evidence.InnerExceptions[1], nested));
        var loads = store.Loads;
        Require(ReferenceEquals(await Failure<McpOAuthStoreException>(provider.ReadAsync()), failure));
        Require(ReferenceEquals(await Failure<McpOAuthStoreException>(provider.SaveDiscoveryAsync(JsonData.EmptyObject)), failure));
        Require(store.Loads == loads && store.Saves == 1);
        await StoreOriginalCancellationStatus();
    }

    private static async Task StoreOriginalCancellationStatus()
    {
        var cause = new OperationCanceledException("synthetic faulted clear-token OCE");
        var faulted = Task.FromException(cause);
        using var canceledSource = new CancellationTokenSource(); canceledSource.Cancel();
        var canceled = Task.FromCanceled(canceledSource.Token);
        foreach (var original in new[] { faulted, canceled })
        {
            var store = new Store { Save = _ => new ValueTask(original) }; var provider = Provider(store);
            var update = provider.SaveCodeVerifierAsync("synthetic-original-status");
            var failure = await Failure<McpOAuthStoreException>(update);
            Require(update.IsFaulted && !update.IsCanceled && ReferenceEquals(failure.Original, original));
            if (ReferenceEquals(original, faulted))
            { Require(original.IsFaulted && ReferenceEquals(((AggregateException)failure.Evidence).InnerExceptions[0], cause)); }
            else
            { Require(original.IsCanceled && failure.Evidence is OperationCanceledException canceledEvidence &&
                canceledEvidence.CancellationToken == canceledSource.Token); }
            var read = provider.ReadAsync(); var readFailure = await Failure<McpOAuthStoreException>(read);
            var later = provider.SaveDiscoveryAsync(JsonData.EmptyObject); var laterFailure = await Failure<McpOAuthStoreException>(later);
            Require(ReferenceEquals(readFailure, failure) && ReferenceEquals(laterFailure, failure));
            Require(read.IsFaulted && later.IsFaulted && store.Loads == 1 && store.Saves == 1);
        }
    }

    private static async Task SynchronousFailure()
    {
        var store = new Store(); var provider = Provider(store);
        await Failure<InvalidOperationException>(provider.CodeVerifierAsync());
        var original = new IOException("synthetic store failure"); store.Load = () => throw original;
        var failure = await Failure<McpOAuthStoreException>(provider.ReadAsync());
        Require(failure.Original is null && ReferenceEquals(failure.Evidence, original));
        store.Load = null; await provider.SaveCodeVerifierAsync("synthetic-after-read-failure");
        Require(await provider.CodeVerifierAsync() == "synthetic-after-read-failure");
        var cause = new OperationCanceledException("synthetic faulted clear-token load OCE");
        var faulted = Task.FromException<McpOAuthState?>(cause);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        var canceled = Task.FromCanceled<McpOAuthState?>(stop.Token);
        foreach (var loadOriginal in new[] { faulted, canceled })
        {
            var statusStore = new Store { Load = () => new(loadOriginal) }; var statusProvider = Provider(statusStore);
            var read = statusProvider.ReadAsync(); var observed = await Failure<McpOAuthStoreException>(read);
            Require(read.IsFaulted && ReferenceEquals(observed.Original, loadOriginal));
            if (ReferenceEquals(loadOriginal, faulted))
                Require(loadOriginal.IsFaulted && ReferenceEquals(((AggregateException)observed.Evidence).InnerExceptions[0], cause));
            else Require(loadOriginal.IsCanceled && observed.Evidence is OperationCanceledException cancellation && cancellation.CancellationToken == stop.Token);
            // A failed direct load, including canceled originals, must not poison later writes.
            statusStore.Load = null; await statusProvider.SaveCodeVerifierAsync("synthetic-recovered-load");
            Require(await statusProvider.CodeVerifierAsync() == "synthetic-recovered-load" && statusStore.Saves == 1);
        }
    }
}
