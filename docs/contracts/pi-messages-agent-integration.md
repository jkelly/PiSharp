# Pi Messages provider and Agent integration — source-only milestone

This change implements a native binding for original package P2-08, with authored
consumer coverage relevant to P3-01, P3-02 and P3-03. The allocation is R43 R1,
base d2025c37a0effb153c766c66b06c024d04da6cbe. No C# parser, compiler,
native build, test, provider execution or Source capture was run. This document
confers no package or phase acceptance.

R43 R2 is a source-only successor to candidate
6110ea702277a1a04014b3a2d360158df08d1d88. The independent static review
identified PM-R43-WIRE-ERROR-CLEANUP-01 and PM-R43-PREVIEW-LIMIT-02.
A mapped wire StreamError now remains primary if physical cleanup fails:
its original message, content, usage, metadata and Source emission observation
survive, with native CleanupFailed recorded separately. Actual local
cancellation retains precedence; cleanup failure after a successful tool
terminal still rejects execution authority.

Typed preview CharacterLimit and DepthLimit map to ResourceLimit.
UnsupportedNumber and UnsupportedUnicode map to UnsupportedFeature;
DuplicateProperty maps to MalformedStream. These are native admission
classifications by exception type and failure value, without public-text tests.
Three additional authored core cases reproduce both reported schedules and
cover all five preview variants, direct/wrapped paths, secondary cleanup,
wire Error/Aborted, cancellation precedence, whole provider values and
zero high-level Agent tool authorization/effects after held cleanup.
Preview Agent limit cases inject a bounded Pi transport through the existing
IModelProvider seam; wire-error cases use PiMessagesModelProvider.
The previous ten cases and all retained original fixtures/accounting are preserved.
All thirteen core cases remain uncompiled and unexecuted.

PiMessagesModelProvider consumes the unchanged FrozenModelCatalog, retains
every selected pi-messages chat row and its complete owned Raw metadata,
and constructs one transport per complete model/API/provider identity.
CreateDirect and CreateSimple forward the same explicit options without
Completions defaults or token/reasoning clamping. The client is borrowed.
Keys are explicit. Cache environment values and lookup are injected; this
binding does not discover ambient credentials or ambient environment.

ModelTransportRegistry snapshots providers and their model selections into
an immutable dictionary. Unknown, wrong and duplicate complete identities
are rejected before a provider iterator is admitted. It implements the
existing IChatTransport, so the existing ChatClient and high-level Agent
can consume it without changes to the catalog, Agent, loop, turn or tool runtime.
This bounded native registry does not qualify Pi's global API registry,
custom providers, aliases or dynamic registration.

Native provenance adds NativeChatAdapter.PiMessages = 2, preserving
OpenAICompletions = 1 and all existing failure-code values. Pi wire errors,
typed decoding/conversion failures, HTTP failures, cancellation and actual
cleanup faults set diagnostics at their respective sites. Cleanup-only
failure before terminal publication becomes an error and cannot authorize
tools. A primary fault survives a secondary cleanup fault; actual cancellation
wins the primary classification. ChatRun adds narrow Pi fallback codes for
preterminal faults and cancellation, and native cleanup provenance for callback
or postterminal iterator cleanup faults. Existing Completions fallback behavior
and its Source aborted observation reader remain unchanged.
Diagnostics do not enter the Pi message JSON. Classification reads neither
public error text nor Source snapshot diagnostic objects.

The core Agent consumer authors real offline HTTP routing through
PiMessagesModelProvider → ModelTransportRegistry → ChatClient → Agent →
TurnRunner → ToolInvoker/policy. It holds the real body cleanup before assistant
commit, the assistant sink before tool authorization, and the tool-result sink
before the next request. It inspects the actual second request's canonical
assistant/tool values, final arguments, signatures, usage, explicit nulls and
unchanged caller inputs, then exercises high-level continuation with retained
context. Other authored cases cover direct/Simple option forwarding, missing
keys, invalid/duplicate selection, provider error, malformed input, EOF,
truncation, cleanup after tool end, abort settlement and borrowed-client reuse.

The core transport consumer authors all eight native codes at real injected
HTTP/reader sites, held publication/delivery cleanup barriers, primary/cleanup
precedence, ChatRun fallbacks, actual throwing cancellation callbacks and
Completions preservation. New Pi cases use owned deadline runners: an expiry
writes and flushes a durable INCOMPLETE_NONPASSING receipt, stops further case
admission and awaits the original task without a second timer. Every held case
releases its own controls in finally and joins its original operation. Receipt
I/O failure also joins the original task before unwinding.

Mandatory catalog coverage is authored against genuine released rows. The
existing offline artifacts/released-npm-ai/pi-ai-0.99.1.tgz is pinned to
SHA-256 f9f44692157d0bf5679c4a17304a310028231d7daaeaaea3b73252f4b7a264d3.
Its package/dist/providers/data/radius.json member is 19,164 bytes,
SHA-256 8e868af981cc64a39da11b135a96252e3eeccb14b92e80d8fbba899ff96f2616.
All 28 Pi Messages chat rows are selected and routed to an injected handler.
Small authored catalogs test negative admission and inert tool behavior only.
The release-row test has not run; injected answers are not genuine provider or
Source executions and do not accept the full released catalog.

Remaining qualification:

- Independently review the exact immutable source candidate and authored consumers.
- In a lead-authorized reopened window, restore/build through the lead's central
  registration, lock and product workflow; hash actual produced DLLs and evidence.
- Execute the core Agent and transport consumers and all retained 29/46 provider
  cases, all 66 mapped criteria and all nine R3 accounting controls. Inspect actual
  publication, delivery, tool, cancellation and cleanup observations, including
  deadline receipt/stop/retained-ownership negative controls.
- Capture the pinned upstream Source through an approved offline process and compare
  whole values, bytes, undefined-property sets, framing and lifecycle observations.
  The genuine Source execution count remains zero.
- Resolve original raw callback model/context/options shape, clock/Date timing,
  global registry/aliases/live queues, native admission restrictions, tool identity
  replacement, JavaScript exception/undefined/code shape and host URL/header/env
  behavior gaps. No Source equivalence is inferred from authored answers.
- The lead must reconcile its separately frozen Simple work and central registration
  with this exact base, then coordinate full native and physical-terminal gates.

All 79 original scopes, five historical independently accepted own-scopes,
zero dependency-complete packages, 378 strict Completions findings, 38 Agent
metadata omissions and eight OPEN full phase gates remain unchanged.
The retained R3 criterion mapping, nine controls and 29/46 cases are unchanged;
its independent static reporting acceptance is not runtime acceptance.
