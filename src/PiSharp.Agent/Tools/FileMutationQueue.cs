namespace PiSharp.Agent.Tools;

/// <summary>Trusted host resolution. Return the same exact key for targets that must serialize.</summary>
public delegate ValueTask<string> FileMutationKeyResolver(string path, CancellationToken cancellationToken);

public sealed record FileMutationQueueOptions(
    int MaximumRegisteredOperations = 128, int MaximumKeys = 128, int MaximumKeyCharacters = 4096);

public enum FileMutationQueueLimit { RegisteredOperations, Keys, KeyCharacters }

public sealed class FileMutationQueueLimitException : Exception
{
    public FileMutationQueueLimit Limit { get; }
    internal FileMutationQueueLimitException(FileMutationQueueLimit limit)
        : base("File mutation queue exceeds a configured logical limit.") => Limit = limit;
}

/// <summary>Immutable instantaneous counts, including canceled reservations that have not settled.</summary>
public sealed record FileMutationQueueSnapshot(int RegisteredOperations, int RegisteredKeys);

/// <summary>
/// Bounded, instance-scoped keyed FIFO. Key resolution registers in admission order; operations for
/// different keys may overlap. The host supplies filesystem identity and awaits its own effect cleanup.
/// No user callback runs under a state lock, and cancellation never detaches an admitted operation.
/// </summary>
public sealed class FileMutationQueue
{
    private readonly object _gate = new();
    private readonly FileMutationKeyResolver _resolveKey;
    private readonly FileMutationQueueOptions _options;
    private readonly Dictionary<string, Reservation> _tails = new(StringComparer.Ordinal);
    private Task _registrationTail = Task.CompletedTask;
    private int _registeredOperations;

    private sealed record Reservation(string Key, Task Predecessor, TaskCompletionSource Finished);

    public FileMutationQueue(FileMutationKeyResolver resolveKey, FileMutationQueueOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(resolveKey);
        _options = options ?? new();
        if (_options.MaximumRegisteredOperations <= 0 || _options.MaximumKeys <= 0 || _options.MaximumKeyCharacters <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Invalid file mutation queue limits.");
        _resolveKey = resolveKey;
    }

    public FileMutationQueueSnapshot Snapshot
    {
        get { lock (_gate) return new(_registeredOperations, _tails.Count); }
    }

    public Task<T> RunAsync<T>(string path, Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateKey(path, nameof(path));
        Task previousRegistration;
        TaskCompletionSource registrationFinished;
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_registeredOperations >= _options.MaximumRegisteredOperations)
                throw new FileMutationQueueLimitException(FileMutationQueueLimit.RegisteredOperations);
            registrationFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
            previousRegistration = _registrationTail;
            _registrationTail = registrationFinished.Task;
            _registeredOperations++;
        }
        return RunRegisteredAsync(path, operation, cancellationToken, previousRegistration, registrationFinished);
    }

    private async Task<T> RunRegisteredAsync<T>(string path, Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken, Task previousRegistration, TaskCompletionSource registrationFinished)
    {
        Reservation? reservation = null;
        try
        {
            try
            {
                // A canceled registration still waits for its position. Releasing this promise early
                // would overlap host key resolvers and let later registration overtake its predecessor.
                await previousRegistration.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var key = await _resolveKey(path, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                ValidateKey(key, "resolvedKey");
                lock (_gate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var existing = _tails.GetValueOrDefault(key);
                    if (existing is null && _tails.Count >= _options.MaximumKeys)
                        throw new FileMutationQueueLimitException(FileMutationQueueLimit.Keys);
                    reservation = new(key, existing?.Finished.Task ?? Task.CompletedTask,
                        new(TaskCreationOptions.RunContinuationsAsynchronously));
                    _tails[key] = reservation;
                }
            }
            finally { registrationFinished.TrySetResult(); }

            // Waiting is deliberately not detached with WaitAsync(token): even a canceled waiter
            // must retain its reservation until all earlier work on this key has settled.
            await reservation!.Predecessor.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (reservation is not null && _tails.TryGetValue(reservation.Key, out var tail) && ReferenceEquals(tail, reservation))
                    _tails.Remove(reservation.Key);
                _registeredOperations--;
            }
            // The host operation's awaited return/throw includes its finally cleanup. A skipped
            // canceled operation reaches here only after its predecessor has completed cleanup.
            reservation?.Finished.TrySetResult();
        }
    }

    private void ValidateKey(string? key, string parameter)
    {
        if (key is null || key.Length == 0) throw new ArgumentException("A nonempty queue key is required.", parameter);
        if (key.Length > _options.MaximumKeyCharacters)
            throw new FileMutationQueueLimitException(FileMutationQueueLimit.KeyCharacters);
        for (var index = 0; index < key.Length; index++)
        {
            if (key[index] == '\0') throw new ArgumentException("Queue keys cannot contain NUL.", parameter);
            if (char.IsHighSurrogate(key[index]))
            {
                if (index + 1 >= key.Length || !char.IsLowSurrogate(key[++index]))
                    throw new ArgumentException("Queue keys must contain valid UTF-16.", parameter);
            }
            else if (char.IsLowSurrogate(key[index])) throw new ArgumentException("Queue keys must contain valid UTF-16.", parameter);
        }
    }
}
