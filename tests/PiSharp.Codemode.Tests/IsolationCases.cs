using System.Text.Json;
using PiSharp.Codemode;
using PiSharp.Contracts;

// Upstream: QuickJS's stack guard turns deep nesting into a catchable RangeError, and a crashing worker is reported as a sandbox
// error ("Worker exited with code ... before the script settled") without affecting pi (host.ts). PiSharp guards the built-ins
// that recurse natively and runs each engine in a worker process, so nothing a script does takes the host down.
internal static partial class Program
{
    private static async Task<CodemodeResult> RunIn(CodemodeWorkerLauncher? worker, string code, double? timeoutMs = 120_000)
    {
        await using var sandbox = new CodemodeSandbox(new() { Worker = worker, TimeoutMs = timeoutMs });
        return await sandbox.ExecuteAsync(code);
    }

    private const string DeepNesting = """
        const levels = 20000;
        let array = []; for (let i = 0; i < levels; i++) array = [array];
        let object = {}; for (let i = 0; i < levels; i++) object = { object };
        const attempt = (run) => { try { run(); return "no error"; } catch (error) { return error.name + ": " + error.message; } };
        return [
          attempt(() => String(array)),
          attempt(() => array.join(",")),
          attempt(() => array.toLocaleString()),
          attempt(() => JSON.stringify(object)),
          attempt(() => JSON.stringify(array)),
          attempt(() => console.log(object)),
          attempt(() => array.flat(Infinity)),
          attempt(() => JSON.parse("[".repeat(levels) + "]".repeat(levels))),
          attempt(() => String([[[1, [2]]], 3])),
          attempt(() => JSON.stringify({ a: { b: [1, { c: 2 }] } })),
        ];
        """;

