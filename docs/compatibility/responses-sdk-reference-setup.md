# Isolated Responses SDK reference setup

**Executed setup update:** the reviewed helper at `afd32714f6043716492a826f50e54dbeb189a28e` completed local preparation and an executor-approved registry restore, both exit0. Exactly OpenAI7.19.0, partial-json0.1.7 and typebox1.3.27 are installed in the fresh workspace below; all4942 installed files match their inspected SRI-pinned archives. SDK archive SHA-256 is `597532dcb9051ee8aa5edadedb57b798b42add68cc5aec1d67e994d0cfad07f1` (2781465bytes), and its packaged Apache2.0 license plus vendored notices are retained. No scripts/native/global/system changes, credentials or provider calls occurred. The existing two-dependency oracle stayed unchanged. [Executed receipt](../../compatibility/responses-sdk-oracle-setup.json) records exact commands, source conversions and archive/installed-tree pins. Genuine wrapper import/runtime closure/repeatability remain pending.

The remaining text preserves the research proposal at `c292f887de3578d280b87d0451138cab2ae9cfac`; its no-install statements describe that earlier checkpoint. The original installation-plan bytes remain immutable.

This is a research-only setup plan. OpenAI 7.19.0 official registry metadata matches the pinned Pi AI lock entry, but its archive has not been acquired, dependencies have not been restored, and no full Responses wrapper capture has run. The existing qualified two-dependency oracle remains immutable. The reviewable [installation plan](../../tools/PiReferenceRunner/responses-sdk-install-plan.json) contains exact versions, archive URLs, SHA-512 integrity values, source/runtime hashes, proposed arguments and pending qualification checks.

## Exact package authority and closure

Use public Pi commit `d86654abb8862e201933517d6f1fce9f88dd117f` and its unchanged `package-lock.json`, SHA-256 `e245cabcefdd23d1ab3cfd21db492e21ca11b7ef4a20f6304aead4dc2b5b569d`. The relevant SDK entry is **`packages/ai/node_modules/openai` at 7.19.0**. The root `node_modules/openai` entry is a different, development-only 6.40.0 dependency and is excluded. [Pinned upstream lock](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/package-lock.json).

| Package | Exact version | Original lock entry | Declared license |
| --- | --- | --- | --- |
| openai | 7.19.0 | `packages/ai/node_modules/openai` | Apache-2.0 |
| partial-json | 0.1.7 | `node_modules/partial-json` | MIT |
| typebox | 1.3.27 | `node_modules/typebox` | MIT |

