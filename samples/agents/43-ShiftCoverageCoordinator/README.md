# Shift Coverage Coordinator

Uses native PiSharp tools to inspect a shift, rank qualified replacements, and record an approved assignment in a local roster. No scheduling service or employee messaging is involved.

```powershell
dotnet run --project samples/agents/43-ShiftCoverageCoordinator -- --demo --approve
dotnet run --project samples/agents/43-ShiftCoverageCoordinator -- --self-test
```

The fixture asks for S1: E2 is eligible; E1 overlaps an assigned shift, E3 cannot cover the full time interval, and E4 lacks the required qualification. Eligible staff sort by assigned hours, then employee ID. Intervals use half-open overlap semantics, so back-to-back shifts are permitted. Every tool reloads and validates the persisted roster, including across new contexts using the same workspace. Assignment recomputes eligibility using earlier local assignments and prevents replacing a different assignee. Repeating the same assignment returns an idempotent receipt. Versioned assignment fingerprints reject changed employee/shift evidence, unknown roster references and conflicting assignments; use a fresh workspace for changed source records. This is a sequential local ledger, without concurrent-writer coordination.

`coverage-ranking.json` and `roster-assignments.json` are written beneath the selected workspace (`--workspace DIR`; a unique temporary directory by default). Assignment requires `--approve`, including direct callback execution. Reports can be generated without it. The fixture omits labor-law/overtime rules and external availability confirmation; this is a deterministic example, not a production scheduling engine.

Eleven focused cases cover ranking/exclusions, idempotency, ineligible employees, approval, distinct shifts, missing input, overlapping assignments after a new context, preservation of prior roster entries, changed source evidence and malformed persisted references. No credentials or network access are used by demos or tests.
