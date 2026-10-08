# Same-file session tree navigation

This implements the no-summary selection path from Pi v0.99.1
[`agent-session.ts` at d86654abb8862e201933517d6f1fce9f88dd117f](https://github.com/badlogic/pi-mono/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/agent-session.ts#L3866).
The inspected local file has SHA-256
`26e76e13456757b0419df26b1f4d9bce9faa28c2588ad66af3e4cc3e9b179e0a`.
`SessionManager.branch/resetLeaf` change only the selected leaf; they do not append
or rewrite the session. Text follows `contentText(content, "")`.

```csharp
var expected = owner.Current;
var view = owner.CaptureTree(expected, token);
// Retain these actual host objects until the chooser returns an ID.
var receipt = await owner.NavigateTreeAsync(view.Attachment,
    new SessionTreeNavigationRequest(selectedId, view.Revision), token,
    beforeTree: (preview, work) => ValueTask.FromResult(true));
```

`SessionTreeNavigationView` exposes `Attachment`, opaque `Revision`, immutable
`Tree`, selected `Context`, `SessionId`, `Path`, `LeafId` and `Generation`.
The host must retain the actual attachment and revision. A generation number,
session ID, log sequence or deserialized tree does not convey selection authority.
RPC must associate its bounded chooser workflow with these host objects and
pass the captured attachment rather than reading `owner.Current` at selection.

`SessionTreeNavigationRequest(string? TargetId, SessionTreeNavigationRevision
ExpectedRevision)` supports an explicit null/root target. Selecting the exact
current leaf is `NoOp`, before any parent resolution, text extraction or callback.
Otherwise user messages and custom messages select their parent (including null)
and return raw canonical source text in `EditorText`. Non-text content blocks are
excluded; text blocks concatenate without a separator. Other targets select
themselves with no editor text. Hidden custom messages follow the same rule.
Stored context edits affect projected context but do not rewrite raw editor text.

The receipt exposes `Disposition` (`Selected`, `NoOp`, `Vetoed`, `Aborted`),
`View`, actual native `Agent`, optional `EditorText`, `CancellationCallbackFailed`,
and convenience `Cancelled`, `Aborted`, `NoOp`, `SessionId`, `LeafId`, `Context`.
Its context, native messages/model/tools and selected leaf describe the publication
boundary. A retained receipt is an immutable observation, not permission to edit a
later attachment or native editor lifetime. The terminal applies tree text only
when its original editor is still trim-empty, using its own correlated authority.

The operation serializes with host mutations and reserves existing session idle
admission. It validates attachment at admission and under the host publication
monitor, and validates log/context/configuration object identity at reservation
and publication. Selecting away and back invalidates an old revision. A foreign
revision, stale selected context, durable append or configuration change rejects.
Active processing, compaction/navigation, pending queues and existing unavailable
or replacement guards remain in force. Existing `IsCompacting` represents this
reservation, following Pi's combined compaction/navigation admission guard.

The existing runtime registry resolves the selected branch's model/tool
declarations; selected projected/native history is validated before the trusted
callback. `beforeTree` receives target, old/new leaf, common ancestor, abandoned
raw entries, prospective context/configuration and editor text. It runs once,
outside state monitors and commit locks. False yields `Vetoed` with the old
context and no editor text. Recursive host mutations, idle waits and cleanup
from this callback reject self-waits through the existing ownership guards.

Caller, attachment lifetime and shutdown cancellation are exceptional. Session
`Abort` yields `Aborted` only for the exact owned work cancellation token; a
foreign `OperationCanceledException`, late callback I/O error or other failure
propagates. Cancellation never detaches the original callback. The existing
cancellation-user joins finish before the reservation settles. Callback failure
facts remain visible in the session and receipt. Close joins the original
navigation through the mutation gate and existing session idle reservation.
No cancellation check after publication erases the selected-context receipt.

Publication updates actual Agent messages/configuration and selected context
under the state monitor, with no await or trusted callback. It retains the same
file, writer, acknowledged log and attachment lifetime/generation. It emits no
append/checkpoint receipt and consumes no ID or clock. Reopening with latest-leaf
semantics still selects the physical tail until a later real append; that append
uses the selected leaf as parent and retains abandoned sibling records. Root
continuation appends a null-parent entry. Storage uncertainty/failure remains the
existing durable append contract's responsibility.

Summary generation, labels, extension SDK tree-event activation, RPC/CLI/terminal
wiring and two-phase shutdown are separate owners. This API deliberately has no
summary switch or invented summary acknowledgment. Existing `SummarizeBranchAsync`
remains separate. The additive owner surface resides in a new partial; the only
shared owner-file change is the `partial` class modifier. No persistent lifecycle
or activation configuration guards are changed.

Base: activation candidate `267323ac3ac88c9f80d0763240905322ecd25c9c`, direct
parent `7452a7b2a355e1f603a31eb693def91a9f565df1`. This preserves the reviewed
activation registry. The separate compiler prerequisite `96e1532e...` is not
included. Ten deterministic regression groups are authored and registered in the
CodingAgent runner; it awaits original tasks directly for this group. No builds,
tests, native/source execution or provider calls were authorized or performed.
