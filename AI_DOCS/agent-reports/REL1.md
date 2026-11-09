# REL1 — 1.5.0.1 release metadata and notes

Status: complete; source frozen for main's candidate package build. Release publication and candidate qualification remain separate, pending work.

Root: `C:/Users/jksof/.codex/worktrees/close-upstream-gaps/PiSharp`, branch `codex/prepare-1.5.0.1`. Assignment: hard, `gpt-6.1-sol`, high reasoning, requested Ultrafast; effective tier unverified because the delegation tool exposes no tier control.

## Files

- `Directory.Build.props`: default `VersionPrefix` becomes `1.5.0.1`; the conditional fixed `PiSharp.ExtensionHost` assembly version stays `1.0.0.0`.
- `compatibility/public-release.json`: top-level candidate version/tag, SDK-derived assembly identity, notes, source-only scope and experimental additions describe the candidate. All three publication flags and the existing four qualification booleans remain false. The Pi v1.5.0 tag/commit is unchanged. The complete original published 1.5.0 top-level record, excluding recursive `previousReleases`, is appended once to history, including its original qualification/status, release scope, publication date, tag commit and release run. Flat aliases retain the website reader's existing contract. Every earlier historical entry remains byte-for-byte unchanged.
- `README.md`: published install is explicitly pinned to 1.5.0; candidate/on-main additions and pending qualification are distinguished. Correctly separates published 1.5.0's Pi 1.2.0 npm runtime from the candidate's reviewed Pi 1.5.0 runtime. Adds candidate notes and the five-library/29-package preparation scope.
- `docs/release-notes/1.5.0.1.md`: concrete results for all C01–C23 corrections; provider/threads/Unicode/tier changes; Durable options, bulk operations and captures; Chord current/legacy/hosting; Env/daemon/SSH/Unix PTY and Windows `ENOTSUP`; evaluation tool; exact package/dependency and experimental opt-in rules; source-compatible default-throwing `ITx.GetSubmissionAsync` caution; stored-format and evidence boundaries.

No website or CHANGELOG files, runtime code, release tooling or tests were edited. No .NET build, restore, paid-provider call, tag, push, NuGet publication or GitHub release operation was run. Existing exact-sibling dependency generation remains unchanged.

## Verification

Read-only source inspection covered the release plan, project map, workflow plan, audit contracts, implementation results/notes, package admission, release scripts/targets, website reader, actual `ITx.GetSubmissionAsync`, Anthropic thread store and capture reports.

A Python static consistency check passed:

1. XML `VersionPrefix`, candidate product/package version and proposed tag agree on 1.5.0.1; all candidate publication/qualification flags are false.
2. Upstream tag/commit match the original published record and `compatibility/target.lock.json` exactly.
3. `git show HEAD:compatibility/public-release.json` matches every original historical entry and every field of the preserved 1.5.0 record; exactly one 1.5.0 historical record exists.
4. The existing website's flat-history published fallback selects 1.5.0 and Pi v1.5.0.
5. Fixed supervisor identity and default candidate SDK assembly metadata agree; exact-version sibling target remains present.
6. The release script lists 28 unique libraries, including all five additions, plus the separate CLI package.
7. Candidate notes contain exactly the C01–C23 correction ID set, and all local links in README and candidate notes resolve to existing files/directories.

`git diff --check -- Directory.Build.props compatibility/public-release.json README.md docs/release-notes/1.5.0.1.md` passed. No new metadata-only regression test was justified for this reversible preparation; existing main-owned package/archive/consumer checks exercise the version and dependency contracts.

## Handoff and boundaries

Main and WEB1 received the frozen checkpoint and published-reader contract. Main owns candidate package/archive validation, the fresh external consumer and all .NET builds. Earlier implementation receipts retain version 1.5.0 and their original fingerprints; no result was relabeled as candidate qualification. Hosted failures/fixes/reruns must be reviewed at the actual final source; the release notes intentionally do not assert hosted acceptance while triage is active.

No REL1 implementation blocker remains. Publication is outside preparation scope. Existing platform/evidence limitations remain explicit: physical macOS input, real SSH hosts, Android/other RIDs, live provider accounts, native plugin/runtime differences, browser/Cloudflare exclusions and the retained Mistral native-policy mismatch.