    private static IEnumerable<(string, Func<Task>)> IsolationCases() =>
    [
        // Native recursion guards: each deep structure fails with a RangeError the script catches, and shallow ones still work.
        Case("isolation.deep-nesting-throws-catchable-range-errors", async () =>
        {
            foreach (var (label, worker) in new[] { ("process", CodemodeWorker.Default), ("thread", (CodemodeWorkerLauncher?)null) })
            {
                var result = await RunIn(worker, DeepNesting);
                Check(result.Ok, label + ": " + result.Error);
                var values = result.Value!.Value.EnumerateArray().Select(item => item.GetString()!).ToArray();
                const string overflow = "RangeError: Maximum call stack size exceeded";
                // console.log formats a value it cannot stringify with String(), as the prelude does on QuickJS's RangeError too.
                Names([overflow, overflow, overflow, overflow, overflow, "no error", overflow], values[..7], label + " guarded built-ins");
                Check(values[7].StartsWith("SyntaxError: ", StringComparison.Ordinal), label + " JSON.parse depth: " + values[7]);
                Names(["no error", "no error"], values[8..], label + " shallow values");
            }
            Ok(await RunIn(CodemodeWorker.Default, "return [String([[1, [2, [3]]]]), JSON.stringify([[1, [2]]]), [1, [2, [3, [4]]]].flat(Infinity)];"),
                "[\"1,2,3\",\"[[1,[2]]]\",[1,2,3,4]]", "guarded built-ins keep their results");
        }),
        // Native recursion the guards do not cover (here a lookup along a prototype chain a million objects long) exhausts the engine thread's stack. It ends only the
        // worker process: the script fails as a sandbox error and the host keeps running scripts.
        Case("isolation.engine-crash-ends-only-the-worker-process", async () =>
        {
            Check(CodemodeWorker.Default is not null, "the test host is a worker launcher");
            // A 16 MiB engine stack (instead of 256 MiB) makes the overflow certain at a size the memory budget admits.
            var small = CodemodeWorker.Default! with { Environment = System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add(CodemodeWorker.StackVariable, (16 * 1024 * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture)) };
            var crashed = await RunIn(small, "let object = {}; for (let i = 0; i < 300000; i++) object = Object.create(object); return object.missing;");
            Failed(crashed, CodemodeErrorKind.Sandbox, "prototype chain", message: "before the script settled");
            Check(crashed.Error!.Message.StartsWith("Worker exited with code ", StringComparison.Ordinal), crashed.Error.Message);
            Ok(await RunIn(CodemodeWorker.Default, "return 'still running'"), "\"still running\"", "the host survives");
            Check(Environment.HasShutdownStarted is false, "host alive");
        }),
        // host.ts terminates the worker when the script settles, times out or is aborted: no worker outlives its script (one spare
        // is kept ready for the next script).
        Case("isolation.worker-processes-do-not-outlive-scripts", async () =>
        {
            // A script's result is delivered only after its worker has been killed and reaped, so once the spares are discarded no
            // worker may be left. Spares started ahead in the background may still be landing, so discard until none is.
            await CodemodeProcessWorker.DiscardSparesAsync();
            for (var i = 0; i < 6; i++) Failed(await RunIn(CodemodeWorker.Default, "while (true) {}", 300), CodemodeErrorKind.Timeout, "timeout " + i);
            for (var i = 0; i < 6; i++) Ok(await RunIn(CodemodeWorker.Default, "return 1"), "1", "run " + i);
            var deadline = DateTime.UtcNow.AddSeconds(20);
            do { await CodemodeProcessWorker.DiscardSparesAsync(); if (CodemodeProcessWorker.Running == 0) break; await Task.Delay(100); }
            while (DateTime.UtcNow < deadline);
            Check(CodemodeProcessWorker.Running == 0, "worker processes left: " + CodemodeProcessWorker.Running);
        }),
        // A host that dies without terminating its workers (killed, crashed) closes their standard input: a worker that sees end
        // of file exits even while its script spins, so it cannot outlive the host. Idle spares do the same.
        Case("isolation.worker-ends-when-its-host-goes-away", async () =>
        {
            var launcher = CodemodeWorker.Default!;
            foreach (var script in new[] { null, "while (true) {}" })
            {
                var info = new System.Diagnostics.ProcessStartInfo(launcher.FileName)
                    { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                foreach (var argument in launcher.Arguments) info.ArgumentList.Add(argument);
                using var worker = System.Diagnostics.Process.Start(info)!;
                try
                {
                    _ = worker.StandardError.BaseStream.CopyToAsync(System.IO.Stream.Null);
                    if (script is not null)
                    {
                        worker.StandardInput.Write(CodemodeWireCodec.Encode(new CodemodeStartMessage(script, "[]", "[]", "{}", CodemodeLimits.DefaultMemoryLimitBytes,
                            CodemodeLimits.DefaultTotalAllocationLimitBytes, CodemodeLimits.DefaultRecursionLimit)) + "\n");
                        worker.StandardInput.Flush();
                        var ready = await worker.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60));
                        Check(ready is not null && ready.Contains("\"ready\"", StringComparison.Ordinal), "script started: " + ready);
                    }
                    worker.StandardInput.Close();
                    using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    try { await worker.WaitForExitAsync(bound.Token); } catch (OperationCanceledException) { }
                    Check(worker.HasExited, (script ?? "idle spare") + ": the worker outlived its host's end of input");
                }
                finally { if (!worker.HasExited) { worker.Kill(entireProcessTree: true); await worker.WaitForExitAsync(); } }
            }
        }),
        // host.ts "reports a broken bridge as a sandbox error" with fixtures/raw-worker.ts: a worker that answers the start message
        // with a hand-made payload. The test executable doubles as that raw worker.
        Case("isolation.broken-bridge-is-a-sandbox-error", async () =>
        {
            foreach (var (message, reason) in new[]
            {
                ("""{"type":"done","ok":true,"value":"1","writes":"null"}""", "store writes are not an array"),
                ("""{"type":"done","ok":true,"value":"1","writes":"[1]"}""", "store writes contain a malformed entry"),
                ("""{"type":"done","ok":true,"value":"1","writes":"[[\"k\", \"{\"]]"}""", "store value for \"k\" is not valid JSON"),
                ("""{"type":"done","ok":true,"value":"{","writes":"[]"}""", "return value is not valid JSON"),
                ("""{"type":"done","ok":false,"error":"5"}""", "script error is not an object"),
                ("""{"type":"done","ok":false,"error":"{}"}""", "script error is malformed"),
                ("""{"type":"nonsense"}""", "unknown message from the worker"),
            })
            {
                var launcher = CodemodeWorker.Default! with { Arguments = [.. CodemodeWorker.Default!.Arguments.Where(argument => argument != CodemodeWorker.Argument), RawWorkerArgument] };
                var result = await RunIn(launcher, message);
                Failed(result, CodemodeErrorKind.Sandbox, reason);
                Check(result.Error!.Message.Contains("Sandbox bridge broken: " + reason, StringComparison.Ordinal), result.Error.Message);
            }
        }),
    ];

    internal const string RawWorkerArgument = "--codemode-raw-worker";

    /// <summary>fixtures/raw-worker.ts: answers the start message with the script text as a raw worker message.</summary>
    private static async Task<int> RawWorkerAsync()
    {
        using var input = new StreamReader(Console.OpenStandardInput());
        var start = await input.ReadLineAsync();
        if (start is null) return 0;
        var code = JsonDocument.Parse(start).RootElement.GetProperty("code").GetString()!;
        await using var output = new StreamWriter(Console.OpenStandardOutput()) { NewLine = "\n" };
        await output.WriteLineAsync(code); await output.FlushAsync();
        while (await input.ReadLineAsync() is not null) { }
        return 0;
    }
}
