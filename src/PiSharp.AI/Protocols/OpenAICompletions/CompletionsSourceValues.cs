using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts;
using PiSharp.Contracts.Compatibility;
using PiSharp.AI.Providers;

namespace PiSharp.AI.Protocols.OpenAICompletions;

/// <summary>Explicit public data, never inferred from an arbitrary exception's message.</summary>
public sealed class CompletionsPublicFailure
{
    public string Message { get; }
    public CompletionsPublicFailure(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Length is 0 or > 8192 || Encoding.UTF8.GetByteCount(message) > 8192 || message.Contains('\0'))
            throw new ArgumentException("Invalid public Completions failure data.", nameof(message));
        CompletionsJson.Unicode(message); Message = message;
    }
}

public sealed class CompletionsPublicFailureException(CompletionsPublicFailure failure)
    : Exception("Completions operation reported admitted public failure data.")
{
    public CompletionsPublicFailure Failure { get; } = failure ?? throw new ArgumentNullException(nameof(failure));
}

/// <summary>Owned raw/presence data alongside a real production ECMAScript serialization.</summary>
public sealed class CompletionsSourceSnapshot
{
    public JsonData Raw { get; }
    public string SerializedJson { get; }
    public ImmutableArray<string> OwnUndefinedPaths { get; }
    internal CompletionsSourceSnapshot(JsonData raw, int maximumCharacters, int maximumBytes)
    {
        var text = raw.ToString();
        if (text.Length > maximumCharacters || Encoding.UTF8.GetByteCount(text) > maximumBytes)
            throw new StreamLimitException("Completions source value exceeds configured limits.");
        Raw = JsonData.Parse(text); // Strict retained syntax, owned duplicate validation.
        OwnUndefinedPaths = Raw.Value.GetProperty("ownUndefinedPaths").EnumerateArray()
            .Select(path => path.GetString()!).ToImmutableArray();
        SerializedJson = EcmaScriptJsonProjection.Project(JsonData.Parse(Raw.Value.GetProperty("value").GetRawText()), new(MaximumInputCharacters: maximumCharacters,
            MaximumInputBytes: maximumBytes, MaximumOutputCharacters: maximumCharacters,
            MaximumOutputBytes: maximumBytes, MaximumDepth: 64, MaximumStringCharacters: maximumCharacters));
    }
}

/// <summary>A producer-owned live message handle. Each read returns an owned immutable revision.</summary>
public sealed class CompletionsSourceMessage
{
    private CompletionsSourceSnapshot _snapshot;
    internal CompletionsSourceMessage(CompletionsSourceSnapshot initial) => _snapshot = initial;
    public CompletionsSourceSnapshot Snapshot => Volatile.Read(ref _snapshot);
    internal void Update(CompletionsSourceSnapshot snapshot) => Volatile.Write(ref _snapshot, snapshot);
}

/// <summary>An actual producer publication, with a run-local ordinal and process-monotonic clock ticks.</summary>
public sealed record CompletionsSourcePublication(long Sequence, long MonotonicTimestamp, CompletionsSourceSnapshot Emission)
{
    public static long TimestampFrequency => Stopwatch.Frequency;
}

public sealed class CompletionsSourceEvent
{
    private readonly JsonData _descriptor;
    private readonly string _member;
    private readonly int _characters, _bytes;
    public string Type { get; }
    public CompletionsSourceMessage Message { get; }
    public CompletionsSourceSnapshot Emission { get; }
    internal CompletionsSourceEvent(JsonData emission, CompletionsSourceMessage message, int characters, int bytes)
    {
        Type = emission.Value.GetProperty("value").GetProperty("type").GetString()!;
        _member = Type == "done" ? "message" : Type == "error" ? "error" : "partial";
        var descriptor = JsonNode.Parse(emission.Value.GetProperty("value").GetRawText())!.AsObject();
        descriptor.Remove(_member); _descriptor = JsonData.Parse(descriptor.ToJsonString());
        Message = message; _characters = characters; _bytes = bytes;
        Emission = new(emission, characters, bytes);
    }
    public CompletionsSourceSnapshot Snapshot
    {
        get
        {
            var message = Message.Snapshot.Raw.Value;
            var value = JsonNode.Parse(_descriptor.ToString())!.AsObject();
            value[_member] = JsonNode.Parse(message.GetProperty("value").GetRawText());
            var paths = message.GetProperty("ownUndefinedPaths").EnumerateArray()
                .Select(path => "/" + _member + path.GetString()).ToArray();
            return new(JsonData.Parse("{\"value\":" + value.ToJsonString() + ",\"ownUndefinedPaths\":" +
                JsonSerializer.Serialize(paths) + "}"), _characters, _bytes);
        }
    }
}

