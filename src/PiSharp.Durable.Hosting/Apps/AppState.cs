// PiSharp-only (decision 0008): the device's shared App State, after durable-pi's (apps/dpi-sdk/src/app-state.ts, apps/dpi-backend/src/
// shared-state.ts, MIT): one JSON object for every agent and session of a device, changed one JSON Pointer path at a time (create, update,
// delete, increment), with a revision that refuses a stale whole-document save and receipts that make a replayed tool call apply once.
// durable-pi keeps it in a second pi-durable Harness; here it lives in the catalog, whose transaction gives the same atomicity.
using System.Text.Json;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Hosting;

/// <summary>One path operation on the App State.</summary>
public enum AppStateOperation
{
    /// <summary>Adds a field that does not exist, or inserts into an array (<c>-</c> or the length appends).</summary>
    Create,
    /// <summary>Replaces an existing value; the empty path replaces the whole document (an object).</summary>
    Update,
    /// <summary>Removes an existing value; the empty path empties the document.</summary>
    Delete,
    /// <summary>Adds a number (default 1) to an existing number.</summary>
    Increment,
}

/// <summary>A change of one App State path. <see cref="Path"/> is a JSON Pointer (<c>""</c>, <c>/goal</c>, <c>/notes/0</c>).</summary>
public sealed record AppStateChange(AppStateOperation Operation, string Path, JsonValue? Value = null);

/// <summary>The App State document and its revision, which every change increases by one.</summary>
public sealed record AppStateValue(JsonObjectValue Value, long Revision);

/// <summary>A change carried an <c>expectedRevision</c> that is no longer current (<c>conflict</c>).</summary>
public sealed class AppStateConflictException(string message) : DurableHostException("conflict", message);

/// <summary>App State document rules: JSON Pointer paths, the four operations and the size limits.</summary>
public static class AppStateDocument
{
    /// <summary>The deepest nesting a document may have.</summary>
    public const int MaxDepth = 24;

    /// <summary>The largest document, in serialized characters.</summary>
    public const int MaxLength = 100_000;

    /// <summary>The document a device starts with (durable-pi's seed).</summary>
    public static JsonObjectValue Initial { get; } = JsonValue.Object([new("goal", "Explore durable agents"), new("counter", 0.0), new("notes", JsonArrayValue.Empty)]);

