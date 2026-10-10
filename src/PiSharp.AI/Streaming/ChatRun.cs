// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/utils/event-stream.ts.
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Text.Json;
using System.Text;
using PiSharp.Contracts;
using PiSharp.AI.Protocols.PiMessages;
using PiSharp.AI.Protocols.GoogleGenerativeAI;
using PiSharp.AI.Protocols.MistralConversations;

namespace PiSharp.AI;

/// <summary>A single-reader run. Read events or dispose; disposal cancels and releases a blocked producer.</summary>
public sealed class ChatRun : IAsyncDisposable
{
    private readonly Channel<StreamEvent> _progress;
    private readonly CancellationTokenSource _cancellation;
    private readonly TaskCompletionSource<ChatResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<StreamTerminalEvent> _terminal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _disposalCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _producer;
    private readonly TaskCompletionSource<bool>? _readerDetached;
    private readonly Task _detachedDrain;
    private ChatFailure? _cleanupFailure;
    private readonly bool _completionsDiagnostics;
    private readonly bool _piMessagesDiagnostics;
    private readonly bool _googleDiagnostics;
    private readonly bool _mistralDiagnostics;
    private int _readerClaimed;
    private int _disposed;
    private readonly TimeProvider? _time;
    private readonly long _started;

    internal ChatRun(IChatTransport transport, ChatRequest request, int capacity, StreamLimits? limits,
        CancellationToken cancellationToken, bool settleAbortedTerminal = false, long? fallbackTimestamp = null,
        bool detachReader = false, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _time = timeProvider;
        _started = timeProvider?.GetTimestamp() ?? 0;
        _completionsDiagnostics = request.Model.Api == "openai-completions";
        _piMessagesDiagnostics = request.Model.Api == "pi-messages";
        _googleDiagnostics = request.Model.Api == "google-generative-ai";
        _mistralDiagnostics = request.Model.Api == "mistral-conversations" && request.Model.Provider == "mistral";
        var fallback = new AssistantMessage(request.Model.Api, request.Model.Provider, request.Model.Id,
            fallbackTimestamp ?? request.Timestamp, [], TokenUsage.Zero, StopReason.Pending);
        var reducer = new AssistantStreamReducer(fallback, limits, allowPiMessagesIdentityReplacement: _piMessagesDiagnostics);
        _progress = Channel.CreateBounded<StreamEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _readerDetached = detachReader ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        _detachedDrain = detachReader ? DrainDetachedAsync() : Task.CompletedTask;
        _producer = ProduceAsync(transport, request, reducer, settleAbortedTerminal);
    }

    public Task<ChatResult> Completion => _completion.Task;
    public ChatFailure? CleanupFailure => Volatile.Read(ref _cleanupFailure);

