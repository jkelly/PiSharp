// PiSharp-only (decision 0011). The `metadata.pisharp` object a session's model requests carry when the host sets
// DurableDeviceOptions.SessionStream: the host's fields (the web agent's chat account and tier) and `op`, an operation ID that is new for
// each prepared request. Generation and compaction pin the request options in the task checkpoint (Generation.cs `streamOptions`,
// Compaction.cs SummaryRequest), so a recovered request reuses its object and a new one is read for the next request. It is routing data
// for the host, never provider metadata: NativeDurableModels' native routes send only `metadata.user_id`, and a host route strips it.
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Hosting;

/// <summary>Reads and writes the <c>metadata.pisharp</c> object of a request's stream options (see
/// <see cref="DurableDeviceOptions.SessionStream"/>).</summary>
public static class SessionStreamMetadata
{
    /// <summary>The metadata key: <c>pisharp</c>.</summary>
    public const string Key = "pisharp";

    /// <summary>The field of the operation ID: <c>op</c>. It is the host's; a field of that name from the hook is replaced.</summary>
    public const string OperationField = "op";

    /// <summary>A fields object from name and value pairs, for a <see cref="DurableDeviceOptions.SessionStream"/> hook; null values are
    /// left out.</summary>
    public static JsonObjectValue Fields(params ReadOnlySpan<(string Name, string? Value)> fields)
    {
        var builder = JsonObjectValue.CreateBuilder();
        foreach (var (name, value) in fields)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);
            if (value is not null) builder[name] = value;
        }
        return builder.ToImmutable();
    }

    /// <summary>The <c>pisharp</c> object of <paramref name="options"/>; null when it has none.</summary>
    public static JsonObjectValue? Read(DurableStreamOptions? options) => Read(options?.Metadata);

    /// <summary>The <c>pisharp</c> object of request <paramref name="metadata"/>; null when it has none.</summary>
    public static JsonObjectValue? Read(JsonObjectValue? metadata) =>
        metadata is not null && metadata.TryGetValue(Key, out var value) && value is JsonObjectValue pisharp ? pisharp : null;

    /// <summary>A string field of the <c>pisharp</c> object, such as <c>account</c>, <c>tier</c> or <c>op</c>; null when it is absent or
    /// not a string.</summary>
    public static string? Field(DurableStreamOptions? options, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Read(options) is { } pisharp && pisharp.TryGetValue(name, out var value) && value is JsonStringValue text ? text.Value : null;
    }

    /// <summary>The request's operation ID: new for each prepared request, the same when that request is recovered.</summary>
    public static string? Operation(DurableStreamOptions? options) => Field(options, OperationField);

    /// <summary>
    /// <paramref name="options"/> without <c>metadata.pisharp</c>, for a route that passes metadata on to a provider; metadata left empty
    /// becomes null. Options without the object come back as they are.
    /// </summary>
    public static DurableStreamOptions? Strip(DurableStreamOptions? options)
    {
        if (options?.Metadata is not { } metadata || !metadata.ContainsKey(Key)) return options;
        var rest = metadata.Remove(Key);
        return options with { Metadata = rest.Count == 0 ? null : rest };
    }

    /// <summary>The object a session's next request carries: the hook's fields, then <c>op</c>.</summary>
    internal static JsonObjectValue Create(JsonObjectValue? fields, string operation) =>
        (fields ?? JsonObjectValue.Empty).Remove(OperationField).SetItem(OperationField, operation);

    /// <summary><paramref name="options"/> (or none) with <paramref name="pisharp"/> as <c>metadata.pisharp</c>; other metadata is
    /// kept.</summary>
    internal static ConversationStreamOptions Attach(ConversationStreamOptions? options, JsonObjectValue pisharp) =>
        (options ?? new ConversationStreamOptions()) with { Metadata = (options?.Metadata ?? JsonObjectValue.Empty).SetItem(Key, pisharp) };
}
