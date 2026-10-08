using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.Cli.Commands;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Import;
using PiSharp.Sessions.Storage;

internal static class SessionCopyCommandTests
{
    private const string Time = "2024-01-01T00:00:00.000Z", Private = "private-copy-fixture-payload";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static string _host = "", _cli = "";
    public static (string Name, Func<Task> Run)[] Cases(string dotnetHost, string cliDll)
    {
        if (!Path.IsPathFullyQualified(dotnetHost) || !Path.IsPathFullyQualified(cliDll) || !File.Exists(dotnetHost) || !File.Exists(cliDll))
            throw new ArgumentException("Session copy child tests require existing explicit host and CLI paths.");
        _host = dotnetHost; _cli = cliDll;
        return
        [
            ("Session copy compiled exact publication reopens and resumes without replaying source effects", ExactCopyAndResume),
            ("Session copy compiled native/current exports retain raw opaque data and every forest branch", FormatsAndForest),
            ("Session copy compiled future archive discloses inert format and exact retention", FutureArchive),
            ("Session copy compiled archive identities remain physical while current migrations apply explicit plans", ArchiveMigrationApplied),
            ("Session copy compiled future incomplete and unavailable archive inventories disclose exact bytes", ArchiveInventoryDisclosures),
            ("Session migration compiled explicit v1 plans and v2 hooks expose complete transformation receipts", LegacyReceipts),
            ("Session copy compiled future/damaged inspection and whole-forest diagnostics block publication", BlockedSources),
            ("Session copy compiled no-overwrite collisions and strict explicit argument grammar preserve source", PathsAndArguments),
            ("Session copy ID plan strict UTF8 Unicode depth duplicates count and exact byte bounds precede effects", PlanAdmission),
            ("Session copy cancellation joins source/plan/temp cleanup and classifies foreign cancellation", CancellationAndCleanup),
            ("Session copy faults uncertain/known publication late cancellation and output failure remain honest", FaultsAndPublication)
        ];
    }

