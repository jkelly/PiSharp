// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/jobs/{job,run}.ts
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Documents;
using PiSharp.Chord.Host;
using PiSharp.Chord.State;

namespace PiSharp.Chord.Jobs;

public sealed record StartOptions(string? Key = null);
public enum AbortResult { Marked, Terminal }
public sealed record JobError(string Kind, Exception? Error = null, string? Reason = null, int? Restarts = null);
public sealed record JobOutcome(string Id, JsonNode? Args, JsonObject? State, string Kind, object? Result = null, JobError? Error = null);
public interface IJobRun
{
    string Id { get; }
    Task<JobOutcome> OutcomeAsync(ChordContext context);
}
public interface IJobHandle
{
    Task<IJobRun> StartAsync(JsonNode? args, ChordContext context, StartOptions? options = null);
    Task<JobOutcome> OutcomeAsync(string id, ChordContext context);
    Task<AbortResult> AbortAsync(string id, ChordContext context);
    IWatchable<JsonNode?> Running { get; }
}
public sealed class JobImplementation
{
    public required Func<JsonNode?, JobContext, Task<object?>> Run { get; init; }
    public Func<JobOutcome, ITx, Task>? Finish { get; init; }
}

/// <summary>A run's call context and invocation-scoped state. Cancellation of the starting call does not cancel this context.</summary>
public sealed class JobContext : IDisposable
{
    internal JobContext(CallContext call, string id, RunState? state) { Call = call; JobId = id; State = state; }
    public CallContext Call { get; }
    public ChordContext Context => Call.Context;
    public JsonObject? Principal => Call.Principal;
    public string Incarnation => Call.Incarnation;
    public CancellationToken AbortSignal => Call.AbortSignal;
    public string JobId { get; }
    public RunState? State { get; }
    public Task<IReference> GetAsync(Contract type, JsonObject parameters, GetOptions? options = null) => Call.GetAsync(type, parameters, options);
    public void Dispose() => Call.Dispose();
}

public sealed class RunState
{
    private readonly Func<JsonObject> read;
    private readonly Func<Func<JsonObject, object?>, ChordContext, CommitPersistence?, Task<object?>> change;
    internal RunState(Func<JsonObject> read, Func<Func<JsonObject, object?>, ChordContext, CommitPersistence?, Task<object?>> change)
    { this.read = read; this.change = change; }
    public JsonObject Value => read();
    public async Task<T> ChangeAsync<T>(Func<JsonObject, T> update, ChordContext context, CommitPersistence? persistence = null) =>
        (T)(await change(draft => update(draft), context, persistence))!;
    public Task ChangeAsync(Action<JsonObject> update, ChordContext context, CommitPersistence? persistence = null) =>
        change(draft => { update(draft); return null; }, context, persistence);
}

/// <summary>Declares work owned by this object, with one record set retained across attempts.</summary>
public static class JobExtensions
{
    public static IJobHandle Job(this ISelf self, JobDefinition definition, JobImplementation implementation)
    {
        ArgumentNullException.ThrowIfNull(self); ArgumentNullException.ThrowIfNull(definition); ArgumentNullException.ThrowIfNull(implementation);
        ArgumentNullException.ThrowIfNull(implementation.Run);
        if (self is IJobDeclarer recording) return recording.DeclareJob(definition, implementation);
        if (self is not Self native) throw new ArgumentException("Jobs require a Chord object self", nameof(self));
        return (IJobHandle)native.DeclareJob(definition.Name, owner => HostedJob.Create(definition, implementation, owner));
    }
}

internal interface IJobDeclarer
{
    IJobHandle DeclareJob(JobDefinition definition, JobImplementation implementation);
}
