# Reload stop-order correction (source-only)

This separate correction applies to `98b03b02253fe9e7a6748dd670ad27811ac24e90`.
The prior reload path canceled the old attachment and awaited discovery before
starting owned-resource stops. A synchronous lifetime cancellation callback or an
already admitted catalog read could depend on physical stop, producing a cycle.

`ReloadReservation.InvalidateCoreAsync` now marks the old attachment stale and
initiates every existing owned stop before invoking lifetime cancellation or
joining discovery. It uses the existing `InitiateOwnedResourceStopsAsync` method,
matching normal owner shutdown. Later retirement still joins the original stop
and body tasks. Cancellation, initiation, discovery and retirement errors continue
through the existing complete-fault aggregation. No replacement task, detached
wait or new resource owner is introduced.

`NativeHostReloadStopOrderTests.Cases()` adds two source-only controls:

- A lifetime cancellation callback waits synchronously for held physical stop;
  concurrent close remains joined, and original cancellation, two stop faults
  (including a faulted OCE), and body failure all remain observable.
- An admitted catalog read waits for held physical stop; reload starts stop before
  draining that read, concurrent close remains joined, and the actual returned
  catalog stream is disposed before the original discovery task completes.

Both controls require reload to start stop **before** launching close, so normal
close cannot accidentally make the old ordering pass. Timeout assertions have a
failure-only dependency release followed by direct joins of all original work;
they do not abandon a blocked test operation.

The new fixture is separate from existing shared helpers. Root-owned registration
must add `.Concat(NativeHostReloadStopOrderTests.Cases())` and admit its
`native-host-reload-stop-order.` prefix to the desired bounded selection. The
existing ten reload cases are unchanged. No compiler, build, tests, native code,
provider, network or package operation was executed in authoring this correction.
