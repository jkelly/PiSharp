# sync-1.6.0 closeout-docs report

Branch `sync/1.6.0-closeout-docs`, based on `ba2fb1ae` (the durable-edge merge). Documentation and status records only. Nothing pushed, tagged, published or deployed. 1.6.0 is described everywhere as an unreleased source candidate with integrated validation pending; the published version stays 1.5.2.

## Commits

- `aff91002` Add the unreleased Pi 1.6.0 candidate changelog and release notes
- `c9b57bbb` Document the Pi 1.6.0 source candidate in the website docs pages
- `68a9e80d` Point the website version and parity data at the Pi 1.6.0 source candidate
- `5652a5f0` Record the Pi 1.6.0 source candidate in the repository status records
- the commit that adds this report

The first four commits were made after a session handover; they carry the `Claude Opus 5.5` co-author line that the harness required from that point, not the `Claude Fable 5.2` line in the brief.

## Files changed

New:

- `docs/release-notes/1.6.0.md`
- `website/src/content/changelog/1.6.0.md` (`status: in-progress`, label Unreleased)
- `AI_DOCS/agent-reports/sync-1.6.0-closeout-docs.md` (this report)

Modified, changelog and records:

- `CHANGELOG.md` (regenerated from the website changelog pages with the same algorithm as `ChangelogSyncCases.cs`; mirrors the 1.6.0 page including its markers)
- `README.md`
- `IMPLEMENTATION_STATUS.md` (new "Current source candidate: Pi 1.6.0" section; the 1.5.2 section is renamed "Previous source candidate")
- `THIRD-PARTY-NOTICES.md` (1.6.0 catalog, preserved 1.5.2 catalog, baseline, Node bridge undici 8.12.4 and cache `1.6.0-no-default-attribution-v1`, v1.6.0 schema blobs; commit `faac5858` still present for distribution-validation.ps1)
- `docs/compatibility/baseline.md`
- `docs/env-ssh.md`
- `docs/plans/full-parity-progress.json` (`releases."1.6.0"`, status `source-candidate`; valid JSON)
- `docs/decisions/0005-pi-durable-experimental.md` (dated note, 2026-11-21: CodemodeNamespace removal, HookTask, record versions)

Modified, website docs:

- `website/src/content/docs/docs/contribute/build.md`
- `website/src/content/docs/docs/contribute/releasing.md`
- `website/src/content/docs/docs/contribute/testing.md`
- `website/src/content/docs/docs/embed/chord.md`
- `website/src/content/docs/docs/embed/coding-agent.md`
- `website/src/content/docs/docs/embed/durable.md`
- `website/src/content/docs/docs/embed/sdk.md`
- `website/src/content/docs/docs/embed/tui.md`
- `website/src/content/docs/docs/extensions/api.md`
- `website/src/content/docs/docs/extensions/node-bridge.md`
- `website/src/content/docs/docs/guides/durable.md`
- `website/src/content/docs/docs/guides/llama-cpp.md`
- `website/src/content/docs/docs/guides/providers.md`
- `website/src/content/docs/docs/index.md`
- `website/src/content/docs/docs/reference/environment.md`
- `website/src/content/docs/docs/reference/tools.md`
- `website/src/content/docs/docs/versioning.md`

Modified, website data and pages:

- `website/src/data/site.ts` (`latestPi` 1.6.0; CRLF preserved)
- `website/src/data/parity.json` (`sourceCandidate` 1.6.0 at `faac5858`, status `implementation-pending-validation`; `publishedRelease` 1.5.2; the acceptance and publication keys for the candidate are not present)
- `website/src/pages/parity.astro` (CRLF preserved)
- `website/src/pages/index.astro` (CRLF preserved)

Not edited: `compatibility/**`, `PUBLIC-DERIVATIVE-INTEGRITY.json`, `tools/**`, `.github/**`, `src/**`, `tests/**`, the 1.5.2 changelog page and release notes, `compatibility/public-release.json`.

