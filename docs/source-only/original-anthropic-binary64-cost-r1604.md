# Original Anthropic cost projection

The original isolated leaf added an internal cost-object builder for explicitly selected original-source snapshots. It does not change native decimal rates, costs, reducers, transports, or PiWireJson. The later snapshot composition integrates the returned owned JsonData into usage.cost without changing native decimal costs.

The algorithm follows pinned Pi 0.99.1 models.ts lines 1205–1211 (SHA256 4739010c7e4f7596607b1dd495b9d5ab6cd921e29f55c5c331259a04b7253269): divide input/output/cache-read rates before multiplying counts; combine short/one-hour cache-write numerator before division; sum components left to right. Counts and rates become binary64 operands before these operations. Rates must already be resolved by the caller; tier/model selection is not implemented here. A missing one-hour count is represented by caller-supplied zero. This does not recover discarded provider input or change error policy.

Six dedicated source-only controls cover zero/default rates, operation-order rounding, mixed one-hour writes, an error-partial usage snapshot, Number rounding of large count operands, and invalid cache split refusal. Hard-coded expected IEEE754 bits were calculated from the pinned arithmetic in the orchestration JavaScript evaluator, not by running the original SDK or C# helper. Tests use reflection to avoid expanding the public API. At the original leaf freeze these controls were not registered or executed. The later isolated focused runner explicitly invokes all six; this does not register them in every repository runner.

JSON numeric values are the compatibility boundary, not byte-identical JavaScript number formatting. No full paired parity is claimed. The original leaf consisted only of the helper, controls and this document; later composition and bounded execution are separate evidence.

## Later bounded execution

See [provider source-projection evidence](../verification/provider-source-projection.md) for the later candidate-specific observation and acceptance status. Historical source-only statements above describe their original freeze, not a claim about every later composition.
