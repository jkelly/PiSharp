# Read-only cross-review: samples 48–57

Reviewed 2026-11-01. Assignment `review-48-57`, hard complexity, assigned `gpt-6.1-sol` / high reasoning, requested ultrafast tier; effective tier unverified. Scope was samples 48–57, excluding this worker's own samples 38–42.

Two confirmed correctness findings require fixes. Source was not edited and no builds, live APIs, commits or pushes were performed. Existing compiled demos were invoked twice with temporary local repro fixtures using `--no-build --demo --approve`. Both returned exit 0, `success:true`, and three error-free stages despite violating their intended decision rules.

## [P2] Sample 57 fails to check conflicts between secondary members of a merge group

Location: `samples/agents/57-DuplicateRecordResolver/Workflow.cs:97–100`; field selection at lines 111–114; deletion at lines 125–127.

`Build` compares each duplicate only with the original survivor. An incomplete survivor can therefore bridge two contradictory records: both pass against the survivor, while the pair contradicts each other. `Fill` then picks the first nonempty phone/external ID and the mutation removes both duplicate records. This is inconsistent with `inspect_duplicate_evidence`, which correctly reports the conflicting duplicate pair as ineligible.

Minimal input:

```json
{
  "records": [
    {"id":"r1","name":"Alex Rivera","email":"shared@example.test","phone":"","externalId":"","address":"","notes":[]},
    {"id":"r2","name":"Alex Rivera","email":"shared@example.test","phone":"3125550101","externalId":"acct-A","address":"","notes":[]},
    {"id":"r3","name":"Alex Rivera","email":"shared@example.test","phone":"3125559999","externalId":"acct-B","address":"","notes":[]}
  ],
  "merges":[{"id":"m1","survivorId":"r1","duplicateIds":["r2","r3"],"resolutions":{}}]
}
```

Observed:

- Inspection: `r2` versus `r3` has conflicts `phone, externalId` and `Eligible:false`.
- Proposal: `Blockers:[]`, survivor phone `3125550101`, external ID `acct-A`.
- Apply: `removedIds:["r2","r3"]`; the conflicting identity has been consolidated into the first one. Original provenance remains archived, but this does not make the incorrect merge safe.

Expected: the whole group is blocked when any two nonempty identity values contradict. Validate all member pairs or aggregate normalized nonempty values per identity field before selecting fields; retain the existing explicit address-resolution rule. Add a three-record regression with an incomplete survivor and contradictory secondary members, asserting no store mutation.

Repro artifacts: `C:/Users/jksof/AppData/Local/Temp/pisharp-review57-791203f39d534ec78e4d02cda485ace3/fixture.json` and `output/report.json` / `output/customer-state.json` under that directory.

## [P2] Sample 56 interprets a missing retention duration as an immediate-expiry policy

Location: `samples/agents/56-DataRetentionCleanup/Workflow.cs:13` and lines 62–73, especially the range-only validation on line 63.

The non-nullable `int RetainDays` deserializes to zero when `retainDays` is omitted. Zero passes the policy range check and yields `expires = created`. Consequently malformed policy evidence becomes an eligible deletion rather than an error/unknown policy. Explicit key approval does not authorize bypassing the independently promised policy eligibility check.

Minimal input:

```json
{
  "asOf":"2026-11-01",
  "policies":[{"category":"diagnostic"}],
  "approvedRecordKeys":["logs:recent"],
  "datasets":[{"id":"logs","records":[
    {"id":"recent","category":"diagnostic","createdOn":"2026-10-31","legalHold":false,"payload":"one-day-old record with missing retention duration"}
  ]}]
}
```

Observed: inspection reports `Status:"eligible", ExpiresOn:"2026-10-31"`; planning succeeds; execution reports `removedCount:1, remainingCount:0`.

Expected: omitted `retainDays` is rejected or produces an ineligible/unknown-policy assessment. Validate required property presence/type before conversion, use required constructor-parameter enforcement, or use a nullable field and reject null. Preserve an explicitly supplied zero-day policy if it is intended to be valid. Add a missing-duration regression asserting that no payload is removed.

Repro artifacts: `C:/Users/jksof/AppData/Local/Temp/pisharp-review56-e84680b3805044669343c3fcf03c5865/fixture.json` and `output/report.json` / `output/retention-state.json` under that directory.

## Review coverage and limits

Read all ten workflow implementations, their batch reports, and relevant fixtures/READMEs/tests. Examined source lookup identity, malformed evidence handling, stage prerequisites, source/plan freshness, approval callback guards, stored-state replay, and the distinction between reported analysis and actual local effects.

No other concrete defects were confirmed in samples 48–55. Their documented bounded fixture models, exact textual migration, nontransactional multi-file writes, recorded-page normalization, and limited XHTML rules were treated as intentional limitations rather than findings. This is a focused correctness review, not a production security audit or exhaustive proof. The two temporary reproductions exercise compiled artifacts available during review; source line references were verified against the inspected worktree.

## Final resolution

Resolution: the missing retention interval and conflicting multi-record merge findings were fixed with regression tests. Final full offline validation and live result comparisons for 56/57 pass.