## Pinned files changed (need re-seal)

Checked every changed and new path against `PUBLIC-DERIVATIVE-INTEGRITY.json`:

- `README.md`
- `IMPLEMENTATION_STATUS.md`
- `THIRD-PARTY-NOTICES.md`
- `docs/compatibility/baseline.md`

No other changed file is pinned. The refresh tool was not run.

## Confirm markers

`<!-- sync-1.6.0: confirm against the chord-admission / chord-workers report -->`:

- `website/src/content/changelog/1.6.0.md:20` (Chord admission and workers bullet)
- `website/src/content/changelog/1.6.0.md:34` (Chord workers breaking bullet)
- `CHANGELOG.md:16` and `CHANGELOG.md:30` (generated mirrors of the two lines above)
- `docs/release-notes/1.6.0.md:219` (Chord candidate changes)
- `docs/release-notes/1.6.0.md:287` (Chord breaking changes)
- `website/src/content/docs/docs/embed/chord.md:110` (Admission and workers subsection)
- `IMPLEMENTATION_STATUS.md:19` (open items)

`<!-- sync-1.6.0: confirm against the integrated validation and the closeout-records acceptance record -->`:

- `docs/release-notes/1.6.0.md:334` (Validation status section)

## Not sourced from a report

- Chord admission and workers text (the `denied` failure cause, `Store`, `Admit`, `Incarnation`, exit ordering, `TerminalScope.On`): taken from the upstream v1.6.0 changelog because no chord-admission or chord-workers report existed. The `ProcessWorkerOptions.Store` name comes from the chord-discovery proposal, not from landed code.
- Statements about 1.5.2 behavior (the tools reference, the 1.5.2 default model `gpt-6.1-sol`, llama.cpp port 8080) were read from the base commit and from `343ece01`.
- The `releasing.md` and `build.md` edits (target `v1.6.0`, VersionPrefix 1.6.0, dry-run default 1.6.0) are mine. The note that the dry run stops at the source pin check comes from item 1 of the meta report.
- The llama.cpp guide uses `--port 9931` explicitly. The upstream 1.6.0 doc dropped `--port`; 9931 matches `LlamaProvider.DefaultServerUrl`.
- The pending-validation phrasing follows the `accept-sync-1.5.2.mjs` precedent so an acceptance tool can swap pending phrases for validated ones.

## Verification

- Website: `npm ci` (269 packages; esbuild install-scripts warning only), then `npm run build` built 60 pages. There were two warnings, both already present before these changes: the `i18n` collection does not exist, and docs entry 404 was not found. No content collection errors.
- Internal link and anchor check over `dist`: 6,734 links, 0 broken. The new anchors resolve (`the-160-source-candidate`, `code-mode-namespaces`, `hooks-that-start-tasks`, `the-render-contract`, `suspend-from-outside`).
- The changelog page shows 1.6.0 with the In progress chip, and the markers are not visible. RSS has no 1.6.0 item. The parity page shows "Latest Pi 1.6.0 / Sync to Pi 1.6.0 in progress".
- CHANGELOG.md sync: MATCH against the generator algorithm.
- Relative links in the edited repository markdown: 222 checked, 25 broken. All 25 are older historical links in `IMPLEMENTATION_STATUS.md` (`../../task-3/...`, `../*_FOR_ASTRA.md` and similar) that these commits did not touch. None of the added links are broken.
- `website/` has no tests and no docs tests were found, so `node --test` had nothing to run.

## Follow-ups for root

1. Re-seal the four pinned files.
2. When chord-admission and chord-workers land, check the marked text against their reports and remove the markers. After editing `website/src/content/changelog/1.6.0.md`, regenerate `CHANGELOG.md` (`PISHARP_WRITE_CHANGELOG=1` with the changelog sync test, or the same algorithm).
3. At acceptance, replace the pending phrases, add the acceptance key to `website/src/data/parity.json`, set the changelog page status and date, and resolve the validation marker in the release notes.
