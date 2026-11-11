# D-SCH discovery: schema, codemode, Durable tool search/tools

Compared upstream `f9a26365568127461c47a07a37dcb42c140a3ac5` (1.5.0) with `b4afdddf44e059c67ccf5097e5317d913521b470` (1.5.2), read-only source discovery. No source edits or builds. Requested service tier ultrafast; effective tier unverified.

## Complete changed-file inventory

`packages/schema`: CHANGELOG.md, README.md, package.json, THIRD_PARTY_NOTICES/typebox-LICENSE (rename from LICENSES), src/{builders,checks,coerce,compiler,explain,index,resolver,typebox-static,validator}.ts, test/{builders,coerce-typebox,coerce,equal,typebox-static,validator}.test.ts.

`packages/codemode`: CHANGELOG.md, package.json, src/declarations.ts, src/index.ts, src/runtime/prelude-source.ts, test/declarations.test.ts, test/sandbox.test.ts.

Requested Durable paths: src/codemode/{codemode,models}.ts; src/tool-search/tool-search.ts; src/tools/{bash,file-mutation-queue,path-utils,read}.ts. All other files in these requested directories unchanged. Durable's changelog identifies the tool-search local-ref fix and malformed-ref code mode fix. Its other runtime bullets belong to other discovery owners.

## Schema contracts and applicability

Native package is `PiSharp.Contracts.Schema` (not a separate src/PiSharp.Schema project); tests are `tests/PiSharp.Schema.Tests`.

| Upstream delta | Native evidence / disposition |
| --- | --- |
| `unsafe<T>`, `recursive<T>` | Already present in `JsonSchemaBuilder.cs`: `Unsafe<T>`, `Recursive`, `Recursive<T>`, `JsonSchemaDefinition<T>`; snapshots protect JsonNode ownership. Native recursive builder intentionally uses fragment `#`, avoiding doubling relative resource paths. Existing GapCases exercise these APIs. |
| `Validator.validate` and `equal` | Already present: `JsonSchemaValidator.Validate`, `TypedJsonSchemaValidator<T>.Validate`, `JsonSchemaValidator.Equal`. Validate preserves original value on success; strict/no coercion. Existing GapCases exercise them. |
| Relative root `$id`, annotations, nested-resource errors, initial dynamic-ref coercion | Already ported: `SchemaResolver.RootBase` is DefaultBase, `BaseFor` indexes on first identifier, compiler's WithoutUnevaluated caches the correct resource base, explainer resolves branch refs with BaseFor, coercer BaseOf calls BaseFor. `GapCases` C01 contains all five targeted regressions. Preserve these. |
| Finite number checks | **Missing.** `SchemaChecks.TypeChecks["number"]` currently accepts every double; `SchemaCoercer.MatchesType` likewise. Integer uses Js.IsInteger and already rejects nonfinite doubles. Change number membership to finite double, including number-in-union; explainer must report `NaN`, `Infinity`, `-Infinity` instead of `number`. Numeric bound and no-type behavior must retain upstream distinctions. |
| Required property present with undefined | **Missing.** ObjectCore unconditionally rejects a required JsUndefined, even when the property check accepts it. Explainer reports missing likewise. Required own key must exist, then its schema decides whether undefined is valid. Optional undefined continues counting as absent. Required name without declared property continues checking presence only. |
| bigint / undefined validation and conversion | **Missing.** Internal value model already has `JsUndefined` and uses `BigInteger` in conversion helpers, but TypeChecks has neither type, numeric checks/explainer accept only doubles, and coercer lacks both conversion cases. Implement native engine counterparts using `BigInteger` and `JsUndefined`. See precise contract below. |
| bigint builder / TypeBox Static Enum, Tuple, BigInt, Undefined | TypeScript static types have no direct C# counterpart. Public API accepts JSON JsonNode and typed APIs serialize CLR values to JSON, intentionally not arbitrary JS values. Do not silently serialize BigInteger as double or map undefined to null. Proposed disposition: implement engine semantics and native internal regression coverage; preserve documented public JSON boundary. If public arbitrary-value support is desired it requires a separately reviewed API, not an ambiguous object overload. |
| leaf-code, type-mask, combined object/list performance changes | Native implementation already deliberately uses closures per keyword group (`SchemaChecks.cs` header). Upstream implementation details need no literal port; all verdict changes above do. Avoid claiming the upstream JS benchmark speedups for native code. |
| notices directory rename | Browser/Vite package layout only; root owns package pins/notices inventory. No runtime C# change. |

Precise internal value contract for implementation:

