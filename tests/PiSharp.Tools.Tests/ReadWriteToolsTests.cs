using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Tools.Files;

internal static class ReadWriteToolsTests
{
    public static object PlatformEvidence { get; private set; } = new { existingDirectoryAlias = "not-exercised" };
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("read/write owned declarations and mandatory-policy composition", Declarations),
        ("read/write real UTF8 bytes overwrite nested Unicode paths and preserve endings", RealUtf8Effects),
        ("read source-shaped offsets trailing lines zero limits and continuation notices", ReadSelection),
        ("read shared head line/byte/oversized-first-line output metadata", ReadTruncation),
        ("read/write bounded input and explicit unfinished content profiles", LimitsAndUnsupportedContent),
        ("read/write invalid final actions and denied transformed targets have no effects", AdmissionAndDenial),
        ("write executes exact transformed policy target and owned content", ExactFinalAction),
        ("write NUL content retains exact UTF8 at inclusive byte limit and over-limit denial has no effects", WriteNulByteLimits),
        ("write NUL original and transformed content remain owned through final policy and execution", WriteNulOwnership),
        ("write content NUL admission retains strict initial path and transformed path/display denial", WriteNulPathDenial),
        ("read/write explicit cwd home Unicode spaces and read filename variants", PathsAndVariants),
        ("write cancellation holds reservation through admitted cleanup and preserves effect state", CancellationAndCleanup),
        ("write mkdir and write faults preserve state and do not poison next mutation", FaultRecovery),
        ("write existing directory aliases serialize cancellation cleanup when supported", ExistingAlias)
    ];

    private static Task Declarations()
    {
        using var temp = new TemporaryFiles();
        var tools = new ReadWriteTools(temp.Root, temp.Root);
        Sequence(["read", "write"], tools.Adapters.Select(value => value.Name));
        Equal(2, tools.ToolsAdded.Value.GetArrayLength());
        foreach (var declaration in tools.Declarations)
        {
            Equal("object", declaration.Value.GetProperty("parameters").GetProperty("type").GetString());
            Check(!declaration.Value.GetProperty("parameters").GetProperty("additionalProperties").GetBoolean(), "Schema allows unsupported fields.");
        }
        Equal("integer", tools.Declarations[0].Value.GetProperty("parameters").GetProperty("properties").GetProperty("offset").GetProperty("type").GetString());
        var invoker = tools.CreateInvoker(new Policy());
        Check(tools.CreateDefinitions(invoker).All(value => ReferenceEquals(value.Executor, invoker)), "Definitions bypass the invoker.");
        Throws<ArgumentNullException>(() => tools.CreateInvoker(null!));
        Throws<ArgumentOutOfRangeException>(() => new ReadWriteTools(temp.Root, temp.Root, options: new(MaximumReadBytes: 0)));
        return Task.CompletedTask;
    }

    private static async Task RealUtf8Effects()
    {
        using var temp = new TemporaryFiles();
        var tools = new ReadWriteTools(temp.Root, temp.Root);
        const string relative = "nested space/\u6587.txt";
        const string text = "\ufeff\u03b1\U0001f642\r\nsecond\n";
        var written = await Invoke(tools, "write", new { path = relative, content = text });
        Success(written); Equal("Successfully wrote to " + relative, written.Content.Single().Text); Equal(JsonValueKind.Null, written.Details.Value.ValueKind);
        var target = Path.Combine(temp.Root, "nested space", "\u6587.txt");
        var actualBytes = await File.ReadAllBytesAsync(target);
        Check(Encoding.UTF8.GetBytes(text).SequenceEqual(actualBytes), "Write changed BOM/Unicode/newline bytes.");
        Equal(text, (await Invoke(tools, "read", new { path = relative })).Content.Single().Text);
        Success(await Invoke(tools, "write", new { path = relative, content = "x" }));
        Check((await File.ReadAllBytesAsync(target)).SequenceEqual(new byte[] { (byte)'x' }), "Overwrite retained old tail or added a BOM.");
        Success(await Invoke(tools, "write", new { path = relative, content = "" })); Equal(0L, new FileInfo(target).Length);
        Equal("", (await Invoke(tools, "read", new { path = relative })).Content.Single().Text);
    }

    private static async Task ReadSelection()
    {
        using var temp = new TemporaryFiles(); var tools = new ReadWriteTools(temp.Root, temp.Root);
        await File.WriteAllBytesAsync(temp.File("lines.txt"), Encoding.UTF8.GetBytes("first\r\n\u754c\nlast\n"));
        var selected = await Invoke(tools, "read", new { path = "lines.txt", offset = 2, limit = 1 });
        Success(selected); Equal("\u754c\n\n[2 more lines in file. Use offset=3 to continue.]", selected.Content.Single().Text);
        Equal(JsonValueKind.Null, selected.Details.Value.ValueKind);
        Equal("", (await Invoke(tools, "read", new { path = "lines.txt", offset = 4 })).Content.Single().Text);
        Equal("\n\n[4 more lines in file. Use offset=1 to continue.]", (await Invoke(tools, "read", new { path = "lines.txt", limit = 0 })).Content.Single().Text);
        var beyond = await Invoke(tools, "read", new { path = "lines.txt", offset = 5 });
        Failure(beyond, ToolFailureKind.InvalidArguments); Equal("Offset 5 is beyond end of file (4 lines total)", beyond.Content.Single().Text);
        await File.WriteAllBytesAsync(temp.File("empty"), []);
        Equal("", (await Invoke(tools, "read", new { path = "empty" })).Content.Single().Text);
        Failure(await Invoke(tools, "read", new { path = "empty", offset = 2 }), ToolFailureKind.InvalidArguments);
    }

    private static async Task ReadTruncation()
    {
        using var temp = new TemporaryFiles(); var tools = new ReadWriteTools(temp.Root, temp.Root);
        var many = string.Join("\n", Enumerable.Range(1, 2001).Select(value => value.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "\n";
        await File.WriteAllBytesAsync(temp.File("many"), Encoding.UTF8.GetBytes(many));
        var lineResult = await Invoke(tools, "read", new { path = "many" }); Success(lineResult);
        Check(lineResult.Content.Single().Text.EndsWith("\n\n[Showing lines 1-2000 of 2002. Use offset=2001 to continue.]", StringComparison.Ordinal), "Line continuation metadata changed.");
        var line = lineResult.Details.Value.GetProperty("truncation");
        Equal(11, line.EnumerateObject().Count()); Equal("lines", line.GetProperty("truncatedBy").GetString());
        Equal(2001, line.GetProperty("totalLines").GetInt32()); Equal(2000, line.GetProperty("outputLines").GetInt32());
        Equal(2000, line.GetProperty("maxLines").GetInt32()); Equal(51200, line.GetProperty("maxBytes").GetInt32());
        var first = new string('x', 30_000); var bytes = first + "\n" + new string('y', 30_000) + "\nend";
        await File.WriteAllBytesAsync(temp.File("bytes"), Encoding.UTF8.GetBytes(bytes));
        var byteResult = await Invoke(tools, "read", new { path = "bytes" }); Success(byteResult);
        Equal(first + "\n\n[Showing lines 1-1 of 3 (50.0KB limit). Use offset=2 to continue.]", byteResult.Content.Single().Text);
        var byteMetadata = byteResult.Details.Value.GetProperty("truncation");
        Equal("bytes", byteMetadata.GetProperty("truncatedBy").GetString()); Equal(30000, byteMetadata.GetProperty("outputBytes").GetInt32());
        Equal(60005, byteMetadata.GetProperty("totalBytes").GetInt32());
        await File.WriteAllBytesAsync(temp.File("long"), Encoding.UTF8.GetBytes(new string('x', 51201)));
        var longResult = await Invoke(tools, "read", new { path = "long" }); Success(longResult);
        Equal("[Line 1 is 50.0KB, exceeds 50.0KB limit. Use bash: sed -n '1p' long | head -c 51200]", longResult.Content.Single().Text);
        Check(longResult.Details.Value.GetProperty("truncation").GetProperty("firstLineExceedsLimit").GetBoolean(), "Long first line was silently returned.");
    }

    private static async Task LimitsAndUnsupportedContent()
    {
        using var temp = new TemporaryFiles();
        var tools = new ReadWriteTools(temp.Root, temp.Root, options: new(MaximumReadBytes: 16, MaximumWriteBytes: 4));
        await File.WriteAllBytesAsync(temp.File("bound"), Encoding.UTF8.GetBytes(new string('x', 16)));
        Success(await Invoke(tools, "read", new { path = "bound" }));
        await File.WriteAllBytesAsync(temp.File("bound"), Encoding.UTF8.GetBytes(new string('x', 17)));
        var over = await Invoke(tools, "read", new { path = "bound" }); Failure(over, ToolFailureKind.ExecutionError);
        Equal("ResourceLimit", over.Details.Value.GetProperty("fileOperation").GetProperty("code").GetString());
        var policy = new Policy();
        Failure(await Invoke(tools, "write", new { path = "absent", content = "12345" }, policy: policy), ToolFailureKind.InvalidArguments);
        Equal(0, policy.Actions.Count); Check(!File.Exists(temp.File("absent")), "Oversized write created a file.");
        Success(await Invoke(tools, "write", new { path = "exact", content = "\U0001f642" })); Equal(4L, new FileInfo(temp.File("exact")).Length);
        var contentTools = new ReadWriteTools(temp.Root, temp.Root);
        var unsupported = new byte[][]
        {
            [0xc3, 0x28], [0xff, 0xfe, 0x41, 0x00], [0x61, 0x00, 0x62], [0x61, 0x01, 0x62],
            Encoding.UTF8.GetBytes("a\u0085b"),
            [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a], Encoding.ASCII.GetBytes("GIF89a"),
            Encoding.ASCII.GetBytes("RIFFxxxxWEBP")
        };
        for (var index = 0; index < unsupported.Length; index++)
        {
            await File.WriteAllBytesAsync(temp.File("unsupported"), unsupported[index]);
            var result = await Invoke(contentTools, "read", new { path = "unsupported" }); Failure(result, ToolFailureKind.ExecutionError);
            Equal("UnsupportedContent", result.Details.Value.GetProperty("fileOperation").GetProperty("code").GetString());
        }
        const string ordinaryBmpPrefix = "BM is a text prefix with at least twenty six characters.";
        await File.WriteAllBytesAsync(temp.File("text"), Encoding.UTF8.GetBytes(ordinaryBmpPrefix));
        Equal(ordinaryBmpPrefix, (await Invoke(contentTools, "read", new { path = "text" })).Content.Single().Text);
        var oversizedHost = new Operations { Read = (_, maximum, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[maximum + 1]) };
        var hostTools = new ReadWriteTools(temp.Root, temp.Root, oversizedHost, new(MaximumReadBytes: 16));
        Failure(await Invoke(hostTools, "read", new { path = "host" }), ToolFailureKind.ExecutionError);
    }

    private static async Task AdmissionAndDenial()
    {
        using var temp = new TemporaryFiles(); var operations = new Operations(); var tools = new ReadWriteTools(temp.Root, temp.Root, operations);
        foreach (var raw in new[] { "{}", "{\"path\":\"x\",\"offset\":0}", "{\"path\":\"x\",\"offset\":1.5}",
            "{\"path\":\"x\",\"limit\":-1}", "{\"path\":\"x\",\"unknown\":1}" })
            Failure(await tools.CreateInvoker(new Policy()).ExecuteAsync(Invocation("read", JsonData.Parse(raw)), default), ToolFailureKind.InvalidArguments);
        foreach (var raw in new[] { "{\"path\":\"x\"}", "{\"path\":\"x\",\"content\":1}", "{\"path\":\"x\\u0000\",\"content\":\"valid\"}" })
            Failure(await tools.CreateInvoker(new Policy()).ExecuteAsync(Invocation("write", JsonData.Parse(raw)), default), ToolFailureKind.InvalidArguments);
        var original = Invocation("write", Arguments(new { path = "original", content = "text" }));
        var policy = new Policy();
        var mismatched = tools.CreateInvoker(policy, [(_, action, _) => ValueTask.FromResult(action with { Target = temp.File("other") })]);
        Failure(await mismatched.ExecuteAsync(original, default), ToolFailureKind.InvalidArguments); Equal(0, policy.Actions.Count);
        var denied = tools.CreateInvoker(new Policy(_ => false), [(_, action, _) => ValueTask.FromResult(Retarget(action, temp.File("denied"), "denied"))]);
        Failure(await denied.ExecuteAsync(original, default), ToolFailureKind.Blocked);
        using var before = new CancellationTokenSource(); before.Cancel();
        Failure(await tools.CreateInvoker(new Policy()).ExecuteAsync(original, before.Token), ToolFailureKind.Canceled);
        Equal(0, operations.DirectoryCalls); Equal(0, operations.WriteCalls); Equal(0, operations.ReadCalls);
        Check(!File.Exists(temp.File("original")) && !File.Exists(temp.File("denied")), "Rejected call created output.");
    }

    private static async Task ExactFinalAction()
    {
        using var temp = new TemporaryFiles(); var operations = new Operations(); var tools = new ReadWriteTools(temp.Root, temp.Root, operations);
        PreparedToolAction? final = null;
        var target = temp.File("transformed.txt");
        var policy = new Policy(action =>
        {
            Check(ReferenceEquals(final, action), "Policy did not receive final immutable instance.");
            Equal(target, action.Target); Equal(target, action.Arguments.Value.GetProperty("path").GetString());
            Equal("transformed content", action.Arguments.Value.GetProperty("content").GetString()); return true;
        });
        var invoker = tools.CreateInvoker(policy, [(_, action, _) =>
            { final = Retarget(action, target, "transformed.txt", "transformed content"); return ValueTask.FromResult(final); }]);
        var result = await invoker.ExecuteAsync(Invocation("write", Arguments(new { path = "original.txt", content = "original" })), default);
        Success(result); Equal("Successfully wrote to transformed.txt", result.Content.Single().Text);
        Equal(target, operations.Writes.Single().Path); Equal("transformed content", Encoding.UTF8.GetString(operations.Writes.Single().Bytes));
        Equal("transformed content", await File.ReadAllTextAsync(target)); Check(!File.Exists(temp.File("original.txt")), "Executor rederived original input target.");
    }

    private static async Task WriteNulByteLimits()
    {
        using var temp = new TemporaryFiles(); var operations = new Operations(); var policy = new Policy();
        var tools = new ReadWriteTools(temp.Root, temp.Root, operations, new(MaximumWriteBytes: 10));
        var invoker = tools.CreateInvoker(policy);
        const string content = "A\0\u03b1\U0001f642\r\n";
        byte[] expected = [0x41, 0x00, 0xce, 0xb1, 0xf0, 0x9f, 0x99, 0x82, 0x0d, 0x0a];
        var arguments = JsonData.Parse("""{ "path": "nested/nul.txt", "content": "A\u0000\u03b1\ud83d\ude42\r\n" }""");
        var original = arguments.ToString();
        var invocation = Invocation("write", arguments);
        Success(await invoker.ExecuteAsync(invocation, default));
        Equal(10, expected.Length); Equal(content, policy.Actions.Single().Arguments.Value.GetProperty("content").GetString());
        Equal(1, operations.DirectoryCalls); Equal(1, operations.WriteCalls);
        Check(expected.SequenceEqual(operations.Writes.Single().Bytes), "Admitted NUL write changed its actual operation bytes.");
        var writtenBytes = await File.ReadAllBytesAsync(temp.File("nested/nul.txt"));
        Check(expected.SequenceEqual(writtenBytes), "NUL/Unicode/CRLF write bytes changed or gained a BOM.");
        Check(ReferenceEquals(arguments, invocation.Call.Arguments), "Write replaced the original owned arguments."); Equal(original, arguments.ToString());

        foreach (var path in new[] { "nested/nul.txt", "absent-parent/over.txt" })
            Failure(await invoker.ExecuteAsync(Invocation("write", Arguments(new { path, content = content + "\0" })), default), ToolFailureKind.InvalidArguments);
        Equal(1, policy.Actions.Count); Equal(1, operations.DirectoryCalls); Equal(1, operations.WriteCalls);
        Check(!Directory.Exists(temp.File("absent-parent")), "Over-byte-limit NUL content created its parent directory.");
        var afterDeniedBytes = await File.ReadAllBytesAsync(temp.File("nested/nul.txt"));
        Check(expected.SequenceEqual(afterDeniedBytes), "Over-byte-limit denial changed existing bytes.");

        using var malformed = JsonDocument.Parse("""{"path":"malformed.txt","content":"\ud800"}""");
        Failure(await invoker.ExecuteAsync(Invocation("write", JsonData.FromElement(malformed.RootElement)), default), ToolFailureKind.InvalidArguments);
        var bounded = new ReadWriteTools(temp.Root, temp.Root, operations, new(MaximumWriteBytes: 10, MaximumArgumentCharacters: 256));
        var oversizedRaw = JsonData.Parse("{\"path\":\"raw-limit.txt\",\"content\":\"\\u0000\"" + new string(' ', 257) + "}");
        var rawPolicy = new Policy();
        Failure(await bounded.CreateInvoker(rawPolicy).ExecuteAsync(Invocation("write", oversizedRaw), default), ToolFailureKind.InvalidArguments);
        Equal(0, rawPolicy.Actions.Count); Equal(1, operations.DirectoryCalls); Equal(1, operations.WriteCalls);
        Check(!File.Exists(temp.File("malformed.txt")) && !File.Exists(temp.File("raw-limit.txt")), "NUL admission relaxed Unicode or raw argument limits.");
        Equal(0, tools.MutationSnapshot.RegisteredOperations);
    }

    private static async Task WriteNulOwnership()
    {
        using var temp = new TemporaryFiles(); var operations = new Operations();
        var tools = new ReadWriteTools(temp.Root, temp.Root, operations, new(MaximumWriteBytes: 7));
        const string originalContent = "in\0";
        const string finalContent = "\u03b1\0\U0001f642";
        byte[] expected = [0xce, 0xb1, 0x00, 0xf0, 0x9f, 0x99, 0x82];
        JsonData arguments;
        using (var document = JsonDocument.Parse("""{ "path": "original.txt", "content": "in\u0000" }"""))
            arguments = JsonData.FromElement(document.RootElement);
        var originalRaw = arguments.ToString();
        PreparedToolAction? prepared = null; PreparedToolAction? final = null;
        string? preparedRaw = null; string? finalRaw = null;
        var target = temp.File("transformed/nul.txt");
        var policy = new Policy(action =>
        {
            Check(ReferenceEquals(final, action), "Policy did not receive the same final owned NUL-content action.");
            Equal(target, action.Target); Equal(target, action.Arguments.Value.GetProperty("path").GetString());
            Equal(finalContent, action.Arguments.Value.GetProperty("content").GetString()); return true;
        });
        var invoker = tools.CreateInvoker(policy, [(_, action, _) =>
        {
            prepared = action; preparedRaw = action.Arguments.ToString();
            Equal(originalContent, action.Arguments.Value.GetProperty("content").GetString());
            final = Retarget(action, target, "transformed/nul.txt", finalContent); finalRaw = final.Arguments.ToString();
            return ValueTask.FromResult(final);
        }]);
        var invocation = Invocation("write", arguments);
        var result = await invoker.ExecuteAsync(invocation, default);
        Success(result); Equal("Successfully wrote to transformed/nul.txt", result.Content.Single().Text);
        Equal(target, operations.Writes.Single().Path);
        var writtenBytes = await File.ReadAllBytesAsync(target);
        Check(expected.SequenceEqual(operations.Writes.Single().Bytes) && expected.SequenceEqual(writtenBytes), "Executor changed final owned NUL content.");
        var preparedAction = prepared ?? throw new InvalidOperationException("NUL invocation did not reach initial action ownership.");
        var finalAction = final ?? throw new InvalidOperationException("NUL invocation did not reach final action ownership.");
        Check(!ReferenceEquals(preparedAction.Arguments, finalAction.Arguments), "Content replacement borrowed original arguments.");
        Equal(preparedRaw, preparedAction.Arguments.ToString()); Equal(finalRaw, finalAction.Arguments.ToString());
        Equal(originalContent, preparedAction.Arguments.Value.GetProperty("content").GetString());
        Equal(originalRaw, arguments.ToString()); Check(ReferenceEquals(arguments, invocation.Call.Arguments), "Invocation argument ownership changed.");
        Check(!File.Exists(temp.File("original.txt")), "Final NUL content was written to the original submitted target.");

        var tooLarge = tools.CreateInvoker(policy, [(_, action, _) => ValueTask.FromResult(Retarget(action, target, "transformed/nul.txt", finalContent + "\0"))]);
        Failure(await tooLarge.ExecuteAsync(invocation, default), ToolFailureKind.InvalidArguments);
        Equal(1, policy.Actions.Count); Equal(1, operations.DirectoryCalls); Equal(1, operations.WriteCalls);
        var afterDeniedBytes = await File.ReadAllBytesAsync(target);
        Check(expected.SequenceEqual(afterDeniedBytes), "Transformed over-byte-limit content changed final bytes.");
        Equal(originalRaw, arguments.ToString()); Equal(preparedRaw, preparedAction.Arguments.ToString()); Equal(finalRaw, finalAction.Arguments.ToString());
        Equal(0, tools.MutationSnapshot.RegisteredOperations);
    }

    private static async Task WriteNulPathDenial()
    {
        using var temp = new TemporaryFiles(); var operations = new Operations(); var policy = new Policy();
        var tools = new ReadWriteTools(temp.Root, temp.Root, operations);
        Failure(await tools.CreateInvoker(policy).ExecuteAsync(Invocation("write", Arguments(new { path = "bad\0path", content = "allowed\0data" })), default), ToolFailureKind.InvalidArguments);
        Failure(await tools.CreateInvoker(policy).ExecuteAsync(Invocation("read", Arguments(new { path = "bad\0path" })), default), ToolFailureKind.InvalidArguments);
        var original = Invocation("write", Arguments(new { path = "untouched.txt", content = "allowed\0data" }));
        var originalRaw = original.Call.Arguments.ToString();
        var pathNul = tools.CreateInvoker(policy, [(_, action, _) =>
            ValueTask.FromResult(Retarget(action, temp.File("valid.txt") + "\0suffix", "valid.txt"))]);
        Failure(await pathNul.ExecuteAsync(original, default), ToolFailureKind.InvalidArguments);
        var displayNul = tools.CreateInvoker(policy, [(_, action, _) =>
            ValueTask.FromResult(Retarget(action, action.Target, "display\0name"))]);
        Failure(await displayNul.ExecuteAsync(original, default), ToolFailureKind.InvalidArguments);
        Equal(0, policy.Actions.Count); Equal(0, operations.DirectoryCalls); Equal(0, operations.WriteCalls); Equal(0, operations.ReadCalls);
        Equal(0, Directory.GetFileSystemEntries(temp.Root).Length);
        Equal(originalRaw, original.Call.Arguments.ToString()); Equal(0, tools.MutationSnapshot.RegisteredOperations);
    }

    private static async Task PathsAndVariants()
    {
        using var temp = new TemporaryFiles(); var home = temp.File("home"); Directory.CreateDirectory(home);
        var operations = new LocalFileOperations(); var paths = new PathResolver(temp.Root, home, operations);
        Equal(Path.Combine(home, "child"), paths.Resolve("@~/child"));
        Equal(temp.File("na me"), paths.Resolve("na\u00a0me")); Equal(temp.File("$HOME"), paths.Resolve("$HOME"));
        Equal(temp.File("~someone"), paths.Resolve("~someone"));
        var fileUrl = "file://" + (OperatingSystem.IsWindows() ? "/" + temp.File("url name").Replace('\\', '/') : temp.File("url name"));
        Equal(temp.File("url name"), paths.Resolve(fileUrl));
        if (OperatingSystem.IsWindows())
        {
            var drivePath = "/" + char.ToLowerInvariant(temp.Root[0]) + temp.Root[2..].Replace('\\', '/');
            Equal(temp.Root, paths.Resolve(drivePath)); Equal(temp.Root, paths.Resolve("/mnt" + drivePath)); Equal(temp.Root, paths.Resolve("/cygdrive" + drivePath));
            Throws<ArgumentException>(() => paths.Resolve(temp.Root[..2] + "drive-relative"));
        }
        var tools = new ReadWriteTools(temp.Root, home);
        Success(await Invoke(tools, "write", new { path = "@~/child", content = "home text" }));
        Equal("home text", await File.ReadAllTextAsync(Path.Combine(home, "child")));
        var screenshot = "Shot 1\u202fAM.txt"; await File.WriteAllTextAsync(temp.File(screenshot), "screenshot text");
        Equal("screenshot text", (await Invoke(tools, "read", new { path = "Shot 1 AM.txt" })).Content.Single().Text);
        var original = "Capture d'\u00e9cran.txt"; var variant = original.Normalize(NormalizationForm.FormD).Replace('\'', '\u2019');
        await File.WriteAllTextAsync(temp.File(variant), "variant text");
        var policy = new Policy(); Equal("variant text", (await Invoke(tools, "read", new { path = original }, policy: policy)).Content.Single().Text);
        Equal(temp.File(variant), policy.Actions.Single().Target);
    }

    private static async Task CancellationAndCleanup()
    {
        using var temp = new TemporaryFiles(); var target = temp.File("same"); await File.WriteAllTextAsync(target, "initial");
        await QueuedCancellation(temp, target, target);
        await CanceledWaitingMutation(temp);
        using var between = new CancellationTokenSource(); var operations = new Operations();
        operations.Directory = async (path, _) => { await operations.Local.CreateDirectoryAsync(path, default); between.Cancel(); };
        var tools = new ReadWriteTools(temp.Root, temp.Root, operations);
        var stopped = await Invoke(tools, "write", new { path = "created/sub/file", content = "x" }, between.Token);
        Failure(stopped, ToolFailureKind.Canceled); Equal(0, operations.WriteCalls);
        Check(Directory.Exists(temp.File("created/sub")) && !File.Exists(temp.File("created/sub/file")), "Cancellation boundary hid directory state or admitted a write.");
        Check(!stopped.Details.Value.GetProperty("fileOperation").GetProperty("writeAttempted").GetBoolean(), "Pre-write cancellation reported a write attempt.");
        using var after = new CancellationTokenSource(); var completed = new Operations();
        completed.Write = async (path, bytes, _) => { await completed.Local.WriteAsync(path, bytes, default); after.Cancel(); };
        var done = await Invoke(new ReadWriteTools(temp.Root, temp.Root, completed), "write", new { path = "completed", content = "known" }, after.Token);
        Failure(done, ToolFailureKind.Canceled);
        Check(done.Details.Value.GetProperty("fileOperation").GetProperty("writeCompleted").GetBoolean(), "Known completed write was concealed.");
        Equal("known", await File.ReadAllTextAsync(temp.File("completed")));
    }

    private static async Task CanceledWaitingMutation(TemporaryFiles temp)
    {
        using var waitingCancellation = new CancellationTokenSource(); var operations = new Operations();
        var entered = Gate(); var release = Gate(); var calls = 0;
        operations.Write = async (path, bytes, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1) { entered.TrySetResult(); await release.Task; }
            await operations.Local.WriteAsync(path, bytes, token);
        };
        var tools = new ReadWriteTools(temp.Root, temp.Root, operations);
        var first = Invoke(tools, "write", new { path = "waiting", content = "first" }); await entered.Task;
        var middle = Invoke(tools, "write", new { path = "waiting", content = "canceled" }, waitingCancellation.Token);
        var last = Invoke(tools, "write", new { path = "waiting", content = "last" });
        try
        {
            waitingCancellation.Cancel();
            Equal(3, tools.MutationSnapshot.RegisteredOperations); Equal(1, operations.DirectoryCalls); Equal(1, operations.WriteCalls);
            Check(!middle.IsCompleted && !last.IsCompleted, "Canceled waiter let its successor overtake the active mutation.");
            release.TrySetResult(); Success(await first); Failure(await middle, ToolFailureKind.Canceled); Success(await last);
            Equal(2, operations.WriteCalls); Equal("last", await File.ReadAllTextAsync(temp.File("waiting")));
            Equal(0, tools.MutationSnapshot.RegisteredOperations);
        }
        finally { waitingCancellation.Cancel(); release.TrySetResult(); await Settle(first, middle, last); }
    }

    private static async Task QueuedCancellation(TemporaryFiles temp, string firstPath, string secondPath)
    {
        using var cancellation = new CancellationTokenSource(); var operations = new Operations();
        var canonical = await operations.Local.CanonicalizeAsync(firstPath, default);
        var entered = Gate(); var never = Gate(); var cleanup = Gate(); var releaseCleanup = Gate(); var successor = Gate(); var sameKeyCalls = 0;
        operations.Write = async (path, bytes, token) =>
        {
            if (path == canonical)
            {
                if (Interlocked.Increment(ref sameKeyCalls) == 1)
                {
                    await operations.Local.WriteAsync(path, bytes[..2], default); entered.TrySetResult();
                    try { await never.Task.WaitAsync(token); }
                    finally { cleanup.TrySetResult(); await releaseCleanup.Task; }
                    return;
                }
                successor.TrySetResult();
            }
            await operations.Local.WriteAsync(path, bytes, token);
        };
        var tools = new ReadWriteTools(temp.Root, temp.Root, operations); var policy = new Policy(); var invoker = tools.CreateInvoker(policy);
        var first = invoker.ExecuteAsync(Invocation("write", Arguments(new { path = firstPath, content = "first-effect" })), cancellation.Token).AsTask();
        await entered.Task;
        var second = invoker.ExecuteAsync(Invocation("write", Arguments(new { path = secondPath, content = "second-effect" })), default).AsTask();
        try
        {
            cancellation.Cancel(); await cleanup.Task;
            Check(!first.IsCompleted && !successor.Task.IsCompleted, "Cancellation released mutation reservation during owned cleanup.");
            Equal(2, tools.MutationSnapshot.RegisteredOperations); Equal(1, tools.MutationSnapshot.RegisteredKeys);
            Success(await Invoke(tools, "write", new { path = "unrelated", content = "other" }));
            Check(!successor.Task.IsCompleted, "Unrelated write disturbed same-key reservation.");
            releaseCleanup.TrySetResult(); var canceled = await first; Failure(canceled, ToolFailureKind.Canceled); Success(await second);
            Check(canceled.Details.Value.GetProperty("fileOperation").GetProperty("writeAttempted").GetBoolean(), "Canceled admitted effect was concealed.");
            Check(!canceled.Details.Value.GetProperty("fileOperation").GetProperty("writeCompleted").GetBoolean(), "Interrupted write claimed completion.");
            Equal("second-effect", await File.ReadAllTextAsync(canonical)); Equal(0, tools.MutationSnapshot.RegisteredOperations);
            Equal(canonical, policy.Actions.First().Target); Equal(canonical, policy.Actions.Skip(1).First().Target);
        }
        finally { cancellation.Cancel(); never.TrySetResult(); releaseCleanup.TrySetResult(); await Settle(first, second); }
    }

    private static async Task FaultRecovery()
    {
        using var temp = new TemporaryFiles(); var operations = new Operations(); var calls = 0;
        operations.Write = async (path, bytes, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new IOException("private write fault payload");
            await operations.Local.WriteAsync(path, bytes, token);
        };
        var tools = new ReadWriteTools(temp.Root, temp.Root, operations);
        var failed = await Invoke(tools, "write", new { path = "fault", content = "first" }); Failure(failed, ToolFailureKind.ExecutionError);
        Check(failed.Content.All(value => !value.Text.Contains("private", StringComparison.Ordinal)), "Write fault leaked host exception.");
        Check(failed.Details.Value.GetProperty("fileOperation").GetProperty("writeAttempted").GetBoolean(), "Fault lost admitted effect status.");
        Success(await Invoke(tools, "write", new { path = "fault", content = "next" })); Equal("next", await File.ReadAllTextAsync(temp.File("fault")));
        operations.Directory = (_, _) => throw new IOException("private mkdir fault");
        var mkdir = await Invoke(tools, "write", new { path = "directory/fault", content = "x" }); Failure(mkdir, ToolFailureKind.ExecutionError);
        Check(!mkdir.Details.Value.GetProperty("fileOperation").GetProperty("writeAttempted").GetBoolean(), "Failed mkdir admitted file write.");
        var read = await Invoke(tools, "read", new { path = "missing" }); Failure(read, ToolFailureKind.ExecutionError);
        await File.WriteAllTextAsync(temp.File("parent-is-file"), "x");
        Failure(await Invoke(new ReadWriteTools(temp.Root, temp.Root), "write", new { path = "parent-is-file/child", content = "x" }), ToolFailureKind.ExecutionError);
        Equal(0, tools.MutationSnapshot.RegisteredOperations);
        // An injected seam verifies dirname(root) without admitting any real root mutation.
        var root = Path.GetPathRoot(temp.Root)!; var rootOperations = new Operations();
        rootOperations.Directory = (path, _) => { Equal(root, path); return ValueTask.CompletedTask; };
        rootOperations.Write = (path, _, _) => { Equal(root, path); throw new IOException("Authored root-write fault; no actual filesystem effect."); };
        var rootFailure = await Invoke(new ReadWriteTools(temp.Root, temp.Root, rootOperations), "write", new { path = root, content = "x" });
        Failure(rootFailure, ToolFailureKind.ExecutionError); Equal(1, rootOperations.DirectoryCalls); Equal(1, rootOperations.WriteCalls);
        Check(rootFailure.Details.Value.GetProperty("fileOperation").GetProperty("directoryCompleted").GetBoolean(), "Root dirname passed an invalid null parent.");
    }

    private static async Task ExistingAlias()
    {
        using var temp = new TemporaryFiles(); var real = temp.File("real"); var alias = temp.File("alias"); Directory.CreateDirectory(real);
        await File.WriteAllTextAsync(Path.Combine(real, "file"), "initial");
        try { Directory.CreateSymbolicLink(alias, real); temp.Links.Add(alias); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            PlatformEvidence = new { existingDirectoryAlias = "unavailable", reason = error.GetType().Name,
                profile = "framework existing-segment symlink/junction resolution", actualAliasParityClaimed = false };
            Console.WriteLine("CAPABILITY existing directory alias unavailable: " + error.GetType().Name); return;
        }
        await QueuedCancellation(temp, Path.Combine(real, "file"), Path.Combine(alias, "file"));
        PlatformEvidence = new { existingDirectoryAlias = "exercised", profile = "framework existing-segment directory symbolic link",
            hardLinkIdentityImplemented = false, hostilePathRaceConfinementClaimed = false };
    }

    private sealed class Operations : IFileOperations
    {
        public readonly LocalFileOperations Local = new();
        public int DirectoryCalls; public int WriteCalls; public int ReadCalls;
        public readonly ConcurrentQueue<(string Path, byte[] Bytes)> Writes = new();
        public Func<string, int, CancellationToken, ValueTask<ReadOnlyMemory<byte>>>? Read;
        public Func<string, CancellationToken, ValueTask>? Directory;
        public Func<string, ReadOnlyMemory<byte>, CancellationToken, ValueTask>? Write;
        public ValueTask<bool> ExistsAsync(string path, CancellationToken token) => Local.ExistsAsync(path, token);
        public ValueTask<string> CanonicalizeAsync(string path, CancellationToken token) => Local.CanonicalizeAsync(path, token);
        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(string path, int maximum, CancellationToken token)
        { Interlocked.Increment(ref ReadCalls); return Read?.Invoke(path, maximum, token) ?? Local.ReadAsync(path, maximum, token); }
        public ValueTask CreateDirectoryAsync(string path, CancellationToken token)
        { Interlocked.Increment(ref DirectoryCalls); return Directory?.Invoke(path, token) ?? Local.CreateDirectoryAsync(path, token); }
        public ValueTask WriteAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken token)
        { Interlocked.Increment(ref WriteCalls); Writes.Enqueue((path, bytes.ToArray())); return Write?.Invoke(path, bytes, token) ?? Local.WriteAsync(path, bytes, token); }
    }
    private sealed class Policy(Func<PreparedToolAction, bool>? allow = null) : IToolActionPolicy
    {
        public readonly ConcurrentQueue<PreparedToolAction> Actions = new();
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Actions.Enqueue(action); return ValueTask.FromResult(new ToolActionAuthorization(allow?.Invoke(action) ?? true)); }
    }
    private sealed class TemporaryFiles : IDisposable
    {
        private readonly string _parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        public string Root { get; }
        public List<string> Links { get; } = [];
        public TemporaryFiles()
        { Root = Path.GetFullPath(Path.Combine(_parent, "pisharp-read-write-" + Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(Root); }
        public string File(string name) => Path.GetFullPath(name, Root);
        public void Dispose()
        {
            if (Path.GetDirectoryName(Root) != _parent || !Path.GetFileName(Root).StartsWith("pisharp-read-write-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the owned temporary root.");
            foreach (var link in Links) if (System.IO.Directory.Exists(link)) System.IO.Directory.Delete(link);
            if (System.IO.Directory.Exists(Root)) System.IO.Directory.Delete(Root, recursive: true);
        }
    }
    private static PreparedToolAction Retarget(PreparedToolAction action, string target, string display, string? content = null)
    {
        var arguments = JsonNode.Parse(action.Arguments.ToString())!.AsObject(); arguments["path"] = target; arguments["displayPath"] = display;
        if (content is not null) arguments["content"] = content;
        return action with { Target = target, Arguments = JsonData.Parse(arguments.ToJsonString()) };
    }
    private static JsonData Arguments(object value) => JsonData.Parse(JsonSerializer.Serialize(value));
    private static ToolInvocation Invocation(string name, JsonData arguments)
    {
        var call = new ToolCallContent("file-call", name, arguments);
        return new(new AssistantMessage("fixture-api", "fixture-provider", "fixture-model", 0, [call], TokenUsage.Zero, StopReason.ToolUse), call, 0);
    }
    private static Task<ToolResult> Invoke(ReadWriteTools tools, string name, object arguments, CancellationToken token = default, Policy? policy = null) =>
        tools.CreateInvoker(policy ?? new Policy()).ExecuteAsync(Invocation(name, Arguments(arguments)), token).AsTask();
    private static void Success(ToolResult value) => Check(!value.IsError && value.Failure is null, "Expected successful native file result.");
    private static void Failure(ToolResult value, ToolFailureKind kind) => Check(value.IsError && value.Failure?.Kind == kind, "Expected file failure " + kind);
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
    private static void Sequence(IEnumerable<string> expected, IEnumerable<string> actual) => Check(expected.SequenceEqual(actual), "File tool declarations changed.");
    private static T Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task Settle(params Task[] tasks) { foreach (var task in tasks) { try { await task; } catch { } } }
}
