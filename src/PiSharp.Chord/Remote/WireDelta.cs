// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): delta/index.ts encoder/decoder.
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PiSharp.Chord.Delta;

namespace PiSharp.Chord.Remote;

// A decoder is cloned during frame preparation. No dictionary mutation is visible before the whole frame passes.
internal sealed class WireDeltaDecoder
{
    private readonly Dictionary<long, JsonArray> paths = [];
    internal WireDeltaDecoder Clone()
    {
        var copy = new WireDeltaDecoder(); foreach (var pair in paths) copy.paths.Add(pair.Key, pair.Value); return copy;
    }
    internal ImmutableArray<Op> Decode(JsonNode? wire)
    {
        if (wire is not JsonArray array) throw new ArgumentException("part: a member's changes are a list of ops");
        var output = ImmutableArray.CreateBuilder<Op>(); JsonArray? previous = null;
        foreach (var item in array)
        {
            if (item is not JsonArray { Count: > 0 } tuple || ChordJson.AsString(tuple[0]) is not { } verb) throw new ArgumentException("op is not a tuple");
            if (verb == "#")
            {
                if (tuple.Count != 3 || !Integer(tuple[1], out var id) || tuple[2] is not JsonArray path) throw new ArgumentException("# shape");
                ValidatePath(path); paths[id] = (JsonArray)path.DeepClone(); continue;
            }
            if (verb == "r")
            {
                DeltaApply.AssertValidOp(tuple); output.Add(new ReplaceOp(tuple[1])); paths.Clear(); previous = null; continue;
            }
            var fullLength = verb switch { "s" or "a" or "t" or "m" => 3, "d" => 2, "p" => 5, _ => throw new ArgumentException("unknown op verb: " + verb) };
            if (tuple.Count != fullLength && tuple.Count != fullLength - 1) throw new ArgumentException(verb + " arity");
            var shortForm = tuple.Count == fullLength - 1;
            JsonArray selected;
            if (shortForm) selected = previous ?? throw new ArgumentException("unresolvable previous path");
            else
            {
                if (tuple[1] is JsonArray inline) selected = inline;
                else if (Integer(tuple[1], out var id) && paths.TryGetValue(id, out var found)) selected = found;
                else throw new ArgumentException("unresolvable path id");
                ValidatePath(selected); previous = selected;
            }
            var expanded = new JsonArray(JsonValue.Create(verb), selected.DeepClone());
            foreach (var payload in tuple.Skip(shortForm ? 1 : 2)) expanded.Add(payload?.DeepClone());
            DeltaApply.AssertValidOp(expanded);
            var segments = selected.Select(x => ChordJson.AsString(x) is { } key ? PathSegment.Of(key) : PathSegment.Of((long)ChordJson.AsNumber(x)!)).ToImmutableArray();
            output.Add(verb switch
            {
                "s" => new SetOp(segments, expanded[2]), "d" => new DeleteOp(segments),
                "a" => new AppendOp(segments, ChordJson.AsString(expanded[2])!),
                "t" => new TruncateOp(segments, (long)ChordJson.AsNumber(expanded[2])!),
                "p" => new SpliceOp(segments, (long)ChordJson.AsNumber(expanded[2])!, (long)ChordJson.AsNumber(expanded[3])!, expanded[4]!.AsArray().ToImmutableArray()),
                _ => new MoveOp(segments, expanded[2]!.AsArray().Select(x => (int)ChordJson.AsNumber(x)!).ToImmutableArray()),
            });
        }
        return output.ToImmutable();
    }
    private static bool Integer(JsonNode? raw, out long value)
    {
        var number = ChordJson.AsNumber(raw); value = 0;
        if (number is null || number < 0 || number > 9007199254740991 || number != Math.Floor(number.Value)) return false;
        value = (long)number.Value; return true;
    }
    private static void ValidatePath(JsonArray path)
    {
        foreach (var segment in path)
            if (ChordJson.AsString(segment) is { } key)
            { if (DeltaOps.ReservedSegments.Contains(key)) throw new ArgumentException("unsafe path segment: " + key); }
            else if (!Integer(segment, out _)) throw new ArgumentException("unsafe path segment");
    }
}
internal sealed class WireDeltaEncoder
{
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> paths = new(StringComparer.Ordinal);
    private long next;
    internal JsonArray Encode(IEnumerable<Op> ops)
    {
        var output = new JsonArray(); string? previous = null;
        foreach (var op in ops)
        {
            var tuple = op.ToJson();
            if (op is ReplaceOp) { output.Add(tuple); seen.Clear(); paths.Clear(); next = 0; previous = null; continue; }
            var path = ((PathOp)op).Path; var key = DeltaPath.Key(path);
            if (key == previous) { tuple.RemoveAt(1); output.Add(tuple); continue; }
            if (paths.TryGetValue(key, out var id)) tuple[1] = JsonValue.Create(id);
            else if (!seen.Add(key))
            {
                id = next++; paths.Add(key, id); output.Add(new JsonArray("#", JsonValue.Create(id), DeltaPath.ToJson(path))); tuple[1] = JsonValue.Create(id);
            }
            previous = key; output.Add(tuple);
        }
        return output;
    }
}
