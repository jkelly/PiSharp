using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Sessions.Serialization;

internal static class SessionEntryMigrationTests
{
    private const string Header = """{"type":"session","id":"session","timestamp":"header-time","cwd":"explicit/path","opaque":{"n":1e400,"null":null}}""";
    private const string User = """{"type":"message","timestamp":"entry-time","message":{"role":"user","content":"hi","timestamp":1,"opaque":{"number":-0.00e+09}},"optional":null}""";
    private static JsonData Data(string value) => JsonData.Parse(value);
    private static string Version(string raw, string version) => raw[..^1] + ",\"version\":" + version + "}";
    private static JsonData Tree(string raw, string id = "entry", string? parent = null) => Data(raw[..^1] + ",\"id\":" +
        JsonSerializer.Serialize(id) + ",\"parentId\":" + JsonSerializer.Serialize(parent) + "}");
    private static SessionEntryMigrationResult Run(ImmutableArray<JsonData> records, ImmutableArray<string> ids = default) => new SessionEntryMigration().Migrate(records, ids);
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("session migration v1 missing null and explicit versions create owned linear identities", V1Identities),
        ("session migration compaction indexes address full file array and preserve opaque fields", CompactionBoundaries),
        ("session migration v2 hook role changes only role and header version", HookRole),
        ("session migration v3 repeat conversion is stable and detached from document lifetime", CurrentAndOwnership),
        ("session migration unknown inert entries survive and future or invalid versions block", VersionsAndUnknown),
        ("session migration identity plans and insufficient known fields block without receipts", Rejections),
        ("session migration ambiguous legacy compaction indexes preserve source and block", UnsafeBoundaries),
        ("session migration strict array syntax unicode depth byte and count limits are bounded", LimitsAndStrictJson)
    ];

    private static Task V1Identities()
    {
        foreach (var header in new[] { Header, Version(Header, "null"), Version(Header, "1") })
        {
            var legacy = Tree(User, "old-id", "old-parent"); var source = ImmutableArray.Create(Data(header), legacy, Data(User));
            var originalText = source.Select(record => record.ToString()).ToArray();
            var result = Run(source, ["00000001", "00000002"]); Completed(result);
            Check(result.WasMigrated && result.SourceVersion == 1 && result.TargetVersion == 3, "Version receipt differs.");
            Equal(header == Header || header == Version(Header, "null"), result.VersionDefaulted);
            Equal("00000001", result.Records[1].Id); Equal<string?>(null, result.Records[1].ParentId);
            Equal("00000002", result.Records[2].Id); Equal("00000001", result.Records[2].ParentId);
            Equal("1e400", result.Records[0].WireBody.Value.GetProperty("opaque").GetProperty("n").GetRawText());
            Equal("-0.00e+09", result.Records[1].WireBody.Value.GetProperty("message").GetProperty("opaque").GetProperty("number").GetRawText());
            Equal(JsonValueKind.Null, result.Records[1].WireBody.Value.GetProperty("optional").ValueKind);
            Check(!result.Records[1].WireBody.Value.TryGetProperty("missing", out _), "Migration invented an optional field.");
            var overwritten = result.Receipts.Single(receipt => receipt.RecordIndex == 1 && receipt.Transform == SessionEntryMigrationTransform.TreeId);
            Check(overwritten.BeforePresent && overwritten.AfterPresent, "Identity overwrite did not disclose both values.");
            Equal("old-id", overwritten.Before!.Value.GetString()); Equal("00000001", overwritten.After!.Value.GetString());
            for (var index = 0; index < source.Length; index++) Equal(originalText[index], source[index].ToString());
        }
        return Task.CompletedTask;
    }

    private static Task CompactionBoundaries()
    {
        const string compaction = """{"type":"compaction","timestamp":"c-time","summary":"summary","firstKeptEntryIndex":1,"firstKeptEntryId":"overwritten","tokensBefore":12,"details":{"value":1e400,"absent":null}}""";
        var source = ImmutableArray.Create(Data(Header), Data(User), Data(compaction));
        var result = Run(source, ["message-id", "compaction-id"]); Completed(result);
        var converted = result.Records[2].WireBody.Value;
        Equal("message-id", converted.GetProperty("firstKeptEntryId").GetString());
        Check(!converted.TryGetProperty("firstKeptEntryIndex", out _), "Legacy index survived a numeric conversion.");
        Equal(source[2].Value.GetProperty("details").GetRawText(), converted.GetProperty("details").GetRawText());
        Check(result.Receipts.Any(receipt => receipt.Transform == SessionEntryMigrationTransform.LegacyIndexRemoved && !receipt.AfterPresent), "Index removal was not receipted.");
        var self = Run([Data(Header), Data(compaction)], ["self"]);
        Completed(self); Equal("self", self.Records[1].WireBody.Value.GetProperty("firstKeptEntryId").GetString());
        var nonnumeric = Run([Data(Header), Data(compaction.Replace("\"firstKeptEntryIndex\":1", "\"firstKeptEntryIndex\":null", StringComparison.Ordinal))], ["one"]);
        Completed(nonnumeric); Equal(JsonValueKind.Null, nonnumeric.Records[1].WireBody.Value.GetProperty("firstKeptEntryIndex").ValueKind);
        Equal("overwritten", nonnumeric.Records[1].WireBody.Value.GetProperty("firstKeptEntryId").GetString());
        return Task.CompletedTask;
    }

    private static Task HookRole()
    {
        const string hook = """{"type":"message","timestamp":"old-time","message":{"role":"hookMessage","timestamp":7,"customType":"plugin-state","content":"text","display":false,"details":{"opaque":1e400,"null":null},"unknown":"\\uD800"},"outer":null}""";
        var source = ImmutableArray.Create(Data(Version(Header, "2")), Tree(hook, "kept-id"));
        var result = Run(source); Completed(result); Equal(2L, result.SourceVersion);
        Equal("kept-id", result.Records[1].Id); Equal<string?>(null, result.Records[1].ParentId);
        var message = result.Records[1].WireBody.Value.GetProperty("message"); Equal("custom", message.GetProperty("role").GetString());
        foreach (var property in source[1].Value.GetProperty("message").EnumerateObject())
            if (property.Name != "role") Equal(property.Value.GetRawText(), message.GetProperty(property.Name).GetRawText());
        Equal(2, result.Receipts.Length); Check(result.Receipts.Any(receipt => receipt.Field == "/message/role"), "Hook role receipt missing.");
        Equal("hookMessage", source[1].Value.GetProperty("message").GetProperty("role").GetString());
        return Task.CompletedTask;
    }

    private static Task CurrentAndOwnership()
    {
        SessionEntryMigrationResult migrated;
        using (var document = JsonDocument.Parse("[" + Header + "," + User + "]"))
            migrated = new SessionEntryMigration().MigrateArray(JsonData.FromElement(document.RootElement), ["stable"]);
        Completed(migrated); Equal("hi", migrated.Records[1].WireBody.Value.GetProperty("message").GetProperty("content").GetString());
        Check(migrated.SourceArray is not null, "Array source was not retained.");
        var current = migrated.Records.Select(entry => entry.WireBody).ToImmutableArray(); var repeat = Run(current);
        Completed(repeat); Check(!repeat.WasMigrated && repeat.Receipts.IsEmpty, "Current records were migrated again.");
        for (var index = 0; index < current.Length; index++) Equal(current[index].ToString(), repeat.Records[index].WireBody.ToString());
        var spaced = Data(" \t" + Version(Header, "3") + "\r\n"); var unchanged = Run([spaced]); Completed(unchanged);
        Equal(spaced.Value.GetRawText(), unchanged.Records[0].WireBody.ToString());
        return Task.CompletedTask;
    }

    private static Task VersionsAndUnknown()
    {
        foreach (var version in new[] { "4", "9223372036854775807" })
        {
            var source = Data(Version(Header, version)); var result = Run([source]); Blocked(result, SessionEntryMigrationCode.FutureVersion);
            Equal(source.ToString(), result.SourceRecords[0].ToString()); Check(result.Records.IsEmpty && result.Receipts.IsEmpty, "Future format produced current records.");
        }
        foreach (var version in new[] { "0", "-1" }) Blocked(Run([Data(Version(Header, version))]), SessionEntryMigrationCode.UnsupportedVersion);
        foreach (var version in new[] { "1.0", "1e400", "\"2\"", "true" }) Blocked(Run([Data(Version(Header, version))]), SessionEntryMigrationCode.InvalidVersion);
        Blocked(Run([Data(User)]), SessionEntryMigrationCode.MissingHeader); Blocked(Run([]), SessionEntryMigrationCode.MissingHeader);
        Blocked(Run([Data(Header), Data(Header)]), SessionEntryMigrationCode.UnexpectedHeader);
        const string unknown = """{"type":"future-entry","timestamp":"unchanged","opaque":{"callback":"System.Type","number":1e400,"value":null}}""";
        var retained = Run([Data(Header), Data(unknown)], ["unknown-id"]); Completed(retained);
        Equal(SessionEntryKind.Unknown, retained.Records[1].Kind); Equal(SessionEntryMigrationCode.UnknownEntryRetained, retained.Diagnostics.Single().Code);
        Equal(Data(unknown).Value.GetProperty("opaque").GetRawText(), retained.Records[1].WireBody.Value.GetProperty("opaque").GetRawText());
        return Task.CompletedTask;
    }

    private static Task Rejections()
    {
        var source = ImmutableArray.Create(Data(Header), Data(User), Data(User));
        Blocked(Run(source), SessionEntryMigrationCode.IdPlanRequired);
        foreach (var ids in new ImmutableArray<string>[] { ["short"], ["same", "same"], ["", "two"], ["\uD800", "two"] })
            Blocked(Run(source, ids), SessionEntryMigrationCode.InvalidIdPlan);
        Blocked(Run([Data(Version(Header, "2")), Tree(User)], ["ignored"]), SessionEntryMigrationCode.InvalidIdPlan);
        const string incompleteHook = """{"type":"message","timestamp":"time","message":{"role":"hookMessage","content":"text","timestamp":1}}""";
        var blocked = Run([Data(Version(Header, "2")), Tree(incompleteHook)]); Blocked(blocked, SessionEntryMigrationCode.CurrentRecordInvalid);
        Equal("hookMessage", blocked.SourceRecords[1].Value.GetProperty("message").GetProperty("role").GetString());
        Check(blocked.Records.IsEmpty && blocked.Receipts.IsEmpty, "Blocked conversion advertised partial completion.");
        return Task.CompletedTask;
    }

    private static Task UnsafeBoundaries()
    {
        foreach (var boundary in new[] { "0", "-1", "2", "9000", "1.0", "1e400" })
        {
            var compaction = Data("{\"type\":\"compaction\",\"timestamp\":\"time\",\"summary\":\"summary\",\"tokensBefore\":1,\"firstKeptEntryIndex\":" + boundary + "}");
            var source = ImmutableArray.Create(Data(Header), compaction, Data(User)); var result = Run(source, ["comp", "later"]);
            Blocked(result, SessionEntryMigrationCode.UnsafeCompactionReference); Equal(compaction.ToString(), result.SourceRecords[1].ToString());
        }
        return Task.CompletedTask;
    }

    private static Task LimitsAndStrictJson()
    {
        var current = ImmutableArray.Create(Data(Version(Header, "3")), Tree(User));
        var bytes = current.Sum(record => Encoding.UTF8.GetByteCount(record.ToString()));
        Completed(new SessionEntryMigration(new(MaximumInputBytes: bytes, MaximumOutputBytes: bytes)).Migrate(current));
        Blocked(new SessionEntryMigration(new(MaximumInputBytes: bytes - 1)).Migrate(current), SessionEntryMigrationCode.ResourceLimit);
        Blocked(new SessionEntryMigration(new(MaximumOutputBytes: bytes - 1)).Migrate(current), SessionEntryMigrationCode.ResourceLimit);
        Blocked(new SessionEntryMigration(new(MaximumRecords: 1)).Migrate(current), SessionEntryMigrationCode.ResourceLimit);
        var array = Data("[" + string.Join(",", current.Select(record => record.ToString())) + "]");
        var arrayBytes = Encoding.UTF8.GetByteCount(array.ToString());
        Completed(new SessionEntryMigration(new(MaximumInputBytes: arrayBytes, MaximumOutputBytes: bytes)).MigrateArray(array));
        var arrayLimit = new SessionEntryMigration(new(MaximumInputBytes: arrayBytes - 1)).MigrateArray(array);
        Blocked(arrayLimit, SessionEntryMigrationCode.ResourceLimit);
        Check(ReferenceEquals(array, arrayLimit.SourceArray) && arrayLimit.SourceRecords.IsEmpty, "Array limit materialized or lost source.");
        Blocked(new SessionEntryMigration(new(MaximumRecords: 1)).MigrateArray(array), SessionEntryMigrationCode.ResourceLimit);
        Blocked(new SessionEntryMigration(new(MaximumOutputBytes: bytes)).Migrate([Data(Header), Data(User)], [new string('a', bytes)]), SessionEntryMigrationCode.ResourceLimit);
        var depth = Data(Header[..^1] + ",\"nested\":{\"deep\":{\"leaf\":null}}}");
        Blocked(new SessionEntryMigration(new(CodecOptions: new(MaximumJsonDepth: 2))).Migrate([depth]), SessionEntryMigrationCode.ResourceLimit);
        foreach (var raw in new[] { "[" + Version(Header, "3") + ",]", "[" + Version(Header, "3") + ",/*opaque comment*/" + Tree(User) + "]" })
        {
            using var permissive = JsonDocument.Parse(raw, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var result = new SessionEntryMigration().MigrateArray(JsonData.FromElement(permissive.RootElement));
            Blocked(result, SessionEntryMigrationCode.InvalidJson); Equal(raw, result.SourceArray!.ToString());
        }
        using (var permissive = JsonDocument.Parse(Version(Header, "3")[..^1] + ",}", new JsonDocumentOptions { AllowTrailingCommas = true }))
            Blocked(Run([JsonData.FromElement(permissive.RootElement)]), SessionEntryMigrationCode.InvalidJson);
        var lone = Data(Header[..^1] + ",\"escapedLone\":\"\\uD800\"}"); Blocked(Run([lone]), SessionEntryMigrationCode.InvalidJson);
        var unicode = Data(Version(Header, "3")[..^1] + ",\"unicode\":\"" + "\u03c0\U0001f600e\u0301" + "\"}");
        var unicodeBytes = Encoding.UTF8.GetByteCount(unicode.ToString()); Check(unicodeBytes > unicode.ToString().Length, "Unicode byte fixture became ASCII.");
        Completed(new SessionEntryMigration(new(MaximumInputBytes: unicodeBytes, MaximumOutputBytes: unicodeBytes)).Migrate([unicode]));
        Blocked(new SessionEntryMigration(new(MaximumInputBytes: unicodeBytes - 1)).Migrate([unicode]), SessionEntryMigrationCode.ResourceLimit);
        Blocked(new SessionEntryMigration(new(CodecOptions: new(MaximumUtf8Bytes: unicodeBytes - 1))).Migrate([unicode]), SessionEntryMigrationCode.ResourceLimit);
        return Task.CompletedTask;
    }

    private static void Completed(SessionEntryMigrationResult result)
    {
        Equal(SessionEntryMigrationStatus.Completed, result.Status); Check(result.Diagnostics.All(diagnostic => !diagnostic.IsBlocking), "Completed migration has a blocker.");
        foreach (var record in result.Records) _ = new SessionEntryCodec().Read(record.WireBody.Value);
    }
    private static void Blocked(SessionEntryMigrationResult result, SessionEntryMigrationCode code)
    {
        Equal(SessionEntryMigrationStatus.Blocked, result.Status); Equal(code, result.Diagnostics.Single().Code);
        Check(result.Diagnostics[0].IsBlocking && result.Records.IsEmpty && result.Receipts.IsEmpty, "Blocked migration exposed partial success.");
        Check(!result.Diagnostics[0].Message.Contains("header-time", StringComparison.Ordinal), "Diagnostic leaked record content.");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
}
