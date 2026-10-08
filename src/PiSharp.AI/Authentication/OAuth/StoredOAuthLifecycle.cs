namespace PiSharp.AI.Authentication.OAuth;

/// <summary>
/// Stored OAuth owner over admitted synthetic dependencies. Keeps original tasks joined before
/// releasing a provider lane, even if a dependency ignores cancellation. Does not select env/API
/// keys, acquire tokens, run login flows, or supply storage/provider effects.
/// </summary>
public sealed class StoredOAuthLifecycle
{
    public const long DefaultMinimumValidityMilliseconds = 300_000;
    public static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(15);
    private readonly IAdmittedOAuthCredentialSource credentials;
    private readonly IAdmittedOAuthRefresh refresh;
    private readonly TimeProvider time;
    private readonly object gate = new();
    private readonly Dictionary<string, Task> tails = new(StringComparer.Ordinal);
    private static readonly AsyncLocal<CallbackScope?> callbackScope = new();
    private sealed class CallbackScope(StoredOAuthLifecycle owner, string provider, CallbackScope? parent)
    {
        public StoredOAuthLifecycle Owner { get; } = owner;
        public string Provider { get; } = provider;
        public CallbackScope? Parent { get; } = parent;
        // Shared across captured execution contexts; stale captures lose authority on completion.
        public int Active = 1;
    }

