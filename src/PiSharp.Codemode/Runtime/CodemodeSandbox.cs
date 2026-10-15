// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/codemode/src/runtime/execution.ts (CodemodeSandboxBase.execute and start, Execution),
// packages/codemode/src/runtime/host.ts, packages/codemode/src/runtime/worker.ts and packages/codemode/src/runtime/protocol.ts. The QuickJS worker is replaced by a Jint engine in a child process (or a thread)
// (decision 0003); docs/compatibility/codemode-engine.md lists the differences.
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.Codemode;

/// <summary>
/// Runs JavaScript in a fresh Jint engine, in a worker process of its own (or on a thread without a launcher). The script sees <c>tools.&lt;name&gt;(args)</c> for every
/// registered tool, <c>ALL_TOOLS</c>, the output helpers <c>text</c>, <c>image</c>, <c>exit</c> and <c>console.*</c>,
/// <c>store</c>/<c>load</c> and the configured globals; nothing else (no timers, network, file system, modules or CLR
/// access). Each execution gets its own engine and worker; the sandbox only holds the tool table and defaults.
/// <see cref="CloseAsync"/> aborts in-flight executions.
/// </summary>
public sealed class CodemodeSandbox : IAsyncDisposable
{
    private static readonly ImmutableHashSet<string> ReservedGlobals =
        ["tools", "ALL_TOOLS", "console", "text", "image", "exit", "globalThis", "store", "load"];
    private readonly object gate = new();
    private readonly List<CodemodeTool> tools = [];
    private readonly ImmutableArray<CodemodeTool> globals;
    private readonly CodemodeSandboxOptions options;
    private readonly HashSet<Execution> running = [];
    private bool closed;

    public CodemodeSandbox(CodemodeSandboxOptions? options = null)
    {
        this.options = options ?? new();
        foreach (var tool in this.options.Tools.IsDefault ? [] : this.options.Tools) RegisterTool(tool);
        CheckGlobals((this.options.Globals.IsDefault ? [] : this.options.Globals).Select(global =>
        {
            ArgumentNullException.ThrowIfNull(global);
            return global.Name;
        }));
        globals = this.options.Globals.IsDefault ? [] : this.options.Globals;
    }

    /// <summary>execution.ts checkGlobals: identifiers or <c>namespace.member</c>, not shadowing a built-in global or each other.</summary>
    private static void CheckGlobals(IEnumerable<string> globalNames)
    {
        var names = new HashSet<string>(StringComparer.Ordinal); var namespaces = new HashSet<string>(StringComparer.Ordinal);
        foreach (var globalName in globalNames)
        {
            var parts = globalName.Split('.');
            if (parts.Length > 2 || !parts.All(CodemodeIdentifier.IsIdentifier) || ReservedGlobals.Contains(parts[0]))
                throw new ArgumentException($"Invalid global name \"{globalName}\"");
            if (!names.Add(globalName)) throw new ArgumentException($"Global \"{globalName}\" is already registered");
            if (parts.Length == 2) namespaces.Add(parts[0]);
        }
        foreach (var name in namespaces)
            if (names.Contains(name)) throw new ArgumentException($"Global \"{name}\" conflicts with the namespace \"{name}\"");
    }

    /// <summary>Throws if a tool with the same name is already registered.</summary>
    public void RegisterTool(CodemodeTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        lock (gate)
        {
            if (tools.Any(existing => existing.Name == tool.Name)) throw new ArgumentException($"Tool \"{tool.Name}\" is already registered");
            tools.Add(tool);
        }
    }

    public bool UnregisterTool(string name) { lock (gate) return tools.RemoveAll(tool => tool.Name == name) > 0; }
    public ImmutableArray<CodemodeTool> Tools { get { lock (gate) return [.. tools]; } }
    public ImmutableArray<CodemodeTool> Globals => globals;

