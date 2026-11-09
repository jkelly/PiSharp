# F09 — Reviewed catalog additions and defaults

Requested by parent to complete F09 provider/model expansion, including default runtime coverage. Source is the exact read-only checkout `E:/AI/PiSharp/upstream-2026-11-08`, commit `7d073328e44337d3292a7db9b0f0308f679c5e97`.

## Implementation

`ReviewedModelCatalog` in PiSharp.AI embeds a separately versioned JSON bundle with source hashes. ModelRegistry and Durable apply it to verified frozen shard data before mutable/user configuration. The 42 historical files and their integrity pins are unchanged. Missing optional maintained files do not disable these corrections/additions.

The bundle includes C19's 18 row corrections plus twelve explicit additions: ten Qwen Individual CN models, the missing international Individual GLM 5.3 model, and Copilot's Microsoft-Decision-1 classifier. Qwen model fields are projected from the existing verified CN shard, with no claim of fresh model metadata; current upstream defines exact membership, provider identity, endpoint and key. Copilot's classifier fields come directly from its checked-out catalog rule, including upstream's Azure-derived price/context caveat.

Provider/auth tables register Qwen Individual CN with its China endpoint and China key. Copilot declares its SystemOne classifier API. The classifier runtime owner coordinates actual Copilot operation registration and authenticated transport. The complete default map now matches selected upstream: nineteen existing defaults changed, one new provider default added; all target models exist.

See `docs/reviewed-catalog-2026-11-08.md` for the complete layered-catalog contract. The historical `BuiltinModelCatalog` remains a frozen reader; the runtime default is the reviewed projection. Projection hashes describe derived JSON, not historical provider bytes.

## Validation

Authored tests cover full C19 scope, exact twelve added rows, all default mappings, new allowlist and China/international key isolation, user override precedence, prepared Anthropic capabilities, fake HTTP direct factories, and CLI/Durable China endpoint routing. Durable independently checks all old rows and advertises the classifier only in classifier lookups. Historical frozen hash tests still cover all 42 unchanged shards.

Execution remains under I13 central coordination. The earlier I06 attempt hit an unrelated R8 compile blocker before tests; the final stable Models run below passed. No live provider calls, new models.dev fetch, package regeneration, commits, or publication.
- Follow-up: exact-identity LiveSessionSelection.Parse also now layers the reviewed catalog (previously it was a separate frozen-only product path); tests check Azure context and new Qwen provider there. Classifier runtime registration/auth is complete in companion `I20-F09-copilot.md`, with centralized validation pending.
- CI2 evidence follow-up: Models shell tests now fail with actionable Bash prerequisite errors instead of silently returning. The Windows-only fallback emits an explicit `SKIP` result with reason `requires-windows-cmd-exe` on non-Windows, preserves its registered-case count, and increments top-level `skipped`; report includes `platform`. Coordinated precise exception policy with harden_ci_runner. Scoped `git diff --check` passes; no build executed for this reporting-only change.

### Stable Models result

Inspected actual JSON and receipt at `artifacts/gap-validation/Models-stable4-5e59351993854fd8a160c596a3c8485a`: **113/113 authored native cases passed, zero failures/skips** on Windows x64, SDK 10.0.401; build had zero warnings/errors. This includes exact default-map scope, Qwen allowlist/key isolation, classifier-versus-chat separation, user override precedence, default exact-ID paths, C19 request capabilities, schema provenance and fake provider routes.

Before/after source SHA-256 is identically `d1b801cfd796390b87d11f11bc8876729fd1eaeb52d820968a3a5ff4c7b62cc8`; `changedDuringRun:false`. The worktree is a dirty successor of HEAD `14dd34b369fba6d236684da6dfc8a39a03263321`. The suite's retained historical `sourceSha` label is distinct from this current source binding. No optional package tarball was provided (`tarballVerified:false`). This Models receipt does not qualify live providers, the separate Copilot operation suite, or Durable scope/route tests. Those keep their own I13 receipts. No new build for this report-only update.
