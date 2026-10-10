using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.Sessions.Serialization;

public sealed record SessionEntryMigrationOptions(int MaximumRecords = 100_000,
    int MaximumInputBytes = 16_777_216, int MaximumOutputBytes = 16_777_216,
    SessionEntryCodecOptions? CodecOptions = null);
public enum SessionEntryMigrationStatus { Completed, Blocked }
public enum SessionEntryMigrationCode
{
    ResourceLimit, InvalidJson, MissingHeader, UnexpectedHeader, InvalidVersion, UnsupportedVersion,
    FutureVersion, IdPlanRequired, InvalidIdPlan, UnsafeCompactionReference, CurrentRecordInvalid, UnknownEntryRetained
}
public enum SessionEntryMigrationTransform { HeaderVersion, TreeId, TreeParent, CompactionBoundary, LegacyIndexRemoved, HookRole }
public sealed record SessionEntryMigrationDiagnostic(SessionEntryMigrationCode Code, int RecordIndex,
    SessionEntryCodecFailure? CodecFailure = null)
{
    public bool IsBlocking => Code != SessionEntryMigrationCode.UnknownEntryRetained;
    public string Message => Code switch
    {
        SessionEntryMigrationCode.ResourceLimit => "Session migration exceeds configured limits.",
        SessionEntryMigrationCode.FutureVersion => "Future session version is retained for inspection only.",
        SessionEntryMigrationCode.IdPlanRequired => "Legacy tree migration requires explicit entry identifiers.",
        SessionEntryMigrationCode.InvalidIdPlan => "Legacy entry identifier plan is invalid.",
        SessionEntryMigrationCode.UnsafeCompactionReference => "Legacy compaction reference cannot be converted safely.",
        SessionEntryMigrationCode.UnknownEntryRetained => "Unknown entry is retained as inert JSON.",
        _ => "Session records cannot complete the supported migration."
    };
}
public sealed record SessionEntryMigrationReceipt(int RecordIndex, SessionEntryMigrationTransform Transform,
    string Field, bool BeforePresent, JsonData? Before, bool AfterPresent, JsonData? After);
public sealed record SessionEntryMigrationResult(SessionEntryMigrationStatus Status, long? SourceVersion,
    bool VersionDefaulted, ImmutableArray<JsonData> SourceRecords, ImmutableArray<SessionEntry> Records,
    ImmutableArray<SessionEntryMigrationReceipt> Receipts, ImmutableArray<SessionEntryMigrationDiagnostic> Diagnostics,
    JsonData? SourceArray = null)
{
    public int TargetVersion => SessionEntryCodec.CurrentVersion;
    public bool WasMigrated => Status == SessionEntryMigrationStatus.Completed && SourceVersion is 1 or 2;
}

