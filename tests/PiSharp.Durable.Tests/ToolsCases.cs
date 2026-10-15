// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/test/tools.test.ts, as tools.<group>.<case>, on LocalExecutionEnv
// (upstream NodeExecutionEnv). Its bash and powershell cases follow Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (tool task v2, durable
// work list area a: a nonzero exit is an error result with structured output; the fake api's retainedOutput()); the read cases (images) and
// the header move to v1.2.0 with area e, which owns the row. Verbatim apart from these adaptations:
// - The tools take validated JSON arguments and the api is FakeToolApi (ToolsSupport.cs), upstream's fakeApi; jsdiff's applyPatch is
//   ToolRunner.ApplyPatch.
// - Upstream's `Object.create(env)` override of openBinaryReader is a LocalExecutionEnv subclass wrapping the reader; `class extends
//   BlockingWriteExecutionEnv { id = "other" }` overrides LocalFileSystem.Id.
// - "receivedSignal toBe controller.signal": the prepare callback gets the call's context object itself, with its cancellation.
// - Symbolic links: the cases that create links run their assertions only where the process may create them (on Windows that needs
//   Developer Mode or elevation), as env.node.* does. "keys a missing file whose name contains a backslash like the created file" is skipped
//   on Windows, as upstream skips it (a backslash is a separator there).
// - bash: on Windows the shell is Git Bash, as for upstream's NodeExecutionEnv; "prepares command, cwd, and an explicit environment" compares
//   $PWD only on POSIX, as upstream does. "runs real PowerShell with UTF-8 output" runs only on Windows, as upstream.
using System.Collections.Immutable;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Tools;
using PiSharp.Durable.Tools.Env;
using static ToolRunner;

internal static partial class Program
{
    private static readonly DurableContext ToolContext = DurableContext.Background;

    private static JsonObjectValue ToolArgs(params (string Key, JsonValue Value)[] properties) =>
        JsonValue.Object(properties.Select(property => KeyValuePair.Create(property.Key, property.Value)));

    private static JsonArrayValue EditList(params (string OldText, string NewText)[] edits) =>
        JsonValue.Array(edits.Select(edit => (JsonValue)ToolArgs(("oldText", edit.OldText), ("newText", edit.NewText))));

    private static async Task WithToolDir(Func<string, Task> body)
    {
        var root = Temp("tools");
        try { await body(root); }
        finally { await RemoveTempDirAsync(root); }
    }

    private static (string, Func<Task>) ToolCase(string slug, Func<string, Task> body) => Case("tools." + slug, () => WithToolDir(body));

    private static (string, Func<Task>) ToolEnvCase(string slug, Func<LocalExecutionEnv, Task> body) =>
        ToolCase(slug, root => body(new LocalExecutionEnv(root)));

    private static string FileText(string path) => File.ReadAllText(path, new System.Text.UTF8Encoding(false));

    private static async Task<string> EnvText(IExecutionEnv env, string path) => (await env.ReadTextFileAsync(path, ToolContext)).GetOrThrow();

    private static async Task WriteText(IExecutionEnv env, string path, string content) => (await env.WriteFileAsync(path, content, ToolContext)).GetOrThrow();

    private static async Task<Exception> Rejects(Func<Task> run, string what)
    {
        try { await run(); }
        catch (Exception error) { return error; }
        throw new InvalidOperationException(what + ": expected a rejection");
    }

    private static void Contains(string text, string part, string what) =>
        Check(text.Contains(part, StringComparison.Ordinal), $"{what}: expected <{part}> in <{text}>");

    private static JsonObjectValue ReadTruncation(ToolExecutionResult result) => (JsonObjectValue)((JsonObjectValue)result.Details!)["truncation"];

