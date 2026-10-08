using System.Text.Json;
using PiSharp.AI.Authentication.OAuth;

// Held synthetic controls. No store, listener, provider requests, token acquisition or ambient inputs.
internal static class OAuthLifecycleTests
{
    private const long Now = 1_000_000;
    private static OAuthCredentialSnapshot Credential(string marker = "SYNTHETIC_ACCESS", long lifetime = 600_000) =>
        new(marker, "SYNTHETIC_REFRESH", Now + lifetime);
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("OAuth assertion failed."); }
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Clock : TimeProvider
    {
        private readonly List<DeadlineTimer> timers = [];
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Now);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Require(dueTime == StoredOAuthLifecycle.RefreshTimeout);
            var timer = new DeadlineTimer(callback, state); timers.Add(timer); return timer;
        }
        public void FireDeadlines() { foreach (var timer in timers.ToArray()) timer.Fire(); }
        private sealed class DeadlineTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool disposed;
            public void Fire() { if (!disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !disposed;
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
    private sealed class Refresh(Func<string, OAuthCredentialSnapshot, CancellationToken, Task<OAuthCredentialSnapshot>> run)
        : IAdmittedOAuthRefresh
    {
        public int Calls { get; private set; }
        public Task<OAuthCredentialSnapshot> RefreshAsync(string provider, OAuthCredentialSnapshot current, CancellationToken token)
        { Calls++; return run(provider, current, token); }
    }
    private sealed class Source : IAdmittedOAuthCredentialSource
    {
        private readonly Dictionary<string, OAuthCredentialSnapshot> values = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SemaphoreSlim> lanes = new(StringComparer.Ordinal);
        private readonly object gate = new();
        public int Writes { get; private set; }
        public Func<string, CancellationToken, Task<OAuthCredentialSnapshot?>>? ReadOverride { get; set; }
        public Func<string, Func<OAuthCredentialSnapshot?, CancellationToken, Task<OAuthCredentialSnapshot?>>,
            CancellationToken, Task<OAuthCredentialSnapshot?>>? ModifyOverride { get; set; }
        public void Seed(string provider, OAuthCredentialSnapshot value) { lock (gate) values[provider] = value; }
        public void Remove(string provider) { lock (gate) values.Remove(provider); }
        public Task<OAuthCredentialSnapshot?> ReadAsync(string provider, CancellationToken token)
        {
            if (ReadOverride is { } read) return read(provider, token);
            token.ThrowIfCancellationRequested();
            lock (gate) return Task.FromResult(values.GetValueOrDefault(provider));
        }
        public Task<OAuthCredentialSnapshot?> ModifyAsync(string provider,
            Func<OAuthCredentialSnapshot?, CancellationToken, Task<OAuthCredentialSnapshot?>> mutation, CancellationToken token)
        {
            if (ModifyOverride is { } modify) return modify(provider, mutation, token);
            return ModifyCoreAsync(provider, mutation, token);
        }
        private async Task<OAuthCredentialSnapshot?> ModifyCoreAsync(string provider,
            Func<OAuthCredentialSnapshot?, CancellationToken, Task<OAuthCredentialSnapshot?>> mutation, CancellationToken token)
        {
            SemaphoreSlim lane;
            lock (gate)
            {
                if (!lanes.TryGetValue(provider, out lane!)) lanes[provider] = lane = new(1, 1);
            }
            await lane.WaitAsync(token);
            try
            {
                OAuthCredentialSnapshot? current;
                lock (gate) current = values.GetValueOrDefault(provider);
                var next = await mutation(current, token);
                token.ThrowIfCancellationRequested();
                lock (gate)
                {
                    if (next is not null) { values[provider] = next; Writes++; }
                    return values.GetValueOrDefault(provider);
                }
            }
            finally { lane.Release(); }
        }
    }
    private static StoredOAuthLifecycle Owner(Source source, Refresh refresh) => new(source, refresh, new Clock());
    private static Refresh NeverRefresh() => new((_, _, _) => throw new InvalidOperationException("Unexpected refresh."));
    private static void RejectReentry(Func<Task<OAuthCredentialSnapshot?>> invoke)
    {
        try { _ = invoke(); Require(false); }
        catch (OAuthLifecycleException error) { Require(error.Failure == OAuthLifecycleFailure.CallbackReentry); }
    }

    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("oauth-snapshot-copy-and-redacted-material", () =>
        {
            var metadata = new Dictionary<string, string> { ["enterpriseUrl"] = "SYNTHETIC_PRIVATE_HOST" };
            var snapshot = new OAuthCredentialSnapshot("SYNTHETIC_ACCESS", "SYNTHETIC_REFRESH", Now, metadata);
            metadata["enterpriseUrl"] = "changed";
            Require(snapshot.ProviderData["enterpriseUrl"] == "SYNTHETIC_PRIVATE_HOST");
            var text = snapshot + JsonSerializer.Serialize(snapshot);
            Require(!text.Contains("SYNTHETIC_", StringComparison.Ordinal));
            return Task.CompletedTask;
        });
        await check("oauth-missing-and-fresh-skip-refresh", async () =>
        {
            var source = new Source(); var refresh = NeverRefresh(); var owner = Owner(source, refresh);
            Require(await owner.ResolveAsync("provider") is null);
            var fresh = Credential(); source.Seed("provider", fresh);
            Require(ReferenceEquals(await owner.ResolveAsync("provider"), fresh));
            Require(refresh.Calls == 0 && source.Writes == 0);
        });
        await check("oauth-equality-rotates-and-preserves-provider-data", async () =>
        {
            var source = new Source(); var old = new OAuthCredentialSnapshot("old", "refresh", Now + 300_000,
                new Dictionary<string, string> { ["enterpriseUrl"] = "synthetic" });
            source.Seed("provider", old);
            var next = new OAuthCredentialSnapshot("rotated", "rotated-r", Now + 600_000, old.ProviderData);
            var refresh = new Refresh((provider, current, _) =>
            { Require(provider == "provider" && ReferenceEquals(current, old)); return Task.FromResult(next); });
            Require(ReferenceEquals(await Owner(source, refresh).ResolveAsync("provider"), next));
            Require(source.Writes == 1 && next.ProviderData["enterpriseUrl"] == "synthetic");
        });
        await check("oauth-concurrent-independent-owners-refresh-once", async () =>
        {
            var source = new Source(); source.Seed("provider", Credential("old", 0));
            var entered = Signal<bool>(); var finish = Signal<OAuthCredentialSnapshot>();
            var refresh = new Refresh((_, _, _) => { entered.SetResult(true); return finish.Task; });
            var first = Owner(source, refresh).ResolveAsync("provider"); await entered.Task;
            var second = Owner(source, refresh).ResolveAsync("provider");
            Require(!first.IsCompleted && !second.IsCompleted && refresh.Calls == 1);
            var next = Credential("rotated"); finish.SetResult(next);
            Require(ReferenceEquals(await first, next) && ReferenceEquals(await second, next));
            Require(refresh.Calls == 1 && source.Writes == 1);
        });
        await check("oauth-double-check-detects-logout", async () =>
        {
            var source = new Source(); var old = Credential("old", 0); source.Seed("provider", old);
            source.ReadOverride = (_, _) => { source.Remove("provider"); return Task.FromResult<OAuthCredentialSnapshot?>(old); };
            var refresh = NeverRefresh();
            Require(await Owner(source, refresh).ResolveAsync("provider") is null);
            Require(refresh.Calls == 0 && source.Writes == 0);
        });
        await check("oauth-explicit-minimum-rechecked-implicit-window-not-enforced", async () =>
        {
            foreach (var requested in new long?[] { null, 0, 600_000, long.MaxValue })
            {
                var source = new Source(); source.Seed("provider", Credential("old", 0));
                var next = Credential("short", 1);
                var refresh = new Refresh((_, _, _) => Task.FromResult(next));
                try
                {
                    Require(ReferenceEquals(await Owner(source, refresh).ResolveAsync("provider", requested), next));
                    Require(requested is null);
                }
                catch (OAuthLifecycleException error)
                { Require(requested.HasValue && error.Failure == OAuthLifecycleFailure.MinimumValidity); }
                Require(source.Writes == 1);
            }
        });
        await check("oauth-refresh-rejection-retains-all-original-task-faults-and-snapshot", async () =>
        {
            var source = new Source(); var old = Credential("old", 0); source.Seed("provider", old);
            var first = new InvalidOperationException("SYNTHETIC_SECRET_ONE");
            var second = new ArgumentException("SYNTHETIC_SECRET_TWO");
            var rejected = Signal<OAuthCredentialSnapshot>(); rejected.SetException([first, second]);
            var refresh = new Refresh((_, _, _) => rejected.Task);
            try { await Owner(source, refresh).ResolveAsync("provider"); Require(false); }
            catch (OAuthLifecycleException error)
            {
                Require(error.Failure == OAuthLifecycleFailure.Refresh && ReferenceEquals(error.OriginalException, first));
                Require(error.OriginalTaskException is { InnerExceptions.Count: 2 } aggregate
                    && ReferenceEquals(aggregate.InnerExceptions[1], second));
                Require(!(error + JsonSerializer.Serialize(error)).Contains("SYNTHETIC_SECRET", StringComparison.Ordinal));
            }
            Require(source.Writes == 0 && ReferenceEquals(await source.ReadAsync("provider", default), old));
        });
        await check("oauth-cancelled-noncooperative-original-stays-joined-and-lane-held", async () =>
        {
            var source = new Source(); source.Seed("provider", Credential("old", 0));
            var entered = Signal<bool>(); var finish = Signal<OAuthCredentialSnapshot>();
            var refresh = new Refresh((_, _, _) => { entered.SetResult(true); return finish.Task; });
            var owner = Owner(source, refresh); using var caller = new CancellationTokenSource();
            var first = owner.ResolveAsync("provider", cancellationToken: caller.Token); await entered.Task;
            caller.Cancel();
            using var queued = new CancellationTokenSource();
            var second = owner.ResolveAsync("provider", cancellationToken: queued.Token); queued.Cancel();
            Require(!first.IsCompleted && !second.IsCompleted);
            finish.SetResult(Credential("late"));
            foreach (var operation in new[] { first, second })
            {
                try { await operation; Require(false); }
                catch (OperationCanceledException) { }
            }
            Require(source.Writes == 0 && refresh.Calls == 1);
        });
        await check("oauth-original-refresh-rejection-precedes-late-cancellation", async () =>
        {
            var source = new Source(); source.Seed("provider", Credential("old", 0));
            var entered = Signal<bool>(); var finish = Signal<OAuthCredentialSnapshot>();
            var refresh = new Refresh((_, _, _) => { entered.SetResult(true); return finish.Task; });
            using var caller = new CancellationTokenSource();
            var operation = Owner(source, refresh).ResolveAsync("provider", cancellationToken: caller.Token);
            await entered.Task; caller.Cancel();
            var fault = new InvalidOperationException("SYNTHETIC_FAULT"); finish.SetException(fault);
            try { await operation; Require(false); }
            catch (OAuthLifecycleException error) { Require(ReferenceEquals(error.OriginalException, fault)); }
            Require(source.Writes == 0);
        });
        await check("oauth-deadline-keeps-original-joined-and-refuses-late-publication", async () =>
        {
            var source = new Source(); source.Seed("provider", Credential("old", 0));
            var entered = Signal<bool>(); var finish = Signal<OAuthCredentialSnapshot>();
            CancellationToken refreshToken = default;
            var refresh = new Refresh((_, _, token) => { refreshToken = token; entered.SetResult(true); return finish.Task; });
            var clock = new Clock(); var operation = new StoredOAuthLifecycle(source, refresh, clock).ResolveAsync("provider");
            await entered.Task; clock.FireDeadlines();
            Require(refreshToken.IsCancellationRequested && !operation.IsCompleted);
            finish.SetResult(Credential("late"));
            try { await operation; Require(false); }
            catch (OAuthLifecycleException error)
            { Require(error.Failure == OAuthLifecycleFailure.Refresh && error.OriginalException is TimeoutException); }
            Require(source.Writes == 0);
        });
        await check("oauth-read-rejection-retains-original-and-no-refresh", async () =>
        {
            var fault = new InvalidOperationException("SYNTHETIC_READ_FAULT");
            var source = new Source { ReadOverride = (_, _) => Task.FromException<OAuthCredentialSnapshot?>(fault) };
            var refresh = NeverRefresh();
            try { await Owner(source, refresh).ResolveAsync("provider"); Require(false); }
            catch (OAuthLifecycleException error)
            { Require(error.Failure == OAuthLifecycleFailure.Read && ReferenceEquals(error.OriginalException, fault)); }
            Require(refresh.Calls == 0);
        });
        await check("oauth-faulted-owned-token-oce-is-original-fault", async () =>
        {
            using var caller = new CancellationTokenSource();
            var rejected = Signal<OAuthCredentialSnapshot?>(); var entered = Signal<bool>();
            var source = new Source { ReadOverride = (_, _) => { entered.SetResult(true); return rejected.Task; } };
            var operation = Owner(source, NeverRefresh()).ResolveAsync("provider", cancellationToken: caller.Token);
            await entered.Task; caller.Cancel();
            var fault = new OperationCanceledException("SYNTHETIC_FAULT", caller.Token); rejected.SetException(fault);
            try { await operation; Require(false); }
            catch (OAuthLifecycleException error)
            { Require(error.Failure == OAuthLifecycleFailure.Read && ReferenceEquals(error.OriginalException, fault)); }
        });
        await check("oauth-foreign-refresh-cancellation-is-original-fault", async () =>
        {
            var source = new Source(); source.Seed("provider", Credential("old", 0));
            using var foreign = new CancellationTokenSource(); foreign.Cancel();
            var refresh = new Refresh((_, _, _) => Task.FromCanceled<OAuthCredentialSnapshot>(foreign.Token));
            try { await Owner(source, refresh).ResolveAsync("provider"); Require(false); }
            catch (OAuthLifecycleException error)
            { Require(error.Failure == OAuthLifecycleFailure.Refresh && error.OriginalException is OperationCanceledException); }
            Require(source.Writes == 0);
        });
        await check("oauth-other-provider-progresses-while-original-held", async () =>
        {
            var source = new Source(); source.Seed("first", Credential("old", 0)); source.Seed("second", Credential());
            var entered = Signal<bool>(); var finish = Signal<OAuthCredentialSnapshot>();
            var refresh = new Refresh((_, _, _) => { entered.SetResult(true); return finish.Task; });
            var owner = Owner(source, refresh); var first = owner.ResolveAsync("first"); await entered.Task;
            Require(await owner.ResolveAsync("second") is not null && !first.IsCompleted);
            finish.SetResult(Credential()); await first;
        });
        await check("oauth-reauthentication-waits-for-original-and-installs-admitted-completion", async () =>
        {
            var source = new Source(); source.Seed("provider", Credential("old", 0));
            var entered = Signal<bool>(); var finish = Signal<OAuthCredentialSnapshot>();
            var refresh = new Refresh((_, _, _) => { entered.SetResult(true); return finish.Task; });
            var owner = Owner(source, refresh); var first = owner.ResolveAsync("provider"); await entered.Task;
            var completion = Credential("reauthenticated"); var reauth = owner.ReauthenticateAsync("provider", completion);
            Require(!reauth.IsCompleted); finish.SetResult(Credential("rotated")); await first;
            Require(ReferenceEquals(await reauth, completion));
            Require(ReferenceEquals(await owner.ResolveAsync("provider"), completion) && source.Writes == 2);
        });
        await check("oauth-modify-lifecycle-first-fault-retains-sibling-original", async () =>
        {
            var source = new Source(); source.Seed("provider", Credential("old", 0));
            var refreshFault = new InvalidOperationException("SYNTHETIC_REFRESH_FAULT");
            var siblingFault = new InvalidOperationException("SYNTHETIC_MODIFY_SIBLING");
            OAuthLifecycleException? refreshFailure = null;
            Task? completion = null;
            source.ModifyOverride = (provider, mutation, token) =>
            {
                var rejected = Signal<OAuthCredentialSnapshot?>();
                completion = CompleteAsync();
                return rejected.Task;
                async Task CompleteAsync()
                {
                    try
                    {
                        try { await mutation(Credential("old", 0), token); Require(false); }
                        catch (OAuthLifecycleException error) { refreshFailure = error; }
                        rejected.SetException([refreshFailure!, siblingFault]);
                    }
                    catch (Exception unexpected)
                    {
                        // Settle the returned original even when the mutation unexpectedly succeeds
                        // or rejects with another exception. The helper itself is joined below.
                        rejected.TrySetException(unexpected);
                        throw;
                    }
                }
            };
            var refresh = new Refresh((_, _, _) => Task.FromException<OAuthCredentialSnapshot>(refreshFault));
            try { await Owner(source, refresh).ResolveAsync("provider"); Require(false); }
            catch (OAuthLifecycleException error)
            {
                Require(ReferenceEquals(error.OriginalException, refreshFault)
                    && ReferenceEquals(error.OriginalLifecycleException, refreshFailure));
                Require(error.OriginalTaskExceptions.Length == 2
                    && ReferenceEquals(error.OriginalTaskExceptions[0].InnerExceptions[0], refreshFault)
                    && ReferenceEquals(error.OriginalTaskExceptions[1].InnerExceptions[0], refreshFailure)
                    && ReferenceEquals(error.OriginalTaskExceptions[1].InnerExceptions[1], siblingFault));
                Require(!(error + JsonSerializer.Serialize(error)).Contains("SYNTHETIC_", StringComparison.Ordinal));
            }
            finally { if (completion is not null) await completion; }
            Require(source.Writes == 0);
        });
        await check("oauth-synchronous-read-modify-reauth-callback-reentry-rejected-before-queue", async () =>
        {
            foreach (var phase in new[] { "read", "modify", "reauth" })
            {
                var source = new Source(); var value = Credential("old", phase == "read" ? 600_000 : 0);
                source.Seed("provider", value); var owner = Owner(source, new Refresh((_, _, _) => Task.FromResult(Credential())));
                void Reenter()
                {
                    RejectReentry(() => owner.ResolveAsync("provider"));
                    RejectReentry(() => owner.ReauthenticateAsync("provider", Credential()));
                }
                if (phase == "read") source.ReadOverride = (_, _) => { Reenter(); return Task.FromResult<OAuthCredentialSnapshot?>(value); };
                else source.ModifyOverride = async (_, mutation, token) => { Reenter(); return await mutation(value, token); };
                Require(await (phase == "reauth" ? owner.ReauthenticateAsync("provider", Credential())
                    : owner.ResolveAsync("provider")) is not null);
                source.ReadOverride = null; source.ModifyOverride = null;
                // Rejection must not have installed another lane tail or retained stale authority.
                Require(await owner.ReauthenticateAsync("provider", Credential()) is not null);
            }
        });
        await check("oauth-deferred-refresh-execution-context-reentry-rejected", async () =>
        {
            var source = new Source(); source.Seed("provider", Credential("old", 0));
            StoredOAuthLifecycle? owner = null;
            var refresh = new Refresh(async (_, _, _) =>
            {
                await Task.Yield();
                RejectReentry(() => owner!.ResolveAsync("provider"));
                await Task.Run(async () =>
                {
                    await Task.Yield();
                    RejectReentry(() => owner!.ReauthenticateAsync("provider", Credential()));
                });
                return Credential();
            });
            owner = Owner(source, refresh);
            Require(await owner.ResolveAsync("provider") is not null && refresh.Calls == 1);
        });
        await check("oauth-nested-provider-scope-keeps-outer-provider-guard", async () =>
        {
            var source = new Source(); StoredOAuthLifecycle? owner = null;
            source.ReadOverride = async (provider, _) =>
            {
                await Task.Yield();
                if (provider == "outer") Require(await owner!.ResolveAsync("inner") is not null);
                else
                {
                    Require(provider == "inner");
                    RejectReentry(() => owner!.ResolveAsync("outer"));
                    RejectReentry(() => owner!.ResolveAsync("inner"));
                    RejectReentry(() => owner!.ReauthenticateAsync("outer", Credential()));
                }
                return Credential();
            };
            owner = Owner(source, NeverRefresh());
            Require(await owner.ResolveAsync("outer") is not null);
            source.ReadOverride = null;
            Require(await owner.ReauthenticateAsync("outer", Credential()) is not null);
        });
        await check("oauth-captured-stale-scope-loses-reentry-authority", async () =>
        {
            var source = new Source(); ExecutionContext? captured = null;
            source.ReadOverride = (_, _) => { captured = ExecutionContext.Capture(); return Task.FromResult<OAuthCredentialSnapshot?>(Credential()); };
            var owner = Owner(source, NeverRefresh()); Require(await owner.ResolveAsync("provider") is not null);
            source.ReadOverride = null; source.Seed("provider", Credential());
            Task<OAuthCredentialSnapshot?>? later = null;
            ExecutionContext.Run(captured!, _ => later = owner.ResolveAsync("provider"), null);
            Require(later is not null && await later is not null);
        });
        await check("oauth-other-owner-same-provider-borrowed-context-is-allowed", async () =>
        {
            var source = new Source(); var otherSource = new Source(); otherSource.Seed("provider", Credential());
            var otherOwner = Owner(otherSource, NeverRefresh());
            source.ReadOverride = async (_, _) => { Require(await otherOwner.ResolveAsync("provider") is not null); return Credential(); };
            Require(await Owner(source, NeverRefresh()).ResolveAsync("provider") is not null);
        });
    }
}