The latter two packages are already required by the unchanged Pi source closure and installed in the separate accepted oracle. OpenAI 7.19.0 declares no mandatory dependencies or optional dependencies. Its six peers are all optional: `@aws-sdk/credential-provider-node` (`>=3.972.0 <4`), `@smithy/hash-node` (`>=4.3.0 <5`), `@smithy/signature-v4` (`>=5.4.0 <6`), `undici` (`>=5 <9`), `ws` (`^8.21.0`) and `zod` (`^3.25 || ^4.0`). The proposed profile omits all six. [Official version metadata](https://registry.npmjs.org/openai/7.19.0).

The anonymous metadata read succeeded through scoped executor approval on 2026-09-30. The response was 7,831 bytes with SHA-256 `21b1512f309d0a0bd5a7e221dadac46d2f0ebfddce41c8cb08673365cc74a984`; selected fields and the body hash are recorded in the plan, while the full body was not retained in this two-file research scope. It declares SDK gitHead `15f6e408a0359df50ee7a996616957cc14988659`, 3,548 packaged files and 18,477,473 unpacked bytes. Those are publisher metadata declarations rather than observations of acquired SDK bytes. Registry signature/attestation verification and packaged license inspection remain pending.

Three packages are the **proposed minimal package profile**, not an observed successful runtime closure. The SDK root exports additional clients and resources, so optional peer declarations alone do not establish import readiness. The first genuine import must expose any missing prerequisite; no mocked module or custom replacement resolver may hide it. [Official SDK entry at the metadata-declared revision](https://github.com/openai/openai-node/blob/15f6e408a0359df50ee7a996616957cc14988659/src/index.ts).

## Fresh task-local workspace and restore

Proposed workspace: `P:\PiSharp\root\Documents\Codex\2026-09-30\task-2\Pi-responses-sdk-oracle-v0.99.1`. It did not exist at research time. Place a fresh local Git clone without hardlinks in `upstream`, pinned to the source commit above. Use owned `archives`, `tools`, `cache`, `config`, `home`, `tmp` and `captures` directories. Preserve `Pi-reference-oracle-v0.99.1`, its source, installed dependencies, accepted locks and goldens.

Reuse the absolute Node v24.19.0 executable and its recorded SHA-256. Copy the existing verified npm 11.6.2 archive into the new workspace; verify its SHA-512 and SHA-256 before inspecting member names/types and confined link targets, then unpack only its bundled CLI under `tools/npm-11.6.2`. The existing archive is 2,663,834 bytes, SHA-256 `585f95094ee5cb2788ee11d90f2a518a7c9ef6e083fa141d0b63ca3383675a20`. Copy cache contents into the new owned cache and let npm verify content integrity; a writable cache must never point at the accepted oracle's cache.

Construct a new root manifest with exactly the three dependencies above. Build lockfile-version 3 by retaining the exact upstream package entry objects; relocate only the selected SDK entry key to `node_modules/openai`. This changes the isolated install layout and preserves the original lock, source and package bytes. The source resolver continues to map upstream workspace aliases and delegates external packages to ordinary Node resolution.

After the new workspace/config/lock are reviewed, the proposed restore arguments are:

```powershell
& $Node "$SdkOracle/tools/npm-11.6.2/package/bin/npm-cli.js" ci `
  --prefix $SdkOracle --ignore-scripts --omit=optional --omit=peer `
  --no-audit --no-fund --bin-links=false --registry=https://registry.npmjs.org/ `
  --cache "$SdkOracle/cache" --userconfig "$SdkOracle/config/user.npmrc" `
  --globalconfig "$SdkOracle/config/global.npmrc"
```

`$Node` is the absolute pinned executable; `$SdkOracle` is the proposed new directory. This is a reviewable future command, not an executed setup helper. Use an explicit credential-free child environment with task-local home, appdata, temporary files and empty npm user/global configs. Do not inherit API/cloud/provider/proxy credentials, user npm configuration, `NODE_OPTIONS` or certificate overrides. No lifecycle scripts, native builds, TypeScript builds, catalog generation, global installations, permanent PATH changes or system configuration changes are included.

The one newly required archive is `https://registry.npmjs.org/openai/-/openai-7.19.0.tgz`, with the exact upstream SHA-512 SRI recorded in the plan. Cache misses may require the two exact previously approved package archives, also recorded there. Reused npm needs no expected download. Network acquisition is limited to the official npm registry for this concrete package profile. A setup decision is pending; if executor sockets require escalation, submit that exact acquisition/restore to the applicable auto-review. No rejection occurred during the metadata research and no installation approval was exercised.

## Genuine wrapper invocation boundary

Import the unchanged `packages/ai/src/api/openai-responses.ts` and invoke its exported `stream` (or subsequently `streamSimple`). Its runtime SDK import is `openai`; the resource-type import is erased by Node TypeScript stripping. The upstream `createClient` and `buildParams` functions remain private and execute internally. Calling a copied builder, injecting private exports, recompiling source or substituting an SDK shim would not qualify this boundary. [Pinned wrapper source](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/ai/src/api/openai-responses.ts).

The first profile can supply a small authored model/transcript, a synthetic `.invalid` base URL, an inert noncredential API-key-shaped marker, deterministic supported session/header options and `maxRetries: 0`. No user API key, login, workload identity or provider registration is needed. Observe the genuine assembled params through awaited `onPayload`, snapshot them and return `undefined` to preserve the original result. Inject supported `options.fetch` to record the actual SDK request method, URL, headers and serialized body, then return an in-memory `Response`/`ReadableStream` containing authored recorded SSE bytes. Awaited `onResponse` and `onProviderStreamEvent`, the emitted assistant frames and final result can then capture the unchanged full wrapper/SDK behavior. Authored input and wire bytes remain distinct from genuine observations.

Reuse the locked existing observer/offline guard and unchanged source resolver. The capture child blocks real fetch/socket/DNS/HTTP and process execution, and snapshots mutable values at each boundary. Verify that every external module resolves inside the new oracle, hash the full installed package trees and actual loaded modules, and report actual counts only after execution. Capture twice with fixed clock and supported header/session inputs; any remaining SDK-generated random or host-specific values must be resolved through supported options or reported, without silently normalizing opaque data. Raw numbers must retain their JSON lexemes where the observation is raw JSON; values already materialized by JavaScript remain subject to its numeric precision.

Before evidence admission, verify source cleanliness and canonical bytes before/after, exact dependency closure and packaged notices, unchanged projected lock, successful genuine SDK import, credential-free/offline execution, and repeatability. A new capture must refuse existing goldens and lock input/output/harness/runtime/dependency/source bytes. No captures or installed-file counts are claimed by this plan.

OAuth, AWS/SigV4, X509, optional-peer functionality, WebSockets, custom undici transport, zod helpers, live provider authentication and full provider parity remain outside this first proposed profile. The full phase gates remain open.
