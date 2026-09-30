# Phase 8 Hardening and packaging

## Outcome and boundaries

Qualify a reproducible, supportable native PiSharp release on .NET 10, with native C# extensions and a verified Node-free installation. Start CI, platform smoke tests, dependency inventories and security review in phase 1; phase 8 closes their evidence and remaining parity gaps. The optional phase 7 bridge has its own package and gate. A bridge delay cannot block an otherwise qualified native release.

Source facts: the pinned Pi repository license is MIT. Microsoft documents that untrusted plugins cannot safely share a trusted .NET process and that NativeAOT lacks dynamic assembly loading and runtime code generation. Therefore the proposed default is a JIT host; same-user workers and AssemblyLoadContext are not sandboxes. Packaging, support tiers and release procedures below are recommendations, with all implementation and verification still to be performed.

## Entry conditions and owners

Release acceptance requires phases 1–6 native gates, a reconciled compatibility manifest and issue owners for every unresolved mandatory row. The release lead owns artifacts/CI; runtime and experience leads own their module fixes; a separate reviewer approves security and parity exceptions. Provision Windows, Linux and macOS runners and real-terminal access early. Signing/publishing identities, package-name availability and release-channel ownership are decisions to resolve before release-candidate production.

## Work packages

### P8-01 Close the native compatibility matrix

Owner: runtime lead; input: phase 1 inventory and phases 2–6 reports. Finish mandatory provider/authentication, MCP/codemode, command, session and native-plugin gaps with their module owners. Do not shift required JavaScript execution semantics into a mandatory Node bridge. Output: row-by-row compatibility/deviation report. Evidence: every required row names its exact test, baseline SHA, platform and result; deferred mandatory behavior blocks a full-parity claim.

### P8-02 Make CI deterministic

Owner: release lead; starts in phase 1. Pin .NET SDK, dependencies, lock files, CI actions and test inputs; use clean restores, nullable/analyzer checks and controlled clocks/randomness. Keep routine tests offline with recorded streams and fake tools. Output: build/test pipeline and reproducibility report. Evidence: repeated clean unsigned payload builds match or have documented nondeterministic inputs; signing timestamps are assessed separately. Live-provider smoke jobs require credentials, spending limits and explicit enablement.

### P8-03 Qualify the platform matrix

Owner: QA; prerequisite: deployable phase 5 slice. Gate Windows x64, Linux x64 and macOS arm64; add Linux arm64/macOS x64 only after equivalent testing. Record minimum supported OS and terminal versions against current .NET support. Output: OS/RID matrix. Evidence: case sensitivity, Unicode/long paths, spaces, executable bits, permissions, symlinks, newline variants, redirected streams, Ctrl+C and process-tree cleanup. Bash and PowerShell retain separate documented semantics.

### P8-04 Review security and trust boundaries

Owner: security reviewer and module owners. Trace hostile model/file/tool/plugin input to execution, terminal controls, credentials and network sinks. Verify authorization after transformations and on nested tools; explicit repository trust; no executable autoload; secret-storage/redaction behavior; bounded frames/resources; content-free or disabled telemetry. Output: threat review and remediation list. Evidence: denied-action tests, malicious fixtures, archive traversal/symlink checks and secret canaries. Do not advertise untrusted-plugin isolation without tested OS enforcement.

### P8-05 Test failures and recovery

Owner: QA/runtime. Input: stable session and process contracts. Inject partial writes, disk-full/read-only storage, malformed frames, disconnects, worker crashes, cancellation races and abrupt termination at commit boundaries. Output: fault-injection/fuzz corpus and recovery runbook. Evidence: no silent history corruption, cross-session stale writes, duplicate side effects or leaked children. Recovery may resume observation or require user action; it must not blindly rerun completed mutations.

### P8-06 Establish performance budgets

Owner: runtime/experience leads. Measure cold/warm startup, idle/streaming memory, long-session load, event throughput, first-render/input latency and plugin reload stability on named hardware. Output: reproducible benchmarks and numerical regression budgets agreed after measurement. Evidence: large synthetic sessions, slow consumers, resize storms and prolonged streaming stay within accepted budgets. Distinguish provider latency from local overhead; investigate growth rather than masking it with event loss.

