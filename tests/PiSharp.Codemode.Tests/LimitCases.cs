using System.Diagnostics;
using PiSharp.Codemode;
using PiSharp.Contracts;

// Upstream: packages/codemode/test/sandbox.test.ts "limits and lifetime" (#10283 output limits), plus the Jint engine limits of
// decision 0003 (memory, recursion, deadline, cancellation), each at its boundary and one past it.
internal static partial class Program
{
    private static int OutputChars(CodemodeResult result) =>
        result.Output.Sum(item => item is CodemodeTextOutput text ? text.Text.Length : ((CodemodeImageOutput)item).Data.Length);

    private static IEnumerable<(string, Func<Task>)> LimitCases() =>
    [
        Case("limits.output-chars-at-boundary-and-one-past", async () =>
        {
            Ok(await Run($"text(\"x\".repeat({CodemodeLimits.MaxOutputChars})); return 1"), "1", "exactly MAX_OUTPUT_CHARS");
            var over = await Run($"text(\"x\".repeat({CodemodeLimits.MaxOutputChars})); text(\"y\"); return 1");
            Failed(over, CodemodeErrorKind.Script, "one character past", "RangeError", "script output exceeded the limit of 16777216 characters or 100000");
            Equal(CodemodeLimits.MaxOutputChars, OutputChars(over), "kept output");
        }),
        Case("limits.output-items-at-boundary-and-one-past", async () =>
        {
            var at = await Run($"for (let i = 0; i < {CodemodeLimits.MaxOutputItems}; i++) text(\"\"); return 1");
            Ok(at, "1", "exactly MAX_OUTPUT_ITEMS"); Equal(CodemodeLimits.MaxOutputItems, at.Output.Length, "items at limit");
            var over = await Run($"for (let i = 0; i <= {CodemodeLimits.MaxOutputItems}; i++) text(\"\"); return 1");
            Failed(over, CodemodeErrorKind.Script, "one item past", "RangeError"); Equal(CodemodeLimits.MaxOutputItems, over.Output.Length, "items kept");
        }),
        Case("limits.infinite-print-is-cancelled-even-when-caught", async () =>
        {
            foreach (var print in new[] { "text(s)", "console.log(s)", "image(\"data:image/png;base64,\" + p)" })
            {
                var started = Stopwatch.StartNew();
                var result = await Run($"const s = \"x\".repeat(1 << 20);\nconst p = \"iVBORw0KGgoA\" + \"A\".repeat(1 << 20);\nfor (;;) {{ try {{ {print}; }} catch {{}} }}", timeoutMs: null);
                Failed(result, CodemodeErrorKind.Script, print, "RangeError", "script output exceeded");
                var chars = OutputChars(result);
                Check(chars <= CodemodeLimits.MaxOutputChars && chars > CodemodeLimits.MaxOutputChars - (2 << 20), $"{print}: {chars} characters");
                Check(started.Elapsed < TimeSpan.FromSeconds(60), print + " ended promptly");
            }
            var empty = await Run("for (;;) text(\"\");", timeoutMs: null);
            Failed(empty, CodemodeErrorKind.Script, "empty prints", "RangeError"); Equal(CodemodeLimits.MaxOutputItems, empty.Output.Length, "item limit");
            // A script that catches the limit error and keeps printing still fails, and its later output is dropped.
            var caught = await Run("let n = 0; for (;;) { try { text(\"\"); } catch (error) { n++; } }", timeoutMs: null);
            Failed(caught, CodemodeErrorKind.Script, "caught limit error", "RangeError"); Equal(CodemodeLimits.MaxOutputItems, caught.Output.Length, "no output after the limit");
        }),
        Case("limits.deadline-at-boundary-and-one-past", async () =>
        {
            var wait = new CodemodeTool("wait", async (_, token) => { await Task.Delay(300, token); return Parse("\"late\""); });
            Ok(await Run("return await tools.wait()", [wait], timeoutMs: 5_000), "\"late\"", "within the deadline");
            Ok(await Run("return await tools.wait()", [wait], timeoutMs: double.PositiveInfinity), "\"late\"", "no deadline");
            var timedOut = await Run("return await tools.wait()", [wait], timeoutMs: 100);
            Failed(timedOut, CodemodeErrorKind.Timeout, "past the deadline", message: "Execution timed out after 100 ms");
            Names(["wait:Cancelled"], timedOut.Calls.Select(call => call.Name + ":" + call.Status), "pending call cancelled");
            var started = Stopwatch.StartNew();
            Failed(await Run("while (true) {}", timeoutMs: 200), CodemodeErrorKind.Timeout, "synchronous loop");
            Failed(await Run("while (true) { try { while (true) {} } catch {} }", timeoutMs: 200), CodemodeErrorKind.Timeout, "loop that catches");
            Failed(await Run("while (true) await null", timeoutMs: 200), CodemodeErrorKind.Timeout, "microtask spin");
            Check(started.Elapsed < TimeSpan.FromSeconds(15), "loops terminated promptly: " + started.Elapsed);
        }),
        Case("limits.caller-cancellation-aborts-and-cancels-calls", async () =>
        {
            var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken toolToken = default;
            var hang = new CodemodeTool("hang", async (_, token) => { toolToken = token; called.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return null; });
            await using var sandbox = new CodemodeSandbox(new() { Tools = [hang] });
            using var cancel = new CancellationTokenSource();
            var pending = sandbox.ExecuteAsync("await tools.hang(); return 'never'", new() { AbortMessage = "user cancelled" }, cancel.Token);
            await called.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(!toolToken.IsCancellationRequested, "not cancelled yet");
            await cancel.CancelAsync();
            var aborted = await pending;
            Failed(aborted, CodemodeErrorKind.Aborted, "aborted", message: "user cancelled");
            Names(["hang:Cancelled"], aborted.Calls.Select(call => call.Name + ":" + call.Status), "cancelled call");
            Check(toolToken.IsCancellationRequested, "tool token cancelled");
            Failed(await sandbox.ExecuteAsync("return 1", null, cancel.Token), CodemodeErrorKind.Aborted, "already cancelled", message: "This operation was aborted");
            var closing = sandbox.ExecuteAsync("await tools.hang(); return 'never'");
            await Task.Delay(100);
            await sandbox.CloseAsync();
            Failed(await closing, CodemodeErrorKind.Aborted, "closed", message: "Sandbox closed");
        }),
        Case("limits.stalled-promise-fails-the-script", async () =>
        {
            Failed(await Run("await tools.echo(1); await new Promise(() => {}); return 'never'", [Echo], timeoutMs: null), CodemodeErrorKind.Script, "stalled", message: "can never settle");
            Ok(await Run("tools.echo(2); return 'early'", [Echo], timeoutMs: null), "\"early\"", "returning with a pending call is not a stall");
        }),
        Case("limits.recursion-at-boundary-and-one-past", async () =>
        {
            const int limit = 200;
            async Task<bool> Reaches(int depth)
            {
                await using var sandbox = new CodemodeSandbox(new() { RecursionLimit = limit });
                var result = await sandbox.ExecuteAsync($"function f(n) {{ return n === 0 ? 0 : f(n - 1) + 1; }} return f({depth});");
                if (!result.Ok) Check(result.Error is { Kind: CodemodeErrorKind.Script, Name: "RangeError", Message: "Maximum call stack size exceeded" }, "recursion error " + result.Error);
                return result.Ok;
            }
            // The deepest call the script can make is the limit less the frames above the script (prelude run(), the async body).
            var deepest = -1;
            for (var depth = limit - 10; depth <= limit + 1; depth++) if (await Reaches(depth)) deepest = depth;
            Check(deepest >= limit - 10 && deepest <= limit, "deepest " + deepest);
            Check(await Reaches(deepest) && !await Reaches(deepest + 1), $"boundary {deepest} and one past");
            // The production limit allows deep recursion (upstream: a RangeError only past 1000 frames); Jint cannot let the script
            // catch it (docs/compatibility/codemode-engine.md).
            Ok(await Run("function f(n){ return n === 0 ? 0 : f(n - 1) + 1; } return f(5000);"), "5000", "default limit allows 5000");
            var overflow = await Run("let depth = 0;\nfunction dive() { depth++; dive(); }\ntry { dive(); } catch (error) { return [error.name, depth > 1000]; }");
            Failed(overflow, CodemodeErrorKind.Script, "runaway recursion", "RangeError", "Maximum call stack size exceeded");
        }),
        Case("limits.memory-segment-and-total-allocation", async () =>
        {
            const long segment = 64L * 1024 * 1024, total = 160L * 1024 * 1024;
            async Task<CodemodeResult> Allocate(string code)
            {
                await using var sandbox = new CodemodeSandbox(new() { Tools = [Echo], MemoryLimitBytes = segment, TotalAllocationLimitBytes = total });
                return await sandbox.ExecuteAsync(code);
            }
            // Allocations well under each budget pass; one uninterrupted run past the segment budget fails.
            Ok(await Allocate("const a = []; for (let i = 0; i < 8; i++) a.push('x'.repeat(1 << 20) + i); return a.length"), "8", "under the segment budget");
            var segmentOver = await Allocate("const a = []; while (true) a.push('x'.repeat(1 << 20) + a.length);");
            Failed(segmentOver, CodemodeErrorKind.Script, "past the segment budget", "InternalError", "out of memory");
            // Awaiting a tool starts a new segment, but the total budget still counts every allocation of the execution.
            Ok(await Allocate("let a = []; for (let round = 0; round < 2; round++) { a = []; for (let i = 0; i < 8; i++) a.push('x'.repeat(1 << 20) + i); await tools.echo(round); } return a.length"),
                "8", "under the total budget across segments");
            Failed(await Allocate("let a = []; for (let round = 0; round < 20; round++) { a = []; for (let i = 0; i < 8; i++) a.push('x'.repeat(1 << 20) + i); await tools.echo(round); } return a.length"),
                CodemodeErrorKind.Script, "past the total budget", "InternalError", "out of memory");
            // The production default stops runaway allocations long before they reach the host's memory.
            var runaway = await Run("let a = [];\nwhile (true) a.push(\"x\".repeat(1 << 20) + a.length);", timeoutMs: 60_000);
            Failed(runaway, CodemodeErrorKind.Script, "default memory limit", "InternalError", "out of memory");
        }),
        Case("limits.engine-threads-exit-after-results", async () =>
        {
            for (var i = 0; i < 20; i++) Ok(await Run("return 1"), "1", "run " + i);
            var threads = Process.GetCurrentProcess().Threads.Count;
            for (var i = 0; i < 20; i++) Failed(await Run("while (true) {}", timeoutMs: 50), CodemodeErrorKind.Timeout, "timeout " + i);
            GC.Collect();
            Check(Process.GetCurrentProcess().Threads.Count <= threads + 5, $"no retained engine threads ({threads} -> {Process.GetCurrentProcess().Threads.Count})");
        }),
    ];
}