    private static IEnumerable<(string, Func<Task>)> ToolsCases()
    {
        yield return Case("tools.fail-with-an-ordinary-error-when-no-environment-is-configured", async () =>
        {
            var error = await Rejects(() => Run(CodingTools.CreateReadTool(), ToolArgs(("path", "x")), null), "no environment");
            Contains(error.Message, "No execution environment", "message");
        });

        // ─── read ───────────────────────────────────────────────────────────
        foreach (var signature in new[] { "GIF87a", "GIF89a" })
            yield return Case($"tools.read.detects-the-complete-{signature}-signature", () =>
                Equal("image/gif", ImageDetection.DetectSupportedImageMimeType(System.Text.Encoding.ASCII.GetBytes(signature)), "mime"));

        yield return ToolEnvCase("read.reads-text-with-offsets-and-limits-and-reports-continuation-as-a-diagnostic", async env =>
        {
            await WriteText(env, "test.txt", string.Join("\n", Enumerable.Range(1, 100).Select(index => $"Line {index}")));
            var result = await Run(CodingTools.CreateReadTool(), ToolArgs(("path", "test.txt"), ("offset", 41), ("limit", 20)), env);
            var output = result.Text;
            Check(!output.Contains("Line 40", StringComparison.Ordinal), "no line 40");
            Contains(output, "Line 41", "line 41");
            Contains(output, "Line 60", "line 60");
            Check(!output.Contains("Line 61", StringComparison.Ordinal), "no line 61");
            Check(!output.Contains("more lines", StringComparison.Ordinal), "no notice in the content");
            Equal("40 more lines in file. Use offset=61 to continue.", result.DiagnosticText, "diagnostic");
        });

        yield return ToolEnvCase("read.truncates-large-text-by-line-count", async env =>
        {
            await WriteText(env, "large.txt", string.Join("\n", Enumerable.Range(1, 2500).Select(index => $"Line {index}")));
            var result = await Run(CodingTools.CreateReadTool(), ToolArgs(("path", "large.txt")), env);
            Equal("Showing lines 1-2000 of 2500. Use offset=2001 to continue.", result.DiagnosticText, "diagnostic");
            Equal("truncated", result.Result.Diagnostics!.Value[0].Code, "code");
            var truncation = ReadTruncation(result.Result);
            Equal("true|lines|2500|2000", $"{truncation["truncated"].ToJson()}|{((JsonStringValue)truncation["truncatedBy"]).Value}|" +
                $"{truncation["totalLines"].ToJson()}|{truncation["outputLines"].ToJson()}", "truncation");
        });

        yield return ToolEnvCase("read.does-not-count-a-trailing-newline-as-an-extra-line-at-the-truncation-limit", async env =>
        {
            await WriteText(env, "exact.txt", string.Join("\n", Enumerable.Repeat("x", 2000)) + "\n");
            var result = await Run(CodingTools.CreateReadTool(), ToolArgs(("path", "exact.txt")), env);
            Equal(null, result.Result.Details, "details");
            Equal(0, result.Result.Diagnostics!.Value.Length, "diagnostics");
        });

        yield return ToolEnvCase("read.shows-the-start-of-a-line-longer-than-the-byte-limit", async env =>
        {
            await WriteText(env, "long.txt", new string('\u00E9', 40_000) + "\nnext\n");
            var result = await Run(CodingTools.CreateReadTool(), ToolArgs(("path", "long.txt")), env);
            // Two-byte characters: the cut lands on a character boundary at or below the limit.
            Equal(new string('\u00E9', 25_600), result.Text, "text");
            Equal("Line 1 is 78.1KB, exceeds the 50.0KB limit; showing its first 50.0KB. Use bash: sed -n '1p' long.txt | tail -c +51201",
                result.DiagnosticText, "diagnostic");
            var truncation = ReadTruncation(result.Result);
            Equal("true|true|51200|1", $"{truncation["truncated"].ToJson()}|{truncation["firstLineExceedsLimit"].ToJson()}|" +
                $"{truncation["outputBytes"].ToJson()}|{truncation["outputLines"].ToJson()}", "truncation");
            Check(!truncation.ContainsKey("content"), "no truncation.content");
        });

        yield return ToolEnvCase("read.rejects-offsets-beyond-the-file", async env =>
        {
            await WriteText(env, "short.txt", "one\ntwo\nthree");
            var error = await Rejects(() => Run(CodingTools.CreateReadTool(), ToolArgs(("path", "short.txt"), ("offset", 100)), env), "offset");
            Contains(error.Message, "Offset 100 is beyond end of file (3 lines total)", "message");
        });

        yield return ToolCase("read.reads-a-log-that-grows-while-it-is-read", async root =>
        {
            var env = new GrowingLogEnv(root);
            await WriteText(env, "app.log", "one\ntwo\n");
            env.LogPath = Path.Combine(root, "app.log");
            var result = await Run(CodingTools.CreateReadTool(), ToolArgs(("path", "app.log"), ("limit", 2)), env);
            Equal("one\ntwo", result.Text, "text");
        });

        yield return ToolEnvCase("read.reports-images-by-content-as-unsupported", async env =>
        {
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNgYGD4DwABBAEAX+XDSwAAAABJRU5ErkJggg==");
            (await env.WriteFileAsync("image.txt", png, ToolContext)).GetOrThrow();
            var result = await Run(CodingTools.CreateReadTool(), ToolArgs(("path", "image.txt")), env);
            Check(result.Result.Output is { Count: 0 } && result.Result.IsError == true, "empty error result");
            Equal("image.txt is an image (image/png); reading images is not supported", result.DiagnosticText, "diagnostic");
        });

        // ─── write ──────────────────────────────────────────────────────────
        yield return ToolEnvCase("write.writes-files-and-creates-parent-directories", async env =>
        {
            var result = await Run(CodingTools.CreateWriteTool(), ToolArgs(("path", "nested/dir/file.txt"), ("content", "hello")), env);
            Equal("Successfully wrote to nested/dir/file.txt", result.Text, "text");
            Equal("hello", await EnvText(env, "nested/dir/file.txt"), "file");
        });

        yield return ToolCase("write.keeps-the-mutation-queue-locked-until-an-aborted-write-settles", async root =>
        {
            var env = new BlockingWriteExecutionEnv(root);
            var tool = CodingTools.CreateWriteTool();
            using var controller = new CancellationTokenSource();
            var firstWrite = Run(tool, ToolArgs(("path", "file.txt"), ("content", "first\n")), env, ToolContext.WithCancellation(controller.Token));
            await env.FirstWriteStarted.Task;
            controller.Cancel();
            var secondWrite = Run(tool, ToolArgs(("path", "file.txt"), ("content", "second\n")), env);
            await Task.Delay(20);
            Check(!env.SecondWriteStarted, "the second write waits");
            env.FinishFirstWrite.Resolve();
            await Rejects(() => firstWrite, "first write");
            await secondWrite;
            Equal("second\n", await EnvText(env, "file.txt"), "file");
        });

        // ─── edit ───────────────────────────────────────────────────────────
        yield return ToolEnvCase("edit.applies-disjoint-edits-and-returns-both-diff-formats", async env =>
        {
            const string original = "alpha\nbeta\ngamma\ndelta\n";
            await WriteText(env, "edit.txt", original);
            var result = await Run(CodingTools.CreateEditTool(),
                ToolArgs(("path", "edit.txt"), ("edits", EditList(("alpha\n", "ALPHA\n"), ("gamma\n", "GAMMA\n")))), env);
            var details = (JsonObjectValue)result.Result.Details!;
            Equal("Successfully replaced 2 block(s) in edit.txt.", result.Text, "text");
            Contains(((JsonStringValue)details["diff"]).Value, "ALPHA", "diff");
            Contains(((JsonStringValue)details["diff"]).Value, "GAMMA", "diff");
            Equal("ALPHA\nbeta\nGAMMA\ndelta\n", ApplyPatch(original, ((JsonStringValue)details["patch"]).Value), "patched");
            Equal("ALPHA\nbeta\nGAMMA\ndelta\n", await EnvText(env, "edit.txt"), "file");
        });

        yield return Case("tools.edit.repairs-edits-sent-as-a-json-string-a-single-object-or-top-level-oldtext-newtext-without-mutating-them", () =>
        {
            var prepare = CodingTools.CreateEditTool().PrepareArguments!;
            var edit = ToolArgs(("oldText", "a"), ("newText", "b"));
            void Same(JsonValue expected, JsonValue actual, string what) =>
                Check(JsonValue.DeepEquals(expected, actual), $"{what}: expected {expected.ToJson()}, actual {actual.ToJson()}");
            var asString = ToolArgs(("path", "f"), ("edits", JsonValue.Array(edit).ToJson()));
            Same(ToolArgs(("path", "f"), ("edits", JsonValue.Array(edit))), prepare(asString), "a JSON string");
            Equal(JsonValue.Array(edit).ToJson(), ((JsonStringValue)asString["edits"]).Value, "unchanged input");
            Same(ToolArgs(("path", "f"), ("edits", JsonValue.Array(edit))), prepare(ToolArgs(("path", "f"), ("edits", edit.ToJson()))), "a JSON object string");
            Same(ToolArgs(("path", "f"), ("edits", JsonValue.Array(edit))), prepare(ToolArgs(("path", "f"), ("edits", edit))), "a single object");
            Same(ToolArgs(("path", "f"), ("edits", JsonValue.Array(edit, ToolArgs(("oldText", "c"), ("newText", "d"))))),
                prepare(ToolArgs(("path", "f"), ("edits", JsonValue.Array(edit)), ("oldText", "c"), ("newText", "d"))), "top-level oldText/newText");
            Same(ToolArgs(("path", "f"), ("edits", "not json")), prepare(ToolArgs(("path", "f"), ("edits", "not json"))), "not JSON");
        });

        yield return ToolEnvCase("edit.matches-all-edits-against-the-original-and-rejects-overlaps", async env =>
        {
            await WriteText(env, "edit.txt", "one\ntwo\nthree\n");
            var error = await Rejects(() => Run(CodingTools.CreateEditTool(), ToolArgs(("path", "edit.txt"),
                ("edits", EditList(("one\ntwo\n", "ONE\nTWO\n"), ("two\nthree\n", "TWO\nTHREE\n")))), env), "overlap");
            Contains(error.Message, "overlap", "message");
            Equal("one\ntwo\nthree\n", await EnvText(env, "edit.txt"), "file");
        });

        yield return ToolEnvCase("edit.rejects-missing-and-duplicate-target-text", async env =>
        {
            await WriteText(env, "edit.txt", "foo foo foo");
            var tool = CodingTools.CreateEditTool();
            Contains((await Rejects(() => Run(tool, ToolArgs(("path", "edit.txt"), ("edits", EditList(("bar", "baz")))), env), "missing")).Message,
                "Could not find the exact text", "missing");
            Contains((await Rejects(() => Run(tool, ToolArgs(("path", "edit.txt"), ("edits", EditList(("foo", "bar")))), env), "duplicate")).Message,
                "Found 3 occurrences", "duplicate");
        });

        yield return ToolCase("edit.keeps-the-mutation-queue-locked-until-an-aborted-edit-write-settles", async root =>
        {
            var env = new BlockingEditExecutionEnv(root);
            await WriteText(env, "file.txt", "alpha\nbeta\n");
            var tool = CodingTools.CreateEditTool();
            using var controller = new CancellationTokenSource();
            var firstEdit = Run(tool, ToolArgs(("path", "file.txt"), ("edits", EditList(("alpha", "ALPHA")))), env,
                ToolContext.WithCancellation(controller.Token));
            await env.FirstEditWriteStarted.Task;
            controller.Cancel();
            var secondEdit = Run(tool, ToolArgs(("path", "file.txt"), ("edits", EditList(("beta", "BETA")))), env);
            await Task.Delay(20);
            Check(!env.SecondEditWriteStarted, "the second edit waits");
            env.FinishFirstEditWrite.Resolve();
            Contains((await Rejects(() => firstEdit, "first edit")).Message, "Operation aborted", "first edit");
            await secondEdit;
            Check(env.FirstEditWriteSettled, "the first write settled");
            Equal("ALPHA\nBETA\n", await EnvText(env, "file.txt"), "file");
        });

        yield return ToolCase("edit.serializes-concurrent-edits-through-canonical-and-symlink-paths", async root =>
        {
            var env = new SlowReadExecutionEnv(root);
            await WriteText(env, "target.txt", "alpha\nbeta\ngamma\n");
            if (!TryCreateSymlinks(root, ("link.txt", "target.txt", false))) return;
            var tool = CodingTools.CreateEditTool();
            await Task.WhenAll(
                Run(tool, ToolArgs(("path", "target.txt"), ("edits", EditList(("alpha", "ALPHA")))), env),
                Run(tool, ToolArgs(("path", "link.txt"), ("edits", EditList(("beta", "BETA")))), env));
            Equal("ALPHA\nBETA\ngamma\n", await EnvText(env, "target.txt"), "file");
        });

        yield return ToolCase("edit.serializes-edits-of-one-file-across-environment-objects-of-one-file-system", async root =>
        {
            var first = new SlowReadExecutionEnv(root);
            var second = new SlowReadExecutionEnv(root);
            await WriteText(first, "file.txt", "alpha\nbeta\n");
            var tool = CodingTools.CreateEditTool();
            await Task.WhenAll(
                Run(tool, ToolArgs(("path", "file.txt"), ("edits", EditList(("alpha", "ALPHA")))), first),
                Run(tool, ToolArgs(("path", "file.txt"), ("edits", EditList(("beta", "BETA")))), second));
            Equal("ALPHA\nBETA\n", await EnvText(first, "file.txt"), "file");
        });

        yield return ToolCase("edit.serializes-a-new-file-created-through-a-symlinked-directory-with-its-canonical-path", async root =>
        {
            var env = new BlockingWriteExecutionEnv(root);
            Directory.CreateDirectory(Path.Combine(root, "real"));
            if (!TryCreateSymlinks(root, ("link", Path.Combine(root, "real"), true))) return;
            var tool = CodingTools.CreateWriteTool();
            var first = Run(tool, ToolArgs(("path", "link/new.txt"), ("content", "first\n")), env);
            await env.FirstWriteStarted.Task;
            var second = Run(tool, ToolArgs(("path", "real/new.txt"), ("content", "second\n")), env);
            await Task.Delay(20);
            Check(!env.SecondWriteStarted, "the second write waits");
            env.FinishFirstWrite.Resolve();
            await Task.WhenAll(first, second);
            Equal("second\n", await EnvText(env, "real/new.txt"), "file");
        });

        yield return ToolEnvCase("edit.keys-a-missing-file-whose-name-contains-a-backslash-like-the-created-file", async env =>
        {
            if (OperatingSystem.IsWindows()) return; // As upstream: a backslash is a path separator on Windows.
            var created = new ToolGate();
            var release = new ToolGate();
            var first = FileMutationQueue.RunAsync(env, "a\\b.txt", async () =>
            {
                await WriteText(env, "a\\b.txt", "first\n");
                created.Resolve();
                await release.Task;
                return 0;
            }, ToolContext);
            await created.Task;
            var entered = false;
            var second = FileMutationQueue.RunAsync(env, "a\\b.txt", () =>
            {
                entered = true;
                return Task.FromResult(0);
            }, ToolContext);
            await Task.Delay(20);
            Check(!entered, "the second mutation waits");
            release.Resolve();
            await Task.WhenAll(first, second);
            Check(entered, "the second mutation ran");
        });

        yield return ToolCase("edit.does-not-serialize-the-same-path-on-different-file-systems", async root =>
        {
            var local = new BlockingWriteExecutionEnv(root);
            var other = new OtherFileSystemEnv(root);
            var tool = CodingTools.CreateWriteTool();
            var blocked = Run(tool, ToolArgs(("path", "file.txt"), ("content", "first\n")), local);
            await local.FirstWriteStarted.Task;
            await Run(tool, ToolArgs(("path", "file.txt"), ("content", "second\n")), other);
            Check(other.SecondWriteStarted, "the other file system's write ran");
            local.FinishFirstWrite.Resolve();
            await blocked;
        });

        yield return ToolEnvCase("edit.edits-regular-files-through-symlinks", async env =>
        {
            await WriteText(env, "target.txt", "before\n");
            if (!TryCreateSymlinks(env.Cwd, ("link.txt", "target.txt", false))) return;
            await Run(CodingTools.CreateEditTool(), ToolArgs(("path", "link.txt"), ("edits", EditList(("before", "after")))), env);
            Equal("after\n", await EnvText(env, "target.txt"), "file");
        });

        yield return ToolEnvCase("edit.preserves-bom-and-crlf-line-endings", async env =>
        {
            await WriteText(env, "edit.txt", "\uFEFFone\r\ntwo\r\n");
            await Run(CodingTools.CreateEditTool(), ToolArgs(("path", "edit.txt"), ("edits", EditList(("two", "TWO")))), env);
            Equal("\uFEFFone\r\nTWO\r\n", await EnvText(env, "edit.txt"), "file");
        });

        // ─── powershell ─────────────────────────────────────────────────────
        yield return ToolCase("powershell.runs-the-command-with-pwsh-as-one-argument-forcing-utf-8-output", async root =>
        {
            var env = new ProgramsEnv(root, ["pwsh"], "h\u00E9llo\n", 0);
            var result = await Run(CodingTools.CreatePowerShellTool(), ToolArgs(("command", "Write-Output 'h\u00E9llo'")), env);
            Equal("h\u00E9llo\n", string.Concat(result.Output), "output");
            Equal(1, env.Commands.Count, "commands");
            Equal(string.Join("\u0001", ["pwsh", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
                "try { [Console]::OutputEncoding=[System.Text.Encoding]::UTF8 } catch {}\nWrite-Output 'h\u00E9llo'"]),
                string.Join("\u0001", env.Commands[0].Argv), "argv");
        });

        yield return ToolCase("powershell.falls-back-to-windows-powershell-and-reports-the-last-start-failure", async root =>
        {
            var windowsOnly = new ProgramsEnv(root, ["powershell"], "ok", 0);
            var result = await Run(CodingTools.CreatePowerShellTool(new PowerShellToolOptions { CommandPrefix = "$x = 1" }), ToolArgs(("command", "$x")),
                windowsOnly);
            Equal("ok", string.Concat(result.Output), "output");
            Equal("pwsh,powershell", string.Join(",", windowsOnly.Commands.Select(command => command.Argv[0])), "programs");
            Check(windowsOnly.Commands[1].Argv[^1].EndsWith("\n$x = 1\n$x", StringComparison.Ordinal), "prefixed script: " + windowsOnly.Commands[1].Argv[^1]);

            var none = new ProgramsEnv(root, [], "", 0);
            var failed = await RunFailing(CodingTools.CreatePowerShellTool(), ToolArgs(("command", "1")), none);
            Equal("spawn powershell ENOENT", failed.Error.Message, "last start failure");
        });

        yield return ToolCase("powershell.answers-a-nonzero-exit-with-an-error-result-carrying-the-exit-code", async root =>
        {
            var env = new ProgramsEnv(root, ["pwsh"], "partial", 3);
            var result = await Run(CodingTools.CreatePowerShellTool(), ToolArgs(("command", "exit 3")), env);
            Equal(true, result.Result.IsError, "isError");
            Equal("""{"output":"partial","truncated":false,"exitCode":3}""", result.Result.StructuredOutput?.ToJson(), "structuredOutput");
            Equal(1, result.Result.Diagnostics?.Length ?? 0, "diagnostics");
            Equal(new ToolDiagnostic(ToolDiagnosticSeverity.Error, "Command exited with code 3", "exit_code"), result.Result.Diagnostics!.Value[0], "diagnostic");
            Equal("partial", string.Concat(result.Output), "streamed");
        });

        yield return ToolEnvCase("powershell.runs-real-powershell-with-utf-8-output", async env =>
        {
            if (!OperatingSystem.IsWindows()) return; // As upstream: Windows only.
            var result = await Run(CodingTools.CreatePowerShellTool(), ToolArgs(("command", "Write-Output ('h' + [char]0xe9 + 'llo'); exit 0")), env);
            Equal("h\u00E9llo", string.Concat(result.Output).Trim(), "output");
        });

        // ─── bash ───────────────────────────────────────────────────────────
        yield return ToolCase("bash.passes-the-retained-window-to-the-environment-and-forwards-what-it-skipped", async root =>
        {
            var window = new ShellOutputWindow(4, 1, 100, 1024);
            var skipped = new ShellOutputSkip(6, 2, true);
            var env = new SkippingEnv(root, skipped);
            var calls = new List<(string, ShellOutputSkip?)>();
            var api = new FakeToolApi(env) { Window = window, OnOutput = (chunk, skip) => calls.Add((chunk, skip)) };
            await CodingTools.CreateBashTool().Execute(ToolArgs(("command", "anything")), api, ToolContext);
            Equal(window, env.Received, "window");
            Equal(1, calls.Count, "calls");
            Equal(("tail\n", (ShellOutputSkip?)skipped), calls[0], "call");
        });

        yield return ToolEnvCase("bash.streams-combined-stdout-and-stderr-returns-no-output-of-its-own-and-repeats-it-with-the-exit-code", async env =>
        {
            var result = await Run(CodingTools.CreateBashTool(), ToolArgs(("command", "printf out; printf err >&2")), env);
            Contains(string.Concat(result.Output), "out", "stdout");
            Contains(string.Concat(result.Output), "err", "stderr");
            Equal(null, result.Result.Output, "output");
            Equal(ToolArgs(("output", string.Concat(result.Output)), ("truncated", false), ("exitCode", 0)).ToJson(),
                result.Result.StructuredOutput?.ToJson(), "structuredOutput");
        });

        yield return ToolEnvCase("bash.answers-a-nonzero-exit-with-an-error-result-and-throws-on-a-timeout-after-streaming-the-output", async env =>
        {
            var tool = CodingTools.CreateBashTool();
            var failed = await Run(tool, ToolArgs(("command", "printf failed; exit 7")), env);
            Equal(true, failed.Result.IsError, "isError");
            Equal("""{"output":"failed","truncated":false,"exitCode":7}""", failed.Result.StructuredOutput?.ToJson(), "structuredOutput");
            Equal(1, failed.Result.Diagnostics?.Length ?? 0, "diagnostics");
            Equal("exit_code", failed.Result.Diagnostics!.Value[0].Code, "code");
            Equal("Command exited with code 7", failed.Result.Diagnostics!.Value[0].Message, "message");
            Equal("failed", string.Concat(failed.Output), "streamed");
            var slow = await RunFailing(tool, ToolArgs(("command", "sleep 2"), ("timeout", 0.01)), env);
            Equal("Command timed out after 0.01 seconds", slow.Error.Message, "timeout");
        });

        yield return ToolCase("bash.reports-the-spill-of-a-command-that-times-out", async root =>
        {
            var env = new TimeoutOutputExecutionEnv(root);
            var failed = await RunFailing(CodingTools.CreateBashTool(), ToolArgs(("command", "emit-output-then-time-out"), ("timeout", 0.05)), env);
            Equal("Command timed out after 0.05 seconds", failed.Error.Message, "message");
            var message = failed.Reported.Count > 0 ? failed.Reported[0].Message : "";
            Check(message.StartsWith("Full output: ", StringComparison.Ordinal), "spill diagnostic: " + message);
            var fullOutput = await EnvText(env, message["Full output: ".Length..]);
            Contains(fullOutput, "line-1\nline-2", "start");
            Contains(fullOutput, $"line-{Truncation.DefaultMaxLines}\nline-{TimeoutOutputExecutionEnv.TruncatedOutputLines}", "end");
        });

        yield return ToolCase("bash.prepares-command-cwd-and-an-explicit-environment-with-the-calls-api", async root =>
        {
            var env = new LocalExecutionEnv(new LocalExecutionEnvOptions
            {
                Cwd = root,
                ShellEnv = new Dictionary<string, string> { ["PI_BASH_PREPARE_INHERITED"] = "inherited" },
            });
            (await env.CreateDirAsync("workspace", null, ToolContext)).GetOrThrow();
            var workspace = Path.Combine(env.Cwd, "workspace");
            using var controller = new CancellationTokenSource();
            var callContext = ToolContext.WithCancellation(controller.Token);
            IExecutionEnv? receivedEnv = null;
            DurableContext? receivedContext = null;
            var tool = CodingTools.CreateBashTool(new BashToolOptions
            {
                CommandPrefix = "prefix=ready",
                Prepare = (execution, api, prepareContext) =>
                {
                    receivedEnv = api.Env;
                    receivedContext = prepareContext;
                    execution.Cwd = workspace;
                    execution.Env = new() { ["PI_BASH_PREPARE_EXPLICIT"] = "explicit" };
                    execution.InheritEnv = false;
                    execution.Command += "\n: > prepared-cwd\nprintf '%s:%s:%s' \"$prefix\" \"${PI_BASH_PREPARE_INHERITED-}\" \"$PI_BASH_PREPARE_EXPLICIT\"";
                    // Git Bash on Windows reports $PWD as an MSYS path, so only POSIX compares it.
                    if (!OperatingSystem.IsWindows()) execution.Command += "\nprintf ':%s' \"$PWD\"";
                    return ValueTask.CompletedTask;
                },
            });
            var result = await Run(tool, ToolArgs(("command", ":")), env, callContext);
            Check(ReferenceEquals(env, receivedEnv), "prepare got the call's environment");
            Check(ReferenceEquals(callContext, receivedContext) && receivedContext!.CancellationToken == controller.Token, "prepare got the call's context");
            var pwd = OperatingSystem.IsWindows() ? "" : ":" + (await env.CanonicalPathAsync(workspace, ToolContext)).GetOrThrow();
            Equal($"ready::explicit{pwd}", string.Concat(result.Output), "output");
            Check((await env.ExistsAsync(Path.Combine(workspace, "prepared-cwd"), ToolContext)).GetOrThrow(), "ran in the prepared cwd");
        });

        yield return ToolEnvCase("bash.supports-command-prefixes", async env =>
        {
            var result = await Run(CodingTools.CreateBashTool(new BashToolOptions { CommandPrefix = "value=hello" }), ToolArgs(("command", "printf $value")), env);
            Equal("hello", string.Concat(result.Output), "output");
        });

        yield return ToolEnvCase("bash.streams-every-byte-and-spills-complete-output-beyond-the-default-limits", async env =>
        {
            var result = await Run(CodingTools.CreateBashTool(),
                ToolArgs(("command", "i=1; while [ $i -le 3000 ]; do echo line-$i; i=$((i + 1)); done")), env);
            var expected = string.Concat(Enumerable.Range(1, 3000).Select(index => $"line-{index}\n"));
            Equal(expected, string.Concat(result.Output), "output");
            var message = result.Reported.Count > 0 ? result.Reported[0].Message : "";
            Check(message.StartsWith("Full output: ", StringComparison.Ordinal), "spill diagnostic: " + message);
            Equal(expected, await EnvText(env, message["Full output: ".Length..]), "spill");
        });

        yield return ToolEnvCase("bash.does-not-spill-output-within-the-limits", async env =>
        {
            var result = await Run(CodingTools.CreateBashTool(), ToolArgs(("command", "printf small")), env);
            Equal(0, result.Reported.Count, "reported");
        });
    }

