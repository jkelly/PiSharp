using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Import;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;
using PiSharp.Sessions.Tree;


/// <summary>Actual production copy driver and complete unchanged-source reader comparisons. Root owns runtime.</summary>
internal static class SessionNativeCopyReaderReferenceTests
{
    private const string AuthoredInputs = """
{"schemaVersion":1,"fixtureId":"session-copy-reader","sourceSha":"d86654abb8862e201933517d6f1fce9f88dd117f","kind":"authored-original-source-inputs-for-actual-native-copy-service","nativeExecutionClaimed":false,"clock":"Authored ISO/nested Unix values; no Date or Date.now override","idMechanism":"Stable caller-supplied v1 ID plan for actual native migration; unchanged Pi opens already-current native exports with no RNG or clock shim","cases":[{"caseId":"v3-exact-framing","purpose":"CRLF blanks valid unterminated final record; Unicode combining supplementary and decoded NUL","nativeJsonl":" \t{\"type\":\"session\",\"version\":3,\"id\":\"copy-framing\",\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"cwd\":\"C:/pisharp-authored-session-workspace\"}\r\n\r\n{\"type\":\"message\",\"id\":\"unicode\",\"parentId\":null,\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"message\":{\"role\":\"user\",\"content\":\"\u03c0\ud83d\ude42e\u0301\\r\\nNUL:\\u0000\",\"timestamp\":7}}","selections":["unicode",null]},{"caseId":"v3-full-forest","purpose":"Physical entries; timestamp-sorted siblings; detached root; global cleared/missing labels; selected/null leaf","nativeJsonl":"{\"type\":\"session\",\"version\":3,\"id\":\"copy-forest\",\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"cwd\":\"C:/pisharp-authored-session-workspace\"}\n{\"type\":\"message\",\"id\":\"root\",\"parentId\":null,\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"message\":{\"role\":\"user\",\"content\":\"root\",\"timestamp\":7}}\n{\"type\":\"thinking_level_change\",\"id\":\"left-setting\",\"parentId\":\"root\",\"timestamp\":\"2024-01-01T00:00:02.000Z\",\"thinkingLevel\":\"low\"}\n{\"type\":\"message\",\"id\":\"left\",\"parentId\":\"left-setting\",\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"message\":{\"role\":\"user\",\"content\":\"left only\",\"timestamp\":7}}\n{\"type\":\"label\",\"id\":\"left-label\",\"parentId\":\"left\",\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"targetId\":\"left\",\"label\":\"left bookmark\"}\n{\"type\":\"thinking_level_change\",\"id\":\"right-setting\",\"parentId\":\"root\",\"timestamp\":\"2024-01-01T00:00:01.000Z\",\"thinkingLevel\":\"high\"}\n{\"type\":\"message\",\"id\":\"right\",\"parentId\":\"right-setting\",\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"message\":{\"role\":\"user\",\"content\":\"right only\",\"timestamp\":7}}\n{\"type\":\"label\",\"id\":\"right-label\",\"parentId\":\"right\",\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"targetId\":\"right\",\"label\":\"right bookmark\"}\n{\"type\":\"label\",\"id\":\"clear-left\",\"parentId\":\"right-label\",\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"targetId\":\"left\"}\n{\"type\":\"label\",\"id\":\"null-clear-left\",\"parentId\":\"clear-left\",\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"targetId\":\"left\",\"label\":null}\n{\"type\":\"message\",\"id\":\"detached\",\"parentId\":null,\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"message\":{\"role\":\"user\",\"content\":\"detached root\",\"timestamp\":7}}\n","selections":["left-label","null-clear-left","detached",null]},{"caseId":"v3-opaque-numbers","purpose":"Unknown native-looking data and raw numeric lexemes; null versus absent custom details","nativeJsonl":"{\"type\":\"session\",\"version\":3,\"id\":\"copy-opaque\",\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"cwd\":\"C:/pisharp-authored-session-workspace\"}\n{\"type\":\"message\",\"id\":\"root\",\"parentId\":null,\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"message\":{\"role\":\"user\",\"content\":\"root\",\"timestamp\":7}}\n{\"type\":\"future_native\",\"id\":\"opaque\",\"parentId\":\"root\",\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"opaque\":{\"wide\":9007199254740993,\"scaled\":1.2300e+2,\"huge\":1e400,\"negativeZero\":-0,\"nil\":null,\"native\":{\"$type\":\"Fake.Type, Native.Assembly\"},\"path\":\"unopened:/inert/image\",\"ordered\":[\"b\",\"a\"]}}\n{\"type\":\"custom_message\",\"id\":\"missing-details\",\"parentId\":\"opaque\",\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"customType\":\"copy-fixture\",\"content\":\"hidden custom\",\"display\":false}\n{\"type\":\"custom_message\",\"id\":\"null-details\",\"parentId\":\"missing-details\",\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"customType\":\"copy-fixture\",\"content\":\"hidden custom\",\"display\":false,\"details\":null}\n","selections":["opaque","null-details",null]},{"caseId":"v3-stored-media","purpose":"Stored image/reasoning/signature/namespace/usage fields remain inert with complete context/LLM arrays","nativeJsonl":"{\"type\":\"session\",\"version\":3,\"id\":\"copy-media\",\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"cwd\":\"C:/pisharp-authored-session-workspace\"}\n{\"type\":\"message\",\"id\":\"system\",\"parentId\":null,\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"message\":{\"role\":\"system\",\"content\":\"Authored stored prompt.\",\"timestamp\":1}}\n{\"type\":\"message\",\"id\":\"image-user\",\"parentId\":\"system\",\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"image metadata only\"},{\"type\":\"image\",\"data\":\"AQID\",\"mimeType\":\"image/png\",\"opaque\":null}],\"timestamp\":7}}\n{\"type\":\"message\",\"id\":\"assistant\",\"parentId\":\"image-user\",\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"message\":{\"role\":\"assistant\",\"api\":\"anthropic-messages\",\"provider\":\"anthropic\",\"model\":\"authored-model\",\"content\":[{\"type\":\"thinking\",\"thinking\":\"stored reasoning\",\"thinkingSignature\":\"opaque+signature\"},{\"type\":\"text\",\"text\":\"stored answer\",\"textSignature\":\"opaque-text-signature\"},{\"type\":\"toolCall\",\"id\":\"call-1\",\"name\":\"read\",\"namespace\":\"files\",\"arguments\":{\"path\":\"unopened:/fixture\"}}],\"usage\":{\"input\":2,\"output\":3,\"cacheRead\":0,\"cacheWrite\":0,\"totalTokens\":5,\"cost\":{\"input\":0.1,\"output\":0.2,\"cacheRead\":0,\"cacheWrite\":0,\"total\":0.3}},\"stopReason\":\"toolUse\",\"timestamp\":8,\"opaque\":{\"nil\":null}}}\n{\"type\":\"message\",\"id\":\"tool\",\"parentId\":\"assistant\",\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"message\":{\"role\":\"toolResult\",\"toolCallId\":\"call-1\",\"toolName\":\"read\",\"content\":[{\"type\":\"text\",\"text\":\"authored stored tool result\"}],\"details\":null,\"isError\":false,\"timestamp\":9}}\n","selections":["tool",null]},{"caseId":"v1-copy-compaction","purpose":"Source missing-version ID/parent/compaction migration versus explicitly authored compatible current copy","nativeJsonl":"{\"type\":\"session\",\"version\":3,\"id\":\"copy-legacy-v1\",\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"cwd\":\"C:/pisharp-authored-session-workspace\"}\n{\"type\":\"message\",\"id\":\"00110001\",\"parentId\":null,\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"message\":{\"role\":\"user\",\"content\":\"retained legacy user\",\"timestamp\":7}}\n{\"type\":\"message\",\"id\":\"00110002\",\"parentId\":\"00110001\",\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"message\":{\"role\":\"user\",\"content\":\"second legacy user\",\"timestamp\":7}}\n{\"type\":\"compaction\",\"id\":\"00110003\",\"parentId\":\"00110002\",\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"summary\":\"authored legacy summary\",\"firstKeptEntryId\":\"00110001\",\"tokensBefore\":10,\"details\":null}\n{\"type\":\"future_native\",\"id\":\"00110004\",\"parentId\":\"00110003\",\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"opaque\":{\"wide\":9007199254740993,\"huge\":1e400,\"negativeZero\":-0,\"nil\":null}}\n","legacyJsonl":"{\"type\":\"session\",\"id\":\"copy-legacy-v1\",\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"cwd\":\"C:/pisharp-authored-session-workspace\"}\n{\"type\":\"message\",\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"message\":{\"role\":\"user\",\"content\":\"retained legacy user\",\"timestamp\":7}}\n{\"type\":\"message\",\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"message\":{\"role\":\"user\",\"content\":\"second legacy user\",\"timestamp\":7}}\n{\"type\":\"compaction\",\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"summary\":\"authored legacy summary\",\"firstKeptEntryIndex\":1,\"tokensBefore\":10,\"details\":null}\n{\"type\":\"future_native\",\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"opaque\":{\"wide\":9007199254740993,\"huge\":1e400,\"negativeZero\":-0,\"nil\":null}}\n","v1EntryIds":["00110001","00110002","00110003","00110004"],"selections":["00110004",null]},{"caseId":"v2-copy-hook","purpose":"Source hookMessage conversion versus explicitly authored compatible current copy; explicit null details","nativeJsonl":"{\"type\":\"session\",\"version\":3,\"id\":\"copy-legacy-v2\",\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"cwd\":\"C:/pisharp-authored-session-workspace\"}\n{\"type\":\"message\",\"id\":\"hook\",\"parentId\":null,\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"message\":{\"role\":\"custom\",\"customType\":\"legacy-hook\",\"content\":\"legacy custom text\",\"display\":false,\"details\":null,\"timestamp\":7}}\n","legacyJsonl":"{\"type\":\"session\",\"version\":2,\"id\":\"copy-legacy-v2\",\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"cwd\":\"C:/pisharp-authored-session-workspace\"}\n{\"type\":\"message\",\"id\":\"hook\",\"parentId\":null,\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"message\":{\"role\":\"hookMessage\",\"customType\":\"legacy-hook\",\"content\":\"legacy custom text\",\"display\":false,\"details\":null,\"timestamp\":7}}\n","selections":["hook",null]},{"caseId":"v1-explicit-copy-compaction","purpose":"Actual explicit-version v1 native migration with the same stable caller ID plan","nativeJsonl":"{\"type\":\"session\",\"version\":3,\"id\":\"copy-legacy-v1\",\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"cwd\":\"C:/pisharp-authored-session-workspace\"}\n{\"type\":\"message\",\"id\":\"00110001\",\"parentId\":null,\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"message\":{\"role\":\"user\",\"content\":\"retained legacy user\",\"timestamp\":7}}\n{\"type\":\"message\",\"id\":\"00110002\",\"parentId\":\"00110001\",\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"message\":{\"role\":\"user\",\"content\":\"second legacy user\",\"timestamp\":7}}\n{\"type\":\"compaction\",\"id\":\"00110003\",\"parentId\":\"00110002\",\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"summary\":\"authored legacy summary\",\"firstKeptEntryId\":\"00110001\",\"tokensBefore\":10,\"details\":null}\n{\"type\":\"future_native\",\"id\":\"00110004\",\"parentId\":\"00110003\",\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"opaque\":{\"wide\":9007199254740993,\"huge\":1e400,\"negativeZero\":-0,\"nil\":null}}\n","legacyJsonl":"{\"type\":\"session\",\"version\":1,\"id\":\"copy-legacy-v1\",\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"cwd\":\"C:/pisharp-authored-session-workspace\"}\n{\"type\":\"message\",\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"message\":{\"role\":\"user\",\"content\":\"retained legacy user\",\"timestamp\":7}}\n{\"type\":\"message\",\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"message\":{\"role\":\"user\",\"content\":\"second legacy user\",\"timestamp\":7}}\n{\"type\":\"compaction\",\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"summary\":\"authored legacy summary\",\"firstKeptEntryIndex\":1,\"tokensBefore\":10,\"details\":null}\n{\"type\":\"future_native\",\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"opaque\":{\"wide\":9007199254740993,\"huge\":1e400,\"negativeZero\":-0,\"nil\":null}}\n","v1EntryIds":["00110001","00110002","00110003","00110004"],"selections":["00110004",null]}],"historicalAuthoredIdMechanism":"Only v1 source migration uses explicit eight-hex crypto.randomUUID values; no other RNG call allowed"}
""";
    private const string InputHash = "f3de7bfc5f6fd4149faf8ddc73ad5910d2d038541e9180e9a2e668bfebff2003";
    private const string PublicRevision = "d86654abb8862e201933517d6f1fce9f88dd117f";
    private const int MaximumFileBytes = 2_097_152;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<int> CaptureNativeExportsAsync(string outputRoot, string executionCandidate)
    {
        try
        {
            var revision = executionCandidate;
            Require(revision.Length == 40 && revision.All(Uri.IsHexDigit), "Invalid explicit execution candidate.");
            ValidateAbsolute(outputRoot); NoLinks(outputRoot);
            Require(!File.Exists(outputRoot) && !Directory.Exists(outputRoot), "Output must be a new directory.");
            var repository = FindRepository();
            var artifactRoot = Path.Combine(repository, "artifacts", "session-native-copy-reader");
            Require(Path.GetDirectoryName(outputRoot) == artifactRoot && Directory.Exists(artifactRoot),
                "Output must be one fresh direct child of the explicit repository artifact root.");
            var inputBytes = Utf8.GetBytes(AuthoredInputs);
            using var input = JsonDocument.Parse(inputBytes, new JsonDocumentOptions { MaxDepth = 64 });
            Require(input.RootElement.GetProperty("sourceSha").GetString() == PublicRevision, "Public revision differs.");
            Require(input.RootElement.GetProperty("cases").GetArrayLength() == 7, "Seven complete input scenarios required.");
            var sourcePins = SourcePins(repository);
            var assemblyPins = AssemblyPins();
            Directory.CreateDirectory(outputRoot);
            await WriteNewAsync(Path.Combine(outputRoot, "authored-inputs.json"), inputBytes).ConfigureAwait(false);
            var sources = new List<object>(); var exports = new List<object>();
            foreach (var test in input.RootElement.GetProperty("cases").EnumerateArray())
            {
                var caseId = test.GetProperty("caseId").GetString()!;
                Require(caseId.Length < 64 && caseId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'), "Invalid fixture identity.");
                var legacy = test.TryGetProperty("legacyJsonl", out var legacyJsonl);
                var sourceText = legacy ? legacyJsonl.GetString()! : test.GetProperty("nativeJsonl").GetString()!;
                var bytes = Utf8.GetBytes(sourceText); var sourceName = caseId + ".source.jsonl";
                var sourcePath = Path.Combine(outputRoot, sourceName);
                await WriteNewAsync(sourcePath, bytes).ConfigureAwait(false);
                var ids = test.TryGetProperty("v1EntryIds", out var idArray)
                    ? idArray.EnumerateArray().Select(id => id.GetString()!).ToImmutableArray() : ImmutableArray<string>.Empty;
                var formats = caseId switch
                {
                    "v3-exact-framing" => new[] { SessionCopyFormat.NativeExact },
                    "v3-opaque-numbers" => [SessionCopyFormat.NativeExact, SessionCopyFormat.CompatibleCurrentJsonl],
                    _ => [SessionCopyFormat.CompatibleCurrentJsonl]
                };
                var originalHash = Hash(bytes);
                foreach (var format in formats)
                {
                    var exportId = caseId + (format == SessionCopyFormat.NativeExact ? "-exact" : "-compatible");
                    var exportName = exportId + ".export.jsonl"; var exportPath = Path.Combine(outputRoot, exportName);
                    var files = new ObservingFiles(); var service = new SessionCopyService(fileSystem: files);
                    var receipt = await service.CopyAsync(new(sourcePath, exportPath, format, ids)).ConfigureAwait(false);
                    Require(receipt.Status == SessionCopyStatus.Published && receipt.OmittedFields == 0 && receipt.OmittedRecords == 0,
                        "Native copy did not publish every field/record.");
                    Require(files.Flushes == 1 && files.PublishCalls == 1 && files.OpenReadCalls == 1,
                        "Actual flushed temporary/source/publication lifecycle differs.");
                    var exported = await ReadBoundedAsync(exportPath).ConfigureAwait(false);
                    Require(receipt.OutputBytes == exported.Length && receipt.OutputSha256 == Hash(exported), "Publication receipt differs.");
                    if (format == SessionCopyFormat.NativeExact) Require(bytes.AsSpan().SequenceEqual(exported), "Exact export changed bytes.");
                    var log = await new SessionLogReader().ReadFileAsync(exportPath).ConfigureAwait(false);
                    Require(log.SourceComplete && log.Status == SessionLogReadStatus.Complete && log.Header is not null,
                        "Published export did not reopen through strict native reader.");
                    Require(log.OriginalBytes.AsSpan().SequenceEqual(exported), "Reader did not retain exact bytes.");
                    SessionLogStoreSnapshot storeSnapshot;
                    await using (var store = await SessionLogStore.OpenAsync(exportPath).ConfigureAwait(false))
                    {
                        storeSnapshot = store.Snapshot;
                        Require(!store.IsPoisoned && storeSnapshot.CommittedByteLength == exported.Length,
                            "Actual writer admission/reopen differs.");
                        Require(storeSnapshot.Entries.Length == log.ValidatedPrefix.Length - 1, "Reopened entry prefix differs.");
                    }
                    var afterStore = await ReadBoundedAsync(exportPath).ConfigureAwait(false);
                    Require(exported.AsSpan().SequenceEqual(afterStore), "Store reopen changed the export.");
                    var entries = storeSnapshot.Entries; var tree = new SessionTreeQueries().Build(entries);
                    var leaves = new List<string?> { storeSnapshot.LeafId };
                    leaves.AddRange(test.GetProperty("selections").EnumerateArray().Select(leaf => leaf.ValueKind == JsonValueKind.Null ? null : leaf.GetString()));
                    leaves.AddRange(entries.Select(entry => entry.Id)); leaves.AddRange(tree.StructuralLeafIds); leaves.Add(null); leaves = leaves.Distinct(StringComparer.Ordinal).ToList();
                    Require(leaves.Count <= 32, "Fixture selection budget exceeded.");
                    var initial = NativeSnapshot(View(storeSnapshot.Header, entries, tree, storeSnapshot.LeafId));
                    var selections = leaves.Select(leaf => new { leafId = leaf,
                        observed = NativeSnapshot(View(storeSnapshot.Header, entries, tree, leaf)) }).ToArray();
                    var repeatedName = exportId + ".repeat.jsonl";
                    var repeated = await new SessionCopyService().CopyAsync(new(sourcePath, Path.Combine(outputRoot, repeatedName), format, ids)).ConfigureAwait(false);
                    var repeatedBytes = await ReadBoundedAsync(Path.Combine(outputRoot, repeatedName)).ConfigureAwait(false);
                    Require(repeated.Status == SessionCopyStatus.Published && repeatedBytes.AsSpan().SequenceEqual(exported),
                        "Repeated actual import with the same caller ID plan multiplied or changed records.");
                    var observationName = exportId + ".native.json";
                    await WriteJsonNewAsync(Path.Combine(outputRoot, observationName), new
                    {
                        schemaVersion = 1, exportId, initial, selections,
                        reader = new { sourceComplete = log.SourceComplete, status = log.Status.ToString(),
                            original = ByteView(log.OriginalBytes.ToArray()), validatedPrefixByteLength = log.ValidatedPrefixByteLength,
                            diagnostics = log.Diagnostics.Select(d => new { code = d.Code.ToString(), d.LineNumber, d.ByteOffset, d.ByteLength,
                                codecFailure = d.CodecFailure?.ToString() }).ToArray() },
                        store = new { storeSnapshot.LeafId, storeSnapshot.CommittedByteLength, storeSnapshot.Sequence,
                            header = storeSnapshot.Header.WireBody.Value, entries = Bodies(storeSnapshot.Entries),
                            indexedIds = storeSnapshot.ById.Keys.Order(StringComparer.Ordinal).ToArray(),
                            bytesUnchangedAfterDispose = true, appendInvoked = false },
                        receipt = Receipt(receipt), repeatedReceipt = Receipt(repeated), repeatedBytes = ByteView(repeatedBytes),
                        guards = new { originalSourceUnchanged = true, exactSourceBytesRetained = true,
                            actualTemporaryFlushAwaited = true, actualTemporaryDisposeBeforePublish = true,
                            noDirectoryEntryCrashDurabilityClaim = true, fullForestAdmitted = true, collisionRejected = true,
                            precancellationHadNoIo = true, zeroTemporaryFilesRemain = true }
                    }).ConfigureAwait(false);
                    var beforeCollisionCalls = files.OpenReadCalls;
                    try { await service.CopyAsync(new(sourcePath, exportPath, format, ids)).ConfigureAwait(false); throw new InvalidOperationException("Collision admitted."); }
                    catch (SessionCopyException error) when (error.Failure == SessionCopyFailure.InvalidRequest) { }
                    Require(files.OpenReadCalls == beforeCollisionCalls, "Collision had I/O.");
                    var afterCollision = await ReadBoundedAsync(exportPath).ConfigureAwait(false);
                    Require(exported.AsSpan().SequenceEqual(afterCollision), "Collision changed published bytes.");
                    using var canceled = new CancellationTokenSource(); canceled.Cancel();
                    var canceledName = exportId + ".canceled.jsonl";
                    try { await service.CopyAsync(new(sourcePath, Path.Combine(outputRoot, canceledName), format, ids), canceled.Token).ConfigureAwait(false);
                        throw new InvalidOperationException("Precanceled copy admitted."); }
                    catch (OperationCanceledException error) when (error.CancellationToken == canceled.Token) { }
                    Require(files.OpenReadCalls == beforeCollisionCalls && !File.Exists(Path.Combine(outputRoot, canceledName)), "Precanceled copy had effects.");
                    var afterSource = await ReadBoundedAsync(sourcePath).ConfigureAwait(false);
                    Require(bytes.AsSpan().SequenceEqual(afterSource) && Hash(afterSource) == originalHash, "Original source changed.");
                    Require(Directory.GetFiles(outputRoot, ".pisharp-copy-*.tmp").Length == 0, "Owned temporary remains.");
                    exports.Add(new { exportId, caseId, format = format.ToString(), source = Pin(outputRoot, sourceName),
                        exported = Pin(outputRoot, exportName), native = Pin(outputRoot, observationName), repeated = Pin(outputRoot, repeatedName), v1EntryIds = ids,
                        selections = leaves.ToArray(), nativePublication = receipt.Status.ToString(), sourceVersion = receipt.Inspection.SourceVersion,
                        migrationTransforms = receipt.Inspection.Migration!.Receipts.Select(r => r.Transform.ToString()).ToArray() });
                }
                sources.Add(new { caseId, original = Pin(outputRoot, sourceName), originalByteView = ByteView(bytes),
                    sourceVersion = legacy ? (caseId.StartsWith("v1-", StringComparison.Ordinal) ? 1 : 2) : 3,
                    sourceOrigin = "Frozen authored input written to a real file; actual native service produced each export" });
            }
            foreach (var pin in sourcePins) Require(Hash(await ReadBoundedAsync(Path.Combine(repository, pin.Path)).ConfigureAwait(false)) == pin.Sha256,
                "Producer/native source changed during generation.");
            Require(AssemblyPins().SequenceEqual(assemblyPins), "Executed assembly bytes changed during generation.");
            await WriteJsonNewAsync(Path.Combine(outputRoot, "manifest.json"), new
            {
                schemaVersion = 1, kind = "actual-native-session-copy-exports", publicSourceSha = PublicRevision, executionCandidate = revision.ToLowerInvariant(),
                nativeRevision = revision.ToLowerInvariant(), nativeRevisionAuthority = "Caller-declared exact clean execution candidate, independently bound by actual loaded assembly hashes; this driver does not run Git",
                priorAuthoredInputSha256 = InputHash, authoredInputs = Pin(outputRoot, "authored-inputs.json"), producerSourcePins = sourcePins,
                executedAssemblies = assemblyPins,
                runtime = new { framework = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
                    architecture = RuntimeInformation.ProcessArchitecture.ToString() },
                sources, exports, checks = new { actualNativeCopyExecuted = true, actualReaderAndStoreReopen = true,
                    actualNativeTreeAndContextExecuted = true, sourceBytesUnchanged = true, noOverwriteAndPrecancellation = true,
                    noProviderToolImageOrExtensionExecution = true, noInteropOrPhaseAcceptanceClaim = true }
            }).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(new { status = "native exports generated", sourceCount = sources.Count,
                exportCount = exports.Count, manifestSha256 = Hash(await ReadBoundedAsync(Path.Combine(outputRoot, "manifest.json")).ConfigureAwait(false)),
                nativeInteropClaimed = false, phaseClosureClaimed = false }, JsonOptions));
            return 0;
        }
        catch (Exception) { Console.Error.WriteLine("Native session copy fixture generation failed; retained partial bundle is not admitted."); return 1; }
    }


