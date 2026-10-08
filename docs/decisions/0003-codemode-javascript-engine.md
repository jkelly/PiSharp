# Codemode JavaScript engine

Status: approved by the project owner on 2026-10-08. This closes the open engine selection in [profile-platform-matrix.md](../compatibility/profile-platform-matrix.md).

## Context

Upstream codemode runs model-written JavaScript as the body of an async function. It uses QuickJS compiled to WebAssembly (`quickjs-wasi` 3.6.2), with one Node worker thread per script. The profile matrix requires codemode to stay in the native profile, with JavaScript semantics, and without routing required execution through the optional Node bridge.

## Decision

Codemode scripts run in **Jint**, a managed JavaScript interpreter for .NET (BSD-2-Clause), inside a new `PiSharp.Codemode` project. Jint and its parser dependency become exact-pinned NuGet dependencies of that project only. The rest of the native libraries stay package-free.

Each script gets a fresh Jint `Engine` with these limits:
- a 256 MB memory limit;
- a recursion limit;
- the script's `timeout_ms` deadline;
- the caller's cancellation token.

Only the host bridge function is exposed; CLR interop is disabled. The upstream prelude JavaScript is reused unchanged, with MIT attribution, so lockdown, the `tools` proxy, output limits and settlement behave as upstream does.

## Consequences

- Jint is not QuickJS. Behavioural differences that matter to scripts are found by a conformance suite ported from upstream `codemode/test/sandbox.test.ts` and recorded as explicit compatibility entries. They are never silently accepted.
- Jint and its dependencies need license notices, an entry in the license evidence matrix, locked restore entries and packaging validation before release.
- No native binaries are added, so the dotnet tool and self-contained builds stay identical on every platform.