    /// <summary>The path's segments, unescaped (<c>~1</c> is <c>/</c>, <c>~0</c> is <c>~</c>).</summary>
    /// <exception cref="HostInvalidException">Not a JSON Pointer, or a segment that would reach an object's prototype.</exception>
    public static IReadOnlyList<string> Segments(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0) return [];
        if (path[0] != '/') throw new HostInvalidException($"The path {path} must be empty or start with /.");
        var segments = path[1..].Split('/').Select(segment => segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)).ToList();
        if (segments.FirstOrDefault(segment => segment is "__proto__" or "constructor" or "prototype") is { } forbidden)
            throw new HostInvalidException($"The path segment {forbidden} is not allowed.");
        return segments;
    }

    /// <summary>The JSON Pointer of segments.</summary>
    public static string Pointer(IEnumerable<string> segments) =>
        string.Concat(segments.Select(segment => "/" + segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)));

    /// <summary>The value at <paramref name="path"/>, or null when there is none.</summary>
    public static JsonValue? Read(JsonObjectValue document, string path)
    {
        ArgumentNullException.ThrowIfNull(document);
        JsonValue current = document;
        foreach (var segment in Segments(path))
        {
            if (Child(current, segment) is not { } child) return null;
            current = child;
        }
        return current;
    }

    /// <summary>Applies one change and checks the result's limits.</summary>
    /// <exception cref="HostInvalidException">The path does not fit the operation, or the result breaks a limit.</exception>
    public static JsonObjectValue Apply(JsonObjectValue document, AppStateChange change)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(change);
        var segments = Segments(change.Path);
        JsonObjectValue result;
        if (segments.Count == 0)
        {
            result = change.Operation switch
            {
                AppStateOperation.Update => change.Value as JsonObjectValue ?? throw new HostInvalidException("The whole App State must be a JSON object."),
                AppStateOperation.Delete => JsonObjectValue.Empty,
                _ => throw new HostInvalidException($"{Name(change.Operation)} needs a path."),
            };
        }
        else
        {
            result = (JsonObjectValue)Replace(document, segments, 0, change, createParents: false);
        }
        Check(result);
        return result;
    }

    /// <summary>Sets <paramref name="path"/> to <paramref name="value"/>, creating it and any missing parent objects (jobs write their
    /// results this way).</summary>
    public static JsonObjectValue Set(JsonObjectValue document, string path, JsonValue value)
    {
        var segments = Segments(path);
        if (segments.Count == 0) return Apply(document, new AppStateChange(AppStateOperation.Update, "", value));
        var result = (JsonObjectValue)Replace(document, segments, 0, new AppStateChange(AppStateOperation.Update, path, value), createParents: true);
        Check(result);
        return result;
    }

    /// <summary>Throws <see cref="HostInvalidException"/> for a document deeper than <see cref="MaxDepth"/>, longer than
    /// <see cref="MaxLength"/> characters, or with a number that is not finite.</summary>
    public static void Check(JsonObjectValue document)
    {
        if (Depth(document) > MaxDepth) throw new HostInvalidException($"The App State may nest at most {MaxDepth} levels.");
        if (document.ToJson().Length > MaxLength) throw new HostInvalidException($"The App State may have at most {MaxLength} characters.");
    }

    internal static JsonObjectValue Parse(string json) =>
        JsonValue.Parse(json) as JsonObjectValue ?? throw new InvalidDataException("The stored App State is not a JSON object.");

    private static JsonValue Replace(JsonValue container, IReadOnlyList<string> segments, int index, AppStateChange change, bool createParents)
    {
        var segment = segments[index];
        var where = Pointer(segments.Take(index + 1));
        if (index < segments.Count - 1)
        {
            var child = Child(container, segment);
            if (child is null)
            {
                if (!createParents || container is not JsonObjectValue) throw new HostInvalidException($"{where} does not exist.");
                child = JsonObjectValue.Empty;
            }
            if (child is not (JsonObjectValue or JsonArrayValue)) throw new HostInvalidException($"{where} is not an object or an array.");
            return With(container, segment, Replace(child, segments, index + 1, change, createParents), where);
        }
        var existing = Child(container, segment);
        switch (change.Operation)
        {
            case AppStateOperation.Create:
                if (container is JsonArrayValue array) return Insert(array, segment, Required(change), where);
                if (existing is not null) throw new HostInvalidException($"{where} already exists.");
                return With(container, segment, Required(change), where);
            case AppStateOperation.Update:
                if (existing is null && !createParents) throw new HostInvalidException($"{where} does not exist.");
                if (existing is null && container is JsonArrayValue) throw new HostInvalidException($"{where} does not exist.");
                return With(container, segment, Required(change), where);
            case AppStateOperation.Delete:
                if (existing is null) throw new HostInvalidException($"{where} does not exist.");
                return Without(container, segment);
            case AppStateOperation.Increment:
                if (existing is not JsonNumberValue number) throw new HostInvalidException(existing is null ? $"{where} does not exist." : $"{where} is not a number.");
                var by = change.Value switch
                {
                    null or JsonNullValue => 1,
                    JsonNumberValue amount => amount.Value,
                    _ => throw new HostInvalidException("increment takes a number."),
                };
                var sum = number.Value + by;
                if (!double.IsFinite(sum)) throw new HostInvalidException($"{where} would not be a finite number.");
                return With(container, segment, sum, where);
            default:
                throw new HostInvalidException($"Unknown operation {change.Operation}.");
        }
    }

    private static JsonValue Required(AppStateChange change) =>
        change.Value ?? throw new HostInvalidException($"{Name(change.Operation)} needs a value.");

    private static JsonValue? Child(JsonValue container, string segment) => container switch
    {
        JsonObjectValue json => json.TryGetValue(segment, out var value) ? value : null,
        JsonArrayValue array => ArrayIndex(segment) is { } index && index < array.Count ? array[index] : null,
        _ => null,
    };

    private static JsonValue With(JsonValue container, string segment, JsonValue value, string where) => container switch
    {
        JsonObjectValue json => json.SetItem(segment, value),
        JsonArrayValue array => ArrayIndex(segment) is { } index && index < array.Count
            ? JsonValue.Array(array.Items.SetItem(index, value))
            : throw new HostInvalidException($"{where} is not an index of the array."),
        _ => throw new HostInvalidException($"{where} is inside a value that is not an object or an array."),
    };

    private static JsonValue Without(JsonValue container, string segment) => container switch
    {
        JsonObjectValue json => json.Remove(segment),
        JsonArrayValue array => JsonValue.Array(array.Items.RemoveAt(ArrayIndex(segment)!.Value)),
        _ => container,
    };

    private static JsonArrayValue Insert(JsonArrayValue array, string segment, JsonValue value, string where)
    {
        var index = segment == "-" ? array.Count : ArrayIndex(segment) ?? throw new HostInvalidException($"{where} is not an index of the array.");
        if (index > array.Count) throw new HostInvalidException($"{where} is past the end of the array.");
        return JsonValue.Array(array.Items.Insert(index, value));
    }

    private static int? ArrayIndex(string segment) =>
        segment.Length > 0 && segment.All(char.IsAsciiDigit) && (segment == "0" || segment[0] != '0') && int.TryParse(segment, out var index) ? index : null;

    private static int Depth(JsonValue value) => value switch
    {
        JsonObjectValue json => 1 + (json.Count == 0 ? 0 : json.Values.Max(Depth)),
        JsonArrayValue array => 1 + (array.Count == 0 ? 0 : array.Items.Max(Depth)),
        _ => 0,
    };

    private static string Name(AppStateOperation operation) => operation.ToString().ToLowerInvariant();
}

