# Pinned task-local agent oracle dependency setup

The task-local dependency setup is **completed**. Scoped executor approval was accepted and `setup.mjs --execute` exited 0: verified npm 11.6.2 and restored exactly typebox 1.3.27 plus partial-json 0.1.7 with scripts disabled. The dedicated `upstream` clone remains clean at its pinned commit. Runtime oracle qualification is in progress; installing dependencies does not qualify captures. Production source commit `c45e3008f818df3ece3bf045aa30cf13666929af` remains the independently accepted bounded native baseline.

The reviewable [setup helper](../../tools/PiReferenceRunner/setup.mjs) defaults to an offline preflight. Syntax checking and that preflight passed: the selected Node binary, clean source commit/lock and exact two-package projection match their pins. It writes no files in default mode. The actual approved action is:

```powershell
& 'P:\PiSharp\root\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' tools/PiReferenceRunner/setup.mjs --execute
```

The helper rejects an existing oracle directory without its own ownership marker, preserves changed project/config inputs, verifies the npm archive before extracting it, uses a separate local source clone without hardlinks, checks the exact npm version, disables lifecycle scripts and validates installed package names/versions/metadata plus unchanged source/lock state. Its setup result is recorded in the dedicated workspace's `.pisharp-oracle-setup.json`; the tracked installation plan contains the exact acquired archive and installed manifest hashes. No package scripts, native builds, provider calls or system/global changes occurred.

## Concrete proposed setup

Use `P:\PiSharp\root\Documents\Codex\2026-09-30\task-2\Pi-reference-oracle-v0.99.1` as a dedicated project. Copy the prepared [manifest](../../compatibility/reference-oracle-dependencies/package.json) and [lock projection](../../compatibility/reference-oracle-dependencies/package-lock.json) to its root. Acquire a fresh local Git clone under its `upstream` subdirectory from the already acquired public checkout, and verify clean HEAD `d86654abb8862e201933517d6f1fce9f88dd117f`. Leave the original reference checkout and PiSharp source unchanged.

Reuse the already locked Node `v24.19.0` Windows x64 executable. Obtain **npm 11.6.2** from the [official registry metadata](https://registry.npmjs.org/npm/11.6.2), download `https://registry.npmjs.org/npm/-/npm-11.6.2.tgz`, verify the recorded SHA-512 integrity, and unpack its bundled CLI under `tools/npm-11.6.2`. Invoke `package/bin/npm-cli.js` through the absolute existing Node path; do not install a global npm command or edit PATH. npm's declared Node requirement is `^20.17.0 || >=22.9.0`, which the installed runtime meets. The complete bootstrap pin is in [reference-oracle-install-plan.json](../../compatibility/reference-oracle-install-plan.json).

The project restores only **typebox 1.3.27** and **partial-json 0.1.7**. The lock projection retains their exact versions, official registry tarball URLs and SHA-512 integrity values from upstream `package-lock.json`, SHA-256 `e245cabcefdd23d1ab3cfd21db492e21ca11b7ef4a20f6304aead4dc2b5b569d`. Read-only registry metadata inspection on 2026-09-30 confirmed both integrity values, no transitive dependencies and no install lifecycle scripts. This is an explicit subset, not a full monorepo restore or a replacement for upstream's full lock.

After archive verification and config-directory preparation, the concrete restore is:

```powershell
& $Node "$Oracle/tools/npm-11.6.2/package/bin/npm-cli.js" ci `
  --prefix $Oracle --ignore-scripts --no-audit --no-fund --bin-links=false `
  --registry=https://registry.npmjs.org/ --cache "$Oracle/cache" `
  --userconfig "$Oracle/config/user.npmrc" --globalconfig "$Oracle/config/global.npmrc"
```

`$Node` is the absolute preinstalled executable and `$Oracle` is the dedicated path above. Empty task-local user/global config files, home and temporary directories replace ambient npm configuration through a credential-free subprocess environment. All downloaded archives, cache/logs, npm files, dependencies, source clone and future fixture outputs stay inside the dedicated project. Network access during acquisition is limited to `registry.npmjs.org` for three exact archives and any normal registry metadata required by npm; no GitHub asset downloads or provider endpoints are required for this subset. Capture archive/extracted package hashes and verify the upstream clone remains clean before reference execution.

No lifecycle scripts run (`--ignore-scripts`), no C/C++/Rust/native builds run, and no TypeScript build or model-catalog regeneration runs. The oracle uses Node's built-in TypeScript stripping and the unchanged upstream `packages/coding-agent/src/experimental/source-resolver.ts`. Standard package lookup reaches the installed ancestor `node_modules`; no external package substitution hook is needed. The original monorepo's Husky prepare hook and eight lock entries marked with install scripts are excluded by the subset. Nothing changes system settings, global package locations, security policy or permanent PATH; no credentials or paid calls are needed.

## Evidence and limits

An advisory stripped-source import scan of `runAgentLoop` and the assistant-frame reducer reached 28 upstream modules with only `typebox`, `typebox/compile`, `typebox/value` and `partial-json` external imports; no static source imports were unresolved. [Recorded graph](../../compatibility/reference-oracle-dependency-graph.json) includes source hashes. Dynamic auth-context imports are Node builtins; lazy OAuth loading is not invoked by an explicitly injected fake stream. The scan is not a full JavaScript parser or runtime load trace. Runtime module tracing, offline guards and twice-repeatable captures must qualify the subset before parity evidence is admitted.

This setup enables unchanged awaited `runAgentLoop` execution with supported injected fake provider/tool seams, and genuine assistant-frame reduction captures. It advances P1-06 through P1-08, stream/tool JSON characterization in P2-03/P2-07, and tool ordering/barrier scenarios in P3-01/P3-03. Snapshot mutable events at emission, fix clock/IDs, omit credential environment and disable network/process execution during captures.

It does not qualify provider HTTP requests, production auth, released catalog/package artifacts, the whole coding-agent/session/RPC runtime, all mandatory requirements, or cross-platform behavior. The 45 mandatory Deferred requirements and full phase/parity gates remain open.

## Required approval path

Default executor sockets are restricted. Scoped escalation first permitted read-only registry metadata; a subsequent scoped escalation permitted the prepared exact archive acquisition/bootstrap/restore. Both approvals were accepted. No approval review rejected an action, and no user approval is outstanding. The authorized ordinary project restore required no separate product policy confirmation.

Completed approval scope: **Task-local npm 11.6.2 bootstrap and restore of the two upstream-locked packages, typebox 1.3.27 and partial-json 0.1.7, from the official npm registry under the dedicated task-2 oracle directory, with scripts disabled and no global/PATH changes. This enables offline agent-loop and stream-reducer reference captures.**
