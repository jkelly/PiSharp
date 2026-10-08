// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/codemode/src/runtime/worker.ts. One script in a fresh Jint engine:
// the prelude, the script, then settled tool results until the host ends it.
using System.Collections.Concurrent;
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

    /// <summary>Runs on the calling thread until <paramref name="stop"/> is cancelled or the inbox completes.</summary>
    public void Run(CancellationToken stop)
    {
        try
        {
            stop.ThrowIfCancellationRequested();
            var engine = new Engine(options =>
            {
                options.LimitMemory(start.MemoryLimitBytes);
                options.LimitRecursion(start.RecursionLimit);
                options.CancellationToken(stop);
                options.Constraints.Constraints.Add(new TotalAllocationConstraint(start.TotalAllocationLimitBytes));
                options.Constraints.MaxArraySize = 32u * 1024 * 1024;
                options.Constraints.RegexTimeout = TimeSpan.FromSeconds(5);
                options.Json.MaxParseDepth = MaximumNativeDepth;
            });
            engine.Execute(EngineShim, "codemode-shim.js");
            var bridge = new ClrFunction(engine, "bridge", (_, arguments) => { Bridge(arguments); return JsValue.Undefined; });
            var prelude = engine.Evaluate(CodemodePrelude.Source, "codemode-prelude.js");
            var api = engine.Call(prelude, JsValue.Undefined, [bridge, new JsString(start.ToolsJson), new JsString(start.GlobalsJson), new JsString(start.StoreJson)]);
            var apiObject = api.AsObject();
            var settle = apiObject.Get("settle"); var run = apiObject.Get("run"); var stalled = apiObject.Get("stalled");
            void Drain() { engine.Advanced.ProcessTasks(); engine.Call(stalled, api, []); }
            JsValue function;
            // The prefix shares the first line with the script so reported line numbers match the script as written.
            try { function = engine.Evaluate("(async (tools, console) => {" + start.Code + "\n})", "codemode.js"); }
            catch (JavaScriptException error) { post(new CodemodeDoneMessage(false, null, null, DescribeException(error))); return; }
            engine.Call(run, api, [function]);
            Drain();
            foreach (var result in inbox.GetConsumingEnumerable(stop))
            {
                engine.Call(settle, api, [new JsNumber(result.Id), result.Ok ? JsBoolean.True : JsBoolean.False,
                    result.Payload is null ? JsValue.Undefined : new JsString(result.Payload)]);
                Drain();
            }
        }
        catch (Exception error) when (stop.IsCancellationRequested &&
            error is ExecutionCanceledException or OperationCanceledException or InvalidOperationException) { }
        catch (Exception error) { post(Failure(error)); }
    }

    /// <summary>Engine limits Jint enforces outside the script's reach fail the script like an uncaught error.</summary>
    private static CodemodeWorkerMessage Failure(Exception error) => error switch
    {
        MemoryLimitExceededException or AllocationLimitException or OutOfMemoryException => Script("InternalError", "out of memory"),
        RecursionDepthOverflowException => Script("RangeError", "Maximum call stack size exceeded"),
        System.Text.RegularExpressions.RegexMatchTimeoutException => Script("InternalError", "regular expression timed out"),
        JavaScriptException script => new CodemodeDoneMessage(false, null, null, DescribeException(script)),
        _ => new CodemodeCrashMessage($"{error.GetType().Name}: {error.Message}")
    };

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
                post(new CodemodeCallMessage((long)TypeConverter.ToNumber(At(1)), kind == "call", At(2).ToString(), Text(At(3))));
                break;
            case "output":
                var type = At(1).ToString();
                post(new CodemodeOutputMessage(type == "image" ? new CodemodeImageOutput(At(2).ToString(), At(3).ToString())
                    : new CodemodeTextOutput(At(2).ToString(), type == "console")));
                break;
            case "done":
                post(TypeConverter.ToBoolean(At(1)) ? new CodemodeDoneMessage(true, Text(At(2)), At(3).ToString(), null)
                    : new CodemodeDoneMessage(false, null, null, At(2).ToString()));
                break;
        }
    }
}
