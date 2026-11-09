# CI3 MCP/codemode fixture corrections and late hosted evidence

Status: source frozen for main validation. Requested model: gpt-6.1-sol high, Ultrafast; effective tier remains unverified. No .NET execution, workspace Git mutations or remote writes. Only the three assigned test files and this report/checkpoint were written.

## Actual failures and proof

Both failures were observed at main commit `3024b1e09e1add83e8cfeeb73bc4235a026b517c` in completed macOS run 42544805821:

| Job | Suite/case | Actual error | Disposition |
|---|---|---|---|
|128927362952|McpParity, `manager.disable-enable-reconnect-and-exposure`|Fourth request has no raw tools after reconnect; sequence is direct → withdrawn → direct → empty → deferred tool_search.|Fixture wrongly treats omitted unchanged continuation declarations as withdrawal.|
|128927363079|Codemode, `session.codemode-server-connects-and-scripts-call-its-tools`|`Sequence contains no matching element` at `SessionCases.cs:209`, selecting codemode from request 1's raw tools.|Fixture wrongly inspects raw declarations after the preceding completed unchanged request.|

Raw uploaded JSON reports are in `artifacts/release-prep/macos-42544805821/report-PiSharp.{McpParity,Codemode}.Tests/`. The run reports 47 MCP and 142 Codemode groups, one failure in each; those counts remain unchanged by this correction.

Product proof: `src/PiSharp.AI/Protocols/AnthropicMessages/AnthropicMessageThreads.cs:33-47` permits continuation only when the extended transcript's prefix plus settings match the previous hash. `Prepare` removes `system` and `tools`, keeps only the new messages and emits `thread.type=continue` with the completed response ID. Hashing at lines 82-87 includes the remaining full request settings, including tools/system. This behavior is the intentionally implemented F08 contract, documented in `AI_DOCS/agent-reports/I02.md`; it is not an MCP deregistration or missing codemode implementation. No pinned upstream golden was recaptured or altered.

## Correction and controls

- `tests/PiSharp.McpParity.Tests/Harness.cs`: optional fake text-response ID; an explicit effective tool view inherits only from the immediately preceding response actually returned by this fixture. Full/create requests reset to their actual raw declarations.
- `tests/PiSharp.McpParity.Tests/ManagerCases.cs`: its five actual prompts receive distinct scripted response IDs. Existing disable/enable/reconnect/deferred assertions now inspect that effective view. Initial raw docs publication, raw disabled withdrawal and raw deferred tool_search publication remain explicit. Original saved-config, status, connection count and successful RPC completion assertions remain intact.
- `tests/PiSharp.Codemode.Tests/SessionCases.cs`: its failing case receives three distinct scripted response IDs. The exact declaration description/schema is checked through the effective view; initial codemode raw declaration is additionally required. Direct MCP tools remain absent from effective codemode-only declarations. Original nested script output, request count, MCP call arguments, parent/call identity, persisted call rows and joined server close assertions remain.
- Both cases embed refusal controls: a continuation before any completed parent fails; an unknown response ID fails; a fresh create cannot retain old declarations; a continuation cannot silently carry changed tools. They check one fake completed response per actual request. Missing initial declarations cannot pass through inheritance.

Helpers affect only these two cases; other cases retain their existing raw request helpers. Existing default fake response IDs remain unchanged elsewhere. No runtime change was needed or made.

## Verification / checkpoint

Read-only static checks confirmed existing partial-class helper signatures, implicit framework usings, unique fixed scripted IDs, and valid complete added JSON literals. Scoped `git diff --check` passed. No compile/runtime pass is claimed. SHA256 checkpoint: `artifacts/release-prep/CI3/checkpoint.json`.

Main focused commands after its serialized build:

- McpParity DLL: `--only manager.disable-enable-reconnect-and-exposure`
- Codemode DLL: `--only session.codemode-server-connects-and-scripts-call-its-tools`
- Then each complete DLL with `--report <fresh path>`. Expected complete case counts remain 47 and 142.

## Remaining completed macOS release failures

The complete macOS run 42544805821 failed six suites. Full exact case IDs/stacks are preserved in `artifacts/release-prep/macos-late-triage.json` and original reports; no exit-code-only success inference is used.

|Job|Suite|Failures|Current owner / evidence|
|---|---|---:|---|
|128927362825|CliSync|3|Already covered by CLI1: catalog, beta expectation, MCP continuation.|
|128927363122|CliParity|14/220|CLI2: continuation history/tool replay assertions, reviewed opus fallback, new thinking checkpoint/session-format expectations.|
|128927363056|InteractiveParity|1/723|CLI2: additional resumed clamped thinking checkpoint.|
|128927363042|ExtensionParity|6/151|EXT2: raw escaped codemode description; unsupported custom built-in operations; unchanged modules after reload; large follow-up assertion; compatibility warning.|
|128927362952|McpParity|1/47|CI3 correction above.|
|128927363079|Codemode|1/142|CI3 correction above.|

The custom-operations tool result explicitly says `Built-in tools with custom operations are not available in the PiSharp Node bridge`, thrown at `src/PiSharp.Cli/Extensions/Pi/PiExtensionHost.Requests.cs:916`. Reload reports explicitly retain v1 handlers after v2 source rewrite and docs instead of wiki. These were handed to root/EXT2 as potential runtime issues; CI3 did not relabel them as harmless continuation differences. The tool-description raw JSON contains the correctly escaped `Promise<{ count: number; }>` text; parse its JSON string before comparing syntax.

## Durable final observation

Run 42544805823 is now complete with failure; Windows finished normally and did not hang. All three OS reports are downloaded:

- Linux: `artifacts/release-prep/durable-late-42544805823/`
- macOS: `artifacts/release-prep/durable-macos-42544805823/`
- Windows: `artifacts/release-prep/durable-windows-42544805823/`

Windows normal/retention0 Durable each have 4,659 cases, one M2 raw escaping failure and one explicit platform skip. Unix normal/retention0 each have 4,665 cases, one M2 failure and two explicit skips. ModelRuntime on all OS has 14 cases/one reviewed-only qwen catalog failure (CLI1). WebAgent on all OS has 539 cases/one Codex strict inline-tool failure (RUN1). macOS Chord additionally has the three known long pipe path failures (HOST1); Windows/Linux Chord 875 pass. Windows passing actual cases: DurableCli 119, Hosting 129, Server 31, Web 17. No additional Durable case failure was found beyond those assigned fixes. Fresh exact-candidate hosted runs are still required.
