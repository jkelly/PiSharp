# Usable explicit plain templates: composition plan

This historical adapter plan is superseded for implemented frontend wiring and
the YAML package decision by [prompt-template-workflow.md](prompt-template-workflow.md).

The independently owned adapters extend frozen discovery commit `d88269079886592e0958995a076154accf6546cb`.
They do not edit shared startup, dispatcher, extension activation, frontend or
runner files. This plan identifies the necessary composition edits explicitly;
the adapters alone do not expose a runnable CLI flag. No native execution occurs
in this slice. The pinned source is Pi v0.99.1
`d86654abb8862e201933517d6f1fce9f88dd117f`.

## Decoder decision

The inspected source contains frontmatter extraction and an injected metadata
decoder, but no native YAML parser. NuGet.Config clears all package sources.
Pinned [frontmatter.ts](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/utils/frontmatter.ts)
calls the standard `yaml` parser, not a restricted key/value grammar. It strips
one leading BOM, normalizes CRLF/lone CR, uses prefix delimiter checks, takes the
first closing `\n---`, and trims the resulting body only for terminated
frontmatter. Empty extracted text skips parsing; parsed null becomes an empty
object. [Prompt loading](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/prompt-templates.ts)
then selects only string-valued description and argument-hint; empty description
falls back to the first nonempty body line, truncated at 60 UTF-16 code units.

Plain, quoted and flow mappings, comments, literal/folded multiline scalars,
tags, anchors/aliases, duplicate-key errors, scalar typing and nonmapping roots
are parser concerns. Metadata keys are case-sensitive. There is no source basis
for treating all scalar tokens as strings or parsing only the two known lines.

The implemented minimal policy is `PromptTemplateFrontmatterPolicy.RejectNonemptyYaml`.
It interprets no YAML: absent/empty frontmatter already bypasses it; every nonempty
frontmatter string produces an explicit warning and that file is skipped. Even
comment-only frontmatter is rejected. Plain templates remain fully usable through
the existing argument expansion and fallback description. This is intentionally
not a YAML decoder or full frontmatter compatibility claim. An approved decoder
can later be supplied to `PromptTemplateCliBinding.LoadAsync` without changing
discovery or admission.

