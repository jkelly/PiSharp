// Decision 0012: versioning. A run stores its workflow version with its input and checkpoint. A change that live runs cannot follow
// (an action renamed or removed, a Data shape changed) bumps the version and supplies a migration, which durable runs when it reserves a
// run stored by an older version (TaskDefinition.Migrate). Without a migration such runs are blocked as migration_failed; a run stored by
// a newer version than the code knows is blocked as task_too_old.
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using JsonValue = PiSharp.Durable.Chord.JsonValue;
using PiSharp.Durable;
using PiSharp.Durable.Chord;

namespace PiSharp.CustomAgents.Workflows;

/// <summary>A stored run as a migration sees it: where it stands and its Data.</summary>
/// <param name="Action">The action (phase) the run will run next.</param>
/// <param name="Data">The run's Data as JSON.</param>
public sealed record StoredRun(string Action, JsonNode? Data);

/// <summary>Converts runs stored by an older workflow version.</summary>
public sealed class WorkflowMigration
{
    private readonly ImmutableArray<Func<StoredRun, int, StoredRun>> steps;

    private WorkflowMigration(ImmutableArray<Func<StoredRun, int, StoredRun>> steps) => this.steps = steps;

    /// <summary>A migration from a function of the stored run and its version.</summary>
    public static WorkflowMigration Custom(Func<StoredRun, int, StoredRun> migrate) => new([migrate ?? throw new ArgumentNullException(nameof(migrate))]);

    /// <summary>Renames actions: a run stopped at an old name continues at the new one.</summary>
    public static WorkflowMigration RenameActions(params (string From, string To)[] renames)
    {
        var map = renames.ToImmutableDictionary(rename => rename.From, rename => rename.To, StringComparer.Ordinal);
        return Custom((run, _) => map.TryGetValue(run.Action, out var renamed) ? run with { Action = renamed } : run);
    }

    /// <summary>Transforms the stored Data JSON (for example to add or rename a property).</summary>
    public static WorkflowMigration TransformData(Func<JsonNode?, int, JsonNode?> transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        return Custom((run, version) => run with { Data = transform(run.Data, version) });
    }

    /// <summary>Applies <paramref name="migrations"/> in order.</summary>
    public static WorkflowMigration Compose(params WorkflowMigration[] migrations) => new([.. migrations.SelectMany(migration => migration.steps)]);

    /// <summary>The durable migration: rewrites the checkpoint's phase, visit counts and Data, and the input's Data.</summary>
    internal TaskMigration ToTaskMigration() => (input, checkpoint, fromVersion) =>
    {
        var stored = (JsonObjectValue)checkpoint;
        var phase = stored.TryGetValue("phase", out var named) && named is JsonStringValue text ? text.Value : "#start";
        var suffix = "";
        var join = phase.IndexOf('#', 1);
        if (join > 0)
        {
            suffix = phase[join..];
            phase = phase[..join];
        }
        var data = stored.TryGetValue("data", out var value) ? JsonNode.Parse(value.ToJson()) : null;
        var run = new StoredRun(phase, data?.DeepClone());
        foreach (var step in steps) run = step(run, fromVersion);
        var migratedData = run.Data is null ? JsonValue.Null : JsonValue.Parse(run.Data.ToJsonString());
        var output = stored.SetItem("phase", phase.StartsWith('#') ? phase + suffix : run.Action + suffix).SetItem("data", migratedData);
        if (stored.TryGetValue("visits", out var visits) && visits is JsonObjectValue counts)
        {
            var renamed = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
            foreach (var (action, count) in counts)
            {
                // Each visited action maps through the whole migration, with a copy of the Data (a custom step may route by it).
                var target = steps.Aggregate(new StoredRun(action, data?.DeepClone()), (current, step) => step(current, fromVersion)).Action;
                renamed[target] = count;
            }
            output = output.SetItem("visits", JsonValue.Object(renamed));
        }
        var original = (JsonObjectValue)input;
        var migratedInput = original.ContainsKey("data") && original["data"] is not JsonNullValue
            ? original.SetItem("data", MigrateInputData(original["data"], fromVersion))
            : original;
        return new TaskMigrationResult(migratedInput, output);
    };

    private JsonValue MigrateInputData(JsonValue data, int fromVersion)
    {
        var run = new StoredRun("#start", JsonNode.Parse(data.ToJson()));
        foreach (var step in steps) run = step(run, fromVersion);
        return run.Data is null ? JsonValue.Null : JsonValue.Parse(run.Data.ToJsonString());
    }
}
