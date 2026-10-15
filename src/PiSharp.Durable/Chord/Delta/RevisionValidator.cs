// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/chord/src/delta/revision-validator.ts. Upstream walks each
// revision for strict JSON (no cycles, plain objects, dense arrays, data properties, finite numbers) and remembers validated containers
// so a revision costs only its new containers. JsonValue enforces all of those rules at construction, so what remains is the walk and
// its memo: a consumer can still call Validate on every revision and pay only for containers it has not seen.
using System.Runtime.CompilerServices;

namespace PiSharp.Durable.Chord.Delta;

/// <summary>Validates immutable replica revisions while skipping containers validated in earlier revisions.</summary>
public sealed class JsonRevisionValidator
{
    private static readonly object Seen = new();
    private readonly ConditionalWeakTable<JsonValue, object> validated = new();

    /// <summary>Validates <paramref name="value"/> and returns it.</summary>
    public T Validate<T>(T value) where T : JsonValue
    {
        ArgumentNullException.ThrowIfNull(value);
        var pending = new Stack<(JsonValue Value, bool Children)>();
        pending.Push((value, false));
        while (pending.TryPop(out var item))
        {
            if (item.Value is not (JsonObjectValue or JsonArrayValue) || validated.TryGetValue(item.Value, out _)) continue;
            if (item.Children)
            {
                validated.AddOrUpdate(item.Value, Seen); // after its children, as upstream marks a container once it finished
                continue;
            }
            pending.Push((item.Value, true));
            IEnumerable<JsonValue> children = item.Value is JsonArrayValue array ? array : ((JsonObjectValue)item.Value).Values;
            foreach (var child in children) pending.Push((child, false));
        }
        return value;
    }

    /// <summary>Whether a container has been validated by this validator.</summary>
    public bool HasValidated(JsonValue value) => validated.TryGetValue(value, out _);
}
