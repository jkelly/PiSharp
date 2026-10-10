using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using PiSharp.Contracts;

namespace PiSharp.Extensions;

/// <summary>Typed authoring over the existing owner-bound tool callback and its actual native context.</summary>
public delegate ValueTask<ExtensionTypedToolResult<TDetails>> ExtensionTypedToolCallback<TArguments, TDetails>(
    TArguments arguments, IExtensionToolContext context, CancellationToken cancellationToken);

/// <summary>Bounded conversion before host admission; these limits do not increase host result quotas.</summary>
public sealed record ExtensionTypedToolConversionOptions(int MaximumUtf8Bytes = 262_144, int MaximumJsonDepth = 32);

/// <summary>Model-facing content and typed details. Null optional JSON references mean absent;
/// JsonData.Null means an explicitly present JSON null. Host result admission remains authoritative.</summary>
public sealed record ExtensionTypedToolResult<TDetails>(JsonData Content, TDetails Details)
{
    public JsonData? StructuredContent { get; init; }
    public JsonData? Usage { get; init; }
    public bool? IsError { get; init; }
    public bool? Terminate { get; init; }

    public JsonData ToJson(JsonTypeInfo<TDetails> detailsType, ExtensionTypedToolConversionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(detailsType);
        ArgumentNullException.ThrowIfNull(Content);
        if (Content.Value.ValueKind != JsonValueKind.Array)
            throw new JsonException("Typed tool content must be an array.");
        var limits = options ?? new();
        if (limits.MaximumUtf8Bytes is < 512 or > 16 * 1024 * 1024 || limits.MaximumJsonDepth is < 1 or > PiSharp.Contracts.JsonData.MaximumDepth)
            throw new ArgumentOutOfRangeException(nameof(options));
        var buffer = new TypedToolJsonBuffer(limits.MaximumUtf8Bytes);
        using (var writer = new Utf8JsonWriter(buffer, new() { MaxDepth = limits.MaximumJsonDepth }))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("content"); Content.Value.WriteTo(writer);
            writer.WritePropertyName("details"); JsonSerializer.Serialize(writer, Details, detailsType);
            if (StructuredContent is { } structured)
            { writer.WritePropertyName("structuredContent"); structured.Value.WriteTo(writer); }
            if (Usage is { } usage)
            { writer.WritePropertyName("usage"); usage.Value.WriteTo(writer); }
            if (IsError is { } error) writer.WriteBoolean("isError", error);
            if (Terminate is { } terminate) writer.WriteBoolean("terminate", terminate);
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory, new JsonDocumentOptions { MaxDepth = limits.MaximumJsonDepth });
        return JsonData.FromElement(document.RootElement);
    }
}

/// <summary>No inferred schema or separate execution path: the returned descriptor uses normal host admission,
/// final argument validation, cancellation, callback ownership and result limits.</summary>
public static class ExtensionTypedTool
{
    public static ExtensionToolDescriptor Define<TArguments, TDetails>(string registrationId, string name,
        string description, JsonData parameters, JsonTypeInfo<TArguments> argumentsType,
        JsonTypeInfo<TDetails> detailsType, ExtensionTypedToolCallback<TArguments, TDetails> executeAsync,
        ExtensionTypedToolConversionOptions? conversionOptions = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(argumentsType);
        ArgumentNullException.ThrowIfNull(detailsType);
        ArgumentNullException.ThrowIfNull(executeAsync);
        if (executeAsync.GetInvocationList().Length != 1)
            throw new ArgumentException("Typed tools require one callback.", nameof(executeAsync));
        return new(registrationId, name, description, parameters, Execute);

        async ValueTask<JsonData> Execute(JsonData arguments, IExtensionToolContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (arguments.Value.ValueKind != JsonValueKind.Object)
                throw new JsonException("Typed tool arguments must be an object.");
            var typed = JsonSerializer.Deserialize(arguments.Value, argumentsType);
            if (typed is null) throw new JsonException("Typed tool arguments cannot deserialize to null.");
            token.ThrowIfCancellationRequested();
            // Retain and await this original once, including plugin cleanup and faults; never race cancellation.
            var original = executeAsync(typed, context, token);
            var result = await original.ConfigureAwait(false);
            if (result is null) throw new JsonException("Typed tool callback returned no result.");
            return result.ToJson(detailsType, conversionOptions);
        }
    }

    /// <summary>Serialize a typed update through the original invocation context's awaited delivery receipt.</summary>
    public static ValueTask ReportUpdateAsync<TDetails>(this IExtensionToolInvocationContext context,
        ExtensionTypedToolResult<TDetails> partialResult, JsonTypeInfo<TDetails> detailsType,
        CancellationToken cancellationToken = default, ExtensionTypedToolConversionOptions? conversionOptions = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(partialResult);
        cancellationToken.ThrowIfCancellationRequested();
        return context.ReportUpdateAsync(partialResult.ToJson(detailsType, conversionOptions), cancellationToken);
    }
}

// One fixed-size buffer plus bounded writer slack. A huge converter string cannot cause writer growth.
internal sealed class TypedToolJsonBuffer(int maximumBytes) : IBufferWriter<byte>
{
    private readonly byte[] bytes = new byte[checked(maximumBytes + 4096)];
    private int written;
    internal ReadOnlyMemory<byte> WrittenMemory => bytes.AsMemory(0, written);
    public void Advance(int count)
    {
        if (count < 0 || count > maximumBytes - written) throw Oversized();
        written += count;
    }
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        if (sizeHint < 0 || Math.Max(sizeHint, 1) > bytes.Length - written) throw Oversized();
        return bytes.AsMemory(written);
    }
    public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
    private static JsonException Oversized() => new("Typed tool JSON conversion exceeded its UTF-8 byte limit.");
}