    private static async Task FutureArchive()
    {
        using var files=new Files();var bytes=Utf8.GetBytes("{\"type\":\"session\",\"version\":99,\"id\":\"../../inert\",\"timestamp\":\"opaque\",\"cwd\":\"unopened:/cwd\"}\r\n{\"type\":\"future\",\"data\":1e400}");await File.WriteAllBytesAsync(files.Source,bytes);
        var record=Published(await Child(files,"session","copy","--source",files.Source,"--destination",files.Destination,"--format","native-archive-exact"),"native-archive-exact");
        Equal(JsonValueKind.Null,record.GetProperty("targetVersion").ValueKind);Check(!record.GetProperty("canPublishCurrent").GetBoolean(),"Archive implied current admission.");
        Check(!record.GetProperty("migrationAppliedToOutput").GetBoolean(),"Archive claimed a migration was applied to unchanged output.");
        var retention=record.GetProperty("exportRetention");Check(retention.GetProperty("exactSourceBytes").GetBoolean()&&!retention.GetProperty("runnableCurrentFormat").GetBoolean(),"Archive mislabeled runnable.");Equal(0,retention.GetProperty("excludedRecords").GetInt32());Equal(0,retention.GetProperty("excludedFields").GetInt32());
        Check((await File.ReadAllBytesAsync(files.Destination)).AsSpan().SequenceEqual(bytes),"Archive changed source bytes.");Equal(Hash(bytes),Hash(await File.ReadAllBytesAsync(files.Source)));NoTemps(files);
        var open=await Child(files,"session","inspect","--session",files.Destination);Equal(1,open.ExitCode);
    }
    private static async Task ArchiveMigrationApplied()
    {
        using var files = new Files();
        const string unknown = """{"type":"future_native","id":"original-opaque-id","timestamp":"2024-01-01T00:00:00.000Z","opaque":{"wide":9007199254740993,"huge":1e400,"nil":null}}""";
        var original = Utf8.GetBytes(Header(files, "1") + "\r\n\r\n" + unknown);
        await File.WriteAllBytesAsync(files.Source, original); await File.WriteAllTextAsync(files.Plan, "[\"planned-new-id\"]", Utf8);
        var archived = Published(await Child(files, CopyArgs(files, format: "native-archive-exact")
            .Concat(new[] { "--id-plan", files.Plan }).ToArray()), "native-archive-exact");
        Check(archived.GetProperty("migration").GetProperty("WasMigrated").GetBoolean(), "Inspection no longer records the valid proposed v1 migration.");
        Check(archived.GetProperty("canPublishCurrent").GetBoolean(), "Admitted legacy proposal was obscured.");
        Check(!archived.GetProperty("migrationAppliedToOutput").GetBoolean(), "A proposed migration was mislabeled as an archive output transform.");
        ArchiveInventory(archived, ["original-opaque-id"], null); await ArchiveBytes(files, files.Destination, archived, original);
        var inspected = await Child(files, "session", "copy-inspect", "--source", files.Source, "--id-plan", files.Plan);
        Equal(0, inspected.ExitCode); Equal("", inspected.Error); Equal(JsonValueKind.Null, Record(inspected.Output).GetProperty("migrationAppliedToOutput").ValueKind);
        var migratedPath = files.File("actual-current.jsonl");
        var migrated = Published(await Child(files, "session", "migrate", "--source", files.Source, "--destination", migratedPath, "--id-plan", files.Plan), "current-jsonl");
        Check(migrated.GetProperty("migrationAppliedToOutput").GetBoolean(), "Actual published legacy transformation was not disclosed.");
        Equal(3, migrated.GetProperty("targetVersion").GetInt32());
        Check(migrated.GetProperty("exportRetention").GetProperty("inertUnknownEntryIds").EnumerateArray().Select(value => value.GetString()).SequenceEqual(new[] { "planned-new-id" }), "Current migration did not report its actual output identity.");
        await using (var store = await SessionLogStore.OpenAsync(migratedPath)) Equal("planned-new-id", store.Snapshot.Entries.Single().Id);
        Equal(Hash(original), Hash(await File.ReadAllBytesAsync(files.Source)));

        var ambiguous = Utf8.GetBytes(Header(files, "1") + "\n" + unknown + "\n" +
            "{\"type\":\"compaction\",\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"summary\":\"inert\",\"firstKeptEntryIndex\":99,\"tokensBefore\":1}\n");
        await File.WriteAllBytesAsync(files.Source, ambiguous); await File.WriteAllTextAsync(files.Plan, "[\"planned-opaque\",\"planned-compaction\"]", Utf8);
        var ambiguousPath = files.File("ambiguous-archive.jsonl");
        var blockedArchive = Published(await Child(files, CopyArgs(files, ambiguousPath, "native-archive-exact")
            .Concat(new[] { "--id-plan", files.Plan }).ToArray()), "native-archive-exact");
        Check(MigrationCode(blockedArchive, "UnsafeCompactionReference"), "Unsafe proposed compaction reference was hidden.");
        Check(!blockedArchive.GetProperty("migrationAppliedToOutput").GetBoolean(), "Blocked plan became an archive output transform.");
        ArchiveInventory(blockedArchive, ["original-opaque-id"], null); await ArchiveBytes(files, ambiguousPath, blockedArchive, ambiguous);
        var deniedPath = files.File("ambiguous-current.jsonl");
        var denied = await Child(files, "session", "migrate", "--source", files.Source, "--destination", deniedPath, "--id-plan", files.Plan);
        Equal(1, denied.ExitCode); Equal("", denied.Error); Equal(JsonValueKind.Null, Record(denied.Output).GetProperty("migrationAppliedToOutput").ValueKind);
        Check(!File.Exists(deniedPath), "Blocked current migration produced output."); Equal(Hash(ambiguous), Hash(await File.ReadAllBytesAsync(files.Source))); NoTemps(files);
    }
    private static async Task ArchiveInventoryDisclosures()
    {
        using var files = new Files();
        const string unknown = """{"type":"future_native","id":"original-opaque-id","timestamp":"2024-01-01T00:00:00.000Z","opaque":{"huge":1e400,"nil":null}}""";
        var future = Utf8.GetBytes(Header(files, "99") + "\r\n" + unknown);
        await File.WriteAllBytesAsync(files.Source, future);
        var futurePath = files.File("future-archive.jsonl");
        var futureReport = Published(await Child(files, CopyArgs(files, futurePath, "native-archive-exact")), "native-archive-exact");
        ArchiveInventory(futureReport, ["original-opaque-id"], null); await ArchiveBytes(files, futurePath, futureReport, future);
        Check(!futureReport.GetProperty("canPublishCurrent").GetBoolean(), "Future inventory authorized current admission.");
        var missing = Utf8.GetBytes(Header(files, "1") + "\n" + unknown + "\n" +
            "{\"type\":\"future_native\",\"timestamp\":\"2024-01-01T00:00:00.000Z\"}\n");
        await File.WriteAllBytesAsync(files.Source, missing); await File.WriteAllTextAsync(files.Plan, "[\"planned-first\",\"planned-missing\"]", Utf8);
        var missingPath = files.File("missing-archive.jsonl");
        var missingReport = Published(await Child(files, CopyArgs(files, missingPath, "native-archive-exact")
            .Concat(new[] { "--id-plan", files.Plan }).ToArray()), "native-archive-exact");
        ArchiveInventory(missingReport, ["original-opaque-id"], "ArchiveIdentityInventoryIncomplete"); await ArchiveBytes(files, missingPath, missingReport, missing);
        var malformed = Utf8.GetBytes(Header(files, "99") + "\n" + unknown + "\n{bad}\n");
        var unclassifiable = Utf8.GetBytes(Header(files, "99") + "\n" + unknown + "\n{\"type\":7,\"id\":\"unclassifiable\"}\n");
        var index = 0;
        foreach (var bytes in new[] { malformed, unclassifiable })
        {
            await File.WriteAllBytesAsync(files.Source, bytes); var destination = files.File($"unavailable-{index++}.bin");
            var report = Published(await Child(files, CopyArgs(files, destination, "native-archive-exact")), "native-archive-exact");
            ArchiveInventory(report, [], "ArchiveIdentityInventoryUnavailable"); await ArchiveBytes(files, destination, report, bytes);
        }
    }
    private static void ArchiveInventory(JsonElement report, string[] ids, string? disclosure)
    {
        Check(!report.GetProperty("migrationAppliedToOutput").GetBoolean(), "Archive applied a proposed transformation.");
        Equal(JsonValueKind.Null, report.GetProperty("targetVersion").ValueKind);
        var retention = report.GetProperty("exportRetention");
        Check(retention.GetProperty("exactSourceBytes").GetBoolean() && !retention.GetProperty("runnableCurrentFormat").GetBoolean() &&
            !retention.GetProperty("piReaderInteroperabilityGuaranteed").GetBoolean(), "Archive retention exceeded inert exact-byte contract.");
        Equal(0, retention.GetProperty("excludedRecords").GetInt32()); Equal(0, retention.GetProperty("excludedFields").GetInt32());
        Equal(0, report.GetProperty("dataRetention").GetProperty("omittedRecords").GetInt32()); Equal(0, report.GetProperty("dataRetention").GetProperty("omittedFields").GetInt32());
        Check(retention.GetProperty("inertUnknownEntryIds").EnumerateArray().Select(value => value.GetString()).SequenceEqual(ids), "Archive listed proposed or partially inventoried identities.");
        var inventory = report.GetProperty("diagnostics").EnumerateArray().Select(row => row.GetProperty("code").GetString())
            .Where(code => code is "ArchiveIdentityInventoryIncomplete" or "ArchiveIdentityInventoryUnavailable").ToArray();
        Check(disclosure is null ? inventory.Length == 0 : inventory.Length == 1 && inventory[0] == disclosure, "Archive identity inventory completeness was not disclosed.");
    }
    private static async Task ArchiveBytes(Files files, string destination, JsonElement report, byte[] bytes)
    {
        Check((await File.ReadAllBytesAsync(destination)).AsSpan().SequenceEqual(bytes), "Archive output changed original bytes.");
        Equal(Hash(bytes), Hash(await File.ReadAllBytesAsync(files.Source))); Equal(Hash(bytes), report.GetProperty("sourceSha256").GetString());
        Equal(Hash(bytes), report.GetProperty("outputSha256").GetString()); Equal(bytes.Length, report.GetProperty("outputBytes").GetInt32()); NoTemps(files);
    }
    private static async Task ExactCopyAndResume()
    {
        using var files = new Files(); var script = files.File("turns.json"); var marker = files.File("effect.txt");
        SessionSuccess(await Child(files, "session", "create", "--session", files.Source, "--workspace", files.Root));
        await Script(script, Tool("write", "write-once", new { path = marker, content = "first effect\n" }, "first user"),
            Text("first final", "Successfully wrote"));
        SessionSuccess(await Child(files, "session", "prompt", "--session", files.Source, "--workspace", files.Root,
            "--offline-script", script, "--message", "first user", "--allow-write", marker));
        Equal("first effect\n", await File.ReadAllTextAsync(marker));
        var before = await File.ReadAllBytesAsync(files.Source); var originalHash = Hash(before);
        var attributes = File.GetAttributes(files.Source); File.SetAttributes(files.Source, attributes | FileAttributes.ReadOnly);
        try
        {
            var copied = Published(await Child(files, CopyArgs(files)), "native-exact");
            Equal(originalHash, copied.GetProperty("sourceSha256").GetString()); Equal(originalHash, copied.GetProperty("outputSha256").GetString());
            Equal(before.Length, copied.GetProperty("outputBytes").GetInt32());
            var copiedBytes = await File.ReadAllBytesAsync(files.Destination);
            Check(before.AsSpan().SequenceEqual(copiedBytes), "Exact compiled copy changed source bytes.");
            await using (var reopened = await SessionLogStore.OpenAsync(files.Destination))
                Check(reopened.Snapshot.Entries.Length > 3, "Published copy did not reopen as a committed session.");
            await File.WriteAllTextAsync(marker, "completed effect must not replay\n", Utf8);
            await Script(script, Text("copy resumed", "first user", "first final", "Successfully wrote", "resumed user"));
            var resumed = SessionSuccess(await Child(files, "session", "resume", "--session", files.Destination, "--workspace", files.Root,
                "--offline-script", script, "--message", "resumed user"));
            Equal(0, resumed.GetProperty("actions").GetArrayLength());
            Check(resumed.GetProperty("requests")[0].GetProperty("historyRequirementsSatisfied").GetBoolean(), "Copied durable history did not reach the actual offline request.");
            Equal("completed effect must not replay\n", await File.ReadAllTextAsync(marker));
            var after = await File.ReadAllBytesAsync(files.Destination);
            Check(after.AsSpan().StartsWith(before) && after.Length > before.Length, "Resume did not append after the copied prefix.");
            Equal(originalHash, Hash(await File.ReadAllBytesAsync(files.Source))); NoTemps(files);
        }
        finally { File.SetAttributes(files.Source, attributes); }
    }

