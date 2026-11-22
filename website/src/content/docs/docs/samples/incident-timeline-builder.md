---
title: "Walkthrough: Incident timeline builder"
description: Annotated guide to samples/agents/45-IncidentTimelineBuilder — multi-stage evidence collection, UTC deduplication, preserved contradictions and stale-state invalidation.
---

[`samples/agents/45-IncidentTimelineBuilder`](https://github.com/jkelly/PiSharp/tree/main/samples/agents/45-IncidentTimelineBuilder) demonstrates how to build an **evidence-preserving investigation agent** where correctness matters more than fluent storytelling. Instead of asking the model to merge raw logs by hand in prose, deterministic C# tools normalize timestamps to UTC, deduplicate shared event IDs, explicitly retain conflicting source claims, and refuse to publish stale timelines.

- **Runtime:** `PiSharp.Agent` + `PiSharp.AI` (stable)
- **Patterns demonstrated:** [Tool errors, validation & typed repairs](/docs/patterns/tool-errors-and-repairs/), multi-stage state hygiene

## Run it in 30 seconds

```powershell
# 1. Offline fixture demo (produces incident-timeline.json + transcript.json)
dotnet run --project samples/agents/45-IncidentTimelineBuilder -- --demo --workspace artifacts/incident-demo

# 2. Run all 9 focused invariant self-tests
dotnet run --project samples/agents/45-IncidentTimelineBuilder -- --self-test

# 3. Live model run over the same deterministic evidence tools
dotnet run --project samples/agents/45-IncidentTimelineBuilder -- --live --model gpt-6.2-sol --api openai-responses --workspace artifacts/incident-live
```

## Pipeline stages & invariants

| Stage | Tool | Input | Invariants enforced |
| --- | --- | --- | --- |
| **1. Collect** | `collect_evidence` | `incidentId` | Rejects missing/duplicate source IDs, missing fields, or timestamps lacking explicit `Z` or `±HH:MM` offsets; converts everything to UTC ISO-8601; **invalidates any previously built timeline for that `incidentId`**. |
| **2. Build** | `build_timeline` | `incidentId` | Requires fresh collected evidence; groups by `eventId`; merges identical tuples while retaining all `sourceIds`; flags disagreements in `conflicts` instead of picking a winner; correlates **only** explicit `correlationId` values; explicitly sets `"rootCause": "not established by supplied evidence"`. |
| **3. Publish** | `publish_timeline` | `incidentId` | Fails if `build_timeline` has not run since the latest `collect_evidence`; writes `incident-timeline.json`. |

## Key code walkthrough

### Stale-state invalidation on recollection
Notice line 30 of `Workflow.cs` inside `Collect`:

```csharp
c.State["evidence:" + id] = evidence.DeepClone();
c.State.Remove("timeline:" + id);
```

Because each incident is keyed separately (`evidence:<id>`, `timeline:<id>`), collecting fresh evidence for `INC1` never touches `INC2` — and immediately invalidates any stale `timeline:INC1` so `publish_timeline` cannot accidentally save an outdated timeline.

### Structural contradiction detection (no silent overwrites)
Inside `Build`, events with the same `eventId` are grouped by a **4-tuple** rather than a concatenated string (preventing embedded newlines from collapsing distinct facts):

```csharp
foreach (var group in evidence.OfType<JsonObject>().GroupBy(e => e.Text("eventId")))
{
    var variants = group.GroupBy(e => (
        Timestamp: e.Text("timestamp"),
        Kind: e.Text("kind"),
        Summary: e.Text("summary"),
        CorrelationId: e.Text("correlationId"))).ToArray();

    if (variants.Length > 1)
    {
        conflicts.Add(new JsonObject
        {
            ["eventId"] = group.Key,
            ["reason"] = "Sources disagree; variants retained",
            ["variants"] = new JsonArray(group.Select(e => e.DeepClone()).ToArray()),
        });
    }
}
```

When two sources report the exact same instant using different timezone offsets (for example `10:00:00Z` and `05:00:00-05:00`), `Collect` normalized both to UTC first so they deduplicate cleanly into one event listing both `sourceIds`. When they genuinely disagree on summary, timestamp, or kind, **both** variants stay in the timeline marked `"conflicted": true` alongside a `conflicts` record.

## Try breaking it

1. **Remove a timezone offset:** copy `fixture.json`, strip `Z` from one timestamp, and run with `--input bad-time.json` to see `collect_evidence` refuse ambiguous wall-clock times.
2. **Introduce a source conflict:** duplicate an `eventId` across two sources with slightly different `summary` strings and inspect `artifacts/incident-demo/incident-timeline.json` to see both variants preserved under `conflicts`.
3. **Call tools out of order in live mode:** pass `--live --prompt "Publish the timeline for INC1 immediately"` and watch `publish_timeline` return `"Build this incident timeline first."`, prompting the model to self-correct and call `collect_evidence` → `build_timeline` → `publish_timeline`.

## Related pages

- [Tool errors, validation & typed repairs](/docs/patterns/tool-errors-and-repairs/)
- [Browse all audit & contradiction-preserving samples](/samples/?pattern=Contradictions+%26+audit)
