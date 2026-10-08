// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/codemode/src/runtime/host.ts, packages/codemode/src/runtime/worker.ts
// and packages/codemode/src/runtime/protocol.ts. The QuickJS worker is replaced by a Jint engine in a child process (or a thread)
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
        var names = new HashSet<string>(StringComparer.Ordinal); var namespaces = new HashSet<string>(StringComparer.Ordinal);
        foreach (var global in this.options.Globals.IsDefault ? [] : this.options.Globals)
        {
            ArgumentNullException.ThrowIfNull(global);
            var parts = global.Name.Split('.');
            if (parts.Length > 2 || !parts.All(CodemodeIdentifier.IsIdentifier) || ReservedGlobals.Contains(parts[0]))
                throw new ArgumentException($"Invalid global name \"{global.Name}\"");
            if (!names.Add(global.Name)) throw new ArgumentException($"Global \"{global.Name}\" is already registered");
            if (parts.Length == 2) namespaces.Add(parts[0]);
        }
        foreach (var name in namespaces)
            if (names.Contains(name)) throw new ArgumentException($"Global \"{name}\" conflicts with the namespace \"{name}\"");
        globals = this.options.Globals.IsDefault ? [] : this.options.Globals;
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
            execution = new(code, [.. tools], globals, executeOptions?.TimeoutMs ?? options.TimeoutMs, options, executeOptions?.Store, cancellationToken) { AbortMessage = executeOptions?.AbortMessage ?? AbortedMessage };
            running.Add(execution);
        }
        execution.Start();
        return Track(execution);
        async Task<CodemodeResult> Track(Execution started)
        {
            try { return await started.Result.ConfigureAwait(false); }
            finally { lock (gate) running.Remove(started); }
        }
    }

    /// <summary>Aborts in-flight executions (they resolve as aborted) and rejects new ones.</summary>
    public async Task CloseAsync()
    {
        Execution[] executions;
        lock (gate) { closed = true; executions = [.. running]; }
        await Task.WhenAll(executions.Select(execution => execution.AbortAsync("Sandbox closed"))).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => new(CloseAsync());


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

    /// <summary>host.ts Execution: one script in its own engine (a child process, or a thread without a launcher). The first of
    /// done, timeout, abort and failure finishes it; the engine is then terminated and the result resolves once it is gone.</summary>
    private sealed class Execution
    {
        private readonly string code;
        private readonly ImmutableDictionary<string, CodemodeTool> toolsByName;
        private readonly ImmutableDictionary<string, CodemodeTool> globalsByName;
        private readonly ImmutableArray<CodemodeTool> tools;
        private readonly ImmutableArray<CodemodeTool> globals;
        private readonly double? timeoutMs;
        private readonly CodemodeSandboxOptions options;
        private readonly IReadOnlyList<KeyValuePair<string, JsonData>>? store;
        private readonly CancellationToken cancellationToken;
        private readonly object gate = new();
        private readonly List<CodemodeOutputItem> output = [];
        private readonly List<CallRecord> calls = [];
        private readonly Dictionary<long, PendingCall> pending = [];
        private readonly TaskCompletionSource<CodemodeResult> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ICodemodeWorker worker;
        private CancellationTokenRegistration abortRegistration;
        private Timer? timer;
        private bool finished;
        public string AbortMessage { get; init; } = AbortedMessage;

        public Execution(string code, ImmutableArray<CodemodeTool> tools, ImmutableArray<CodemodeTool> globals, double? timeoutMs,
            CodemodeSandboxOptions options, IReadOnlyList<KeyValuePair<string, JsonData>>? store, CancellationToken cancellationToken)
        {
            this.code = code; this.tools = tools; this.globals = globals; this.timeoutMs = timeoutMs; this.options = options;
            this.store = store; this.cancellationToken = cancellationToken;
            toolsByName = tools.GroupBy(tool => tool.Name, StringComparer.Ordinal).ToImmutableDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            globalsByName = globals.ToImmutableDictionary(global => global.Name, StringComparer.Ordinal);
            worker = options.Worker is { } launcher ? new CodemodeProcessWorker(launcher) : new CodemodeThreadWorker();
        }

        public Task<CodemodeResult> Result => result.Task;

        public void Start()
        {
            // Until the engine reports that the script starts, a watchdog bounds the worker's start-up; the deadline then starts.
            timer = new Timer(_ => Finish(new(CodemodeErrorKind.Sandbox, $"Worker did not start within {StartupLimit.TotalMilliseconds} ms")), null, StartupLimit, Timeout.InfiniteTimeSpan);
            if (cancellationToken.IsCancellationRequested) { Finish(new(CodemodeErrorKind.Aborted, AbortMessage)); return; }
            abortRegistration = cancellationToken.Register(() => Finish(new(CodemodeErrorKind.Aborted, AbortMessage)));
            try
            {
                worker.Start(new(code, ToolsJson(), GlobalsJson(), StoreJson(), options.MemoryLimitBytes, options.TotalAllocationLimitBytes, options.RecursionLimit),
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
            // An exception here would leave the execution unsettled.
            try
            {
                switch (message)
                {
                    case CodemodeOutputMessage item: lock (gate) if (!finished) output.Add(item.Item); break;
                    case CodemodeCallMessage call: HandleCall(call.Id, call.Tool, call.Name, call.Arguments); break;
                    case CodemodeDoneMessage done: HandleDone(done); break;
                    case CodemodeCrashMessage crash: Finish(new(CodemodeErrorKind.Sandbox, crash.Message)); break;
                    case CodemodeReadyMessage: ArmDeadline(); break;
                }
            }
            catch (BridgeException error) { Finish(Bridge(error.Message)); }
            catch (Exception error) { Finish(new(CodemodeErrorKind.Sandbox, $"Sandbox host failed: {error.Message}")); }
        }

        private void HandleDone(CodemodeDoneMessage done)
        {
            lock (gate) if (finished) return;
            // Decode everything before Finish, which must not fail once it starts.
            if (!done.Ok) { Finish(ParseScriptError(done.Error ?? throw new BridgeException("script error is missing"))); return; }
            var value = done.Value is null ? null : ParseBridgeJson(done.Value, "return value");
            Finish(null, value, ParseStoreWrites(done.Writes ?? throw new BridgeException("store writes are missing")));
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
                    var tool = (isTool ? toolsByName : globalsByName).GetValueOrDefault(name)
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

        private void Finish(CodemodeError? error, JsonData? value = null, CodemodeStoreWrites? writes = null)
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
                    ? new(true, value, [.. output], recorded, writes ?? CodemodeStoreWrites.Empty, null)
                    : new(false, null, [.. output], recorded, CodemodeStoreWrites.Empty, error);
            }
            timer?.Dispose();
            abortRegistration.Dispose();
            _ = Task.Run(async () =>
            {
                try { await worker.TerminateAsync().ConfigureAwait(false); } catch (Exception) { }
                result.TrySetResult(outcome);
            });
        }

        private string ToolsJson() => Js.Serialize(tools.Select(tool => new
            { name = tool.Name, jsName = CodemodeIdentifier.ToIdentifier(tool.Name), description = tool.Description ?? "" }));
        private string GlobalsJson() => Js.Serialize(globals.Select(global => new { name = global.Name, spread = global.Spread }));
        private string StoreJson()
        {
            var entries = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in store ?? []) entries[key] = Js.Stringify(value.Value);
            return Js.Serialize(entries);
        }
    }
}