    public async IAsyncEnumerable<StreamEvent> ReadEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _readerClaimed, 1) != 0)
            throw new InvalidOperationException("A chat run has exactly one event reader.");
        using var registration = cancellationToken.Register(Cancel);
        try
        {
            await foreach (var value in _progress.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return value;
            yield return await _terminal.Task.ConfigureAwait(false);
        }
        finally
        {
            if (_readerDetached is not null)
            {
                if (cancellationToken.IsCancellationRequested || _cancellation.IsCancellationRequested)
                {
                    Cancel();
                    await _producer.ConfigureAwait(false);
                }
                else
                {
                    // The caller reader has exited the inner channel enumeration. Its single
                    // owned replacement can now release buffered frames and a blocked writer.
                    _readerDetached.TrySetResult(true);
                }
            }
            // Abandoning iteration must release a producer blocked behind a full channel.
            else if (!_completion.Task.IsCompleted) Cancel();
        }
    }

    private async Task DrainDetachedAsync()
    {
        if (!await _readerDetached!.Task.ConfigureAwait(false)) return;
        // The sealed channel has one writer, no completion error and the original bounded
        // capacity. This task owns no callback, extra queue or cancellation registration.
        await foreach (var _ in _progress.Reader.ReadAllAsync().ConfigureAwait(false)) { }
    }

    public void Cancel()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        RequestCancellation();
    }

    private void RequestCancellation()
    {
        try { _cancellation.Cancel(); }
        catch (AggregateException)
        {
            // Transport cancellation callbacks are external code. Preserve a sanitized cleanup
            // diagnostic, while allowing cancellation and the producer's cleanup to finish.
            Interlocked.CompareExchange(ref _cleanupFailure,
                new(ChatFailureKind.Provider, "Provider cancellation callback failed.")
                { NativeDiagnostic = CleanupDiagnostic() }, null);
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
        {
            // Disposal may finish between the state read and Cancel; cancellation already happened.
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _ = DisposeCoreAsync();
        return new(_disposalCompletion.Task);
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            try
            {
                RequestCancellation();
                try { await _producer.ConfigureAwait(false); }
                finally { await _detachedDrain.ConfigureAwait(false); }
            }
            finally { _cancellation.Dispose(); }
            _disposalCompletion.TrySetResult();
        }
        catch (Exception exception) { _disposalCompletion.TrySetException(exception); }
    }

    private async Task ProduceAsync(IChatTransport transport, ChatRequest request, AssistantStreamReducer reducer,
        bool settleAbortedTerminal)
    {
        StreamTerminalEvent? terminal = null;
        JsonData? abortedSourceObservation = null;
        NativeChatDiagnostic? abortedDiagnostic = null, abortedCleanupDiagnostic = null;
        NativeChatDiagnostic? googleCleanupDiagnostic = null;
        var googleConsumerFailed = false;
        ChatFailure? failure = null;
        try
        {
            _cancellation.Token.ThrowIfCancellationRequested();
            var events = transport.StreamAsync(TranscriptSurrogates.Sanitize(request), _cancellation.Token);
            if (_googleDiagnostics || _mistralDiagnostics)
                events = ObserveGoogleCleanup(events, () => googleCleanupDiagnostic = CleanupDiagnostic(),
                    () => googleConsumerFailed, _cancellation.Token);
            await foreach (var value in events.WithCancellation(_cancellation.Token).ConfigureAwait(false))
            {
                try
                {
                    if (value is StreamTerminalEvent nativeTerminal)
                    {
                        ValidateDiagnostic(nativeTerminal.NativeDiagnostic);
                        ValidateDiagnostic(nativeTerminal.NativeCleanupDiagnostic);
                        if (_googleDiagnostics && (nativeTerminal.NativeDiagnostic is { Adapter: not NativeChatAdapter.GoogleGenerativeAI } ||
                            nativeTerminal.NativeCleanupDiagnostic is { Adapter: not NativeChatAdapter.GoogleGenerativeAI }))
                            throw new StreamProtocolException("Invalid Google native diagnostic provenance.");
                        if (_mistralDiagnostics && (nativeTerminal.NativeDiagnostic is { Adapter: not NativeChatAdapter.MistralConversations } ||
                            nativeTerminal.NativeCleanupDiagnostic is { Adapter: not NativeChatAdapter.MistralConversations }))
                            throw new StreamProtocolException("Invalid Mistral native diagnostic provenance.");
                    }
                    if (value is StreamError { Reason: StopReason.Aborted } aborted)
                    {
                        abortedSourceObservation = ReadAbortedSourceObservation(aborted, request);
                        if (aborted.Message.Api == request.Model.Api && aborted.Message.Provider == request.Model.Provider &&
                            aborted.Message.Model == request.Model.Id && aborted.Message.Timestamp == request.Timestamp)
                        { abortedDiagnostic = aborted.NativeDiagnostic; abortedCleanupDiagnostic = aborted.NativeCleanupDiagnostic; }
                    }
                    // The opt-in high-level path may retain an authoritative aborted terminal
                    // already returned by the provider. No successful terminal/progress gains
                    // admission after cancellation; the existing reducer still validates bounds.
                    if (!(settleAbortedTerminal && value is StreamError { Reason: StopReason.Aborted }))
                        _cancellation.Token.ThrowIfCancellationRequested();
                    reducer.Apply(value);
                    if (value is StreamTerminalEvent end)
                    {
                        terminal = end;
                        if (end is StreamError)
                            failure = new(end.Reason == StopReason.Aborted ? ChatFailureKind.Cancelled : ChatFailureKind.Provider,
                                ErrorMessage(end.Message)) { NativeDiagnostic = end.NativeDiagnostic };
                        break; // Terminal wins once; never ask a provider for subsequent events.
                    }
                    await _progress.Writer.WriteAsync(value, _cancellation.Token).ConfigureAwait(false);
                }
                catch (Exception) when (_googleDiagnostics || _mistralDiagnostics)
                {
                    // Mark the primary before await-foreach unwinds through owned disposal.
                    // The observer records a release fault without replacing this exception.
                    googleConsumerFailed = true;
                    throw;
                }
            }
            if (terminal is null) failure = new(ChatFailureKind.UnexpectedEof, "Provider stream ended without a terminal event.")
            { NativeDiagnostic = ProtocolDiagnostic(NativeChatFailureCode.UnexpectedEof) };
        }
        catch (Exception) when (terminal is not null)
        {
            // A provider iterator can throw during cleanup after its terminal event.
            // Google requires successful/tool authority to wait for physical iterator
            // cleanup. Preserve an existing primary error while recording that release fault.
            if (_googleDiagnostics || _mistralDiagnostics)
            {
                terminal = terminal with { NativeCleanupDiagnostic = CleanupDiagnostic() };
                if (terminal is not StreamError)
                {
                    terminal = _mistralDiagnostics ? MistralSettlementError(terminal) : GoogleSettlementError(terminal, NativeChatFailureCode.CleanupFailed, false);
                    failure = new(ChatFailureKind.Provider, ErrorMessage(terminal.Message))
                    { NativeDiagnostic = terminal.NativeDiagnostic };
                }
            }
            else if (_completionsDiagnostics)
                terminal = terminal with { NativeCleanupDiagnostic = CleanupDiagnostic() };
            else if (_piMessagesDiagnostics)
            {
                // Ordinary text-only completion keeps its outcome and records release
                // failure separately. Completed calls can reach direct executors even
                // with Stop; every call-bearing terminal requires successful cleanup.
                terminal = terminal with { NativeCleanupDiagnostic = terminal.NativeCleanupDiagnostic ?? CleanupDiagnostic() };
                if (terminal is StreamDone && (terminal.Reason == StopReason.ToolUse ||
                    terminal.Message.Content.Any(static content => content is ToolCallContent)))
                {
                    terminal = PiMessagesSettlementError(terminal, NativeChatFailureCode.CleanupFailed, false);
                    failure = new(ChatFailureKind.Provider, ErrorMessage(terminal.Message)) { NativeDiagnostic = terminal.NativeDiagnostic };
                }
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        { failure = new(ChatFailureKind.Cancelled, "Chat run was cancelled.")
            { NativeDiagnostic = ProtocolDiagnostic(NativeChatFailureCode.Cancelled) }; }
        catch (StreamLimitException exception)
        { failure = new(ChatFailureKind.ResourceLimit, _googleDiagnostics ? "Google data exceeds configured limits." : _mistralDiagnostics ? "Mistral data exceeds configured limits." : exception.Message)
            { NativeDiagnostic = ProtocolDiagnostic(NativeChatFailureCode.ResourceLimit) }; }
        catch (Exception exception) when (exception is StreamProtocolException or JsonException)
        { failure = new(ChatFailureKind.MalformedStream, _googleDiagnostics ? "Invalid Google streamed data." : _mistralDiagnostics ? "Invalid Mistral streamed data." : exception.Message)
            { NativeDiagnostic = ProtocolDiagnostic(NativeChatFailureCode.MalformedStream) }; }
        catch (PiMessagesException exception) when (_piMessagesDiagnostics)
        { failure = new(ChatFailureKind.Provider, "Provider transport failed.")
            { NativeDiagnostic = PiMessagesData.Diagnostic(exception) }; }
        catch (Exception exception) when (_googleDiagnostics)
        { failure = new(ChatFailureKind.Provider, "Google provider operation failed.")
            { NativeDiagnostic = GoogleNativeDiagnostics.FromException(exception) }; }
        catch (Exception exception) when (_mistralDiagnostics)
        { failure = new(ChatFailureKind.Provider, "Mistral provider operation failed.")
            { NativeDiagnostic = MistralNativeDiagnostics.FromException(exception) }; }
        catch (Exception)
        { failure = new(ChatFailureKind.Provider, "Provider transport failed.")
            { NativeDiagnostic = ProtocolDiagnostic(NativeChatFailureCode.SourceFailed) }; }
        finally
        {
            terminal ??= reducer.Failure(failure ?? new(ChatFailureKind.Provider, "Provider stream failed."));
            if (failure?.Kind == ChatFailureKind.Cancelled && terminal.Reason == StopReason.Aborted)
            {
                var primary = terminal.NativeDiagnostic ?? abortedDiagnostic;
                var secondary = terminal.NativeCleanupDiagnostic ?? abortedCleanupDiagnostic;
                if (primary != terminal.NativeDiagnostic || secondary != terminal.NativeCleanupDiagnostic)
                    terminal = terminal with { NativeDiagnostic = primary, NativeCleanupDiagnostic = secondary };
            }
            if (_readerDetached is not null)
            {
                // Close the producer side before joining its owned drain. If the caller has
                // not detached, settle the dormant drain without competing for the reader.
                _progress.Writer.TryComplete();
                _readerDetached.TrySetResult(false);
                await _detachedDrain.ConfigureAwait(false);
            }
            if (_googleDiagnostics && _cancellation.IsCancellationRequested && terminal.Reason != StopReason.Aborted)
            {
                // The original iterator disposal and any owned detached drain have joined.
                // A local abort during those joins wins over an earlier terminal.
                terminal = GoogleSettlementError(terminal, NativeChatFailureCode.Cancelled, true);
                failure = new(ChatFailureKind.Cancelled, ErrorMessage(terminal.Message))
                { NativeDiagnostic = terminal.NativeDiagnostic };
            }
            if (_piMessagesDiagnostics && _cancellation.IsCancellationRequested && terminal.Reason != StopReason.Aborted)
            {
                terminal = PiMessagesSettlementError(terminal, NativeChatFailureCode.Cancelled, true);
                failure = new(ChatFailureKind.Cancelled, ErrorMessage(terminal.Message)) { NativeDiagnostic = terminal.NativeDiagnostic };
            }
            if (_mistralDiagnostics && _cancellation.IsCancellationRequested && terminal.Reason != StopReason.Aborted)
            {
                terminal = MistralSettlementError(terminal, aborted: true);
                failure = new(ChatFailureKind.Cancelled, ErrorMessage(terminal.Message)) { NativeDiagnostic = terminal.NativeDiagnostic };
            }
            // Retain only this invocation's real producer observation after every owned
            // join. The guard, failure classification and terminal/result pair stay intact.
            if (failure?.Kind == ChatFailureKind.Cancelled && terminal.Reason == StopReason.Aborted && abortedSourceObservation is not null)
            {
                terminal = terminal with { SourceDrainSnapshot = abortedSourceObservation };
                var observed = abortedSourceObservation.Value.GetProperty("value").GetProperty("error");
                if (observed.TryGetProperty("errorMessage", out var text) && text.ValueKind == JsonValueKind.String &&
                    text.GetString() == "Request was aborted")
                    terminal = terminal with { Message = terminal.Message with
                    { ExtraProperties = (terminal.Message.ExtraProperties ?? JsonFields.Empty)
                        .Set("errorMessage", JsonData.Parse("\"Request was aborted\"")) } };
            }
            var cleanupDiagnostic = terminal.NativeCleanupDiagnostic ?? googleCleanupDiagnostic ??
                Volatile.Read(ref _cleanupFailure)?.NativeDiagnostic;
            var diagnostic = terminal.NativeDiagnostic ?? failure?.NativeDiagnostic;
            if (diagnostic != terminal.NativeDiagnostic || cleanupDiagnostic != terminal.NativeCleanupDiagnostic)
                terminal = terminal with { NativeDiagnostic = diagnostic, NativeCleanupDiagnostic = cleanupDiagnostic };
            if (failure is not null) failure = failure with { NativeDiagnostic = terminal.NativeDiagnostic };
            terminal = Timed(terminal, request);
            _terminal.TrySetResult(terminal);
            _completion.TrySetResult(new(terminal.Message, failure)
            { NativeDiagnostic = terminal.NativeDiagnostic, NativeCleanupDiagnostic = terminal.NativeCleanupDiagnostic });
            _progress.Writer.TryComplete();
        }
    }

    /// <summary>
    /// With a clock, the final message of a response this run started gets the monotonic time since the run began. A
    /// message that already has a duration, or whose timestamp predates this request (a response that started
    /// elsewhere), stays untimed. Native transports stamp the request timestamp, so it is the source's response start.
    /// </summary>
    private StreamTerminalEvent Timed(StreamTerminalEvent terminal, ChatRequest request)
    {
        if (_time is null || terminal.Message.DurationMs is not null || terminal.Message.Timestamp < request.Timestamp) return terminal;
        var elapsed = Math.Round(_time.GetElapsedTime(_started).TotalMilliseconds, MidpointRounding.AwayFromZero);
        return terminal with { Message = terminal.Message with { DurationMs = Math.Max(0, (long)elapsed) } };
    }

    private static async IAsyncEnumerable<StreamEvent> ObserveGoogleCleanup(IAsyncEnumerable<StreamEvent> events,
        Action cleanupFailed, Func<bool> consumerFailed, [EnumeratorCancellation] CancellationToken token)
    {
        var reader = events.GetAsyncEnumerator(token);
        var sourceFailed = false;
        try
        {
            while (true)
            {
                StreamEvent current;
                try
                {
                    if (!await reader.MoveNextAsync().ConfigureAwait(false)) break;
                    current = reader.Current;
                }
                catch (Exception) { sourceFailed = true; throw; }
                yield return current;
            }
        }
        finally
        {
            try { await reader.DisposeAsync().ConfigureAwait(false); }
            catch (Exception)
            {
                cleanupFailed();
                // Cleanup is still physically joined. A source/admission failure already
                // propagating through this scope keeps primary authority, even if disposal
                // throws the very same exception instance. No message heuristic is used.
                if (!sourceFailed && !consumerFailed()) throw;
            }
        }
    }

    private NativeChatDiagnostic? ProtocolDiagnostic(NativeChatFailureCode code) =>
        _piMessagesDiagnostics ? new(NativeChatAdapter.PiMessages, code) :
        _mistralDiagnostics ? MistralNativeDiagnostics.FromCode(code) :
        _googleDiagnostics ? GoogleNativeDiagnostics.FromCode(code) : null;
    private NativeChatDiagnostic? CleanupDiagnostic() => _completionsDiagnostics ?
        new(NativeChatAdapter.OpenAICompletions, NativeChatFailureCode.CleanupFailed) :
        ProtocolDiagnostic(NativeChatFailureCode.CleanupFailed);

    private static StreamError GoogleSettlementError(StreamTerminalEvent previous, NativeChatFailureCode code, bool aborted)
    {
        var reason = aborted ? StopReason.Aborted : StopReason.Error;
        var text = aborted ? "Request was aborted" : "Google invocation cleanup failed.";
        return new(reason, previous.Message with { StopReason = reason,
            ExtraProperties = (previous.Message.ExtraProperties ?? JsonFields.Empty)
                .Set("errorMessage", JsonData.Parse(JsonSerializer.Serialize(text))) })
        { NativeDiagnostic = GoogleNativeDiagnostics.FromCode(code), NativeCleanupDiagnostic = previous.NativeCleanupDiagnostic };
    }

    private static StreamError MistralSettlementError(StreamTerminalEvent previous, bool aborted = false) =>
        new(aborted ? StopReason.Aborted : StopReason.Error, previous.Message with { StopReason = aborted ? StopReason.Aborted : StopReason.Error,
            ExtraProperties = (previous.Message.ExtraProperties ?? JsonFields.Empty).Set("errorMessage", JsonData.Parse(aborted ? "\"Request was aborted\"" : "\"Mistral invocation cleanup failed.\"")) })
        { NativeDiagnostic = MistralNativeDiagnostics.FromCode(aborted ? NativeChatFailureCode.Cancelled : NativeChatFailureCode.CleanupFailed), NativeCleanupDiagnostic = previous.NativeCleanupDiagnostic };

    private static StreamError PiMessagesSettlementError(StreamTerminalEvent previous, NativeChatFailureCode code, bool aborted)
    {
        // A final call is progress until all owned joins settle. A late cancellation
        // or iterator cleanup fault must not retain a successful/tool terminal.
        var reason = aborted ? StopReason.Aborted : StopReason.Error;
        var message = new AssistantMessage(previous.Message.Api, previous.Message.Provider, previous.Message.Model,
            previous.Message.Timestamp, [], TokenUsage.Zero, reason,
            JsonFields.Empty.Set("errorMessage", JsonData.Parse(aborted ? "\"Request was aborted\"" : "\"Pi Messages invocation cleanup failed.\"")));
        return new(reason, message) { NativeDiagnostic = new(NativeChatAdapter.PiMessages, code), NativeCleanupDiagnostic = previous.NativeCleanupDiagnostic };
    }

    private static string ErrorMessage(AssistantMessage message) =>
        message.ExtraProperties is not null && message.ExtraProperties.TryGet("errorMessage", out var value)
            && value!.Value.ValueKind == JsonValueKind.String ? value.Value.GetString()! : "Provider returned an error.";

    private static void ValidateDiagnostic(NativeChatDiagnostic? diagnostic)
    {
        if (diagnostic is not null && (!Enum.IsDefined(diagnostic.Adapter) || !Enum.IsDefined(diagnostic.Code)))
            throw new StreamProtocolException("Invalid native chat diagnostic.");
    }

    private static JsonData? ReadAbortedSourceObservation(StreamError terminal, ChatRequest request)
    {
        if (request.Model.Api != "openai-completions" || terminal.Message.Api != request.Model.Api ||
            terminal.Message.Provider != request.Model.Provider || terminal.Message.Model != request.Model.Id ||
            terminal.Message.Timestamp != request.Timestamp) return null;
        if ((terminal.SourceDrainSnapshot ?? terminal.SourceEmissionSnapshot) is not { } observation) return null;
        var text = observation.ToString();
        // The producer's configured admission remains authoritative; this additional cap
        // accepts no larger diagnostic and creates no expanded or later final view.
        if (text.Length > 8_388_608 || Encoding.UTF8.GetByteCount(text) > 8_388_608) return null;
        try
        {
            var owned = JsonData.Parse(text);
            var root = owned.Value;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("ownUndefinedPaths", out var paths) || paths.ValueKind != JsonValueKind.Array ||
                paths.EnumerateArray().Any(path => path.ValueKind != JsonValueKind.String) ||
                !root.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object ||
                !IsString(value, "type", "error") || !IsString(value, "reason", "aborted") ||
                !value.TryGetProperty("error", out var message) || message.ValueKind != JsonValueKind.Object ||
                !IsString(message, "api", request.Model.Api) || !IsString(message, "provider", request.Model.Provider) ||
                !IsString(message, "model", request.Model.Id) || !IsString(message, "stopReason", "aborted") ||
                !message.TryGetProperty("timestamp", out var timestamp) || timestamp.ValueKind != JsonValueKind.Number ||
                !timestamp.TryGetInt64(out var time) || time != request.Timestamp) return null;
            return owned;
        }
        catch (Exception error) when (error is JsonException or StreamProtocolException) { return null; }

        static bool IsString(JsonElement value, string name, string expected) =>
            value.TryGetProperty(name, out var member) && member.ValueKind == JsonValueKind.String && member.GetString() == expected;
    }
}

public sealed class ChatClient(IChatTransport transport, int capacity = 32, StreamLimits? limits = null)
{
    /// <summary>Opt-in clock whose monotonic timestamps time each run's final message (durationMs). Null leaves the
    /// terminal message exactly as reduced, as before durations existed.</summary>
    public TimeProvider? TimeProvider { get; init; }

    public ValueTask<ChatRun> StartAsync(ChatRequest request, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new ChatRun(transport, request, capacity, limits, cancellationToken, timeProvider: TimeProvider));

    /// <summary>Retains a provider's observed bounded Aborted terminal despite work cancellation; drain without a canceled reader token.</summary>
    public ValueTask<ChatRun> StartWithAbortSettlementAsync(ChatRequest request, CancellationToken cancellationToken = default,
        long? fallbackTimestamp = null) =>
        ValueTask.FromResult(new ChatRun(transport, request, capacity, limits, cancellationToken, true, fallbackTimestamp, timeProvider: TimeProvider));

    /// <summary>Reader return detaches consumption; the run owns a bounded discard drain and the continuing producer. Dispose the run to cancel and join.</summary>
    public ValueTask<ChatRun> StartWithDetachedReaderAsync(ChatRequest request, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new ChatRun(transport, request, capacity, limits, cancellationToken, detachReader: true, timeProvider: TimeProvider));

    public async Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        await using var run = await StartAsync(request, cancellationToken).ConfigureAwait(false);
        await foreach (var _ in run.ReadEventsAsync().ConfigureAwait(false)) { }
        return await run.Completion.ConfigureAwait(false);
    }
}