/// <summary>The device's App State: read it, change one path, or save the whole document against a revision. Every change notifies
/// <see cref="DeviceChangeKind.AppState"/>.</summary>
public sealed class DeviceAppState
{
    private readonly DurableDeviceHost host;

    internal DeviceAppState(DurableDeviceHost host) => this.host = host;

    /// <summary>The document and its revision; the initial document at revision 0 before the first change.</summary>
    public AppStateValue Get() => host.Catalog.AppState() is { } stored
        ? new AppStateValue(AppStateDocument.Parse(stored.Value), stored.Revision)
        : new AppStateValue(AppStateDocument.Initial, 0);

    /// <summary>
    /// Applies one change atomically. With <paramref name="expectedRevision"/>, a document that changed since is refused with
    /// <see cref="AppStateConflictException"/>. With <paramref name="receiptKey"/>, a key already used returns its first result and changes
    /// nothing.
    /// </summary>
    /// <exception cref="HostInvalidException">The change does not fit the document.</exception>
    public AppStateValue Change(AppStateChange change, long? expectedRevision = null, string? receiptKey = null) =>
        Commit(document => AppStateDocument.Apply(document, change), expectedRevision, receiptKey);

    /// <summary>Saves the whole document, refused when <paramref name="expectedRevision"/> is no longer current.</summary>
    public AppStateValue Replace(JsonObjectValue document, long expectedRevision) =>
        Change(new AppStateChange(AppStateOperation.Update, "", document), expectedRevision);

    /// <summary>Sets a path, creating it and its missing parents (job results).</summary>
    public AppStateValue Set(string path, JsonValue value, string? receiptKey = null) =>
        Commit(document => AppStateDocument.Set(document, path, value), null, receiptKey);

    private AppStateValue Commit(Func<JsonObjectValue, JsonObjectValue> change, long? expectedRevision, string? receiptKey)
    {
        if (receiptKey is { Length: 0 or > 512 }) throw new HostInvalidException("A receipt key must be 1 to 512 characters.");
        var (json, revision) = host.Catalog.ChangeAppState(current =>
        {
            var revisionNow = current?.Revision ?? 0;
            if (expectedRevision is { } expected && expected != revisionNow)
                throw new AppStateConflictException($"App State changed while you were editing it (revision {revisionNow}, not {expected}). Load the latest and try again.");
            var document = current is { } stored ? AppStateDocument.Parse(stored.Value) : AppStateDocument.Initial;
            return change(document).ToJson();
        }, receiptKey, host.Options.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        host.Notify(new DeviceChange(DeviceChangeKind.AppState, null));
        return new AppStateValue(AppStateDocument.Parse(json), revision);
    }

    /// <summary>A change as JSON for the transcript and tools: <c>{operation, path, value?}</c>.</summary>
    internal static JsonObjectValue Describe(AppStateChange change, long revision) => JsonValue.Object(
    [
        new("text", $"Shared App State: {change.Operation.ToString().ToLowerInvariant()} {(change.Path.Length == 0 ? "/" : change.Path)} · revision {revision}"),
        new("operation", change.Operation.ToString().ToLowerInvariant()),
        new("path", change.Path),
        new("value", change.Value ?? JsonValue.Null),
        new("revision", revision),
    ]);

    internal static string Text(JsonValue? value) => value is null ? "null" : JsonSerializer.Serialize(JsonDocument.Parse(value.ToJson()).RootElement,
        new JsonSerializerOptions { WriteIndented = true });
}
