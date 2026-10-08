# Explicit prompt templates in native frontends

This CURRENT source-only implementation composes frozen prompt adapters
`6074b82bc01f40b79d067ddf54b041737c002058` with frozen RPC baseline
`c7481d3b9466c8d6330e895bcb0c10d71e294008` in an isolated branch. No native
build/test, package restore/acquisition, main merge or provider call was executed.
Compilation, dependency locks and runtime acceptance belong to the integrator.

## Implemented user flow

The shared session RPC startup now accepts repeatable
`--prompt-template <absolute file or directory>`. Interactive and terminal hosts
inherit it through their existing RPC composition and startup validation. The
one-shot `session prompt` and `session resume` paths also accept it. Other session
commands reject the option. There is no implicit project/home discovery, settings
loading, package-resource scanning or new project-trust authority.

For example, a file named `review.md` can contain:

```markdown
---
description: Review a change
argument-hint: "[focus]"
---
Review $1 and explain the main risks.
```

Supply its absolute path at startup, then submit `/review 'the change'` as a
prompt. The input that reaches existing user-message materialization and the
provider request is `Review the change and explain the main risks.` Explicit
steer/follow_up submissions expand into their existing queues without starting
a generation. An ordinary prompt with streamingBehavior=steer still follows
ordinary command-first handling. Extension-injected input defaults to no
expansion in the captured admission API.

The startup owner awaits discovery before dispatcher publication and writes
diagnostics only to stderr. Failed files are skipped; missing explicit paths
produce one error per exact path unless another diagnostic already covers it.
Other templates remain usable. No-template startups retain the existing native
admission path and one-shot message representation.

The profile captures templates once and borrows raw input handlers and command
execution from the extension's existing binding revision. Extension commands
execute before templates on ordinary prompts and are rejected for explicit
queues. Handler-transformed slash text is expanded once, without redispatching
an extension command. No session/generation/process cancellation or cleanup
ownership is transferred. The dispatcher addition is only an optional selector
by command.Type; existing callers retain their single-admission fallback.

`get_commands` now includes prompt rows after extension rows. The frontend
accepts prompt names independently of the native extension identifier rules,
including Unicode and names containing spaces. Ordinary known template slash
input is submitted unchanged to the host. A name containing whitespace cannot
be invoked by the pinned first-token expander; accepting its catalog row does
not pretend otherwise. Existing frontend built-in commands retain precedence;
`/send /name ...` submits directly when that local precedence is inconvenient.

Native completion display accepts `/complete /prefix` to list prompt names and
descriptions and `/complete name` to show a template description. Extension
argument completion retains `/complete extensionName prefix`, including when
an extension and template share a name; prompt rows never acquire extension
completion authority. Controls in descriptions are displayed inertly. This is
local completion display, not new Tab insertion/argument completion or full
upstream editor parity. RPC prompt rows keep the pinned name/description/source/
sourceInfo fields; argumentHint remains native metadata, not an extra wire field.

## Standard YAML assembly and package decision

`PiSharp.PromptTemplates.Yaml` references the core and exactly
`YamlDotNet` version `[16.3.0]`. Core and RPC projects gain no YAML reference.
The CLI enables this frontend adapter by default. Building the CLI project with
`EnablePromptTemplateYaml=false` excludes its reference and uses the explicit
plain-template policy instead; nonempty frontmatter then warns and skips files.
The full solution includes the YAML project, so the complete solution build
still requires the package even if the CLI property is disabled.

