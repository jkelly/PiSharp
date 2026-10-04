using System.Collections.Immutable;
using PiSharp.Contracts;

namespace PiSharp.ExtensionHost.Protocol;

public enum WorkerValuePresence { Absent, Undefined, Json }
/// <summary>An owned value. JSON null differs from absence and JavaScript undefined.</summary>
public sealed record WorkerValue
{
    public WorkerValuePresence Presence { get; }
    public JsonData? Json { get; }
    private WorkerValue(WorkerValuePresence presence, JsonData? json) { Presence = presence; Json = json; }
    public static WorkerValue Absent { get; } = new(WorkerValuePresence.Absent, null);
    public static WorkerValue Undefined { get; } = new(WorkerValuePresence.Undefined, null);
    public static WorkerValue FromJson(JsonData value) => new(WorkerValuePresence.Json, value ?? throw new ArgumentNullException(nameof(value)));
}
public enum WorkerMessageKind { Hello, Request, Response, Error, Progress, Cancel, Shutdown }
public enum WorkerOutcome { NotSent, Unknown }
public enum WorkerProtocolFailure
{
    FrameLimit, DepthLimit, ValueCountLimit, InvalidUtf8, InvalidUnicode, MalformedFrame,
    DuplicateProperty, NonFiniteNumber, UnsupportedVersion, PartialFinalFrame,
    InvalidEnvelope, Handshake, StaleGeneration, Correlation, PendingLimit,
    CallbackLimit, HandleLimit, WriteLimit, BufferedLimit, ProgressLimit,
    RemoteError, Cancelled, EndOfInput, Closed, Transport
}
public sealed class WorkerProtocolException : IOException
{
    public WorkerProtocolFailure Failure { get; }
    public WorkerOutcome Outcome { get; }
    public string? RemoteCode { get; }
    public WorkerProtocolException(WorkerProtocolFailure failure, WorkerOutcome outcome = WorkerOutcome.NotSent,
        string? remoteCode = null) : base($"Extension worker protocol: {failure}.")
    { Failure = failure; Outcome = outcome; RemoteCode = remoteCode; }
}
public sealed record WorkerCallbackHandle(string OwnerId, long OwnerGeneration, string RegistrationId, string CallbackId);
public sealed record WorkerMessage(WorkerMessageKind Kind, long WorkerGeneration, long SessionGeneration,
    long? Id = null, string? Method = null, WorkerValue? Value = null, WorkerCallbackHandle? Handle = null,
    ImmutableArray<string> Features = default, string? ErrorCode = null, WorkerOutcome Outcome = WorkerOutcome.Unknown);
public sealed record WorkerProtocolOptions(int MaximumFrameBytes = 1_048_576, int MaximumJsonDepth = 32,
    int MaximumJsonValues = 65_536, int MaximumPendingCalls = 32, int MaximumCallbacks = 16,
    int MaximumPendingWrites = 32, int MaximumHandles = 128, int MaximumOwners = 128,
    int MaximumProgressPerCall = 16, long MaximumBufferedBytes = 8_388_608, int ReadBufferSize = 4096)
{
    internal void Validate()
    {
        if (MaximumFrameBytes is < 1 or > 16_777_216 || MaximumJsonDepth is < 1 or > 64 ||
            MaximumJsonValues is < 1 or > 1_048_576 || MaximumPendingCalls is < 1 or > 4096 ||
            MaximumCallbacks is < 1 or > 4096 || MaximumPendingWrites is < 1 or > 4096 ||
            MaximumHandles is < 1 or > 4096 || MaximumOwners is < 1 or > 4096 ||
            MaximumProgressPerCall is < 1 or > 4096 || MaximumBufferedBytes is < 1 or > 268_435_456 ||
            ReadBufferSize is < 1 or > 65_536) throw new ArgumentOutOfRangeException(nameof(WorkerProtocolOptions));
    }
}
public delegate ValueTask<WorkerValue> WorkerRequestHandler(WorkerRequestContext request, CancellationToken cancellationToken);
public sealed class WorkerRequestContext
{
    public long Id { get; }
    public string Method { get; }
    public WorkerValue Value { get; }
    public WorkerCallbackHandle? Handle { get; }
    private readonly Func<WorkerValue, CancellationToken, ValueTask> _progress;
    internal WorkerRequestContext(WorkerMessage message, Func<WorkerValue, CancellationToken, ValueTask> progress)
    { Id = message.Id!.Value; Method = message.Method!; Value = message.Value!; Handle = message.Handle; _progress = progress; }
    public ValueTask ReportProgressAsync(WorkerValue value, CancellationToken cancellationToken = default) =>
        _progress(value, cancellationToken);
}
public sealed record WorkerProtocolSnapshot(int PendingCalls, int ActiveCallbacks, int PendingWrites,
    int RegisteredHandles, long BufferedBytes, bool Ready, bool Stopped);