/// <summary>The actual outer event-reader return/next result. Done has an absent value, represented by presence data.</summary>
public sealed class CompletionsIteratorResult
{
    public bool Done { get; }
    public CompletionsSourceEvent? Value { get; }
    internal CompletionsIteratorResult(CompletionsSourceEvent? value) { Value = value; Done = value is null; }
    public CompletionsSourceSnapshot Snapshot
    {
        get
        {
            if (Value is null) return new(JsonData.Parse("{\"value\":{\"done\":true},\"ownUndefinedPaths\":[\"/value\"]}"), 1024, 1024);
            var snapshot = Value.Snapshot;
            return new(JsonData.Parse("{\"value\":{\"value\":" + snapshot.Raw.Value.GetProperty("value").GetRawText() +
                ",\"done\":false},\"ownUndefinedPaths\":" + JsonSerializer.Serialize(snapshot.OwnUndefinedPaths
                    .Select(path => "/value" + path)) + "}"), 8_388_608, 8_388_608);
        }
    }
}

public sealed record CompletionsCleanupOutcome(bool Succeeded, ChatFailure? Failure);

internal interface ICompletionsOwnedEnumerator : IPreparedCompletionsEnumerator, IAsyncDisposable
{
    ValueTask CompleteSourceAsync(bool interrupted);
    CompletionsPublicFailure? PublicFailure { get; }
}

internal sealed class CompletionsProductionContext
{
    public CompletionsStartupHandoff? Startup { get; set; }
    private Func<StreamEvent, JsonData>? _capture;
    private Func<OpenAICompletionsWireFailure?, List<StreamEvent>>? _finish;
    private Func<AssistantMessage>? _current;
    private Task _cleanup = Task.CompletedTask;
    public bool FinalBatch { get; private set; }
    public CompletionsPublicFailure? PublicFailure { get; private set; }
    public Task Cleanup => _cleanup;
    public AssistantMessage? Current => _current?.Invoke();
    public void Bind(Func<StreamEvent, JsonData> capture, Func<OpenAICompletionsWireFailure?, List<StreamEvent>> finish,
        Func<AssistantMessage> current)
    { _capture = capture; _finish = finish; _current = current; }
    public JsonData Capture(StreamEvent frame) => _capture is { } capture ? capture(frame) :
        CompletionsSourceEventProjection.Capture(frame, frame is StreamTerminalEvent terminal ? terminal.Message :
            ((StreamStarted)frame).Partial, []);
    public List<StreamEvent> Finish(OpenAICompletionsWireFailure? failure)
    { FinalBatch = true; return _finish!(failure); }
    public void Admit(Exception error)
    { if (error is CompletionsPublicFailureException admitted) PublicFailure = admitted.Failure; }
    public async ValueTask CloseSourceAsync(IAsyncEnumerator<JsonData> enumerator, bool interrupted)
    {
        if (enumerator is ICompletionsOwnedEnumerator owned)
        {
            try { await owned.CompleteSourceAsync(interrupted).ConfigureAwait(false); }
            finally { PublicFailure ??= owned.PublicFailure; _cleanup = owned.DisposeAsync().AsTask(); }
        }
        else _cleanup = enumerator.DisposeAsync().AsTask();
    }
}
