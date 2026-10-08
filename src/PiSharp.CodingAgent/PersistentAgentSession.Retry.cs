using System.Text.Json;
using PiSharp.Agent;
using PiSharp.CodingAgent.Configuration;
using PiSharp.Contracts;
using PiSharp.Sessions.Compaction;
using PiSharp.Sessions.Serialization;

namespace PiSharp.CodingAgent;

public sealed record SessionAutoRetryStarted(long OperationGeneration, int Attempt, int MaxAttempts, long DelayMs, string ErrorMessage) : SessionOperationEvent(OperationGeneration);
public sealed record SessionAutoRetryEnded(long OperationGeneration, bool Success, int Attempt, string? FinalError = null) : SessionOperationEvent(OperationGeneration);

public sealed partial class PersistentAgentSession
{
    private AgentRetryPolicy _retryPolicy = new(false);
    private bool _retryConfigured;
    private double _retryContextWindow;
    private Func<bool, CancellationToken, Task>? _persistRetryEnabled;
    private Func<long, CancellationToken, Task>? _retryDelay;
    private SessionRetryCoordinator? _retryCoordinator;
    private TaskCompletionSource? _retrySettingsWrite;
    private Task? _retrySettingsOriginal;
    private readonly AsyncLocal<bool> _inRetrySettingsCallback = new();
    [ThreadStatic] private static List<PersistentAgentSession>? _retrySettingsCancellationOwners;
    public bool IsRetryOwnedCallback => _inRetrySettingsCallback.Value || _retrySettingsCancellationOwners?.Contains(this) == true || _retryCoordinator?.IsOwnedCallback == true;
    public AgentRetryPolicy RetryPolicy { get { lock (_gate) return _retryPolicy; } }
    public bool AutoRetryEnabled { get { lock (_gate) return _retryConfigured && _retryPolicy.Enabled; } }
    public bool AutomaticRetryConfigured { get { lock (_gate) return _retryConfigured; } }
    public bool IsRetrying { get { lock (_gate) return _retryCoordinator?.IsRetrying == true; } }

    /// <summary>Explicit idle admission. Persistence is granted only by the supplied acknowledged original callback.</summary>
    public void ConfigureAutomaticRetry(AgentRetryPolicy policy, Func<bool, CancellationToken, Task>? persistEnabledOriginal = null,
        double contextWindow = 0, Func<long, CancellationToken, Task>? originalDelay = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (originalDelay is not null && originalDelay.GetInvocationList().Length != 1)
            throw new ArgumentException("Retry delay requires one explicitly owned callback.", nameof(originalDelay));
        if (!double.IsFinite(contextWindow) || contextWindow < 0) throw new ArgumentOutOfRangeException(nameof(contextWindow));
        ThrowConfigurationSelfWait();
        lock (_gate)
        {
            ThrowAvailable(); ThrowInputMutation();
            if (_active is not null || _inputSubmission is not null || _retrySettingsWrite is not null)
                throw new InvalidOperationException("Retry admission requires an idle session.");
            _retryPolicy = policy; _retryConfigured = true; _retryContextWindow = contextWindow;
            _persistRetryEnabled = persistEnabledOriginal; _retryDelay = originalDelay;
        }
    }
    internal sealed record RetryAdmission(AgentRetryPolicy Policy, Func<bool, CancellationToken, Task>? Persistence,
        double ContextWindow, Func<long, CancellationToken, Task>? Delay);
    internal RetryAdmission? CaptureRetryAdmission()
    { lock (_gate) return _retryConfigured ? new(_retryPolicy, _persistRetryEnabled, _retryContextWindow, _retryDelay) : null; }
    internal void RetainRetryAdmission(RetryAdmission? admission)
    {
        if (admission is null) return;
        double window; lock (_gate) window = _retryConfigured ? _retryContextWindow : admission.ContextWindow;
        ConfigureAutomaticRetry(admission.Policy, admission.Persistence, window, admission.Delay);
    }

