# Documentation drift detector

Runs `inspect_document` → `propose_corrections` → `write_corrections`, with explicit `documentId` (`setup` in the fixture). The repository snapshot contains files, exact supported command strings and setting keys. Documents contain text and references whose targets occur exactly once.

The comparison identifies missing targets and exact line/offset locations. A correction is proposed only when its configured replacement exists in the same snapshot. Missing replacement evidence stays unresolved; ambiguous, duplicate and overlapping references are rejected. Subsequent stages reject changed source evidence.

Outputs are `drift-<documentId>.json` and `proposed-<documentId>.md`. These are local review artifacts; repository files are not edited. The commands are compared as strings and never executed. The sample verifies existence of setting keys, not their runtime semantics.

```powershell
dotnet run --project samples/agents/52-DocumentationDriftDetector -- --demo
dotnet run --project samples/agents/52-DocumentationDriftDetector -- --self-test
```

Six tests cover all reference types and line locations, text preservation, unresolved replacements, incomplete/malformed snapshots, ambiguous occurrences, multiple IDs/repeats and stale/stage guards.
