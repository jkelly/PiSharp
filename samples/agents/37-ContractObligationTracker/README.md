# Contract Obligation Tracker

Compute sourced obligations and reminder windows for `contract-a` as of **2026-11-01**. The fixture includes an overdue quarterly report, renewal notice at the start of its 14-day lead period, an acceptance pack due ten calendar days after delivery, and a transition plan whose triggering event is unknown.

```powershell
dotnet run --project samples/agents/37-ContractObligationTracker -- --demo
dotnet run --project samples/agents/37-ContractObligationTracker -- --self-test
```

Run from the repository root. `inspect_contract` validates each obligation's source and clause, `calculate_deadlines` derives calendar dates and statuses, and `save_obligation_register` writes `obligations-contract-a.json` with source text, action, owner, reminder date, days remaining and status counts. The output distinguishes overdue, due-today, due-soon, upcoming, fulfilled and unresolved. A missing event never receives an invented deadline.

Each structured obligation references a local source document and clause and supplies exactly one `dueDate` or `eventId`/`offsetDays` rule. Lead periods are calendar days, inclusive of the reminder start date. This sample consumes already structured clauses; it does not claim to parse arbitrary contracts, determine applicable law, calculate business-day calendars or send notices.

Use `--input FILE --workspace DIR` for another fixture or `--live --model MODEL` for native provider orchestration. The top-level `asOf` is explicit and reproducible. Saving this local review artifact needs no mutation approval; nothing changes in an external contract system.

Self-tests cover native execution and saved output, fixed/event-based deadlines, exact lead/due boundaries, missing events, clause provenance, missing contracts, ambiguous sources/rules, fulfilled obligations, second contract independence and deterministic repeats.