### P8-07 Produce native distribution artifacts

Owner: release lead; prerequisites: P8-02/03. Package a .NET tool plus self-contained RID-specific archives; verify proposed package IDs before publication. Start untrimmed with JIT and external plugin directories. Treat trimming, single-file and NativeAOT as separately qualified variants, not implicit optimizations. Output: installable candidates and manifest. Evidence: clean-machine install/run/update/remove, native plugin loading/dependency resolution and no Node/npm present; standalone users need no preinstalled .NET runtime.

### P8-08 Verify provenance and licenses

Owner: release/security leads. Generate SBOMs and notices covering copied Pi portions, NuGet/native dependencies, terminal assets and any bundled search binaries. Lock hashes and retain applicable licenses. Output: artifact checksums, source/SDK/baseline metadata, provenance and channel-appropriate signatures. Evidence: verify downloaded candidates against the manifest, fail tampering checks, scan secrets/vulnerabilities and review any redistribution restriction. The optional Node package receives its own dependency and npm/native-module audit.

### P8-09 Ship documentation and SDK examples

Owner: experience lead; reviewed by QA. Deliver install/troubleshooting, CLI/RPC, session import/export/recovery, trust/security, native-plugin SDK, API migration and compatibility guides. Include runnable embedding, RPC cancellation, custom tool, persistent-state and UI/noUI examples. Output: versioned docs/sample packages. Evidence: compile and execute samples against shipped packages on clean machines; test every documented install command. State Node bridge tiers separately and explain unsupported custom-TUI/deep-import cases.

### P8-10 Rehearse upgrade rollback and publication

Owner: release lead. Input: signed/checksummed candidate and all native evidence. Rehearse fresh install, upgrade, side-by-side rollback and uninstall without deleting user sessions or credentials; define copy/backup rules before format changes. Output: release checklist, rollback commands and notes. Evidence: installed artifacts match the tested commit and preserve supported data. Publication requires release-owner approval; retain known-good artifacts and never replace an existing version with different bytes.

### P8-11 Establish maintenance ownership

Owner: runtime/release leads. Track PiSharp SemVer independently from the pinned Pi compatibility SHA. Define runtime/security patch response and upstream-upgrade review: inventory diff, fixture refresh, change classification, differential runs and updated compatibility notes. Output: maintenance policy and ownership schedule. Evidence: rehearse one baseline-change review without silently adopting main. Self-contained archives must be rebuilt to incorporate runtime security fixes; optional bridge support follows its own tested Node/version matrix.

## Native release gate and evidence

All mandatory native rows pass on declared platforms; no unresolved critical/high-severity defects; any narrower preview is explicitly labeled. Archive the exact artifact hashes, CI links, golden/differential reports, real-terminal evidence, security disposition, performance budgets, SBOM/notices and clean-machine results. Prove native install and runtime with Node absent, including required MCP/codemode paths. Optional bridge artifacts ship only after their separate conformance, trust and packaging gate; absent bridge support is not a native failure.

## Sequence effort and decisions

P8-02–04 and dependency records begin early. P8-05/06 run once vertical slices stabilize; P8-07–09 can overlap after package contracts freeze. P8-10 waits for evidence closure; P8-11 establishes ownership before publication. Allow roughly 4–7 engineer-weeks of hardening/release effort within the overall native estimate, including work performed earlier; do not add it twice. Assumes shared QA, three platform runners and timely signing access. Broader OS coverage, untrusted-code sandboxing or late mandatory parity gaps require re-estimation.

## Sources

- [Pinned upstream MIT license](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/LICENSE)
- [.NET plugin loading and trust warning](https://learn.microsoft.com/en-us/dotnet/core/tutorials/creating-app-with-plugin-support)
- [NativeAOT constraints](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
- [.NET tools](https://learn.microsoft.com/en-us/dotnet/core/tools/global-tools)
- [Single file deployment constraints](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)
- [.NET support lifecycle](https://dotnet.microsoft.com/en-us/platform/support/policy)
