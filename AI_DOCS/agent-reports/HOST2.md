# HOST2 — deterministic startup-shutdown fixture acquisition

Status: test-only correction complete; central repeated focused execution and the full 14-case Hosting suite remain pending. Requested model/reasoning: gpt-6.1-sol/high; requested Ultrafast tier, effective tier unverified. No .NET build or test was run by this worker.

## Observed evidence

Read the actual Windows f473bef artifact at `artifacts/release-prep/candidate-ci/f473-gap-chord-windows/chord/c05dd5be3a44470f964240126caa398d/PiSharp.Chord.Hosting.Tests.tests.{stdout,stderr}.log`. It reports 14 cases, 13 passed, with only `workers.startup-shutdown-closes-connected-host` failing the native close marker assertion at Program.cs:597. The other real worker lifecycle case passed. This is evidence of the marker failure, not proof by itself of a runtime cleanup defect.

## Source proof and correction

The original child fixture served Bootstrap with a native open, and independently served Remote with only a ParentService dependency. The parent's remote PID call could therefore finish before Bootstrap acquired a native resource. The `bootstrapped` signal inside the open callback was also premature: `NativeHandle.OpenAsync` assigns its acquired value and `state = "open"` only after the callback returns. Waiting for that signal would not prove committed acquisition.

Read the real lifecycle implementation:

- `src/PiSharp.Chord/Host/Native.cs`: native state becomes `open` after the awaited open callback returns. Closing an `unopened` native releases its lease without calling the resource close delegate.
- `src/PiSharp.Chord/Host/Attempt.cs`: `Self.Get` records a retained dependency, activation waits for dependencies to be live, and own native opens finish before `Attempt.Live`/`Owner.WentLive`.
- `src/PiSharp.Chord/Host/Reference.cs`: `Binding.ReadyAsync` returns only for a live, hydrated target.
- `src/PiSharp.Chord/Host/Host.cs`: `GetAsync` awaits that reference-ready barrier.
- `src/PiSharp.Chord.Hosting/Workers/WorkerMain.cs`: shutdown closes the connected host even while initialization remains unfinished. No source defect was established here, and this file is unchanged.

Only `tests/PiSharp.Chord.Hosting.Tests/Program.cs` changes executable code:

1. Remote explicitly retains Bootstrap through `self.Get(Bootstrap, new())`. Remote can no longer become ready or answer PID until Bootstrap's native acquisition has completed and its target is live.
2. The initializer awaits `host.GetAsync(Bootstrap, new(), worker.Context)` and holds that reference, replacing the signal inside the open callback.
3. The existing `--hold-startup` gate remains. The parent still asserts the worker is `starting`, then checks native close marker contents and absence of the child PID after bounded close. No assertion, deadline, native close requirement or process ownership check was removed.
4. Optional `--only <exact case name>[,<exact case name>...]` selects existing identities for bounded repeated reproduction. Unknown/empty selections are rejected. No arguments still execute the same full 14 cases; the JSON count reflects the cases actually selected. Child fixture entry modes run before selection parsing as before.

This is a reference-readiness barrier, not a sleep or a marker emitted before the resource was acquired. No runtime change, native timing increase or platform skip was added.

## Actual checks and handoff

Scoped `git diff --check -- tests/PiSharp.Chord.Hosting.Tests/Program.cs` passed. Reviewed the complete test diff against the lifecycle sources above. Builds/execution remain exclusively with main; no local or hosted runtime success is claimed for this correction.

Set `PISHARP_TEST_DOTNET` to the pinned host, build Hosting.Tests once, then repeat:

```text
<pinned-dotnet> tests/PiSharp.Chord.Hosting.Tests/bin/Release/net10.0/PiSharp.Chord.Hosting.Tests.dll --only workers.startup-shutdown-closes-connected-host
```

After repeated focused success, invoke the same DLL without `--only` and require all 14 Hosting cases. The required hosted gap gates remain full-suite gates. The source freeze/checkpoint is this Program.cs diff plus this report; no Git index/branch, remote, general Tools, shared JSON, runtime WorkerMain or memory changes were made.
