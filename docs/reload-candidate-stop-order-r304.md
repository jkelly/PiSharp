# Candidate binding failure stop order

Source-only correction on `e3bce705cfc415d788ee74d6c207ddc322bc1972`
(tree `443a2667f317fe50717eec19766c9c770c3d85ad`).

A candidate runtime binder may register owned resources and cancellation
callbacks before throwing. Reload settlement formerly canceled its lifetime
before starting those resources' physical stops. A cancellation callback
joining physical stop therefore prevented that stop from ever starting.

Settlement now uses the existing `InitiateOwnedResourceStopsAsync(Candidate)`
before canceling the candidate lifetime. This starts all selected stops and
joins their initiation acknowledgments, not their physical completion.
Cancellation, stop/body retirement, runtime release and writer retirement still
join their existing originals and preserve every collected fault. There is no
new owner, cancellation wrapper, detached cleanup or rollback of old authority.

`ReloadCandidateStopOrderTests.Cases()` supplies one source-only control with
two candidate resources, a throwing binder, synchronous cancellation waiting
for physical stop, multifault stop/body tasks and held multifault runtime
release (including faulted OCE siblings). Both stop-entry witnesses must occur
before concurrent close is launched, so close cannot conceal the baseline
deadlock. Failure teardown releases latches and joins all launched work.

The parent must register the case group in its owned test Program. No tests,
compiler, native activation or provider work were executed. Source inspection
checked the existing registration, reservation, lease, lifecycle, task, codec,
transport and policy signatures and the inherited net10.0 project graph.
