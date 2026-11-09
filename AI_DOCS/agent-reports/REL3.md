# REL3 draft-compatible gated GitHub publication

Status: complete and frozen for root integration. Requested worker model/tier: gpt-6.1-sol high, Ultrafast; effective tier unverified. No GitHub mutation, tag creation, .NET execution, Git index/branch mutation or other remote write was performed.

## Contract and implementation

`tools/release/publish-github-release.mjs` replaces only the final Create release shell invocation in `release.yml`. The existing release job still requires build, NuGet publish, core validation, Durable validation, gap validation and native environment validation, and still runs only on a tag push. Manual dry runs cannot publish. Root's manual default version 1.5.0.1 remains unchanged.

The helper also requires push/tag context, exact tag/version equality and an explicit 40-character expected source commit from `GITHUB_SHA`. It reads the actual GitHub tag ref, peels annotated tags with a bounded cycle-checked walk, and requires that existing tag to resolve to the exact expected commit. It never creates a tag.

All CLI execution uses `child_process.spawn` with structured argv, `shell:false`, hidden Windows startup, bounded output and a 10-minute process timeout. Repository, tag, title, notes and package paths are separate arguments; filenames and version are constrained. Assets come from the gated validated package artifact. The helper additionally requires nonempty regular nupkg files of the exact version; it does not replace the existing package-validation/build gates.

GitHub API reads explicitly request included HTTP response headers. Only a genuine HTTP 404 plus gh exit 1 and the API's `Not Found` JSON is treated as absence. Authentication, rate-limit, network, malformed-header/JSON and other CLI failures refuse publication. A release returned with the wrong tag/identity or an existing published release refuses before mutation.

- **No existing release:** recheck tag, then create using `--verify-tag`, prepared version-specific notes or `--generate-notes`, correct prerelease flag and the exact package argv.
- **Matching draft:** upload all validated assets with `--clobber`; an upload failure stops without promotion. Recheck the source tag and same still-draft release identity, then edit with `--draft=false` and the correct prerelease boolean. Use the version notes file when available; otherwise preserve the prepared draft body. A moved tag, replaced draft or concurrent publication prevents the promotion call.

This preserves the intended future tag gate when root prepares a reviewable unpublished draft now. It does not publish the current candidate or grant manual publication.

## Offline validation

`node --test tools/release/publish-github-release.test.mjs`: **14 actual tests passed**, zero failures/skips. Root independently ran the same 14 controls successfully. Every GitHub call in these tests is dependency-injected and recorded; no actual gh process or network request runs.

Controls cover exact argv for paths containing spaces; create with notes; generated notes/prerelease; draft upload-before-promote ordering; prepared-note preservation; published refusal; absent/wrong-source tag; annotated tag peeling; auth/rate/network/malformed/false 404 errors; upload failure without promotion; tag/draft changes after upload; manual/branch/mismatched-version/invalid-SHA refusal; wrong-version and empty package inputs; and wrong draft tag.

The test is now included in the existing Core applicability offline Node command, as root requested. YAML/static checks verified unchanged six release dependencies, push-only release/NuGet guards and the final helper invocation. Scoped whitespace validation passed. No runtime publication is claimed by these controls.

## Files and handoff

- `tools/release/publish-github-release.mjs`
- `tools/release/publish-github-release.test.mjs`
- `.github/workflows/release.yml` (final helper invocation only for REL3; root's version change preserved)
- `.github/workflows/core.yml` (include the offline helper tests)
- This report; hashes recorded in `artifacts/release-prep/REL3/checkpoint.json`.

Optional release-guide wording proposed to root, without editing frozen website content: “A prepared GitHub draft stays unpublished until the matching existing tag passes all release gates; the final job uploads validated packages before promoting that draft.” Root may add it if the guide currently assumes the release never exists before the tag push.

Root integration explicitly pins Node 22 in the final release job, matching the Core test job. This three-line toolchain addition follows the worker checkpoint; publication dependencies and guards remain unchanged.
