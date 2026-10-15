using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Codemode;
using PiSharp.Contracts;

// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/codemode/test/deterministic.test.ts, ported by reading. Upstream runs
// every case on its worker and remote sandboxes; PiSharp runs them on its two engine hosts, a worker process and a thread. The
// deterministic.*.replay-* and deterministic.jint-* cases are authored for PiSharp; differential.deterministic-* compares with runs of
// the real upstream package (Deterministic/deterministic-goldens.json).
internal static partial class Program
{
    private static readonly double StartedAt = new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
    /// <summary>The first <c>Math.random()</c> with seed 1 (deterministic.test.ts PINNED_FIRST_RANDOM).</summary>
    private const double PinnedFirstRandom = 0.34227450605335275;
    /// <summary>The first two <c>Math.random()</c> values with seed 0 (deterministic.test.ts PINNED_SEED_ZERO).</summary>
    private static readonly double[] PinnedSeedZero = [0.6711372530266764, 0.7258461452833667];

    /// <summary>The engine hosts: a worker process per script (the CLI's) and a thread of this process.</summary>
    private static readonly (string Name, Func<CodemodeSandboxOptions, CodemodeSandboxOptions> Host)[] Hosts =
    [
        ("process", options => options with { Worker = CodemodeWorker.Default ?? throw new InvalidOperationException("no worker launcher") }),
        ("thread", options => options with { Worker = null }),
    ];

    private sealed class Recorded
    {
        private readonly object gate = new();
        private readonly List<CodemodeHostCall> calls = [];
        private readonly List<(long Seq, long Issued)> idles = [];
        private readonly List<string> events = [];
        public CodemodeRun Run { get; set; } = null!;
        public string? Fingerprint { get; set; }
        public List<CodemodeHostCall> Calls { get { lock (gate) return [.. calls]; } }
        public List<(long Seq, long Issued)> Idles { get { lock (gate) return [.. idles]; } }
        public List<string> Events { get { lock (gate) return [.. events]; } }
        public void Call(CodemodeHostCall call) { lock (gate) { calls.Add(call); events.Add($"call {call.Id}"); } }
        public void Idle(long seq, long issued) { lock (gate) { idles.Add((seq, issued)); events.Add($"idle {seq}"); } }
        public void Started(string fingerprint) { lock (gate) { Fingerprint = fingerprint; events.Add("started"); } }
    }

