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

internal static class EditDifferentialTests
{
    private const string SourceSha = "d86654abb8862e201933517d6f1fce9f88dd117f";
    private const string InputSha = "fd07bb0eb503fdcd692822dbe2d6a51d0deae47b16c7a0e56ae7ea80788812f2";
    private const string ExpectedSha = "2b17786c53dc3f36e95e92920066aa64159d6d10ec1e25de2cfe110f91dcb4fb";
    private static string? _fixtureDirectory;
    public static JsonElement? Evidence { get; private set; }
    public static JsonElement? AccessEvidence { get; private set; }
    public static void Configure(string fixtureRoot)
    {
        if (!Path.IsPathFullyQualified(fixtureRoot)) throw new ArgumentException("An explicit absolute repository fixture root is required.");
        var directory = Path.Combine(Path.GetFullPath(fixtureRoot), "fixtures", "pi-v0.99.1", "edit");
        if (!Directory.Exists(directory)) throw new ArgumentException("The immutable edit fixture directory is absent.");
        _fixtureDirectory = directory;
    }
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("edit complete captured twelve-case prepared helper result and saved-byte comparison", CapturedCorpus),
        ("edit actual OS access existence readonly file directory and explicit platform profile", LocalAccess),
        ("edit access cancellation settles reservation and failure before any content read", ProbeCancellation),
        ("edit empty prepared array exact error and generic malformed structural admission", EmptyAndStructural)
    ];

    private static async Task CapturedCorpus()
    {
        var directory = _fixtureDirectory ?? throw new InvalidOperationException("Configure immutable edit fixtures before running the comparison.");
        using var input = ReadPinned(Path.Combine(directory, "core.input.json"), InputSha, 1_048_576);
        using var expected = ReadPinned(Path.Combine(directory, "core.expected.json"), ExpectedSha, 2_097_152);
        Equal(SourceSha, input.RootElement.GetProperty("sourceSha").GetString()); Equal(SourceSha, expected.RootElement.GetProperty("sourceSha").GetString());
        Equal("authored-edit-input-and-owned-filesystem-layout", input.RootElement.GetProperty("kind").GetString());
        Equal("captured-unchanged-edit-oracle", expected.RootElement.GetProperty("kind").GetString());
        var inputs = input.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        var sources = expected.RootElement.GetProperty("observations").GetProperty("cases").EnumerateArray().ToArray();
        Equal(12, inputs.Length); Equal(inputs.Length, sources.Length);
        using var temp = new TemporaryFiles(); Directory.CreateDirectory(temp.Child("files"));
        var operations = new Operations(); var probe = new Probe(); var queue = Queue(operations);
        var tool = new EditTool(temp.Root, temp.Root, queue, operations, accessProbe: probe);
        var reports = new List<object>(); var differences = new List<object>(); var successes = 0; var failures = 0;
        for (var index = 0; index < inputs.Length; index++)
        {
            var authored = inputs[index]; var source = sources[index]; var id = authored.GetProperty("caseId").GetString()!;
            Equal(id, source.GetProperty("caseId").GetString());
            var path = authored.GetProperty("path").GetString()!; Equal(path, source.GetProperty("path").GetString());
            Check(path.StartsWith("files/", StringComparison.Ordinal) && !path.Contains("..", StringComparison.Ordinal) && !path.Contains('\\') && !path.Contains(':') && !path.Contains('\0'), "Only confined literal fixture paths are admitted.");
            var target = temp.Child(path); var missing = authored.TryGetProperty("missing", out var absent) && absent.GetBoolean();
            if (!missing)
            {
                var bytes = Encoding.UTF8.GetBytes(authored.GetProperty("utf8").GetString()!);
                await using var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
                await stream.WriteAsync(bytes);
            }
            var before = await Snapshot(target); var args = JsonNode.Parse(authored.GetProperty("arguments").GetRawText())!.AsObject();
            args["path"] = path; var owned = JsonData.Parse(args.ToJsonString()); var initialArguments = owned.ToString();
            var invocation = Invocation(owned); var prepared = await tool.PrepareAsync(invocation, CancellationToken.None);
            Equal(initialArguments, owned.ToString());
            var publicPrepared = JsonSerializer.SerializeToElement(new { path = prepared.Arguments.Value.GetProperty("displayPath").GetString(),
                edits = prepared.Arguments.Value.GetProperty("edits").Clone() });
            Compare(id + ".preparation.prepared", source.GetProperty("preparation").GetProperty("prepared"), publicPrepared, differences);
            Compare(id + ".preparation.suppliedBefore", source.GetProperty("preparation").GetProperty("suppliedBefore"), owned.Value, differences);
            Check(source.GetProperty("preparation").GetProperty("ownUndefinedPaths").GetArrayLength() == 0, "Prepared fixture has unhandled undefined fields.");
            var helper = Helpers(authored, publicPrepared, source.GetProperty("helperObservations"), id, differences);
            var reads = operations.ReadCalls; var writes = operations.WriteCalls; var probes = probe.Calls;
            var policy = new Policy(target); var result = await tool.CreateInvoker(policy).ExecuteAsync(invocation, CancellationToken.None);
            var portable = ResultObservation(result);
            Compare(id + ".result", source.GetProperty("result"), portable, differences);
            var after = await Snapshot(target);
            var filesystem = JsonSerializer.SerializeToElement(new { before, after,
                byteIdentical = before is null ? after is null : after is not null && before.Value.GetProperty("base64").GetString() == after.Value.GetProperty("base64").GetString() });
            Compare(id + ".filesystem", source.GetProperty("filesystem"), filesystem, differences);
            Equal(1, policy.Actions.Count); Equal(0, queue.Snapshot.RegisteredOperations);
            if (result.IsError) failures++; else successes++;
            var counts = new { accessCalls = probe.Calls - probes, readCalls = operations.ReadCalls - reads, writeCalls = operations.WriteCalls - writes };
            if (id == "empty-edits") { Equal(0, counts.accessCalls); Equal(0, counts.readCalls); Equal(0, counts.writeCalls); }
            else if (missing) { Equal(1, counts.accessCalls); Equal(0, counts.readCalls); Equal(0, counts.writeCalls); }
            else if (result.IsError) { Equal(1, counts.accessCalls); Equal(1, counts.readCalls); Equal(0, counts.writeCalls); }
            else { Equal(1, counts.accessCalls); Equal(2, counts.readCalls); Equal(1, counts.writeCalls); }
            reports.Add(new { caseId = id, sourceObservation = source.Clone(), native = new
            {
                ownedSuppliedBefore = owned.Value.Clone(), ownedSuppliedAfter = owned.Value.Clone(), publicPrepared,
                finalAction = new { prepared.ToolName, prepared.Operation, prepared.Kind, prepared.Target, arguments = prepared.Arguments.Value.Clone(),
                    prepared.CommandArguments, prepared.WorkingDirectory, prepared.Environment }, helpers = helper,
                portableResult = portable, actualResult = NativeResult(result), filesystem, counts,
                preview = new { available = false, requiredFollowup = "A public native read-only edit-preview orchestration API is not implemented in this candidate." }
            } });
        }
        Equal(5, successes); Equal(7, failures);
        Evidence = JsonSerializer.SerializeToElement(new { schemaVersion = 1, sourceSha = SourceSha,
            inputSha256 = InputSha, expectedSha256 = ExpectedSha, corpusCases = inputs.Length, successes, failures,
            compared = "Complete portable tool results, all failure name/message contracts, prepared public fields, available pure helpers and exact file snapshots; no diff/patch/string normalization.",
            mapping = "Typed native TextContent maps to source type:text; native InvalidArguments/ExecutionError failures map to source generic Error name. Native envelope/effect metadata retained separately. Owned input mutation and missing preview seam remain explicit observations.",
            cases = reports, differences, phaseAcceptanceClaimed = false });
        Check(differences.Count == 0, "Frozen edit comparison differences: " + JsonSerializer.Serialize(differences));
    }

    private static JsonElement? Helpers(JsonElement authored, JsonElement prepared, JsonElement source, string id, List<object> differences)
    {
        if (source.ValueKind == JsonValueKind.Null) return null;
        var raw = authored.GetProperty("utf8").GetString()!; var bom = raw.StartsWith('\ufeff') ? "\ufeff" : ""; var text = bom.Length == 0 ? raw : raw[1..];
        var normalized = EditMatcher.NormalizeToLF(text); var ending = EditMatcher.DetectLineEnding(text);
        var normalization = JsonSerializer.SerializeToElement(new { splitBom = new { bom, text }, detectedLineEnding = ending,
            normalized, fuzzyView = EditMatcher.NormalizeForFuzzyMatch(normalized) });
        Compare(id + ".helpers.normalization", source.GetProperty("normalization"), normalization, differences);
        var edits = prepared.GetProperty("edits").EnumerateArray().Select(edit => new TextEdit(edit.GetProperty("oldText").GetString()!, edit.GetProperty("newText").GetString()!)).ToImmutableArray();
        JsonElement matching; JsonElement? generated = null; JsonElement? find = null;
        try
        {
            var plan = EditPlan.Create(raw, edits, prepared.GetProperty("path").GetString()!);
            matching = Fulfilled(new { baseContent = plan.BaseContent, newContent = plan.NewContent });
            var formatted = DiffFormatter.Format(prepared.GetProperty("path").GetString()!, plan.BaseContent, plan.NewContent);
            generated = JsonSerializer.SerializeToElement(new { display = Fulfilled(new { diff = formatted.Diff, firstChangedLine = formatted.FirstChangedLine }),
                patch = Fulfilled(formatted.Patch), restoredLineEndings = EditMatcher.RestoreLineEndings(plan.NewContent, ending) });
        }
        catch (EditPlanException error) { matching = Rejected(error.Message); }
        if (!edits.IsEmpty)
        {
            var value = EditMatcher.Find(normalized, EditMatcher.NormalizeToLF(edits[0].OldText));
            find = Fulfilled(new { found = value.Found, index = value.Index, matchLength = value.MatchLength,
                usedFuzzyMatch = value.UsedFuzzyMatch, contentForReplacement = value.ContentForReplacement });
        }
        var actual = JsonSerializer.SerializeToElement(new { normalization, matching, fuzzyFind = find, generated });
        Compare(id + ".helperObservations", source, actual, differences);
        return actual;
    }

    private static async Task LocalAccess()
    {
        using var temp = new TemporaryFiles(); var target = temp.Child("read-only.txt"); var directory = temp.Child("directory");
        await File.WriteAllBytesAsync(target, Encoding.UTF8.GetBytes("original\n")); Directory.CreateDirectory(directory);
        var local = new LocalFileAccessProbe(); await local.CheckAsync(target, FileAccessModes.Read | FileAccessModes.Write, CancellationToken.None);
        Equal("ENOENT", await AccessCode(local, temp.Child("absent"), FileAccessModes.Read | FileAccessModes.Write));
        string? readonlyCode = null; string? unixPermissionCode = null; var platform = OperatingSystem.IsWindows() ? "windows-libuv-metadata-profile" : "unix-libc-real-id-profile";
        if (OperatingSystem.IsWindows())
        {
            var fileAttributes = File.GetAttributes(target); var directoryAttributes = File.GetAttributes(directory);
            try
            {
                File.SetAttributes(target, fileAttributes | FileAttributes.ReadOnly);
                await local.CheckAsync(target, FileAccessModes.Read, CancellationToken.None);
                readonlyCode = await AccessCode(local, target, FileAccessModes.Read | FileAccessModes.Write); Equal("EPERM", readonlyCode);
                File.SetAttributes(directory, directoryAttributes | FileAttributes.ReadOnly);
                await local.CheckAsync(directory, FileAccessModes.Read | FileAccessModes.Write, CancellationToken.None);
                var operations = new Operations(); var tool = new EditTool(temp.Root, temp.Root, Queue(operations), operations);
                var result = await Invoke(tool, Input("read-only.txt", "original", "changed"), new Policy(target));
                Equal("Could not edit file: read-only.txt. Error code: EPERM.", result.Failure?.Message);
                Equal(0, operations.ReadCalls); Equal(0, operations.WriteCalls);
                Equal("original\n", await File.ReadAllTextAsync(target));
            }
            finally { File.SetAttributes(target, fileAttributes); File.SetAttributes(directory, directoryAttributes); }
        }
        else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var mode = File.GetUnixFileMode(target);
            try
            {
                File.SetUnixFileMode(target, UnixFileMode.UserRead);
                unixPermissionCode = await AccessCode(local, target, FileAccessModes.Read | FileAccessModes.Write);
                Check(unixPermissionCode is null or "EACCES", "Unexpected real-ID libc access outcome.");
            }
            finally { File.SetUnixFileMode(target, mode); }
        }
        var nonDirectory = Path.Combine(target, "child");
        Equal(OperatingSystem.IsWindows() ? "ENOENT" : "ENOTDIR", await AccessCode(local, nonDirectory, FileAccessModes.Read | FileAccessModes.Write));
        AccessEvidence = JsonSerializer.SerializeToElement(new { platform, actualMissingError = "ENOENT", readonlyCode,
            unixPermissionCode, unixPermissionDenialObserved = unixPermissionCode == "EACCES",
            windowsAclChecked = false, sourcePlatformDifferentialClaimed = false });
    }

    private static async Task ProbeCancellation()
    {
        using var temp = new TemporaryFiles(); var target = temp.Child("file"); await File.WriteAllTextAsync(target, "original\n");
        foreach (var failureAfterCancellation in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource(); var entered = Gate(); var release = Gate(); var operations = new Operations();
            var probe = new Probe(async (_, _, _) => { entered.TrySetResult(); await release.Task; if (failureAfterCancellation) throw new FileAccessProbeException("EACCES"); });
            var queue = Queue(operations); var tool = new EditTool(temp.Root, temp.Root, queue, operations, accessProbe: probe);
            var active = Invoke(tool, Input("file", "original", "changed"), new Policy(target), cancellation.Token);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); cancellation.Cancel();
                Check(!active.IsCompleted && queue.Snapshot.RegisteredOperations == 1, "Cancellation released an unsettled access callback reservation.");
                Equal(0, operations.ReadCalls); Equal(0, operations.WriteCalls);
            }
            finally { release.TrySetResult(); }
            var result = await active; Equal(ToolFailureKind.Canceled, result.Failure?.Kind);
            Equal(0, queue.Snapshot.RegisteredOperations); Equal(0, operations.ReadCalls); Equal(0, operations.WriteCalls);
            Equal("original\n", await File.ReadAllTextAsync(target));
        }
        var deniedOperations = new Operations(); var deniedProbe = new Probe((_, _, _) => throw new FileAccessProbeException("EACCES"));
        var deniedTool = new EditTool(temp.Root, temp.Root, Queue(deniedOperations), deniedOperations, accessProbe: deniedProbe);
        var denied = await Invoke(deniedTool, Input("file", "original", "changed"), new Policy(target));
        Equal("Could not edit file: file. Error code: EACCES.", denied.Failure?.Message); Equal(0, deniedOperations.ReadCalls); Equal(0, deniedOperations.WriteCalls);
        var rawProbe = new Probe((_, _, _) => throw new IOException("PRIVATE-CALLBACK-TEXT"));
        var rawTool = new EditTool(temp.Root, temp.Root, Queue(deniedOperations), deniedOperations, accessProbe: rawProbe);
        var raw = await Invoke(rawTool, Input("file", "original", "changed"), new Policy(target));
        Equal("Could not edit file: file. Access probe failed.", raw.Failure?.Message);
        Check(!raw.Content.Any(value => value.Text.Contains("PRIVATE-CALLBACK-TEXT", StringComparison.Ordinal)), "Arbitrary access callback text escaped.");
    }

    private static async Task EmptyAndStructural()
    {
        using var temp = new TemporaryFiles(); var target = temp.Child("file"); await File.WriteAllTextAsync(target, "original\n");
        var operations = new Operations(); var probe = new Probe(); var queue = Queue(operations); var tool = new EditTool(temp.Root, temp.Root, queue, operations, accessProbe: probe);
        var empty = JsonData.Parse("{\"path\":\"file\",\"edits\":[]}"); var policy = new Policy(target);
        var prepared = await tool.PrepareAsync(Invocation(empty), CancellationToken.None); Equal(0, prepared.Arguments.Value.GetProperty("edits").GetArrayLength());
        var result = await Invoke(tool, empty, policy); Equal(ToolFailureKind.InvalidArguments, result.Failure?.Kind);
        Equal("Edit tool input is invalid. edits must contain at least one replacement.", result.Failure?.Message);
        Equal(1, policy.Actions.Count); Equal(0, probe.Calls); Equal(0, operations.ReadCalls); Equal(0, operations.WriteCalls); Equal(0, queue.Snapshot.RegisteredOperations);
        try { _ = EditPlan.Create("original\n", [], "file"); throw new InvalidOperationException("Empty pure plan should reject no change."); }
        catch (EditPlanException error) { Equal(EditPlanFailure.NoChange, error.Failure); Equal("No changes made to file. The replacements produced identical content.", error.Message); }
        foreach (var text in new[] { "{}", "{\"path\":\"file\"}", "{\"path\":\"file\",\"edits\":1}", "{\"path\":\"file\",\"edits\":[{\"oldText\":1,\"newText\":\"x\"}]}" })
        {
            var malformedPolicy = new Policy(target); var malformed = await Invoke(tool, JsonData.Parse(text), malformedPolicy);
            Equal(ToolFailureKind.InvalidArguments, malformed.Failure?.Kind); Equal("Invalid or unsupported final tool action input.", malformed.Failure?.Message);
            Equal(0, malformedPolicy.Actions.Count);
        }
        Equal(0, probe.Calls); Equal(0, operations.ReadCalls); Equal(0, operations.WriteCalls);
    }

    private static JsonDocument ReadPinned(string path, string expected, int maximum)
    {
        var info = new FileInfo(path); Check(info.Length <= maximum, "Fixture resource bound exceeded.");
        var bytes = File.ReadAllBytes(path); Equal(expected, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        return JsonDocument.Parse(bytes);
    }
    private static async Task<JsonElement?> Snapshot(string path)
    {
        if (!File.Exists(path)) return null;
        var bytes = await File.ReadAllBytesAsync(path);
        return JsonSerializer.SerializeToElement(new { bytes = bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            base64 = Convert.ToBase64String(bytes), utf8 = Encoding.UTF8.GetString(bytes) });
    }
    private static JsonElement Fulfilled(object value) => JsonSerializer.SerializeToElement(new { status = "fulfilled", value, ownUndefinedPaths = Array.Empty<string>() });
    private static JsonElement Rejected(string message) => JsonSerializer.SerializeToElement(new { status = "rejected", error = new { name = "Error", message, ownProperties = new { } } });
    private static JsonElement ResultObservation(ToolResult result)
    {
        if (result.IsError)
        {
            Check(result.Failure is { Kind: ToolFailureKind.InvalidArguments or ToolFailureKind.ExecutionError }, "Unexpected portable failure category.");
            Check(result.Content.Length == 1 && result.Content[0].Text == result.Failure!.Message, "Failure content and typed message differ.");
            return Rejected(result.Failure!.Message);
        }
        Check(result.Failure is null && !result.Terminate && result.Content.All(value => value.ExtraProperties is null), "Unmapped native success fields.");
        return Fulfilled(new { content = result.Content.Select(value => new { type = "text", text = value.Text }).ToArray(), details = result.Details.Value.Clone() });
    }
    private static JsonElement NativeResult(ToolResult result) => JsonSerializer.SerializeToElement(new
    {
        result.IsError, result.Terminate, failure = result.Failure is null ? null : new { kind = result.Failure.Kind.ToString(), result.Failure.Message },
        content = result.Content.Select(value => new { value.Text, extraProperties = value.ExtraProperties }).ToArray(), details = result.Details.Value.Clone()
    });
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
        private readonly LocalFileOperations _local = new(); public int ReadCalls; public int WriteCalls;
        public ValueTask<bool> ExistsAsync(string path, CancellationToken token) => _local.ExistsAsync(path, token);
        public ValueTask<string> CanonicalizeAsync(string path, CancellationToken token) => _local.CanonicalizeAsync(path, token);
        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(string path, int maximum, CancellationToken token) { Interlocked.Increment(ref ReadCalls); return _local.ReadAsync(path, maximum, token); }
        public ValueTask CreateDirectoryAsync(string path, CancellationToken token) => _local.CreateDirectoryAsync(path, token);
        public ValueTask WriteAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken token) { Interlocked.Increment(ref WriteCalls); return _local.WriteAsync(path, bytes, token); }
    }
    private sealed class Probe(Func<string, FileAccessModes, CancellationToken, ValueTask>? check = null) : IFileAccessProbe
    {
        private readonly LocalFileAccessProbe _local = new(); public int Calls;
        public ValueTask CheckAsync(string path, FileAccessModes modes, CancellationToken token)
        { Interlocked.Increment(ref Calls); return check?.Invoke(path, modes, token) ?? _local.CheckAsync(path, modes, token); }
    }
    private sealed class Policy(string target) : IToolActionPolicy
    {
        public readonly ConcurrentQueue<PreparedToolAction> Actions = new();
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Actions.Enqueue(action); return ValueTask.FromResult(new ToolActionAuthorization(action.Target == Path.GetFullPath(target))); }
    }
    private sealed class TemporaryFiles : IDisposable
    {
        private readonly string _parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        public string Root { get; } public TemporaryFiles() { Root = Path.Combine(_parent, "pisharp-edit-differential-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root); }
        public string Child(string path) => Path.GetFullPath(Path.Combine(Root, path));
        public void Dispose()
        {
            if (Path.GetDirectoryName(Root) != _parent || !Path.GetFileName(Root).StartsWith("pisharp-edit-differential-", StringComparison.Ordinal)) throw new InvalidOperationException("Refusing cleanup outside the owned comparison root.");
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
    private static FileMutationQueue Queue(IFileOperations operations) => new(async (path, token) =>
    { var key = await operations.CanonicalizeAsync(path, token); return OperatingSystem.IsWindows() ? key.ToUpperInvariant() : key; });
    private static ToolInvocation Invocation(JsonData arguments)
    { var call = new ToolCallContent("edit-differential-call", "edit", arguments); return new(new AssistantMessage("fixture-api", "fixture-provider", "fixture-model", 0, [call], TokenUsage.Zero, StopReason.ToolUse), call, 0); }
    private static JsonData Input(string path, string oldText, string newText) => JsonData.Parse(JsonSerializer.Serialize(new { path, edits = new[] { new { oldText, newText } } }));
    private static async Task<ToolResult> Invoke(EditTool tool, JsonData input, Policy policy, CancellationToken token = default) => await tool.CreateInvoker(policy).ExecuteAsync(Invocation(input), token);
    private static async Task<string?> AccessCode(IFileAccessProbe probe, string path, FileAccessModes modes)
    { try { await probe.CheckAsync(path, modes, CancellationToken.None); return null; } catch (FileAccessProbeException error) { return error.Code; } }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
}
