# Native existing-session before-switch observation payload

Isolated successor of verified clean main `ef4a50c130629b2a2089e0a921f3135c3351a5cc`. The coordinator reports this base independently qualified at full core 1651/1651 and original selector 140/140. Those historical results do not qualify this successor. Five new regression groups are authored, zero executed; no native build, test, source/SDK execution or binary launch occurred here. Independent review and authorized runtime qualification are pending.

## Established gap and pinned source

Pi v0.99.1 remains pinned at `d86654abb8862e201933517d6f1fce9f88dd117f`.

- [extensions/types.ts:732–738](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/extensions/types.ts#L732), SHA256 `de0ccce3b8b222d19a2ecef24902fc72aab62b6573129cbf886bc064dd6ab505`: SessionBeforeSwitchEvent has type=session_before_switch, reason=new/resume and optional targetSessionFile. It has no sessionFile field or event AbortSignal.
- [agent-session-runtime.ts:133–147](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/agent-session-runtime.ts#L133), SHA256 `b163ce35d2b6e3ba1fc080c0674044a3f1cd9faec93cc2ed015e31b69780bf4a`: emitBeforeSwitch constructs those fields; only cancel===true cancels. [Switch to an existing session passes resume and the target file before opening it](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/agent-session-runtime.ts#L203). New session passes new with no target path.

At the qualified native base, NativeExtensionActivation's existing-file callback emits `{type:session_before_switch,sessionFile:target.Path}`, omitting reason and using the wrong path field. Its new-session branch already emits `{type:session_before_switch,reason:new}`. Existing typed switch/creation veto support, replacement session_start and session_compact are implemented; no nonexistent session_switch/session_fork topic is introduced. Broader event catalog work such as session_info_changed, session_shutdown, session_compact_failed and tree events is not included or inferred complete from this correction.

## Production correction and preservation

The existing callback body is extracted into NativeSessionBeforeSwitchBinding to test the actual production preflight hook without executing a native package loader. NativeExtensionActivation installs its BeforeSwitchAsync method with exactly Binding.Snapshot and the existing host closing token. The provider and owner keep their existing shared BeforeSwitch delegate routing, including SDK switch and catalog resume. The extracted body first awaits the same typed BeforeSessionSwitchAsync proposal with actual previous/target session IDs, target path and selected leaf. A typed cancel still returns false before ordinary observations.

After typed continue, the only behavioral change is the observation payload: exactly type=session_before_switch, reason=resume, targetSessionFile=the actual staged target.Path. No legacy sessionFile, target session ID, generation, branch wrapper or additional signal field is serialized. Internal core switch/resume reasons and the separate new/fork creation path are unchanged.

Registry dispatch methods are unchanged. Generic observation dispatch still receives the same operation and host session tokens, owns the same captured registration/context leases and propagates the same callback, context cleanup and cancellation failures. Host target admission, configuration validation, typed veto ordering, source/target reservation, acknowledged writer retirement, committed replacement event ordering and every original physical rollback/retirement/disposal join remain unchanged. Replacement session_start and compaction binding/dispatch are untouched. No terminal/provider transport, credentials, approvals, package/trust policy, compiler or task-10 compaction fixture changes.

## Explicit native profile differences

This is a field-contract correction within the existing bounded native host profile, not complete upstream before-switch event parity. Upstream emits before opening the requested file; native preflight runs after staging/validating the actual target and reserving both coordinators. Consequently a missing native target emits no observation. Native observation context exposes the old attached source generation, not writable staged target authority. Native target paths are absolute admitted paths.

Source handlers share the event result ABI and source runner report/continue behavior. Native typed lifecycle veto handlers are a separate API evaluated before ordinary observations; ordinary native observations have no return value and currently propagate noncancellation errors to staged rollback. They receive an existing linked cancellation token although the source payload has no AbortSignal. Native immutable JSON/context limits and captured binding revision also remain. This slice changes none of those authority, cancellation, error or ordering choices and claims no larger parity acceptance.

## Authored regressions and owned paths

Five in-process integration groups use the actual new production binding, ExtensionRegistry, NativeSessionSnapshotProvider, PersistentSessionLifecycle and ReplaceableAgentSession:

1. Actual direct switch, catalog resume and registered SDK command switch: exact three-field immutable payload with no legacy field; old source snapshot/branch/generation; typed proposal before observer before actual attachment publication; fresh generation-two target; no provider/tool execution.
2. Typed veto: no ordinary observation, unchanged source checkpoint length/authority/fault state, and actual staged coordinator disposed before completion.
3. Actual missing-target storage OpenFailed and precanceled admission: neither typed nor ordinary callbacks, no admitted target preflight and unchanged source authority.
4. Held original observer: caller cancellation reaches its existing native linked token; owner and registry disposal remain pending until release; original canceled operation retains known preflight-token origin and the original observer OCE as inner cause; actual stage and source closure both join without publication. Finally releases first, joins the original invocation and independently joins both original disposal tasks even if a prior join fails.
5. Original IOException identity propagates through unchanged generic dispatch, stage rollback leaves the source checkpoint/authority usable, and a subsequent actual switch succeeds with the failed observation removed.

The runner directly awaits these groups without an outer deadline race. Temporary cleanup checks the exact unique child directory and rejects linked roots; deletion follows successful registry and owner closure. No live reads use File.ReadAllBytesAsync against an active writable session. The held fixture waits callback entry or original settlement, never just an orphaned callback signal. All success-path callback assertions propagate through the unchanged generic dispatcher rather than a report/continue method.

The production loader and CLI/RPC binaries are not run; their shared activation wiring is statically inspected. Source differential coverage and the disclosed before-switch timing/result/cancellation differences remain open. Existing qualified groups are unchanged. Review and a new coordinator-authorized five-group run are required before claiming corrected acceptance.

## Preparation argument-type correction

The coordinator's frozen preparation on `57d094c9bc96a2ccbf8bec4640308af4def591e2` passed host info, SDK version and locked restore, then failed full-solution build with CS1503 at NativeBeforeSwitchPayloadTests.cs:141: SessionEntry[] cannot convert to ImmutableArray<SessionEntry>. The remaining 20 preparation steps and all tests were unexecuted; original process/output owners joined. Failure handoff SHA256 `895295b45455122764c4e03e8534fee563405c9042e5bda957c7807d11305ca5` and the original logs remain unchanged.

The isolated correction replaces only that one-element new[] argument with a collection expression targeted by the sole SessionLogStore.AppendAsync(ImmutableArray<SessionEntry>, CancellationToken) signature. Seed records, assertions, gates, tokens, owner/context cleanup and original joins are byte-for-byte unchanged after reversing that argument syntax. Related fixture arrays are string arrays used only for foreach; no AddRange or PromptAsync overloads exist in this fixture. InvokeCommandAsync receives JsonData; switch/resume request types and native delegate signatures were checked statically. No production source or existing test runner changes. Five groups remain authored, zero executed; this correction is uncompiled. Independent review and fresh coordinator-authorized preparation/runtime qualification remain required; historical failures and provider/compiler holds are retained.
