using System.Text.Json;
using PiSharp.CodingAgent;
using PiSharp.Contracts;

namespace PiSharp.Rpc.Protocol;

/// <summary>Projects explicitly supplied compaction semantics onto the pinned original RPC wire.</summary>
/// <remarks>Callers own admission, checkpoint acknowledgement, lifecycle ordering and error provenance.
/// Recovery status/checkpoint identifiers cannot establish an original compaction result.</remarks>
public static class OriginalCompactionEventProjector
{
    public static JsonData Start(SessionCompactionReason reason, RpcDispatchOptions? options = null)
    {
        var origin = Reason(reason);
        return RpcCommandCodec.Event("compaction_start", writer => writer.WriteString("reason", origin), Limits(options));
    }

    public static JsonData Result(string summary, string firstKeptEntryId, double tokensBefore,
        double estimatedTokensAfter, JsonData? usage = null, JsonData? details = null, RpcDispatchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(summary); ArgumentNullException.ThrowIfNull(firstKeptEntryId);
        Number(tokensBefore, nameof(tokensBefore)); Number(estimatedTokensAfter, nameof(estimatedTokensAfter));
        if (usage is not null && usage.Value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Original compaction usage must be an object when supplied.", nameof(usage));
        return RpcCommandCodec.Build(writer =>
        {
            writer.WriteString("summary", summary); writer.WriteString("firstKeptEntryId", firstKeptEntryId);
            writer.WriteNumber("tokensBefore", tokensBefore); writer.WriteNumber("estimatedTokensAfter", estimatedTokensAfter);
            if (usage is not null) RpcCommandCodec.Raw(writer, "usage", usage);
            if (details is not null) RpcCommandCodec.Raw(writer, "details", details);
        }, Limits(options).MaximumOutputBytes);
    }

    public static JsonData End(SessionCompactionReason reason, JsonData? result, bool aborted, bool willRetry,
        string? errorMessage = null, RpcDispatchOptions? options = null)
    {
        var origin = Reason(reason); var limits = Limits(options);
        if (willRetry && (reason != SessionCompactionReason.Overflow || result is null || aborted || errorMessage is not null) ||
            result is not null && (aborted || errorMessage is not null) || aborted && errorMessage is not null)
            throw new ArgumentException("Original compaction terminal inputs are inconsistent.");
        var canonicalResult = result is null ? null : ValidateResult(result, limits);
        return RpcCommandCodec.Event("compaction_end", writer =>
        {
            writer.WriteString("reason", origin);
            if (canonicalResult is not null) RpcCommandCodec.Raw(writer, "result", canonicalResult);
            writer.WriteBoolean("aborted", aborted); writer.WriteBoolean("willRetry", willRetry);
            if (errorMessage is not null) writer.WriteString("errorMessage", errorMessage);
        }, limits);
    }

    private static JsonData ValidateResult(JsonData result, RpcDispatchOptions options)
    {
        var body = result.Value;
        if (body.ValueKind != JsonValueKind.Object || body.EnumerateObject().Any(property =>
            property.Name is not ("summary" or "firstKeptEntryId" or "tokensBefore" or "estimatedTokensAfter" or "usage" or "details")))
            throw new ArgumentException("Expected the original compaction result field inventory.", nameof(result));
        string Text(string name) => body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : throw new ArgumentException("Missing original compaction string " + name + ".", nameof(result));
        double Tokens(string name) => body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetDouble(out var tokens) && double.IsFinite(tokens) && tokens >= 0
            ? tokens : throw new ArgumentException("Missing finite nonnegative original compaction estimate " + name + ".", nameof(result));
        return Result(Text("summary"), Text("firstKeptEntryId"), Tokens("tokensBefore"), Tokens("estimatedTokensAfter"),
            body.TryGetProperty("usage", out var usage) ? JsonData.FromElement(usage) : null,
            body.TryGetProperty("details", out var details) ? JsonData.FromElement(details) : null, options);
    }

    private static string Reason(SessionCompactionReason reason) => reason switch
    {
        SessionCompactionReason.Manual => "manual", SessionCompactionReason.Threshold => "threshold",
        SessionCompactionReason.Overflow => "overflow", _ => throw new ArgumentOutOfRangeException(nameof(reason))
    };
    private static void Number(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0) throw new ArgumentOutOfRangeException(name);
    }
    private static RpcDispatchOptions Limits(RpcDispatchOptions? options)
    {
        var limits = options ?? new();
        if (limits.MaximumOutputBytes is < 256 or > int.MaxValue - 1) throw new ArgumentOutOfRangeException(nameof(options));
        return limits;
    }
}
