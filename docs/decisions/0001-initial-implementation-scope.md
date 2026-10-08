# Initial implementation scope

Status: implementation scope for local development; release profiles and compatibility exceptions remain unapproved.

The first implementation milestone establishes an offline .NET 10 stream seam and a reproducible account of the pinned public Pi source. It follows Phase 1 and the minimum P2 contracts required to begin P3. It is not a release or a full compatibility baseline freeze.

Use only public Pi `v0.99.1` at `d86654abb8862e201933517d6f1fce9f88dd117f` and original PiSharp contributions. Keep the native runtime independent of Node. A development-only Node runner may exercise unmodified pinned upstream source. Captured upstream results and authored synthetic fixture expectations must have separate provenance.

The initial physical projects are `PiSharp.Contracts`, `PiSharp.AI` and an executable compatibility test harness. Framework-only dependencies allow tests on this PC without credentials, paid provider calls, package installation or external restore feeds. Tests use recorded event frames and deterministic fake sources. A replay fixture is not evidence of a working provider HTTP adapter.

The stream seam must preserve indexed/interleaved content, opaque metadata and final tool arguments. Partial JSON cannot authorize tool execution. Completion, cancellation, malformed input and disposal need explicit terminal outcomes. Bounded producer/consumer behavior must be tested with task gates. The authoritative agent-message barrier and tool scheduler belong to the next P3 milestone.

The test harness must detect meaningful mutations, preserve event/array order and distinguish absent JSON fields from explicit null. Baseline tooling must reject changed source/artifact hashes. No golden regeneration occurs implicitly during a test run.

Windows developer checks are the current evidence scope. Linux x64, macOS arm64, the full mandatory provider matrix, session/RPC interoperability, native extension loading, required MCP/codemode behavior and terminal qualification remain phase tasks. No implementation slice closes their gates.

The parent owns independent Astra acceptance, decisions about release profiles and publication. Local feature commits may be reviewed; public pushes, merging, tagging, deployment and releases are outside this task.
