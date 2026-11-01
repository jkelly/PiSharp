# Incident Timeline Builder

Collects multiple local evidence sources, builds a deduplicated UTC timeline, and saves the result with provenance, correlation groups and unresolved contradictions.

```powershell
dotnet run --project samples/agents/45-IncidentTimelineBuilder -- --demo
dotnet run --project samples/agents/45-IncidentTimelineBuilder -- --self-test
```

Call `collect_evidence`, `build_timeline`, then `publish_timeline` with the same explicit `incidentId`. INC1 has four source records representing three events: the duplicate event uses different timezone offsets but normalizes to the same instant. Each timeline event retains its source IDs. Identical event IDs with conflicting facts retain each variant and produce a conflict entry. Variant identity uses a structural tuple of timestamp, kind, summary and correlation ID, so embedded newlines cannot merge different facts. Correlations use only supplied correlation IDs, and the result explicitly says root cause is not established.

Event IDs, summaries, kinds and timezone-bearing timestamps are required. Recollecting invalidates that incident's previous timeline. Separate incident IDs have separate state. `incident-timeline.json` is a workspace report; it contains no inferred causality. Nine focused cases cover chronology/deduplication, provenance/correlation, multiple incidents, ordering/missing data, conflicting sources including embedded-newline variants, timezone requirements and stale-publication prevention. No credentials or network access are required.
