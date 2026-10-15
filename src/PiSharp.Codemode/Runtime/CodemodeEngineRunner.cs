// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/codemode/src/runtime/vm.ts (startVm, drain, deterministic mode, the
// fingerprint), packages/codemode/src/runtime/inline.ts (the interrupt budget) and packages/codemode/src/runtime/worker.ts. One script
// in a fresh Jint engine: the prelude, the script, then settled tool results until the host ends it. Deterministic mode
// (CodemodeSandbox.Start) is described in CodemodeDeterministicClock.cs.
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Interop;

namespace PiSharp.Codemode;

internal sealed class CodemodeEngineRunner(CodemodeStartMessage start, Action<CodemodeWorkerMessage> post)
{
    /// <summary>Nesting depth past which the guarded built-ins throw a catchable RangeError, as QuickJS's stack guard does.</summary>
    internal const int MaximumNativeDepth = 1000;
    private readonly BlockingCollection<CodemodeResultMessage> inbox = [];

    /// <summary>Jint compatibility shim, evaluated before the prelude so its lockdown freezes the result:
    /// <list type="bullet">
    /// <item>error stacks use "\n" like QuickJS (Jint joins frames with the platform newline);</item>
    /// <item>Atomics.wait/waitAsync, which would block the engine thread past cancellation, are removed;</item>
    /// <item>the built-ins that recurse natively on nesting depth (Array join/toString/toLocaleString, flat, JSON.stringify) throw
    /// a catchable <c>RangeError: Maximum call stack size exceeded</c> past <see cref="MaximumNativeDepth"/> levels instead of
    /// exhausting the engine thread's stack. JSON.parse is bounded by Jint's parse depth.</item>
    /// </list></summary>
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

