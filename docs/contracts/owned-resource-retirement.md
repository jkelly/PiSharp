# Owned resource retirement

Source-only implementation; authored fixtures have not been compiled or executed.

`ReplaceableAgentSession.RegisterOwnedResource(attachment, closeBody, stopBody: null)` retains a bounded resource
record for one exact current attachment. There are at most 128 records per owner, including retired
records whose stable close receipts remain callable. Registration itself performs no callback or
resource acquisition. The committed-current `AttachmentChanged`/`AfterReplacement` notification
can register metadata through a narrow attachment marker; source preflight, retirement callbacks,
and unrelated generation contexts cannot use that exception.

`OwnedResourceLease.CloseAsync()` returns its stable original settlement. An optional single
`Func<Task>` stop callback initiates once outside owner locks before normal semaphore admission
or any idle joins. Its invocation must return the actual stop task promptly; it must initiate the
resource channel/process stop without awaiting catalog publication or owner admission first.
Its original settlement can remain held while active calls settle. Stop callback lifecycle reentry
rejects before mutation across asynchronous continuations. The stop original is directly joined
before the close body, and stop failure never skips that body's cleanup or publication join.
A normal close then queues effect-free semaphore admission, joins idle session work, and acquires the actual persistent
replacement reservation before executing its body. A transition holding that semaphore cancels
and directly joins a queued admission original before claiming the same body. The body runs once.
The semaphore admission token never reaches the resource body or its publication. Callback
self-close, owner disposal, replacement and recursive publication reject before mutation.

The callback receives an opaque `OwnedResourceRetirementTransaction`. Its
`PrepareAndPublishCatalogAsync((registry, activeNames, token) => prepared)` captures registry and
active names afresh through the exact persistent reservation, then directly joins the existing
prepared catalog pipeline and durable append/publication. It admits one original publication per
resource callback. Even an unawaited admitted publication is retained and joined before resource
completion. The transaction expires when the body and publication originals settle; stale,
escaped, foreign-execution-context and recursive uses reject. Ordinary session APIs still reject
replacement state. Only the minted reservation can use the private catalog admission path.

Replacement preserves target validation, veto, diagnostic drain and `BeforeRetirement` ordering.
Resource withdrawal follows those checks and precedes physical writer/capability retirement.
Final shutdown initiates every resource stop before cancellation/provider joins or waiting for
the owner semaphore, then closes host admission and joins active work. It withdraws resources under
the same reservation before attachment-lifetime cancellation and persistent stop/disposal.
Caller cancellation cannot detach an admitted withdrawal. If a faulted session cannot reserve a
durable catalog operation, final cleanup still invokes/joins resource bodies with a cleanup-only
transaction whose publication fails explicitly; it returns no successful publication receipt.

Stop/body/publication failures remain original failures, including repeated resource close identity.
A durable declaration acknowledgment is not rolled back if subsequent registry publication or
resource cleanup fails. Source replacement may retain the source with an already withdrawn
catalog; callers must inspect its actual acknowledged state rather than retrying resource effects.
Shutdown attempts every selected resource body and retains all failures before physical cleanup.

Eleven fixtures cover held acknowledged publication before lifetime cancellation, queued close
racing switch, veto/target failure, callback and escaped-capability controls, directly joined
unawaited publication plus body failures, stale attachment registration, and committed attachment
registration and an originating input callback switch. Three additional groups hold an actual
synthetic provider original during normal close and shutdown, hold the independent stop original,
and verify stop reentry plus combined original faults. Queued switch and preflight/veto controls
also assert stop-once and no automatic stop before successful preflight. Replacement continues
to require its existing idle source reservation; a busy source is rejected rather than stopped
before target/veto admission. Root owns registration, build/test allocation, actual MCP host binding and native
qualification. No process, provider, package or network operation was run during authoring.
