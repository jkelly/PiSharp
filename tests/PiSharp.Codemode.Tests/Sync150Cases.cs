using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PiSharp.Codemode;
using PiSharp.Contracts;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/codemode/test/worker-lifetime.test.ts (acquireWorker, onWorkerStarted,
// settling without waiting for worker exit), packages/coding-agent/src/extensions/codemode/execute.ts (the process-wide worker limit and
// the wait for a free worker) and test/suite/agent-session-codemode.test.ts (#11501, output type summaries). Pi 1.3.0-1.5.0 behaviour,
// ported by reading: inputs and expectations are the upstream tests' where they exist and authored otherwise, never captured from a run.
// Upstream mocks Worker.terminate; PiSharp's equivalent is a worker double whose termination never completes. The executor's worker
// limit is process-wide, so its cases set PI_CODEMODE_MAX_WORKERS for their duration and wait for earlier workers to exit first.
internal static partial class Program
{
    /// <summary>Reports ready, writes "partial" and calls "hang"; its termination and exit complete only when the test says so.</summary>
    private sealed class HangingWorker : ICodemodeWorker
    {
        public readonly TaskCompletionSource Exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Terminations;
        public Action<CodemodeWorkerMessage>? Received;
        public void Start(CodemodeStartMessage start, Action<CodemodeWorkerMessage> received, Action<int?> exited, Action<Exception> broken)
        {
            Received = received;
            received(new CodemodeReadyMessage());
            received(new CodemodeOutputMessage(new CodemodeTextOutput("partial")));
            received(new CodemodeCallMessage(1, true, "hang", null));
        }
        public void Send(CodemodeResultMessage result) { }
        public Task TerminateAsync() { Interlocked.Increment(ref Terminations); return new TaskCompletionSource().Task; }
        public Task Exited => Exit.Task;
    }

    /// <summary>A host whose <c>hold</c> tool waits until <see cref="Release"/> and whose <c>mark</c> tool records its text.</summary>
    private sealed class GateHost : ICodemodeHost
    {
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Holding { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Marks { get; } = [];
        public ImmutableArray<CodemodeNestedTool> Tools => [Nested("hold"), Nested("mark")];
        public ICodemodeModelRuntime? Models => null;
        public void Release() => released.TrySetResult();
        public async ValueTask<CodemodeNestedOutcome> ExecuteToolAsync(string name, JsonData? arguments, CancellationToken cancellationToken)
        {
            if (name == "hold") { Holding.TrySetResult(); await released.Task.WaitAsync(cancellationToken); }
            else lock (Marks) Marks.Add(arguments?.Value.GetProperty("text").GetString() ?? "");
            return new(null, Parse("""{"content":[{"type":"text","text":"done"}]}"""), false);
        }
        public IReadOnlyList<KeyValuePair<string, JsonData>> ReadStore() => [];
        public ValueTask AppendStoreEntryAsync(JsonData data, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private static async Task Until(Func<bool> condition, string what, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException("timed out waiting: " + what);
            await Task.Delay(20);
        }
    }

    /// <summary>Runs <paramref name="run"/> with <c>PI_CODEMODE_MAX_WORKERS</c> set, once workers of earlier cases have exited.</summary>
    private static async Task WithMaxWorkers(string? value, Func<Task> run)
    {
        await Until(() => CodemodeExecutor.ActiveWorkers == 0 && CodemodeExecutor.WaitingScripts == 0, "earlier codemode workers to exit");
        var previous = Environment.GetEnvironmentVariable(CodemodeExecutor.MaxWorkersVariable);
        Environment.SetEnvironmentVariable(CodemodeExecutor.MaxWorkersVariable, value);
        try { await run(); }
        finally { Environment.SetEnvironmentVariable(CodemodeExecutor.MaxWorkersVariable, previous); }
        await Until(() => CodemodeExecutor.ActiveWorkers == 0, "this case's workers to exit");
    }

    private static string Text(JsonData result, int index) => Content(result)[index].GetProperty("text").GetString()!;

    /// <summary>The refusal of a script that got no worker: two text items and no nested calls.</summary>
    private static void DidNotStart(JsonData result, string error, string what)
    {
        Check(result.Value.GetProperty("isError").GetBoolean(), what + ": isError");
        Equal(2, Content(result).GetArrayLength(), what + ": items");
        var header = Text(result, 0);
        Check(header.StartsWith("Script failed\nWall time ", StringComparison.Ordinal) && header.EndsWith(" seconds\nOutput:\n", StringComparison.Ordinal), what + ": " + header);
        Equal($"Script error:\n{error}\n\nThe script did not start.", Text(result, 1), what);
        Equal(0, result.Value.GetProperty("details").GetProperty("calls").GetArrayLength(), what + ": calls");
    }

    private static IEnumerable<(string, Func<Task>)> Sync150Cases() =>
    [
        // worker-lifetime.test.ts #10748: reject admission before creating a worker, not by terminating an excess worker.
        Case("sync150.worker-lifetime-hooks.denied-admission-starts-no-worker", async () =>
        {
            int acquired = 0, started = 0;
            await using var sandbox = new CodemodeSandbox(new()
            {
                AcquireWorker = () => { acquired++; throw new InvalidOperationException("worker capacity exhausted"); },
                OnWorkerStarted = _ => started++
            });
            var result = await sandbox.ExecuteAsync("while (true) {}");
            Failed(result, CodemodeErrorKind.Sandbox, "denied");
            Equal("worker capacity exhausted", result.Error!.Message, "message");
            Equal(1, acquired, "acquired");
            Equal(0, started, "no worker started");
        }),
        // #10748: a worker that fails to start returns its reserved capacity. Upstream points workerUrl at an invalid URL; PiSharp
        // points the launcher at an executable that does not exist.
        Case("sync150.worker-lifetime-hooks.release-when-the-worker-fails-to-start", async () =>
        {
            int acquired = 0, released = 0, started = 0;
            var missing = new CodemodeWorkerLauncher(Path.Combine(Temp("missing-worker"), "no-such-worker.exe"), []);
            await using var sandbox = new CodemodeSandbox(new()
            {
                Worker = missing,
                AcquireWorker = () => { acquired++; return () => Interlocked.Increment(ref released); },
                OnWorkerStarted = _ => started++
            });
            Failed(await sandbox.ExecuteAsync("return 1"), CodemodeErrorKind.Sandbox, "missing worker", message: "Failed to start worker:");
            await Until(() => Volatile.Read(ref released) == 1, "release");
            Equal(1, acquired, "acquired");
            Equal(0, started, "no worker started");
        }),
        // Authored, for upstream's "does not acquire admission when wasm loading fails" (PiSharp loads no wasm): an execution aborted
        // before its worker starts reserves nothing.
        Case("sync150.worker-lifetime-hooks.no-admission-before-start", async () =>
        {
            var acquired = 0;
            await using var sandbox = new CodemodeSandbox(new() { AcquireWorker = () => { acquired++; return () => { }; } });
            Failed(await sandbox.ExecuteAsync("return 1", null, new CancellationToken(true)), CodemodeErrorKind.Aborted, "aborted first");
            Equal(0, acquired, "acquired");
        }),
        // #10748: exit accounting applies to successful executions and host-controlled runs too.
        Case("sync150.worker-lifetime-hooks.execute-and-start-report-the-actual-exit", async () =>
        {
            var exits = new List<Task>();
            int acquired = 0, released = 0;
            await using var sandbox = new CodemodeSandbox(new()
            {
                AcquireWorker = () => { Interlocked.Increment(ref acquired); return () => Interlocked.Increment(ref released); },
                OnWorkerStarted = exited => { lock (exits) exits.Add(exited); }
            });
            Ok(await sandbox.ExecuteAsync("return 1"), "1", "execute");
            var run = sandbox.Start("return 2", new() { StartedAt = 0, Seed = 0, OnCall = _ => { } });
            Ok(await run.Result, "2", "start");
            Check(run.Finished, "finished");
            Equal(2, exits.Count, "exits");
            await Task.WhenAll(exits).WaitAsync(TimeSpan.FromSeconds(30));
            await Until(() => Volatile.Read(ref released) == 2, "releases");
            Equal(2, acquired, "acquired");
        }),
        // #10748: a failing lifetime hook must not leak the worker it was handed.
        Case("sync150.worker-lifetime-hooks.failing-hook-requests-termination", async () =>
        {
            Task? exited = null;
            var released = 0;
            await using var sandbox = new CodemodeSandbox(new()
            {
                AcquireWorker = () => () => Interlocked.Increment(ref released),
                OnWorkerStarted = promise => { exited = promise; throw new InvalidOperationException("lifetime hook failed"); }
            });
            var result = await sandbox.ExecuteAsync("while (true) {}");
            Failed(result, CodemodeErrorKind.Sandbox, "hook");
            Equal("lifetime hook failed", result.Error!.Message, "message");
            Check(exited is not null, "hook called");
            await exited!.WaitAsync(TimeSpan.FromSeconds(30));
            await Until(() => Volatile.Read(ref released) == 1, "release");
        }),
        // #10748: abort, timeout and close settle as soon as termination is requested, whether or not the worker has exited; neither
        // close() nor a late worker message changes the decided outcome.
        Case("sync150.stop-without-exit-wait.abort-timeout-and-close-settle-before-exit", async () =>
        {
            foreach (var how in new[] { "abort", "timeout", "close" })
            {
                var worker = new HangingWorker();
                Task? exited = null;
                CancellationToken toolToken = default;
                var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var hang = new CodemodeTool("hang", (_, token) =>
                {
                    toolToken = token; called.TrySetResult();
                    return new ValueTask<JsonData?>(new TaskCompletionSource<JsonData?>().Task);
                });
                var sandbox = new CodemodeSandbox(new()
                {
                    Tools = [hang], TimeoutMs = how == "timeout" ? 100 : 600_000, WorkerFactory = () => worker, OnWorkerStarted = promise => exited = promise
                });
                using var abort = new CancellationTokenSource();
                var pending = sandbox.ExecuteAsync("unused", null, abort.Token);
                await called.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Task? closing = null;
                if (how == "abort") abort.Cancel();
                if (how == "close") closing = sandbox.CloseAsync();
                var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
                Failed(result, how == "timeout" ? CodemodeErrorKind.Timeout : CodemodeErrorKind.Aborted, how);
                Equal("text:partial", Output(result), how + " output");
                Names(["hang:Cancelled"], result.Calls.Select(call => call.Name + ":" + call.Status), how + " calls");
                Check(toolToken.IsCancellationRequested, how + ": the tool's token is cancelled");
                Equal(1, Volatile.Read(ref worker.Terminations), how + " terminations");
                Check(exited is { IsCompleted: false }, how + ": the worker has not exited");
                if (closing is not null) await closing.WaitAsync(TimeSpan.FromSeconds(10));
                await sandbox.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10));
                worker.Received!(new CodemodeDoneMessage(true, "42", "[]", null));
                Check(ReferenceEquals(result, await pending), how + ": the outcome stays");
                Equal(1, Volatile.Read(ref worker.Terminations), how + " terminations after close");
                worker.Exit.SetResult();
                await exited!.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }),
        // execute.ts maxCodemodeWorkers: Number(PI_CODEMODE_MAX_WORKERS) when it is a positive integer, else 2. Authored values.
        Case("sync150.worker-admission-limit.max-workers-variable", async () =>
        {
            foreach (var (value, expected) in new (string?, int)[] { (null, 2), ("3", 3), (" 4 ", 4), ("1.0", 1), ("0x3", 3), ("1e1", 10), ("", 2), ("0", 2),
                ("-1", 2), ("1.5", 2), ("abc", 2), ("Infinity", 2) })
                await WithMaxWorkers(value, () => { Equal(expected, CodemodeExecutor.MaxWorkers(), value ?? "unset"); return Task.CompletedTask; });
        }),
        // execute.ts reserveCodemodeWorker: at the limit, scripts wait for a free worker in arrival order; a worker's slot frees when
        // its worker exits.
        Case("sync150.wait-for-free-worker.waits-in-arrival-order", () => WithMaxWorkers("1", async () =>
        {
            var host = new GateHost();
            var first = Execute("await tools.hold({}); return 'A';", host);
            await host.Holding.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var second = Execute("await tools.mark({ text: 'B' }); return 'B';", host);
            await Until(() => CodemodeExecutor.WaitingScripts == 1, "B waits");
            var third = Execute("await tools.mark({ text: 'C' }); return 'C';", host);
            await Until(() => CodemodeExecutor.WaitingScripts == 2, "C waits");
            Check(!second.IsCompleted && !third.IsCompleted && host.Marks.Count == 0, "both wait while A holds the only worker");
            host.Release();
            Equal("A", ResultText(await first.WaitAsync(TimeSpan.FromSeconds(60))), "A");
            Equal("B", ResultText(await second.WaitAsync(TimeSpan.FromSeconds(60))), "B");
            Equal("C", ResultText(await third.WaitAsync(TimeSpan.FromSeconds(60))), "C");
            Names(["B", "C"], host.Marks, "arrival order");
        })),
        // execute.ts: the wait counts toward timeout_ms, honours abort and is capped (60 s upstream; shortened here).
        Case("sync150.wait-for-free-worker.timeout-abort-and-cap", () => WithMaxWorkers("1", async () =>
        {
            var host = new GateHost();
            var holder = Execute("await tools.hold({}); return 'A';", host);
            await host.Holding.Task.WaitAsync(TimeSpan.FromSeconds(30));
            DidNotStart(await Execute("// @options: {\"timeout_ms\": 300}\nreturn 1;", host),
                "Script timed out: Execution timed out after 300 ms while waiting for a free codemode worker", "deadline");
            using (var abort = new CancellationTokenSource(150))
                DidNotStart(await CodemodeExecutor.ExecuteAsync("call-2", "return 1;", host, cancellationToken: abort.Token),
                    "Script aborted: This operation was aborted", "abort while waiting");
            DidNotStart(await CodemodeExecutor.ExecuteAsync("call-3", "return 1;", host, cancellationToken: new CancellationToken(true)),
                "Script aborted: This operation was aborted", "aborted before waiting");
            var cap = CodemodeExecutor.WorkerWaitMs;
            CodemodeExecutor.WorkerWaitMs = 300;
            try
            {
                DidNotStart(await Execute("return 1;", host),
                    "No codemode worker became free within 0.3 seconds. All 1 workers, shared by every session in this process, are held by other scripts. Retry after they finish.", "cap");
                // A deadline past the cap still fails at the cap, with the cap's message.
                DidNotStart(await Execute("// @options: {\"timeout_ms\": 5000}\nreturn 1;", host),
                    "No codemode worker became free within 0.3 seconds. All 1 workers, shared by every session in this process, are held by other scripts. Retry after they finish.", "deadline past the cap");
            }
            finally { CodemodeExecutor.WorkerWaitMs = cap; }
            Equal(0, CodemodeExecutor.WaitingScripts, "no waiter left behind");
            host.Release();
            Equal("A", ResultText(await holder.WaitAsync(TimeSpan.FromSeconds(60))), "holder");
        })),
        // execute.ts: a script that waited runs with what is left of its deadline: Math.max(1, Math.round(deadline - waited)).
        Case("sync150.wait-for-free-worker.deadline-shrinks-by-the-wait", () => WithMaxWorkers("1", async () =>
        {
            var host = new GateHost();
            var holder = Execute("await tools.hold({}); return 'A';", host);
            await host.Holding.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var waiting = Execute("// @options: {\"timeout_ms\": 4000}\nwhile (true) {}", host);
            await Until(() => CodemodeExecutor.WaitingScripts == 1, "waits");
            await Task.Delay(1000);
            host.Release();
            Equal("A", ResultText(await holder.WaitAsync(TimeSpan.FromSeconds(60))), "holder");
            var text = ResultText(await waiting.WaitAsync(TimeSpan.FromSeconds(60)));
            const string Prefix = "Script error:\nScript timed out: Execution timed out after ";
            var start = text.IndexOf(Prefix, StringComparison.Ordinal);
            Check(start >= 0, text);
            var ms = int.Parse(text[(start + Prefix.Length)..].Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
            Check(ms is >= 1 and <= 3000, "the deadline left after a wait of over a second: " + ms);
        })),
        // agent-session-codemode.test.ts #11501: unions list each variant's fields, const fields show their value, other multi-line
        // types point to describeTool(), and single-line types stay. The TypeBox schemas are written out as the JSON Schema they produce.
        Case("sync150.output-type-summaries.union-multi-line-and-single-line", () =>
        {
            ToolLoadoutTool Tool(string name, string output) => new(JsonData.Parse(new JsonObject { ["name"] = name, ["description"] = name + " tool",
                ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() } }.ToJsonString()), ToolExposure.Direct)
            { OutputSchema = Parse(output) };
            ImmutableArray<ToolLoadoutTool> tools =
            [
                Tool("process", """
                    {"anyOf":[
                      {"type":"object","properties":{"action":{"const":"start","type":"string"},
                        "process":{"type":"object","properties":{"id":{"description":"Process id","type":"string"},"pid":{"type":"integer"}},"required":["id","pid"]}},
                        "required":["action","process"]},
                      {"type":"object","properties":{"action":{"const":"list","type":"string"},
                        "processes":{"type":"array","items":{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}},
                        "truncated":{"type":"boolean"}},"required":["action","processes"]}]}
                    """),
                Tool("entries", """{"type":"array","items":{"type":"object","properties":{"id":{"type":"string"},"size":{"type":"integer"}},"required":["id","size"]}}"""),
                Tool("count", """{"type":"number"}"""),
                // Authored: oneOf, a null anyOf falling through to oneOf, a const null, and a union with a non-object variant.
                Tool("state", """{"anyOf":null,"oneOf":[{"type":"object","properties":{"ok":{"const":true},"value":{"const":null}},"required":["ok"]},{"type":"object","properties":{}}]}"""),
                Tool("mixed", """{"anyOf":[{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]},{"type":"null"}]}"""),
            ];
            var changes = CodemodeToolDefinition.PrepareLoadout(new(tools, tools, tools), CodemodeMode.On, false, null, tool => tool.OutputSchema);
            Equal("process tool\n\nCodemode: `tools.process(args)` resolves to `{ action: \"start\", process } | { action: \"list\", processes, truncated? }`.",
                changes.Descriptions["process"], "process");
            Equal("entries tool\n\nCodemode: `tools.entries(args)` resolves to a value; `await describeTool(\"entries\")` shows its type.",
                changes.Descriptions["entries"], "entries");
            Equal("count tool\n\nCodemode: `tools.count(args)` resolves to `number`.", changes.Descriptions["count"], "count");
            Equal("state tool\n\nCodemode: `tools.state(args)` resolves to `{ ok: true, value?: null } | {  }`.", changes.Descriptions["state"], "state");
            // A union with a non-object variant keeps its type when that fits on one line (an integer field's tag comment makes entries span lines).
            Equal("mixed tool\n\nCodemode: `tools.mixed(args)` resolves to `{ id: string; } | null`.", changes.Descriptions["mixed"], "mixed");
        }),
    ];
}