    public StoredOAuthLifecycle(IAdmittedOAuthCredentialSource credentials, IAdmittedOAuthRefresh refresh,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(credentials); ArgumentNullException.ThrowIfNull(refresh);
        this.credentials = credentials; this.refresh = refresh; time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Returns null if no stored OAuth remains. Refreshes at equality with the five-minute window.
    /// Explicit validity is clamped to that window and checked again after rotation. The implicit
    /// window only triggers refresh, matching the original provider contract.
    /// </summary>
    public Task<OAuthCredentialSnapshot?> ResolveAsync(string provider,
        long? minimumValidityMilliseconds = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(provider);
        return Enqueue(provider, () => ResolveCoreAsync(provider, minimumValidityMilliseconds, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Publishes an already admitted reauthentication completion through the same atomic source.
    /// No login, callback listener, PKCE generation or token exchange occurs here.
    /// </summary>
    public Task<OAuthCredentialSnapshot?> ReauthenticateAsync(string provider, OAuthCredentialSnapshot admittedCompletion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(provider); ArgumentNullException.ThrowIfNull(admittedCompletion);
        return Enqueue(provider, async () =>
        {
            Task<OAuthCredentialSnapshot?>? original = null;
            try
            {
                original = credentials.ModifyAsync(provider, (_, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return Task.FromResult<OAuthCredentialSnapshot?>(admittedCompletion);
                }, cancellationToken);
                var post = await original.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return post;
            }
            catch (OperationCanceledException error) when (IsOwnedCancellation(error, cancellationToken, original)) { throw; }
            catch (Exception error) { throw new OAuthLifecycleException(OAuthLifecycleFailure.Reauthentication, error, original?.Exception); }
        }, cancellationToken);
    }

    private async Task<OAuthCredentialSnapshot?> ResolveCoreAsync(string provider, long? requestedMinimum,
        CancellationToken token)
    {
        OAuthCredentialSnapshot? current;
        Task<OAuthCredentialSnapshot?>? originalRead = null;
        try
        {
            originalRead = credentials.ReadAsync(provider, token);
            current = await originalRead.ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (IsOwnedCancellation(error, token, originalRead)) { throw; }
        catch (Exception error) { throw new OAuthLifecycleException(OAuthLifecycleFailure.Read, error, originalRead?.Exception); }
        token.ThrowIfCancellationRequested();
        if (current is null) return null;
        var minimum = Math.Max(DefaultMinimumValidityMilliseconds, requestedMinimum ?? 0);
        if (!ExpiresSoon(current, minimum)) return current;

        OAuthCredentialSnapshot? post;
        Task<OAuthCredentialSnapshot?>? originalModify = null;
        try
        {
            originalModify = credentials.ModifyAsync(provider, async (authoritative, mutationToken) =>
            {
                mutationToken.ThrowIfCancellationRequested();
                if (authoritative is null || !ExpiresSoon(authoritative, minimum)) return null;
                using var deadline = new CancellationTokenSource(RefreshTimeout, time);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(mutationToken, deadline.Token);
                OAuthCredentialSnapshot replacement;
                Task<OAuthCredentialSnapshot>? originalRefresh = null;
                try
                {
                    originalRefresh = refresh.RefreshAsync(provider, authoritative, linked.Token);
                    replacement = await originalRefresh.ConfigureAwait(false);
                }
                catch (OperationCanceledException error) when (IsOwnedCancellation(error, linked.Token, originalRefresh)
                    && mutationToken.IsCancellationRequested)
                { throw new OperationCanceledException("Stored OAuth refresh cancelled.", mutationToken); }
                catch (Exception error) { throw new OAuthLifecycleException(OAuthLifecycleFailure.Refresh, error, originalRefresh?.Exception); }
                // A rejection above owns its original fault, even if cancellation arrived meanwhile.
                // A successful non-cooperative completion cannot publish after cancellation/deadline.
                mutationToken.ThrowIfCancellationRequested();
                if (deadline.IsCancellationRequested)
                    throw new OAuthLifecycleException(OAuthLifecycleFailure.Refresh,
                        new TimeoutException("Stored OAuth refresh deadline elapsed."));
                return replacement ?? throw new OAuthLifecycleException(OAuthLifecycleFailure.Refresh);
            }, token);
            post = await originalModify.ConfigureAwait(false);
        }
        catch (OAuthLifecycleException error) { throw error.RetainOriginalTask(originalModify?.Exception); }
        catch (OperationCanceledException error) when (IsOwnedCancellation(error, token, originalModify)) { throw; }
        catch (Exception error) { throw new OAuthLifecycleException(OAuthLifecycleFailure.Modify, error, originalModify?.Exception); }
        token.ThrowIfCancellationRequested();
        if (post is not null && requestedMinimum.HasValue && ExpiresSoon(post, minimum))
            throw new OAuthLifecycleException(OAuthLifecycleFailure.MinimumValidity);
        return post;
    }

    private bool ExpiresSoon(OAuthCredentialSnapshot credential, long minimum)
    {
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        // Saturate addition instead of wrapping an extreme caller minimum into a fresh result.
        var threshold = now > long.MaxValue - minimum ? long.MaxValue : now + minimum;
        return threshold >= credential.ExpiresUnixMilliseconds;
    }

    private static bool IsOwnedCancellation(OperationCanceledException error, CancellationToken token, Task? original = null) =>
        token.IsCancellationRequested && error.CancellationToken == token && (original is null || original.IsCanceled);

    private Task<OAuthCredentialSnapshot?> Enqueue(string provider, Func<Task<OAuthCredentialSnapshot?>> operation,
        CancellationToken token)
    {
        for (var scope = callbackScope.Value; scope is not null; scope = scope.Parent)
            if (Volatile.Read(ref scope.Active) != 0 && ReferenceEquals(scope.Owner, this)
                && string.Equals(scope.Provider, provider, StringComparison.Ordinal))
                throw new OAuthLifecycleException(OAuthLifecycleFailure.CallbackReentry);
        // Publish the tail before invoking user code, so synchronous injected reentry sees this lane.
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task previous;
        lock (gate)
        {
            previous = tails.GetValueOrDefault(provider) ?? Task.CompletedTask;
            tails[provider] = completed.Task;
        }
        return RunAsync();

        async Task<OAuthCredentialSnapshot?> RunAsync()
        {
            CallbackScope? borrowedScope = null;
            var previousScope = callbackScope.Value;
            try
            {
                await previous.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                borrowedScope = new(this, provider, previousScope);
                callbackScope.Value = borrowedScope;
                return await operation().ConfigureAwait(false);
            }
            finally
            {
                if (borrowedScope is not null) Interlocked.Exchange(ref borrowedScope.Active, 0);
                callbackScope.Value = previousScope;
                lock (gate)
                    if (tails.GetValueOrDefault(provider) == completed.Task) tails.Remove(provider);
                completed.SetResult();
            }
        }
    }
}