    private static async Task FormatsAndForest()
    {
        using var files = new Files(); var original = Current(files); await File.WriteAllBytesAsync(files.Source, original);
        var exact = Published(await Child(files, CopyArgs(files)), "native-exact");
        var exactBytes = await File.ReadAllBytesAsync(files.Destination);
        Check(original.AsSpan().SequenceEqual(exactBytes), "Native export normalized JSONL framing.");
        Equal(0, exact.GetProperty("dataRetention").GetProperty("omittedFields").GetInt32());
        var compatible = files.File("compatible.jsonl");
        var report = Published(await Child(files, CopyArgs(files, compatible, "current-jsonl")), "current-jsonl");
        Check(!report.GetProperty("piReaderInteroperabilityVerified").GetBoolean(), "Authored CLI test claimed Pi reader interoperability.");
        Check(report.GetProperty("diagnostics").EnumerateArray().Any(item => item.GetProperty("code").GetString() == "SemanticCompatibilityUnverified"), "Compatible formatting hid its qualification limit.");
        Equal(0, report.GetProperty("dataRetention").GetProperty("omittedRecords").GetInt32());
        Check(!report.GetRawText().Contains(Private, StringComparison.Ordinal), "Inspection echoed opaque transcript data.");
        await using (var store = await SessionLogStore.OpenAsync(compatible))
        {
            Equal("../../source-id", store.Snapshot.Header.Id); Equal(4, store.Snapshot.Entries.Length);
            var opaque = store.Snapshot.Entries[^1].WireBody.Value.GetProperty("opaque");
            Equal("1e400", opaque.GetProperty("huge").GetRawText()); Equal("9007199254740993", opaque.GetProperty("wide").GetRawText());
            Equal("1.2300e+2", opaque.GetProperty("scaled").GetRawText()); Equal("-0", opaque.GetProperty("negativeZero").GetRawText());
            Equal(JsonValueKind.Null, opaque.GetProperty("nil").ValueKind); Check(!opaque.TryGetProperty("absent", out _), "Missing field became explicit null.");
            Equal("Fake.Type, Native.Assembly", opaque.GetProperty("native").GetProperty("$type").GetString());
            var projector = new SessionContextProjector();
            Equal("left", projector.Project(store.Snapshot.Entries, "../left").Messages[^1].WireBody.Value.GetProperty("content").GetString());
            Equal("right", projector.Project(store.Snapshot.Entries, "right").Messages[^1].WireBody.Value.GetProperty("content").GetString());
            Equal(0, projector.Project(store.Snapshot.Entries, null).Ancestry.Length);
        }
        var treeResult = await Child(files, "session", "tree", "--session", compatible);
        var tree = SessionSuccess(treeResult); Equal(2, tree.GetProperty("nodes").EnumerateArray()
            .Single(node => node.GetProperty("Id").GetString() == "root").GetProperty("children").GetArrayLength());
        Equal(Hash(original), Hash(await File.ReadAllBytesAsync(files.Source))); NoTemps(files);
    }

