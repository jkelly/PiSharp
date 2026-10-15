// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/chord/src/delta/index.ts (encoder, decoder). Path interning and arity
// omission live between the tracker and a boundary; Op and Apply know nothing about them. One encoder/decoder pair per independent state
// stream: every decoder must observe exactly the batches its matching encoder produced, beginning with that state's base.
using System.Collections.Immutable;

namespace PiSharp.Durable.Chord.Delta;

/// <summary>
/// Encodes operation batches as wire tuples. A path is inlined on first use and interned on its second (<c>["#", id, path]</c> then the
/// id), because a definition costs more than the path it replaces. A batch's op that repeats the previous op's path omits it (arity
/// tells the forms apart). A base batch (one with <c>r</c>) resets the dictionary, so a reader can recover from it with a fresh decoder.
/// </summary>
public sealed class DeltaEncoder
{
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> ids = new(StringComparer.Ordinal);
    private int nextId;

    /// <summary>Encodes one batch.</summary>
    public ImmutableArray<JsonArrayValue> Encode(IEnumerable<Op> ops)
    {
        ArgumentNullException.ThrowIfNull(ops);
        string? previous = null; // arity omission is scoped to a batch
        var output = ImmutableArray.CreateBuilder<JsonArrayValue>();
        foreach (var op in ops)
        {
            if (op is ReplaceOp replace)
            {
                output.Add(replace.ToJson());
                seen.Clear(); ids.Clear(); nextId = 0; previous = null;
                continue;
            }
            var pathOp = (PathOp)op;
            var key = DeltaPath.Key(pathOp.Path);
            if (key == previous)
            {
                output.Add(JsonValue.Array([JsonValue.String(op.Verb), .. Payload(op)]));
                continue;
            }
            JsonValue reference = DeltaPath.ToJson(pathOp.Path);
            if (ids.TryGetValue(key, out var existing)) reference = JsonValue.Number(existing);
            else if (seen.Contains(key))
            {
                var id = nextId++;
                ids[key] = id;
                output.Add(JsonValue.Array(JsonValue.String("#"), JsonValue.Number(id), DeltaPath.ToJson(pathOp.Path)));
                reference = JsonValue.Number(id);
            }
            else seen.Add(key);
            output.Add(JsonValue.Array([JsonValue.String(op.Verb), reference, .. Payload(op)]));
            previous = key;
        }
        return output.ToImmutable();
    }

    private static IEnumerable<JsonValue> Payload(Op op) => op switch
    {
        SetOp set => [set.Value],
        DeleteOp => [],
        AppendOp append => [JsonValue.String(append.Text)],
        TruncateOp truncate => [JsonValue.Number(truncate.Count)],
        SpliceOp splice => [JsonValue.Number(splice.Index), JsonValue.Number(splice.Remove), new JsonArrayValue(splice.Items)],
        MoveOp move => [JsonValue.Array(move.Permutation.Select(index => (JsonValue)index))],
        _ => throw new ArgumentException("Unexpected operation.", nameof(op))
    };
}

/// <summary>Decodes wire tuples back into operations (the inverse of <see cref="DeltaEncoder"/>).</summary>
public sealed class DeltaDecoder
{
    private readonly Dictionary<long, ImmutableArray<PathSegment>> paths = [];

    /// <summary>Decodes one batch.</summary>
    public ImmutableArray<Op> Decode(IEnumerable<JsonValue> wire)
    {
        ArgumentNullException.ThrowIfNull(wire);
        ImmutableArray<PathSegment>? previous = null; // scoped to the batch, as in Encode
        var output = ImmutableArray.CreateBuilder<Op>();
        foreach (var item in wire)
        {
            DeltaValidation.AssertValidWireOp(item);
            var op = (JsonArrayValue)item;
            var verb = ((JsonStringValue)op[0]).Value;
            if (verb == "#")
            {
                paths[(long)((JsonNumberValue)op[1]).Value] = DeltaValidation.ReadPath(op[2]);
                continue;
            }
            if (verb == "r")
            {
                output.Add(new ReplaceOp(op[1]));
                paths.Clear(); previous = null;
                continue;
            }
            var isShort = (verb == "d" && op.Count == 1) || (verb is not ("d" or "p") && op.Count == 2) || (verb == "p" && op.Count == 4);
            ImmutableArray<PathSegment> path;
            if (isShort) path = previous ?? throw new PathError(ImmutableArray<PathSegment>.Empty);
            else
            {
                path = op[1] is JsonNumberValue id
                    ? paths.TryGetValue((long)id.Value, out var resolved) ? resolved : throw new PathError((long)id.Value)
                    : DeltaValidation.ReadPath(op[1]);
                previous = path;
            }
            if (verb is not ("p" or "m") && path.IsEmpty) throw new PathError(path);
            var rest = isShort ? op.Items[1..] : op.Items[2..];
            output.Add(verb switch
            {
                "s" => new SetOp(path, rest[0]),
                "d" => new DeleteOp(path),
                "a" => new AppendOp(path, ((JsonStringValue)rest[0]).Value),
                "t" => new TruncateOp(path, (long)((JsonNumberValue)rest[0]).Value),
                "p" => new SpliceOp(path, (long)((JsonNumberValue)rest[0]).Value, (long)((JsonNumberValue)rest[1]).Value, ((JsonArrayValue)rest[2]).Items),
                _ => new MoveOp(path, [.. ((JsonArrayValue)rest[0]).Select(index => (int)((JsonNumberValue)index).Value)])
            });
        }
        return output.ToImmutable();
    }
}
