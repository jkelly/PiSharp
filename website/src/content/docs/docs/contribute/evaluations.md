---
title: Evaluation tooling
description: Run deterministic fixtures, compare observations, or explicitly execute a staged runtime.
---

PiSharp 1.5.0.1 source includes [`tools/PiSharp.Evals`](https://github.com/jkelly/PiSharp/tree/main/tools/PiSharp.Evals). It is a repository tool, not a NuGet library. Its default mode runs deterministic synthetic fixtures without starting a CLI process or contacting a provider:

```powershell
dotnet run --project tools/PiSharp.Evals -- --repetitions 2 --out artifacts/eval-fixture-check
```

The output directory must be fresh. `report.json` identifies fixture results; synthetic observations do not prove runtime or provider behavior. `--cases cases.json` supplies validated cases. `--input observations.json` compares a caller-supplied `{ plan, results }` without execution; imported provenance remains the caller's responsibility.

## Staged runtime mode

`--live` explicitly enables execution. It requires `--runtime-dir`, `--entry`, `--docs`, `--provider`, `--model` and a fresh `--out`. Managed entry points use `--dotnet`. Optional controls include `--timeout-seconds`, an explicit `--credential-env NAME,NAME` allowlist, and `--container-image image@sha256:digest` with `--container-executable` (default `docker`). See the [argument parser](https://github.com/jkelly/PiSharp/blob/main/tools/PiSharp.Evals/Program.cs) and [staged runtime](https://github.com/jkelly/PiSharp/blob/main/tools/PiSharp.Evals/StagedRuntime.cs) before executing a model.

Runtime/provider options require `--live`; imported comparisons cannot be mixed with execution options. Reports preserve per-case results and process evidence, and execution owns bounded process lifetimes. A passing fixture report is not a live result. Missing credentials, skipped cases and failed processes must remain visible in qualification.

## Release evidence

The [gap regression workflow](https://github.com/jkelly/PiSharp/blob/main/.github/workflows/gap-regressions.yml) and native daemon workflow retain exact checkout identity and per-case evidence. Confirm the source SHA and required cases, not just the existence of a report file. [Releasing](/docs/contribute/releasing/) describes how these checks join core and Durable validation.
