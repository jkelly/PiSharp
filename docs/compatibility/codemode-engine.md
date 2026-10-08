# Codemode engine: Jint versus QuickJS

Pi v1.1.0 runs codemode scripts in QuickJS compiled to WebAssembly (`quickjs-wasi` 3.6.2), one Node worker thread per
script. PiSharp runs them in [Jint](https://github.com/sebastienros/jint) **4.16.3** (BSD-2-Clause, exact pin), with its
parser [Acornima](https://github.com/adams85/acornima) **1.7.0** (BSD-3-Clause, transitive), as
[decision 0003](../decisions/0003-codemode-javascript-engine.md) chose. Jint 4.16.3 was the newest stable release at least
two weeks old on 2026-10-08 (published 2026-09-19; 4.16.4 and 4.17.0 were younger).

The host side lives in `src/PiSharp.Codemode/Runtime/CodemodeSandbox.cs`, the engine in `CodemodeEngineRunner.cs` and the
worker processes in `CodemodeWorkers.cs`. Conformance cases are in `tests/PiSharp.Codemode.Tests` (`prelude.*`, `sandbox.*`,
`limits.*`, `isolation.*`).

## Isolation: one worker process per script

Pi's guarantee is that a script can never take pi down: the QuickJS VM is a separate wasm instance in a worker thread, its
stack is guarded, and a worker that dies is reported as `Worker exited with code N before the script settled`.

Jint runs on the .NET stack, and .NET cannot recover from a stack overflow. Some built-ins recurse natively on the depth of
a value (for example a property lookup along a prototype chain a million objects long), and no in-process guard covers all of
them. PiSharp therefore runs each script's engine in a **child process** of its own executable (`pisharp --codemode-worker`),
speaking upstream's protocol (start, call, output, done, crash, result) as JSON lines over standard input and output:

- A crash ends only the worker. The script fails as a `sandbox` error, `Worker exited with code -1073741571 before the
  script settled`, exactly as upstream reports a dying worker, and the host keeps running.
- Deadlines, aborts and finished scripts kill the worker process (upstream: `worker.terminate()`); no worker outlives its
  script. One spare worker is started ahead and warmed (engine compiled) so the next script starts quickly. A worker exits
  when the host closes its standard input, so spares never outlive the host.
- Cost: each script still pays a process start (about 100 to 300 ms with the spare, against a few ms for a Node worker).
- Hosts set `CodemodeWorker.Default` (the CLI does at startup). An SDK host that sets nothing runs engines on a thread of
  its own process, which the deep-nesting cases below can crash.

## What is the same

- **The prelude.** `Runtime/codemode-prelude.js` is upstream's `PRELUDE_SOURCE` evaluated (its limit constants substituted),
  byte for byte below one attribution line. The loader checks its SHA-256
  (`224cd74082a03a57105af78e1fe205bda692f096252ff4202121016dd78f6228`) before every use. Lockdown, the `tools` proxy with
  close-match errors, `ALL_TOOLS`, `text()`, `image()` validation, `exit()`, `console.*`, `store()`/`load()` with their
  256 KiB per value and 1 MiB total limits, and the 16 Mi character / 100,000 item output limits therefore behave as
  upstream. A script that catches the output-limit error cannot keep printing: the prelude reports the failure first and
  the host ends the engine.
- **One engine per script**, in its own worker. Nothing is shared between executions.
- **The bridge.** Only one host function, `bridge(kind, a, b, c)`, is exposed, and only to the prelude's closure. Values
  cross as JSON strings. Jint's CLR interop is never enabled (`AllowClr` is not called), so scripts cannot reach .NET types.
- **No host globals.** There are no timers, `fetch`, `process`, `require`, modules or `WebAssembly`. `eval` and `Function`
  work and stay inside the engine. A script waiting on a promise nothing can settle fails with upstream's "can never
  settle" error.
- **Settlement.** Done, timeout, abort, worker exit and engine failure race as in `host.ts`: the first one wins, pending tool
  calls are cancelled and recorded as `cancelled`, and the result resolves once the worker is gone. Malformed worker payloads
  fail the run as `Sandbox bridge broken: …`.
- **Nested calls** have no limit of their own, as upstream (the shared invoker's 128-call and depth-8 limits do not apply
  to codemode trees); cancellation, deadlines and the session lifetime still end them.

## Limits

| Limit | QuickJS (Pi) | Jint (PiSharp) |
| --- | --- | --- |
| Native nesting depth | Stack guard: a catchable `RangeError` | A shim evaluated before the prelude makes Array `join`/`toString`/`toLocaleString`, `flat` and `JSON.stringify` throw a catchable `RangeError: Maximum call stack size exceeded` past 1000 levels; `JSON.parse` stops at 1000 levels with a catchable `SyntaxError`. The bound is low because unwinding an exception through Jint costs memory that grows with the square of its depth (a throw about 3000 calls deep exceeds the 256 MiB budget). Other native recursion (prototype or proxy chains) can exhaust the worker's 256 MiB stack: the worker exits and the script fails as a sandbox error. |
| Memory | 256 MiB heap (`memoryLimit`); overruns throw a catchable `InternalError: out of memory` | Jint's `LimitMemory(256 MiB)` counts bytes **allocated** by the engine thread between two host entries (the start of the script and every settled tool call), plus a 1 GiB budget for the whole execution. Overruns fail the script with `InternalError: out of memory`; Jint does not let the script catch it. |
| Recursion of script functions | Stack guard; overflow is a catchable `RangeError` | `LimitRecursion(10000)`. Overflow fails the script with `RangeError: Maximum call stack size exceeded`; Jint does not let the script catch it. |
| Deadline | `timeout_ms` (none by default for the tool); the worker is terminated | `timeout_ms` kills the worker process. |
| Caller cancellation | `AbortSignal`; the worker is terminated | The caller's `CancellationToken` kills the worker process. |
| Regular expressions | Interruptible | Jint's regex timeout of 5 s inside the worker; deadlines and aborts kill the worker regardless. |
| Arrays | Heap limit | `MaxArraySize` of 32 Mi elements in addition to the memory budget. |

## Differences scripts can observe

- **Uncatchable engine limits.** Memory and script-recursion overruns end the script instead of throwing inside it. Jint
  raises them outside the script's reach; there is no option to make them catchable.
- **Deep throws.** Unwinding an exception through Jint costs memory that grows with the square of the call depth, so a
  script that throws about 3000 calls deep fails with `InternalError: out of memory` instead of reaching its `catch`.
- **Allocation accounting.** The memory budget counts allocations, not the live heap, so a script that creates a lot of
  short-lived garbage in one uninterrupted run (several hundred MiB without awaiting a tool) can hit it although QuickJS
  would collect the garbage. Awaiting a tool starts a new run; the 1 GiB execution budget still bounds the total.
- **Error messages of the engine.** Messages produced by the engine itself differ: for example `JSON.stringify` of a
  circular structure throws `TypeError: Cyclic reference detected.` (V8: "Converting circular structure to JSON"), and
  syntax errors read `Unexpected token '=' (codemode.js:2:10)`. Messages written by the prelude, the tools and the host are
  upstream's. Stack frames read `    at f (codemode.js:2:7)`; the line numbers match the script as written, as upstream.
- **Shimmed built-ins.** The guarded built-ins are wrappers: their `name` and `length` are the originals', but
  `Function.prototype.toString` shows their source.
- **Stack line endings.** Jint joins stack frames with the platform newline. The shim makes `Error.prototype.stack` use
  `\n`, as QuickJS does.
- **Blocking `Atomics`.** `Atomics.wait` and `Atomics.waitAsync` would block the engine past cancellation; the shim removes
  them. QuickJS's worker never blocks there.
- **Extra built-ins.** Jint 4.16 has `Temporal`, `ShadowRealm`, `Iterator` helpers, `DisposableStack` and other recent
  built-ins QuickJS may lack. They are frozen by the lockdown like every other built-in and reach nothing outside the engine.
- **`JSON.parse` messages in `// @options:`.** An invalid options line reports .NET's JSON error text after
  "@options must be valid JSON with supported fields `max_output_tokens` and `timeout_ms`: ".
- **Tool calls start in order.** As in `host.ts`, each call's implementation starts in message order.

## Session integration

- `codemode` is registered with every CLI session, inactive, as upstream registers it: `--tools`, `--exclude-tools` and
  `defaultTools` select it, also with `--no-mcp`. Codemode MCP servers activate it unless `autoEnableCodemode` is false.
- `scriptNeedsServer`: upstream makes a script wait for the servers it names or searches. PiSharp publishes a server's tools
  between runs, so a prompt admitted while codemode is active waits until every codemode server has connected (or failed);
  later prompts do not wait.

## Not ported

- `wasm.ts` and the `workerUrl`/`wasm` options configure QuickJS and Node workers; PiSharp's equivalent is
  `CodemodeWorkerLauncher`.
