using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Agent;
using PiSharp.Agent.Tools;
using PiSharp.Contracts;
using PiSharp.Tools.Files;

internal static class EditPreviewTests
{
    private const string SourceSha = "d86654abb8862e201933517d6f1fce9f88dd117f";
    private const string InputSha = "fd07bb0eb503fdcd692822dbe2d6a51d0deae47b16c7a0e56ae7ea80788812f2";
    private const string ExpectedSha = "2b17786c53dc3f36e95e92920066aa64159d6d10ec1e25de2cfe110f91dcb4fb";
    private static string? _fixtureDirectory;
    public static JsonElement? Evidence { get; private set; }
    public static JsonElement? PlatformEvidence { get; private set; }
    public static void Configure(string repositoryRoot)
    {
        if (!Path.IsPathFullyQualified(repositoryRoot)) throw new ArgumentException("An explicit absolute repository root is required.");
        _fixtureDirectory = Path.Combine(Path.GetFullPath(repositoryRoot), "fixtures", "pi-v0.99.1", "edit");
        if (!Directory.Exists(_fixtureDirectory)) throw new ArgumentException("The immutable edit fixture directory is absent.");
    }
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("preview complete captured twelve fulfilled bodies and unchanged actual file bytes", CapturedPreviews),
        ("preview mandatory final read policy transformed targets and invalid actions", FinalPolicy),
        ("preview actual readonly file R_OK profile and directory read failure", LocalReadAuthority),
        ("preview cancellation waits for canonical access read and cleanup callbacks", CancellationSettlement),
        ("preview display budget is independent from an unused unified patch", DisplayBudget),
        ("preview UTF8 logical limits structural admission and private callback errors", BoundsAndPrivacy),
        ("preview actual readonly observation proceeds while a same-file queued writer waits", ConcurrentWriter)
    ];

    private static async Task CapturedPreviews()
    {
        var directory = _fixtureDirectory ?? throw new InvalidOperationException("Configure immutable edit fixtures before comparing previews.");
        using var input = ReadPinned(Path.Combine(directory, "core.input.json"), InputSha, 1_048_576);
        using var expected = ReadPinned(Path.Combine(directory, "core.expected.json"), ExpectedSha, 2_097_152);
        Equal(SourceSha, input.RootElement.GetProperty("sourceSha").GetString());
        Equal(SourceSha, expected.RootElement.GetProperty("sourceSha").GetString());
        Equal("authored-edit-input-and-owned-filesystem-layout", input.RootElement.GetProperty("kind").GetString());
        Equal("captured-unchanged-edit-oracle", expected.RootElement.GetProperty("kind").GetString());
        var inputs = input.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        var sources = expected.RootElement.GetProperty("observations").GetProperty("cases").EnumerateArray().ToArray();
        Equal(12, inputs.Length); Equal(inputs.Length, sources.Length);
        using var temp = new TemporaryFiles(); Directory.CreateDirectory(temp.Child("files"));
        var reports = new List<object>(); var differences = new List<object>(); var displays = 0; var errors = 0;
        for (var index = 0; index < inputs.Length; index++)
        {
            var authored = inputs[index]; var source = sources[index]; var id = authored.GetProperty("caseId").GetString()!;
            Equal(id, source.GetProperty("caseId").GetString());
            var path = authored.GetProperty("path").GetString()!; Equal(path, source.GetProperty("path").GetString());
            Check(path.StartsWith("files/", StringComparison.Ordinal) && !path.Contains("..", StringComparison.Ordinal) && !path.Contains('\\') && !path.Contains(':') && !path.Contains('\0'), "Fixture path is outside the literal owned layout.");
            var target = temp.Child(path); var missing = authored.TryGetProperty("missing", out var absent) && absent.GetBoolean();
            if (!missing) await WriteNew(target, Encoding.UTF8.GetBytes(authored.GetProperty("utf8").GetString()!));
            var before = await Snapshot(target);
            var supplied = JsonNode.Parse(authored.GetProperty("arguments").GetRawText())!.AsObject(); supplied["path"] = path;
            var owned = JsonData.Parse(supplied.ToJsonString()); var originalArguments = owned.ToString();
            // The source compute API accepts an already typed array. Exercise the existing preparation variant API separately.
            var preparationOperations = new Operations(); var queue = Queue(preparationOperations);
            var edit = new EditTool(temp.Root, temp.Root, queue, preparationOperations);
            var prepared = await edit.PrepareAsync(Invocation("edit", owned), CancellationToken.None);
            var publicPrepared = JsonSerializer.SerializeToElement(new { path = prepared.Arguments.Value.GetProperty("displayPath").GetString(),
                edits = prepared.Arguments.Value.GetProperty("edits").Clone() });
            Compare(id + ".preparation.prepared", source.GetProperty("preparation").GetProperty("prepared"), publicPrepared, differences);
            Compare(id + ".preparation.suppliedBefore", source.GetProperty("preparation").GetProperty("suppliedBefore"), owned.Value, differences);
            Equal(originalArguments, owned.ToString()); Equal(0, preparationOperations.ReadCalls); Equal(0, preparationOperations.WriteCalls);
            var edits = publicPrepared.GetProperty("edits").EnumerateArray().Select(item => new TextEdit(item.GetProperty("oldText").GetString()!, item.GetProperty("newText").GetString()!)).ToImmutableArray();
            var operations = new Operations(); var probe = new Probe(); var policy = new Policy(target);
            var preview = new EditPreview(temp.Root, temp.Root, policy, operations, accessProbe: probe);
            var outcome = await preview.ComputeAsync(path, edits); Fulfilled(outcome);
            var observed = JsonSerializer.SerializeToElement(new { status = "fulfilled", value = outcome.Preview!.Value, ownUndefinedPaths = Array.Empty<string>() });
            Compare(id + ".preview", source.GetProperty("preview"), observed, differences);
            var after = await Snapshot(target);
            var expectedBefore = source.GetProperty("filesystem").GetProperty("before");
            Compare(id + ".filesystem.before", expectedBefore, Element(before), differences);
            Compare(id + ".filesystem.afterReadonly", expectedBefore, Element(after), differences);
            Equal(1, policy.Actions.Count); Equal(1, probe.Calls); Equal(FileAccessModes.Read, probe.Modes.Single());
            Equal(missing ? 0 : 1, operations.ReadCalls); Equal(0, operations.WriteCalls); Equal(0, operations.DirectoryCalls);
            Equal(target, policy.Actions.Single().Target); Equal(target, probe.Paths.Single());
            if (outcome.Preview.Value.TryGetProperty("error", out _)) errors++; else displays++;
            reports.Add(new { caseId = id, sourceObservation = source.Clone(), native = new
            {
                publicPrepared, suppliedBefore = owned.Value.Clone(), suppliedAfter = owned.Value.Clone(), preview = observed,
                invocation = NativeResult(outcome.Invocation), finalAction = Action(policy.Actions.Single()),
                filesystem = new { before, after, byteIdentical = SameBytes(before, after) },
                counts = new { operations.CanonicalCalls, probe.Calls, modes = probe.Modes.Select(mode => mode.ToString()).ToArray(), operations.ReadCalls, operations.WriteCalls, operations.DirectoryCalls }
            } });
        }
        Equal(5, displays); Equal(7, errors);
        Evidence = JsonSerializer.SerializeToElement(new { schemaVersion = 1, sourceSha = SourceSha, inputSha256 = InputSha, expectedSha256 = ExpectedSha,
            corpusCases = inputs.Length, displays, errors, compared = "Complete source preview envelopes and owned bodies, prepared public inputs, exact original and unchanged preview bytes; no field masks or string normalization.",
            mapping = "Native Preview maps directly to the source fulfilled value. Empty native Content, Details=Preview, IsError=false and no Failure/Terminate are checked separately. Source post-edit filesystem.after is retained but is not the expected readonly state.",
            cases = reports, differences, phaseAcceptanceClaimed = false });
        Check(differences.Count == 0, "Frozen preview differences: " + JsonSerializer.Serialize(differences));
    }

    private static async Task FinalPolicy()
    {
        using var temp = new TemporaryFiles(); var original = temp.Child("one"); var retarget = temp.Child("two");
        await WriteNew(original, Encoding.UTF8.GetBytes("original\n")); await WriteNew(retarget, Encoding.UTF8.GetBytes("other\n"));
        try { _ = new EditPreview(temp.Root, temp.Root, null!); throw new InvalidOperationException("Missing policy was accepted."); }
        catch (ArgumentNullException) { }
        var operations = new Operations(); var probe = new Probe(); var deniedPolicy = new Policy(original, allow: false);
        var denied = await new EditPreview(temp.Root, temp.Root, deniedPolicy, operations, accessProbe: probe).ComputeAsync("one", [new("original", "changed")]);
        NativeFailure(denied, ToolFailureKind.Blocked); Equal(1, deniedPolicy.Actions.Count); Equal(0, probe.Calls); Equal(0, operations.ReadCalls);
        var invalidPolicy = new Policy(retarget);
        ToolActionTransform invalid = (_, action, _) => ValueTask.FromResult(action with { Target = retarget });
        var invalidResult = await new EditPreview(temp.Root, temp.Root, invalidPolicy, operations, accessProbe: probe, transforms: [invalid]).ComputeAsync("one", [new("original", "changed")]);
        NativeFailure(invalidResult, ToolFailureKind.InvalidArguments); Equal(0, invalidPolicy.Actions.Count); Equal(0, probe.Calls);
        PreparedToolAction? transformed = null;
        ToolActionTransform valid = (_, action, _) =>
        {
            var arguments = JsonNode.Parse(action.Arguments.ToString())!.AsObject(); arguments["path"] = retarget;
            arguments["edits"] = JsonSerializer.SerializeToNode(new[] { new { oldText = "other", newText = "OTHER" } });
            transformed = action with { Target = retarget, Arguments = JsonData.Parse(arguments.ToJsonString()) }; return ValueTask.FromResult(transformed);
        };
        var finalPolicy = new Policy(retarget);
        var accepted = await new EditPreview(temp.Root, temp.Root, finalPolicy, operations, accessProbe: probe, transforms: [valid]).ComputeAsync("one", [new("original", "changed")]);
        Fulfilled(accepted); Equal("-1 other\n+1 OTHER", accepted.Preview!.Value.GetProperty("diff").GetString());
        Check(ReferenceEquals(transformed, finalPolicy.Actions.Single()), "Policy did not receive the immutable transformed action.");
        Equal(EditPreview.AuthorityName, finalPolicy.Actions.Single().ToolName); Equal(EditPreview.Operation, finalPolicy.Actions.Single().Operation);
        Equal(PreparedToolActionKind.Path, finalPolicy.Actions.Single().Kind); Equal(retarget, operations.ReadPaths.Single()); Equal(retarget, probe.Paths.Single());
        var blockedPolicy = new Policy(original);
        var blocked = await new EditPreview(temp.Root, temp.Root, blockedPolicy, operations, accessProbe: probe, transforms: [valid]).ComputeAsync("one", [new("original", "changed")]);
        NativeFailure(blocked, ToolFailureKind.Blocked); Equal(retarget, blockedPolicy.Actions.Single().Target); Equal(1, operations.ReadCalls);
        var hook = await new EditPreview(temp.Root, temp.Root, finalPolicy, operations, accessProbe: probe,
            transforms: [(_, _, _) => throw new IOException("PRIVATE-TRANSFORM")]).ComputeAsync("one", [new("original", "changed")]);
        NativeFailure(hook, ToolFailureKind.HookError); Check(!hook.Invocation.Details.ToString().Contains("PRIVATE-TRANSFORM", StringComparison.Ordinal), "Callback text escaped.");
        Equal(0, operations.WriteCalls); Equal(0, operations.DirectoryCalls);
        Equal("original\n", await File.ReadAllTextAsync(original)); Equal("other\n", await File.ReadAllTextAsync(retarget));
    }

    private static async Task LocalReadAuthority()
    {
        using var temp = new TemporaryFiles(); var target = temp.Child("readonly"); var directory = temp.Child("directory");
        await WriteNew(target, Encoding.UTF8.GetBytes("original\n")); Directory.CreateDirectory(directory);
        var attributes = File.GetAttributes(target); string? writeCode = null;
        try
        {
            if (OperatingSystem.IsWindows()) File.SetAttributes(target, attributes | FileAttributes.ReadOnly);
            var operations = new Operations(); var result = await new EditPreview(temp.Root, temp.Root, new Policy(target), operations).ComputeAsync("readonly", [new("original", "changed")]);
            Fulfilled(result); Equal("-1 original\n+1 changed", result.Preview!.Value.GetProperty("diff").GetString()); Equal(1, operations.ReadCalls); Equal(0, operations.WriteCalls);
            Equal("original\n", await File.ReadAllTextAsync(target));
            if (OperatingSystem.IsWindows())
            {
                try { await new LocalFileAccessProbe().CheckAsync(target, FileAccessModes.Read | FileAccessModes.Write, CancellationToken.None); }
                catch (FileAccessProbeException error) { writeCode = error.Code; }
                Equal("EPERM", writeCode);
            }
            var directoryResult = await new EditPreview(temp.Root, temp.Root, new Policy(directory)).ComputeAsync("directory", [new("old", "new")]);
            Fulfilled(directoryResult); Equal("Cannot read file contents for edit preview.", directoryResult.Preview!.Value.GetProperty("error").GetString());
            PlatformEvidence = JsonSerializer.SerializeToElement(new { platform = OperatingSystem.IsWindows() ? "windows-libuv-metadata-R_OK-profile" : "unix-libc-real-id-R_OK-profile",
                actualReadonlyPreviewAllowed = OperatingSystem.IsWindows(), windowsWriteCode = writeCode, windowsAclChecked = false,
                unixPermissionDenialTested = false, sourcePlatformDifferentialClaimed = false });
        }
        finally { if (OperatingSystem.IsWindows()) File.SetAttributes(target, attributes); }
    }

    private static async Task CancellationSettlement()
    {
        using var temp = new TemporaryFiles(); var target = temp.Child("file"); await WriteNew(target, Encoding.UTF8.GetBytes("original\n"));
        foreach (var stage in new[] { "canonical", "access", "read" })
        foreach (var failAfterCancellation in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource(); var entered = Gate(); var release = Gate(); var cleaning = Gate(); var cleanup = Gate();
            var operations = new Operations(); var policy = new Policy(target); var probe = new Probe();
            async Task Wait()
            {
                entered.TrySetResult();
                try { await release.Task; if (failAfterCancellation) throw new IOException("PRIVATE-LATE-FAILURE"); }
                finally { cleaning.TrySetResult(); await cleanup.Task; }
            }
            if (stage == "canonical") operations.Canonical = async (path, _) => { await Wait(); return path; };
            if (stage == "access") probe = new Probe(async (_, _, _) => await Wait());
            if (stage == "read") operations.Read = async (_, _, _) => { await Wait(); return Encoding.UTF8.GetBytes("original\n"); };
            var active = new EditPreview(temp.Root, temp.Root, policy, operations, accessProbe: probe).ComputeAsync("file", [new("original", "changed")], cancellation.Token).AsTask();
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); cancellation.Cancel();
                Check(!active.IsCompleted, "Cancellation detached the unsettled owned callback."); release.TrySetResult();
                await cleaning.Task.WaitAsync(TimeSpan.FromSeconds(3)); Check(!active.IsCompleted, "Cancellation detached owned asynchronous cleanup.");
            }
            finally { release.TrySetResult(); cleanup.TrySetResult(); await active.WaitAsync(TimeSpan.FromSeconds(3)); }
            var outcome = await active.WaitAsync(TimeSpan.FromSeconds(3)); NativeFailure(outcome, ToolFailureKind.Canceled);
            Equal(stage == "canonical" ? 0 : 1, policy.Actions.Count); Equal(stage == "read" ? 1 : 0, operations.ReadCalls);
            Equal(0, operations.WriteCalls); Equal("original\n", await File.ReadAllTextAsync(target));
        }
    }

    private static async Task DisplayBudget()
    {
        var options = new DiffFormatterOptions(MaximumOutputCharacters: 9);
        var display = DiffFormatter.FormatDisplay("a\n", "A\n", options); Equal("-1 a\n+1 A", display.Diff); Equal(1, display.FirstChangedLine);
        try { _ = DiffFormatter.Format("a-file-name", "a\n", "A\n", options); throw new InvalidOperationException("Unified patch should exceed the independent limit."); }
        catch (EditPlanException error) { Equal(EditPlanFailure.ResourceLimit, error.Failure); }
        using var temp = new TemporaryFiles(); var target = temp.Child("long-file-label"); await WriteNew(target, Encoding.UTF8.GetBytes("a\n"));
        var operations = new Operations(); var preview = new EditPreview(temp.Root, temp.Root, new Policy(target), operations, new(DiffOptions: options));
        var result = await preview.ComputeAsync("long-file-label", [new("a", "A")]); Fulfilled(result);
        Equal(display.Diff, result.Preview!.Value.GetProperty("diff").GetString()); Equal(1, result.Preview.Value.GetProperty("firstChangedLine").GetInt32());
        Equal(1, operations.ReadCalls); Equal(0, operations.WriteCalls); Equal("a\n", await File.ReadAllTextAsync(target));
    }

    private static async Task BoundsAndPrivacy()
    {
        using var temp = new TemporaryFiles(); var target = temp.Child("file"); await WriteNew(target, Encoding.UTF8.GetBytes("a\n"));
        var invalidPolicy = new Policy(target); var invalidOperations = new Operations(); var invalidProbe = new Probe();
        var limited = new EditPreview(temp.Root, temp.Root, invalidPolicy, invalidOperations, new(MaximumEdits: 1), invalidProbe);
        foreach (var edits in new ImmutableArray<TextEdit>[] { default, [new("a", "A"), new("b", "B")], [new("\ud800", "x")] })
            NativeFailure(await limited.ComputeAsync("file", edits), ToolFailureKind.InvalidArguments);
        Equal(0, invalidPolicy.Actions.Count); Equal(0, invalidProbe.Calls); Equal(0, invalidOperations.ReadCalls);
        foreach (var sample in new (byte[] Bytes, EditPreviewOptions Options, TextEdit Edit, string Error)[]
        {
            (Encoding.UTF8.GetBytes("123456789"), new(MaximumReadBytes: 8), new("1", "a"), new FileToolException(FileToolFailure.ResourceLimit).Message),
            (Encoding.UTF8.GetBytes("a\n"), new(MaximumPlanCharacters: 2), new("a", "long"), "Edit plan exceeds a configured logical limit."),
            (Encoding.UTF8.GetBytes("a\nb\n"), new(DiffOptions: new(MaximumLines: 1)), new("a", "A"), "Edit plan exceeds a configured logical limit."),
            (Encoding.UTF8.GetBytes("a\n"), new(DiffOptions: new(MaximumOutputCharacters: 8)), new("a", "A"), "Edit plan exceeds a configured logical limit.")
        })
        {
            var operations = new Operations { Read = (_, _, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(sample.Bytes) };
            var outcome = await new EditPreview(temp.Root, temp.Root, new Policy(target), operations, sample.Options).ComputeAsync("file", [sample.Edit]);
            Fulfilled(outcome); Equal(sample.Error, outcome.Preview!.Value.GetProperty("error").GetString()); Equal(0, operations.WriteCalls);
        }
        // Source computeEditsDiff reads with readFile(path, "utf-8"): undecodable bytes preview as U+FFFD; NUL and control characters are text.
        foreach (var (bytes, edit, line) in new (byte[], TextEdit, string)[] { ([0xFF, 0xFE, 0x61], new("a", "A"), "+1 \ufffd\ufffdA"), ([0x61, 0x00], new("a", "\0"), "+1 \0\0") })
        {
            var operations = new Operations { Read = (_, _, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(bytes) };
            var outcome = await new EditPreview(temp.Root, temp.Root, new Policy(target), operations).ComputeAsync("file", [edit]);
            Fulfilled(outcome); Check(outcome.Preview!.Value.TryGetProperty("diff", out var diff) && diff.GetString()!.Contains(line, StringComparison.Ordinal),
                "lossy preview: " + outcome.Preview.Value.GetRawText());
        }
        var unicode = "\ufeffuntouched\r\nvalue=\uff26\uff4f\uff4f-cafe\u0301 \ud83d\ude00\rEND";
        var unicodeBytes = Encoding.UTF8.GetBytes(unicode); var unicodeOperations = new Operations { Read = (_, _, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(unicodeBytes) };
        var unicodeResult = await new EditPreview(temp.Root, temp.Root, new Policy(target), unicodeOperations).ComputeAsync("file", [new("value=Foo-caf\u00e9 \ud83d\ude00", "value=Bar \ud83d\ude00")]);
        Fulfilled(unicodeResult); Equal(2, unicodeResult.Preview!.Value.GetProperty("firstChangedLine").GetInt32());
        Check(unicodeResult.Preview.Value.GetProperty("diff").GetString()!.Contains("value=Bar \ud83d\ude00", StringComparison.Ordinal), "Supplementary Unicode replacement is absent.");
        Equal(unicode, Encoding.UTF8.GetString(unicodeBytes));
        var failedRead = new Operations { Read = (_, _, _) => throw new IOException("PRIVATE-READ-PAYLOAD") };
        var readResult = await new EditPreview(temp.Root, temp.Root, new Policy(target), failedRead).ComputeAsync("file", [new("a", "A")]);
        Fulfilled(readResult); Equal("Cannot read file contents for edit preview.", readResult.Preview!.Value.GetProperty("error").GetString());
        foreach (var coded in new[] { false, true })
        {
            var operations = new Operations(); var probe = new Probe((_, _, _) => throw (coded ? (Exception)new FileAccessProbeException("EACCES") : new IOException("PRIVATE-ACCESS-PAYLOAD")));
            var outcome = await new EditPreview(temp.Root, temp.Root, new Policy(target), operations, accessProbe: probe).ComputeAsync("file", [new("a", "A")]);
            Fulfilled(outcome); Equal(coded ? "Could not edit file: file. Error code: EACCES." : "Could not edit file: file. Access probe failed.", outcome.Preview!.Value.GetProperty("error").GetString());
            Equal(0, operations.ReadCalls); Check(!outcome.Invocation.Details.ToString().Contains("PRIVATE-", StringComparison.Ordinal), "Private exception payload escaped.");
        }
        Equal("a\n", await File.ReadAllTextAsync(target));
    }

    private static async Task ConcurrentWriter()
    {
        using var temp = new TemporaryFiles(); var target = temp.Child("file"); await WriteNew(target, Encoding.UTF8.GetBytes("old\n"));
        var entered = Gate(); var release = Gate(); var writerOperations = new Operations(); var queue = Queue(writerOperations);
        writerOperations.Write = async (path, bytes, token) => { entered.TrySetResult(); await release.Task; await new LocalFileOperations().WriteAsync(path, bytes, token); };
        var tools = new ReadWriteTools(temp.Root, temp.Root, writerOperations, mutationQueue: queue);
        var writePolicy = new Policy(target, expectedOperation: "write");
        var writer = tools.CreateInvoker(writePolicy).ExecuteAsync(Invocation("write", JsonData.Parse("{\"path\":\"file\",\"content\":\"written\\n\"}")), CancellationToken.None).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); Equal(1, queue.Snapshot.RegisteredOperations);
            var operations = new Operations(); var before = await Snapshot(target);
            var outcome = await new EditPreview(temp.Root, temp.Root, new Policy(target), operations).ComputeAsync("file", [new("old", "preview")]).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            Fulfilled(outcome); Equal("-1 old\n+1 preview", outcome.Preview!.Value.GetProperty("diff").GetString());
            Check(!writer.IsCompleted && SameBytes(before, await Snapshot(target)), "Preview waited on or changed the queued writer state.");
            Equal(1, queue.Snapshot.RegisteredOperations); Equal(0, operations.WriteCalls);
        }
        finally { release.TrySetResult(); await writer.WaitAsync(TimeSpan.FromSeconds(3)); }
        var result = await writer.WaitAsync(TimeSpan.FromSeconds(3)); Check(!result.IsError, "Owned queued writer failed.");
        Equal(0, queue.Snapshot.RegisteredOperations); Equal("written\n", await File.ReadAllTextAsync(target));
    }

    private static JsonDocument ReadPinned(string path, string expected, int maximum)
    {
        var bytes = File.ReadAllBytes(path); Check(bytes.Length <= maximum, "Fixture exceeds its read budget.");
        Equal(expected, Convert.ToHexStringLower(SHA256.HashData(bytes))); return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
    }
    private static async Task WriteNew(string path, byte[] bytes)
    { await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous); await stream.WriteAsync(bytes); }
    private static async Task<JsonElement?> Snapshot(string path)
    {
        if (!File.Exists(path)) return null; var bytes = await File.ReadAllBytesAsync(path);
        return JsonSerializer.SerializeToElement(new { bytes = bytes.Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), base64 = Convert.ToBase64String(bytes), utf8 = Encoding.UTF8.GetString(bytes) });
    }
    private static bool SameBytes(JsonElement? before, JsonElement? after) => before is null ? after is null : after is not null && before.Value.GetProperty("base64").GetString() == after.Value.GetProperty("base64").GetString();
    private static JsonElement Element(JsonElement? value) => value ?? JsonSerializer.SerializeToElement<object?>(null);
    private static object Action(PreparedToolAction action) => new { action.ToolName, action.Operation, kind = action.Kind.ToString(), action.Target,
        arguments = action.Arguments.Value.Clone(), action.CommandArguments, action.WorkingDirectory, action.Environment };
    private static object NativeResult(ToolResult result) => new { result.IsError, result.Terminate,
        failure = result.Failure is null ? null : new { kind = result.Failure.Kind.ToString(), result.Failure.Message },
        content = result.Content.Select(value => new { value.Text, extraProperties = value.ExtraProperties }).ToArray(), details = result.Details.Value.Clone(),
        structuredContent = result.StructuredContent?.Value.Clone() };
    private static void Fulfilled(EditPreviewOutcome outcome)
    { Check(outcome.Preview is not null && !outcome.Invocation.IsError && outcome.Invocation.Failure is null && !outcome.Invocation.Terminate && outcome.Invocation.Content.IsEmpty && outcome.Invocation.StructuredContent is null, "Expected a source-shaped fulfilled preview."); Equal(outcome.Preview!.ToString(), outcome.Invocation.Details.ToString()); }
    private static void NativeFailure(EditPreviewOutcome outcome, ToolFailureKind kind)
    { Check(outcome.Preview is null && outcome.Invocation.IsError, "Native mediation failure was conflated with a fulfilled preview error."); Equal(kind, outcome.Invocation.Failure?.Kind); }
    private static void Compare(string path, JsonElement expected, JsonElement actual, List<object> differences)
    {
        if (expected.ValueKind != actual.ValueKind) { differences.Add(new { path, expected = expected.Clone(), actual = actual.Clone() }); return; }
        if (expected.ValueKind == JsonValueKind.Object)
        {
            var left = expected.EnumerateObject().ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal);
            var right = actual.EnumerateObject().ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal);
            if (!left.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(right.Keys)) { differences.Add(new { path, expected = expected.Clone(), actual = actual.Clone() }); return; }
            foreach (var entry in left) Compare(path + "." + entry.Key, entry.Value, right[entry.Key], differences);
        }
        else if (expected.ValueKind == JsonValueKind.Array)
        {
            if (expected.GetArrayLength() != actual.GetArrayLength()) { differences.Add(new { path, expected = expected.Clone(), actual = actual.Clone() }); return; }
            for (var index = 0; index < expected.GetArrayLength(); index++) Compare(path + "[" + index + "]", expected[index], actual[index], differences);
        }
        else if (expected.ValueKind == JsonValueKind.String ? expected.GetString() != actual.GetString() : expected.GetRawText() != actual.GetRawText())
            differences.Add(new { path, expected = expected.Clone(), actual = actual.Clone() });
    }
    private sealed class Operations : IFileOperations
    {
        private readonly LocalFileOperations _local = new(); public int CanonicalCalls; public int ReadCalls; public int WriteCalls; public int DirectoryCalls;
        public Func<string, CancellationToken, ValueTask<string>>? Canonical;
        public Func<string, int, CancellationToken, ValueTask<ReadOnlyMemory<byte>>>? Read;
        public Func<string, ReadOnlyMemory<byte>, CancellationToken, ValueTask>? Write;
        public readonly ConcurrentQueue<string> ReadPaths = new();
        public ValueTask<bool> ExistsAsync(string path, CancellationToken token) => _local.ExistsAsync(path, token);
        public ValueTask<string> CanonicalizeAsync(string path, CancellationToken token) { Interlocked.Increment(ref CanonicalCalls); return Canonical?.Invoke(path, token) ?? _local.CanonicalizeAsync(path, token); }
        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(string path, int maximum, CancellationToken token) { Interlocked.Increment(ref ReadCalls); ReadPaths.Enqueue(path); return Read?.Invoke(path, maximum, token) ?? _local.ReadAsync(path, maximum, token); }
        public ValueTask CreateDirectoryAsync(string path, CancellationToken token) { Interlocked.Increment(ref DirectoryCalls); return _local.CreateDirectoryAsync(path, token); }
        public ValueTask WriteAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken token) { Interlocked.Increment(ref WriteCalls); return Write?.Invoke(path, bytes, token) ?? _local.WriteAsync(path, bytes, token); }
    }
    private sealed class Probe(Func<string, FileAccessModes, CancellationToken, ValueTask>? check = null) : IFileAccessProbe
    {
        private readonly LocalFileAccessProbe _local = new(); public int Calls; public readonly ConcurrentQueue<string> Paths = new(); public readonly ConcurrentQueue<FileAccessModes> Modes = new();
        public ValueTask CheckAsync(string path, FileAccessModes modes, CancellationToken token)
        { Interlocked.Increment(ref Calls); Paths.Enqueue(path); Modes.Enqueue(modes); return check?.Invoke(path, modes, token) ?? _local.CheckAsync(path, modes, token); }
    }
    private sealed class Policy(string target, bool allow = true, string expectedOperation = EditPreview.Operation) : IToolActionPolicy
    {
        public readonly ConcurrentQueue<PreparedToolAction> Actions = new();
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Actions.Enqueue(action); return ValueTask.FromResult(new ToolActionAuthorization(allow && action.Target == Path.GetFullPath(target) && action.Kind == PreparedToolActionKind.Path && action.Operation == expectedOperation)); }
    }
    private sealed class TemporaryFiles : IDisposable
    {
        private readonly string _parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        public string Root { get; } = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "pisharp-edit-preview-" + Guid.NewGuid().ToString("N"));
        public TemporaryFiles() => Directory.CreateDirectory(Root);
        public string Child(string path) => Path.GetFullPath(Path.Combine(Root, path));
        public void Dispose()
        {
            if (Path.GetDirectoryName(Root) != _parent || !Path.GetFileName(Root).StartsWith("pisharp-edit-preview-", StringComparison.Ordinal)) throw new InvalidOperationException("Refusing cleanup outside the owned preview root.");
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
    private static FileMutationQueue Queue(IFileOperations operations) => new(async (path, token) => { var key = await operations.CanonicalizeAsync(path, token); return OperatingSystem.IsWindows() ? key.ToUpperInvariant() : key; });
    private static ToolInvocation Invocation(string name, JsonData arguments)
    { var call = new ToolCallContent("owned-preview-test", name, arguments); return new(new AssistantMessage("fixture-api", "fixture-provider", "fixture-model", 0, [call], TokenUsage.Zero, StopReason.ToolUse), call, 0); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
}
