# I-CODE: Pi 1.5.2 codemode

Implemented against frozen upstream `b4afdddf44e059c67ccf5097e5317d913521b470` in `E:/AI/PiSharp/upstream-v152`, following the reviewed D-SCH contracts. Requested reasoning high/service tier ultrafast; effective service tier unverified.

## Changes

- `src/PiSharp.Codemode/CodemodeDeclarations.cs`: exposes public nullable `ResolveSchemaRef(string reference, JsonElement root)` and routes declaration expansion through it. The adapter strips raw empty pointer segments (the established codemode behavior), then delegates URI/UTF-8 decoding, pointer unescaping, target validation, and lookup to existing `LocalSchemaReferences.TryResolve`. Shared JSON Pointer behavior and Contracts files are unchanged. Malformed percent/UTF-8 input cannot resolve a same-named literal definition. Remote references and missing/non-schema targets return null. Existing renderer cycle/expansion guards remain in place.
- `src/PiSharp.Codemode/Runtime/codemode-prelude.js`: uses the exact evaluated upstream 1.5.2 body, including the primitive number branch in `format`. Console output and thrown `NaN`, `Infinity`, `-Infinity` use string forms; object/array JSON behavior remains JSON behavior. Updated attribution.
- `src/PiSharp.Codemode/Runtime/CodemodePrelude.cs`: updated attribution and required embedded-body integrity pin to `e64ad1a6afffbfb8f75a0d08434a9a3fec88d1e54206b03953bfc7b5820cc119`.
- `tests/PiSharp.Codemode.Tests/Sync152Cases.cs`: seven new registered cases. Public helper tests include actual definitions named malformed escapes, valid URI/UTF-8 and `~1`/`~0`, non-schema and remote/missing targets, boolean roots, codemode empty-segment behavior alongside the unchanged shared helper, and cyclic rendering. Rendering tests exercise SchemaToType (including size cap), RenderToolOutputType, RenderToolSignature, RenderToolSample, and tool/global/namespaced RenderDeclarations. Four sandbox cases cover process/thread hosts and ExecuteAsync/deterministic Start, all five console levels and all three thrown nonfinite primitives.
- `tests/PiSharp.Codemode.Tests/Program.cs`: registered `cases.AddRange(Sync152Cases());` after Sync150Cases. Root explicitly authorized this registration.
- `tests/PiSharp.Codemode.Tests/PureCases.cs`: root-authorized attribution/hash assertion update for the current embedded prelude; historical golden captures are untouched.
- `AI_DOCS/agent-reports/sync-1.5.2-codemode-prelude-check.mjs`: reproducible read-only offline source equality and JavaScript behavior check. It runs the full prelude in fresh Node VM realms with ordinary globals (removing Node's default console before lockdown).

## Verification

Run from `E:/AI/PiSharp/update-1.5.2`:

```powershell
node AI_DOCS/agent-reports/sync-1.5.2-codemode-prelude-check.mjs
git diff --check -- src/PiSharp.Codemode tests/PiSharp.Codemode.Tests
```

Both passed. Node v24.22.0 reported source equality, the SHA above, five correct console levels, three correct text primitives, and three correct thrown primitives; the object containing nonfinite values retained JSON null conversion. The source equality check evaluates the frozen upstream TypeScript template with its constants, then compares every body character (17,344 characters; no trailing newline). It initially caught the missing upstream explanatory comment/branch ordering and an added final newline, both corrected before the passing run. Initial VM checks also exposed Node context-proxy/default-console setup differences; the check now uses `vm.constants.DONT_CONTEXTIFY` and removes the host console.

No dotnet build/run was launched, per root's serialized validation requirement. Root should run:

```powershell
dotnet run --project tests/PiSharp.Codemode.Tests -- --only sync152.
dotnet run --project tests/PiSharp.Codemode.Tests
```

Native compilation and process/thread test execution remain for root's integration validation. No provider calls or credentials were used.

## Integration follow-up

Root's validation worker ran all 149 Codemode cases: all seven new cases passed, and the sole failure was the old `differential.deterministic-prelude-hash` assumption that the current prelude must retain the 1.2.0 capture's fingerprint. Receipt: `artifacts/gap-validation/sync152-codemode-final-0898f78941cd4755a99c5804e455e3d3`.

Updated `tests/PiSharp.Codemode.Tests/DeterministicCases.cs` to explicitly validate the preserved 1.2.0 fixture hash `8c6c9242` and its historical fingerprint, then independently check the runtime against current 1.5.2 hash `4dce1bde`. The historical capture file and its provenance were not changed. Extended the offline Node verification to calculate the current FNV-1a over the frozen upstream evaluated prelude's UTF-16 units and assert `4dce1bde`; it passed with source equality and existing runtime checks. Scoped diff checks also passed. The validation worker owns the native rerun; no build/run was performed for this follow-up.
