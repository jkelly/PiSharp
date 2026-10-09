using System.Runtime.CompilerServices;
using PiSharp.Contracts;

namespace PiSharp.Rpc;

/// <summary>One complete LF frame or nonempty EOF tail; rejected frames retain no untrusted payload.</summary>
public sealed record JsonlFrameAdmission
{
    public JsonData? Record { get; }
    public JsonlTransportFailure? Failure { get; }
    public string? FailureMessage { get; }
    public bool IsFinalFrame { get; }
    public bool IsAccepted => Record is not null;

    internal JsonlFrameAdmission(JsonData record, bool final)
    { Record = record; IsFinalFrame = final; }
    internal JsonlFrameAdmission(JsonlTransportException error, bool final)
    { Failure = error.Failure; FailureMessage = error.SyntaxError ?? error.Message; IsFinalFrame = final; }
}

/// <summary>Pull-driven LF-only JSON-object reader. One instance supports one enumeration.</summary>
public sealed class JsonlReader : IAsyncDisposable
{
    private readonly Stream _input;
    private readonly JsonlTransportOptions _options;
    private readonly JsonlStreamOwnership _ownership;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _stopToken;
    private CancellationToken _readToken;
    internal bool OwnsCancellation(OperationCanceledException error) => _readToken.CanBeCanceled &&
        _readToken.IsCancellationRequested && error.CancellationToken == _readToken;
    private readonly object _sync = new();
    private Task? _cleanup; private TaskCompletionSource? _activity; private int _claimed;

    public JsonlReader(Stream input, JsonlTransportOptions? options = null, JsonlStreamOwnership ownership = JsonlStreamOwnership.Borrowed)
    {
        ArgumentNullException.ThrowIfNull(input); _options = options ?? new(); _options.Validate(ownership);
        if (!input.CanRead) throw new ArgumentException("JSONL input must be readable.", nameof(input));
        _input = input; _ownership = ownership; _stopToken = _stop.Token;
    }

    public async IAsyncEnumerable<JsonData> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var admission in ReadCoreAsync(recover: false, cancellationToken).ConfigureAwait(false))
            yield return admission.Record!;
    }

    /// <summary>Reports complete syntax/non-object rejections and resumes at the next LF frame. Hardening failures remain terminal.</summary>
    public IAsyncEnumerable<JsonlFrameAdmission> ReadAdmissionsAsync(CancellationToken cancellationToken = default) =>
        ReadCoreAsync(recover: true, cancellationToken);

    private async IAsyncEnumerable<JsonlFrameAdmission> ReadCoreAsync(bool recover,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _claimed, 1) != 0) throw new InvalidOperationException("A JSONL reader has one enumeration.");
        try
        {
            BeginActivity();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopToken);
            var token = linked.Token;
            _readToken = token;
            var buffer = new byte[_options.ReadBufferBytes];
            var frame = new byte[Math.Min(_options.ReadBufferBytes, _options.MaximumFrameBytes + 1)]; var length = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var count = await _input.ReadAsync(buffer, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (count == 0)
                {
                    if (length != 0)
                    {
                        if (frame[length - 1] == (byte)'\r') length--;
                        var admission = Admit(frame.AsSpan(0, length), recover, final: true);
                        EndActivity(); yield return admission; BeginActivity();
                    }
                    yield break;
                }
                for (var index = 0; index < count; index++)
                {
                    token.ThrowIfCancellationRequested(); var value = buffer[index];
                    if (value == (byte)'\n')
                    {
                        if (length > 0 && frame[length - 1] == (byte)'\r') length--;
                        var admission = Admit(frame.AsSpan(0, length), recover, final: false);
                        length = 0; EndActivity(); yield return admission; BeginActivity(); continue;
                    }
                    if (length >= _options.MaximumFrameBytes && !(length == _options.MaximumFrameBytes && value == (byte)'\r'))
                        throw new JsonlTransportException(JsonlTransportFailure.FrameLimit);
                    if (length == frame.Length)
                        Array.Resize(ref frame, (int)Math.Min(_options.MaximumFrameBytes + 1L, Math.Max(length + 1L, frame.Length * 2L)));
                    frame[length++] = value;
                }
            }
        }
        finally
        {
            // Release active iterator work before joining cleanup: cleanup may already be waiting for us.
            EndActivity(); await DisposeAsync().ConfigureAwait(false);
        }
    }

    private JsonlFrameAdmission Admit(ReadOnlySpan<byte> frame, bool recover, bool final)
    {
        try { return new(JsonlRecordCodec.Parse(frame, _options, final), final); }
        catch (JsonlTransportException error) when (recover && error.Failure is
            JsonlTransportFailure.MalformedJson or JsonlTransportFailure.PartialFinalFrame or JsonlTransportFailure.InvalidRecord)
        { return new(error, final); }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion; Task settled;
        lock (_sync)
        {
            if (_cleanup is not null) return new(_cleanup);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _cleanup = completion.Task;
            settled = _activity?.Task ?? Task.CompletedTask;
        }
        _ = Cleanup(completion, settled); return new(completion.Task);
    }
    private void BeginActivity()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_cleanup is not null, this);
            _activity = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
    private void EndActivity()
    {
        TaskCompletionSource? activity;
        lock (_sync) { activity = _activity; _activity = null; }
        activity?.TrySetResult();
    }
    private async Task Cleanup(TaskCompletionSource completion, Task settled)
    {
        Exception? failure = null;
        try
        {
            try { _stop.Cancel(); } catch (Exception error) { failure = error; }
            await settled.ConfigureAwait(false);
            try { if (_ownership == JsonlStreamOwnership.Owned) await _input.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { failure = failure is null ? error : new AggregateException(failure, error); }
            if (failure is null) completion.SetResult(); else completion.SetException(failure);
        }
        catch (Exception error) { completion.SetException(error); }
        finally { _stop.Dispose(); }
    }
}
