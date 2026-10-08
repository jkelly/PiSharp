# Successful binding and concurrent close

The close-observed branch after candidate binding now independently initiates
all candidate owned stops before invoking lifetime cancellation. This mirrors
the terminal binding-failure settlement ordering. Existing stop initiation is
idempotent, and original cancellation, stop, body and runtime failures remain
joined by the existing settlement.

The additional source-only control holds a successful binder after registration,
starts concurrent close, waits for both stop-entry witnesses, then lets binding
return. Cancellation synchronously waits for physical stop; runtime release is
separately held and multifault originals are checked in reload and close.
Unlike the binding-failure control, this is concurrent-close coverage, not a
deterministic baseline deadlock witness: public close normally starts the same
stops itself before canceling, and registration is refused after close admission.
No tests or compiler were run.