    /// <summary>Refresh the explicitly admitted model window after an idle model-selection acknowledgement.</summary>
    public void SetAutomaticRetryContextWindow(double contextWindow)
    {
        if (!double.IsFinite(contextWindow) || contextWindow < 0) throw new ArgumentOutOfRangeException(nameof(contextWindow));
        ThrowConfigurationSelfWait();
        lock (_gate)
        {
            ThrowAvailable(); ThrowInputMutation();
            if (_active is not null || _inputSubmission is not null)
                throw new InvalidOperationException("Retry model admission requires an idle session.");
            _retryContextWindow = contextWindow;
        }
    }

    public Task SetAutoRetryEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        ThrowConfigurationSelfWait(); cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource original; Func<bool, CancellationToken, Task>? persist;
        lock (_gate)
        {
            ThrowAvailable();
            if (!_retryConfigured) throw new InvalidOperationException("Retry requires explicit session admission.");
            if (_operationPhase == SessionOperationPhase.Settlement || _retrySettingsWrite is not null)
                throw new InvalidOperationException("Retry preferences are already settling or being persisted.");
            original = new(TaskCreationOptions.RunContinuationsAsynchronously); _retrySettingsWrite = original; _retrySettingsOriginal = original.Task; persist = _persistRetryEnabled;
        }
        _ = SetRetryEnabledOwnedAsync(original, persist, enabled, cancellationToken); return original.Task;
    }
    private async Task SetRetryEnabledOwnedAsync(TaskCompletionSource original, Func<bool, CancellationToken, Task>? persist,
        bool enabled, CancellationToken token)
    {
        var failures = new List<Exception>(); var cancellation = new RetrySettingsCancellation(this);
        try
        {
            if (persist is not null)
            {
                using var registration = token.UnsafeRegister(static state => ((RetrySettingsCancellation)state!).Cancel(), cancellation);
                var previous = _inRetrySettingsCallback.Value; _inRetrySettingsCallback.Value = true;
                try
                {
                    var originals = new List<Task>();
                    foreach (var entry in persist.GetInvocationList())
                        try { originals.Add(((Func<bool, CancellationToken, Task>)entry)(enabled, cancellation.Stop.Token) ??
                            throw new InvalidOperationException("Persistence returned no original acknowledgement task.")); }
                        catch (Exception error) { AddDistinctFailure(failures, error); }
                    // Invoke every admitted writer before joining, and join every actual original even after a fault.
                    foreach (var acknowledged in originals)
                        try { await acknowledged.ConfigureAwait(false); }
                        catch (Exception error)
                        {
                            AddDistinctFailure(failures, error);
                            // Await exposes one fault; the completed original can own additional faults (e.g. WhenAll).
                            if (acknowledged.Exception is { } aggregate) AddDistinctFailure(failures, aggregate);
                        }
                }
                finally { _inRetrySettingsCallback.Value = previous; }
            }
            // Once acknowledged, caller cancellation cannot roll back the persisted preference.
            if (failures.Count == 0)
                lock (_gate) _retryPolicy = new(enabled, _retryPolicy.MaxRetries, _retryPolicy.BaseDelayMs, _retryPolicy.MaxAgentDelayMs);
        }
        catch (Exception error) { AddDistinctFailure(failures, error); }
        finally
        {
            foreach (var error in cancellation.Close()) AddDistinctFailure(failures, error);
            lock (_gate) if (ReferenceEquals(_retrySettingsWrite, original)) _retrySettingsWrite = null;
        }
        if (failures.Count == 0) original.TrySetResult();
        else original.TrySetException(failures.Count == 1 ? failures[0] : new AggregateException(failures));
    }
    private Task RetrySettingsIdleLocked() => _retrySettingsOriginal ?? Task.CompletedTask;
    public Task AbortRetryAsync()
    {
        ThrowConfigurationSelfWait(); SessionRetryCoordinator? coordinator;
        lock (_gate) coordinator = _retryCoordinator;
        return coordinator?.AbortRetryAsync() ?? Task.CompletedTask;
    }
    private void ThrowRetrySelfWait()
    {
        if (IsRetryOwnedCallback)
            throw new InvalidOperationException("Retry-owned callbacks cannot await their own session settlement.");
    }
    internal void RejectRetryOwnedSelfWait() => ThrowRetrySelfWait();
    private sealed class RetrySettingsCancellation(PersistentAgentSession owner)
    {
        private readonly object lifetime = new(); private bool stopped, closed;
        private readonly List<Exception> failures = [];
        internal readonly CancellationTokenSource Stop = new();
        internal void Cancel()
        {
            lock (lifetime)
            {
                if (stopped || closed) return; stopped = true;
                var owners = _retrySettingsCancellationOwners ??= []; owners.Add(owner);
                try { Stop.Cancel(); } catch (Exception error) { AddDistinctFailure(failures, error); }
                finally { owners.RemoveAt(owners.Count - 1); }
            }
        }
        internal Exception[] Close() { lock (lifetime) { closed = true; Stop.Dispose(); return failures.ToArray(); } }
    }
    private SessionRetryCoordinator? BeginRetryOperation(long generation)
    {
        lock (_gate)
        {
            if (!_retryConfigured) return null;
            return _retryCoordinator = new(() => RetryPolicy, value => EmitRetryEventAsync(value, generation), _retryDelay);
        }
    }
    private Task EmitRetryEventAsync(SessionRetryEvent value, long generation) => (value switch
    {
        SessionRetryStarted started => EmitOperationAsync(new SessionAutoRetryStarted(generation, started.Attempt, started.MaxAttempts, started.DelayMs, started.ErrorMessage)),
        SessionRetryEnded ended => EmitOperationAsync(new SessionAutoRetryEnded(generation, ended.Success, ended.Attempt, ended.FinalError)),
        _ => throw new InvalidOperationException("Unsupported retry observation.")
    }).AsTask();

    private async Task<bool> TryAutomaticRetryAsync(AgentLoopResult result, TaskCompletionSource idle, CancellationToken token)
    {
        SessionRetryCoordinator? coordinator; double window;
        lock (_gate) { coordinator = _retryCoordinator; window = _retryContextWindow; }
        if (coordinator is null || result.Turns.IsEmpty) return false;
        using var bashBoundary = await BeginUserBashBoundaryAsync(idle).ConfigureAwait(false);
        var assistant = result.Turns[^1].Result.Chat.Message;
        if (token.IsCancellationRequested) { await coordinator.FinishCancelledAsync().ConfigureAwait(false); return false; }
        var overflow = SessionRecoveryClassifier.IsContextOverflow(assistant, window);
        var retry = await coordinator.PrepareRetryAsync(assistant.StopReason, RetryErrorMessage(assistant), overflow,
            () => OmitRetryAssistantAsync(assistant, idle, token), token).ConfigureAwait(false);
        if (!retry) await coordinator.FinishAsync(assistant.StopReason, RetryErrorMessage(assistant)).ConfigureAwait(false);
        return retry && !token.IsCancellationRequested;
    }
    private Task OmitRetryAssistantAsync(AssistantMessage assistant, TaskCompletionSource idle, CancellationToken token)
    {
        string target;
        lock (_gate)
        {
            if (!ReferenceEquals(_active, idle)) throw Error(PersistentAgentSessionFailure.StaleSession);
            var wire = PiWireJson.WriteMessage(assistant).Value;
            var acknowledged = _context.ContextEntries.LastOrDefault(entry => entry.SourceEntry.Id == _lastAcknowledgedAssistantId &&
                entry.Messages.Any(message => message.Role == "assistant" && JsonElement.DeepEquals(message.WireBody.Value, wire)));
            if (acknowledged is null) throw Error(PersistentAgentSessionFailure.InvalidCommit);
            target = acknowledged.SourceEntry.Id;
        }
        SetOperationPhase(SessionOperationPhase.RecoveryOmission);
        return OmitRecoveryAttemptAsync([target], token, idle);
    }
    private static string? RetryErrorMessage(AssistantMessage assistant)
    {
        var wire = PiWireJson.WriteMessage(assistant).Value;
        return wire.TryGetProperty("errorMessage", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() : null;
    }
}
