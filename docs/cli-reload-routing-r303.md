# CLI reload routing source slice

Base: `e3bce705cfc415d788ee74d6c207ddc322bc1972`, tree
`443a2667f317fe50717eec19766c9c770c3d85ad`.

The actual `RpcSessionCommand.RunAsync`, settings overload, and presentation
overload now accept an explicit `NativeHostReloadAdmission`. After attaching the
profile's real session owner, the command configures that admission. Existing
RPC `prompt` input containing `/reload` enters a profile-owned input admission
and calls `NativeHostReloadCoordinator` on that same owner. The persistent
session's existing replacement reservation explicitly permits its own idle
input callback; RPC releases its transition lock before entering that callback.
No new dispatcher protocol, activation owner, process, provider or resource
discovery path is introduced.

Flags are copied and callback cardinality validated by the admission constructor.
The profile retains each exact attachment's coordinator and returns its original
task directly. A subsequent attachment receives candidate payload/flags only
from an acknowledged successful publication, including publication with later
lifecycle failures. An unrelated navigation attachment is refused. Repeated
requests for an old attachment replay its existing attempt, including failure;
this slice does not add a command to replace a failed admission or retry it.
Owner shutdown continues to join the coordinator's retained original task.

`/reload` with arguments, images, streaming delivery, steer or follow-up delivery
is refused. Without explicit admission it is refused before provider work.
Ordinary inputs continue through the captured skills/templates/extension input
admission. The existing persistent input boundary sanitizes command failures
to its existing prompt-admission failure message; the profile API retains the
explicit missing-admission error and the owner retains complete reload faults.

This is an actual caller routing slice, not complete built-in extension refresh.
There is no ambient default admission. The embedding caller must supply actual
staging, registry commit, cleanup and lifecycle operations. This slice does not
replace the profile's readonly native extension or captured template/skill
objects, nor add refreshed completion/catalog views or final-close lifecycle
dispatch for such replacement objects. Those host-view hookups remain necessary
before claiming built-in extension reload support. CLI Program and Terminal
composition remain unchanged. Startup MCP/resource admissions are a disjoint
parent-owned change.

Three source-only controls in `CliReloadRoutingTests.Cases()` cover the public
RPC caller, absent admission without provider work, and exact original task /
generation / concurrent close joins through an actual profile and persistent
session. Register this group in the parent-owned test Program before execution.
Fixture inspection checked actual command argument prefix, script envelope,
profile/lifecycle signatures, runtime lease constructor, registry bindings,
codec namespace, transport/policy interfaces, JSONL stream overrides, and the
net10.0 nullable/implicit-using project graph. No ref structs cross awaits.
No compiler, tests, native activation, provider calls or network were run.
