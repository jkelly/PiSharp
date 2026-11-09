// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/jobs/definition.ts
using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Documents;

namespace PiSharp.Chord.Jobs;

public enum JobResume { Rerun, Abandon }
public sealed record JobPersistence(JobResume Resume);
public sealed record JobAdapter(Func<JsonNode?, JsonNode?> Args, Func<object?, object?> Result);
public sealed class JobOptions
{
    public required long Version { get; init; }
    public required JsonNode Args { get; init; }
    public required JsonNode Result { get; init; }
    public StateDefinition? State { get; init; }
    public int? Concurrency { get; init; }
    public IReadOnlyDictionary<long, JobAdapter>? Accepts { get; init; }
    public JobPersistence? Persist { get; init; }
}

/// <summary>A named, versioned job declaration. Arguments are closed JSON; only transient results may contain bytes.</summary>
public sealed class JobDefinition
{
    internal Snapshot ArgumentSnapshot { get; }
    internal Snapshot ResultSnapshot { get; }
    private JobDefinition(string name, JobOptions options, Snapshot args, Snapshot result)
    {
        Name = name; Version = options.Version; ArgumentSnapshot = args; ResultSnapshot = result;
        State = options.State; Concurrency = options.Concurrency; Persist = options.Persist;
        Accepts = new ReadOnlyDictionary<long, JobAdapter>(new Dictionary<long, JobAdapter>(options.Accepts ?? new Dictionary<long, JobAdapter>()));
        Fingerprint = ContractsInternal.Hash64(ChordJson.Stringify(new JsonObject
        {
            ["args"] = args.Normal.DeepClone(), ["result"] = result.Normal.DeepClone(), ["state"] = State?.Fingerprint,
        }));
    }
    public string Name { get; }
    public long Version { get; }
    public JsonNode Args => ArgumentSnapshot.Schema.DeepClone();
    public JsonNode Result => ResultSnapshot.Schema.DeepClone();
    public StateDefinition? State { get; }
    public int? Concurrency { get; }
    public IReadOnlyDictionary<long, JobAdapter> Accepts { get; }
    public JobPersistence? Persist { get; }
    public string Fingerprint { get; }

    public static JobDefinition Define(string name, JobOptions options)
    {
        ContractsInternal.CheckName(name, "A job name");
        ArgumentNullException.ThrowIfNull(options);
        if (options.Version is < 1 or > 9007199254740991L) throw new ArgumentException($"{name}.version: expected a positive integer");
        var args = ContractsInternal.SnapshotArguments(options.Args, $"{name}.args");
        if (args.Bytes) throw new ArgumentException($"{name}.args: job arguments are JSON; no bytes()");
        var result = ContractsInternal.SnapshotSchema(options.Result, $"{name}.result");
        if (options.State?.Keyed == true) throw new ArgumentException($"{name}.state: expected a singleton document definition");
        if (options.Concurrency is <= 0) throw new ArgumentException($"{name}.concurrency: expected a positive integer");
        if (options.Persist is { } persistence)
        {
            if (!Enum.IsDefined(persistence.Resume)) throw new ArgumentException($"{name}.persist.resume: expected rerun or abandon");
            if (options.State is { Persist: false }) throw new ArgumentException($"{name}.state: a persisted job's state must be persisted");
            if (result.Bytes) throw new ArgumentException($"{name}.result: a persisted job's result is JSON; no bytes()");
        }
        foreach (var (version, adapter) in options.Accepts ?? new Dictionary<long, JobAdapter>())
        {
            if (version < 1 || version >= options.Version) throw new ArgumentException($"{name}.accepts[{version}]: not an older version");
            if (adapter?.Args is null || adapter.Result is null) throw new ArgumentException($"{name}.accepts[{version}]: args and result functions required");
        }
        return new JobDefinition(name, options, args, result);
    }
}
