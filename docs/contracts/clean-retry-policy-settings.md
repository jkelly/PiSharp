# Clean retry policy and settings projection

Source-only independent reimplementation from pinned Pi v0.99.1 commit
`d86654abb8862e201933517d6f1fce9f88dd117f`, specifically
`packages/ai/src/utils/retry.ts` and
`packages/coding-agent/src/core/settings-manager.ts` (migration and retry getters).
No quarantined retry implementation, patch or composition was read or reused.

`AgentRetryPolicy` captures enabled (true), maximum retry attempts (3), base delay
(2000ms), and agent delay cap (60000ms). The initial provider request consumes no
retry attempt. `DelayMs` matches binary64 exponential multiplication followed by
safe-integer saturation and the agent cap. The classifier requires an error stop
reason and a nonempty error string; permanent billing/quota patterns take priority
over transient provider/network patterns. It implements the pinned non-Unicode
JavaScript case canonicalization and excludes all four JavaScript line terminators
from wildcard separators. The driver must handle context overflow before using it.

`RetrySettingsProjection.NormalizeLayer` returns owned JSON and never modifies its
input. Call it on every independently loaded settings file BEFORE precedence merge.
Legacy `retry.maxDelayMs` migrates to `retry.provider.maxRetryDelayMs` when that cap
is absent or null; an existing nonnull provider cap wins. The legacy key is removed
even when its value is invalid. Unrecognized fields and provider array spread
properties survive. This migration never changes `maxAgentDelayMs`.
`ReadEffective` reads the merged result with nullish defaults. Native admission
rejects malformed types, negative/fractional or unsafe numeric settings; retry
counts additionally fit `Int32`. Upstream's unchecked invalid JavaScript settings
are intentionally not reproduced. This leaf grants no filesystem writes.

The four authored fixture groups are UNEXECUTED and not registered in the reserved
shared test runner. Root integration must concatenate `CleanRetryPolicyTests.Cases()`.
Source inspection includes numeric saturation, defaults, invalid input, permanent
error precedence, JavaScript separator/case boundaries, unknown-field retention,
input immutability and legacy migration controls. These are leaf controls; they
do not claim retry loop, original-task joining, events, RPC or settings durability.

Remaining root-owned work: implement the retry driver and original backoff abort
join; bind startup captured preferences in ordinary host sessions and replacements
before lifecycle observers; retain dynamic preferences across replacement; admit
an explicit persistence store and await original acknowledgement with failure
rollback; decode and dispatch retry RPC controls; expose state and attach retry
events with correct ownership. Automatic Bash compaction settlement is an
independent feature, not transient retry implementation. Inherited c809 composition
fixture API errors are disclosed and corrected by root in a separate successor.
