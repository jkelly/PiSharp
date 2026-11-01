# Field Service Dispatcher

The workflow inspects a job and technician calendars, ranks feasible technicians, and records a local assignment. For the fixture's `JOB-71`, `TECH-ADA` has the electrical skill, shift capacity, no overlapping visit, ten minutes of inbound travel and ten minutes to the next appointment. `TECH-BEN` cannot arrive in time; `TECH-CAM` lacks the required skill.

```powershell
dotnet run --project samples/agents/42-FieldServiceDispatcher -- --demo --approve
dotnet run --project samples/agents/42-FieldServiceDispatcher -- --self-test
```

Feasibility requires all skills, a positive job interval inside the shift, no appointment overlap, arrival before the fixed start and enough travel time to the next visit. Travel uses Manhattan distance on a nonnegative fixture grid multiplied by `travelMinutesPerUnit`; it is not road routing. The fixture position is authoritative at `asOf`. Future prior appointments supersede it; past visits cannot reset the current position. Feasible candidates sort by total inbound/outbound travel, then technician ID for deterministic ties.

`assign_technician` independently checks host approval and recomputes the ranking. Only the current top feasible candidate can be assigned. `assignments/<operationId>.json` binds the key to job and technician source snapshots. Same-key replay survives a fresh context; changed payloads and duplicate jobs under different keys fail. Existing saved assignments join technician calendars and prevent double booking. A process-level gate serializes assignment admission; use one process per workspace. Production dispatch needs a transactional database and calendar concurrency/version checks across processes.

Approval permits only this local simulation. No technician is contacted and no live dispatch calendar changes, including in live model mode. Real deployment needs authoritative availability, authenticated dispatch APIs, traffic-aware routing, labor rules, and a separate reviewed external assignment adapter.
