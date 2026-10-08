# Codemode engine: Jint versus QuickJS

Pi v1.1.0 runs codemode scripts in QuickJS compiled to WebAssembly (`quickjs-wasi` 3.6.2), one Node worker thread per
script. PiSharp runs them in [Jint](https://github.com/sebastienros/jint) **4.16.3** (BSD-2-Clause, exact pin), with its
parser [Acornima](https://github.com/adams85/acornima) **1.7.0** (BSD-3-Clause, transitive), as
[decision 0003](../decisions/0003-codemode-javascript-engine.md) chose. Jint 4.16.3 was the newest stable release at least
two weeks old on 2026-10-08 (published 2026-09-19; 4.16.4 and 4.17.0 were younger).

The engine lives in `src/PiSharp.Codemode/Runtime/CodemodeSandbox.cs`. Its conformance cases are in
`tests/PiSharp.Codemode.Tests` (`prelude.*`, `sandbox.*`, `limits.*`).

## What is the same

- **The prelude.** `Runtime/codemode-prelude.js` is upstream's `PRELUDE_SOURCE` evaluated (its limit constants substituted),
  byte for byte below one attribution line. The loader checks its SHA-256
  (`224cd74082a03a57105af78e1fe205bda692f096252ff4202121016dd78f6228`) before every use. Lockdown, the `tools` proxy with
  close-match errors, `ALL_TOOLS`, `text()`, `image()` validation, `exit()`, `console.*`, `store()`/`load()` with their
  256 KiB per value and 1 MiB total limits, and the 16 Mi character / 100,000 item output limits therefore behave as
  upstream. A script that catches the output-limit error cannot keep printing: the prelude reports the failure first and
  the host ends the engine.
- **One engine per script.** Each execution creates a fresh `Engine` on its own thread. Nothing is shared between
  executions; parallel executions do not see each other's globals.
- **The bridge.** Only one host function, `bridge(kind, a, b, c)`, is exposed, and only to the prelude's closure. Values
  cross as JSON strings. Jint's CLR interop is never enabled (`AllowClr` is not called), so scripts cannot reach .NET types.
- **No host globals.** There are no timers, `fetch`, `process`, `require`, modules or `WebAssembly`. `eval` and `Function`
  work and stay inside the engine. A script waiting on a promise nothing can settle fails with upstream's "can never
  settle" error.
- **Settlement.** Done, timeout, abort and engine failure race as in `host.ts`: the first one wins, pending tool calls are
  cancelled and recorded as `cancelled`, and the result resolves once the engine thread has exited.

## Limits

| Limit | QuickJS (Pi) | Jint (PiSharp) |
| --- | --- | --- |
| Memory | 256 MiB heap (`memoryLimit`); overruns throw a catchable `InternalError: out of memory` | Jint's `LimitMemory(256 MiB)` counts bytes **allocated** by the engine thread between two host entries (a host entry is the start of the script and every settled tool call), plus a 1 GiB budget for the whole execution. Overruns fail the script with `InternalError: out of memory`; the script cannot catch it. |
| Recursion | Stack guard (`MAX_STACK_SIZE`); overflow is a catchable `RangeError` | `LimitRecursion(10000)` on a thread with a 1 GiB stack reservation (64 MiB in 32-bit processes). Overflow fails the script with `RangeError: Maximum call stack size exceeded`; the script cannot catch it. |
| Deadline | `timeout_ms` (none by default for the tool); the worker is terminated | `timeout_ms` cancels the engine through Jint's cancellation constraint, checked between statements, including in loops that catch exceptions and in microtask loops. |
| Caller cancellation | `AbortSignal`; the worker is terminated | The caller's `CancellationToken` cancels the engine the same way. |
| Regular expressions | Interruptible | Jint's regex timeout of 5 s; a backtracking expression delays cancellation by at most that long and then fails the script with `InternalError: regular expression timed out`. |
| Arrays | Heap limit | `MaxArraySize` of 32 Mi elements in addition to the memory budget. |

## Differences scripts can observe

- **Uncatchable engine limits.** Memory and recursion overruns end the script instead of throwing inside it (see above).
  Upstream's `agent-session-codemode.test.ts` "limits script memory" and `sandbox.test.ts` "turns deep recursion into a
  catchable RangeError" are ported with this difference.
- **Allocation accounting.** The memory budget counts allocations, not the live heap, so a script that creates a lot of
  short-lived garbage in one uninterrupted run (several hundred MiB without awaiting a tool) can hit it although QuickJS
  would collect the garbage. Awaiting a tool starts a new run; the 1 GiB execution budget still bounds the total.
- **Native recursion.** Jint does not guard native recursion such as `String()` or `JSON.stringify` on a structure nested
  hundreds of thousands of levels deep; such a structure (which must fit the allocation budgets) can exhaust the engine
  thread's stack, which ends the PiSharp process. QuickJS turns this into a `RangeError`. Process isolation for codemode
  would remove this; it is listed as an open owner decision.
- **Error messages of the engine.** Messages produced by the engine itself differ: for example `JSON.stringify` of a
  circular structure throws `TypeError: Cyclic reference detected.` (V8: "Converting circular structure to JSON"), and
  syntax errors read `Unexpected token '=' (codemode.js:2:10)`. Messages written by the prelude, the tools and the host are
  upstream's. Stack frames read `    at f (codemode.js:2:7)`; the line numbers match the script as written, as upstream.
- **Stack line endings.** Jint joins stack frames with the platform newline. A shim evaluated before the prelude (so the
  lockdown freezes it) makes `Error.prototype.stack` use `\n`, as QuickJS does.
- **Blocking `Atomics`.** `Atomics.wait` and `Atomics.waitAsync` would block the engine thread past cancellation; the shim
  removes them. QuickJS's worker never blocks there.
- **Extra built-ins.** Jint 4.16 has `Temporal`, `ShadowRealm`, `Iterator` helpers, `DisposableStack` and other recent
  built-ins QuickJS may lack. They are frozen by the lockdown like every other built-in and reach nothing outside the engine.
- **`JSON.parse` messages in `// @options:`.** An invalid options line reports .NET's JSON error text after
  "@options must be valid JSON with supported fields `max_output_tokens` and `timeout_ms`: ".
- **Tool calls start in order.** As in `host.ts`, each call's implementation runs on the engine thread until its first
  `await`, so call rows appear in the order the script made the calls.

## Not ported

- `wasm.ts` and the worker entry (`worker.ts` as a file, `workerUrl`, `wasm` options): they configure QuickJS and Node
  workers, which PiSharp does not use. The sandbox-error cases for a missing worker file or a failing wasm module have no
  equivalent.
- The `raw-worker.ts` bridge-corruption fixture: the Jint bridge is called by the unchanged prelude only, and its decoding
  errors (`Sandbox bridge broken: ...`) are implemented as in `host.ts` but have no fixture that sends hand-made payloads.
