// PiSharp-only (no upstream Pi source): strict local fixture and artifact helpers.
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Samples;
namespace PiSharp.Samples.PurchaseRequestCoordinator;

internal static class Data
{
    public static JsonNode Json(object value) => JsonSerializer.SerializeToNode(value)!;
    public static IEnumerable<JsonObject> Rows(JsonNode? node) => node is JsonArray a
        ? a.Select(x => x as JsonObject ?? throw new InvalidOperationException("Expected object record."))
        : throw new InvalidOperationException("Required record array is missing.");
    public static string Text(JsonNode node, string key) => node[key]?.GetValue<string>() is { Length: > 0 } s
        ? s : throw new InvalidOperationException("Missing " + key + ".");
    public static long Number(JsonNode node, string key, long minimum = 0) => node[key]?.GetValue<long>() is long n && n >= minimum
        ? n : throw new InvalidOperationException("Invalid " + key + ".");
    public static DateTimeOffset Time(JsonNode node, string key) => DateTimeOffset.Parse(Text(node, key), System.Globalization.CultureInfo.InvariantCulture);
    public static JsonObject? Find(JsonNode? rows, string id)
    {
        var matches = Rows(rows).Where(x => Text(x, "id") == id).ToArray();
        if (matches.Length > 1) throw new InvalidOperationException("Ambiguous record ID: " + id);
        return matches.SingleOrDefault();
    }
    public static string Id(JsonObject arguments, string key)
    {
        var id = Text(arguments, key);
        if (id.Length > 80 || id.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')))
            throw new ArgumentException("Use a bounded alphanumeric ID with dashes or underscores.");
        return id;
    }
    public static string[] Strings(JsonNode? value) => value is JsonArray a
        ? a.Select(x => x?.GetValue<string>() ?? throw new InvalidOperationException("Missing text entry.")).ToArray()
        : throw new InvalidOperationException("Required string array is missing.");
    public static void Approved(SampleContext c)
    {
        if (!c.Approved) throw new InvalidOperationException("Host approval is required for this local mutation.");
    }
    public static JsonObject Missing(string id) => new() { ["found"] = false, ["id"] = id, ["reason"] = "Record not found" };
    public static async Task<JsonObject?> ExistingAsync(SampleContext c, string path, JsonObject payload, CancellationToken token)
    {
        var full = c.ResolvePath(path);
        if (!File.Exists(full)) return null;
        var receipt = JsonNode.Parse(await File.ReadAllTextAsync(full, token))!.AsObject();
        if (!JsonNode.DeepEquals(receipt["payload"], payload)) throw new InvalidOperationException("Idempotency key conflicts with existing payload.");
        receipt["replayed"] = true;
        return receipt;
    }
    public static async Task<JsonNode?> CreateReceiptAsync(SampleContext c, string path, JsonObject payload, JsonNode result, CancellationToken token)
    {
        var full = c.ResolvePath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var receipt = new JsonObject { ["payload"] = payload.DeepClone(), ["result"] = result.DeepClone(), ["replayed"] = false, ["scope"] = "workspace-only" };
        // CreateNew prevents replacing an already committed receipt on a race/retry.
        await using var stream = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, receipt, SampleContext.JsonOptions, token);
        return receipt;
    }
}
