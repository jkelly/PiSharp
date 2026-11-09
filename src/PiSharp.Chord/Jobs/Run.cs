using System.Text.Json.Nodes;
using PiSharp.Chord.Data;
using PiSharp.Chord.Documents;

namespace PiSharp.Chord.Jobs;

internal sealed class Invocation
{
    public CancellationTokenSource Abort { get; } = new();
    public SealToken Token { get; } = new();
    public bool Running { get; private set; } = true;
    public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Settle() => Running = false;
}

internal sealed class JobRun : IJobRun
{
    private readonly TaskCompletionSource<JobOutcome> outcome = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public JobRun(string id, long version, string fingerprint, string? key, JsonNode? args, string phase)
    {
        Id = id; Version = version; Fingerprint = fingerprint; Key = key; Args = args; Phase = phase;
        // A discarded tx may have no consumer of the returned run; still observe its terminal rejection.
        _ = outcome.Task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    }
    public string Id { get; }
    public long Version { get; }
    public string Fingerprint { get; }
    public string? Key { get; }
    public JsonObject? Trace { get; set; }
    public JsonNode? Args { get; }
    public string Phase { get; set; }
    public int Restarts { get; set; }
    public int FinishFailures { get; set; }
    public bool AbortMarked { get; set; }
    public string? Blocked { get; set; }
    public string? BlockedPhase { get; set; }
    public bool Resuming { get; set; }
    public Invocation? Invocation { get; set; }
    public bool Invoked => Invocation?.Running == true;
    public JobOutcome? Outcome { get; set; }
    public JsonObject? Entry { get; set; }
    public long? MappedVersion { get; set; }
    public JsonNode? MappedArgs { get; set; }
    public JobOutcome? MappedOutcome { get; set; }
    public Task<JobOutcome> OutcomeAsync(ChordContext context) => outcome.Task.WaitAsync(context.AbortSignal ?? CancellationToken.None);
    public Task OverAsync(ChordContext context) => (Invocation is { Running: true } invocation ? invocation.Ended.Task : Task.CompletedTask)
        .WaitAsync(context.AbortSignal ?? CancellationToken.None);
    public void Resolve(JobOutcome result) => outcome.TrySetResult(result);
    public void Reject(Exception error) => outcome.TrySetException(error);
    public JsonObject EntryFor(JsonObject? state, JsonNode? args)
    {
        var entry = new JsonObject { ["id"] = Id, ["version"] = Version };
        if (Phase == "blocked") { entry["phase"] = "blocked"; entry["reason"] = Blocked; return entry; }
        if (Key is not null) entry["key"] = Key;
        entry["args"] = args?.DeepClone(); entry["phase"] = Phase == "finishing" ? "finishing" : "running";
        if (state is not null) entry["state"] = state.DeepClone();
        return entry;
    }
}