    /// <summary><paramref name="code"/> is an async function body: <c>return</c> and top-level <c>await</c> work. Never
    /// throws for script failures; those come back as a failed result.</summary>
    public Task<CodemodeResult> ExecuteAsync(string code, CodemodeExecuteOptions? executeOptions = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(code);
        Execution execution;
        lock (gate)
        {
            if (closed) return Task.FromException<CodemodeResult>(new InvalidOperationException("Sandbox is closed"));
            ImmutableArray<CodemodeTool> current = [.. tools];
            execution = new(new(code, ToolsJson(current.Select(tool => (tool.Name, CodemodeIdentifier.ToIdentifier(tool.Name), tool.Description))),
                GlobalsJson(globals.Select(global => (global.Name, global.Spread))), executeOptions?.Store,
                executeOptions?.TimeoutMs ?? options.TimeoutMs, executeOptions?.AbortMessage ?? AbortedMessage)
            {
                Tools = current.GroupBy(tool => tool.Name, StringComparer.Ordinal).ToImmutableDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal),
                Globals = globals.ToImmutableDictionary(global => global.Name, StringComparer.Ordinal)
            }, options, cancellationToken);
            running.Add(execution);
        }
        execution.Start();
        return Track(execution);
    }

    private async Task<CodemodeResult> Track(Execution started)
    {
        try { return await started.Result.ConfigureAwait(false); }
        finally { lock (gate) running.Remove(started); }
    }

    /// <summary>
    /// execution.ts <c>CodemodeSandboxBase.start()</c>: runs <paramref name="code"/> deterministically, with the caller answering its
    /// calls. The same script, start time, seed, tools, globals and store, answered with the same results in the same <c>seq</c> order,
    /// makes the same calls with the same arguments and produces the same output, on any engine with the same fingerprint, so a host
    /// can replay it after a restart. The engine runs in UTC with the invariant culture, its clock reads <c>StartedAt</c> and then the
    /// <c>now</c> of the result being processed, its randomness comes from <c>Seed</c>, and <c>WeakRef</c>,
    /// <c>FinalizationRegistry</c> and <c>ShadowRealm</c> do not exist.
    /// <para>The sandbox's own tools and globals are not used: the run declares its own, and every call goes to
    /// <see cref="CodemodeRunOptions.OnCall"/>. The sandbox's limits and worker launcher apply. Never throws for script failures;
    /// cancel <paramref name="cancellationToken"/> or call <see cref="CodemodeRun.AbortAsync"/> to end the run with an error the script
    /// cannot catch.</para>
    /// </summary>
    public CodemodeRun Start(string code, CodemodeRunOptions runOptions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(runOptions);
        ArgumentNullException.ThrowIfNull(runOptions.OnCall);
        if (!double.IsFinite(runOptions.StartedAt) || runOptions.StartedAt < 0)
            throw new ArgumentException("startedAt must be a non-negative number of milliseconds since the epoch", nameof(runOptions));
        var declaredTools = runOptions.Tools.IsDefault ? [] : runOptions.Tools;
        var declaredGlobals = runOptions.Globals.IsDefault ? [] : runOptions.Globals;
        foreach (var tool in declaredTools) ArgumentNullException.ThrowIfNull(tool);
        foreach (var global in declaredGlobals) ArgumentNullException.ThrowIfNull(global);
        if (declaredTools.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count() != declaredTools.Length)
            throw new ArgumentException("Tool names must be unique", nameof(runOptions));
        var scriptNames = declaredTools.Select(tool => tool.JsName ?? CodemodeIdentifier.ToIdentifier(tool.Name)).ToList();
        if (scriptNames.Distinct(StringComparer.Ordinal).Count() != declaredTools.Length)
            throw new ArgumentException("Tool script names must be unique", nameof(runOptions));
        CheckGlobals(declaredGlobals.Select(global => global.Name));
        Execution execution;
        lock (gate)
        {
            if (closed) throw new InvalidOperationException("Sandbox is closed");
            execution = new(new(code, ToolsJson(declaredTools.Select((tool, index) => (tool.Name, scriptNames[index], tool.Description))),
                GlobalsJson(declaredGlobals.Select(global => (global.Name, global.Spread))), runOptions.Store,
                runOptions.TimeoutMs ?? double.PositiveInfinity, runOptions.AbortMessage ?? AbortedMessage)
            {
                Deterministic = new(runOptions.StartedAt, runOptions.Seed),
                Control = runOptions
            }, options, cancellationToken);
            running.Add(execution);
        }
        // As in JavaScript, where start() returns before any callback can run, no callback runs before Start returns.
        try
        {
            execution.Start();
            return new CodemodeRun(execution, Track(execution));
        }
        finally { execution.Open(); }
    }

    private static string ToolsJson(IEnumerable<(string Name, string JsName, string? Description)> tools) =>
        Js.Serialize(tools.Select(tool => new { name = tool.Name, jsName = tool.JsName, description = tool.Description ?? "" }));
    private static string GlobalsJson(IEnumerable<(string Name, bool Spread)> globals) =>
        Js.Serialize(globals.Select(global => new { name = global.Name, spread = global.Spread }));

    /// <summary>Aborts in-flight executions (they resolve as aborted) and rejects new ones.</summary>
    public async Task CloseAsync()
    {
        Execution[] executions;
        lock (gate) { closed = true; executions = [.. running]; }
        await Task.WhenAll(executions.Select(execution => execution.AbortAsync("Sandbox closed"))).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => new(CloseAsync());


    /// <summary>How long a finished execution waits for its engine to stop before it settles anyway (tests shorten it).</summary>
    internal static TimeSpan TerminationLimit { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Message of an aborted execution, as an AbortSignal without a reason reports it.</summary>
    internal const string AbortedMessage = "This operation was aborted";

    private sealed class BridgeException(string message) : Exception(message);

    private sealed class PendingCall(CallRecord? record)
    {
        public CallRecord? Record { get; } = record;
        public Stopwatch Started { get; } = Stopwatch.StartNew();
        public CancellationTokenSource Cancellation { get; } = new();
    }

    private sealed class CallRecord(string name)
    {
        public string Name { get; } = name;
        public CodemodeCallStatus Status { get; set; } = CodemodeCallStatus.Cancelled;
        public double DurationMs { get; set; }
    }

    /// <summary>What one execution runs: <c>Tools</c> and <c>Globals</c> run the script's calls in <see cref="ExecuteAsync"/>;
    /// <c>Control</c> hands them to the caller in <see cref="Start"/>, which also sets <c>Deterministic</c>.</summary>
    internal sealed record ExecutionPlan(string Code, string ToolsJson, string GlobalsJson, IReadOnlyList<KeyValuePair<string, JsonData>>? Store,
        double? TimeoutMs, string AbortMessage)
    {
        public ImmutableDictionary<string, CodemodeTool> Tools { get; init; } = ImmutableDictionary<string, CodemodeTool>.Empty;
        public ImmutableDictionary<string, CodemodeTool> Globals { get; init; } = ImmutableDictionary<string, CodemodeTool>.Empty;
        public CodemodeDeterministicStart? Deterministic { get; init; }
        public CodemodeRunOptions? Control { get; init; }
    }

    /// <summary>host.ts Execution: one script in its own engine (a child process, or a thread without a launcher). The first of
    /// done, timeout, abort and failure finishes it; the engine is then terminated and the result resolves once it is gone.</summary>
    internal sealed class Execution
    {
        private readonly ExecutionPlan plan;
        private readonly CodemodeRunOptions? control;
        private readonly double? timeoutMs;
        private readonly CodemodeSandboxOptions options;
        private readonly CancellationToken cancellationToken;
        /// <summary>Result numbers <see cref="Deliver"/> used.</summary>
        private readonly HashSet<long> delivered = [];
        private readonly object gate = new();
        private readonly List<CodemodeOutputItem> output = [];
        private readonly List<CallRecord> calls = [];
        private readonly Dictionary<long, PendingCall> pending = [];
        private readonly TaskCompletionSource<CodemodeResult> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ICodemodeWorker worker;
        private CancellationTokenRegistration abortRegistration;
        private Timer? timer;
        private bool finished;
        /// <summary>Start runs: set once Start has returned the run; until then the worker's messages wait (see <see cref="Open"/>).</summary>
        private readonly ManualResetEventSlim opened = new();
        private int startingThread;

        public Execution(ExecutionPlan plan, CodemodeSandboxOptions options, CancellationToken cancellationToken)
        {
            this.plan = plan; control = plan.Control; timeoutMs = plan.TimeoutMs; this.options = options; this.cancellationToken = cancellationToken;
            worker = options.WorkerFactory?.Invoke() ?? (options.Worker is { } launcher ? new CodemodeProcessWorker(launcher) : new CodemodeThreadWorker());
        }

        public Task<CodemodeResult> Result => result.Task;

        /// <summary>Lets the worker's messages, and so the run's callbacks, through once <see cref="CodemodeSandbox.Start"/> returned.</summary>
        public void Open() => opened.Set();

        /// <summary>Whether the execution has its outcome.</summary>
        public bool Finished { get { lock (gate) return finished; } }

        /// <summary>execution.ts Execution.deliver (<see cref="CodemodeRun.Deliver"/>).</summary>
        public void Deliver(long seq, double now, long id, CodemodeOutcome outcome)
        {
            ArgumentNullException.ThrowIfNull(outcome);
            CodemodeResultMessage message;
            lock (gate)
            {
                if (finished) return;
                if (!double.IsFinite(now)) throw new ArgumentException($"The clock value of result {seq} is not a finite number", nameof(now));
                if (seq < 1 || delivered.Contains(seq)) throw new ArgumentException($"Result number {seq} is not a positive integer used once", nameof(seq));
                if (!pending.TryGetValue(id, out var call)) throw new InvalidOperationException($"Call {id} is not waiting for a result");
                if (!outcome.Ok && outcome.Properties is { } given && given.Value.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException($"The properties of result {seq} are not an object", nameof(outcome));
                // Encoded before anything is recorded, so a rejected answer leaves the call waiting for another one.
                message = new(id, outcome.Ok, outcome.Ok ? outcome.Value?.ToString() : outcome.Message)
                {
                    Seq = seq, Now = now, Properties = outcome.Ok ? null : outcome.Properties?.ToString()
                };
                delivered.Add(seq);
                pending.Remove(id);
                if (call.Record is { } record) { record.Status = outcome.Ok ? CodemodeCallStatus.Ok : CodemodeCallStatus.Error; record.DurationMs = call.Started.Elapsed.TotalMilliseconds; }
            }
            worker.Send(message);
        }

        public void Start()
        {
            startingThread = Environment.CurrentManagedThreadId;
            // Until the engine reports that the script starts, a watchdog bounds the worker's start-up; the deadline then starts.
            timer = new Timer(_ => Finish(new(CodemodeErrorKind.Sandbox, $"Worker did not start within {StartupLimit.TotalMilliseconds} ms")), null, StartupLimit, Timeout.InfiniteTimeSpan);
            if (cancellationToken.IsCancellationRequested) { Finish(new(CodemodeErrorKind.Aborted, plan.AbortMessage)); return; }
            abortRegistration = cancellationToken.Register(() => Finish(new(CodemodeErrorKind.Aborted, plan.AbortMessage)));
            try
            {
                worker.Start(new(plan.Code, plan.ToolsJson, plan.GlobalsJson, StoreJson(), options.MemoryLimitBytes, options.TotalAllocationLimitBytes, options.RecursionLimit)
                    { Deterministic = plan.Deterministic, InterruptBudget = options.InterruptBudget },
                    Received, code => Finish(new(CodemodeErrorKind.Sandbox, $"Worker exited with code {code?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"} before the script settled")),
                    error => Finish(error is FormatException format ? Bridge(format.Message) : new(CodemodeErrorKind.Sandbox, $"Sandbox host failed: {error.Message}")));
            }
            catch (Exception error) { Finish(new(CodemodeErrorKind.Sandbox, "Failed to start worker: " + error.Message)); }
        }

        private static readonly TimeSpan StartupLimit = TimeSpan.FromSeconds(60);

        /// <summary>host.ts arms the deadline when the execution starts; PiSharp arms it when the script starts in its engine.</summary>
        private void ArmDeadline()
        {
            lock (gate)
            {
                if (finished) return;
                timer?.Dispose(); timer = null;
                if (timeoutMs is { } timeout && double.IsFinite(timeout))
                {
                    var due = TimeSpan.FromMilliseconds(Math.Clamp(timeout, 0, int.MaxValue));
                    timer = new Timer(_ => Finish(new(CodemodeErrorKind.Timeout, $"Execution timed out after {Js.Number(timeout)} ms")), null, due, Timeout.InfiniteTimeSpan);
                }
            }
        }

        public Task<CodemodeResult> AbortAsync(string message) { Finish(new(CodemodeErrorKind.Aborted, message)); return result.Task; }

        private static CodemodeError Bridge(string reason) => new(CodemodeErrorKind.Sandbox,
            $"Sandbox bridge broken: {reason}. The script may have modified built-ins such as a prototype's toJSON.");

        private void Received(CodemodeWorkerMessage message)
        {
            // A worker reporting on the starting thread itself (a test double) cannot wait for Start to return.
            if (control is not null && !opened.IsSet && Environment.CurrentManagedThreadId != startingThread) opened.Wait();
            // An exception here would leave the execution unsettled.
            try
            {
                switch (message)
                {
                    case CodemodeOutputMessage item: lock (gate) if (!finished) output.Add(item.Item); break;
                    case CodemodeCallMessage call when control is not null: ForwardCall(call, control); break;
                    case CodemodeCallMessage call: HandleCall(call.Id, call.Tool, call.Name, call.Arguments); break;
                    case CodemodeDoneMessage done: HandleDone(done); break;
                    case CodemodeCrashMessage crash: Finish(new(CodemodeErrorKind.Sandbox, crash.Message)); break;
                    case CodemodeReadyMessage: ArmDeadline(); break;
                    case CodemodeStartedMessage started: if (!Finished) control?.OnStarted?.Invoke(started.Fingerprint); break;
                    case CodemodeIdleMessage idle: if (!Finished) control?.OnIdle?.Invoke(idle.Seq, idle.Issued); break;
                    case CodemodeExhaustedMessage exhausted:
                        Finish(new(CodemodeErrorKind.Timeout, $"Execution exceeded its interrupt budget of {exhausted.Budget.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
                        break;
                }
            }
            catch (BridgeException error) { Finish(Bridge(error.Message)); }
            catch (Exception error) { Finish(new(CodemodeErrorKind.Sandbox, $"Sandbox host failed: {error.Message}")); }
        }

        private void HandleDone(CodemodeDoneMessage done)
        {
            lock (gate) if (finished) return;
            // Decode everything before Finish, which must not fail once it starts.
            if (!done.Ok) { Finish(ParseScriptError(done.Error ?? throw new BridgeException("script error is missing")), during: done.During); return; }
            var value = done.Value is null ? null : ParseBridgeJson(done.Value, "return value");
            Finish(null, value, ParseStoreWrites(done.Writes ?? throw new BridgeException("store writes are missing")), done.During);
        }

        internal static JsonData ParseBridgeJson(string json, string what)
        {
            try { return JsonData.Parse(json); }
            catch (Exception) { throw new BridgeException($"{what} is not valid JSON"); }
        }

        internal static CodemodeStoreWrites ParseStoreWrites(string json)
        {
            var entries = ParseBridgeJson(json, "store writes").Value;
            if (entries.ValueKind != JsonValueKind.Array) throw new BridgeException("store writes are not an array");
            var set = new List<KeyValuePair<string, JsonData>>(); var deleted = new List<string>();
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() is not (1 or 2) || entry[0].ValueKind != JsonValueKind.String ||
                    entry.GetArrayLength() == 2 && entry[1].ValueKind != JsonValueKind.String)
                    throw new BridgeException("store writes contain a malformed entry");
                var key = entry[0].GetString()!;
                if (entry.GetArrayLength() == 1) deleted.Add(key);
                else set.Add(new(key, ParseBridgeJson(entry[1].GetString()!, $"store value for {Js.Stringify(key)}")));
            }
            return new([.. set], [.. deleted]);
        }

        internal static CodemodeError ParseScriptError(string json)
        {
            var parsed = ParseBridgeJson(json, "script error").Value;
            if (parsed.ValueKind != JsonValueKind.Object) throw new BridgeException("script error is not an object");
            string? Field(string name, bool required)
            {
                if (!parsed.TryGetProperty(name, out var value)) return required ? throw new BridgeException("script error is malformed") : null;
                return value.ValueKind == JsonValueKind.String ? value.GetString() : throw new BridgeException("script error is malformed");
            }
            return new(CodemodeErrorKind.Script, Field("message", true)!, Field("name", false), Field("stack", false));
        }

        /// <summary>execution.ts forwardCall: a host-controlled run records the call and hands it to the caller, which answers with
        /// <see cref="Deliver"/>.</summary>
        private void ForwardCall(CodemodeCallMessage message, CodemodeRunOptions run)
        {
            var arguments = message.Arguments is null ? null : ParseBridgeJson(message.Arguments, "call arguments");
            lock (gate)
            {
                if (finished) return;
                if (pending.ContainsKey(message.Id)) throw new BridgeException($"duplicate call id {message.Id}");
                CallRecord? record = message.Tool ? new(message.Name) : null;
                if (record is not null) calls.Add(record);
                pending[message.Id] = new(record);
            }
            run.OnCall(new(message.Id, message.Tool ? CodemodeCallTarget.Tool : CodemodeCallTarget.Global, message.Name, arguments));
        }

        private void HandleCall(long id, bool isTool, string name, string? argumentsJson)
        {
            PendingCall call;
            lock (gate)
            {
                if (finished) return;
                if (pending.ContainsKey(id)) throw new BridgeException($"duplicate call id {id}");
                CallRecord? record = isTool ? new(name) : null;
                if (record is not null) calls.Add(record);
                pending[id] = call = new(record);
            }
            // Like host.ts handleCall, the call starts in message order: the implementation runs until its first await here.
            _ = RunCall();
            async Task RunCall()
            {
                bool ok; string? payload;
                try
                {
                    var tool = (isTool ? plan.Tools : plan.Globals).GetValueOrDefault(name)
                        ?? throw new InvalidOperationException($"Unknown {(isTool ? "tool" : "global")} \"{name}\"");
                    var arguments = argumentsJson is null ? null : JsonData.Parse(argumentsJson);
                    var value = tool.Execute is null ? null : await tool.Execute(arguments, call.Cancellation.Token).ConfigureAwait(false);
                    ok = true; payload = value?.ToString();
                }
                catch (Exception error) { ok = false; payload = error.Message; }
                lock (gate)
                {
                    // Already cancelled by Finish: the record keeps "cancelled" and the engine is gone or going.
                    if (finished || !pending.Remove(id)) return;
                    if (call.Record is { } record) { record.Status = ok ? CodemodeCallStatus.Ok : CodemodeCallStatus.Error; record.DurationMs = call.Started.Elapsed.TotalMilliseconds; }
                }
                worker.Send(new(id, ok, payload));
            }
        }

        private void Finish(CodemodeError? error, JsonData? value = null, CodemodeStoreWrites? writes = null, CodemodeDuring? during = null)
        {
            CodemodeResult outcome;
            lock (gate)
            {
                if (finished) return;
                finished = true;
                foreach (var call in pending.Values)
                {
                    if (call.Record is { } record) record.DurationMs = call.Started.Elapsed.TotalMilliseconds;
                    try { call.Cancellation.Cancel(); } catch (AggregateException) { }
                }
                pending.Clear();
                var recorded = calls.Select(call => new CodemodeCall(call.Name, call.Status, call.DurationMs)).ToImmutableArray();
                outcome = error is null
                    ? new(true, value, [.. output], recorded, writes ?? CodemodeStoreWrites.Empty, null) { During = during }
                    : new(false, null, [.. output], recorded, CodemodeStoreWrites.Empty, error) { During = during };
            }
            timer?.Dispose();
            abortRegistration.Dispose();
            _ = Task.Run(async () =>
            {
                // The outcome is decided: a termination that stalls must not keep it from the caller.
                try { await worker.TerminateAsync().WaitAsync(TerminationLimit).ConfigureAwait(false); } catch (Exception) { }
                result.TrySetResult(outcome);
            });
        }

        private string StoreJson()
        {
            var entries = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in plan.Store ?? []) entries[key] = Js.Stringify(value.Value);
            return Js.Serialize(entries);
        }
    }
}
