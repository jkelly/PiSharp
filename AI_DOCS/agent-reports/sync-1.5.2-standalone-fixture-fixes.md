# Standalone MCP / durable-message fixture fixes

Follow-up to validator receipts `sync152-mcptuioriginalqualification-stable-r2-a882388e9b8b49189aa12cfb7af206b9` and the DurableMessages 5/8 run. These fixes change authored test fixtures only; no production admission or artifact bound changed. Root explicitly authorized the isolated fixture/project wiring. No build or test was run by this worker.

## MCP dynamic-roots admission

The failing control passed a newly allocated but connection-identical `McpServerEntry` and expected rejection. `AdmittedMcpHttpChannelFactory` intentionally compares `McpConfigurationReader.SameConnection` so `/mcp` presentation-only republishes retain their admitted transport. This was a stale identity assertion, not unauthorized endpoint acquisition.

`tests/PiSharp.CodingAgent.Tests/McpDynamicRootsHttpBindingTests.cs` now admits and explicitly closes the equivalent clone, then submits an entry with genuinely different `Scope = Project` and expects synchronous refusal before any roots/HTTP effect. Static/dynamic ambiguity and multicast checks remain.

## Native initializer fixture

All three initializer controls failed before loading because they copied every application-output DLL into a published plugin package. The measured copied DLL total was 32,087,152 bytes; native preflight has an unchanged 16,777,216-byte aggregate bound. The larger current CLI closure exposed this fragile fixture design. Raising the production cap or skipping preflight would test the wrong behavior.

Added `tests/fixtures/extensions/native/PublishedFixture.InitializerContext`, an authored library with only shared `Extensions.Abstractions` / `Contracts` references and generated deps/runtimeconfig. The existing public fixture entry's binder, command/context reads, stale-callback refusal, and disposal assertions were moved unchanged into it. The linked `NativeInitializerLoaderTests` now packages exactly that DLL and its two metadata files. Both CodingAgent.Tests and DurableMessages.Tests reference the fixture and copy its metadata into their output; no existing historical fixture, solution, or publisher helper was changed.

## Verification / handoff

All six changed/new test/project files were checked for LF-only content. Scoped `git diff --check` passes. Validator was notified before fixture edits and again at source-ready. Run the same two standalone executables through the receipt runner with `-TestAssembly PiSharp.CodingAgent.Tests.dll`. Their project restores must generate the new fixture lock and update the two consumer project locks; this worker did not invoke restore/build.