    private static async Task LegacyReceipts()
    {
        using var files = new Files();
        const string compaction = """{"type":"compaction","timestamp":"2024-01-01T00:00:00.000Z","summary":"summary","firstKeptEntryIndex":1,"tokensBefore":10,"opaque":{"huge":1e400,"nil":null}}""";
        var original = Utf8.GetBytes(Header(files, null) + "\r\n" + LegacyUser() + "\n" + compaction);
        await File.WriteAllBytesAsync(files.Source, original);
        var denied = await Child(files, "session", "migrate", "--source", files.Source, "--destination", files.Destination);
        Equal(1, denied.ExitCode); Equal("", denied.Error);
        var blocked = Record(denied.Output); Equal("blocked", blocked.GetProperty("status").GetString());
        Check(MigrationCode(blocked, "IdPlanRequired"), "V1 generated identifiers without an explicit plan.");
        Check(!File.Exists(files.Destination), "Blocked legacy source was published.");
        await File.WriteAllTextAsync(files.Plan, "[\"../../u\",\"c\"]", Utf8);
        var inspected = await Child(files, "session", "copy-inspect", "--source", files.Source, "--id-plan", files.Plan);
        Equal(0, inspected.ExitCode); Equal("", inspected.Error); Equal("inspected", Record(inspected.Output).GetProperty("status").GetString());
        var migrated = Published(await Child(files, "session", "migrate", "--source", files.Source, "--destination", files.Destination, "--id-plan", files.Plan), "current-jsonl");
        var migration = migrated.GetProperty("migration"); Equal(1L, migrated.GetProperty("sourceVersion").GetInt64());
        Check(migration.GetProperty("VersionDefaulted").GetBoolean() && migration.GetProperty("WasMigrated").GetBoolean(), "Defaulted legacy source version lost its receipt.");
        var receipts = migration.GetProperty("receipts").EnumerateArray().ToArray(); Equal(7, receipts.Length);
        var boundary = receipts.Single(item => item.GetProperty("transform").GetString() == "CompactionBoundary");
        Equal("../../u", boundary.GetProperty("after").GetString());
        var removed = receipts.Single(item => item.GetProperty("transform").GetString() == "LegacyIndexRemoved");
        Equal(1, removed.GetProperty("before").GetInt32()); Check(!removed.GetProperty("AfterPresent").GetBoolean(), "Legacy index deletion was hidden.");
        var header = receipts.Single(item => item.GetProperty("transform").GetString() == "HeaderVersion");
        Check(!header.GetProperty("BeforePresent").GetBoolean(), "Missing version was conflated with null.");
        await using (var store = await SessionLogStore.OpenAsync(files.Destination))
        { Equal("../../u", store.Snapshot.Entries[1].ParentId); Equal("../../u", store.Snapshot.Entries[1].WireBody.Value.GetProperty("firstKeptEntryId").GetString()); }
        var exact = await Child(files, CopyArgs(files, files.File("exact-legacy.jsonl")).Concat(new[] { "--id-plan", files.Plan }).ToArray());
        Equal(1, exact.ExitCode); Check(Record(exact.Output).GetProperty("diagnostics").EnumerateArray().Any(item => item.GetProperty("code").GetString() == "ExactCopyRequiresCurrentVersion"), "Legacy exact copy became runnable current data.");
        Equal(Hash(original), Hash(await File.ReadAllBytesAsync(files.Source)));
        const string hook = """{"type":"message","id":"hook","parentId":null,"timestamp":"2024-01-01T00:00:00.000Z","message":{"role":"hookMessage","timestamp":7,"customType":"extension","content":"custom content","display":false,"details":{"huge":1e400,"nil":null}}}""";
        var v2 = Utf8.GetBytes(Header(files, "2") + "\n" + hook + "\n"); await File.WriteAllBytesAsync(files.Source, v2);
        var output = files.File("v2.jsonl");
        var second = Published(await Child(files, "session", "migrate", "--source", files.Source, "--destination", output), "current-jsonl");
        var hookReceipt = second.GetProperty("migration").GetProperty("receipts").EnumerateArray().Single(item => item.GetProperty("transform").GetString() == "HookRole");
        Equal("hookMessage", hookReceipt.GetProperty("before").GetString()); Equal("custom", hookReceipt.GetProperty("after").GetString());
        await using (var store = await SessionLogStore.OpenAsync(output)) Equal("custom", store.Snapshot.Entries[0].WireBody.Value.GetProperty("message").GetProperty("role").GetString());
        Equal(Hash(v2), Hash(await File.ReadAllBytesAsync(files.Source))); NoTemps(files);
    }

    private static async Task BlockedSources()
    {
        using var files = new Files();
        var future = Utf8.GetBytes(Header(files, "4") + "\n" + User("r", null, Private));
        await File.WriteAllBytesAsync(files.Source, future);
        var inspected = await Child(files, "session", "copy-inspect", "--source", files.Source);
        Equal(1, inspected.ExitCode); Equal("", inspected.Error);
        var report = Record(inspected.Output); Check(MigrationCode(report, "FutureVersion"), "Future version was treated as current.");
        Equal(Hash(future), report.GetProperty("sourceSha256").GetString()); Equal(future.Length, report.GetProperty("capturedBytes").GetInt32());
        Check(!inspected.Output.Contains(Private, StringComparison.Ordinal), "Future inspection leaked raw data.");
        foreach (var bytes in new[]
        {
            future, Utf8.GetBytes(Header(files, "3") + "\n{bad}\n" + User("child", "lost", Private)),
            Utf8.GetBytes(Header(files, "3") + "\n" + User("a", "missing", Private)),
            Utf8.GetBytes(Header(files, "3") + "\n" + User("a", "b", Private) + "\n" + User("b", null, "parent")),
            Utf8.GetBytes(Header(files, "3") + "\n" + User("a", "b", Private) + "\n" + User("b", "a", "cycle")),
            Utf8.GetBytes(Header(files, "3") + "\n" + User("a", null, "duplicate") + "\n" + User("a", null, "duplicate")),
            Utf8.GetBytes(Header(files, "3") + "\n{\"type\":"),
            Utf8.GetBytes(Header(files, "3") + "\n").Concat(new byte[] { 0xc3 }).ToArray()
        })
        {
            await File.WriteAllBytesAsync(files.Source, bytes);
            var denied = await Direct(CopyArgs(files, format: "current-jsonl"));
            Equal(1, denied.ExitCode); Equal("", denied.Error); Equal("blocked", Record(denied.Output).GetProperty("status").GetString());
            Check(!File.Exists(files.Destination), "Malformed source published a runnable descendant prefix.");
            Equal(Hash(bytes), Hash(await File.ReadAllBytesAsync(files.Source))); NoTemps(files);
        }
        await File.WriteAllBytesAsync(files.Source, Array.Empty<byte>());
        var empty = await Direct(["session", "copy-inspect", "--source", files.Source]); Equal(1, empty.ExitCode);
        Check(Record(empty.Output).GetProperty("readerDiagnostics").EnumerateArray().Any(item => item.GetProperty("code").GetString() == "EmptyLog"), "Empty log was admitted as a session.");
        var boundedSource = new byte[8_388_609]; await File.WriteAllBytesAsync(files.Source, boundedSource);
        var budget = await Direct(CopyArgs(files)); Equal(1, budget.ExitCode); Equal("", budget.Error);
        var budgetReport = Record(budget.Output); Equal("blocked", budgetReport.GetProperty("status").GetString());
        Check(!budgetReport.GetProperty("sourceComplete").GetBoolean() && budgetReport.GetProperty("sourceSha256").ValueKind == JsonValueKind.Null,
            "Bounded partial capture advertised a full-source hash.");
        Check(!File.Exists(files.Destination), "Over-budget source was published."); Equal(Hash(boundedSource), Hash(await File.ReadAllBytesAsync(files.Source))); NoTemps(files);
    }