    /// <summary>Every read appends, as a busy writer would; the scanned lines stay as they were.</summary>
    private sealed class GrowingLogEnv(string cwd) : LocalExecutionEnv(cwd)
    {
        public string? LogPath { get; set; }

        public override async Task<Result<IBinaryReader, FileError>> OpenBinaryReaderAsync(string path, BinaryReaderOptions? options, DurableContext context)
        {
            var opened = await base.OpenBinaryReaderAsync(path, options, context);
            return opened.IsOk && LogPath is { } log ? new GrowingReader(opened.Value, log) : opened;
        }
    }

    private sealed class GrowingReader(IBinaryReader inner, string logPath) : IBinaryReader
    {
        public Task<Result<FileMetadata, FileError>> InfoAsync(DurableContext context) => inner.InfoAsync(context);
        public Task<Result<byte[], FileError>> ReadAsync(long offset, long length, DurableContext context)
        {
            using (var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                stream.Write("more\n"u8);
            return inner.ReadAsync(offset, length, context);
        }
        public Task<Result<LineScan, FileError>> ScanLinesAsync(long startLine, long? endLine, DurableContext context) =>
            inner.ScanLinesAsync(startLine, endLine, context);
        public Task CloseAsync(DurableContext context) => inner.CloseAsync(context);
    }

    /// <summary>Records the window it got and delivers one chunk after an omission.</summary>
    private sealed class SkippingEnv(string cwd, ShellOutputSkip skipped) : LocalExecutionEnv(cwd)
    {
        public ShellOutputWindow? Received { get; private set; }

        public override Task<Result<ShellExecResult, ExecutionError>> ExecAsync(ShellCommand command, ShellExecOptions? options, DurableContext context)
        {
            Received = options?.Window;
            options?.OnOutput?.Invoke("tail\n", context, new ShellOutputInfo(ShellStream.Stdout, skipped));
            return Task.FromResult<Result<ShellExecResult, ExecutionError>>(new ShellExecResult(0));
        }
    }
}
