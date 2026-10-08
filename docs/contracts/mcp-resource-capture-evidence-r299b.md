# MCP synchronous capture evidence correction R299b

A synchronous server capture callback has no canceled Task original. Dispatch now wraps every callback exception, including OCE, with McpResourceCallbackException before the async leaf can infer canceled Task state. Validation remains outside the callback wrapper. Terminal dispatch evidence retains the wrapper and exact synchronous exception (Original null). Actual canceled leaf tasks still follow the leaf physical-original/token provenance rules.

One additional authored prepared-execution control throws synchronous capture OCE with all native invocation tokens live, requires an error result, no channel effects, and transferred evidence with the identical exception and null original. This is separate from the previously authored faulted-channel-OCE control. Unregistered and unexecuted.

No new imports or external APIs. Existing net10 default SDK globs/references audited as in the R299 handoff. IReadOnlyList, Func, Task and cancellation members use existing imports/implicit usings; McpResourceCallbackException uses the existing resources namespace. No async ref structs. Source diff whitespace checked; no compiler or execution.