          const MAX_DEPTH = 1000;
          const RangeErrorCtor = RangeError;
          const apply = Reflect.apply, ownKeys = Reflect.ownKeys, ownDescriptor = Reflect.getOwnPropertyDescriptor, isArray = Array.isArray;
          const overflow = () => new RangeErrorCtor("Maximum call stack size exceeded");
          const define = (object, name, value) => Object.defineProperty(object, name, { value, writable: true, enumerable: false, configurable: true });
          const named = (fn, name, length) => {
            Object.defineProperty(fn, "name", { value: name, configurable: true });
            Object.defineProperty(fn, "length", { value: length, configurable: true });
            return fn;
          };
          // Nested arrays re-enter join through toString, so a counter bounds the native recursion.
          let depth = 0;
          for (const [name, length] of [["join", 1], ["toLocaleString", 0]]) {
            const original = Array.prototype[name];
            define(Array.prototype, name, named(function (...args) {
              if (depth >= MAX_DEPTH) throw overflow();
              depth++;
              try { return apply(original, this, args); } finally { depth--; }
            }, name, length));
          }
          // Native recursion that does not re-enter script code is measured before it starts.
          const tooDeep = (value, limit, arraysOnly) => {
            if (typeof value !== "object" || value === null) return false;
            const stack = [value, 1];
            const seen = new Set();
            while (stack.length > 0) {
              const level = stack.pop();
              const object = stack.pop();
              if (level > limit) return true;
              if (seen.has(object)) continue;
              seen.add(object);
              const keys = ownKeys(object);
              for (let index = 0; index < keys.length; index++) {
                if (typeof keys[index] !== "string") continue;
                const property = ownDescriptor(object, keys[index]);
                if (property === undefined || !("value" in property)) continue;
                const child = property.value;
                if (typeof child === "object" && child !== null && (!arraysOnly || isArray(child))) stack.push(child, level + 1);
              }
            }
            return false;
          };
          const stringify = JSON.stringify;
          define(JSON, "stringify", named(function (value, replacer, space) {
            if (tooDeep(value, MAX_DEPTH, false)) throw overflow();
            return apply(stringify, JSON, [value, replacer, space]);
          }, "stringify", 3));
          const flat = Array.prototype.flat;
          define(Array.prototype, "flat", named(function (...args) {
            const requested = args.length === 0 || args[0] === undefined ? 1 : Number(args[0]);
            if (requested >= MAX_DEPTH && tooDeep(this, MAX_DEPTH, true)) throw overflow();
            return apply(flat, this, args);
          }, "flat", 0));
        })();
        """;

    public void Settle(CodemodeResultMessage result) => inbox.TryAdd(result);
    public void Complete() => inbox.CompleteAdding();

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

    private sealed class InterruptBudgetException() : Exception("Execution interrupted");

    /// <summary>inline.ts interruptBudget: statements of the whole execution from the moment the script starts (the shims and the
    /// prelude's lockdown are not charged), never reset between host entries. Jint counts statements the same way every time the same
    /// script runs with the same results, so the budget stops a replay where it stopped the run.</summary>
    private sealed class InterruptBudgetConstraint(long budget) : Constraint
    {
        private long used;
        public bool Armed { get; set; }
        public override void Check() { if (Armed && ++used > budget) throw new InterruptBudgetException(); }
        public override void Reset() { }
    }

    /// <summary>Calls the script has issued, tools and globals; reported with every <c>idle</c>.</summary>
    private long issued;
    /// <summary>Deterministic mode: the result being processed, 0 during the script's first run; reported with <c>done</c>.</summary>
    private long current;
    private CodemodeDeterministicClock? clock;

    /// <summary>Runs on the calling thread until <paramref name="stop"/> is cancelled or the inbox completes.</summary>
    public void Run(CancellationToken stop)
    {
        try
        {
            stop.ThrowIfCancellationRequested();
            clock = start.Deterministic is { } deterministic ? new(deterministic) : null;
            var interruptBudget = start.InterruptBudget is { } budget ? new InterruptBudgetConstraint(budget) : null;
            var engine = new Engine(options =>
            {
                options.LimitMemory(start.MemoryLimitBytes);
                options.LimitRecursion(start.RecursionLimit);
                options.CancellationToken(stop);
                options.Constraints.Constraints.Add(new TotalAllocationConstraint(start.TotalAllocationLimitBytes));
                if (interruptBudget is not null) options.Constraints.Constraints.Add(interruptBudget);
                options.Constraints.MaxArraySize = 32u * 1024 * 1024;
                options.Constraints.RegexTimeout = TimeSpan.FromSeconds(5);
                options.Json.MaxParseDepth = MaximumNativeDepth;
                clock?.Configure(options);
            });
            engine.Execute(EngineShim, "codemode-shim.js");
            clock?.Install(engine);
            var bridge = new ClrFunction(engine, "bridge", (_, arguments) => { Bridge(arguments); return JsValue.Undefined; });
            var prelude = engine.Evaluate(CodemodePrelude.Source, "codemode-prelude.js");
            var api = engine.Call(prelude, JsValue.Undefined, [bridge, new JsString(start.ToolsJson), new JsString(start.GlobalsJson), new JsString(start.StoreJson)]);
            var apiObject = api.AsObject();
            var settle = apiObject.Get("settle"); var run = apiObject.Get("run"); var stalled = apiObject.Get("stalled");
            void Drain() { engine.Advanced.ProcessTasks(); engine.Call(stalled, api, []); }
            void Apply(CodemodeResultMessage result)
            {
                engine.Call(settle, api, [new JsNumber(result.Id), result.Ok ? JsBoolean.True : JsBoolean.False,
                    result.Payload is null ? JsValue.Undefined : new JsString(result.Payload),
                    result.Properties is null ? JsValue.Undefined : new JsString(result.Properties)]);
                Drain();
            }
            if (clock is not null) post(new CodemodeStartedMessage(Fingerprint(start)));
            JsValue function;
            // The prefix shares the first line with the script so reported line numbers match the script as written.
            try { function = engine.Evaluate("(async (tools, console) => {" + start.Code + "\n})", "codemode.js"); }
            catch (JavaScriptException error) { post(new CodemodeDoneMessage(false, null, null, DescribeException(error))); return; }
            post(new CodemodeReadyMessage());
            if (interruptBudget is not null) interruptBudget.Armed = true;
            engine.Call(run, api, [function]);
            Drain();
            if (clock is not null) post(new CodemodeIdleMessage(0, issued));
            // vm.ts deliver: deterministic results are applied strictly in seq order; ones that arrive early wait for the gap to fill.
            long nextSeq = 1;
            var early = new Dictionary<long, CodemodeResultMessage>();
            foreach (var result in inbox.GetConsumingEnumerable(stop))
            {
                if (clock is null) { Apply(result); continue; }
                if (result.Seq is not { } seq || seq < nextSeq || early.ContainsKey(seq))
                {
                    post(new CodemodeCrashMessage($"Error: Result {result.Id} has sequence number {(result.Seq is { } given ? given.ToString(CultureInfo.InvariantCulture) : "undefined")}, expected {nextSeq} or later, once"));
                    continue;
                }
                early[seq] = result;
                while (early.Remove(nextSeq, out var next))
                {
                    // Never backwards: a resumed host's clock may be behind the one that started the run.
                    if (next.Now is { } now && double.IsFinite(now)) clock.Now = Math.Max(clock.Now, now);
                    current = nextSeq;
                    Apply(next);
                    post(new CodemodeIdleMessage(nextSeq, issued));
                    nextSeq++;
                }
            }
        }
        catch (Exception error) when (stop.IsCancellationRequested &&
            error is ExecutionCanceledException or OperationCanceledException or InvalidOperationException) { }
        catch (Exception error) { post(Failure(error)); }
    }

    /// <summary>Engine limits Jint enforces outside the script's reach fail the script like an uncaught error.</summary>
    private CodemodeWorkerMessage Failure(Exception error) => error switch
    {
        InterruptBudgetException => new CodemodeExhaustedMessage(start.InterruptBudget ?? 0),
        MemoryLimitExceededException or AllocationLimitException or OutOfMemoryException => Script("InternalError", "out of memory") with { During = During() },
        RecursionDepthOverflowException => Script("RangeError", "Maximum call stack size exceeded") with { During = During() },
        System.Text.RegularExpressions.RegexMatchTimeoutException => Script("InternalError", "regular expression timed out") with { During = During() },
        JavaScriptException script => new CodemodeDoneMessage(false, null, null, DescribeException(script)) { During = During() },
        _ => new CodemodeCrashMessage($"{error.GetType().Name}: {error.Message}")
    };

    /// <summary>vm.ts: the host stops the engine on <c>done</c>, so the <c>idle</c> of this drain never arrives; <c>done</c> carries its numbers.</summary>
    private CodemodeDuring? During() => clock is null ? null : new(current, issued);

    /// <summary>vm.ts DETERMINISTIC_VERSION: bumped whenever this runner, its shims or the deterministic clock change what a
    /// deterministic script observes beyond what the fingerprint's other parts already capture.</summary>
    internal const int DeterministicVersion = 1;

    /// <summary>vm.ts fnv1a: FNV-1a over UTF-16 code units, as 8 hex digits.</summary>
    internal static string Fnv1a(string text)
    {
        var hash = 0x811c9dc5u;
        foreach (var unit in text) { hash ^= unit; hash *= 0x01000193u; }
        return hash.ToString("x8", CultureInfo.InvariantCulture);
    }

    /// <summary>What the engine is: everything a deterministic script observes that could change under a replay. Pi's fingerprint names
    /// QuickJS (<c>quickjs-wasi X, quickjs Y, prelude H, deterministic N, stack S, heap H, budget B</c>); PiSharp's names Jint and its
    /// parser, the prelude (the same FNV-1a as upstream's, over the same source), the Jint shims, this runner's version, the .NET
    /// runtime and platform whose math and globalization Jint uses, and the limits. The two never match, so a run cannot move
    /// between Pi and PiSharp.</summary>
    private static readonly Lazy<string> EngineIdentity = new(() =>
    {
        static string Version(Assembly assembly) => assembly.GetName().Version is { } version ? version.ToString(3) : "unknown";
        var jint = typeof(Engine).Assembly;
        var acornima = jint.GetReferencedAssemblies().FirstOrDefault(name => name.Name == "Acornima")?.Version?.ToString(3) ?? "unknown";
        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "macos" : "other";
        var arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        return $"jint {Version(jint)}, acornima {acornima}, prelude {Fnv1a(CodemodePrelude.Source)}, shim {Fnv1a(EngineShim + "\n" + CodemodeDeterministicClock.Shim)}, " +
            $"deterministic {DeterministicVersion}, dotnet {Environment.Version}, os {os}-{arch}";
    });

    internal static string Fingerprint(CodemodeStartMessage start) =>
        $"{EngineIdentity.Value}, recursion {start.RecursionLimit}, heap {start.MemoryLimitBytes}, total {start.TotalAllocationLimitBytes}, " +
        $"budget {(start.InterruptBudget is { } budget ? budget.ToString(CultureInfo.InvariantCulture) : "none")}";

    private static CodemodeDoneMessage Script(string name, string message) =>
        new(false, null, null, JsonSerializer.Serialize(new { name, message, stack = $"{name}: {message}" }));

    internal static string DescribeException(JavaScriptException error)
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
                issued++;
                post(new CodemodeCallMessage((long)TypeConverter.ToNumber(At(1)), kind == "call", At(2).ToString(), Text(At(3))));
                break;
            case "output":
                var type = At(1).ToString();
                post(new CodemodeOutputMessage(type == "image" ? new CodemodeImageOutput(At(2).ToString(), At(3).ToString())
                    : new CodemodeTextOutput(At(2).ToString(), type == "console")));
                break;
            case "done":
                post(TypeConverter.ToBoolean(At(1)) ? new CodemodeDoneMessage(true, Text(At(2)), At(3).ToString(), null) { During = During() }
                    : new CodemodeDoneMessage(false, null, null, At(2).ToString()) { During = During() });
                break;
        }
    }
}
