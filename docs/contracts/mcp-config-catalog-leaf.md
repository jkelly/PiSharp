# Original MCP configuration and catalog leaf

This is an authored independent native component against Pi v0.99.1,
`d86654abb8862e201933517d6f1fce9f88dd117f`, based on verified main
`754251be28928b24eaae5895b744c423450c91a3` / tree
`738429cf70aa4609938e398d3ef00b5836bf157c`. R262 identifies MCP as an original
feature, not a future Extras item. This component is uncompiled and its tests
are unexecuted; neither MCP runtime parity nor completion of the port is claimed.

## Source evidence

All five R262 MCP source length/SHA256 pins were rechecked as inert local bytes.
The extra `tools.ts` identity is recorded in the leaf state. Requirements come
from these pinned sources:

- [mcp-servers.ts](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/mcp-servers.ts): transport/config/OAuth validation, server and per-tool exposure rules, extension server registrations.
- [config.ts](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/extensions/mcp/config.ts): global then trusted project loading, project replacement preserving order, disabled entries, optional codemode preference, and independent diagnostics.
- [index.ts](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/extensions/mcp/index.ts): configured entries override extension entries, stable name ownership, namespaces, connected discovery requirements, withdrawn definitions re-registered hidden, and owning lifecycle.
- [tools.ts](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/extensions/mcp/tools.ts): sanitized 64-character names/hash collisions, native exposure projection, description and input-schema defaults.
- [runtime.ts](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/extensions/mcp/runtime.ts): transport, initialize/list/call/resource, progress, timeout, reconnect and shutdown requirements.
- [main.ts](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/main.ts): dedicated MCP CLI entrypoint.

## Owned API and implementation

The public namespace is `PiSharp.Extensions.Mcp.Configuration`, implemented
under `src/PiSharp.Extensions.Abstractions/Mcp/Configuration`. R262's logical
`src/PiSharp.Extensions` directory is not an existing SDK project; the actual
Abstractions project compiles the new files through its default source glob.
There are no shared project, package, registry, activation, CLI or session edits.

`McpConfigurationReader.Validate(name, JsonElement)` returns a validated inert
configuration or the original validation message. Its immutable `Raw` JSON
preserves unknown and optional fields. Defaults are enabled, 60 seconds and
codemode exposure. HTTP wins over command when no transport type is supplied;
`streamable-http` is accepted, and legacy SSE diagnoses explicitly. OAuth fields
are validated as inert text/port/URI metadata. No credential is read or resolved.

`Load(globalDocument?, projectDocument?, projectTrusted)` consumes supplied
source labels and JSON text. Null means absent. It never reads a path. It does
not parse the project document unless the caller supplies true. Valid project
entries replace global entries in their original positions; invalid replacements
leave the global entry and add a diagnostic. Disabled servers remain listed.
The optional codemode preference remains absent when unspecified; its effective
default is true. Last duplicate JSON values and array-index property enumeration
are normalized before creating native immutable JSON.

`GetToolExposure(config, toolName)` gives exact names precedence over wildcard
patterns, otherwise selects the first matching pattern, then the server default.
Patterns treat regex punctuation as literal. Wildcards exclude the four
ECMAScript line terminators. Names use ordinal comparison.

`McpCatalogPlanner.ComposeServers(configuration, registeredServers)` keeps
configured entries first, including disabled overrides, and reports overridden
extension provenance. Repeated supplied registrations replace the prior value
without moving its position, as in the pinned registration Map. It neither owns
registrations nor emits registration-change events.

`CreateToolName(server, tool, isTaken?)` performs UTF16-unit sanitization and uses
the first eight SHA256 hex characters of UTF8 `server + NUL + tool` for long or
colliding names. `ToToolExposure` maps `codemode-deferred` to native `Deferred`,
while the plan retains the original MCP mode for discovery decisions.

`Plan(borrowedSnapshots, autoEnableCodemode, previousNameOwners?)` produces tool
names, labels, descriptions, parameters, namespaces and timeout metadata.
Missing or null schema type defaults to object; absent properties becomes an
empty object. Missing server instructions defaults to the original namespace
description; supplied empty instructions remain empty. Name ownership can be
carried into successive plans so withdrawn names remain reserved. Disconnected
or disabled snapshots do not contribute a new offered catalog. This is not an
instruction to delete previously published tools when a connection drops.

The plan reports codemode and tool-search requirements and the widest resource
tool exposure. It does not activate any tool by name. The runtime owner must
verify genuine discovery-tool implementation identity before activation, as
the original does. Plans contain no client, transport, execution delegate or
grant; they confer no file, process, network or final-action authority.

## Authored tests and coordinator integration

`tests/PiSharp.Extensions.ContractTests/McpConfigurationTests.cs` adds fifteen
synthetic groups in the existing fixture style. They cover absent/default and
trusted/untrusted config, invalid entries and preference, transport precedence,
loopback metadata, inert command/environment expressions, exact and wildcard
exposure, duplicate/numeric object order, extension overrides, UTF16/name hashes,
namespace/schema descriptions, discovery modes and immutable refresh ownership.
All fixtures are literals; no external server, process, environment lookup,
credential store or network request is invoked.

The shared runner remains unchanged. Its owner must concatenate
`McpConfigurationTests.Cases()` into the existing case list. Then the existing
runner's `--filter mcp-configuration.` selects the authored groups after the
coordinator's normal admitted offline build. No new test framework or project
is needed. Tests authored: 15. Tests executed: 0. Builds: 0.

## Remaining original requirements and limits

The current native registry already supplies tool namespaces, exposure metadata,
borrowed prepared invocation and generation-owned callback lifetimes. Those
foundations do not connect an MCP server. The critical missing bridge is the
actual admitted MCP runtime host, with:

- Owned `registerMcpServer`/`getMcpServers` and change notifications, extension override provenance, and config/CLI settings workflows.
- Explicitly admitted stdio/streamable HTTP transport, initialize/list pagination, instructions and resource capabilities; no ambient auto-connect here.
- Installation of descriptors through the existing tool pipeline so hooks, argument preparation, final authorization, progress and cancellation retain their owners.
- Genuine codemode/tool-search discovery, dynamic exposure and hidden withdrawal; retain old published definitions during transient disconnect and reconnect lazily.
- Generation fencing for late connect/list answers, uncertain-effect-safe call behavior, and joined session shutdown/replacement/reload.
- Separately authorized OAuth/sign-in/refresh/store and config-value resolution. `${NAME}` and `!cmd` remain literal in this leaf.

Config add/update/remove persistence, connection retries, result content/image/
resource conversion and output-schema/annotation/rendering projection are not
implemented by this metadata leaf. The session/core owners must supply them.
No qualification or gate is advanced by the leaf state.

Native JSON is depth-limited by the existing immutable `JsonData` profile.
Malformed JSON diagnostic details use System.Text.Json, not V8 wording.
URI parsing currently uses System.Uri: complete WHATWG URL normalization and
unusual URL spellings require source differential qualification. Prototype-
inherited object keys are not treated as configuration values. Full JavaScript
regex boundary behavior for exotic server/tool names is not qualified. These
are disclosed source-parity limits, not claimed complete validation parity.

No ambient trust is changed by the caller-supplied `projectTrusted` flag. No
packages, Node dependency, credentials, downloaded source, global ledgers,
main merges or publication are added. Native installation remains Node-free.
