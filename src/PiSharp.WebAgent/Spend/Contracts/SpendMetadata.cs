// Decision 0011 (run W3, S0): the Spend Guard's fields in a durable request's metadata.pisharp. W2 (WU1, WU7) sends {account, tier} from
// the chat binding plus op, the host's operation id (new for each prepared request, the same when it is recovered); S0 adds source ("chat")
// and run (the chat id) in DeviceHostService's SessionStream, so the guard can apply per-source and per-run (per-chat) caps and key the
// reservation on op. The object is saved with the prepared request, so a recovered request keeps all five; a route strips it before any
// provider sees it (SessionStreamMetadata.Strip).
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Hosting;

namespace PiSharp.WebAgent.Spend;

/// <summary>The <c>metadata.pisharp</c> fields of a durable request, as the Spend Guard reads them.</summary>
/// <param name="Account">The chat's account id (<c>account</c>), or null.</param>
/// <param name="Tier">The service tier (<c>tier</c>), or null.</param>
/// <param name="Op">The operation id (<c>op</c>): the reservation's key; null when the host set none.</param>
/// <param name="Source">Where the request was started (<c>source</c>), or null when it is missing or unknown.</param>
/// <param name="Run">The run key (<c>run</c>: the chat id), or null.</param>
public sealed record SpendStreamFields(string? Account, string? Tier, string? Op, SpendSource? Source, string? Run);

/// <summary>Writes and reads the Spend Guard's <c>metadata.pisharp</c> fields.</summary>
public static class SpendMetadata
{
    /// <summary>The account field: <c>account</c>.</summary>
    public const string AccountField = "account";

    /// <summary>The service tier field: <c>tier</c>.</summary>
    public const string TierField = "tier";

    /// <summary>The source field: <c>source</c> (a <see cref="SpendSource"/> wire name).</summary>
    public const string SourceField = "source";

    /// <summary>The run key field: <c>run</c>.</summary>
    public const string RunField = "run";

    /// <summary><paramref name="fields"/> (a SessionStream hook's, or none) with <c>source</c> and, when given, <c>run</c> set.</summary>
    public static JsonObjectValue With(JsonObjectValue? fields, SpendSource source, string? run)
    {
        var result = (fields ?? JsonObjectValue.Empty).SetItem(SourceField, source.Wire());
        return string.IsNullOrEmpty(run) ? result.Remove(RunField) : result.SetItem(RunField, run);
    }

    /// <summary>The fields of a chat's requests: <paramref name="fields"/> (its binding's <c>account</c> and <c>tier</c>) with
    /// <c>source</c> <c>chat</c> and <c>run</c> <paramref name="chatId"/>.</summary>
    public static JsonObjectValue Chat(JsonObjectValue? fields, string chatId)
    {
        ArgumentException.ThrowIfNullOrEmpty(chatId);
        return With(fields, SpendSource.Chat, chatId);
    }

    /// <summary>The fields of <paramref name="options"/>' <c>metadata.pisharp</c>; every field null when it has none.</summary>
    public static SpendStreamFields Read(DurableStreamOptions? options) => Read(SessionStreamMetadata.Read(options));

    /// <summary>The fields of a <c>pisharp</c> object (<see cref="SessionStreamMetadata.Read(JsonObjectValue?)"/>).</summary>
    public static SpendStreamFields Read(JsonObjectValue? pisharp)
    {
        string? Text(string name) => pisharp is not null && pisharp.TryGetValue(name, out var value) && value is JsonStringValue text && text.Value.Length > 0 ? text.Value : null;
        SpendSource? source = SpendSources.TryParse(Text(SourceField), out var parsed) ? parsed : null;
        return new SpendStreamFields(Text(AccountField), Text(TierField), Text(SessionStreamMetadata.OperationField), source, Text(RunField));
    }
}