* `type: "bigint"` accepts only `System.Numerics.BigInteger`; `type: "undefined"` accepts only `JsUndefined`. Types work standalone and in arrays/anyOf/oneOf/objects/items.
* Bigint conversion: bigint unchanged; bool => 1/0; finite double truncated toward zero then converted; null/undefined => 0; existing bigint string grammar (`-1n`, integer/decimal text truncating fraction) plus case-insensitive true/false. Unsupported input unchanged.
* Undefined conversion: undefined, null, zero bigint, false, zero double, case-insensitive `undefined`/`null`, empty string and exact `0` => JsUndefined; other inputs unchanged. Conversion of bigint const/enum members uses bigint conversion before strict comparison. MatchesType must recognize both new types.
* Numeric minimum/maximum/exclusive bounds compare BigInteger and double without converting arbitrary-size integers to doubles. `multipleOf` with either bigint operand uses exact integer arithmetic, rejects zero divisor and any nonintegral/nonfinite double operand, never throws. Existing decimal double multipleOf behavior remains.
* Explainer walks bigint numeric constraints and reports decimal values without `n`; type error for bigint against number says `got bigint`; nonfinite number errors name the value. Required undefined accepted by a declared schema must not emit a missing error. Object presence (`HasOwn`) remains distinct from GetOwn's absent sentinel.

## Codemode contracts

`CodemodeDeclarations.ResolveRef` is private and still calls permissive `Uri.UnescapeDataString`: malformed percent text can accidentally resolve a literal `%GG` or `%E0` property. Existing Contracts `LocalSchemaReferences.TryResolve` already strictly rejects invalid URI escapes/UTF8; expose public `CodemodeDeclarations.ResolveSchemaRef(string reference, JsonElement root)` returning nullable JsonElement and route declaration rendering through strict resolution. Upstream exports this helper from declarations and package root. Preserve upstream behavior of ignoring empty path segments for this codemode helper (existing native behavior); shared helper currently retains empty segments intentionally, so use an explicit compatibility option/adapter rather than silently changing shared lookup behavior. Tests should include an actual definition named `%E0`/`%GG` to distinguish rejection from accidental missing-key success; valid percent-encoded and ~1/~0 names; remote/missing/cyclic; and all rendering entrypoints.

`Runtime/codemode-prelude.js` format() lacks upstream's `if (typeof value === "number") return String(value);`. Add it before JSON.stringify: console and thrown primitive nonfinite numbers must say `NaN`, `Infinity`, `-Infinity`, not `null`. Applies both embedded engine modes consuming this prelude. Add regression to sandbox tests for console and thrown values, matching upstream sandbox test.

## Durable tool search and tools

`Durable.Tools/ToolSearch/ToolSearch.cs` already follows local refs through strict `LocalSchemaReferences.TryResolve`, initializes a reference-identity seen set with the root, reads each referenced object once, keeps inline occurrence frequency, excludes unused defs. `tests/PiSharp.Durable.Tests/ToolSearchReferenceCases.cs` covers chains, sharing, cycles, escaped names, empty names, bad UTF8, malformed escapes, unreachable definitions. `Extensions.Abstractions/Mcp/Discovery/ToolSearchRanker.cs` already uses shared `AppendSearchText`; the same contract is therefore present for CLI/shared search. Native adds a depth-64 guard and supports empty keys whereas upstream filters empty segments: existing intentional defensive/correct-pointer behavior, not a new omission.

Durable codemode/models and bash/file-mutation-queue/path-utils/read changes only move Chord imports from package root to context/json subpath exports. Native namespaces already resolve direct CLR contracts; no behavior change needed in these six files. Any Chord namespace relocation belongs to D-CHO.

## Proposed work split and validation

1. I-SCH: `src/PiSharp.Contracts/Schema/{SchemaChecks,SchemaCompiler,SchemaExplainer,SchemaCoercer}.cs` and new `tests/PiSharp.Schema.Tests/Sync152Cases.cs`. Own finite/undefined/bigint semantics. Register tests through root, preserve historical fixtures. Main decision requested: approve internal JS-value model support while keeping public JsonNode boundary explicit.
2. I-CODE: `src/PiSharp.Codemode/CodemodeDeclarations.cs`, `Runtime/codemode-prelude.js`, new codemode test file; potentially additive strict decode helper in Contracts only with coordinated ownership. Own public ResolveSchemaRef and nonfinite formatting. Existing search APIs are already correct and need no edits.

Root should serialize builds/runs. Run Schema test executable including full draft7/2020-12 conformance, Codemode executable including both runtimes, Durable tool-search/reference tests, and extension/shared search tests. No live provider calls required. Add targeted native cases drawn from all new upstream validator/coerce tests (standalone/object/pattern/item checks, required absent vs present undefined, exact huge bigint modulus and fractional/zero divisor, finite membership and diagnostic text). Existing relative-id GapCases should remain green. Historical upstream fixture hashes are immutable; new vectors require separate v1.5.2 provenance.

## Approved I-SCH implementation

Root approved the internal BigInteger/JsUndefined value-model implementation with unchanged public JSON representation. Implemented finite number membership and diagnostics; declared-required undefined presence/check behavior; bigint numeric constraints using exact mixed bigint/double comparisons; bigint and undefined conversions; bigint strict equality/memo equality and conversion to existing primitive types. Added explicit public Coerce/Parse rejection of bigint/undefined results rather than silently representing them as JSON null. This touches `JsonSchemaValidator.cs` and `JsValues.cs` in addition to the four planned engine files.

Added `tests/PiSharp.Schema.Tests/Sync152Cases.cs` with eight case groups. Root must register `cases.AddRange(Sync152Cases)` in Program.cs. No tests or builds run by this worker; root owns serialized execution. `git diff --check` passed after initial edits. I-CODE assigned separately by root.