Concrete standard candidate: [YamlDotNet 16.3.0](https://www.nuget.org/packages/YamlDotNet/16.3.0),
[MIT license](https://github.com/aaubry/YamlDotNet/blob/v16.3.0/LICENSE.txt).
It has net8.0 assets usable by net10.0 and no package dependencies for that asset.
It is a pinned evaluation candidate, not a latest-version recommendation. Adding
it would replace framework-only packaging with a third-party runtime assembly,
require an authorized reviewed package/local source, update package lock files
in dependent projects, include license notices in distribution and amend the
offline restore/release evidence. No package reference, lock file or source
configuration has been changed, and no package was downloaded.

The smallest candidate decoder would deserialize to an untyped object using
the library, with duplicate-key checking and attempted unquoted scalar type
resolution, then select string values from an ordinal mapping. Empty/null and
nonmapping roots yield empty metadata. However, its default schema/tag handling
is not proven identical to Node yaml 2.9.0: booleans such as yes/on, timestamps,
numeric formats, explicit tags, aliases/merge keys, duplicate keys and multiple
documents need comparison before acceptance. Do not use dictionary<string,string>
coercion. The [standard builder](https://github.com/aaubry/YamlDotNet/blob/v16.3.0/YamlDotNet/Serialization/DeserializerBuilder.cs)
also carries dynamic-code/trimming constraints; published trimming/AOT would
need separate qualification. Error wording remains library-native. A full-YAML
implementation is blocked on the package/qualification decision, not handwritten
here under a compatibility label.

## Independently owned adapters

`Prompts/PromptTemplateCliConfiguration.TryConsume` consumes repeatable
`--prompt-template <absolute file or directory>` flags, using existing
`SessionCommands.Absolute` validation at the shared parser's current option
boundary. It leaves other options and their opaque values to that parser and
does no reads. Unlike source cwd/home resolution, this bounded native CLI
profile requires absolute paths. No default discovery or no-prompt-templates
flag is introduced. Each explicit selection reports missing paths. The startup
owner must explicitly authorize the selections; tool read grants or executable
extension approval are not reused as prompt authority.

`Prompts/PromptTemplateCliBinding.LoadAsync` loads once before publishing the host,
defaults to the plain-only policy, retains diagnostics, exposes `AdmissionFor`
with the explicit operation and retains borrowed raw input/command boundaries.
Its `CommandCatalog` adapter appends prompt rows after extension rows without
deduplicating across categories. Exact get_commands fields are name, description,
source and sourceInfo; argumentHint stays in native template metadata. Completion
queries remain delegated exclusively to the borrowed extension catalog. No
template becomes executable extension registration. Skills, if added, must be
appended after prompts by their owner.

## One composition batch, exact shared ownership

| Owner / file | Entry point and required edit |
|---|---|
| CLI integrator: Commands/RpcSessionCommand.cs | In Parse, retain original argument-count validation, create a selection builder and call TryConsume at each current option boundary before the existing option branches; add captured configuration to Arguments and usage. In RunCoreAsync, await binding load before dispatcher publication and report each template diagnostic to stderr, never stdout. |
| CLI/profile integrator: Commands/OfflineSessionProfile.cs | Retain the binding capture and expose AdmissionFor(operation), a nonnull ordinary admission even without extensions, and the combined read-only CommandCatalog. Keep replacement/disposal/generation ownership unchanged. |
| Extension owner: Extensions/NativeExtensionActivation.cs | Expose the existing RegisteredExtensionInputAdmission captured in BindGeneration separately from CommandInputAdmission. Provide IPromptTemplateCommandAdmission over that same Binding.Snapshot and enabled-command list, retaining space-only raw command parsing, actual invocation await, closing token and diagnostic behavior. Do not pass the combined InputAdmission as raw handlers or dispatch will occur twice. |
| RPC integrator after frozen model/thinking patch: PiSharp.Rpc/Protocol/RpcSessionDispatcher.cs | Add optional Func<string, IPromptInputAdmission> inputAdmissionSelector constructor parameter, retaining legacy single-admission fallback. The prompt/steer/follow_up cases must enter SubmitInputAsync when either selector or legacy admission exists. Select using command.Type and carry the selected admission into the existing InputAdmission wrapper instead of rereading _inputAdmission. A prompt with streamingBehavior=steer remains Prompt. Keep original linked token, input callback marker, session capture, QueueOnly/budget preflight, joins and monitor ownership unchanged. get_commands already delegates to the host catalog. |
| CLI composition: Commands/RpcSessionCommand.cs | Pass binding.AdmissionForRpc as selector and binding.CommandCatalog as extensionCommandCatalog. This same host covers InteractiveSessionCommand and TerminalSessionCommand; ResolveStartupWorkspace must parse the new flags too. |
| CLI integrator: Commands/SessionCommands.cs | For one-shot session run, add the same configuration to Parse and its Arguments record, load the binding in ModifyAsync, and use ordinary admission for SubmitInputAsync even when no extension is active. Preserve command/error/cleanup ownership. |
| Frontend owner: Interactive/InteractiveSessionFrontend.cs | In the chat-commands response, separate extension completion names from prompt submission names. Current extension identifier validation rejects prompt filenames with spaces and treats all rows as executable extensions. Keep /complete restricted to extensions; allow known prompt invocations to pass as prompt input. For template names containing whitespace, use /send and document that the pinned first-token expansion cannot invoke them. Built-in local frontend commands retain their current precedence. |
| Completion/editor owner: Interactive/TerminalChatInput.cs and Interactive/TerminalSessionView.cs | Use prompt name/description candidates for command-name suggestions where supported; do not send template argument completion to pisharp_complete_extension_command. Argument hints need the native template descriptor or a separately specified native-only transport; do not silently add them to pinned get_commands wire fields. The current base has no general prompt name autocomplete binding. |
| Test integrator: tests/PiSharp.CodingAgent.Tests/Program.cs | Register discovery, resource-set and CLI-adapter Cases() together once. No scattered registration edits. |

Dispatcher planning must compose after frozen model/thinking implementation
`5b1b029179b94013cda47f5a387231d7ef46f5c4` and test-only successor
`1bf544f823f3f42ceed61d9452e8b64e93522bc2`. Supplied bundles were inspected in an
isolated checkout without applying them. Their dispatcher diff adds model order
and the six model/thinking switch cases; it leaves the constructor's input
admission parameters, prompt/steer/follow_up routing, get_commands and input
admission wrapper/SubmitInputAsync unchanged. The proposed selector edits
therefore target the existing seams after that frozen patch. No integration
application or runtime conflict check is claimed. No agent/request/provider
thinking files are modified.

## Next-batch acceptance

Create a plain `review.md` containing `Review $1`; launch the existing offline
RPC host with its normal session/workspace/script flags plus
`--prompt-template <absolute review.md>`. Send get_commands and verify a prompt
row; send prompt `/review 'the change'` and verify the scripted request receives
`Review the change`. Repeat in interactive/terminal via the shared RPC host and
in one-shot session run. Also verify explicit steer/follow_up expansion, command
collision precedence/rejection, images, frontend submission, no-extension host,
YAML warning/file skip and repeated missing-path diagnostics.

Six new CLI-adapter groups and one discovery correction group are authored,
unregistered and unexecuted. Run them with the prior 15 discovery/resource-set
and 32 pure/catalog/admission groups after composition (54 groups total). Author
end-to-end host tests in the integrator's owned fixture files before claiming a
usable CLI command. No new test framework is needed. Default directory/package
discovery, full YAML and full CURRENT acceptance remain separate qualifications.