/// <summary>Pure explicit v1/v2 conversion. It owns no file, random generator, clock or extension authority.</summary>
public sealed class SessionEntryMigration
{
    private readonly SessionEntryMigrationOptions _options;
    private readonly SessionEntryCodecOptions _codecOptions;
    private readonly SessionEntryCodec _codec;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public SessionEntryMigration(SessionEntryMigrationOptions? options = null)
    {
        _options = options ?? new(); _codecOptions = _options.CodecOptions ?? new(); _codec = new(_codecOptions);
        if (_options.MaximumRecords <= 0 || _options.MaximumInputBytes <= 0 || _options.MaximumOutputBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Invalid session migration limits.");
    }

    public SessionEntryMigrationResult MigrateArray(JsonData array, ImmutableArray<string> v1EntryIds = default)
    {
        ArgumentNullException.ThrowIfNull(array);
        var raw = array.ToString();
        var arrayBytes = StrictByteCount(raw);
        if (arrayBytes is null)
            return Block([], null, false, SessionEntryMigrationCode.InvalidJson, -1) with { SourceArray = array };
        if (arrayBytes > _options.MaximumInputBytes)
            return Block([], null, false, SessionEntryMigrationCode.ResourceLimit, -1) with { SourceArray = array };
        if (array.Value.ValueKind != JsonValueKind.Array)
            return Block([], null, false, SessionEntryMigrationCode.InvalidJson, -1) with { SourceArray = array };
        if (array.Value.GetArrayLength() > _options.MaximumRecords)
            return Block([], null, false, SessionEntryMigrationCode.ResourceLimit, -1) with { SourceArray = array };
        // Reparse the entire retained syntax; an externally created JsonElement may have allowed comments/commas.
        try
        {
            using var document = JsonDocument.Parse(raw, PiSharp.Contracts.JsonData.DocumentOptions);
            var owned = document.RootElement.EnumerateArray().Select(JsonData.FromElement).ToImmutableArray();
            return Migrate(owned, v1EntryIds) with { SourceArray = array };
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        { return Block([], null, false, SessionEntryMigrationCode.InvalidJson, -1) with { SourceArray = array }; }
    }

    public SessionEntryMigrationResult Migrate(ImmutableArray<JsonData> records, ImmutableArray<string> v1EntryIds = default)
    {
        if (records.IsDefaultOrEmpty) return Block([], null, false, SessionEntryMigrationCode.MissingHeader, -1);
        if (records.Length > _options.MaximumRecords)
            return Block(records, null, false, SessionEntryMigrationCode.ResourceLimit, -1);
        var input = ImmutableArray.CreateBuilder<JsonData>(records.Length); long total = 0;
        for (var index = 0; index < records.Length; index++)
        {
            var record = records[index];
            if (record is null) return Block(records, null, false, SessionEntryMigrationCode.InvalidJson, index);
            var raw = record.ToString(); var bytes = StrictByteCount(raw);
            if (bytes is null) return Block(records, null, false, SessionEntryMigrationCode.InvalidJson, index);
            if (raw.Length > _codecOptions.MaximumRecordCharacters || bytes > _codecOptions.MaximumUtf8Bytes ||
                (total += bytes.Value) > _options.MaximumInputBytes)
                return Block(records, null, false, SessionEntryMigrationCode.ResourceLimit, index);
            try
            {
                using var document = JsonDocument.Parse(raw, PiSharp.Contracts.JsonData.DocumentOptions);
                ValidateJson(document.RootElement, 0);
                if (document.RootElement.ValueKind != JsonValueKind.Object || !TryString(document.RootElement, "type", out var type) || type.Length == 0)
                    return Block(records, null, false, SessionEntryMigrationCode.InvalidJson, index);
                input.Add(JsonData.FromElement(document.RootElement));
            }
            catch (MigrationLimitException) { return Block(records, null, false, SessionEntryMigrationCode.ResourceLimit, index); }
            catch (Exception error) when (error is JsonException or InvalidOperationException or EncoderFallbackException)
            { return Block(records, null, false, SessionEntryMigrationCode.InvalidJson, index); }
        }
        var originals = input.MoveToImmutable(); var header = originals[0].Value;
        if (header.GetProperty("type").GetString() != "session")
            return Block(originals, null, false, SessionEntryMigrationCode.MissingHeader, 0);
        for (var index = 1; index < originals.Length; index++)
            if (originals[index].Value.GetProperty("type").GetString() == "session")
                return Block(originals, null, false, SessionEntryMigrationCode.UnexpectedHeader, index);
        var defaulted = !header.TryGetProperty("version", out var version) || version.ValueKind == JsonValueKind.Null;
        long sourceVersion = 1;
        if (!defaulted && (version.ValueKind != JsonValueKind.Number || !version.TryGetInt64(out sourceVersion)))
            return Block(originals, null, false, SessionEntryMigrationCode.InvalidVersion, 0);
        if (sourceVersion > SessionEntryCodec.CurrentVersion)
            return Block(originals, sourceVersion, defaulted, SessionEntryMigrationCode.FutureVersion, 0);
        if (sourceVersion is not (1 or 2 or 3))
            return Block(originals, sourceVersion, defaulted, SessionEntryMigrationCode.UnsupportedVersion, 0);
        if (sourceVersion == 1 && originals.Length > 1 && v1EntryIds.IsDefaultOrEmpty)
            return Block(originals, sourceVersion, defaulted, SessionEntryMigrationCode.IdPlanRequired, -1);
        if ((sourceVersion != 1 && !v1EntryIds.IsDefaultOrEmpty) ||
            (sourceVersion == 1 && !ValidIdPlan(v1EntryIds, originals.Length - 1)))
            return Block(originals, sourceVersion, defaulted, SessionEntryMigrationCode.InvalidIdPlan, -1);

        var receipts = ImmutableArray.CreateBuilder<SessionEntryMigrationReceipt>();
        var diagnostics = ImmutableArray.CreateBuilder<SessionEntryMigrationDiagnostic>();
        var output = ImmutableArray.CreateBuilder<SessionEntry>(originals.Length); long outputBytes = 0;
        for (var index = 0; index < originals.Length; index++)
        {
            var value = originals[index].Value; var updates = new Dictionary<string, string?>(StringComparer.Ordinal);
            void Set(string name, string? raw, SessionEntryMigrationTransform transform)
            {
                var beforePresent = value.TryGetProperty(name, out var before);
                receipts.Add(new(index, transform, "/" + name, beforePresent, beforePresent ? JsonData.FromElement(before) : null,
                    raw is not null, raw is null ? null : JsonData.Parse(raw)));
                updates.Add(name, raw);
            }
            if (index == 0 && sourceVersion < 3) Set("version", "3", SessionEntryMigrationTransform.HeaderVersion);
            if (index > 0 && sourceVersion == 1)
            {
                Set("id", JsonSerializer.Serialize(v1EntryIds[index - 1]), SessionEntryMigrationTransform.TreeId);
                Set("parentId", index == 1 ? "null" : JsonSerializer.Serialize(v1EntryIds[index - 2]), SessionEntryMigrationTransform.TreeParent);
                if (value.GetProperty("type").GetString() == "compaction" && value.TryGetProperty("firstKeptEntryIndex", out var boundary) && boundary.ValueKind == JsonValueKind.Number)
                {
                    // The upstream loop has assigned only the preceding/current IDs at this point.
                    if (!boundary.TryGetInt64(out var target) || target < 1 || target > index)
                        return Block(originals, sourceVersion, defaulted, SessionEntryMigrationCode.UnsafeCompactionReference, index);
                    Set("firstKeptEntryId", JsonSerializer.Serialize(v1EntryIds[(int)target - 1]), SessionEntryMigrationTransform.CompactionBoundary);
                    Set("firstKeptEntryIndex", null, SessionEntryMigrationTransform.LegacyIndexRemoved);
                }
            }
            if (index > 0 && sourceVersion < 3 && value.GetProperty("type").GetString() == "message" &&
                value.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object &&
                TryString(message, "role", out var role) && role == "hookMessage")
            {
                receipts.Add(new(index, SessionEntryMigrationTransform.HookRole, "/message/role", true,
                    JsonData.FromElement(message.GetProperty("role")), true, JsonData.Parse("\"custom\"")));
                updates.Add("message", Rewrite(message, new(StringComparer.Ordinal) { ["role"] = "\"custom\"" }));
            }
            var transformed = updates.Count == 0 ? originals[index].ToString() : Rewrite(value, updates);
            var encodedBytes = StrictByteCount(transformed);
            if (encodedBytes is null || (outputBytes += encodedBytes.Value) > _options.MaximumOutputBytes)
                return Block(originals, sourceVersion, defaulted, SessionEntryMigrationCode.ResourceLimit, index);
            try
            {
                var entry = _codec.ParseUtf8(StrictUtf8.GetBytes(transformed)); output.Add(entry);
                if (entry.Kind == SessionEntryKind.Unknown) diagnostics.Add(new(SessionEntryMigrationCode.UnknownEntryRetained, index));
            }
            catch (SessionEntryCodecException error)
            {
                var code = error.Failure is SessionEntryCodecFailure.CharacterLimit or SessionEntryCodecFailure.Utf8ByteLimit or SessionEntryCodecFailure.DepthLimit
                    ? SessionEntryMigrationCode.ResourceLimit : SessionEntryMigrationCode.CurrentRecordInvalid;
                return Block(originals, sourceVersion, defaulted, code, index, error.Failure);
            }
        }
        return new(SessionEntryMigrationStatus.Completed, sourceVersion, defaulted, originals,
            output.MoveToImmutable(), receipts.ToImmutable(), diagnostics.ToImmutable());
    }

    private bool ValidIdPlan(ImmutableArray<string> ids, int count)
    {
        if (count == 0) return ids.IsDefaultOrEmpty;
        if (ids.IsDefault || ids.Length != count) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
            if (string.IsNullOrEmpty(id) || id.Length > _codecOptions.MaximumRecordCharacters || StrictByteCount(id) is null || !seen.Add(id)) return false;
        return true;
    }
    private void ValidateJson(JsonElement value, int depth)
    {
        if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            if (++depth > _codecOptions.MaximumJsonDepth) throw new MigrationLimitException();
            if (value.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    if (StrictByteCount(property.Name) is null || !names.Add(property.Name)) throw new JsonException();
                    ValidateJson(property.Value, depth);
                }
            }
            else foreach (var item in value.EnumerateArray()) ValidateJson(item, depth);
        }
        else if (value.ValueKind == JsonValueKind.String && StrictByteCount(value.GetString()!) is null) throw new JsonException();
    }
    private static int? StrictByteCount(string value)
    { try { return StrictUtf8.GetByteCount(value); } catch (EncoderFallbackException) { return null; } }
    private static bool TryString(JsonElement value, string name, out string text)
    {
        if (value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String) { text = item.GetString()!; return true; }
        text = ""; return false;
    }
    private static string Rewrite(JsonElement value, Dictionary<string, string?> updates)
    {
        var text = new StringBuilder("{"); var first = true; var seen = new HashSet<string>(StringComparer.Ordinal);
        void Write(string name, string raw)
        { if (!first) text.Append(','); first = false; text.Append(JsonSerializer.Serialize(name)).Append(':').Append(raw); }
        foreach (var property in value.EnumerateObject())
        {
            seen.Add(property.Name);
            if (updates.TryGetValue(property.Name, out var replacement)) { if (replacement is not null) Write(property.Name, replacement); }
            else Write(property.Name, property.Value.GetRawText());
        }
        foreach (var update in updates) if (!seen.Contains(update.Key) && update.Value is not null) Write(update.Key, update.Value);
        return text.Append('}').ToString();
    }
    private static SessionEntryMigrationResult Block(ImmutableArray<JsonData> source, long? version, bool defaulted,
        SessionEntryMigrationCode code, int index, SessionEntryCodecFailure? codecFailure = null) =>
        new(SessionEntryMigrationStatus.Blocked, version, defaulted, source, [], [], [new(code, index, codecFailure)]);
    private sealed class MigrationLimitException : Exception { }
}