    /// <summary>deterministic.test.ts start(): tools <c>echo</c> and <c>wait</c>. Every call but <c>wait</c> is answered at once, by
    /// default with its argument, as result number 1, 2, ... with the clock at <c>StartedAt + 1000 * seq</c>; <c>wait</c> is left for the
    /// test to answer.</summary>
    private static Recorded StartRun(CodemodeSandbox sandbox, string code, Func<CodemodeRunOptions, CodemodeRunOptions>? configure = null,
        Func<CodemodeHostCall, CodemodeOutcome>? answer = null, CancellationToken token = default)
    {
        var recorded = new Recorded();
        long seq = 0;
        answer ??= call => CodemodeOutcome.Success(call.Args);
        var options = new CodemodeRunOptions
        {
            StartedAt = StartedAt, Seed = 1, Tools = [new("echo"), new("wait")], TimeoutMs = 20_000,
            OnStarted = recorded.Started,
            OnCall = call =>
            {
                recorded.Call(call);
                if (call.Name == "wait") return;
                var number = Interlocked.Increment(ref seq);
                recorded.Run.Deliver(number, StartedAt + 1000 * number, call.Id, answer(call));
            },
            OnIdle = recorded.Idle
        };
        recorded.Run = sandbox.Start(code, configure?.Invoke(options) ?? options, token);
        return recorded;
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException("Condition was not reached");
            await Task.Delay(5);
        }
    }

    /// <summary>The result without wall-clock call durations.</summary>
    private static string Timeless(CodemodeResult result) => JsonSerializer.Serialize(new
    {
        result.Ok, value = Json(result.Value), output = Output(result), calls = result.Calls.Select(call => $"{call.Name}:{call.Status}"),
        writes = result.StoreWrites.Set.Select(entry => entry.Key + "=" + entry.Value).Concat(result.StoreWrites.Delete.Select(key => "-" + key)),
        error = result.Error is { } error ? $"{error.Kind}:{error.Name}:{error.Message}" : null, result.During
    });

    private static double[] Numbers(JsonData? value) => [.. value!.Value.EnumerateArray().Select(item => item.GetDouble())];

    private static IEnumerable<(string, Func<Task>)> DeterministicCases() =>
        Hosts.SelectMany(host => DeterministicHostCases(host.Name, host.Host)).Concat(
        [
            Case("deterministic.execute-is-not-deterministic", async () =>
            {
                await using var sandbox = new CodemodeSandbox();
                var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var first = await sandbox.ExecuteAsync("return [Date.now(), Math.random()]");
                var second = await sandbox.ExecuteAsync("return [Date.now(), Math.random()]");
                var values = Numbers(first.Value);
                Check(values[0] >= before && values[0] <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "real clock: " + values[0]);
                Check(!Numbers(second.Value).Contains(values[1]), "real randomness");
                Equal(null, first.During, "execute() has no during");
            }),
            Case("deterministic.validates-its-options", async () =>
            {
                var sandbox = new CodemodeSandbox();
                CodemodeRunOptions Options(double startedAt = 0) => new() { StartedAt = startedAt, Seed = 1, OnCall = _ => { } };
                foreach (var startedAt in new[] { -1, double.NaN, double.PositiveInfinity })
                    Equal("startedAt must be a non-negative number of milliseconds since the epoch (Parameter 'runOptions')",
                        Throws<ArgumentException>(() => sandbox.Start("", Options(startedAt)), "startedAt " + startedAt).Message, "startedAt " + startedAt);
                Check(Throws<ArgumentException>(() => sandbox.Start("", Options() with { Tools = [new("a"), new("a")] }), "same name")
                    .Message.StartsWith("Tool names must be unique", StringComparison.Ordinal), "tool names");
                Check(Throws<ArgumentException>(() => sandbox.Start("", Options() with { Globals = [new("tools")] }), "reserved global")
                    .Message.StartsWith("Invalid global name \"tools\"", StringComparison.Ordinal), "global names");
                Check(Throws<ArgumentException>(() => sandbox.Start("", Options() with { Globals = [new("ns"), new("ns.member")] }), "namespace")
                    .Message.StartsWith("Global \"ns\" conflicts with the namespace \"ns\"", StringComparison.Ordinal), "namespace conflict");
                await sandbox.CloseAsync();
                Equal("Sandbox is closed", Throws<InvalidOperationException>(() => sandbox.Start("", Options()), "closed").Message, "closed");
            }),
            Case("deterministic.interrupt-budget-stops-at-the-same-statement", async () =>
            {
                // inline.ts: an interrupt budget, not the machine's speed, stops a script that computes without awaiting a tool.
                await using var sandbox = new CodemodeSandbox(new() { InterruptBudget = 20_000 });
                const string spin = "let n = 0; for (;;) { n++; if (n % 1000 === 0) text(String(n)); }";
                var runs = new List<CodemodeResult>();
                for (var index = 0; index < 2; index++) runs.Add(await StartRun(sandbox, spin).Run.Result);
                runs.Add(await sandbox.ExecuteAsync(spin));
                foreach (var result in runs) Failed(result, CodemodeErrorKind.Timeout, "budget", message: "Execution exceeded its interrupt budget of 20000");
                Check(runs[0].Output.Length > 0, "computed before it stopped");
                Equal(Output(runs[0]), Output(runs[1]), "stopped at the same point when run again");
                Equal(Output(runs[0]), Output(runs[2]), "execute() spends the budget the same way");
                Failed(await StartRun(sandbox, "for (;;) await null").Run.Result, CodemodeErrorKind.Timeout, "promise job loop", message: "interrupt budget");
                Failed(await StartRun(sandbox, "await tools.echo(1); for (;;) {}").Run.Result, CodemodeErrorKind.Timeout, "after a result", message: "interrupt budget");
                // The budget spans the whole execution, results included; a script within it is unaffected.
                Ok(await StartRun(sandbox, "let s = 0; for (let i = 0; i < 100; i++) s += await tools.echo(i); return s").Run.Result, "4950", "within the budget");
                var recorded = StartRun(sandbox, "return 1");
                await recorded.Run.Result;
                Check(recorded.Fingerprint!.EndsWith(", budget 20000", StringComparison.Ordinal), "fingerprint names the budget: " + recorded.Fingerprint);
            }),
        ]).Concat(DeterministicDifferentialCases());

    private static IEnumerable<(string, Func<Task>)> DeterministicHostCases(string host, Func<CodemodeSandboxOptions, CodemodeSandboxOptions> configure)
    {
        CodemodeSandbox Create(CodemodeSandboxOptions? options = null) => new(configure(options ?? new()));
        return
        [
            Case($"deterministic.{host}.same-calls-and-output-for-the-same-start-seed-and-results", async () =>
            {
                const string code = """
                    const values = [Date.now(), performance.now(), Math.random(), Math.random(), new Date().toISOString()];
                    const first = await tools.echo(values);
                    text(JSON.stringify([Date.now(), performance.now(), Math.random()]));
                    return await tools.echo(first.concat(Math.random()));
                    """;
                await using var sandbox = Create();
                var one = StartRun(sandbox, code);
                var two = StartRun(sandbox, code);
                var a = await one.Run.Result; var b = await two.Run.Result;
                // Call durations are wall-clock time; everything else is the same.
                Equal(Timeless(a), Timeless(b), "same result");
                Equal(JsonSerializer.Serialize(one.Calls.Select(call => $"{call.Id}{call.Target}{call.Name}{call.Args}")),
                    JsonSerializer.Serialize(two.Calls.Select(call => $"{call.Id}{call.Target}{call.Name}{call.Args}")), "same calls");
                // The clock: startedAt, then the time of the result being processed; performance.now() counts from startedAt.
                var args = one.Calls[0].Args!.Value;
                Equal(StartedAt, args[0].GetDouble(), "Date.now() at the start");
                Equal(0d, args[1].GetDouble(), "performance.now() at the start");
                Equal("2026-01-15T10:00:00.000Z", args[4].GetString(), "toISOString");
                var printed = JsonSerializer.Deserialize<double[]>(((CodemodeTextOutput)a.Output[0]).Text)!;
                Equal(StartedAt + 1000, printed[0], "Date.now() while result 1 is processed");
                Equal(1000d, printed[1], "performance.now() while result 1 is processed");
                // Pinned: a change here changes what replayed scripts see, so it must change the fingerprint too.
                var random = args[2].GetDouble();
                Equal(PinnedFirstRandom, random, "first random with seed 1");

                // The seed decides the random values, not the start time.
                var later = StartRun(sandbox, code, options => options with { StartedAt = StartedAt + 5000 });
                var reseeded = StartRun(sandbox, code, options => options with { Seed = 2 });
                await later.Run.Result; await reseeded.Run.Result;
                Equal(random, later.Calls[0].Args!.Value[2].GetDouble(), "same seed, later start");
                Check(reseeded.Calls[0].Args!.Value[2].GetDouble() != random, "another seed");
                // Seed 0 has its own values: QuickJS turns a zero state into 1, which must not make it seed 1's.
                var zero = await StartRun(sandbox, "return [Math.random(), Math.random()]", options => options with { Seed = 0 }).Run.Result;
                Check(zero.Ok, "seed 0 ran");
                Check(Numbers(zero.Value).SequenceEqual(PinnedSeedZero), "seed 0 values: " + Json(zero.Value));
                Check(!Numbers(zero.Value).Contains(random), "seed 0 is not seed 1");
            }),
            Case($"deterministic.{host}.utc-whatever-the-host-timezone-without-weakref-or-finalization-registry", async () =>
            {
                const string code = "return [new Date(0).getHours(), new Date(0).getTimezoneOffset(), typeof WeakRef, typeof FinalizationRegistry]";
                // process.env.TZ = "Asia/Kolkata": the worker process gets it (.NET reads TZ on Linux and macOS, not on Windows).
                var kolkata = host == "process";
                await using var sandbox = kolkata
                    ? new CodemodeSandbox(new() { Worker = CodemodeWorker.Default! with { Environment = ImmutableDictionary<string, string>.Empty.Add("TZ", "Asia/Kolkata") } })
                    : Create();
                Ok(await StartRun(sandbox, code).Run.Result, """[0,0,"undefined","undefined"]""", "deterministic run");
                // execute() is unchanged: the host's timezone and every intrinsic.
                var offset = kolkata && !OperatingSystem.IsWindows() ? TimeSpan.FromMinutes(330) : TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UnixEpoch);
                var hours = (24 + offset.Hours) % 24;
                Ok(await sandbox.ExecuteAsync(code), $"""[{hours},{-(int)offset.TotalMinutes},"function","function"]""", "execute()");
            }),
            Case($"deterministic.{host}.applies-results-in-seq-order-whatever-order-they-are-delivered-in", async () =>
            {
                await using var sandbox = Create();
                var recorded = StartRun(sandbox, """
                    const order = [];
                    await Promise.all([tools.wait("a").then(() => order.push("a")), tools.wait("b").then(() => order.push("b"))]);
                    return order;
                    """);
                await WaitFor(() => recorded.Calls.Count == 2 && recorded.Idles.Count == 1);
                var calls = recorded.Calls;
                // b is result 1 and a result 2, but a arrives first.
                recorded.Run.Deliver(2, StartedAt + 2000, calls[0].Id, CodemodeOutcome.Success(JsonData.Null));
                recorded.Run.Deliver(1, StartedAt + 1000, calls[1].Id, CodemodeOutcome.Success(JsonData.Null));
                Ok(await recorded.Run.Result, """["b","a"]""", "order");
                // No idle after the last result: the script finished in that drain, and the host stopped the engine.
                Equal("(0, 2) (1, 2)", string.Join(" ", recorded.Idles), "idles");
            }),
            Case($"deterministic.{host}.reports-the-fingerprint-before-any-call-and-the-calls-issued-after-each-result", async () =>
            {
                await using var sandbox = Create(new() { MemoryLimitBytes = 64 * 1024 * 1024 });
                var recorded = StartRun(sandbox, "await tools.echo(1); await Promise.all([tools.echo(2), tools.echo(3)]);");
                Check((await recorded.Run.Result).Ok, "ran");
                Names(["started", "call 1", "idle 0"], recorded.Events.Take(3), "first events");
                Equal("(0, 1) (1, 3) (2, 3)", string.Join(" ", recorded.Idles), "idles");
                Names(["1 Tool echo 1", "2 Tool echo 2", "3 Tool echo 3"], recorded.Calls.Select(call => $"{call.Id} {call.Target} {call.Name} {call.Args}"), "calls");
                // PiSharp's fingerprint names Jint instead of QuickJS (an intentional difference); the shape is the same.
                var fingerprint = recorded.Fingerprint!;
                Check(System.Text.RegularExpressions.Regex.IsMatch(fingerprint,
                    @"^jint \S+, acornima \S+, prelude [0-9a-f]{8}, shim [0-9a-f]{8}, deterministic \d+, dotnet \S+, os \S+, recursion \d+, heap 67108864, total \d+, budget none$"),
                    "fingerprint: " + fingerprint);
                var same = StartRun(sandbox, "return 1");
                await using var other = Create(new() { MemoryLimitBytes = 32 * 1024 * 1024 });
                var different = StartRun(other, "return 1");
                await same.Run.Result; await different.Run.Result;
                Equal(fingerprint, same.Fingerprint, "same sandbox");
                Check(different.Fingerprint != fingerprint, "another heap limit");
            }),
            Case($"deterministic.{host}.numbers-calls-to-globals-with-tools-and-answers-errors-as-rejections", async () =>
            {
                await using var sandbox = Create();
                var recorded = StartRun(sandbox, """
                    const found = await search("x", 2);
                    try { await tools.echo("fail"); } catch (error) { text(error.message); }
                    return found;
                    """, options => options with { Globals = [new("search") { Spread = true }] },
                    call => call.Args?.ToString() == "\"fail\"" ? CodemodeOutcome.Failure("no")
                        : CodemodeOutcome.Success(call.Name == "search" ? Parse("""["found"]""") : call.Args));
                var result = await recorded.Run.Result;
                var calls = recorded.Calls;
                Equal("1 Global search [\"x\",2]", $"{calls[0].Id} {calls[0].Target} {calls[0].Name} {calls[0].Args}", "global call");
                Ok(result, """["found"]""", "value");
                Equal("text:no", Output(result), "output");
                Equal("2 Tool echo", $"{calls[1].Id} {calls[1].Target} {calls[1].Name}", "tool call");
                // Calls to globals count as issued calls too.
                Equal("(0, 1) (1, 2)", string.Join(" ", recorded.Idles.Take(2)), "idles");
                Names(["echo:Error"], result.Calls.Select(call => $"{call.Name}:{call.Status}"), "recorded calls");
            }),
            Case($"deterministic.{host}.keeps-the-clock-monotonic-and-rejects-a-clock-value-that-is-not-finite", async () =>
            {
                await using var sandbox = Create();
                var recorded = StartRun(sandbox, "const before = Date.now(); await tools.wait(1); return [before, Date.now(), performance.now()]");
                await WaitFor(() => recorded.Calls.Count == 1);
                Check(Throws<ArgumentException>(() => recorded.Run.Deliver(1, double.NaN, 1, CodemodeOutcome.Success(JsonData.Null)), "NaN")
                    .Message.Contains("not a finite number", StringComparison.Ordinal), "NaN message");
                // A resumed host whose clock is behind the run's start.
                recorded.Run.Deliver(1, StartedAt - 2000, 1, CodemodeOutcome.Success(JsonData.Null));
                Ok(await recorded.Run.Result, $"[{StartedAt},{StartedAt},0]", "clock");
            }),
            Case($"deterministic.{host}.reports-the-result-the-engine-was-processing-when-the-script-ended", async () =>
            {
                await using var sandbox = Create();
                var returned = StartRun(sandbox, "await tools.echo(1); return await tools.echo(2)");
                var thrown = StartRun(sandbox, "await tools.echo(1); await Promise.all([tools.echo(2), tools.echo(3)]); throw new Error('x')");
                var first = StartRun(sandbox, "return 1");
                var a = await returned.Run.Result;
                Ok(a, "2", "returned"); Equal(new CodemodeDuring(2, 2), a.During, "returned during");
                var b = await thrown.Run.Result;
                Check(!b.Ok, "thrown"); Equal(new CodemodeDuring(3, 3), b.During, "thrown during");
                var c = await first.Run.Result;
                Ok(c, "1", "first run"); Equal(new CodemodeDuring(0, 0), c.During, "first run during");
                Equal(null, (await sandbox.ExecuteAsync("return 1")).During, "execute() has none");
            }),
            Case($"deterministic.{host}.leaves-a-call-waiting-when-its-answer-is-rejected", async () =>
            {
                // deterministic.test.ts answers with values JSON cannot hold (1n); a JsonData always holds JSON, so the answer that cannot be
                // sent here is one whose properties are not an object.
                await using var sandbox = Create();
                var recorded = StartRun(sandbox, "return await tools.wait(1)");
                await WaitFor(() => recorded.Calls.Count == 1);
                Throws<ArgumentException>(() => recorded.Run.Deliver(1, StartedAt, 1, CodemodeOutcome.Failure("x", Parse("7"))), "number properties");
                recorded.Run.Deliver(1, StartedAt, 1, CodemodeOutcome.Success(Parse("\"fine\"")));
                Ok(await recorded.Run.Result, "\"fine\"", "answered after the rejected answer");
            }),
            Case($"deterministic.{host}.rejects-with-an-error-carrying-the-answers-properties", async () =>
            {
                await using var sandbox = Create();
                var recorded = StartRun(sandbox,
                    "try { await tools.echo(1); } catch (e) { return [e instanceof Error, e.message, e.name, e.structuredOutput]; }",
                    answer: _ => CodemodeOutcome.Failure("denied", Parse("""{"structuredOutput":{"code":7},"name":"Ignored","message":"ignored"}""")));
                Ok(await recorded.Run.Result, """[true,"denied","Error",{"code":7}]""", "error");
            }),
            Case($"deterministic.{host}.rejects-rejection-properties-that-are-not-an-object-and-tools-sharing-a-script-name", async () =>
            {
                await using var sandbox = Create();
                var recorded = StartRun(sandbox, "return await tools.wait(1)");
                await WaitFor(() => recorded.Calls.Count == 1);
                foreach (var properties in new[] { "null", "[1]" })
                    Check(Throws<ArgumentException>(() => recorded.Run.Deliver(1, StartedAt, 1, CodemodeOutcome.Failure("x", Parse(properties))), properties)
                        .Message.Contains("not an object", StringComparison.Ordinal), properties);
                recorded.Run.Deliver(1, StartedAt, 1, CodemodeOutcome.Success(Parse("1")));
                Ok(await recorded.Run.Result, "1", "value");
                Check(Throws<ArgumentException>(() => sandbox.Start("", new() { StartedAt = StartedAt, Seed = 1, Tools = [new("a-b"), new("a_b")], OnCall = _ => { } }), "script names")
                    .Message.Contains("script names must be unique", StringComparison.Ordinal), "script names message");
            }),
            Case($"deterministic.{host}.uses-the-script-names-the-run-passes", async () =>
            {
                await using var sandbox = Create();
                var recorded = StartRun(sandbox, "return [typeof tools.renamed, Object.keys(tools), ALL_TOOLS.map((tool) => tool.name)]",
                    options => options with { Tools = [new("my-tool") { JsName = "renamed" }] });
                Ok(await recorded.Run.Result, """["function",["renamed","my-tool"],["renamed"]]""", "names");
            }),
            Case($"deterministic.{host}.ends-with-an-error-the-script-cannot-catch-when-the-caller-aborts", async () =>
            {
                await using var sandbox = Create();
                using var controller = new CancellationTokenSource();
                var recorded = StartRun(sandbox, """
                    try { await tools.wait(1); } catch { text("caught"); }
                    text("after");
                    """, options => options with { AbortMessage = "diverged" }, token: controller.Token);
                await WaitFor(() => recorded.Calls.Count == 1);
                controller.Cancel();
                var result = await recorded.Run.Result;
                Failed(result, CodemodeErrorKind.Aborted, "aborted", message: "diverged");
                Equal("diverged", result.Error!.Message, "message");
                Equal("", Output(result), "no output");
                recorded.Run.Deliver(1, StartedAt, 1, CodemodeOutcome.Success(JsonData.Null));
                // CodemodeRun.AbortAsync is the AbortSignal-with-a-reason form.
                var second = StartRun(sandbox, "await tools.wait(1)");
                await WaitFor(() => second.Calls.Count == 1);
                var aborted = await second.Run.AbortAsync("Sandbox fingerprint changed");
                Failed(aborted, CodemodeErrorKind.Aborted, "AbortAsync", message: "Sandbox fingerprint changed");
                Check(second.Run.Finished, "finished");
            }),
            Case($"deterministic.{host}.is-finished-once-it-has-its-outcome", async () =>
            {
                await using var sandbox = Create();
                var recorded = StartRun(sandbox, "await tools.wait(1); return 2");
                await WaitFor(() => recorded.Calls.Count == 1);
                Check(!recorded.Run.Finished, "not finished while waiting");
                recorded.Run.Deliver(1, StartedAt, 1, CodemodeOutcome.Success(JsonData.Null));
                await recorded.Run.Result;
                Check(recorded.Run.Finished, "finished");
            }),
            Case($"deterministic.{host}.rejects-a-call-that-is-not-waiting-and-a-result-number-used-twice", async () =>
            {
                await using var sandbox = Create();
                var recorded = StartRun(sandbox, "await Promise.all([tools.wait(1), tools.wait(2)])");
                await WaitFor(() => recorded.Calls.Count == 2);
                var run = recorded.Run;
                Check(Throws<InvalidOperationException>(() => run.Deliver(1, StartedAt, 99, CodemodeOutcome.Success(JsonData.Null)), "99")
                    .Message.Contains("Call 99 is not waiting", StringComparison.Ordinal), "not waiting");
                run.Deliver(1, StartedAt, 1, CodemodeOutcome.Success(JsonData.Null));
                Check(Throws<ArgumentException>(() => run.Deliver(1, StartedAt, 2, CodemodeOutcome.Success(JsonData.Null)), "seq 1 again")
                    .Message.Contains("used once", StringComparison.Ordinal), "used twice");
                Check(Throws<ArgumentException>(() => run.Deliver(0, StartedAt, 2, CodemodeOutcome.Success(JsonData.Null)), "seq 0")
                    .Message.Contains("used once", StringComparison.Ordinal), "seq 0");
                run.Deliver(2, StartedAt, 2, CodemodeOutcome.Success(JsonData.Null));
                Check((await run.Result).Ok, "ran");
            }),
            // Authored: what durable code mode does after a restart. A run is interrupted after some results; a new run with the same
            // start data gets the journaled results again, each once the engine has issued its call, then continues live. It makes the
            // same calls, reports the same counts and ends with the same outcome as a run that was never interrupted, also on the other
            // engine host (both report the same fingerprint).
            Case($"deterministic.{host}.replay-from-a-midpoint-continues-like-an-uninterrupted-run", async () =>
            {
                const string code = """
                    const names = await Promise.all(["a", "bb", "ccc"].map((name) => tools.read(name)));
                    let sum = 0;
                    for (const name of names) sum += (await tools.size(name)) * Math.random();
                    const stamp = Date.now();
                    if (sum > 1) await search(String(stamp), performance.now());
                    await tools.write({ sum, stamp });
                    store("sum", sum);
                    console.log("done at", Date.now(), performance.now());
                    return { sum, stamp, last: Math.random() };
                    """;
                static CodemodeOutcome World(CodemodeHostCall call) => call.Name switch
                {
                    "read" => CodemodeOutcome.Success(Parse(JsonSerializer.Serialize(call.Args!.Value.GetString()!.ToUpperInvariant()))),
                    "size" => CodemodeOutcome.Success(Parse(call.Args!.Value.GetString()!.Length.ToString(CultureInfo.InvariantCulture))),
                    "write" => CodemodeOutcome.Failure("disk full", Parse("""{"retry":true}""")),
                    _ => CodemodeOutcome.Success(Parse("\"ok\""))
                };
                static double Clock(long seq) => StartedAt + 137.25 * seq;
                var journal = new List<(long Seq, double Now, long Id, CodemodeOutcome Outcome)>();
                CodemodeRunOptions Options(Recorded recorded, Action<CodemodeHostCall> onCall) => new()
                {
                    StartedAt = StartedAt, Seed = 99, Tools = [new("read"), new("size"), new("write")], Globals = [new("search") { Spread = true }],
                    Store = [new("sum", Parse("0"))], TimeoutMs = 20_000, OnStarted = recorded.Started,
                    OnCall = call => { recorded.Call(call); onCall(call); }, OnIdle = recorded.Idle
                };

                // The uninterrupted run, answering every call at once.
                await using var sandbox = Create();
                var live = new Recorded();
                long liveSeq = 0;
                live.Run = sandbox.Start(code, Options(live, call =>
                {
                    var seq = Interlocked.Increment(ref liveSeq);
                    var outcome = World(call);
                    lock (journal) journal.Add((seq, Clock(seq), call.Id, outcome));
                    live.Run.Deliver(seq, Clock(seq), call.Id, outcome);
                }));
                var expected = await live.Run.Result;
                Check(journal.Count >= 6, "journal size " + journal.Count);
                Equal("disk full", expected.Error?.Message ?? "", "the write fails the script");

                foreach (var midpoint in new[] { 0, 1, 3, journal.Count - 1 })
                {
                    // The interrupted attempt: the first `midpoint` results, then the process dies.
                    var interrupted = new Recorded();
                    long delivered = 0;
                    interrupted.Run = sandbox.Start(code, Options(interrupted, call =>
                    {
                        var entry = journal.First(item => item.Id == call.Id);
                        if (entry.Seq > midpoint) return;
                        Interlocked.Increment(ref delivered);
                        interrupted.Run.Deliver(entry.Seq, entry.Now, entry.Id, entry.Outcome);
                    }));
                    await WaitFor(() => interrupted.Idles.Any(idle => idle.Seq == midpoint));
                    await interrupted.Run.AbortAsync("process killed");

                    // The resumed run, on the other host: replay the journal up to the midpoint, then continue live.
                    var otherHost = Hosts.First(other => other.Name != host).Host;
                    await using var resumedSandbox = new CodemodeSandbox(otherHost(new()));
                    var resumed = new Recorded();
                    var ready = new Dictionary<long, CodemodeHostCall>();
                    long next = 0, liveNext = midpoint;
                    var replayGate = new object();
                    resumed.Run = resumedSandbox.Start(code, Options(resumed, call =>
                    {
                        lock (replayGate)
                        {
                            var entry = journal.FirstOrDefault(item => item.Id == call.Id && item.Seq <= midpoint);
                            if (entry.Seq == 0)
                            {
                                // A new call: answered live, numbered after the replayed results.
                                var seq = ++liveNext;
                                resumed.Run.Deliver(seq, Clock(seq), call.Id, World(call));
                                return;
                            }
                            ready[entry.Id] = call;
                            // durable codemode.ts deliverReplayed: in journal order, each once its call was issued.
                            while (next < midpoint && ready.ContainsKey(journal[(int)next].Id))
                            {
                                var replay = journal[(int)next++];
                                resumed.Run.Deliver(replay.Seq, replay.Now, replay.Id, replay.Outcome);
                            }
                        }
                    }));
                    var actual = await resumed.Run.Result;
                    Equal(live.Fingerprint, resumed.Fingerprint, $"midpoint {midpoint}: fingerprint on both hosts");
                    Equal(Timeless(expected), Timeless(actual), $"midpoint {midpoint}: outcome");
                    Equal(string.Join(" ", live.Calls.Select(call => $"{call.Id}:{call.Target}:{call.Name}:{call.Args}")),
                        string.Join(" ", resumed.Calls.Select(call => $"{call.Id}:{call.Target}:{call.Name}:{call.Args}")), $"midpoint {midpoint}: calls");
                    Equal(string.Join(" ", live.Idles), string.Join(" ", resumed.Idles), $"midpoint {midpoint}: issued counts");
                    // What the interrupted attempt saw is a prefix of the uninterrupted run.
                    Equal(string.Join(" ", live.Idles.Take(interrupted.Idles.Count)), string.Join(" ", interrupted.Idles), $"midpoint {midpoint}: prefix");
                }

                // Another seed takes another path: a replay must start with the stored seed.
                var reseeded = new Recorded();
                long reseededSeq = 0;
                reseeded.Run = sandbox.Start(code, Options(reseeded, call =>
                {
                    var seq = Interlocked.Increment(ref reseededSeq);
                    reseeded.Run.Deliver(seq, Clock(seq), call.Id, World(call));
                }) with { Seed = 100 });
                Check(Timeless(await reseeded.Run.Result) != Timeless(expected), "another seed diverges");
            }),
            // Authored: Jint has built-ins QuickJS lacks; in a deterministic run none of them reads the host's clock, zone or randomness.
            Case($"deterministic.{host}.jint-built-ins-read-only-the-virtual-environment", async () =>
            {
                await using var sandbox = Create();
                var recorded = StartRun(sandbox, """
                    const at = () => [String(Temporal.Now.instant()), Temporal.Now.timeZoneId(), String(Temporal.Now.plainDateTimeISO()),
                      new Intl.DateTimeFormat("en-US", { hour: "numeric", minute: "numeric" }).format(Date.now()),
                      Intl.DateTimeFormat().resolvedOptions().timeZone, new Intl.DateTimeFormat() instanceof Intl.DateTimeFormat,
                      new Intl.DateTimeFormat("en-US", { timeZone: "Asia/Kolkata", hour: "numeric" }).format(0),
                      new Date(0).toLocaleString(), (1234.5).toLocaleString(), performance.now()];
                    const before = at();
                    await tools.echo(1);
                    return [before, at(), typeof ShadowRealm, typeof performance.now, String(Math.random), Intl.DateTimeFormat.name,
                      Intl.DateTimeFormat.length, typeof Intl.DateTimeFormat.supportedLocalesOf, Object.isFrozen(performance)];
                    """);
                Ok(await recorded.Run.Result, """
                    [["2026-01-15T10:00:00Z","UTC","2026-01-15T10:00:00","10:00 AM","UTC",true,"5 AM","1/1/1970, 12:00:00 AM","1,234.5",0],["2026-01-15T10:00:01Z","UTC","2026-01-15T10:00:01","10:00 AM","UTC",true,"5 AM","1/1/1970, 12:00:00 AM","1,234.5",1000],"undefined","function","function random() { [native code] }","DateTimeFormat",0,"function",true]
                    """.Trim(), "built-ins");
            }),
        ];
    }

    // ------------------------------------------------------------------ differential against the real upstream package

    private static JsonElement DeterministicGoldens()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Deterministic", "deterministic-goldens.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement.Clone();
        Equal("1.2.0", root.GetProperty("piCodemode").GetString(), "pi-codemode version");
        return root;
    }

    /// <summary>gen-deterministic-goldens.mjs answer(): echo resolves with its argument except "fail"; search with ["found", args].</summary>
    private static CodemodeOutcome GoldenAnswer(CodemodeHostCall call)
    {
        if (call.Target == CodemodeCallTarget.Global && call.Name == "search")
            return CodemodeOutcome.Success(Parse($"[\"found\",{call.Args}]"));
        if (call.Args is { Value.ValueKind: JsonValueKind.String } text && text.Value.GetString() == "fail")
            return CodemodeOutcome.Failure("no", Parse("""{"code":7,"name":"Ignored"}"""));
        return CodemodeOutcome.Success(call.Args);
    }

    /// <summary>Runs one golden scenario with the generator's policy; returns its events, calls and result as the generator records them.</summary>
    private static async Task<JsonObject> RunScenario(CodemodeSandbox sandbox, JsonElement golden, JsonElement scenario)
    {
        var startedAt = scenario.GetProperty("startedAt").GetDouble();
        var step = scenario.GetProperty("step").GetDouble();
        var batch = scenario.GetProperty("policy").GetString() == "batch";
        var gate = new object();
        var events = new JsonArray();
        var calls = new JsonArray();
        var unanswered = new List<CodemodeHostCall>();
        long seq = 0;
        CodemodeRun run = null!;
        void Deliver(long number, CodemodeHostCall call) => run.Deliver(number, startedAt + step * number, call.Id, GoldenAnswer(call));
        var target = (CodemodeCallTarget value) => value == CodemodeCallTarget.Tool ? "tool" : "global";
        run = sandbox.Start(scenario.GetProperty("code").GetString()!, new()
        {
            StartedAt = startedAt, Seed = scenario.GetProperty("seed").GetUInt32(), TimeoutMs = 20_000,
            Tools = [.. golden.GetProperty("tools").EnumerateArray().Select(tool => new CodemodeDeclaredTool(tool.GetProperty("name").GetString()!)
            {
                JsName = tool.TryGetProperty("jsName", out var jsName) ? jsName.GetString() : null,
                Description = tool.TryGetProperty("description", out var description) ? description.GetString() : null
            })],
            Globals = [.. golden.GetProperty("globals").EnumerateArray().Select(global => new CodemodeDeclaredGlobal(global.GetProperty("name").GetString()!)
            {
                Spread = global.TryGetProperty("spread", out var spread) && spread.GetBoolean()
            })],
            Store = [.. scenario.GetProperty("store").EnumerateObject().Select(entry => new KeyValuePair<string, JsonData>(entry.Name, JsonData.FromElement(entry.Value)))],
            OnStarted = _ => { lock (gate) events.Add("started"); },
            OnCall = call =>
            {
                lock (gate)
                {
                    events.Add($"call {call.Id} {target(call.Target)} {call.Name}");
                    var recorded = new JsonObject { ["id"] = call.Id, ["target"] = target(call.Target), ["name"] = call.Name };
                    if (call.Args is { } args) recorded["args"] = JsonNode.Parse(args.ToString());
                    calls.Add(recorded);
                    if (!batch) Deliver(++seq, call);
                    else unanswered.Add(call);
                }
            },
            OnIdle = (number, issued) =>
            {
                lock (gate)
                {
                    events.Add($"idle {number} {issued}");
                    if (!batch || unanswered.Count == 0) return;
                    var pending = unanswered.OrderBy(call => call.Id).ToList();
                    unanswered.Clear();
                    var numbers = Enumerable.Reverse(pending).ToDictionary(call => call.Id, _ => ++seq);
                    foreach (var call in pending) Deliver(numbers[call.Id], call);
                }
            }
        });
        var result = await run.Result;
        var outcome = new JsonObject { ["ok"] = result.Ok };
        if (result.Ok)
        {
            if (result.Value is { } value) outcome["value"] = JsonNode.Parse(value.ToString());
            var set = new JsonObject();
            foreach (var (key, written) in result.StoreWrites.Set) set[key] = JsonNode.Parse(written.ToString());
            outcome["storeWrites"] = new JsonObject { ["set"] = set, ["delete"] = new JsonArray([.. result.StoreWrites.Delete.Select(key => (JsonNode?)key)]) };
        }
        else
        {
            var error = new JsonObject { ["kind"] = result.Error!.Kind.ToString().ToLowerInvariant() };
            if (result.Error.Name is { } name) error["name"] = name;
            error["message"] = result.Error.Message;
            outcome["error"] = error;
        }
        outcome["output"] = new JsonArray([.. result.Output.Select(item => (JsonNode?)(item switch
        {
            CodemodeTextOutput { Console: true } text => new JsonObject { ["type"] = "text", ["text"] = text.Text, ["console"] = true },
            CodemodeTextOutput text => new JsonObject { ["type"] = "text", ["text"] = text.Text },
            CodemodeImageOutput image => new JsonObject { ["type"] = "image", ["data"] = image.Data, ["mimeType"] = image.MimeType },
            _ => throw new InvalidOperationException("unknown output")
        }))]);
        outcome["calls"] = new JsonArray([.. result.Calls.Select(call => (JsonNode?)new JsonObject { ["name"] = call.Name, ["status"] = call.Status.ToString().ToLowerInvariant() })]);
        if (result.During is { } during) outcome["during"] = new JsonObject { ["seq"] = during.Seq, ["issued"] = during.Issued };
        lock (gate) return new JsonObject { ["events"] = events.DeepClone(), ["calls"] = calls.DeepClone(), ["result"] = outcome };
    }

    private static IEnumerable<(string, Func<Task>)> DeterministicDifferentialCases() =>
    [
        // The prelude is upstream's byte for byte, so its fingerprint part equals upstream's.
        Case("differential.deterministic-prelude-hash", async () =>
        {
            var golden = DeterministicGoldens();
            await using var sandbox = new CodemodeSandbox();
            var recorded = StartRun(sandbox, "return 1");
            await recorded.Run.Result;
            Check(golden.GetProperty("upstreamFingerprint").GetString()!.Contains($"prelude {golden.GetProperty("preludeHash").GetString()},", StringComparison.Ordinal), "golden fingerprint");
            Check(recorded.Fingerprint!.Contains($"prelude {golden.GetProperty("preludeHash").GetString()},", StringComparison.Ordinal),
                $"PiSharp {recorded.Fingerprint} against upstream {golden.GetProperty("upstreamFingerprint").GetString()}");
        }),
        .. Hosts.Select(host => Case($"differential.deterministic-runs.{host.Name}", async () =>
        {
            var golden = DeterministicGoldens();
            var scenarios = golden.GetProperty("scenarios").EnumerateArray().ToList();
            Check(scenarios.Count >= 15, "scenario count");
            await using var sandbox = new CodemodeSandbox(host.Host(new()));
            var mismatches = new List<string>();
            foreach (var scenario in scenarios)
            {
                var expected = JsonNode.Parse(scenario.GetProperty("expected").GetRawText())!;
                var actual = await RunScenario(sandbox, golden, scenario);
                if (!JsonNode.DeepEquals(expected, actual))
                    mismatches.Add($"{scenario.GetProperty("name").GetString()}:\nexpected {expected.ToJsonString()}\nactual   {actual.ToJsonString()}");
            }
            NoMismatches(mismatches, scenarios.Count, "deterministic runs");
        })),
    ];
}
