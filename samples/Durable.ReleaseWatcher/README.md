# Durable.ReleaseWatcher

The reference release watcher from [decision 0010](../../docs/decisions/0010-durable-application-contracts.md) (plan
[09](../../docs/plans/09-durable-remaining-features.md), M5). It watches release feeds and GitHub releases, judges each new or changed
release under a named policy, sends medium-confidence judgments to a person, researches the risky ones, and takes feedback that tunes
later judgments.

The plan proposed `samples/durable/ReleaseWatcher`; the sample lives in `samples/Durable.ReleaseWatcher` to match the other samples
(`Durable.Chat`, `Durable.Examples`).

## It is an ordinary client

The watcher holds no privileged path into a device. Every step is a public v1 route called through `DurableDeviceClient`, so anything it
does, another client can do the same way:

| Step | Client method | Route |
| --- | --- | --- |
| Check the device offers what the watcher needs | `DeviceAsync` → `DeviceInfo.Supports` | `GET /v1/device` |
| Find or create each source | `SourceAsync`, `CreateSourceAsync` | `GET /v1/sources/{id}`, `POST /v1/sources` |
| Publish the policy, keyed by the spec's hash, and activate it | `PublishPolicyAsync`, `PolicyAsync`, `ActivatePolicyAsync` | `POST /v1/policies/{id}/versions`, `GET /v1/policies/{id}`, `PUT /v1/policies/{id}/active` |
| Find or create the session | `SendAsync` with an Idempotency-Key | `POST /v1/sessions` |
| Find or create the source check schedule | `SchedulesAsync`, `CreateScheduleAsync` | `GET /v1/schedules`, `POST /v1/schedules` |
| Issue a trigger credential restricted to that schedule | `SendAsync` | `POST /v1/trigger-credentials` |
| Tick the schedule (trigger credential only) | `TickScheduleAsync` | `POST /v1/schedules/{id}/tick` |
| Read the source checks and their evidence lists | `ExecutionsAsync`, `ExecutionArtifactsAsync` | `GET /v1/sessions/{s}/executions`, `GET .../executions/{task}/artifacts` |
| Read a release | `EvidenceAsync`, `EvidenceTextAsync` | `GET /v1/sessions/{s}/evidence/{id}`, `GET .../evidence/{id}/text` |
| Judge a release, keyed by its evidence ID | `StartJudgmentAsync` | `POST /v1/sessions/{s}/judgments` |
| List judgments, approve or reject one | `JudgmentsAsync`, `JudgmentAsync`, `DecideJudgmentAsync` | `GET /v1/sessions/{s}/judgments[/{id}]`, `POST .../judgments/{id}/decision` |
| Research an approved risky release, keyed by its judgment | `StartResearchAsync` | `POST /v1/sessions/{s}/research` |
| Read artifacts, executions, usage and budgets | `ArtifactsAsync`, `ExecutionAsync`, `ExecutionUsageAsync`, `BudgetsAsync` | `GET .../artifacts`, `GET .../executions/{task}`, `GET .../executions/{task}/usage`, `GET /v1/budgets` |
| Give feedback | `SendFeedbackAsync` (the request ID is the Idempotency-Key) | `POST /v1/sessions/{s}/artifacts/{id}/feedback` |

The device does the durable work in three built-in jobs: `pisharp-source-check` fetches the sources, dedupes items by content hash and
keeps new or changed ones as evidence; `pisharp-judge` judges one release, waits for a person when the policy's confidence route asks for
one, and keeps a finding; `pisharp-research` researches an approved release from its evidence, one finding per angle and a synthesis.

## No duplicates

Evidence is content addressed, and the watcher starts each judgment with the release's evidence ID as its Idempotency-Key. An unchanged
release, a repeated pass, or a pass after a restart replays the first judgment instead of starting another; a changed release has new
evidence and is judged again. Research is keyed by its judgment the same way, the session by the configured name, and the policy by the
hash of its spec, so running `setup` again finds what the first run made.

## The offline demo

```bash
dotnet run --project samples/Durable.ReleaseWatcher -- demo
```

The demo hosts a device (`DurableDeviceHost` behind `DurableDeviceServer`) and a recorded feed (`fixtures/releases-1.xml`, then
`releases-2.xml`) on loopback, with a manual clock and a faux classifier, so it needs no network, provider or key. Only starting the device
uses hosting APIs; the scenario itself goes through `ReleaseWatcher` and `DurableDeviceClient`:

1. Set up the watcher and issue a trigger credential restricted to its schedule.
2. Tick with that credential: 1.0.0 is judged `ignore` and 1.1.0 `breaking`, both automatically, and 1.1.0 is researched.
3. The feed changes 1.0.0's notes and adds the security fix 1.2.0. Tick: both are judged; 1.2.0's 0.70 confidence routes it to review and
   the demo approves it.
4. Read the reviewed judgment's usage and today's budget, check every completion audit, and give feedback (more on the breaking finding;
   less, undo, less on an ignored one, with a retried request replaying).
5. Restart the device and set up again, which finds the same session, policy version and schedule. Tick the unchanged feed: the check
   keeps no evidence list, and the next pass replays every judgment, so no artifact or judgment is duplicated.

It prints a JSON report. `tests/PiSharp.Durable.Server.Tests` runs the same scenario (`release-watcher.offline-scenario`) and checks it.

## Against a device

Copy `watcher.sample.json`, point `cwd` at one of the device's start directories, and list your sources. The configuration holds no
secrets: a private source names a `credentialRef` that the device resolves from its own configuration, the administrative credential is
read from `PISHARP_DEVICE_TOKEN`, and `tick` reads the trigger credential from `PISHARP_TRIGGER_TOKEN`. Neither is ever an argument.

```bash
dotnet run --project samples/Durable.ReleaseWatcher -- setup --device http://127.0.0.1:4792 --config watcher.json --issue-token
```

```bash
dotnet run --project samples/Durable.ReleaseWatcher -- tick --device http://127.0.0.1:4792 --schedule <schedule id>
```

```bash
dotnet run --project samples/Durable.ReleaseWatcher -- process --device http://127.0.0.1:4792 --config watcher.json
```

`status` lists the last check, the runs and the judgments waiting for review; `review <judgmentId> approve|reject [--reason <text>]`
decides one; `feedback <artifactId> more|less|dismiss|undo [--labels <a,b>] [--request <id>]` records feedback in the configured profile.
A device that runs its own schedules (the default) ticks the schedule itself; `tick` is for a device started with schedules off, or for
running the check now from cron or CI with only the trigger credential.

The watcher never edits dependencies, sends messages, publishes or installs anything. It does not read a dependency manifest; a
configuration lists what to watch.
