using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.Agent;

public enum AgentPendingInputMode { OneAtATime, All }

/// <summary>Complete independent FIFO contents and drain modes captured at one queue state boundary.</summary>
public sealed record AgentPendingInputQueueSnapshot(
    ImmutableArray<TranscriptEntry> SteeringMessages, ImmutableArray<TranscriptEntry> FollowUpMessages,
    AgentPendingInputMode SteeringMode, AgentPendingInputMode FollowUpMode)
{
    // Opaque queue-owned revision. Not serialized; independently constructed snapshots cannot authorize removal.
    internal object? StateToken { get; init; }
}

public sealed record AgentPendingInputQueueOptions(
    AgentPendingInputMode SteeringMode = AgentPendingInputMode.OneAtATime,
    AgentPendingInputMode FollowUpMode = AgentPendingInputMode.OneAtATime,
    int MaximumMessagesPerQueue = 256, int MaximumMessageCharacters = 65_536,
    long MaximumCharactersPerQueue = 1_048_576, int MaximumJsonDepth = 32);

public enum AgentPendingInputFailure { InvalidInput, ResourceLimit }

public sealed class AgentPendingInputException : Exception
{
    public AgentPendingInputFailure Failure { get; }
    internal AgentPendingInputException(AgentPendingInputFailure failure) : base(failure == AgentPendingInputFailure.ResourceLimit
        ? "Pending agent input exceeds configured limits."
        : "Pending agent input must be an owned system/user/custom message with matching role.") => Failure = failure;
}

/// <summary>
/// Bounded independent steering/follow-up FIFOs of canonical immutable input values.
/// Short state operations are serialized; this class supplies no run or busy-agent lifecycle.
/// </summary>
public sealed class AgentPendingInputQueue
{
    private readonly object _gate = new();
    private object _stateToken = new();
    private readonly AgentPendingInputQueueOptions _options;
    private readonly State _steering;
    private readonly State _followUp;
    private sealed record Item(TranscriptEntry Message, int Characters);
    private sealed class State(AgentPendingInputMode mode)
    {
        public AgentPendingInputMode Mode = mode;
        public ImmutableQueue<Item> Items = ImmutableQueue<Item>.Empty;
        public int Count;
        public long Characters;
    }

    public AgentPendingInputQueue(AgentPendingInputQueueOptions? options = null)
    {
        _options = options ?? new();
        ValidateMode(_options.SteeringMode);
        ValidateMode(_options.FollowUpMode);
        if (_options.MaximumMessagesPerQueue <= 0 || _options.MaximumMessageCharacters <= 0 ||
            _options.MaximumCharactersPerQueue <= 0 || _options.MaximumJsonDepth is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(options), "Invalid pending agent input limits.");
        _steering = new(_options.SteeringMode);
        _followUp = new(_options.FollowUpMode);
    }

    public AgentPendingInputMode SteeringMode
    {
        get { lock (_gate) return _steering.Mode; }
        set { ValidateMode(value); lock (_gate) { if (_steering.Mode != value) { _stateToken = new(); _steering.Mode = value; } } }
    }
    public AgentPendingInputMode FollowUpMode
    {
        get { lock (_gate) return _followUp.Mode; }
        set { ValidateMode(value); lock (_gate) { if (_followUp.Mode != value) { _stateToken = new(); _followUp.Mode = value; } } }
    }
    public int SteeringCount { get { lock (_gate) return _steering.Count; } }
    public int FollowUpCount { get { lock (_gate) return _followUp.Count; } }
    public bool HasQueuedMessages { get { lock (_gate) return _steering.Count != 0 || _followUp.Count != 0; } }

    public void EnqueueSteering(TranscriptEntry message, CancellationToken cancellationToken = default) =>
        Enqueue(_steering, message, cancellationToken);
    public void EnqueueFollowUp(TranscriptEntry message, CancellationToken cancellationToken = default) =>
        Enqueue(_followUp, message, cancellationToken);
    public ImmutableArray<TranscriptEntry> PeekSteering(CancellationToken cancellationToken = default) =>
        Select(_steering, consume: false, cancellationToken);
    public ImmutableArray<TranscriptEntry> PeekFollowUp(CancellationToken cancellationToken = default) =>
        Select(_followUp, consume: false, cancellationToken);
    public ImmutableArray<TranscriptEntry> DrainSteering(CancellationToken cancellationToken = default) =>
        Select(_steering, consume: true, cancellationToken);
    public ImmutableArray<TranscriptEntry> DrainFollowUp(CancellationToken cancellationToken = default) =>
        Select(_followUp, consume: true, cancellationToken);
    public ValueTask<ImmutableArray<TranscriptEntry>> GetSteeringMessagesAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(DrainSteering(cancellationToken));
    public ValueTask<ImmutableArray<TranscriptEntry>> GetFollowUpMessagesAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(DrainFollowUp(cancellationToken));

