using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Agent.Tools;
using PiSharp.Contracts;
using PiSharp.Tools.Files;
using PiSharp.Tools.Processes;

internal static class GrepToolTests
{
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("grep exact pinned argv explicit cwd environment and option separator", Arguments),
        ("grep actual adapter and fake process preserve event order file paths CRLF and empty details", Formatting),
        ("grep exact match line and byte limits retain source notices and details", Limits),
        ("grep unsupported context numeric text and malformed JSON reject explicitly", Unsupported),
        ("grep final transformed policy authorizes exact target and denial prevents effects", Policy),
        ("grep exit errors lexical canonical and undisplayed path escapes refuse results", Errors),
        ("grep mandatory admission and changed image refuse before process or spill acquisition", Admission),
        ("grep incomplete original capture cleanup diagnostics and clipping refuse after spill join", Receipts),
        ("grep original operation and spill failure identities survive cancellation and joins", FailureOrdering),
        ("grep cancellation joins held original process then spill and observes successful cleanup cancellation", Cancellation),
        ("grep complete fake process joins actual exclusive local spill write and removal", LocalSpill),
        ("grep positive context repeats overlapping blocks caches per invocation and normalizes Unicode CRLF", ContextFormatting),
        ("grep positive context read errors are cached unavailable markers and empty files remain readable", ContextReadFailures),
        ("grep positive context exact source line byte truncation and total metadata", ContextTruncation),
        ("grep positive context final policy capability absence and transformed options precede effects", ContextPolicy),
        ("grep positive context complete receipt and immediately rechecked containment precede reads", ContextContainment),
        ("grep positive context rejects numeric file cache line and formatted output resource bounds", ContextBounds),
        ("grep positive context cancellation directly joins held original read and cleanup", ContextCancellation)
    ];
    private static async Task Arguments()
    {
        using var f = new Fixture(); var executor = f.Executor();
        await executor.GrepAsync(new("--leading (a|b)", f.Root, "*.cs", true, true, 7), default);
        var request = f.Runner.Last!;
        Check(request.Arguments.SequenceEqual(["--json", "--line-number", "--color=never", "--hidden", "--ignore-case", "--fixed-strings", "--glob", "*.cs", "--", "--leading (a|b)", f.Root]), "Pinned grep argv or '--' separation changed.");
        Check(request.Executable == f.Binary.Executable && request.WorkingDirectory == f.Root && request.Environment.Count == 0 && request.TimeoutSeconds is null && f.Spills.Last!.Disposed, "Explicit launch or spill ownership changed.");
        await executor.GrepAsync(new("", f.Root, "", false, false, 100), default);
        Check(f.Runner.Last!.Arguments.SequenceEqual(["--json", "--line-number", "--color=never", "--hidden", "--", "", f.Root]), "Source empty pattern/glob argv changed.");
    }
    private static async Task Formatting()
    {
        using var f = new Fixture(); Directory.CreateDirectory(Path.Combine(f.Root, "nested"));
        var nested = Path.Combine(f.Root, "nested", "b.cs"); await File.WriteAllTextAsync(nested, "fake fixture");
        f.Runner.Result = Receipt(Event(nested, 7, "Second\r\n") + Event(f.File, 2, "First\n") + Event(nested, 7, "Second\n"), "warning must not become a path", 0);
        var result = await Invoke(f.Tool(), new { pattern = "fake" });
        Check(!result.IsError && Text(result) == "nested/b.cs:7: Second\na.cs:2: First\nnested/b.cs:7: Second" && !result.HasProperty("details") && f.Spills.Last!.Disposed, "Event order/duplicates/POSIX/CRLF/details changed.");
        f.Runner.Result = Receipt(Event(f.File, 1, "File\n"), "", 0);
        Check(Text(await Invoke(f.Tool(), new { pattern = "File", path = f.File })) == "a.cs:1: File", "File target basename changed.");
        f.Runner.Result = Receipt("", "ignored warning", 1);
        var empty = await Invoke(f.Tool(), new { pattern = "none" });
        Check(Text(empty) == "No matches found" && !empty.HasProperty("details"), "Exit 1 empty result/details changed.");
        var tool = f.Tool(); var invoker = tool.CreateInvoker(new Permit()); Check(ReferenceEquals(tool.CreateDefinition(invoker).Executor, invoker), "Definition bypassed mandatory policy.");
    }
    private static async Task Limits()
    {
        using var f = new Fixture(); var longText = new string('x', 501) + "\r\n";
        f.Runner.Result = Receipt(Event(f.File, 1, longText) + Event(f.File, 2, "hidden\n"), "", 0);
        var result = await Invoke(f.Tool(), new { pattern = "x", limit = 1 });
        Check(Text(result) == "a.cs:1: " + new string('x', 500) + "... [truncated]\n\n[1 matches limit reached. Use limit=2 for more, or refine pattern. Some lines truncated to 500 chars. Use read tool to see full lines]", "Source match/line notices differ.");
        Check(result.Details!.Value.GetProperty("matchLimitReached").GetInt32() == 1 && result.Details.Value.GetProperty("linesTruncated").GetBoolean(), "Match/line details missing.");
        var events = string.Concat(Enumerable.Range(1, 120).Select(n => Event(f.File, n, new string('z', 500) + "\n")));
        f.Runner.Result = Receipt(events, "", 0); result = await Invoke(f.Tool(), new { pattern = "z", limit = 120 });
        Check(!result.IsError && Text(result).EndsWith("[120 matches limit reached. Use limit=240 for more, or refine pattern. 50.0KB limit reached]", StringComparison.Ordinal), "Match/byte notice order differs.");
        var t = result.Details!.Value.GetProperty("truncation");
        Check(t.GetProperty("outputBytes").GetInt32() <= 51200 && !t.GetProperty("lastLinePartial").GetBoolean() && !result.Details.Value.TryGetProperty("linesTruncated", out _), "Whole-line byte truncation differs.");
    }
    private static async Task Unsupported()
    {
        using var f = new Fixture();
        foreach (var args in new object[] { new { pattern = "x", context = 1 }, new { pattern = "x", context = -1 }, new { pattern = "x", limit = 1.5 }, new { pattern = "x", limit = 0 }, new { pattern = "x", limit = 10001 }, new { pattern = "x\0" }, new { pattern = "x", literal = "true" } })
            Check((await Invoke(f.Tool(), args)).Failure?.Kind == ToolFailureKind.InvalidArguments, "Unsupported input was silently approximated.");
        Check((await Invoke(f.Tool(), new { pattern = "x", path = Path.GetDirectoryName(f.Root)! })).Failure?.Kind == ToolFailureKind.InvalidArguments, "Outside workspace target admitted.");
        Check(f.Runner.Calls == 0 && f.Spills.Calls == 0, "Invalid input reached executor.");
        foreach (var stdout in new[] { "not JSON\n", "{\"type\":\"context\"}\n", "{\"type\":\"match\",\"data\":{\"path\":{\"bytes\":\"YQ==\"}}}\n", Event(f.File, 0, "bad\n"), Event(f.File, 1, "multiline\ntext\n") })
        {
            f.Runner.Result = Receipt(stdout, "", 0); var result = await Invoke(f.Tool(), new { pattern = "x" });
            Check(result.IsError && f.Spills.Last!.Disposed, "Unsupported JSON/encoding/line event succeeded or escaped cleanup.");
        }
    }
    private static async Task Policy()
    {
        using var f = new Fixture(); PreparedToolAction? seen = null;
        ToolActionTransform transform = (_, action, _) => ValueTask.FromResult(action with { Target = f.File,
            Arguments = JsonData.Parse(JsonSerializer.Serialize(new { pattern = "--literal", path = f.File, literal = true, limit = 2 })) });
        var result = await Invoke(f.Tool(), new { pattern = "original" }, new Permit(a => { seen = a; return false; }), [transform]);
        Check(result.Failure?.Kind == ToolFailureKind.Blocked && seen is not null && seen.Target == f.File && seen.Arguments.Value.GetProperty("literal").GetBoolean() && seen.Arguments.Value.GetProperty("pattern").GetString() == "--literal" && seen.Arguments.Value.GetProperty("limit").GetInt32() == 2 && f.Runner.Calls == 0 && f.Spills.Calls == 0, "Final denial/target/options did not precede effects.");
        f.Runner.Result = Receipt(Event(f.File, 1, "--literal\n"), "", 0);
        result = await Invoke(f.Tool(), new { pattern = "original" }, new Permit(a => a.Target == f.File), [transform]);
        var launched = f.Runner.Last ?? throw new InvalidOperationException("No original request");
        Check(!result.IsError && launched.Arguments[^2] == "--literal" && launched.Arguments[^1] == f.File, "Transformed authorized invocation was not authoritative.");
    }
    private static async Task Errors()
    {
        using var f = new Fixture();
        foreach (var stderr in new[] { " regex error \r\n", "" })
        {
            f.Runner.Result = Receipt(Event(f.File, 1, "partial\n"), stderr, 2);
            var result = await Invoke(f.Tool(), new { pattern = "[" });
            Check(result.IsError && Text(result).Contains(stderr.Length == 0 ? "ripgrep exited with code 2" : "regex error", StringComparison.Ordinal) && f.Spills.Last!.Disposed, "Fatal exit with useful stdout was accepted or stderr/fallback lost.");
        }
        var callsBeforeMissing = f.Runner.Calls;
        Check((await Invoke(f.Tool(), new { pattern = "x", path = Path.Combine(f.Root, "missing") })).IsError && f.Runner.Calls == callsBeforeMissing, "Missing target reached process execution.");
        var outside = Path.Combine(Path.GetDirectoryName(f.Root)!, "outside.cs");
        f.Runner.Result = Receipt(Event(f.File, 1, "shown\n") + Event(outside, 1, "hidden\n"), "", 0);
        Check((await Invoke(f.Tool(), new { pattern = "x", limit = 1 })).IsError && f.Spills.Last!.Disposed, "Undisplayed escaping match accepted.");
        // Existing-segment canonicalization of a symlink is covered without creating an OS link.
        var injected = new FakeExecutor { Matches = [new(f.File, 1, "x\n")] };
        var files = new RedirectFiles(f.File, outside);
        var tool = new GrepTool(f.Root, f.Root, injected, files);
        Check((await Invoke(tool, new { pattern = "x" })).IsError, "Canonical escaping result accepted.");
    }
    private static async Task Admission()
    {
        using var f = new Fixture(); f.Admitted = false;
        await Throws<IOException>(() => f.Executor().GrepAsync(Request(f), default).AsTask());
        Check(f.Runner.Calls == 0 && f.Spills.Calls == 0, "Denied binary reached effects.");
        f.Admitted = true; var altered = f.Bytes.ToArray(); altered[0] ^= 1; await File.WriteAllBytesAsync(f.Binary.Executable, altered);
        await Throws<IOException>(() => f.Executor().GrepAsync(Request(f), default).AsTask());
        Check(f.Runner.Calls == 0 && f.Spills.Calls == 0, "Changed image bytes reached effects.");
    }
    private static async Task Receipts()
    {
        using var f = new Fixture(); var good = Receipt(Event(f.File, 1, "x\n"), "", 0);
        foreach (var result in new[] { good with { Process = good.Process with { CleanupConfirmed = false } }, good with { Process = good.Process with { CapturedOutputComplete = false } }, good with { StandardOutput = new("", true) }, good with { StandardError = new("", true) }, good with { Process = good.Process with { Diagnostics = [ProcessDiagnostic.OutputIoFailed] } }, good with { Process = good.Process with { ProcessStarted = false } } })
        {
            f.Runner.Result = result; await Throws<IOException>(() => f.Executor().GrepAsync(Request(f), default).AsTask());
            Check(f.Spills.Last!.Disposed, "Incomplete original receipt escaped spill cleanup.");
        }
    }
    private static async Task FailureOrdering()
    {
        using var f = new Fixture(); using var cancel = new CancellationTokenSource();
        var original = new IOException("original operation"); var cleanup = new IOException("original spill");
        f.Runner.Work = (_, _) => throw original; f.Spills.Dispose = () => { cancel.Cancel(); throw cleanup; };
        var error = await Throws<AggregateException>(() => f.Executor().GrepAsync(Request(f), cancel.Token).AsTask());
        Check(error.InnerExceptions.Count == 2 && ReferenceEquals(error.InnerExceptions[0], original) && ReferenceEquals(error.InnerExceptions[1], cleanup), "Cancellation replaced original cause identities/order.");
    }
    private static async Task Cancellation()
    {
        await HeldCancellation(duringProcess: true); await HeldCancellation(duringProcess: false);
    }
    private static async Task HeldCancellation(bool duringProcess)
    {
        using var f = new Fixture(); using var cancel = new CancellationTokenSource();
        var entered = Gate(); var processRelease = Gate(); var spillEntered = Gate(); var spillRelease = Gate(); var processJoined = false;
        f.Runner.Work = async (_, _) => { entered.TrySetResult(); await processRelease.Task; processJoined = true; return Receipt(Event(f.File, 1, "x\n"), "", 0); };
        f.Spills.Dispose = async () => { spillEntered.TrySetResult(); await spillRelease.Task; };
        Task<ImmutableArray<GrepMatch>>? original = null;
        try
        {
            original = f.Executor().GrepAsync(Request(f), cancel.Token).AsTask();
            Check(await Task.WhenAny(entered.Task, original) == entered.Task && !original.IsCompleted, "Original settled before process entry.");
            if (duringProcess) { cancel.Cancel(); Check(!original.IsCompleted, "Cancellation abandoned original process."); }
            processRelease.TrySetResult();
            Check(await Task.WhenAny(spillEntered.Task, original) == spillEntered.Task && !original.IsCompleted && processJoined, "Original process/spill ownership lost.");
            if (!duringProcess) cancel.Cancel();
            Check(!original.IsCompleted && !f.Spills.Last!.Disposed, "Cancellation abandoned held spill cleanup.");
            spillRelease.TrySetResult(); var error = await Throws<OperationCanceledException>(() => original ?? throw new InvalidOperationException());
            Check(error.CancellationToken == cancel.Token && f.Spills.Last!.Disposed, "Caller token or completed spill disposal lost.");
        }
        finally
        {
            try { cancel.Cancel(); }
            finally { processRelease.TrySetResult(); spillRelease.TrySetResult(); if (original is not null) { try { await original; } catch (OperationCanceledException) when (cancel.IsCancellationRequested) { } } }
        }
    }
    private static async Task LocalSpill()
    {
        using var f = new Fixture(); string? spill = null; var originalRunnerJoined = false;
        f.Runner.Work = async (request, _) =>
        {
            spill = request.SpillPath;
            await File.WriteAllTextAsync(request.SpillPath, "owned authored stdout and stderr spill");
            originalRunnerJoined = true;
            return Receipt(Event(f.File, 1, "owned\n"), "warning", 0);
        };
        var result = await f.Executor(new LocalFdSpillLeaseFactory(f.Root)).GrepAsync(Request(f), default);
        var ownedSpill = spill ?? throw new InvalidOperationException("Spill was not acquired");
        Check(result.Length == 1 && originalRunnerJoined && !Directory.Exists(Path.GetDirectoryName(ownedSpill)), "Actual owned spill write/removal or original runner join lost.");
    }
    private static async Task ContextFormatting()
    {
        using var f = new Fixture(); var other = Path.Combine(f.Root, "nested", "b.cs");
        var reader = new ContextReader();
        reader.Contents[f.File] = Encoding.UTF8.GetBytes("one\r\nα hit\r\nthree\rfour\n");
        reader.Contents[other] = Encoding.UTF8.GetBytes("前\nmatch\n後");
        f.Runner.Result = Receipt(Event(f.File, 2, "receipt differs\n") + Event(f.File, 3, "receipt differs\n") + Event(other, 2, "match\n"), "", 0);
        var tool = new GrepTool(f.Root, f.Root, f.Executor(), contextReader: reader);
        var result = await Invoke(tool, new { pattern = "hit", context = 1 });
        const string expected = "a.cs-1- one\na.cs:2: α hit\na.cs-3- three\na.cs-2- α hit\na.cs:3: three\na.cs-4- four\nnested/b.cs-1- 前\nnested/b.cs:2: match\nnested/b.cs-3- 後";
        Check(!result.IsError && Text(result) == expected && !result.HasProperty("details") && reader.Calls == 2 &&
            reader.Bounds.All(bound => bound == GrepTool.MaximumContextFileBytes) && f.Spills.Last!.Disposed,
            "Positive context overlap/order/source read/cache/normalization changed.");
        await Invoke(tool, new { pattern = "hit", context = 1 });
        Check(reader.Calls == 4, "Context cache leaked across invocations.");
        f.Runner.Result = Receipt(Event(f.File, 1, "receipt\n"), "", 0);
        Check(Text(await Invoke(tool, new { pattern = "x", path = f.File, context = 1 })) == "a.cs:1: one\na.cs-2- α hit", "File target/first-line clipping changed.");
        f.Runner.Result = Receipt(Event(f.File, 4, "receipt\n"), "", 0);
        Check(Text(await Invoke(tool, new { pattern = "x", context = 1 })) == "a.cs-3- three\na.cs:4: four\na.cs-5- ", "Trailing LF context split changed.");
        var beforeZero = reader.Calls;
        Check(Text(await Invoke(tool, new { pattern = "x", context = 0 })) == "a.cs:4: receipt" && reader.Calls == beforeZero, "Zero context started using file reads.");
        f.Runner.Result = Receipt("", "", 1);
        Check(Text(await Invoke(tool, new { pattern = "none", context = 1 })) == "No matches found" && reader.Calls == beforeZero, "No-match context caused a read.");
    }
    private static async Task ContextReadFailures()
    {
        using var f = new Fixture(); var reader = new ContextReader(); var tool = f.Tool(reader);
        f.Runner.Result = Receipt(Event(f.File, 1, "x\n") + Event(f.File, 2, "y\n"), "", 0);
        foreach (var error in new Exception[] { new IOException("read failed"), new UnauthorizedAccessException("denied") })
        {
            reader.Work = (_, _, _) => throw error; var before = reader.Calls;
            var result = await Invoke(tool, new { pattern = "x", context = 1 });
            Check(!result.IsError && Text(result) == "a.cs:1: (unable to read file)\na.cs:2: (unable to read file)" && reader.Calls == before + 1,
                "Read error marker/rejected-file cache differs from pinned Pi.");
        }
        reader.Work = (_, _, _) => throw new FileToolException(FileToolFailure.ResourceLimit);
        Check((await Invoke(tool, new { pattern = "x", context = 1 })).IsError, "Resource failure was hidden as an ordinary unreadable file.");
        reader.Work = null; reader.Contents[f.File] = [];
        f.Runner.Result = Receipt(Event(f.File, 1, "receipt\n"), "", 0);
        Check(Text(await Invoke(tool, new { pattern = "x", context = 1 })) == "a.cs:1: ", "Readable empty UTF-8 file became unavailable.");
        f.Runner.Result = Receipt(Event(f.File, int.MaxValue, "receipt\n"), "", 0);
        Check(Text(await Invoke(tool, new { pattern = "x", context = 100 })) == "", "Out-of-range receipt line overflowed context arithmetic.");
    }
    private static async Task ContextTruncation()
    {
        using var f = new Fixture(); var reader = new ContextReader();
        reader.Contents[f.File] = Encoding.UTF8.GetBytes(new string('x', 501) + "\nmatch\nafter");
        f.Runner.Result = Receipt(Event(f.File, 2, "receipt\n") + Event(Path.Combine(f.Root, "not-read.cs"), 3, "undisplayed\n"), "", 0);
        var result = await Invoke(f.Tool(reader), new { pattern = "x", context = 1, limit = 1 });
        Check(Text(result) == "a.cs-1- " + new string('x', 500) + "... [truncated]\na.cs:2: match\na.cs-3- after\n\n[1 matches limit reached. Use limit=2 for more, or refine pattern. Some lines truncated to 500 chars. Use read tool to see full lines]",
            "Context match limit/line truncation/notices differ.");
        reader.Contents[f.File] = Encoding.UTF8.GetBytes(string.Join('\n', Enumerable.Repeat(new string('λ', 500), 80)));
        f.Runner.Result = Receipt(Event(f.File, 40, "receipt\n"), "", 0);
        result = await Invoke(f.Tool(reader), new { pattern = "x", context = 100 });
        var raw = string.Join('\n', Enumerable.Range(1, 80).Select(n => n == 40 ? $"a.cs:{n}: {new string('λ', 500)}" : $"a.cs-{n}- {new string('λ', 500)}"));
        var expected = ToolOutputTruncator.Head(raw, new(int.MaxValue));
        var t = result.Details!.Value.GetProperty("truncation");
        Check(!result.IsError && Text(result) == expected.Content + "\n\n[50.0KB limit reached]" &&
            t.GetProperty("totalBytes").GetInt32() == expected.TotalBytes && t.GetProperty("totalLines").GetInt32() == 80 &&
            t.GetProperty("outputBytes").GetInt32() == expected.OutputBytes && t.GetProperty("outputLines").GetInt32() == expected.OutputLines &&
            !t.GetProperty("lastLinePartial").GetBoolean() && !result.Details.Value.TryGetProperty("linesTruncated", out _),
            "Bounded head differs from existing pure truncator or total metadata counts retained lines only.");
    }
    private static async Task ContextPolicy()
    {
        using var f = new Fixture(); var reader = new ContextReader(); reader.Contents[f.File] = Encoding.UTF8.GetBytes("before\nmatch\nafter");
        PreparedToolAction? seen = null;
        var result = await Invoke(f.Tool(reader), new { pattern = "x", context = 1 }, new Permit(action => { seen = action; return false; }));
        Check(result.Failure?.Kind == ToolFailureKind.Blocked && seen!.Arguments.Value.GetProperty("context").GetInt32() == 1 && reader.Calls == 0 && f.Runner.Calls == 0,
            "Positive-context read escaped final action policy.");
        Check((await Invoke(f.Tool(), new { pattern = "x", context = 1 })).Failure?.Kind == ToolFailureKind.InvalidArguments && f.Runner.Calls == 0,
            "Missing explicit read capability reached search.");
        ToolActionTransform addContext = (_, action, _) => ValueTask.FromResult(action with {
            Arguments = JsonData.Parse(JsonSerializer.Serialize(new { pattern = "x", path = action.Target, context = 1, limit = 100 })) });
        Check((await Invoke(f.Tool(), new { pattern = "x" }, transforms: [addContext])).Failure?.Kind == ToolFailureKind.InvalidArguments && f.Runner.Calls == 0,
            "Transform granted an absent read capability.");
        f.Runner.Result = Receipt(Event(f.File, 2, "receipt\n"), "", 0);
        result = await Invoke(f.Tool(reader), new { pattern = "x" }, new Permit(action => action.Arguments.Value.GetProperty("context").GetInt32() == 1), [addContext]);
        Check(Text(result) == "a.cs-1- before\na.cs:2: match\na.cs-3- after" && reader.Calls == 1, "Authorized transformed context was not authoritative.");
    }
    private static async Task ContextContainment()
    {
        using var f = new Fixture(); var reader = new ContextReader(); reader.Contents[f.File] = Encoding.UTF8.GetBytes("x");
        var outside = Path.Combine(Path.GetDirectoryName(f.Root)!, "outside.cs");
        f.Runner.Result = Receipt(Event(f.File, 1, "x\n") + Event(outside, 1, "undisplayed\n"), "", 0);
        Check((await Invoke(f.Tool(reader), new { pattern = "x", context = 1, limit = 1 })).IsError && reader.Calls == 0 && f.Spills.Last!.Disposed,
            "Complete receipt validation did not precede context reads.");
        var injected = new FakeExecutor { Matches = [new(f.File, 1, "x\n")] };
        var files = new RedirectFiles(f.File, outside, safeCalls: 1);
        var tool = new GrepTool(f.Root, f.Root, injected, files, reader);
        Check((await Invoke(tool, new { pattern = "x", context = 1 })).IsError && reader.Calls == 0,
            "Canonical path changed after receipt validation and was read outside the target.");
    }
    private static async Task ContextBounds()
    {
        using var f = new Fixture(); var reader = new ContextReader(); var tool = f.Tool(reader);
        foreach (var context in new[] { -1.0, 0.5, 101.0 })
            Check((await Invoke(tool, new { pattern = "x", context })).Failure?.Kind == ToolFailureKind.InvalidArguments, "Unbounded/fractional context admitted.");
        Check(f.Runner.Calls == 0 && reader.Calls == 0, "Invalid context caused effects.");
        f.Runner.Result = Receipt(Event(f.File, 1, "x\n"), "", 0);
        foreach (var bytes in new[] { new byte[] { 0xff }, new byte[] { 0 }, Encoding.UTF8.GetBytes(new string('\n', GrepTool.MaximumContextFileLines)),
            Encoding.UTF8.GetBytes(new string('a', GrepTool.MaximumContextFileBytes + 1)) })
        {
            reader.Contents[f.File] = bytes;
            Check((await Invoke(tool, new { pattern = "x", context = 1 })).IsError, "Encoding/file/line resource bounds accepted.");
        }
        reader.Work = (_, _, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes(new string('a', GrepTool.MaximumContextFileBytes)));
        f.Runner.Result = Receipt(string.Concat(Enumerable.Range(1, 5).Select(n => Event(Path.Combine(f.Root, $"{n}.cs"), 1, "x\n"))), "", 0);
        var beforeCache = reader.Calls;
        Check((await Invoke(tool, new { pattern = "x", context = 1 })).IsError && reader.Calls == beforeCache + 4,
            "Aggregate cache limit failed to prevent another read.");
        reader.Work = (_, _, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(ReadOnlyMemory<byte>.Empty);
        f.Runner.Result = Receipt(string.Concat(Enumerable.Range(1, 129).Select(n => Event(Path.Combine(f.Root, $"{n}.cs"), 1, "x\n"))), "", 0);
        var beforeFiles = reader.Calls;
        Check((await Invoke(tool, new { pattern = "x", context = 1, limit = 129 })).IsError && reader.Calls == beforeFiles + 128,
            "File-count cache bound failed to prevent another read.");
        reader.Work = null;
        reader.Contents[f.File] = Encoding.UTF8.GetBytes(string.Join('\n', Enumerable.Repeat(new string('a', 500), 201)));
        f.Runner.Result = Receipt(string.Concat(Enumerable.Repeat(Event(f.File, 101, "x\n"), 100)), "", 0);
        Check((await Invoke(tool, new { pattern = "x", context = 100 })).IsError, "Formatted-output resource ceiling silently returned partial success.");
    }
    private static async Task ContextCancellation()
    {
        foreach (var throwOnRelease in new[] { false, true })
        {
            using var f = new Fixture(); using var cancel = new CancellationTokenSource();
            var entered = Gate(); var release = Gate(); var joined = false; var reader = new ContextReader();
            f.Runner.Result = Receipt(Event(f.File, 1, "x\n"), "", 0);
            reader.Work = async (_, _, token) =>
            {
                Check(f.Spills.Last!.Disposed, "Read began before original executor/spill join.");
                entered.TrySetResult();
                try { await release.Task; if (throwOnRelease) throw new OperationCanceledException(token); return Encoding.UTF8.GetBytes("x"); }
                finally { joined = true; }
            };
            Task<ToolResult>? original = null;
            try
            {
                original = Invoke(f.Tool(reader), new { pattern = "x", context = 1 }, token: cancel.Token);
                Check(await Task.WhenAny(entered.Task, original) == entered.Task && !original.IsCompleted, "Original completed before held read entry.");
                cancel.Cancel(); Check(!original.IsCompleted && !joined, "Cancellation abandoned held original read.");
                release.TrySetResult(); var result = await original;
                Check(joined && result.Failure?.Kind == ToolFailureKind.Canceled && reader.Calls == 1, "Cancellation/read cleanup was not directly observed.");
            }
            finally
            {
                cancel.Cancel(); release.TrySetResult(); if (original is not null) await original;
            }
        }
    }
    private sealed class ContextReader : IGrepContextReader
    {
        public readonly Dictionary<string, byte[]> Contents = new(StringComparer.Ordinal);
        public readonly List<int> Bounds = [];
        public int Calls;
        public Func<string, int, CancellationToken, ValueTask<ReadOnlyMemory<byte>>>? Work;
        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(string path, int maximumBytes, CancellationToken token)
        {
            Calls++; Bounds.Add(maximumBytes);
            return Work is not null ? Work(path, maximumBytes, token) : ValueTask.FromResult<ReadOnlyMemory<byte>>(Contents[path]);
        }
    }
    private sealed class FakeExecutor : IGrepExecutor
    {
        public ImmutableArray<GrepMatch> Matches = [];
        public ValueTask<ImmutableArray<GrepMatch>> GrepAsync(GrepExecutionRequest request, CancellationToken token) => ValueTask.FromResult(Matches);
    }
    private sealed class RedirectFiles(string path, string replacement, int safeCalls = 0) : IDirectoryFileOperations
    {
        private readonly LocalFileOperations _local = new();
        private int _calls;
        public ValueTask<string> CanonicalizeAsync(string value, CancellationToken token) => value == path && ++_calls > safeCalls ? ValueTask.FromResult(replacement) : _local.CanonicalizeAsync(value, token);
        public ValueTask<bool> ExistsAsync(string value, CancellationToken token) => _local.ExistsAsync(value, token);
        public ValueTask<bool> IsDirectoryAsync(string value, CancellationToken token) => _local.IsDirectoryAsync(value, token);
        public ValueTask<ImmutableArray<string>> ReadDirectoryAsync(string value, int count, int chars, CancellationToken token) => throw new InvalidOperationException("Unexpected enumeration");
        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(string value, int count, CancellationToken token) => throw new InvalidOperationException("Unexpected context read");
        public ValueTask CreateDirectoryAsync(string value, CancellationToken token) => throw new InvalidOperationException("Unexpected write");
        public ValueTask WriteAsync(string value, ReadOnlyMemory<byte> bytes, CancellationToken token) => throw new InvalidOperationException("Unexpected write");
    }
    private sealed class Runner : ISeparatedProcessRunner
    {
        public int Calls; public ProcessRequest? Last; public SeparatedProcessRunResult Result = Receipt("", "", 0);
        public Func<ProcessRequest, CancellationToken, ValueTask<SeparatedProcessRunResult>>? Work;
        public ValueTask<ProcessRunResult> RunAsync(ProcessRequest request, ProcessOutputCallback? onUpdate = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Merged stream runner used");
        public ValueTask<SeparatedProcessRunResult> RunSeparatedAsync(ProcessRequest request, CancellationToken token = default) { Calls++; Last = request; return Work?.Invoke(request, token) ?? ValueTask.FromResult(Result); }
    }
    private sealed class Spills(string root) : IFdSpillLeaseFactory
    {
        public string Root => root; public int Calls; public Lease? Last; public Func<ValueTask>? Dispose;
        public ValueTask<IFdSpillLease> CreateAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Calls++; Last = new(Path.Combine(root, "spill.log"), Dispose); return ValueTask.FromResult<IFdSpillLease>(Last); }
    }
    private sealed class Lease(string path, Func<ValueTask>? dispose) : IFdSpillLease
    {
        public string SpillPath => path; public bool Disposed;
        public async ValueTask DisposeAsync() { if (dispose is not null) await dispose(); Disposed = true; }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string _parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        public string Root { get; } public string File { get; } public byte[] Bytes = Encoding.UTF8.GetBytes("authored nonexecutable ripgrep fixture");
        public RipgrepBinaryDescriptor Binary { get; } public Runner Runner = new(); public Spills Spills { get; } public bool Admitted = true;
        public Fixture()
        {
            Root = Path.Combine(_parent, "pisharp-grep-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root);
            File = Path.Combine(Root, "a.cs"); System.IO.File.WriteAllText(File, "owned authored fixture\n");
            var binary = Path.Combine(Root, "authored-rg.exe"); System.IO.File.WriteAllBytes(binary, Bytes);
            Binary = new(binary, Bytes.Length, Convert.ToHexStringLower(SHA256.HashData(Bytes)), "authored-fixture", "win-x64", "explicit-authored-nonexecution"); Spills = new(Root);
        }
        public RipgrepExecutor Executor(IFdSpillLeaseFactory? spills = null) => new(Binary, Root, ImmutableDictionary<string, string>.Empty, Runner,
            (descriptor, token) => { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(Admitted && descriptor == Binary); }, spills ?? Spills);
        public GrepTool Tool(IGrepContextReader? reader = null) => new(Root, Root, Executor(), contextReader: reader);
        public void Dispose() { if (Path.GetDirectoryName(Root) != _parent || !Path.GetFileName(Root).StartsWith("pisharp-grep-", StringComparison.Ordinal)) throw new InvalidOperationException("Invalid owned fixture root"); Directory.Delete(Root, recursive: true); }
    }
    private sealed class Permit(Func<PreparedToolAction, bool>? allow = null) : IToolActionPolicy
    {
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new ToolActionAuthorization(allow?.Invoke(action) ?? true)); }
    }
    private static async Task<ToolResult> Invoke(GrepTool tool, object args, Permit? policy = null, IEnumerable<ToolActionTransform>? transforms = null,
        CancellationToken token = default)
    {
        var call = new ToolCallContent("grep-fixture", "grep", JsonData.Parse(JsonSerializer.Serialize(args)));
        return await tool.CreateInvoker(policy ?? new Permit(), transforms).ExecuteAsync(new(new AssistantMessage("fixture", "fixture", "fixture", 0, [call], TokenUsage.Zero, StopReason.ToolUse), call, 0), token);
    }
    private static string Event(string path, int number, string text) => JsonSerializer.Serialize(new { type = "match", data = new { path = new { text = path }, line_number = number, lines = new { text }, submatches = new[] { new { start = 0, end = 1 }, new { start = 1, end = 2 } } } }) + "\n";
    private static GrepExecutionRequest Request(Fixture f) => new("x", f.Root, null, false, false, 100);
    private static SeparatedProcessRunResult Receipt(string stdout, string stderr, int code)
    {
        var merged = stdout + stderr; var t = ToolOutputTruncator.Tail(merged);
        var original = new ProcessRunResult(code == 0 ? ProcessRunStatus.Exited : ProcessRunStatus.NonZeroExit, code, 1, true, true, true,
            new(t.Content, t, Encoding.UTF8.GetByteCount(merged), 0, null), new(merged, false), 0, []);
        return new(original, new(stdout, false), new(stderr, false));
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string Text(ToolResult result) => result.Content.Single().Text;
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task<T> Throws<T>(Func<Task> operation) where T : Exception { try { await operation(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
