using System.Security.Cryptography;
using System.Text;
using PiSharp.Sessions.Import;
using PiSharp.Sessions.Serialization;

internal static class SessionArchiveCopyTests
{
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("session archive future formats preserve full bytes without current admission", FutureArchive),
        ("session archive damaged UTF8 and incomplete JSON remain exact inert data", DamagedArchive),
        ("session export reports retained unknown native state without exclusions", UnknownRetention),
        ("session archive never publishes partial input and pre-cancel has no effects", BoundsAndCancellation),
        ("session archive identity receipts use original v1 bytes despite plans missing IDs or blocked compaction", OriginalLegacyIdentities),
        ("session archive unavailable identity inventory never returns a partially parsed list", UnavailableIdentityInventory)
    ];
    private static async Task FutureArchive()
    {
        using var files = new Files(); var bytes=Encoding.UTF8.GetBytes("{\"type\":\"session\",\"version\":99,\"id\":\"../../inert\",\"timestamp\":\"opaque\",\"cwd\":\"unopened:/cwd\",\"future\":null}\r\n{\"type\":\"future\",\"id\":\"../inert\",\"payload\":1e400}");
        await File.WriteAllBytesAsync(files.Source,bytes);var service=new SessionCopyService();
        var blocked=await service.CopyAsync(new(files.Source,files.File("current.jsonl"),SessionCopyFormat.CompatibleCurrentJsonl));Check(!blocked.Published&&!File.Exists(blocked.DestinationPath));
        var result=await service.CopyAsync(new(files.Source,files.File("archive.jsonl"),SessionCopyFormat.NativeArchiveExact));
        Check(result.Status==SessionCopyStatus.Published&&!result.Inspection.CanPublishCurrent&&result.Inspection.SourceVersion==99);
        Check(result.Retention is {ExactSourceBytes:true,RunnableCurrentFormat:false,ExcludedRecords:0,ExcludedFields:0,PiReaderInteroperabilityGuaranteed:false});
        Check(result.Diagnostics.Any(row=>row.Code==SessionCopyDiagnosticCode.ArchiveNotRunnable));await Exact(files,result,bytes);
        Check(result.Retention!.InertUnknownEntryIds.SequenceEqual(new[]{"../inert"}));
        Check(!result.Diagnostics.Any(row=>row.Code is SessionCopyDiagnosticCode.ArchiveIdentityInventoryIncomplete or SessionCopyDiagnosticCode.ArchiveIdentityInventoryUnavailable));
        await service.CopyAsync(new(files.Source,files.File("again.jsonl"),SessionCopyFormat.NativeArchiveExact));Check((await File.ReadAllBytesAsync(files.File("again.jsonl"))).AsSpan().SequenceEqual(bytes));
        await Throws<SessionCopyException>(()=>service.CopyAsync(new(files.Source,result.DestinationPath,SessionCopyFormat.NativeArchiveExact)));await Exact(files,result,bytes);
    }
    private static async Task DamagedArchive()
    {
        using var files=new Files();var service=new SessionCopyService();var index=0;
        foreach(var bytes in new[]{Array.Empty<byte>(),new byte[]{0xc3},Encoding.UTF8.GetBytes("{\"type\":\"session\",\"version\":4}\n{bad}\n{\"type\":" )})
        {
            await File.WriteAllBytesAsync(files.Source,bytes);var result=await service.CopyAsync(new(files.Source,files.File($"archive-{index++}.bin"),SessionCopyFormat.NativeArchiveExact));
            Check(result.Published&&result.Retention is {RunnableCurrentFormat:false,ExactSourceBytes:true});await Exact(files,result,bytes);
        }
    }
    private static async Task UnknownRetention()
    {
        using var files=new Files();var bytes=Encoding.UTF8.GetBytes("{\"type\":\"session\",\"version\":3,\"id\":\"s\",\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"cwd\":\"C:/inert\"}\n{\"type\":\"future_native\",\"id\":\"opaque\",\"parentId\":null,\"timestamp\":\"opaque\",\"state\":{\"$type\":\"Fake.Type, Fake.Assembly\",\"n\":9007199254740993,\"huge\":1e400,\"nil\":null}}\n");await File.WriteAllBytesAsync(files.Source,bytes);
        foreach(var format in new[]{SessionCopyFormat.NativeExact,SessionCopyFormat.CompatibleCurrentJsonl,SessionCopyFormat.NativeArchiveExact})
        {
            var result=await new SessionCopyService().CopyAsync(new(files.Source,files.File(format+".jsonl"),format));Check(result.Published&&result.Retention is {ExcludedRecords:0,ExcludedFields:0,PiReaderInteroperabilityGuaranteed:false});
            Check(result.Retention!.InertUnknownEntryIds.SequenceEqual(new[]{"opaque"}));Check(result.Diagnostics.Any(row=>row.Code==SessionCopyDiagnosticCode.UnknownEntriesRetainedInert));Check(result.Retention.RunnableCurrentFormat==(format!=SessionCopyFormat.NativeArchiveExact));await Exact(files,result,bytes);
        }
    }
    private static async Task BoundsAndCancellation()
    {
        using var files=new Files();var bytes=Encoding.UTF8.GetBytes("0123456789");await File.WriteAllBytesAsync(files.Source,bytes);
        var service=new SessionCopyService(new(new(MaximumInputBytes:8),MaximumOutputBytes:8));var result=await service.CopyAsync(new(files.Source,files.File("partial.bin"),SessionCopyFormat.NativeArchiveExact));Check(!result.Published&&!File.Exists(result.DestinationPath));Check(!result.Inspection.Log.SourceComplete);
        using var cancellation=new CancellationTokenSource();cancellation.Cancel();await Throws<OperationCanceledException>(()=>new SessionCopyService().CopyAsync(new(files.Source,files.File("cancel.bin"),SessionCopyFormat.NativeArchiveExact),cancellation.Token));Check(!File.Exists(files.File("cancel.bin"))&&!Directory.EnumerateFiles(files.Root,".pisharp-copy-*.tmp").Any());Check((await File.ReadAllBytesAsync(files.Source)).AsSpan().SequenceEqual(bytes));
    }
    private const string LegacyHeader = """{"type":"session","version":1,"id":"legacy","timestamp":"2024-01-01T00:00:00.000Z","cwd":"C:/inert","opaque":null}""";
    private const string PhysicalUnknown = """{"type":"future_native","id":"original-opaque-id","timestamp":"2024-01-01T00:00:00.000Z","opaque":{"wide":9007199254740993,"huge":1e400,"nil":null}}""";
    private static async Task OriginalLegacyIdentities()
    {
        using var files = new Files(); var service = new SessionCopyService();
        var original = Encoding.UTF8.GetBytes(LegacyHeader + "\r\n\r\n" + PhysicalUnknown);
        await File.WriteAllBytesAsync(files.Source, original);
        var archive = await service.CopyAsync(new(files.Source, files.File("legacy-archive.jsonl"), SessionCopyFormat.NativeArchiveExact, ["planned-new-id"]));
        Check(archive.Published && archive.Inspection.Migration is { WasMigrated: true, Status: SessionEntryMigrationStatus.Completed });
        Check(archive.Inspection.CurrentRecords.Single(entry => !entry.IsHeader).Id == "planned-new-id");
        Inventory(archive, ["original-opaque-id"], null); await Exact(files, archive, original);
        var current = await service.CopyAsync(new(files.Source, files.File("legacy-current.jsonl"), SessionCopyFormat.CompatibleCurrentJsonl, ["planned-new-id"]));
        Check(current.Published && current.Retention is { RunnableCurrentFormat: true, ExcludedFields: 0, ExcludedRecords: 0 });
        Check(current.Retention!.InertUnknownEntryIds.SequenceEqual(new[] { "planned-new-id" }));
        Check((await File.ReadAllTextAsync(current.DestinationPath)).Contains("\"id\":\"planned-new-id\"", StringComparison.Ordinal));
        Check((await File.ReadAllBytesAsync(files.Source)).AsSpan().SequenceEqual(original));

        var missing = Encoding.UTF8.GetBytes(LegacyHeader + "\n" + PhysicalUnknown + "\n" +
            "{\"type\":\"future_native\",\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"opaque\":null}\n" +
            "{\"type\":\"future_native\",\"id\":17,\"timestamp\":\"2024-01-01T00:00:00.000Z\"}\n");
        await File.WriteAllBytesAsync(files.Source, missing);
        var partial = await service.CopyAsync(new(files.Source, files.File("missing-archive.jsonl"), SessionCopyFormat.NativeArchiveExact,
            ["planned-first", "planned-missing", "planned-nonstring"]));
        Check(partial.Inspection.Migration is { WasMigrated: true });
        Inventory(partial, ["original-opaque-id"], SessionCopyDiagnosticCode.ArchiveIdentityInventoryIncomplete); await Exact(files, partial, missing);

        var ambiguous = Encoding.UTF8.GetBytes(LegacyHeader + "\n" + PhysicalUnknown + "\n" +
            "{\"type\":\"compaction\",\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"summary\":\"inert\",\"firstKeptEntryIndex\":99,\"tokensBefore\":1}\n");
        await File.WriteAllBytesAsync(files.Source, ambiguous);
        var blockedPlan = await service.CopyAsync(new(files.Source, files.File("ambiguous-archive.jsonl"), SessionCopyFormat.NativeArchiveExact,
            ["planned-opaque", "planned-compaction"]));
        Check(blockedPlan.Inspection.Migration is { Status: SessionEntryMigrationStatus.Blocked } &&
            blockedPlan.Inspection.Migration.Diagnostics.Any(row => row.Code == SessionEntryMigrationCode.UnsafeCompactionReference));
        Inventory(blockedPlan, ["original-opaque-id"], null); await Exact(files, blockedPlan, ambiguous);
    }
    private static async Task UnavailableIdentityInventory()
    {
        using var files = new Files(); var service = new SessionCopyService();
        var prefix = LegacyHeader + "\n" + PhysicalUnknown + "\n";
        var invalidRows = new[]
        {
            "{bad}", "{\"type\":", "null", "[]", "{\"id\":\"unclassifiable\"}", "{\"type\":7,\"id\":\"unclassifiable\"}",
            "{\"type\":\"future_native\",\"id\":\"duplicate-a\",\"id\":\"duplicate-b\"}",
            "{\"type\":\"future_native\",\"id\":\"nested-duplicate\",\"opaque\":{\"secret\":1,\"secret\":2}}",
            "{\"type\":\"future_native\",\"id\":\"deep\",\"opaque\":" + new string('[', 40) + "0" + new string(']', 40) + "}"
        };
        var inputs = invalidRows.Select(row => Encoding.UTF8.GetBytes(prefix + row + "\n")).Concat(new[]
        {
            Encoding.UTF8.GetBytes(prefix).Concat(new byte[] { 0xc3 }).ToArray(), Array.Empty<byte>(), Encoding.UTF8.GetBytes(" \t\r\n\n")
        }).ToArray();
        for (var index = 0; index < inputs.Length; index++)
        {
            var bytes = inputs[index]; await File.WriteAllBytesAsync(files.Source, bytes);
            var result = await service.CopyAsync(new(files.Source, files.File($"unavailable-{index}.bin"), SessionCopyFormat.NativeArchiveExact));
            Inventory(result, [], SessionCopyDiagnosticCode.ArchiveIdentityInventoryUnavailable); await Exact(files, result, bytes);
            Check(!result.Diagnostics.Any(row => row.Code == SessionCopyDiagnosticCode.UnknownEntriesRetainedInert));
        }
    }
    private static void Inventory(SessionCopyResult result, string[] expectedIds, SessionCopyDiagnosticCode? disclosure)
    {
        Check(result.Published && result.Retention is { ExactSourceBytes: true, RunnableCurrentFormat: false, ExcludedRecords: 0, ExcludedFields: 0, PiReaderInteroperabilityGuaranteed: false });
        Check(result.Retention!.InertUnknownEntryIds.SequenceEqual(expectedIds));
        var inventory = result.Diagnostics.Where(row => row.Code is SessionCopyDiagnosticCode.ArchiveIdentityInventoryIncomplete or SessionCopyDiagnosticCode.ArchiveIdentityInventoryUnavailable).ToArray();
        Check(disclosure is null ? inventory.Length == 0 : inventory.Length == 1 && inventory[0].Code == disclosure);
        Check(result.OmittedFields == 0 && result.OmittedRecords == 0);
    }
    private static async Task Exact(Files files,SessionCopyResult result,byte[] bytes){Check((await File.ReadAllBytesAsync(result.DestinationPath)).AsSpan().SequenceEqual(bytes));Check((await File.ReadAllBytesAsync(files.Source)).AsSpan().SequenceEqual(bytes));var hash=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();Check(result.OutputSha256==hash&&result.Inspection.SourceSha256==hash&&result.OutputBytes==bytes.Length);Check(result.Inspection.Log.SourceComplete&&result.Inspection.OriginalBytes.AsSpan().SequenceEqual(bytes));Check(!Directory.EnumerateFiles(files.Root,".pisharp-copy-*.tmp").Any());}
    private static async Task Throws<T>(Func<Task> action)where T:Exception{try{await action();}catch(T){return;}throw new InvalidOperationException("Expected failure missing.");}
    private static void Check(bool value){if(!value)throw new InvalidOperationException("Archive retention contract failed.");}
    private sealed class Files:IDisposable
    {
        private readonly string parent=Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        public string Root{get;}public Files(){Root=Path.Combine(parent,"pisharp-archive-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Root);}public string Source=>File("source.jsonl");public string File(string name)=>Path.Combine(Root,name);
        public void Dispose(){if(Path.GetDirectoryName(Root)!=parent||!Path.GetFileName(Root).StartsWith("pisharp-archive-",StringComparison.Ordinal))throw new InvalidOperationException("Unowned test cleanup rejected.");Directory.Delete(Root,recursive:true);}
    }
}
