// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/codemode/src/runtime/host.ts, packages/codemode/src/runtime/worker.ts
// and packages/codemode/src/runtime/protocol.ts. The QuickJS worker is replaced by a Jint engine on a dedicated thread
// (decision 0003); docs/compatibility/codemode-engine.md lists the differences.
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Interop;
using PiSharp.Contracts;

namespace PiSharp.Codemode;

/// <summary>
/// Runs JavaScript in a fresh Jint engine on its own thread. The script sees <c>tools.&lt;name&gt;(args)</c> for every
/// registered tool, <c>ALL_TOOLS</c>, the output helpers <c>text</c>, <c>image</c>, <c>exit</c> and <c>console.*</c>,
/// <c>store</c>/<c>load</c> and the configured globals; nothing else (no timers, network, file system, modules or CLR
/// access). Each execution gets its own engine and thread; the sandbox only holds the tool table and defaults.
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

    /// <summary>Jint compatibility shim, evaluated before the prelude so its lockdown freezes the result:
    /// error stacks use "\n" like QuickJS (Jint joins frames with the platform newline), and Atomics.wait/waitAsync, which
    /// would block the engine thread past cancellation, are removed (QuickJS's worker never blocks there).</summary>
    internal const string EngineShim = """
        (function () {
          "use strict";
          const descriptor = Object.getOwnPropertyDescriptor(Error.prototype, "stack");
          if (descriptor && typeof descriptor.get === "function") {
            const get = descriptor.get;
            Object.defineProperty(Error.prototype, "stack", {
              get() { const stack = get.call(this); return typeof stack === "string" ? stack.replace(/\r\n?/g, "\n") : stack; },
              set: descriptor.set, enumerable: descriptor.enumerable, configurable: descriptor.configurable,
            });
          }
          if (typeof Atomics === "object" && Atomics !== null) { delete Atomics.wait; delete Atomics.waitAsync; }
        })();
        """;

    private sealed class BridgeException(string message) : Exception(message);
    private sealed class AllocationLimitException(string message) : Exception(message);

    /// <summary>Allocation budget of the whole execution, never reset between host entries (Jint's memory limit is per entry).</summary>
    private sealed class TotalAllocationConstraint(long limit) : Constraint
    {
        private readonly long start = GC.GetAllocatedBytesForCurrentThread();
        public override void Check()
        {
            if (GC.GetAllocatedBytesForCurrentThread() - start > limit)
                throw new AllocationLimitException($"Script has allocated more than {limit} bytes in total");
        }
        public override void Reset() { }
    }

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

    /// <summary>One script run in its own engine and thread. The first of done, timeout, abort and failure finishes it; the
    /// engine is then cancelled and the result resolves once its thread has exited.</summary>
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
        private readonly BlockingCollection<EngineAction> inbox = [];
        private readonly CancellationTokenSource engineStop = new();
        private readonly TaskCompletionSource<CodemodeResult> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
        }

        public Task<CodemodeResult> Result => result.Task;

        public void Start()
        {
            if (timeoutMs is { } timeout && double.IsFinite(timeout))
            {
                var due = TimeSpan.FromMilliseconds(Math.Clamp(timeout, 0, int.MaxValue));
                timer = new Timer(_ => Finish(new(CodemodeErrorKind.Timeout, $"Execution timed out after {Js.Number(timeout)} ms")), null, due, Timeout.InfiniteTimeSpan);
            }
            if (cancellationToken.IsCancellationRequested) { Finish(new(CodemodeErrorKind.Aborted, AbortMessage)); exited.TrySetResult(); return; }
            abortRegistration = cancellationToken.Register(() => Finish(new(CodemodeErrorKind.Aborted, AbortMessage)));
            Thread thread;
            try
            {
                thread = new Thread(Run, Environment.Is64BitProcess ? 1024 * 1024 * 1024 : 64 * 1024 * 1024)
                { IsBackground = true, Name = "codemode" };
                thread.Start();
            }
            catch (Exception error)
            {
                Finish(new(CodemodeErrorKind.Sandbox, "Failed to start worker: " + error.Message)); exited.TrySetResult();
            }
        }

        public Task<CodemodeResult> AbortAsync(string message) { Finish(new(CodemodeErrorKind.Aborted, message)); return result.Task; }

        private void Run()
        {
            try
            {
                if (engineStop.IsCancellationRequested) return;
                var engine = new Engine(engineOptions =>
                {
                    engineOptions.LimitMemory(options.MemoryLimitBytes);
                    engineOptions.LimitRecursion(options.RecursionLimit);
                    engineOptions.CancellationToken(engineStop.Token);
                    engineOptions.Constraints.Constraints.Add(new TotalAllocationConstraint(options.TotalAllocationLimitBytes));
                    engineOptions.Constraints.MaxArraySize = 32u * 1024 * 1024;
                    engineOptions.Constraints.RegexTimeout = TimeSpan.FromSeconds(5);
                });
                engine.Execute(EngineShim, "codemode-shim.js");
                var bridge = new ClrFunction(engine, "bridge", (_, arguments) => { Bridge(arguments); return JsValue.Undefined; });
                var prelude = engine.Evaluate(CodemodePrelude.Source, "codemode-prelude.js");
                var api = engine.Call(prelude, JsValue.Undefined, [bridge, new JsString(ToolsJson()), new JsString(GlobalsJson()), new JsString(StoreJson())]);
                var apiObject = api.AsObject();
                var settle = apiObject.Get("settle"); var run = apiObject.Get("run"); var stalled = apiObject.Get("stalled");
                void Drain() { engine.Advanced.ProcessTasks(); engine.Call(stalled, api, []); }
                JsValue function;
                // The prefix shares the first line with the script so reported line numbers match the script as written.
                try { function = engine.Evaluate("(async (tools, console) => {" + code + "\n})", "codemode.js"); }
                catch (JavaScriptException error) { HandleDone(false, DescribeException(error), null, null); return; }
                engine.Call(run, api, [function]);
                Drain();
                foreach (var action in inbox.GetConsumingEnumerable(engineStop.Token))
                {
                    action(engine, settle, api);
                    Drain();
                }
            }
            catch (Exception error) when (engineStop.IsCancellationRequested &&
                error is ExecutionCanceledException or OperationCanceledException or InvalidOperationException) { }
            catch (Exception error) { Finish(Failure(error)); }
            finally { exited.TrySetResult(); }
        }

        /// <summary>Engine limits Jint enforces outside the script's reach fail the script like an uncaught error.</summary>
        private CodemodeError Failure(Exception error) => error switch
        {
            MemoryLimitExceededException or AllocationLimitException or OutOfMemoryException =>
                Script("InternalError", "out of memory"),
            RecursionDepthOverflowException => Script("RangeError", "Maximum call stack size exceeded"),
            System.Text.RegularExpressions.RegexMatchTimeoutException => Script("InternalError", "regular expression timed out"),
            ExecutionCanceledException or OperationCanceledException => new(CodemodeErrorKind.Aborted, AbortedMessage),
            BridgeException bridge => new(CodemodeErrorKind.Sandbox,
                $"Sandbox bridge broken: {bridge.Message}. The script may have modified built-ins such as a prototype's toJSON."),
            JavaScriptException script => ParseScriptError(DescribeException(script)),
            _ => new(CodemodeErrorKind.Sandbox, $"{error.GetType().Name}: {error.Message}")
        };

        private static CodemodeError Script(string name, string message) => new(CodemodeErrorKind.Script, message, name, $"{name}: {message}");

        private static string DescribeException(JavaScriptException error)
        {
            string name = "Error", message = error.Message; string? stack = null;
            if (error.Error is ObjectInstance instance)
            {
                try
                {
                    var nameValue = instance.Get("name"); if (nameValue.IsString()) name = nameValue.AsString();
                    var messageValue = instance.Get("message"); if (messageValue.IsString()) message = messageValue.AsString();
                    var stackValue = instance.Get("stack"); if (stackValue.IsString()) stack = stackValue.AsString().Replace("\r\n", "\n", StringComparison.Ordinal);
                }
                catch (Exception) { }
            }
            else if (error.Error.IsString()) message = error.Error.AsString();
            var head = message.Length > 0 ? $"{name}: {message}" : name;
            // worker.ts describeException: QuickJS stacks list frames only, as the shim makes Jint's do.
            if (stack is not null) { var trimmed = Js.Trim("x" + stack); stack = trimmed[1..]; }
            return JsonSerializer.Serialize(new { name, message, stack = string.IsNullOrEmpty(stack) ? head : $"{head}\n{stack}" });
        }

        // Called from the prelude with primitives only: (kind, a, b, c).
        private void Bridge(JsValue[] arguments)
        {
            JsValue At(int index) => index < arguments.Length ? arguments[index] : JsValue.Undefined;
            static string? Text(JsValue value) => value.IsUndefined() ? null : value.ToString();
            var kind = At(0).ToString();
            switch (kind)
            {
                case "call":
                case "global":
                    HandleCall((long)TypeConverter.ToNumber(At(1)), kind == "call", At(2).ToString(), Text(At(3)));
                    break;
                case "output":
                    var type = At(1).ToString();
                    AddOutput(type == "image" ? new CodemodeImageOutput(At(2).ToString(), At(3).ToString())
                        : new CodemodeTextOutput(At(2).ToString(), type == "console"));
                    break;
                case "done":
                    if (TypeConverter.ToBoolean(At(1))) HandleDone(true, null, Text(At(2)), At(3).ToString());
                    else HandleDone(false, At(2).ToString(), null, null);
                    break;
            }
        }

        private void AddOutput(CodemodeOutputItem item) { lock (gate) if (!finished) output.Add(item); }

        private void HandleDone(bool ok, string? errorJson, string? valueJson, string? writesJson)
        {
            lock (gate) if (finished) return;
            // Decode everything before Finish, which must not fail once it starts.
            try
            {
                if (!ok) { Finish(ParseScriptError(errorJson!)); return; }
                var value = valueJson is null ? null : ParseBridgeJson(valueJson, "return value");
                Finish(null, value, ParseStoreWrites(writesJson!));
            }
            catch (BridgeException error) { Finish(Failure(error)); }
        }

        private static JsonData ParseBridgeJson(string json, string what)
        {
            try { return JsonData.Parse(json); }
            catch (Exception) { throw new BridgeException($"{what} is not valid JSON"); }
        }

        private static CodemodeStoreWrites ParseStoreWrites(string json)
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

        private static CodemodeError ParseScriptError(string json)
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
                if (pending.ContainsKey(id)) { Finish(Failure(new BridgeException($"duplicate call id {id}"))); return; }
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
                    var settledOk = ok; var settledPayload = payload;
                    inbox.TryAdd((engine, settle, api) => engine.Call(settle, api,
                        [new JsNumber(id), settledOk ? JsBoolean.True : JsBoolean.False, settledPayload is null ? JsValue.Undefined : new JsString(settledPayload)]));
                }
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
            try { engineStop.Cancel(); } catch (AggregateException) { }
            inbox.CompleteAdding();
            _ = exited.Task.ContinueWith(_ => result.TrySetResult(outcome), TaskScheduler.Default);
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

    private delegate void EngineAction(Engine engine, JsValue settle, JsValue api);
}