Official [NuGet 16.3.0 metadata](https://www.nuget.org/packages/YamlDotNet/16.3.0)
was checked on 2026-10-04: net8.0 assets are compatible with net10.0 and have no
package dependencies. Newer 18.1.0 exists; 16.3.0 is deliberately pinned rather
than floated. The [GitHub NuGet advisory search](https://github.com/advisories?query=ecosystem%3Anuget+YamlDotNet)
returned [GHSA-rpch-cqj9-h65r / CVE-2018-1000210](https://github.com/advisories/GHSA-rpch-cqj9-h65r),
affecting versions through 4.3.2 and patched in 5.0.0. It does not list 16.3.0 as
affected. The maintainer's [advisories page](https://github.com/aaubry/YamlDotNet/security/advisories)
has no published advisories. This is a dated metadata/advisory check, not an
assertion that the dependency has no vulnerabilities. The NuGet registration
API was inaccessible to browsing; package hash and restore audit evidence remain
for acquisition. No library binary or package was downloaded here.

Parsing, quoting, block folding/chomping, mappings, collections and aliases are
performed by the standard library. The adapter deserializes untyped values with
duplicate-key checking and unquoted type inference, then selects the two exact
string keys; it does not coerce numbers, booleans, null or collections to strings.
Nonmapping/empty/null roots yield empty metadata. A small handler over the
library's parsed scalar events preserves literal-block strings and explicit
string tags before null/scalar inference. It implements no YAML lexical grammar.

Before object conversion, a second standard parser pass records anchor/alias
events and checks the conversion budget. The guard follows the pinned Node
[Document default of 100](https://github.com/eemeli/yaml/blob/v2.9.0/src/doc/Document.ts#L394-L418),
[anchor initialization](https://github.com/eemeli/yaml/blob/v2.9.0/src/nodes/toJS.ts#L30-L45)
and [alias accounting](https://github.com/eemeli/yaml/blob/v2.9.0/src/nodes/Alias.ts#L64-L133):
each anchor starts with count one, references increment its count, and its first
reference caches a weight based on the maximum child cost. Nested alias cost uses
the referenced anchor's current count and cached weight. Products saturate above
100, and graph traversal uses explicit stacks without expanding alias targets.
This is per-anchor accounting, not a global cap on unrelated alias tokens.

Syntax maxima are initialized once bottom-up and updated only when an alias
leaf's current cost increases. Propagation stops at an unchanged parent maximum;
first-reference anchor weights remain separately cached. An accepted anchor has
at most 99 references under the minimum-weight policy, so each alias leaf is
refreshed at most 99 times. Syntax maxima only increase and saturate at 101,
bounding increases per parent edge by that fixed limit. No first reference
rescans its target subtree. These bounds make accounting visits proportional to
physical syntax size with constants from the fixed limit, while preserving
current-count-at-first-use semantics. An internal observer reports deterministic
node/weight/reference visit counts through the CLI's conditional decoder seam;
it introduces no public API, global counters, timers or second test framework.
The nested-anchor regression checks depths 32/128/512, a hot scalar anchor and
a collection whose alias descendants increase before its first reference.

Two explicit differences remain: cyclic graphs are rejected before conversion
even where Node can produce a cyclic object, and a zero-weight collection receives
a minimum weight of one (Node's empty collections can retain zero weight).
Thus 100 references to an empty anchor reject here. Ordinary scalar anchors permit
99 references in addition to their initial count; the 100th reference rejects.
Forward/unresolved references reject. Mapping keys/values are both accounted for;
complex-key stringification, merge/custom-tag conversion and anchor redefinition
remain unqualified. This guard bounds alias conversion, not input bytes, parser
depth, node count, CPU or arbitrary YAML structure. No blanket resource bound or
full Node equivalence is claimed. Parsing twice requires two standard-library
passes; the guard's accounting visits are proportional to physical syntax size
under the fixed alias policy. Standard parser CPU, object conversion and actual
runtime behavior remain unqualified.

This supports the normal prompt metadata forms and is not qualified as exact
Node yaml 2.9.0 equivalence. Explicit nonstring/custom tags, numeric edge formats
such as octal and very large integers, merge-key behavior, complex
keys and exception wording retain library behavior pending differential evidence.
The adapter's ordinary quoted/plain/flow/block/scalar/alias/malformed cases are
authored tests, not captured upstream or executed native acceptance. There is
no full-YAML compatibility claim and no handwritten partial parser.

## Packaging and remaining evidence

The adapter introduces a YamlDotNet runtime assembly into the default CLI and
its transitive consumers; it has no native or Node runtime component. Existing
framework-only packaging statements no longer describe the default CLI. The
core dependency graph remains package-free. The MIT copyright/permission notice
is in `third-party/YamlDotNet.LICENSE.txt`; CLI output and publish copy it into
`licenses/`, and adapter package metadata includes it. The integrator must verify
that notice and both adapter/runtime assemblies are present in the actual
published artifact. Library builder reflection/trimming/AOT remains unqualified.

NuGet.Config still clears package sources. The coordinator must acquire the
exact package through its authorized workflow, record its hash, supply an
explicit approved offline source/cache, and generate changed lock files for the
new adapter and dependent CLI/test/fixture projects. No old lock file, global
NuGet setting, source/trust policy or native launch/build workflow was rewritten
here. The source-only patch therefore requires that acquisition/lock step before
the normal offline build can succeed.

Register these collections together in the coding-agent runner:
PromptTemplateDiscoveryTests, PromptTemplateResourceSetTests,
PromptTemplateCliAdapterTests, PromptTemplateYamlTests,
PromptTemplateFrontendTests and PromptTemplateWorkflowTests. Existing pure
template/catalog/admission cases are already registered in the base. This slice
adds 13 YAML, two frontend and six real offline host/one-shot workflow
groups, all authored and unexecuted. The composed prompt suite totals 75 groups.
New alias cases cover normal/scalar boundaries, independent anchors, nested cached
weights, cyclic/unresolved rejection and the stricter empty-collection rule.
Wired cases retain a canonical image alongside expanded text, select the first
same-name template while emitting a collision on stderr, and cancel a host only
after its original held input read is released and joined. The image case uses
the existing text-only Responses profile: canonical retention is tested, not
vision request projection. Existing pure cases separately cover extension-command
collisions and pending handler/command cancellation; the local frontend case
covers template/extension completion-name collisions.

Registration must keep every `prompt template ` case on the runner's direct
`await test.Run()` branch. The inspected base already has that prefix exemption;
verify it survives composition when appending the six collections. Never wrap
these operations in the runner's outer `WaitAsync`: that can detach original
read/handler/session cleanup. Their own deadlines signal cancellation and then
await the original tasks and connection disposal. No shared runner edit is made.
Skip YAML/workflow groups requiring metadata in a deliberately package-free
CLI qualification. Run existing RPC model/thinking, command/input, frontend
completion and lifecycle regressions after integration. No new framework or
runner-registration edits were introduced here.

The loader still follows links with no containment or read-size budget. Windows
reparse behavior, enumeration order and malformed UTF-8 equivalence remain
unqualified. No implicit default discovery, skill loading, automatic reload,
future v1.0.2 or Extras functionality was added.
