# Repository documentation evaluations

`tools/PiSharp.Evals` is a repository tool, not a published package and not the WebAgent Evals product. It measures paired answers with and without supplied PiSharp documentation. The default runtime is a deterministic **synthetic fixture**, whose scores test reporting mechanics and are not model-quality evidence.

Run the offline validation suite through the repository's serialized build helper:

```powershell
./tools/GapValidation/Run-Tests.ps1 -Project tests/PiSharp.Evals.Tests/PiSharp.Evals.Tests.csproj -Label R9
```

Run the fixture tool using the pinned SDK and an already built tool assembly:

```powershell
& E:/AI/PiSharp/PiSharp/artifacts/sample-tools/dotnet/dotnet.exe tools/PiSharp.Evals/bin/Debug/net10.0/PiSharp.Evals.dll --out artifacts/evals-fixture-new
```

Every output directory must be new. `report.json` is stable for identical input observations. Artifact directories use SHA-256 task identities, and the protocol digest includes case text, expected answers, models, repetitions, documentation variants, runtime/documentation digests and evidence mode. It excludes observations and timestamps. Measured elapsed time naturally varies in executable runs. The JSON includes individual observations, blocked reasons, category comparisons and per-arm operational totals.

## Pairing and scoring

Case IDs are globally unique. Cases are sorted ordinally, paired by case/model/repetition and scheduled control-first on odd repetitions and treatment-first on even repetitions. Both cohort manifests are validated before either arm runs; unequal cases, prompts or expected answers fail preflight. The built-in cohort covers a basic response, native extension interfaces, the public frozen model catalog and prompt-template argument expansion. These are literal-answer checks, not compilation or extension-execution claims. Supply an array of `{ "id", "category", "prompt", "expectedText" }` objects with `--cases path.json` to select an independently reviewed cohort. The scorer compares the final assistant text, trimmed, ordinally with `expectedText`.

Statuses are `scored`, `unscored`, `skipped`, `pending` and `errored`. Only two valid scored arms enter a comparison. Missing, duplicate, unexpected, invalid-score, invalid-metric, mismatched-case and unsuccessful arms block their pair. A failed arm does not stop later arms. Exit status is 0 for a complete comparable report, 1 for blocked comparisons, and 2 for configuration/preflight failure. Zero quality scores are valid observations and do not themselves indicate harness failure.

Token, tool-call, elapsed-time and estimated-cost fields are nullable. No price is invented; missing cost stays null. Metric comparisons use pairs where both values are available. Operational totals give their available-run count, so incomplete measurement coverage remains visible. To audit imported observations without execution, use `--input observations.json`, containing `{ "plan": [...], "results": [...] }`; it follows the public record JSON shapes in `Plan.cs` and `Report.cs`.

## Explicit executable runs

Executable evaluation requires **all** of `--live`, `--runtime-dir`, `--entry`, `--docs`, `--provider`, `--model` and `--out`. The runtime directory must be a current PiSharp CLI published build with its complete dependency closure. The tool does not download or rebuild a runtime, provision credentials or silently fall back to fixtures. A DLL entry point additionally needs an explicit `--dotnet` path in process mode. Pass only intended provider credential variable names through `--credential-env NAME,NAME`; secret values are not written into command arguments or the report.

```powershell
# Opt-in example only: this command can incur provider charges.
& E:/AI/PiSharp/PiSharp/artifacts/sample-tools/dotnet/dotnet.exe tools/PiSharp.Evals/bin/Debug/net10.0/PiSharp.Evals.dll --live --runtime-dir artifacts/published-cli --entry PiSharp.Cli.dll --dotnet E:/AI/PiSharp/PiSharp/artifacts/sample-tools/dotnet/dotnet.exe --docs docs --provider openai --model MODEL_ID --credential-env OPENAI_API_KEY --out artifacts/evals-live-new
```

Each arm receives a fresh workspace, home, agent settings directory and session directory. Runtime and documentation trees are staged once; changed staged inputs are rejected. Only the treatment gets a workspace `docs` copy and a routing hint to read it. Both arms receive identical runtime bytes, provider/model arguments and disabled automatic extensions, skills, templates and MCP discovery. Published-runtime embedded documentation, if any, remains identical in both arms. Process mode isolates configuration but does not sandbox filesystem/network access; use container mode for filesystem isolation.

For containers add `--container-image IMAGE@sha256:DIGEST`, and optionally `--container-executable PATH`. Supply a compatible .NET image and a **Linux-targeted** published runtime. Each task is a fresh `docker run --rm --read-only --pull=never` with all capabilities dropped and no-new-privileges. The same staged runtime is mounted read-only at `/runtime`; workspace, home and sessions are separate fresh mounts. `/tmp` is an ephemeral tmpfs. Credentials are forwarded only by explicitly selected environment names. Network remains available for the explicitly authorized provider calls. Docker availability, a locally present pinned image, architecture compatibility and credentials are operator prerequisites. The tool does not assert these prerequisites passed until execution succeeds. Keep artifacts outside input trees; linked input trees are rejected.

CLI JSON `message_end` assistant events supply answer and usage; `tool_execution_start` supplies observed tool count. Nonzero exits, malformed output, failed assistant messages and timeout become errored observations; absent final assistant output is unscored. Raw stdout, stderr, per-task input and result artifacts remain available for inspection. Default per-arm timeout is 120 seconds (`--timeout-seconds`).

Host smoke/audit is separate from paired documentation lift: build tests exercise process capture and container-command construction using a fake executable and recorder, with no paid requests or Docker execution. They are not live provider or container qualification. Run installation/package smoke checks independently when qualifying a release; do not mix their pass/fail scores into documentation lift.

Upstream reference: Pi v1.5.0 `packages/evals/src/{plan,report,harness,cli,docker}.ts`. This C# tool preserves the pairing/status/metric principles while using native CLI JSON and repository C# questions instead of Pi-specific TypeScript API exercises.