    private const string Family = "fixtures/pi-v0.99.1/session-native-copy-reader";
    private const string ManifestSha256 = "b172f07b3df3d741a7faa616ca1ac2325e3b9195e59874fbba30bf138f33e707";
    private const int EvidenceBytes = 32 * 1024 * 1024;
    private static readonly string[] CaseIds =
    ["v3-exact-framing", "v3-full-forest", "v3-opaque-numbers", "v3-stored-media",
        "v1-copy-compaction", "v2-copy-hook", "v1-explicit-copy-compaction"];

    public static IEnumerable<(string Name, Func<Task> Run)> Cases() => CaseIds.Select(caseId =>
        ("session-native-copy-reader complete actual export records branches and loss disclosure " + caseId,
            (Func<Task>)(() => CompareCaseAsync(caseId))));

    private static async Task CompareCaseAsync(string caseId)
    {
        var repository = FindRepository();
        var manifestBytes = await ReadEvidenceAsync(Path.Combine(repository, Family, "manifest.json")).ConfigureAwait(false);
        Require(Hash(manifestBytes) == ManifestSha256, "Frozen actual-source fixture manifest changed.");
        using var manifest = JsonDocument.Parse(manifestBytes);
        var capturePin = manifest.RootElement.GetProperty("capture");
        var captureBytes = await ReadEvidenceAsync(Path.Combine(repository, Family, capturePin.GetProperty("path").GetString()!)).ConfigureAwait(false);
        Require(captureBytes.Length == capturePin.GetProperty("bytes").GetInt32() &&
            Hash(captureBytes) == capturePin.GetProperty("sha256").GetString(), "Capture receipt hash/bytes differ.");
        var lockPin = manifest.RootElement.GetProperty("oracleLock");
        var lockBytes = await ReadEvidenceAsync(Path.Combine(repository, Family, lockPin.GetProperty("path").GetString()!)).ConfigureAwait(false);
        Require(lockBytes.Length == lockPin.GetProperty("bytes").GetInt32() &&
            Hash(lockBytes) == lockPin.GetProperty("sha256").GetString(), "Oracle receipt hash/bytes differ.");
        using var capture = JsonDocument.Parse(captureBytes, new JsonDocumentOptions { MaxDepth = 128 });
        using var oracleLock = JsonDocument.Parse(lockBytes, new JsonDocumentOptions { MaxDepth = 128 });
        Require(manifest.RootElement.GetProperty("caseCount").GetInt32() == 7 &&
            manifest.RootElement.GetProperty("exportCount").GetInt32() == 8, "Complete actual-native scenario matrix differs.");
        Require(capture.RootElement.GetProperty("sourceSha").GetString() == PublicRevision, "Pinned Pi source differs.");
        var environment = oracleLock.RootElement.GetProperty("environmentPins");
        Require(environment.GetProperty("sourceSha").GetString() == PublicRevision &&
            environment.GetProperty("sourceFingerprint").GetProperty("canonicalGit").GetProperty("files").GetInt32() == 2093 &&
            environment.GetProperty("sourceFingerprint").GetProperty("canonicalGit").GetProperty("sha256").GetString() ==
                "2d65bfaee0e2556cb82ae7e0425de68560ecc3be4451f69ceff3bcc9c6144aa3" &&
            environment.GetProperty("dependencies").GetArrayLength() == 8 &&
            environment.GetProperty("runtime").GetProperty("version").GetString() == "v24.19.0",
            "Whole source/runtime/eight dependency fingerprint differs.");
        foreach (var name in new[] { "sourceUnchanged", "dependenciesUnchanged", "nativeOriginalsUnchanged" })
            Require(oracleLock.RootElement.GetProperty(name).GetBoolean(), "Capture did not retain original pins: " + name);
        var checks = capture.RootElement.GetProperty("checks");
        foreach (var name in new[] { "wholeSessionManagerLoaded", "clocksAndRngUnmodified", "scratchWriteGuard",
            "networkAndProcessesDenied", "actualNativeSourcesAndExportsUnchanged" })
            Require(checks.GetProperty(name).GetBoolean(), "Capture control differs: " + name);
        Require(checks.GetProperty("ownedOpenDescriptors").GetInt32() == 0, "Source descriptors did not join.");
        var childReceipts = oracleLock.RootElement.GetProperty("children");
        Require(childReceipts.GetArrayLength() == 2, "Two genuinely completed source readers required.");
        foreach (var child in childReceipts.EnumerateArray())
            Require(child.GetProperty("status").GetInt32() == 0 && child.GetProperty("signal").ValueKind == JsonValueKind.Null &&
                child.GetProperty("error").ValueKind == JsonValueKind.Null &&
                child.GetProperty("boundedTimeoutMilliseconds").GetInt32() == 20000 &&
                child.GetProperty("maxBufferBytes").GetInt32() == 8 * 1024 * 1024 &&
                child.GetProperty("stdoutBytes").GetInt32() <= 8 * 1024 * 1024 &&
                child.GetProperty("stderrBytes").GetInt32() <= 8 * 1024 * 1024,
                "A timeout, buffer failure, signal or unfinished source child cannot qualify.");
        var nativeManifestBytes = CheckedBytes(capture.RootElement.GetProperty("nativeManifestOriginal"));
        using var nativeManifest = JsonDocument.Parse(nativeManifestBytes);
        Require(nativeManifest.RootElement.GetProperty("executionCandidate").GetString() ==
            manifest.RootElement.GetProperty("executionCandidate").GetString(), "Declared clean candidate differs.");
        Require(nativeManifest.RootElement.GetProperty("executedAssemblies").GetArrayLength() == 3,
            "Actual executed assembly pins absent.");
        var authoredSource = nativeManifest.RootElement.GetProperty("sources").EnumerateArray().Single(item =>
            item.GetProperty("caseId").GetString() == caseId);
        var originalBytes = CheckedBytes(authoredSource.GetProperty("originalByteView"));
        var observations = capture.RootElement.GetProperty("observations").EnumerateArray()
            .Where(item => item.GetProperty("caseId").GetString() == caseId).ToArray();
        Require(observations.Length == (caseId == "v3-opaque-numbers" ? 2 : 1), "Actual native format variants lost.");
        var root = Path.Combine(Path.GetTempPath(), "pisharp-native-copy-reader-" + Guid.NewGuid().ToString("N"));
        ValidateAbsolute(root); NoLinks(root); Directory.CreateDirectory(root);
        var owned = new List<string>();
        try
        {
            var sourcePath = Owned("source.jsonl"); await WriteNewAsync(sourcePath, originalBytes).ConfigureAwait(false);
            foreach (var observed in observations)
            {
                var exportId = observed.GetProperty("exportId").GetString()!;
                var artifact = nativeManifest.RootElement.GetProperty("exports").EnumerateArray().Single(item =>
                    item.GetProperty("exportId").GetString() == exportId);
                var ids = artifact.GetProperty("v1EntryIds").EnumerateArray().Select(id => id.GetString()!).ToImmutableArray();
                var format = Enum.Parse<SessionCopyFormat>(artifact.GetProperty("format").GetString()!);
                var destination = Owned(exportId + ".jsonl");
                var service = new SessionCopyService();
                var receipt = await service.CopyAsync(new(sourcePath, destination, format, ids)).ConfigureAwait(false);
                Require(receipt.Status == SessionCopyStatus.Published && receipt.OmittedFields == 0 &&
                    receipt.OmittedRecords == 0 && receipt.Retention is { ExcludedFields: 0, ExcludedRecords: 0 },
                    "Fresh actual export lost source fields/records.");
                var exported = await ReadBoundedAsync(destination).ConfigureAwait(false);
                Require(exported.AsSpan().SequenceEqual(CheckedBytes(observed.GetProperty("actualNativeExportBefore"))),
                    "Current actual service no longer produces the captured native bytes.");
                var repeatedPath = Owned(exportId + ".repeat.jsonl");
                var repeatedReceipt = await service.CopyAsync(new(sourcePath, repeatedPath, format, ids)).ConfigureAwait(false);
                Require(repeatedReceipt.Status == SessionCopyStatus.Published &&
                    (await ReadBoundedAsync(repeatedPath).ConfigureAwait(false)).AsSpan().SequenceEqual(exported),
                    "Repeated actual import changed identities or multiplied records.");
                using var originalArtifact = JsonDocument.Parse(CheckedBytes(observed.GetProperty("nativeArtifactOriginal")),
                    new JsonDocumentOptions { MaxDepth = 128 });
                using var currentReceipt = JsonDocument.Parse(JsonSerializer.Serialize(Receipt(receipt)));
                Same(currentReceipt.RootElement, originalArtifact.RootElement.GetProperty("receipt"), "complete native copy/migration/retention receipt");
                var log = await new SessionLogReader().ReadFileAsync(destination).ConfigureAwait(false);
                Require(log.SourceComplete && log.Status == SessionLogReadStatus.Complete, "Fresh export could not reopen.");
                SessionLogStoreSnapshot reopened;
                await using (var store = await SessionLogStore.OpenAsync(destination).ConfigureAwait(false))
                { reopened = store.Snapshot; Require(!store.IsPoisoned, "Fresh reopened writer poisoned."); }
                Require((await ReadBoundedAsync(destination).ConfigureAwait(false)).AsSpan().SequenceEqual(exported),
                    "Native reader/store reopen changed export bytes.");
                var tree = new SessionTreeQueries().Build(reopened.Entries);
                var initial = NativeSnapshotElement(View(reopened.Header, reopened.Entries, tree, reopened.LeafId));
                CompareFull(initial, observed.GetProperty("initial"), observed.GetProperty("initialComparison"), originalArtifact.RootElement.GetProperty("initial"));
                CompareFull(initial, observed.GetProperty("reopened"), observed.GetProperty("reopenComparison"), originalArtifact.RootElement.GetProperty("initial"));
                var selections = observed.GetProperty("selections");
                Require(selections.GetArrayLength() == reopened.Entries.Length + 1, "Every physical entry and explicit root must be compared.");
                var selectedIds = new HashSet<string?>(StringComparer.Ordinal);
                foreach (var selection in selections.EnumerateArray())
                {
                    var leaf = OptionalString(selection.GetProperty("leafId")); Require(selectedIds.Add(leaf), "Duplicate selection.");
                    var current = NativeSnapshotElement(View(reopened.Header, reopened.Entries, tree, leaf));
                    var baseline = originalArtifact.RootElement.GetProperty("selections").EnumerateArray()
                        .Single(item => OptionalString(item.GetProperty("leafId")) == leaf).GetProperty("observed");
                    CompareFull(current, selection.GetProperty("observed"), selection.GetProperty("comparison"), baseline);
                    using var nativeView = JsonDocument.Parse(current.GetProperty("rawJson").GetString()!);
                    CompareWire(selection.GetProperty("publicProjection"), nativeView.RootElement.GetProperty("projection"));
                    CompareWire(selection.GetProperty("publicContext"), nativeView.RootElement.GetProperty("context"));
                }
                Require(selectedIds.Contains(null) && reopened.Entries.All(entry => selectedIds.Contains(entry.Id)), "Selected matrix is incomplete.");
                using var completeRecords = JsonDocument.Parse(JsonSerializer.Serialize(new[] { reopened.Header.WireBody.Value }.Concat(Bodies(reopened.Entries))));
                CompareWire(observed.GetProperty("parsed"), completeRecords.RootElement);
                CompareWire(observed.GetProperty("loaded"), completeRecords.RootElement);
                CompareWire(observed.GetProperty("loadedAgain"), completeRecords.RootElement);
                var afterOpen = CheckedBytes(observed.GetProperty("afterOpen"));
                Require(afterOpen.AsSpan().SequenceEqual(CheckedBytes(observed.GetProperty("afterSelections"))) &&
                    afterOpen.AsSpan().SequenceEqual(CheckedBytes(observed.GetProperty("afterReopen"))), "Source queries/reopen mutated the scratch record set.");
                var expectedSourceBytes = exported.Length != 0 && exported[^1] != (byte)'\n' ? exported.Concat(new byte[] { (byte)'\n' }).ToArray() : exported;
                Require(expectedSourceBytes.AsSpan().SequenceEqual(afterOpen) &&
                    expectedSourceBytes.AsSpan().SequenceEqual(CheckedBytes(observed.GetProperty("afterLoad"))) &&
                    expectedSourceBytes.AsSpan().SequenceEqual(CheckedBytes(observed.GetProperty("afterRepeatedLoad"))),
                    "Only measured final-LF repair is admitted for the current native exports.");
                Require((await ReadBoundedAsync(sourcePath).ConfigureAwait(false)).AsSpan().SequenceEqual(originalBytes),
                    "Original import source changed.");
            }
            Require(Directory.GetFiles(root, ".pisharp-copy-*.tmp").Length == 0, "Actual publisher left an owned temporary.");
        }
        finally
        {
            // Delete only individually named owned files after actual stream/store joins.
            foreach (var path in owned)
            { Require(Path.GetDirectoryName(Path.GetFullPath(path)) == root, "Cleanup escaped owned prefix."); NoLinks(path); if (File.Exists(path)) File.Delete(path); }
            Require(Directory.GetFileSystemEntries(root).Length == 0, "Unowned/unfinished artifact prevents cleanup.");
            Directory.Delete(root, recursive: false);
        }
        Require(Hash(await ReadEvidenceAsync(Path.Combine(repository, Family, capturePin.GetProperty("path").GetString()!)).ConfigureAwait(false)) ==
            capturePin.GetProperty("sha256").GetString(), "Read-only frozen capture changed.");

        string Owned(string name)
        { Require(name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.'), "Invalid owned filename.");
            var path = Path.Combine(root, name); Require(Path.GetDirectoryName(path) == root, "Owned path escaped."); owned.Add(path); return path; }
    }

    private static JsonElement NativeSnapshotElement(object value) => JsonSerializer.SerializeToElement(NativeSnapshot(value));
    private static async Task<byte[]> ReadEvidenceAsync(string path)
    { NoLinks(path); Require(new FileInfo(path).Length is > 0 and <= EvidenceBytes, "Evidence exceeds separate read-only bound.");
        var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false); Require(bytes.Length <= EvidenceBytes, "Evidence grew beyond bound."); return bytes; }
    private static byte[] CheckedBytes(JsonElement view)
    {
        var bytes = Convert.FromBase64String(view.GetProperty("base64").GetString()!);
        Require(bytes.Length == view.GetProperty("bytes").GetInt32() && Hash(bytes) == view.GetProperty("sha256").GetString() &&
            Utf8.GetBytes(view.GetProperty("utf8").GetString()!).AsSpan().SequenceEqual(bytes), "Complete raw byte views differ.");
        return bytes;
    }
    private static string? OptionalString(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetString();
    private static void CompareFull(JsonElement actualNative, JsonElement source, JsonElement comparison, JsonElement originalNative)
    {
        using var actual = JsonDocument.Parse(actualNative.GetProperty("rawJson").GetString()!, new JsonDocumentOptions { MaxDepth = 128 });
        using var original = JsonDocument.Parse(originalNative.GetProperty("rawJson").GetString()!, new JsonDocumentOptions { MaxDepth = 128 });
        Same(actual.RootElement, original.RootElement, "complete current versus captured native view");
        Same(actualNative.GetProperty("ownPropertyPaths"), originalNative.GetProperty("ownPropertyPaths"), "complete native own-property inventory");
        Same(actualNative.GetProperty("rawNumericTokens"), originalNative.GetProperty("rawNumericTokens"), "complete native numeric-token inventory");
        var differences = CompareWire(source, actual.RootElement);
        var recorded = comparison.GetProperty("differences").EnumerateArray().Select(item =>
            (Path: item.GetProperty("path").GetString()!, Kind: item.GetProperty("kind").GetString()!)).ToArray();
        Require(differences.SequenceEqual(recorded), "Full numeric/value/property difference inventory was altered or truncated.");
        Require(comparison.GetProperty("rawValuesMatch").GetBoolean() == (differences.Count == 0), "Truthful raw match receipt differs.");
        using var sourceSerialized = JsonDocument.Parse(comparison.GetProperty("sourceSerializedRawJson").GetString()!, new JsonDocumentOptions { MaxDepth = 128 });
        Same(source.GetProperty("value"), sourceSerialized.RootElement, "complete source serialized observation");
        var nativeOwn = actualNative.GetProperty("ownPropertyPaths").EnumerateArray().Select(path => path.GetString()!).ToHashSet(StringComparer.Ordinal);
        var sourceOwn = source.GetProperty("ownPropertyPaths").EnumerateArray().Select(path => path.GetString()!).ToHashSet(StringComparer.Ordinal);
        var sourceOnly = sourceOwn.Except(nativeOwn, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var nativeOnly = nativeOwn.Except(sourceOwn, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Require(sourceOnly.SequenceEqual(comparison.GetProperty("sourceOnlyOwnPropertyPaths").EnumerateArray().Select(path => path.GetString())) &&
            nativeOnly.SequenceEqual(comparison.GetProperty("nativeOnlyOwnPropertyPaths").EnumerateArray().Select(path => path.GetString())),
            "Complete own-property difference inventory differs.");
        Require(nativeOnly.Length == 0 && sourceOnly.All(path => source.GetProperty("ownUndefinedPaths").EnumerateArray().Any(value => value.GetString() == path)),
            "Undisclosed field-presence difference.");
    }

    // Audit every raw numeric token and whole raw value; these are disclosures, not
    // a numerical normalization or waiver. Non-numeric differences always fail.
    private static List<(string Path, string Kind)> CompareWire(JsonElement source, JsonElement native)
    {
        var nonfinite = source.GetProperty("nonFiniteNumbers").EnumerateArray().ToDictionary(item => item.GetProperty("path").GetString()!, item => item.GetProperty("value").GetString()!, StringComparer.Ordinal);
        var negative = source.GetProperty("negativeZeroPaths").EnumerateArray().Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal);
        var differences = new List<(string Path, string Kind)>(); Visit(native, source.GetProperty("value"), "");
        foreach (var path in source.GetProperty("ownUndefinedPaths").EnumerateArray().Select(value => value.GetString()!))
            Require(!TryAt(native, path, out _), "Own undefined must correspond to native field absence: " + path);
        return differences;
        void Add(string path, string kind)
        { Require(differences.Count < 10000, "Exhaustive per-view difference bound exceeded."); differences.Add((path, kind)); }
        void Visit(JsonElement left, JsonElement right, string path)
        {
            if (left.ValueKind == JsonValueKind.Number)
            {
                var raw = left.GetRawText(); var number = double.Parse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
                if (right.ValueKind == JsonValueKind.Null)
                { Require(double.IsInfinity(number) && nonfinite.TryGetValue(path, out var spelling) && spelling == (number > 0 ? "Infinity" : "-Infinity"), "Undisclosed nonfinite conversion: " + path);
                    Add(path, "source-nonfinite-json-serialization"); return; }
                Require(right.ValueKind == JsonValueKind.Number && right.GetDouble() == number, "Unproven JS numeric conversion: " + path);
                var sourceRaw = right.GetRawText();
                if (raw != sourceRaw)
                {
                    if (negative.Contains(path)) { Require(number == 0 && raw.StartsWith("-", StringComparison.Ordinal), "Invalid negative-zero sidecar."); Add(path, "source-negative-zero-serialization"); }
                    else Add(path, DecimalIdentity(raw) == DecimalIdentity(sourceRaw) ? "numeric-token-spelling" : "numeric-value-difference");
                }
                return;
            }
            Require(left.ValueKind == right.ValueKind, "Nonnumeric source value/shape differs: " + path);
            if (left.ValueKind == JsonValueKind.Object)
            {
                var leftFields = left.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
                var rightFields = right.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
                Require(leftFields.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(rightFields.Keys), "Nonnumeric property presence differs: " + path);
                foreach (var key in leftFields.Keys.Order(StringComparer.Ordinal)) Visit(leftFields[key], rightFields[key], path + "/" + Escape(key));
            }
            else if (left.ValueKind == JsonValueKind.Array)
            { Require(left.GetArrayLength() == right.GetArrayLength(), "Array shape differs: " + path);
                for (var index = 0; index < left.GetArrayLength(); index++) Visit(left[index], right[index], path + "/" + index); }
            else Require(left.ValueKind == JsonValueKind.String ? left.GetString() == right.GetString() : left.GetRawText() == right.GetRawText(), "Nonnumeric source value differs: " + path);
        }
    }
    private static string DecimalIdentity(string lexeme)
    {
        var parts = lexeme.Split(['e', 'E']); var mantissa = parts[0]; var sign = mantissa.StartsWith("-", StringComparison.Ordinal) ? "-" : "";
        mantissa = mantissa.TrimStart('-'); var point = mantissa.IndexOf('.');
        var fraction = point < 0 ? 0 : mantissa.Length - point - 1; var digits = mantissa.Replace(".", "", StringComparison.Ordinal).TrimStart('0');
        if (digits.Length == 0) return "0";
        var exponent = System.Numerics.BigInteger.Parse(parts.Length == 1 ? "0" : parts[1], System.Globalization.CultureInfo.InvariantCulture) - fraction;
        while (digits.EndsWith('0')) { digits = digits[..^1]; exponent++; }
        return sign + digits + "e" + exponent.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    private static bool TryAt(JsonElement value, string pointer, out JsonElement found)
    {
        found = value;
        foreach (var encoded in pointer.Split('/').Skip(1))
        {
            var key = encoded.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (found.ValueKind == JsonValueKind.Object) { if (!found.TryGetProperty(key, out found)) return false; }
            else if (found.ValueKind == JsonValueKind.Array && int.TryParse(key, out var index) && index < found.GetArrayLength()) found = found[index];
            else return false;
        }
        return true;
    }
    private static void Same(JsonElement left, JsonElement right, string where)
    {
        Require(left.ValueKind == right.ValueKind, "Raw JSON shape differs: " + where);
        if (left.ValueKind == JsonValueKind.Object)
        {
            var a = left.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
            var b = right.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
            Require(a.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(b.Keys), "Raw JSON property presence differs: " + where);
            foreach (var key in a.Keys) Same(a[key], b[key], where + "/" + Escape(key));
        }
        else if (left.ValueKind == JsonValueKind.Array)
        { Require(left.GetArrayLength() == right.GetArrayLength(), "Raw JSON array length differs: " + where);
            for (var index = 0; index < left.GetArrayLength(); index++) Same(left[index], right[index], where + "/" + index); }
        else Require(left.ValueKind == JsonValueKind.String ? left.GetString() == right.GetString() : left.GetRawText() == right.GetRawText(), "Raw JSON value/token differs: " + where);
    }

    private static object View(SessionEntry header, ImmutableArray<SessionEntry> entries, SessionTreeSnapshot tree, string? leaf)
    {
        var history = new SessionHistoryProjector().Project(entries, leaf);
        var projection = history.Context;
        var context = new { messages = Messages(projection.Messages), thinkingLevel = projection.ThinkingLevel,
            model = projection.Model is null ? null : new { provider = projection.Model.Provider, modelId = projection.Model.ModelId } };
        var result = new Dictionary<string, object?>
        {
            ["header"] = header.WireBody.Value, ["entries"] = Bodies(entries),
            ["tree"] = tree.RootIds.Select(id => TreeNode(id, tree)).ToArray(),
            ["leafId"] = leaf, ["branch"] = Bodies(tree.GetBranch(leaf)),
            ["contextEntries"] = projection.ContextEntries.Select(c => c.SourceEntry.WireBody.Value).ToArray(),
            ["projection"] = new { entries = projection.ContextEntries.Select(c => new { sourceEntry = c.SourceEntry.WireBody.Value,
                messages = Messages(c.Messages) }).ToArray(), context.messages, context.thinkingLevel, context.model },
            ["context"] = context, ["llmMessages"] = Messages(projection.LlmMessages),
            ["children"] = new[] { new { parentId = (string?)null, entries = Bodies(tree.RootIds.Select(id => tree.ById[id].Entry).ToImmutableArray()) } }
                .Concat(entries.Select(entry => new { parentId = (string?)entry.Id, entries = Bodies(tree.GetChildren(entry.Id)) })).ToArray(),
            ["labels"] = entries.Select(entry =>
            {
                var label = new Dictionary<string, object?> { ["entryId"] = entry.Id };
                if (tree.GetLabel(entry.Id) is { } resolved) label["label"] = resolved.Label;
                return label;
            }).ToArray(),
            ["effectiveTools"] = history.SystemState.Tools.Select(tool => tool.Value).ToArray(),
            ["systemPrompt"] = history.SystemState.Prompt
        };
        if (tree.SessionName is not null) result["sessionName"] = tree.SessionName;
        if (leaf is not null) result["leafEntry"] = tree.GetEntry(leaf)!.WireBody.Value;
        if (history.EffectiveSystemMessage is not null) result["effectiveSystemMessage"] = history.EffectiveSystemMessage.WireBody.Value;
        return result;
    }

    private static Dictionary<string, object?> TreeNode(string id, SessionTreeSnapshot tree)
    {
        var node = tree.ById[id]; var children = tree.GetChronologicalChildren(id);
        Require(children.Status == SessionTreeOrderStatus.Completed, "Fixture timestamp ordering is unsupported.");
        var wire = new Dictionary<string, object?> { ["entry"] = node.Entry.WireBody.Value,
            ["children"] = children.Entries.Select(child => TreeNode(child.Id, tree)).ToArray() };
        if (node.ResolvedLabel is { } label) { wire.Add("label", label.Label); wire.Add("labelTimestamp", label.Timestamp); }
        return wire;
    }
    private static JsonElement[] Bodies(ImmutableArray<SessionEntry> entries) => entries.Select(entry => entry.WireBody.Value).ToArray();
    private static JsonElement[] Messages(ImmutableArray<TranscriptEntry> entries) => entries.Select(entry => entry.WireBody.Value).ToArray();
    private static object NativeSnapshot(object value)
    {
        var raw = JsonSerializer.Serialize(value); using var parsed = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 64 });
        var own = new List<string>(); var numbers = new List<object>(); Visit(parsed.RootElement, "");
        return new { rawJson = raw, ownPropertyPaths = own.ToArray(), rawNumericTokens = numbers.ToArray(),
            ownUndefinedPaths = Array.Empty<string>(), numericProfile = "Owned JSON lexemes; no conversion to JavaScript Number" };
        void Visit(JsonElement element, string path)
        {
            if (element.ValueKind == JsonValueKind.Number) numbers.Add(new { path, lexeme = element.GetRawText() });
            else if (element.ValueKind == JsonValueKind.Object)
                foreach (var property in element.EnumerateObject()) { var child = path + "/" + Escape(property.Name); own.Add(child); Visit(property.Value, child); }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                var index = 0; foreach (var child in element.EnumerateArray()) { var childPath = path + "/" + index++; own.Add(childPath); Visit(child, childPath); }
            }
        }
    }
    private static string Escape(string value) => value.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
    private static object Receipt(SessionCopyResult result)
    {
        var migration = result.Inspection.Migration!;
        return new { status = result.Status.ToString(), format = result.Format.ToString(), result.OutputSha256, result.OutputBytes,
            result.OmittedRecords, result.OmittedFields, storageDurability = result.StorageDurability.ToString(), retention = result.Retention, sourceSha256 = result.Inspection.SourceSha256,
            original = ByteView(result.Inspection.OriginalBytes.ToArray()), currentRecords = Bodies(result.Inspection.CurrentRecords),
            migration = new { status = migration.Status.ToString(), migration.SourceVersion, migration.VersionDefaulted, migration.TargetVersion,
                migration.WasMigrated, sourceRecords = migration.SourceRecords.Select(record => record.Value).ToArray(),
                records = Bodies(migration.Records), receipts = migration.Receipts.Select(r => new { r.RecordIndex,
                    transform = r.Transform.ToString(), r.Field, r.BeforePresent, before = r.Before?.Value, r.AfterPresent, after = r.After?.Value }).ToArray(),
                diagnostics = migration.Diagnostics.Select(d => new { code = d.Code.ToString(), d.RecordIndex, codecFailure = d.CodecFailure?.ToString() }).ToArray() },
            diagnostics = result.Diagnostics.Select(d => new { code = d.Code.ToString(), d.RecordIndex, graphFailure = d.GraphFailure?.ToString() }).ToArray() };
    }
    private sealed record FilePin(string Path, int Bytes, string Sha256);
    private static FilePin Pin(string root, string name)
    { var bytes = File.ReadAllBytes(Path.Combine(root, name)); Require(bytes.Length <= MaximumFileBytes, "Artifact exceeds budget."); return new(name, bytes.Length, Hash(bytes)); }
    private static FilePin[] SourcePins(string root)
    {
        string[] paths = ["tests/PiSharp.Sessions.Tests/SessionNativeCopyReaderReferenceTests.cs",
            "tools/PiReferenceRunner/capture-native-session-copy-reader.mjs", "Directory.Build.props",
            "src/PiSharp.Sessions/PiSharp.Sessions.csproj", "src/PiSharp.Sessions/Import/SessionCopyService.cs",
            "src/PiSharp.Sessions/Serialization/SessionEntryCodec.cs", "src/PiSharp.Sessions/Serialization/SessionEntryMigration.cs",
            "src/PiSharp.Sessions/Storage/SessionLogReader.cs", "src/PiSharp.Sessions/Storage/SessionLogStore.cs",
            "src/PiSharp.Sessions/Context/SessionContextProjector.cs", "src/PiSharp.Sessions/Context/SessionContextInfluenceProjector.cs",
            "src/PiSharp.Sessions/Context/SessionHistoryProjector.cs", "src/PiSharp.Sessions/Tree/SessionTreeQueries.cs"];
        return paths.Select(path => Pin(root, path)).ToArray();
    }
    private static object ByteView(byte[] bytes) => new { bytes = bytes.Length, sha256 = Hash(bytes), utf8 = Utf8.GetString(bytes), base64 = Convert.ToBase64String(bytes) };
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static async Task<byte[]> ReadBoundedAsync(string path)
    { Require(new FileInfo(path).Length <= MaximumFileBytes, "Input/artifact exceeds budget."); var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        Require(bytes.Length <= MaximumFileBytes, "Input/artifact exceeds budget."); return bytes; }
    private static async Task WriteNewAsync(string path, byte[] bytes)
    { Require(bytes.Length <= MaximumFileBytes, "Fixture exceeds budget."); await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
        8192, FileOptions.Asynchronous); await stream.WriteAsync(bytes).ConfigureAwait(false); await stream.FlushAsync().ConfigureAwait(false); }
    private static Task WriteJsonNewAsync(string path, object value) => WriteNewAsync(path, Utf8.GetBytes(JsonSerializer.Serialize(value, JsonOptions) + "\n"));
    private sealed record AssemblyPin(string Name, string Path, long Bytes, string Sha256);
    private static AssemblyPin[] AssemblyPins() => new[] { typeof(SessionNativeCopyReaderReferenceTests).Assembly,
        typeof(SessionCopyService).Assembly, typeof(JsonData).Assembly }.Select(assembly =>
        new AssemblyPin(assembly.GetName().Name!, assembly.Location, new FileInfo(assembly.Location).Length,
            Hash(File.ReadAllBytes(assembly.Location)))).ToArray();
    private static string FindRepository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "PiSharp.Sessions", "Import", "SessionCopyService.cs"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Session reference repository was not found.");
    }
    private static void ValidateAbsolute(string path)
    { Require(path.Length is > 0 and <= 4096 && Path.IsPathFullyQualified(path) && Path.GetFullPath(path) == path && !path.Contains('\0'), "Explicit normalized absolute path required."); }
    private static void NoLinks(string path)
    { for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        { if (File.Exists(current) || Directory.Exists(current)) Require((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0, "Linked paths are unsupported."); } }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class ObservingFiles : ISessionCopyFileSystem
    {
        internal int OpenReadCalls { get; private set; }
        internal int Flushes { get; private set; }
        internal int PublishCalls { get; private set; }
        private bool _closed;
        public async ValueTask<Stream> OpenReadAsync(string path, CancellationToken token)
        { OpenReadCalls++; var stream = await SessionCopyService.LocalFileSystem.OpenReadAsync(path, token).ConfigureAwait(false);
            Require(stream.CanRead && !stream.CanWrite, "Source was not opened read-only."); return stream; }
        public async ValueTask<Stream> CreateNewTemporaryAsync(string path) => new ObservingWriteStream(
            await SessionCopyService.LocalFileSystem.CreateNewTemporaryAsync(path).ConfigureAwait(false), this);
        public async ValueTask PublishNewAsync(string temporaryPath, string destinationPath)
        { Require(Flushes == 1 && _closed, "Publish preceded awaited flush/disposal."); PublishCalls++;
            await SessionCopyService.LocalFileSystem.PublishNewAsync(temporaryPath, destinationPath).ConfigureAwait(false); }
        public ValueTask DeleteTemporaryAsync(string path) => SessionCopyService.LocalFileSystem.DeleteTemporaryAsync(path);
        private sealed class ObservingWriteStream(Stream inner, ObservingFiles owner) : Stream
        {
            public override bool CanRead => false; public override bool CanSeek => inner.CanSeek; public override bool CanWrite => inner.CanWrite;
            public override long Length => inner.Length; public override long Position { get => inner.Position; set => inner.Position = value; }
            public override void Flush() => inner.Flush();
            public override async Task FlushAsync(CancellationToken token)
            { await inner.FlushAsync(token).ConfigureAwait(false); owner.Flushes++; }
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) => inner.WriteAsync(buffer, token);
            public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
            public override void SetLength(long value) => inner.SetLength(value);
            public override async ValueTask DisposeAsync()
            { await inner.DisposeAsync().ConfigureAwait(false); owner._closed = true; GC.SuppressFinalize(this); }
            protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); owner._closed = true; } base.Dispose(disposing); }
        }
    }
}
