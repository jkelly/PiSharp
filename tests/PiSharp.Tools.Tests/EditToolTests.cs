using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Agent;
using PiSharp.Agent.Tools;
using PiSharp.Contracts;
using PiSharp.Tools.Files;

internal static class EditToolTests
{
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("edit owned schema prepares arrays object string and appended legacy replacements", ArgumentVariants),
        ("edit disjoint reverse order replacements use only original content", OriginalMatching),
        ("edit ambiguous overlapping missing empty and no-op plans leave real bytes unchanged", InvalidPlans),
        ("edit fuzzy NFKC quotes dashes spaces preserve untouched original lines", FuzzyPreservation),
        ("edit BOM CRLF mixed endings and supplementary Unicode real bytes", EncodingAndEndings),
        ("edit native diff context line numbering and independently applicable patches", DiffRoundTrips),
        ("edit transformed exact final action policy handoff and denied calls have no effects", FinalAction),
        ("edit exact-byte outside-change conflict preserves outside bytes and next reservation", OutsideChange),
        ("edit two queued edits read the preceding committed content", QueuedEdits),
        ("edit shared write queue holds cancellation cleanup and permits unrelated targets", SharedWriteCleanup),
        ("edit cancellation after canonical/read/conflict/write awaits preserves known effects", CancellationBoundaries),
        ("edit input output argument diff budgets and write faults reject or expose effects", LimitsAndFaults)
    ];

    private static async Task ArgumentVariants()
    {
        using var temp = new TemporaryFiles(); var operations = new Operations(); var tool = Tool(temp, operations); var policy = new Policy();
        var properties = tool.Declaration.Value.GetProperty("parameters").GetProperty("properties");
        Check(!properties.TryGetProperty("oldText", out _) && !properties.TryGetProperty("newText", out _), "Legacy fields leaked into public schema.");
        Equal("array", properties.GetProperty("edits").GetProperty("type").GetString());
        var inputs = new (string Input, string After)[]
        {
            ("{\"path\":\"file\",\"edits\":[{\"oldText\":\"a\",\"newText\":\"A\"}]}", "A\nb\n"),
            ("{\"path\":\"file\",\"edits\":{\"oldText\":\"a\",\"newText\":\"A\"}}", "A\nb\n"),
            (JsonSerializer.Serialize(new { path = "file", edits = "[{\"oldText\":\"a\",\"newText\":\"A\"}]" }), "A\nb\n"),
            (JsonSerializer.Serialize(new { path = "file", edits = "{\"oldText\":\"a\",\"newText\":\"A\"}" }), "A\nb\n"),
            ("{\"path\":\"file\",\"oldText\":\"a\",\"newText\":\"A\"}", "A\nb\n"),
            ("{\"path\":\"file\",\"edits\":[{\"oldText\":\"a\",\"newText\":\"A\"}],\"oldText\":\"b\",\"newText\":\"B\"}", "A\nB\n"),
            ("{\"path\":\"file\",\"edits\":\"invalid json\",\"oldText\":\"a\",\"newText\":\"A\"}", "A\nb\n"),
            ("{\"path\":\"file\",\"edits\":[{\"oldText\":\"a\",\"newText\":\"A\"}],\"oldText\":1}", "A\nb\n")
        };
        foreach (var input in inputs)
        {
            await File.WriteAllTextAsync(temp.File("file"), "a\nb\n"); var result = await Invoke(tool, JsonData.Parse(input.Input), policy);
            Success(result); Equal(input.After, await File.ReadAllTextAsync(temp.File("file")));
            Equal(JsonValueKind.Array, policy.Actions.Last().Arguments.Value.GetProperty("edits").ValueKind);
        }
        var before = operations.WriteCalls;
        foreach (var input in new[] { "{}", "{\"path\":\"file\",\"edits\":[]}", "{\"path\":\"file\",\"edits\":\"invalid json\"}",
            "{\"path\":\"file\",\"edits\":[{\"oldText\":1,\"newText\":\"x\"}]}", "{\"path\":\"file\",\"oldText\":\"a\"}" })
            Failure(await Invoke(tool, JsonData.Parse(input)), ToolFailureKind.InvalidArguments);
        Equal(before, operations.WriteCalls); Throws<ArgumentNullException>(() => tool.CreateInvoker(null!));
    }

    private static async Task OriginalMatching()
    {
        using var temp = new TemporaryFiles(); var tool = Tool(temp);
        await File.WriteAllTextAsync(temp.File("file"), "alpha\nbeta\ngamma\ndelta\n");
        Success(await Invoke(tool, Input("file", new TextEdit("gamma\n", "GAMMA\n"), new TextEdit("alpha\n", "ALPHA\n"))));
        Equal("ALPHA\nbeta\nGAMMA\ndelta\n", await File.ReadAllTextAsync(temp.File("file")));
        await File.WriteAllTextAsync(temp.File("file"), "foo\nbar\nbaz\n");
        Success(await Invoke(tool, Input("file", new TextEdit("foo\n", "foo bar\n"), new TextEdit("bar\n", "BAR\n"))));
        Equal("foo bar\nBAR\nbaz\n", await File.ReadAllTextAsync(temp.File("file")));
        await File.WriteAllTextAsync(temp.File("file"), "a\nb\n");
        Success(await Invoke(tool, Input("file", new TextEdit("a", "a"), new TextEdit("b", "B"))));
        Equal("a\nB\n", await File.ReadAllTextAsync(temp.File("file")));
        await File.WriteAllTextAsync(temp.File("file"), "foo\n");
        var result = await Invoke(tool, Input("file", new TextEdit("foo", "created"), new TextEdit("created", "later")));
        Failure(result, ToolFailureKind.ExecutionError); Code(result, "NotFound"); Equal("foo\n", await File.ReadAllTextAsync(temp.File("file")));
    }

    private static async Task InvalidPlans()
    {
        using var temp = new TemporaryFiles(); var operations = new Operations(); var tool = Tool(temp, operations);
        var cases = new (string Before, TextEdit[] Edits, string Code)[]
        {
            ("foo foo foo", [new TextEdit("foo", "bar")], "Ambiguous"),
            ("one\ntwo\nthree\n", [new TextEdit("one\ntwo\n", "ONE\n"), new TextEdit("two\nthree", "THREE")], "Overlap"),
            ("abcdef", [new TextEdit("abcdef", "all"), new TextEdit("cd", "inside")], "Overlap"),
            ("a\nb\n", [new TextEdit("a", "A"), new TextEdit("missing", "M")], "NotFound"),
            ("content", [new TextEdit("", "new")], "EmptyOldText"), ("content", [new TextEdit("content", "content")], "NoChange"),
            ("", [new TextEdit("x", "y")], "NotFound"), ("hello   \nhello\n", [new TextEdit("hello", "new")], "Ambiguous")
        };
        foreach (var test in cases)
        {
            var bytes = Encoding.UTF8.GetBytes(test.Before); await File.WriteAllBytesAsync(temp.File("file"), bytes);
            var count = operations.WriteCalls; var result = await Invoke(tool, Input("file", test.Edits));
            Failure(result, ToolFailureKind.ExecutionError); Code(result, test.Code); Equal(count, operations.WriteCalls);
            var actual = await File.ReadAllBytesAsync(temp.File("file")); Check(actual.SequenceEqual(bytes), "Invalid plan changed original bytes.");
            Check(!result.Details.Value.GetProperty("fileOperation").GetProperty("writeAttempted").GetBoolean(), "Invalid plan admitted overwrite.");
        }
    }

    private static async Task FuzzyPreservation()
    {
        using var temp = new TemporaryFiles(); var tool = Tool(temp);
        var before = "keep before  \nfirst target  \nfirst after\nkeep middle   \nsecond target  \nsecond after\nkeep after  \n";
        await File.WriteAllTextAsync(temp.File("file"), before);
        var result = await Invoke(tool, Input("file", new TextEdit("first target\nfirst after", "FIRST\nFIRST2"), new TextEdit("second target\nsecond after", "SECOND\nSECOND2")));
        Success(result); var after = "keep before  \nFIRST\nFIRST2\nkeep middle   \nSECOND\nSECOND2\nkeep after  \n";
        Equal(after, await File.ReadAllTextAsync(temp.File("file"))); Equal(after, ApplyPatch(before, result.Details.Value.GetProperty("patch").GetString()!));
        await File.WriteAllTextAsync(temp.File("file"), "replace me   \nafter   \n");
        Success(await Invoke(tool, Input("file", new TextEdit("replace me\n", "after\n")))); Equal("after\nafter   \n", await File.ReadAllTextAsync(temp.File("file")));
        var cases = new (string Before, string Old, string New, string After)[]
        {
            ("keep \u2018glyph\u2019  \n\uff21\uff22\uff23\ncafe\u0301\n", "ABC\ncaf\u00e9\n", "XYZ\ncoffee\n", "keep \u2018glyph\u2019  \nXYZ\ncoffee\n"),
            ("console.log(\u2018hello\u2019);\n", "console.log('hello');", "console.log('world');", "console.log('world');\n"),
            ("range: 1\u20135\nbreak\u2014here\n", "range: 1-5\nbreak-here", "range: 10-50\nbreak--here", "range: 10-50\nbreak--here\n"),
            ("hello\u00a0world\n", "hello world", "hello universe", "hello universe\n")
        };
        foreach (var test in cases)
        { await File.WriteAllTextAsync(temp.File("file"), test.Before); Success(await Invoke(tool, Input("file", new TextEdit(test.Old, test.New)))); Equal(test.After, await File.ReadAllTextAsync(temp.File("file"))); }
        Equal("x\u0085", EditMatcher.NormalizeForFuzzyMatch("x\u0085")); Equal("x", EditMatcher.NormalizeForFuzzyMatch("x\ufeff"));
        // Retain the upstream JS split-empty-pattern quirk rather than silently inventing empty-old semantics.
        Equal("YX", EditPlan.Create("X", [new TextEdit(" ", "Y")], "file").Content);
    }

    private static async Task EncodingAndEndings()
    {
        using var temp = new TemporaryFiles(); var tool = Tool(temp);
        foreach (var test in new[]
        {
            (Before: "\ufefffirst\r\nsecond\r\nthird\r\n", Old: "second\n", New: "SECOND\n", After: "\ufefffirst\r\nSECOND\r\nthird\r\n"),
            (Before: "first\r\nsecond\nthird\r", Old: "second\n", New: "SECOND\n", After: "first\r\nSECOND\r\nthird\r\n"),
            (Before: "first\nsecond\r\nthird\r", Old: "second\r\n", New: "SECOND\r\n", After: "first\nSECOND\nthird\n"),
            (Before: "\U0001f642 before\n\u6587 after\n", Old: "\U0001f642 before", New: "\U0001f680 changed", After: "\U0001f680 changed\n\u6587 after\n")
        })
        {
            await File.WriteAllBytesAsync(temp.File("file"), Encoding.UTF8.GetBytes(test.Before)); Success(await Invoke(tool, Input("file", new TextEdit(test.Old, test.New))));
            var bytes = await File.ReadAllBytesAsync(temp.File("file")); Check(bytes.SequenceEqual(Encoding.UTF8.GetBytes(test.After)), "BOM/newline/Unicode bytes differ from the authored source-derived expectation.");
        }
    }

    private static Task DiffRoundTrips()
    {
        var example = DiffFormatter.Format("file", "a\nb\nc\n", "a\nB\nc\n");
        Equal(" 1 a\n-2 b\n+2 B\n 3 c", example.Diff); Equal(2, example.FirstChangedLine);
        Equal("--- file\n+++ file\n@@ -1,3 +1,3 @@\n a\n-b\n+B\n c\n", example.Patch);
        var inputs = new List<string> { "", "tail", "tail\n", "\n" };
        for (var length = 1; length <= 3; length++)
            for (var mask = 0; mask < 1 << length; mask++) inputs.Add(string.Concat(Enumerable.Range(0, length).Select(bit => (mask & 1 << bit) == 0 ? "a\n" : "b\n")));
        foreach (var before in inputs) foreach (var after in inputs)
        {
            var diff = DiffFormatter.Format("file", before, after); Equal(after, ApplyPatch(before, diff.Patch));
            Check((before == after) == (diff.FirstChangedLine is null), "First changed line disagrees with actual content equality.");
        }
        var many = string.Join('\n', Enumerable.Range(1, 600).Select(value => "line " + value.ToString("D3", System.Globalization.CultureInfo.InvariantCulture))) + "\n";
        var plan = EditPlan.Create(many, [new TextEdit("line 100\n", "LINE 100\n"), new TextEdit("line 300\n", "LINE 300\n"), new TextEdit("line 500\n", "LINE 500\n")], "file");
        var gapped = DiffFormatter.Format("file", plan.BaseContent, plan.NewContent); Equal(100, gapped.FirstChangedLine);
        Check(gapped.Diff.Contains("...") && !gapped.Diff.Contains("line 250") && gapped.Diff.Split('\n').Length < 50, "Large diff context was not bounded to source-shaped neighborhoods.");
        Equal(plan.NewContent, ApplyPatch(plan.BaseContent, gapped.Patch));
        return Task.CompletedTask;
    }

    private static async Task FinalAction()
    {
        using var temp = new TemporaryFiles(); var operations = new Operations(); var tool = Tool(temp, operations);
        await File.WriteAllTextAsync(temp.File("original"), "original"); await File.WriteAllTextAsync(temp.File("other"), "target");
        var input = Input("original", new TextEdit("original", "unused")); var denied = new Policy(_ => false);
        Failure(await Invoke(tool, input, denied), ToolFailureKind.Blocked); Equal(0, operations.ReadCalls); Equal(0, operations.WriteCalls);
        var invalidPolicy = new Policy(); var invalid = tool.CreateInvoker(invalidPolicy, [(_, action, _) => ValueTask.FromResult(action with { Target = temp.File("other") })]);
        Failure(await invalid.ExecuteAsync(Invocation(input), default), ToolFailureKind.InvalidArguments); Equal(0, invalidPolicy.Actions.Count);
        PreparedToolAction? transformed = null;
        var policy = new Policy(action => { Check(ReferenceEquals(transformed, action), "Policy did not receive exact final action instance."); return true; });
        var invoker = tool.CreateInvoker(policy, [(_, action, _) =>
        {
            var arguments = JsonNode.Parse(action.Arguments.ToString())!.AsObject(); arguments["path"] = temp.File("other"); arguments["displayPath"] = "other";
            arguments["edits"] = JsonNode.Parse("[{\"oldText\":\"target\",\"newText\":\"transformed\"}]");
            transformed = action with { Target = temp.File("other"), Arguments = JsonData.Parse(arguments.ToJsonString()) }; return ValueTask.FromResult(transformed);
        }]);
        Success(await invoker.ExecuteAsync(Invocation(input), default)); Equal("transformed", await File.ReadAllTextAsync(temp.File("other")));
        Equal("original", await File.ReadAllTextAsync(temp.File("original"))); Equal(temp.File("other"), operations.Writes.Single().Path);
    }

    private static async Task OutsideChange()
    {
        using var temp = new TemporaryFiles(); var operations = new Operations(); var tool = Tool(temp, operations); await File.WriteAllTextAsync(temp.File("file"), "original");
        var reads = 0;
        operations.Read = async (path, maximum, token) =>
        {
            if (Interlocked.Increment(ref reads) == 2) await operations.Local.WriteAsync(path, Encoding.UTF8.GetBytes("outside"), default);
            return await operations.Local.ReadAsync(path, maximum, token);
        };
        var result = await Invoke(tool, Input("file", new TextEdit("original", "planned"))); Failure(result, ToolFailureKind.ExecutionError); Code(result, "OutsideChange");
        Equal(0, operations.WriteCalls); Equal("outside", await File.ReadAllTextAsync(temp.File("file")));
        operations.Read = null; Success(await Invoke(tool, Input("file", new TextEdit("outside", "next")))); Equal("next", await File.ReadAllTextAsync(temp.File("file")));
        Equal(0, tool.MutationSnapshot.RegisteredOperations);
    }

    private static async Task QueuedEdits()
    {
        using var temp = new TemporaryFiles(); var operations = new Operations(); var tool = Tool(temp, operations); await File.WriteAllTextAsync(temp.File("file"), "zero\n");
        var entered = Gate(); var release = Gate(); var writes = 0;
        operations.Write = async (path, bytes, token) =>
        { if (Interlocked.Increment(ref writes) == 1) { entered.TrySetResult(); await release.Task; } await operations.Local.WriteAsync(path, bytes, token); };
        var first = Invoke(tool, Input("file", new TextEdit("zero", "one"))); await entered.Task;
        var second = Invoke(tool, Input("file", new TextEdit("one", "two")));
        try
        {
            Equal(2, operations.ReadCalls); Check(!second.IsCompleted, "Queued edit read before predecessor committed.");
            release.TrySetResult(); Success(await first); Success(await second); Equal("two\n", await File.ReadAllTextAsync(temp.File("file")));
            Equal(4, operations.ReadCalls); Equal(0, tool.MutationSnapshot.RegisteredOperations);
        }
        finally { release.TrySetResult(); await Settle(first, second); }
    }

    private static async Task SharedWriteCleanup()
    {
        using var temp = new TemporaryFiles(); using var cancellation = new CancellationTokenSource();
        var operations = new Operations(); var queue = Queue(operations); var tool = Tool(temp, operations, queue);
        var writer = new ReadWriteTools(temp.Root, temp.Root, operations, mutationQueue: queue); await File.WriteAllTextAsync(temp.File("file"), "original");
        var entered = Gate(); var blocked = Gate(); var cleanup = Gate(); var release = Gate(); var successor = Gate(); var calls = 0;
        operations.Write = async (path, bytes, token) =>
        {
            if (path == temp.File("file") && Interlocked.Increment(ref calls) == 1)
            {
                await operations.Local.WriteAsync(path, bytes[..2], default); entered.TrySetResult();
                try { await blocked.Task.WaitAsync(token); } finally { cleanup.TrySetResult(); await release.Task; }
                return;
            }
            if (path == temp.File("file")) successor.TrySetResult();
            await operations.Local.WriteAsync(path, bytes, token);
        };
        var first = Invoke(tool, Input("file", new TextEdit("original", "changed")), token: cancellation.Token); await entered.Task;
        var call = new ToolCallContent("writer", "write", JsonData.Parse(JsonSerializer.Serialize(new { path = "file", content = "following writer" })));
        var invocation = new ToolInvocation(new AssistantMessage("fixture-api", "fixture-provider", "fixture-model", 0, [call], TokenUsage.Zero, StopReason.ToolUse), call, 0);
        var second = writer.CreateInvoker(new Policy()).ExecuteAsync(invocation, default).AsTask();
        try
        {
            cancellation.Cancel(); await cleanup.Task; Check(!first.IsCompleted && !successor.Task.IsCompleted, "Edit cancellation released shared write reservation before cleanup.");
            Success(await Invoke(tool, Input("unrelated", new TextEdit("one", "two")), setup: () => File.WriteAllTextAsync(temp.File("unrelated"), "one")));
            Check(!successor.Task.IsCompleted, "Unrelated edit released the same-key reservation.");
            release.TrySetResult(); var result = await first; Failure(result, ToolFailureKind.Canceled);
            Check(result.Details.Value.GetProperty("fileOperation").GetProperty("writeAttempted").GetBoolean() &&
                !result.Details.Value.GetProperty("fileOperation").GetProperty("writeCompleted").GetBoolean(), "Canceled edit concealed partial overwrite.");
            Success(await second); Equal("following writer", await File.ReadAllTextAsync(temp.File("file"))); Equal(0, queue.Snapshot.RegisteredOperations);
        }
        finally { cancellation.Cancel(); blocked.TrySetResult(); release.TrySetResult(); await Settle(first, second); }
    }

    private static async Task CancellationBoundaries()
    {
        using var temp = new TemporaryFiles(); await File.WriteAllTextAsync(temp.File("file"), "original");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel(); var operations = new Operations(); Failure(await Invoke(Tool(temp, operations), Input("file", new TextEdit("original", "new")), token: canceled.Token), ToolFailureKind.Canceled);
            Equal(0, operations.ReadCalls); Equal(0, operations.WriteCalls);
        }
        for (var boundary = 0; boundary < 3; boundary++)
        {
            using var cancellation = new CancellationTokenSource(); var operations = new Operations(); var count = 0;
            if (boundary == 0) operations.Canonicalize = async (path, token) => { var target = await operations.Local.CanonicalizeAsync(path, token); cancellation.Cancel(); return target; };
            else operations.Read = async (path, maximum, token) => { var bytes = await operations.Local.ReadAsync(path, maximum, token); if (Interlocked.Increment(ref count) == boundary) cancellation.Cancel(); return bytes; };
            Failure(await Invoke(Tool(temp, operations), Input("file", new TextEdit("original", "new")), token: cancellation.Token), ToolFailureKind.Canceled);
            Equal(0, operations.WriteCalls); Equal("original", await File.ReadAllTextAsync(temp.File("file")));
        }
        using var afterWrite = new CancellationTokenSource(); var completed = new Operations();
        completed.Write = async (path, bytes, _) => { await completed.Local.WriteAsync(path, bytes, default); afterWrite.Cancel(); };
        var result = await Invoke(Tool(temp, completed), Input("file", new TextEdit("original", "completed")), token: afterWrite.Token);
        Failure(result, ToolFailureKind.Canceled); Check(result.Details.Value.GetProperty("fileOperation").GetProperty("writeCompleted").GetBoolean(), "Late cancellation lost known completed edit.");
        Equal("completed", await File.ReadAllTextAsync(temp.File("file")));
    }

    private static async Task LimitsAndFaults()
    {
        using var temp = new TemporaryFiles(); var operations = new Operations(); await File.WriteAllTextAsync(temp.File("file"), "abcdefgh");
        var small = Tool(temp, operations, options: new(MaximumInputBytes: 8, MaximumOutputBytes: 8));
        Failure(await Invoke(small, Input("file", new TextEdit("a", "123456789"))), ToolFailureKind.InvalidArguments); Equal(0, operations.ReadCalls);
        await File.WriteAllTextAsync(temp.File("file"), "abcdefghi"); var oversized = await Invoke(small, Input("file", new TextEdit("a", "A"))); Failure(oversized, ToolFailureKind.ExecutionError); Code(oversized, "ResourceLimit");
        await File.WriteAllTextAsync(temp.File("file"), "a\nb\n");
        var noDiff = Tool(temp, operations, options: new(DiffOptions: new(MaximumOutputCharacters: 16)));
        Code(await Invoke(noDiff, Input("file", new TextEdit("a", "A"))), "ResourceLimit"); Equal(0, operations.WriteCalls);
        var noTrace = Tool(temp, operations, options: new(DiffOptions: new(MaximumTraceCells: 1)));
        Code(await Invoke(noTrace, Input("file", new TextEdit("a", "A"))), "ResourceLimit"); Equal(0, operations.WriteCalls);
        var oneEdit = Tool(temp, operations, options: new(MaximumEdits: 1)); Failure(await Invoke(oneEdit, Input("file", new TextEdit("a", "A"), new TextEdit("b", "B"))), ToolFailureKind.InvalidArguments);
        foreach (var bytes in new byte[][] { [0xff, 0xfe, 0x41, 0x00], [0xc3, 0x28], [0x61, 0x00, 0x62], Encoding.ASCII.GetBytes("GIF89a") })
        { await File.WriteAllBytesAsync(temp.File("file"), bytes); var failure = await Invoke(Tool(temp, operations), Input("file", new TextEdit("a", "A"))); Code(failure, "UnsupportedContent"); Equal(0, operations.WriteCalls); }
        await File.WriteAllTextAsync(temp.File("file"), "original"); var calls = 0;
        operations.Write = async (path, bytes, token) =>
        { if (Interlocked.Increment(ref calls) == 1) { await operations.Local.WriteAsync(path, bytes[..2], default); throw new IOException("private fault"); } await operations.Local.WriteAsync(path, bytes, token); };
        var tool = Tool(temp, operations); var failed = await Invoke(tool, Input("file", new TextEdit("original", "changed"))); Failure(failed, ToolFailureKind.ExecutionError); Code(failed, "EditIoFailure");
        Check(!failed.Content.Single().Text.Contains("private", StringComparison.Ordinal) && failed.Details.Value.GetProperty("fileOperation").GetProperty("writeAttempted").GetBoolean(), "Write failure leaked host text or concealed the attempted effect.");
        Equal("ch", await File.ReadAllTextAsync(temp.File("file"))); Success(await Invoke(tool, Input("file", new TextEdit("ch", "next")))); Equal("next", await File.ReadAllTextAsync(temp.File("file")));
    }

    // Independent unified-patch consumer: checks context/removal payloads and declared line counts, including missing EOF LF.
    private static string ApplyPatch(string before, string patch)
    {
        var source = Lines(before); var output = new List<string>(); var copied = 0; var rows = patch.Split('\n');
        for (var row = 2; row < rows.Length;)
        {
            if (rows[row].Length == 0) { row++; continue; }
            var match = System.Text.RegularExpressions.Regex.Match(rows[row++], "^@@ -([0-9]+),([0-9]+) \\+([0-9]+),([0-9]+) @@$"); Check(match.Success, "Native patch hunk header is invalid.");
            var oldCount = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture); var newCount = int.Parse(match.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture);
            var start = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) - (oldCount == 0 ? 0 : 1);
            while (copied < start) output.Add(source[copied++]); var removed = 0; var added = 0;
            while (row < rows.Length && rows[row].Length > 0 && !rows[row].StartsWith("@@", StringComparison.Ordinal))
            {
                var line = rows[row++]; var content = line[1..] + "\n";
                if (row < rows.Length && rows[row] == "\\ No newline at end of file") { content = content[..^1]; row++; }
                if (line[0] is ' ' or '-') { Equal(content, source[copied++]); removed++; }
                if (line[0] is ' ' or '+') { output.Add(content); added++; }
            }
            Equal(oldCount, removed); Equal(newCount, added);
        }
        while (copied < source.Count) output.Add(source[copied++]); return string.Concat(output);
    }
    private static List<string> Lines(string text)
    {
        var lines = new List<string>(); var remaining = text;
        while (remaining.Length > 0) { var newline = remaining.IndexOf('\n'); var length = newline < 0 ? remaining.Length : newline + 1; lines.Add(remaining[..length]); remaining = remaining[length..]; }
        return lines;
    }
    private sealed class Operations : IFileOperations
    {
        public readonly LocalFileOperations Local = new(); public int ReadCalls; public int WriteCalls;
        public readonly ConcurrentQueue<(string Path, byte[] Bytes)> Writes = new();
        public Func<string, int, CancellationToken, ValueTask<ReadOnlyMemory<byte>>>? Read;
        public Func<string, ReadOnlyMemory<byte>, CancellationToken, ValueTask>? Write;
        public Func<string, CancellationToken, ValueTask<string>>? Canonicalize;
        public ValueTask<bool> ExistsAsync(string path, CancellationToken token) => Local.ExistsAsync(path, token);
        public ValueTask<string> CanonicalizeAsync(string path, CancellationToken token) => Canonicalize?.Invoke(path, token) ?? Local.CanonicalizeAsync(path, token);
        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(string path, int maximum, CancellationToken token) { Interlocked.Increment(ref ReadCalls); return Read?.Invoke(path, maximum, token) ?? Local.ReadAsync(path, maximum, token); }
        public ValueTask CreateDirectoryAsync(string path, CancellationToken token) => Local.CreateDirectoryAsync(path, token);
        public ValueTask WriteAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken token) { Interlocked.Increment(ref WriteCalls); Writes.Enqueue((path, bytes.ToArray())); return Write?.Invoke(path, bytes, token) ?? Local.WriteAsync(path, bytes, token); }
    }
    private sealed class Policy(Func<PreparedToolAction, bool>? allow = null) : IToolActionPolicy
    {
        public readonly ConcurrentQueue<PreparedToolAction> Actions = new();
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Actions.Enqueue(action); return ValueTask.FromResult(new ToolActionAuthorization(allow?.Invoke(action) ?? true)); }
    }
    private sealed class TemporaryFiles : IDisposable
    {
        private readonly string _parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())); public string Root { get; }
        public TemporaryFiles() { Root = Path.Combine(_parent, "pisharp-edit-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root); }
        public string File(string name) => Path.Combine(Root, name);
        public void Dispose()
        {
            if (Path.GetDirectoryName(Root) != _parent || !Path.GetFileName(Root).StartsWith("pisharp-edit-", StringComparison.Ordinal)) throw new InvalidOperationException("Refusing cleanup outside owned edit test root.");
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
    private static FileMutationQueue Queue(IFileOperations operations) => new(async (path, token) =>
    { var key = await operations.CanonicalizeAsync(path, token); return OperatingSystem.IsWindows() ? key.ToUpperInvariant() : key; });
    private static EditTool Tool(TemporaryFiles temp, Operations? operations = null, FileMutationQueue? queue = null, EditToolOptions? options = null)
    { operations ??= new(); return new(temp.Root, temp.Root, queue ?? Queue(operations), operations, options); }
    private static JsonData Input(string path, params TextEdit[] edits) => JsonData.Parse(JsonSerializer.Serialize(new { path, edits = edits.Select(edit => new { oldText = edit.OldText, newText = edit.NewText }) }));
    private static ToolInvocation Invocation(JsonData input)
    { var call = new ToolCallContent("edit-call", "edit", input); return new(new AssistantMessage("fixture-api", "fixture-provider", "fixture-model", 0, [call], TokenUsage.Zero, StopReason.ToolUse), call, 0); }
    private static async Task<ToolResult> Invoke(EditTool tool, JsonData input, Policy? policy = null, CancellationToken token = default, Func<Task>? setup = null)
    { if (setup is not null) await setup(); return await tool.CreateInvoker(policy ?? new Policy()).ExecuteAsync(Invocation(input), token); }
    private static void Success(ToolResult result) => Check(!result.IsError && result.Failure is null, "Expected successful edit.");
    private static void Failure(ToolResult result, ToolFailureKind kind) => Check(result.IsError && result.Failure?.Kind == kind, "Expected edit failure " + kind);
    private static void Code(ToolResult result, string code) { Failure(result, ToolFailureKind.ExecutionError); Equal(code, result.Details.Value.GetProperty("fileOperation").GetProperty("code").GetString()); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task Settle(params Task[] tasks) { foreach (var task in tasks) { try { await task; } catch { } } }
}