    private static async Task PathsAndArguments()
    {
        using var files = new Files(); var bytes = Current(files); await File.WriteAllBytesAsync(files.Source, bytes);
        Published(await Child(files, CopyArgs(files)), "native-exact");
        var collision = await Child(files, CopyArgs(files)); Failure(collision, "InvalidRequest", 1, "not_attempted");
        var collisionBytes = await File.ReadAllBytesAsync(files.Destination);
        Check(bytes.AsSpan().SequenceEqual(collisionBytes), "Repeated copy overwrote the published destination.");
        Failure(await Child(files, CopyArgs(files, files.Source)), "InvalidRequest", 1, "not_attempted");
        foreach (var arguments in new[]
        {
            new[] { "session", "copy" }, new[] { "session", "copy", "--source", files.Source, "--destination", files.File("missing-format.jsonl") },
            new[] { "session", "copy-inspect", "--source", files.Source, "--source", files.Source },
            new[] { "session", "copy-inspect", "--source", files.Source, "--unknown", Private },
            new[] { "session", "migrate", "--source", files.Source, "--destination", files.File("no.jsonl"), "--format", "current-jsonl" },
            new[] { "session", "copy-inspect", "--source", files.Source, "--id-plan" },
            new[] { "session", "copy", "--source", files.Source, "--destination", files.File("no.jsonl"), "--format", "infer" }
        }) Failure(await Direct(arguments), "InvalidArguments", 2, "not_attempted");
        Failure(await Child(files, "session", "copy-inspect", "--source", "relative.jsonl"), "InvalidPath", 2, "not_attempted");
        foreach (var source in new[] { files.Source + "\0", files.Source + "\ud800", new string('x', 4097), Path.Combine(files.Root, "..", Path.GetFileName(files.Root), "source.jsonl") })
            Failure(await Direct(["session", "copy-inspect", "--source", source]), "InvalidPath", 2, "not_attempted");
        if (OperatingSystem.IsWindows()) Failure(await Direct(CopyArgs(files, files.Source + ":new-stream")), "InvalidPath", 2, "not_attempted");
        var failed = await Child(files, "session", "copy-inspect", "--source", files.File(Private + ".jsonl"));
        Failure(failed, "ReadFailed", 1, "not_attempted"); Check(!failed.Error.Contains(Private, StringComparison.Ordinal), "File exception included an untrusted path.");
        Equal(Hash(bytes), Hash(await File.ReadAllBytesAsync(files.Source))); NoTemps(files);
    }

    private static async Task PlanAdmission()
    {
        using var files = new Files(); var original = Utf8.GetBytes(Header(files, "1") + "\n" + LegacyUser() + "\n");
        await File.WriteAllBytesAsync(files.Source, original);
        foreach (var json in new[] { "{}", "[1]", "[null]", "[\"\"]", "[\"same\",\"s\\u0061me\"]", "[\"\\u0000\"]", "[\"\\ud800\"]", "[[[\"u\"]]]", "[\"u\",]", "[/*comment*/\"u\"]" })
        {
            await File.WriteAllTextAsync(files.Plan, json, Utf8);
            Failure(await Direct(MigrationArgs(files)), "InvalidIdPlan", 2, "not_attempted"); Check(!File.Exists(files.Destination), "Invalid ID plan reached publication.");
        }
        await File.WriteAllBytesAsync(files.Plan, new byte[] { 0xff }); Failure(await Direct(MigrationArgs(files)), "InvalidIdPlan", 2, "not_attempted");
        await File.WriteAllBytesAsync(files.Plan, new byte[] { 0xef, 0xbb, 0xbf }.Concat(Utf8.GetBytes("[\"u\"]")).ToArray());
        Failure(await Direct(MigrationArgs(files)), "InvalidIdPlan", 2, "not_attempted");
        await File.WriteAllTextAsync(files.Plan, JsonSerializer.Serialize(new[] { new string('x', 4097) }), Utf8);
        Failure(await Direct(MigrationArgs(files)), "ResourceLimit", 2, "not_attempted");
        await File.WriteAllTextAsync(files.Plan, JsonSerializer.Serialize(Enumerable.Range(0, 10_001).Select(index => "i-" + index)), Utf8);
        Failure(await Direct(MigrationArgs(files)), "ResourceLimit", 2, "not_attempted");
        var exact = "[\"u\"]".PadRight(1_048_576, ' '); await File.WriteAllTextAsync(files.Plan, exact + " ", Utf8);
        Failure(await Direct(MigrationArgs(files)), "ResourceLimit", 2, "not_attempted");
        await File.WriteAllTextAsync(files.Plan, exact, Utf8); Published(await Direct(MigrationArgs(files)), "current-jsonl");
        var boundary = new string('x', 4096); await File.WriteAllTextAsync(files.Plan, JsonSerializer.Serialize(new[] { boundary }), Utf8);
        Published(await Direct(MigrationArgs(files, files.File("id-boundary.jsonl"))), "current-jsonl");
        var unicode = "../../\u03c0\U0001f642e\u0301"; await File.WriteAllTextAsync(files.Plan, JsonSerializer.Serialize(new[] { unicode }), Utf8);
        var planHash = Hash(await File.ReadAllBytesAsync(files.Plan)); var attributes = File.GetAttributes(files.Plan);
        var unicodePath = files.File("unicode.jsonl"); File.SetAttributes(files.Plan, attributes | FileAttributes.ReadOnly);
        try
        {
            Published(await Child(files, MigrationArgs(files, unicodePath)), "current-jsonl");
            Equal(planHash, Hash(await File.ReadAllBytesAsync(files.Plan)));
        }
        finally { File.SetAttributes(files.Plan, attributes); }
        await using (var reopened = await SessionLogStore.OpenAsync(unicodePath)) Equal(unicode, reopened.Snapshot.Entries[0].Id);
        await File.WriteAllTextAsync(files.Plan, "[]", Utf8);
        var mismatch = await Direct(MigrationArgs(files, files.File("mismatch.jsonl"))); Equal(1, mismatch.ExitCode);
        Check(MigrationCode(Record(mismatch.Output), "IdPlanRequired"), "Empty explicit plan generated legacy IDs.");
        Equal(Hash(original), Hash(await File.ReadAllBytesAsync(files.Source))); NoTemps(files);
    }

