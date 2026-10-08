# MCP resource original callback evidence successor R297

This separate successor preserves frozen R296 commit `8b8c207207f7b6747f489dd649eb64c5df51b563` and its artifacts. It addresses static findings R296-CALLBACK-03 and R296-CALLBACK-04 from `R296_LEAF_ORIGINAL_CALLBACK_STATIC_REVIEW.json`, SHA256 `4d58b123db90ba78e5842e550c9695cff755d72665c725d6a6a74e6eb69a093a`. No applicable AGENTS.md/.agents instructions were present in this owned clone or workspace root.

The output saver must have exactly one invocation entry at construction. Every captured request delegate is validated before any server request starts, including aggregate listing. Multicast callbacks are rejected rather than invoking subscribers whose earlier return values would be discarded.

The leaf converts each admitted ValueTask to its Task exactly once and awaits that retained original. On failure, McpResourceCallbackException exposes that Task and preserves its complete Task.Exception aggregate as InnerException. Synchronous callback throws have no Task original and are retained as the inner evidence. Faulted Tasks carrying OperationCanceledException stay faulted through the non-cancellation wrapper. Genuine operation cancellation requires an actually canceled original, the supplied operation token requested, and the awaited cancellation exception carrying that exact token. Canceled originals with unrelated tokens are retained as faults.

Aggregate listing errors and output-save notices remain human-readable. Returned McpResourceResult.OriginalCallbackFailures exposes complete original evidence to the native owning caller; ToToolResult deliberately omits it from model/script payloads. If owner cancellation or another later failure follows already collected callback faults, ExecuteAsync throws an AggregateException containing both callback evidence and the later error. It does not substitute cancellation for already settled callback faults. AsyncLocal evidence is scoped to one ExecuteAsync operation and shared only by its aggregate server branches; a lock protects branch collection. The existing output callback reentry guard remains active.

Eight additional synthetic groups supplement the original eight: admission refusal before effects; held selected request multifaults; aggregate-list native evidence; held binary and truncated-text saver multifaults; faulted request OCE with cleanup fault; faulted saver OCE with cleanup fault; request/saver original cancellation token provenance; owner cancellation races with held request/saver multifault originals. Controls assert held completion, exact original task association, exact nested exception object identities, and canceled versus faulted Task state. All 16 groups remain unregistered and unexecuted.

Only source inspection and git whitespace checking were performed. No build, tests, original runtime, Node, native process, network, credentials, package/project/lock changes, trust/security changes, shared Program/registry/host edits or formal qualification were performed. R296 host integration gaps and native ordering/base64 policy differences remain open. This successor requires independent immutable source review and producer-specific qualification before any parity or acceptance claim.

Static type/import and project mapping was inspected without compilation:

| Source dependency | Exact existing source or SDK inclusion |
|---|---|
| JsonData.Parse, Value, ToString | src/PiSharp.Contracts/Json/JsonData.cs, namespace PiSharp.Contracts; Abstractions directly references Contracts |
| IExtensionToolInvocationContext and inherited identity/tokens | src/PiSharp.Extensions.Abstractions/ToolInvocationContracts.cs:6 and RegistrationContracts.cs:40; runtime explicitly imports PiSharp.Extensions |
| McpInvocationIdentity, McpRequestOptions, McpRuntimeProtocolException | src/PiSharp.Extensions.Abstractions/Mcp/Runtime/McpRuntimeContracts.cs:9,12,68, namespace PiSharp.Extensions.Mcp.Runtime |
| McpResourceServer, Request, Saver, Result, CallbackException | owned Abstractions/Mcp/Resources/McpResourceContracts.cs; runtime and fixture explicitly import PiSharp.Extensions.Mcp.Resources |
| ImmutableArray, LINQ, Task, ValueTask, IOException, CancellationToken, UTF8/runes and JsonElement | explicit System.Collections.Immutable, System.Text, System.Text.Json imports and root ImplicitUsings; BCL types under net10.0 |
| Owned runtime/test files | Runtime project directly references Abstractions; ContractTests directly references Runtime. All are Microsoft.NET.Sdk projects with default source globs; only test Consumer/**/*.cs is removed, so McpResourceTests.cs remains included |
| Compiler context | Directory.Build.props supplies net10.0, Nullable enable, ImplicitUsings enable, TreatWarningsAsErrors true; global.json pins SDK10.0.401 with rollForward disabled. Root Directory.Build.targets is an empty search fence; no nested Directory.Build files were found in the three inspected projects |

Existing fixture Context implements every required member in these pinned contracts. The new evidence uses ValueTask<T>.AsTask, Task.IsCanceled/Exception, and ImmutableArray<McpResourceCallbackException>; no new package, external namespace alias or project reference is needed. This static association does not establish compiler or runtime success.
