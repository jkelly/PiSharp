# Training Compliance Tracker

Inspects employee completion evidence, calculates training status using the fixture's explicit `asOf` date, and writes employee reminders and manager escalation drafts. Nothing is sent externally.

```powershell
dotnet run --project samples/agents/44-TrainingComplianceTracker -- --demo
dotnet run --project samples/agents/44-TrainingComplianceTracker -- --self-test
```

The E1 fixture produces expired safety training, privacy training due within 30 days, and missing overdue equipment training. Initial deadlines are hire date plus `initialDueDays`; renewal deadlines are latest completion plus `validDays`. Training remains valid on its due date and becomes expired the following day. Future or pre-hire completion evidence is rejected. Missing manager IDs produce escalation drafts explicitly marked unready for delivery.

Tools run in order: `inspect_training`, `assess_compliance`, `draft_reminders`, all with an explicit `employeeId`. Assessments are keyed by employee, and drafting recalculates current fixture evidence. `training-assessment.json` and `training-reminders.json` are local workspace artifacts. Eight focused cases cover statuses, grounded drafts, multiple employees, ordering/missing data, future completions, deadline boundaries and absent managers. No credentials or network access are required.