    private static async Task CancellationAndCleanup()
    {
        using var files = new Files(); var bytes = Current(files); await File.WriteAllBytesAsync(files.Source, bytes);
        using var pre = new CancellationTokenSource(); pre.Cancel(); var never = new FaultFiles();
        Failure(await Direct(CopyArgs(files), pre.Token, new SessionCopyService(fileSystem: never)), "Canceled", 1, "not_attempted");
        Equal(0, never.Reads); Equal(0, never.Created);
        var entered = Gate(); var closing = Gate(); var release = Gate(); using var canceled = new CancellationTokenSource();
        var readFiles = new FaultFiles { ReadWrap = stream => new FaultStream(stream) { ReadEntered = entered, DisposeEntered = closing, DisposeRelease = release } };
        var run = Direct(CopyArgs(files), canceled.Token, new SessionCopyService(fileSystem: readFiles));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); canceled.Cancel(); await closing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!run.IsCompleted, "Canceled source read returned before owned disposal joined."); release.TrySetResult();
            Failure(await run.WaitAsync(TimeSpan.FromSeconds(5)), "Canceled", 1, "not_attempted"); Equal(0, readFiles.Created);
        }
        finally { canceled.Cancel(); release.TrySetResult(); await run; }
        using var partialCancel = new CancellationTokenSource(); entered = Gate(); closing = Gate(); release = Gate();
        var writeFiles = new FaultFiles { WriteWrap = stream => new FaultStream(stream) { WriteEntered = entered, DisposeEntered = closing, DisposeRelease = release } };
        var partial = Direct(CopyArgs(files), partialCancel.Token, new SessionCopyService(fileSystem: writeFiles));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); partialCancel.Cancel(); await closing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!partial.IsCompleted, "Canceled temporary write returned before cleanup joined."); release.TrySetResult();
            Failure(await partial.WaitAsync(TimeSpan.FromSeconds(5)), "Canceled", 1, "not_attempted"); Check(!File.Exists(files.Destination), "Canceled partial write was published."); NoTemps(files);
        }
        finally { partialCancel.Cancel(); release.TrySetResult(); await partial; }
        await File.WriteAllTextAsync(files.Plan, "[\"u\"]", Utf8); using var planCancel = new CancellationTokenSource(); entered = Gate(); closing = Gate(); release = Gate();
        var planFiles = new FaultFiles { ReadWrap = stream => new FaultStream(stream) { ReadEntered = entered, DisposeEntered = closing, DisposeRelease = release, DisposeFault = true } };
        var plan = Direct(["session", "copy-inspect", "--source", files.Source, "--id-plan", files.Plan], planCancel.Token,
            new SessionCopyService(fileSystem: never), planFiles);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); planCancel.Cancel(); await closing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!plan.IsCompleted, "Canceled plan read abandoned actual cleanup."); release.TrySetResult();
            var result = await plan.WaitAsync(TimeSpan.FromSeconds(5)); Failure(result, "Canceled", 1, "not_attempted");
            Check(Record(result.Error).GetProperty("idPlanCleanupFailed").GetBoolean(), "Cleanup failure masked the plan cancellation cause.");
        }
        finally { planCancel.Cancel(); release.TrySetResult(); await plan; }
        var foreign = new FaultFiles { ReadWrap = stream => new FaultStream(stream) { ForeignCancellation = true } };
        Failure(await Direct(CopyArgs(files), service: new SessionCopyService(fileSystem: foreign)), "ReadFailed", 1, "not_attempted");
        Failure(await Direct(["session", "copy-inspect", "--source", files.Source, "--id-plan", files.Plan], planFiles: foreign), "IdPlanReadFailed", 1, "not_attempted");
        Equal(Hash(bytes), Hash(await File.ReadAllBytesAsync(files.Source))); NoTemps(files);
    }

    private static async Task FaultsAndPublication()
    {
        using var files = new Files(); var bytes = Current(files); await File.WriteAllBytesAsync(files.Source, bytes);
        foreach (var (code, io) in new[]
        {
            ("WriteFailed", new FaultFiles { WriteWrap = stream => new FaultStream(stream) { WriteFault = true } }),
            ("FlushFailed", new FaultFiles { WriteWrap = stream => new FaultStream(stream) { FlushFault = true } })
        })
        {
            var failed = await Direct(CopyArgs(files), service: new SessionCopyService(fileSystem: io)); Failure(failed, code, 1, "not_attempted");
            Check(!failed.Error.Contains(Private, StringComparison.Ordinal), "Private I/O cause leaked into fixed diagnostics."); NoTemps(files);
        }
        var race = new FaultFiles { BeforePublish = async (_, destination) => await File.WriteAllTextAsync(destination, "owned competitor") };
        Failure(await Direct(CopyArgs(files), service: new SessionCopyService(fileSystem: race)), "PublishFailed", 1, "uncertain");
        Equal("owned competitor", await File.ReadAllTextAsync(files.Destination)); NoTemps(files);
        var uncertainPath = files.File("uncertain.jsonl");
        var uncertain = new FaultFiles { AfterPublish = () => throw new IOException(Private) };
        Failure(await Direct(CopyArgs(files, uncertainPath), service: new SessionCopyService(fileSystem: uncertain)), "PublishFailed", 1, "uncertain");
        var uncertainBytes = await File.ReadAllBytesAsync(uncertainPath);
        Check(bytes.AsSpan().SequenceEqual(uncertainBytes), "Uncertain post-move failure lied about possible effects.");
        using var late = new CancellationTokenSource(); var publishedPath = files.File("late.jsonl");
        var lateFiles = new FaultFiles { AfterPublish = () => { late.Cancel(); return ValueTask.CompletedTask; } };
        Published(await Direct(CopyArgs(files, publishedPath), late.Token, new SessionCopyService(fileSystem: lateFiles)), "native-exact");
        var cleanupPath = files.File("cleanup.jsonl"); var badCleanup = new FaultFiles { ReadWrap = stream => new FaultStream(stream) { DisposeFault = true } };
        var cleanup = await Direct(CopyArgs(files, cleanupPath), service: new SessionCopyService(fileSystem: badCleanup));
        Equal(1, cleanup.ExitCode); Equal("", cleanup.Error); Equal("published_with_cleanup_failure", Record(cleanup.Output).GetProperty("status").GetString());
        Equal("published", Record(cleanup.Output).GetProperty("publication").GetString());
        var writerPath = files.File("writer-failure.jsonl"); using var writerCancellation = new CancellationTokenSource();
        using var stdout = new BrokenWriter(writerCancellation); using var stderr = new StringWriter();
        var exit = await SessionCopyCommand.RunAsync(CopyArgs(files, writerPath), stdout, stderr, writerCancellation.Token,
            new SessionCopyService(fileSystem: badCleanup));
        Equal(1, exit); Equal("CommandFailed", Record(stderr.ToString()).GetProperty("code").GetString());
        Equal("published", Record(stderr.ToString()).GetProperty("publication").GetString());
        Equal("CleanupFailed", Record(stderr.ToString()).GetProperty("cleanupFailures")[0].GetString());
        var writerBytes = await File.ReadAllBytesAsync(writerPath);
        Check(bytes.AsSpan().SequenceEqual(writerBytes), "Output delivery failure lost actual publication state.");
        Equal(Hash(bytes), Hash(await File.ReadAllBytesAsync(files.Source))); NoTemps(files);
    }

    private static string[] CopyArgs(Files files, string? destination = null, string format = "native-exact") =>
        ["session", "copy", "--source", files.Source, "--destination", destination ?? files.Destination, "--format", format];
    private static string[] MigrationArgs(Files files, string? destination = null) =>
        ["session", "migrate", "--source", files.Source, "--destination", destination ?? files.Destination, "--id-plan", files.Plan];
    private static string Header(Files files, string? version) => "{\"type\":\"session\",\"id\":\"../../source-id\",\"timestamp\":\"" + Time +
        "\",\"cwd\":" + JsonSerializer.Serialize(files.Root) + (version is null ? "" : ",\"version\":" + version) + "}";
    private static string User(string id, string? parent, string content) => JsonSerializer.Serialize(new
    { type = "message", id, parentId = parent, timestamp = Time, message = new { role = "user", timestamp = 7, content } });
    private static string LegacyUser() => JsonSerializer.Serialize(new
    { type = "message", timestamp = Time, message = new { role = "user", timestamp = 7, content = "legacy" } });
    private static byte[] Current(Files files)
    {
        const string opaque = """{"type":"future_native","id":"opaque","parentId":"right","timestamp":"2024-01-01T00:00:00.000Z","opaque":{"huge":1e400,"wide":9007199254740993,"scaled":1.2300e+2,"negativeZero":-0,"nil":null,"native":{"$type":"Fake.Type, Native.Assembly"},"image":"unopened:/private-copy-fixture-payload","text":"\u03c0\ud83d\ude42"}}""";
        var literalUnicode = opaque.Replace("\\u03c0\\ud83d\\ude42", "\u03c0\U0001f642", StringComparison.Ordinal);
        return Utf8.GetBytes(" \t" + Header(files, "3") + "\r\n\n" + User("root", null, "root") + "\n" + User("../left", "root", "left") +
            "\r\n \t\r\n" + User("right", "root", "right") + "\n" + literalUnicode);
    }
    private static Task Script(string path, params object[] turns) => File.WriteAllTextAsync(path,
        JsonSerializer.Serialize(new { schemaVersion = 1, turns }), Utf8);
    private static object Tool(string name, string call, object arguments, params string[] required)
    {
        var id = "fc-" + call; var json = JsonSerializer.Serialize(arguments);
        return new { requiredInputTexts = required, events = new object[]
        {
            new { type = "response.output_item.added", output_index = 0, item = new { type = "function_call", id, call_id = call, name, arguments = "" } },
            new { type = "response.function_call_arguments.delta", output_index = 0, item_id = id, delta = json[..(json.Length / 2)] },
            new { type = "response.output_item.done", output_index = 0, item = new { type = "function_call", id, call_id = call, name, arguments = json } }, Completed()
        } };
    }
    private static object Text(string text, params string[] required)
    {
        var id = "msg-" + text.Replace(' ', '-');
        return new { requiredInputTexts = required, events = new object[]
        {
            new { type = "response.output_item.added", output_index = 0, item = new { type = "message", id, content = Array.Empty<object>() } },
            new { type = "response.output_text.delta", output_index = 0, item_id = id, delta = text },
            new { type = "response.output_item.done", output_index = 0, item = new { type = "message", id, content = new[] { new { type = "output_text", text } } } }, Completed()
        } };
    }
    private static object Completed() => new { type = "response.completed", response = new
    { status = "completed", output = Array.Empty<object>(), usage = new { input_tokens = 8, output_tokens = 4, total_tokens = 12 } } };
    private sealed record Result(int ExitCode, string Output, string Error);
    private static async Task<Result> Direct(string[] args, CancellationToken token = default, SessionCopyService? service = null, ISessionCopyFileSystem? planFiles = null)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        var exit = await SessionCopyCommand.RunAsync(args, output, error, token, service, planFiles);
        return new(exit, output.ToString(), error.ToString());
    }
    private static async Task<Result> Child(Files files, params string[] arguments)
    {
        var start = new ProcessStartInfo(_host) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = files.Root,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8 };
        start.ArgumentList.Add(_cli); foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start }; using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Check(process.Start(), "Compiled session copy CLI child did not start.");
        var output = Read(process.StandardOutput, deadline.Token); var error = Read(process.StandardError, deadline.Token);
        try { await process.WaitForExitAsync(deadline.Token); return new(process.ExitCode, await output, await error); }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            deadline.Cancel();
            foreach (var task in new[] { output, error }) try { await task; } catch (Exception) { }
        }
    }
    private static async Task<string> Read(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder(); var buffer = new char[1024];
        while (true)
        {
            var count = await reader.ReadAsync(buffer, token); if (count == 0) return result.ToString();
            if (count > 1_048_576 - result.Length) throw new InvalidOperationException("Authored child output exceeds bound.");
            result.Append(buffer, 0, count);
        }
    }
    private static JsonElement Record(string text)
    {
        Check(text.EndsWith('\n') && text.Count(character => character == '\n') == 1, "Command did not emit one LF JSON record.");
        using var document = JsonDocument.Parse(text); return document.RootElement.Clone();
    }
    private static JsonElement Published(Result result, string format)
    {
        Equal(0, result.ExitCode); Equal("", result.Error); var record = Record(result.Output);
        Equal("published", record.GetProperty("status").GetString()); Equal("published", record.GetProperty("publication").GetString());
        Equal(format, record.GetProperty("format").GetString()); Check(record.GetProperty("sourceReadOnly").GetBoolean(), "Copy source policy was obscured.");
        return record;
    }
    private static JsonElement SessionSuccess(Result result)
    { Equal(0, result.ExitCode); Equal("", result.Error); var record = Record(result.Output); Equal("completed", record.GetProperty("status").GetString()); return record; }
    private static void Failure(Result result, string code, int exit, string publication)
    {
        Equal(exit, result.ExitCode); Equal("", result.Output); var record = Record(result.Error);
        Equal(code, record.GetProperty("code").GetString()); Equal(publication, record.GetProperty("publication").GetString());
        Check(!result.Error.Contains(Private, StringComparison.Ordinal), "Fixed diagnostics leaked an I/O cause.");
    }
    private static bool MigrationCode(JsonElement record, string code) => record.GetProperty("migration").GetProperty("diagnostics")
        .EnumerateArray().Any(item => item.GetProperty("code").GetString() == code);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void NoTemps(Files files) => Check(!Directory.EnumerateFiles(files.Root, ".pisharp-copy-*.tmp").Any(), "Owned temporary file survived joined cleanup.");
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), "Expected " + expected + ", observed " + actual + ".");
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Files : IDisposable
    {
        private readonly string _parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        public string Root { get; }
        public string Source => File("source.jsonl"); public string Destination => File("destination.jsonl"); public string Plan => File("ids.json");
        public string File(string name) => Path.Combine(Root, name);
        public Files() { Root = Path.Combine(_parent, "pisharp-copy-cli-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root); }
        public void Dispose()
        {
            if (Path.GetDirectoryName(Root) != _parent || !Path.GetFileName(Root).StartsWith("pisharp-copy-cli-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing unowned recursive test cleanup.");
            Directory.Delete(Root, recursive: true);
        }
    }
    private sealed class FaultFiles : ISessionCopyFileSystem
    {
        public int Reads { get; private set; } public int Created { get; private set; }
        public Func<Stream, Stream>? ReadWrap { get; init; } public Func<Stream, Stream>? WriteWrap { get; init; }
        public Func<string, string, ValueTask>? BeforePublish { get; init; } public Func<ValueTask>? AfterPublish { get; init; }
        public async ValueTask<Stream> OpenReadAsync(string path, CancellationToken token)
        { Reads++; var stream = await SessionCopyService.LocalFileSystem.OpenReadAsync(path, token); return ReadWrap?.Invoke(stream) ?? stream; }
        public async ValueTask<Stream> CreateNewTemporaryAsync(string path)
        { Created++; var stream = await SessionCopyService.LocalFileSystem.CreateNewTemporaryAsync(path); return WriteWrap?.Invoke(stream) ?? stream; }
        public async ValueTask PublishNewAsync(string source, string destination)
        {
            if (BeforePublish is not null) await BeforePublish(source, destination);
            await SessionCopyService.LocalFileSystem.PublishNewAsync(source, destination);
            if (AfterPublish is not null) await AfterPublish();
        }
        public ValueTask DeleteTemporaryAsync(string path) => SessionCopyService.LocalFileSystem.DeleteTemporaryAsync(path);
    }
    private sealed class FaultStream(Stream inner) : Stream
    {
        public bool ForeignCancellation { get; init; } public bool WriteFault { get; init; } public bool FlushFault { get; init; } public bool DisposeFault { get; init; }
        public TaskCompletionSource? ReadEntered { get; init; } public TaskCompletionSource? WriteEntered { get; init; }
        public TaskCompletionSource? DisposeEntered { get; init; } public TaskCompletionSource? DisposeRelease { get; init; }
        public override bool CanRead => inner.CanRead; public override bool CanWrite => inner.CanWrite; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (ForeignCancellation) throw new OperationCanceledException(Private);
            if (ReadEntered is not null) { ReadEntered.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); }
            return await inner.ReadAsync(buffer, cancellationToken);
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        {
            if (WriteFault || WriteEntered is not null) await inner.WriteAsync(bytes[..Math.Min(5, bytes.Length)], cancellationToken);
            if (WriteFault) throw new IOException(Private);
            if (WriteEntered is not null) { WriteEntered.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); }
            await inner.WriteAsync(bytes, cancellationToken);
        }
        public override async Task FlushAsync(CancellationToken cancellationToken)
        { await inner.FlushAsync(cancellationToken); if (FlushFault) throw new IOException(Private); }
        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync(); DisposeEntered?.TrySetResult();
            if (DisposeRelease is not null) await DisposeRelease.Task;
            if (DisposeFault) throw new IOException(Private);
            GC.SuppressFinalize(this);
        }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
    private sealed class BrokenWriter(CancellationTokenSource cancellation) : StringWriter
    {
        public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        { cancellation.Cancel(); throw new OperationCanceledException(Private); }
    }
}
