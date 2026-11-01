# Duplicate Record Resolver

Compare customer identities, review explicit merge groups, and apply approved conflict-aware merges in a local JSON store. The fixture merges `c2` into `c1` and `c4` into `c3` while preserving the ambiguous shared-mailbox records `c5` and `c6`.

```powershell
dotnet run --project samples/agents/57-DuplicateRecordResolver -- --demo --approve
dotnet run --project samples/agents/57-DuplicateRecordResolver -- --self-test
dotnet run --project samples/agents/57-DuplicateRecordResolver -- --input path/to/fixture.json --workspace path/to/output --approve
```

The native agent calls `inspect_duplicate_evidence` with all explicit `recordIds`, then `propose_duplicate_merges` and `apply_duplicate_merges` with requested `mergeIds`. Input `records` have unique `id`, `name`, `email`, `phone`, `externalId`, `address` and `notes`. `merges` name an `id`, `survivorId`, `duplicateIds`, and optional explicit address `resolutions`.

Matching requires normalized names plus at least one matching email, phone or external ID. Nonempty contradictory identity fields block a merge; similar names and shared email alone never override conflicting identities. Email normalization trims/case-folds but does not strip plus tags or punctuation. Phones compare digits only when 10–15 digits are present; country-code inference is intentionally unsupported. Groups cannot overlap. Every duplicate must independently match the survivor, and all duplicates are checked against each other for contradictory identity fields. Blank survivor values cannot hide a conflict elsewhere in the group.

Nonempty conflicting addresses require a supplied resolution equal to one of the source addresses. Missing fields are filled from the group, notes are unioned in source order, survivor IDs are retained, and original records are archived as provenance. The fixture's `c1` address becomes `20 Pine St`, and `c3` gains its verified email. Both host and callback require approval; fresh fingerprints reject changed records or instructions.

`duplicate-merge-plan.json` reports blockers and complete proposed survivors. `customer-state.json` stores four surviving records and two original-record histories; `duplicate-merge-receipt.json` records actual removals. Exact requests can be replayed after restart without merging twice or replacing provenance. Eight offline regression cases cover multiple identities, missing records, overlapping groups, group phone/external-ID conflicts, missing resolutions, stale input, approval and replay.

No CRM or external database is modified. The matching policy is conservative fixture logic, not a general identity-verification service; ambiguous people require human investigation.