    /// <summary>Peek the source-mode-selected steering prefix, otherwise the follow-up prefix, without consuming.</summary>
    public ImmutableArray<TranscriptEntry> PeekQueuedMessages(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return SelectLocked(_steering.Count != 0 ? _steering : _followUp, consume: false, cancellationToken);
        }
    }
    public void ClearSteering(CancellationToken cancellationToken = default) => Clear(_steering, cancellationToken);
    public void ClearFollowUp(CancellationToken cancellationToken = default) => Clear(_followUp, cancellationToken);
    public void ClearAll(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_steering.Count != 0 || _followUp.Count != 0) _stateToken = new();
            Reset(_steering);
            Reset(_followUp);
        }
    }

    /// <summary>Captures both full queues without consuming, independently of the mode-selected peek prefix.</summary>
    public AgentPendingInputQueueSnapshot GetSnapshot(CancellationToken cancellationToken = default)
    {
        lock (_gate) return SnapshotLocked(cancellationToken);
    }

    /// <summary>Returns both complete queues and clears them atomically after the owned snapshot is constructed.</summary>
    public AgentPendingInputQueueSnapshot ClearAndSnapshot(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var snapshot = SnapshotLocked(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (_steering.Count != 0 || _followUp.Count != 0) _stateToken = new();
            Reset(_steering);
            Reset(_followUp);
            return snapshot;
        }
    }

    /// <summary>Clears only the exact queue revision captured by this instance. Failure returns no removal.</summary>
    public bool TryClearAndSnapshot(AgentPendingInputQueueSnapshot expected,
        [NotNullWhen(true)] out AgentPendingInputQueueSnapshot? removed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        removed = null;
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(expected.StateToken, _stateToken) ||
                expected.SteeringMode != _steering.Mode || expected.FollowUpMode != _followUp.Mode ||
                !Matches(expected.SteeringMessages, _steering) || !Matches(expected.FollowUpMessages, _followUp)) return false;
            var snapshot = SnapshotLocked(cancellationToken);
            // Allocate before the commit, so failure cannot leave a partly cleared pair.
            var nextToken = _steering.Count != 0 || _followUp.Count != 0 ? new object() : _stateToken;
            cancellationToken.ThrowIfCancellationRequested();
            Reset(_steering); Reset(_followUp); _stateToken = nextToken;
            removed = snapshot;
            return true;
        }

        static bool Matches(ImmutableArray<TranscriptEntry> expectedMessages, State state)
        {
            if (expectedMessages.IsDefault || expectedMessages.Length != state.Count) return false;
            var index = 0;
            foreach (var item in state.Items)
                if (!ReferenceEquals(expectedMessages[index++], item.Message)) return false;
            return true;
        }
    }

    private AgentPendingInputQueueSnapshot SnapshotLocked(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var steering = Full(_steering);
        var followUp = Full(_followUp);
        cancellationToken.ThrowIfCancellationRequested();
        return new(steering, followUp, _steering.Mode, _followUp.Mode) { StateToken = _stateToken };

        ImmutableArray<TranscriptEntry> Full(State state)
        {
            var messages = ImmutableArray.CreateBuilder<TranscriptEntry>(state.Count);
            foreach (var item in state.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                messages.Add(item.Message);
            }
            return messages.MoveToImmutable();
        }
    }

    private void Enqueue(State state, TranscriptEntry message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (message is null || message.Role is not ("system" or "user" or "custom") || message.WireBody is null ||
                message.WireBody.Value.ValueKind != JsonValueKind.Object ||
                !message.WireBody.Value.TryGetProperty("role", out var role) || role.ValueKind != JsonValueKind.String ||
                role.GetString() != message.Role) throw Failure(AgentPendingInputFailure.InvalidInput);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        { throw Failure(AgentPendingInputFailure.InvalidInput); }
        var characters = message.WireBody.Value.GetRawText().Length;
        if (characters > _options.MaximumMessageCharacters) throw Failure(AgentPendingInputFailure.ResourceLimit);
        CheckDepth(message.WireBody.Value, 0, cancellationToken);
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (state.Count >= _options.MaximumMessagesPerQueue ||
                characters > _options.MaximumCharactersPerQueue - state.Characters)
                throw Failure(AgentPendingInputFailure.ResourceLimit);
            var next = state.Items.Enqueue(new(message, characters));
            _stateToken = new();
            state.Items = next;
            state.Count++;
            state.Characters += characters;
        }
    }

    private ImmutableArray<TranscriptEntry> Select(State state, bool consume, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return SelectLocked(state, consume, cancellationToken);
        }
    }
    private ImmutableArray<TranscriptEntry> SelectLocked(State state, bool consume, CancellationToken cancellationToken)
    {
        if (state.Count == 0) return [];
        var count = state.Mode == AgentPendingInputMode.All ? state.Count : 1;
        var result = ImmutableArray.CreateBuilder<TranscriptEntry>(count);
        var remaining = state.Items;
        long characters = 0;
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            remaining = remaining.Dequeue(out var item);
            result.Add(item.Message);
            characters += item.Characters;
        }
        // Build the owned result before consuming. No callback or await runs under the lock.
        var selected = result.MoveToImmutable();
        cancellationToken.ThrowIfCancellationRequested();
        if (consume)
        {
            _stateToken = new();
            state.Items = remaining;
            state.Count -= count;
            state.Characters -= characters;
        }
        return selected;
    }
    private void Clear(State state, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (state.Count != 0) _stateToken = new();
            Reset(state);
        }
    }
    private static void Reset(State state)
    {
        state.Items = ImmutableQueue<Item>.Empty;
        state.Count = 0;
        state.Characters = 0;
    }
    private void CheckDepth(JsonElement value, int parentDepth, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) return;
        var depth = parentDepth + 1;
        if (depth > _options.MaximumJsonDepth) throw Failure(AgentPendingInputFailure.ResourceLimit);
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject()) CheckDepth(property.Value, depth, cancellationToken);
        else
            foreach (var child in value.EnumerateArray()) CheckDepth(child, depth, cancellationToken);
    }
    private static void ValidateMode(AgentPendingInputMode mode)
    {
        if (mode is not (AgentPendingInputMode.OneAtATime or AgentPendingInputMode.All))
            throw new ArgumentOutOfRangeException(nameof(mode), "Unsupported pending input drain mode.");
    }
    private static AgentPendingInputException Failure(AgentPendingInputFailure failure) => new(failure);
}
