# Standalone streaming JSON display preview

`PiSharp.AI.StreamingJsonPreview` is a bounded, stateless display helper. `Parse(string? rawInput)` accepts the caller's accumulated fragments and returns an immutable `StreamingJsonPreviewResult` with the exact `RawInput`, owned `JsonData Value` and diagnostic `Stage`. It does not certify complete arguments, validate a tool schema or grant execution authority. It is not integrated into the accepted reducer or `Snapshot`; that previously documented partial-argument gap remains open. Final arguments still require the original strict, complete object and the tool pipeline's validation/authorization.

## Attempt order and supported evidence

The helper follows the pinned Pi attempt order: empty input yields `{}`; otherwise try strict JSON, strict repaired JSON, original partial JSON, repaired partial JSON, then `{}` fallback. Complete arrays, strings, booleans, numbers and null retain their shapes. Partial top-level null falls back to `{}`, while complete `null` stays null. Strings can retain their completed prefix; incomplete containers retain prior members. Raw controls inside strings are escaped and backslashes before invalid escapes are doubled. Repairs affect the display value, never `RawInput`.

The partial reader substantially adapts public `partial-json` 0.1.7 with its default all-types behavior, subject to the native exclusions below. Its global lowercase last-`e` fallback for incomplete numbers is preserved. No JavaScript runtime or npm dependency is used by this helper. [The third-party notices](../../THIRD-PARTY-NOTICES.md) retain full Pi and Promplate Dev Team MIT licenses; [the reference lock](../../tools/PiReferenceRunner/json-preview-lock.json) pins the unchanged source/dependency bytes.

The initial native comparison uses capture commit `14a1b04690b81d1b1c1621a4a05123346dbee88d`, from unchanged Pi `d86654abb8862e201933517d6f1fce9f88dd117f`. It verifies input SHA-256 `5d05b574b515c99d27a71d74b862487d0ae729dd98414d31c8a73a36f21be975` and golden SHA-256 `246182c3bc276f6d92e659bf4403447bcaacadd8aa8666c89d58c0145595ce28`. **32 captured cases match; 7 cases are explicitly excluded.** This is a measured subset, not all-39 parity or qualification of all partial-json grammar/number behavior.

| Excluded captured case | Reason |
| --- | --- |
| `exact-large-integer` | Deliberately narrower initial numeric policy; upstream accepts this exactly representable `2^53` value. This exclusion is not evidence of upstream precision loss. |
| `inexact-large-integer` | The original integer rounds in JavaScript; this native profile rejects it rather than reproducing loss. |
| `inexact-decimal-a`, `inexact-decimal-b` | Distinct long decimal inputs collapse to one upstream value; the native profile rejects both. |
| `negative-zero` | Ordinary JSON output erases the negative-zero distinction; this profile rejects it. |
| `overflow-number` | The upstream result is non-finite `Infinity`, outside this native JSON-value profile. |
| `unicode-surrogate-prefix` | Upstream retains an escaped lone high surrogate; the native JSON string API cannot extract that value losslessly. |

Supported comparisons preserve object members, array order, decoded strings, explicit null, scalar kinds and numeric tokens; only object-key order is ignored. The excluded cases never acquire passing value-comparison status. The source fixture and expected values are not rewritten.

## Native bounds and hardening

Defaults are 65,536 UTF-16 input characters and 32 container nesting levels; configured depth must be 1-64. Limits apply before parsing, including incomplete and trailing raw containers outside strings. A quoted invalid escape followed by a supplementary Unicode character does not leave admission scanning in an incorrect string state. Recursive partial parsing also checks depth.

The numeric policy requires a framework `decimal`-representable value, magnitude at most `9,007,199,254,740,991`, and at most 15 mantissa digits after removing leading zeroes. Negative zero is rejected. These conservative limits are deliberately narrower than the upstream runtime and do not promise lossless JavaScript numeric parity; numeric formatting/rounding beyond the captured supported cases is unqualified. Duplicate property names and unpaired UTF-16 surrogates in string values, names or raw CLR input are also rejected. These are native hardening policies.

`StreamingJsonPreviewException.Failure` distinguishes `CharacterLimit`, `DepthLimit`, `UnsupportedNumber`, `UnsupportedUnicode` and `DuplicateProperty`. Messages are fixed and contain no rejected payload or property name. Malformed ordinary input still follows the captured preview fallback behavior; unsupported/limit failures propagate and are never swallowed into `{}`.

The helper retains no cross-call fragments or state. Each result retains accepted raw input and its bounded preview. Repair can expand characters by up to six times, and parsing creates temporary strings/trees; this is not a total heap cap. Consumers retaining many results own that memory, and callers must supply a newly accumulated string for another preview.

## Observed .NET surrogate limitation

On the executed .NET 10 runtime, `JsonDocument.Parse` accepts JSON containing `"\uD83D"`, but `JsonElement.GetString()` throws `InvalidOperationException`. Separately, `JsonSerializer.Serialize` of a CLR string containing a lone high surrogate emits a replacement U+FFFD value. Tests verify both observations and the helper's rejection; no surrogate is silently replaced by this API. A valid escaped high/low pair remains supported. This is concrete runtime evidence rather than a promise about every .NET version or platform. [JsonElement string API](https://learn.microsoft.com/en-us/dotnet/api/system.text.json.jsonelement.getstring?view=net-10.0)

Run `./tools/test-native.ps1`. The framework-only native harness runs with Node absent and adds four checks: frozen-corpus comparison with explicit exclusions, exact limit/hardening boundaries and payload-free diagnostics, inability to bypass strict final validation, and the runtime surrogate policy. `artifacts/native/json-preview-core.actual.json` records every case, its stage or exclusion reason, runtime and subset counts. The main native report labels the subset and deferred reducer integration. Provider behavior, general numeric/partial grammar qualification, reducer integration, cross-platform execution and full P2-07 remain open.

Primary behavior sources are [pinned Pi `json-parse.ts`](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/ai/src/utils/json-parse.ts) and the frozen `partial-json` 0.1.7 package recorded in the reference lock, from [its public project](https://github.com/promplate/partial-json-parser-js).
