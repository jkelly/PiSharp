# Reserved native observation binding

Source-only successor to 115592c76221ec53eafabc4d7671ecebb1633066. No build,
test, provider, terminal, or native process was run for this change.

The retained R378 checkpoint singleton failed in its real CLI create command
before launching the cooked frontend. Its public receipt does not retain the
inner exception. Independent source diagnosis establishes a deterministic
conflict: initial runtime binding holds the persistent replacement reservation,
but both native observation adapters previously called ordinary configuration
methods that reject that reservation.

Both adapters now use the existing owner-binding authority boundary. The owner
requires the exact current attachment and captures a reservation only inside its
active runtime binding and resource registration callback context. The persistent
setters recheck exact attachment/callback authority and that same live reservation
under the session gate, retain idle,
input-mutation and callback self-wait checks, and only assign the observer.
No callback runs under either gate. The owner gate is released before entering
the session gate. Session-to-owner identity validation follows the existing reload
publication order and prevents a stale setter overwriting a newly published view.
Outside authorized binding the ordinary availability guards remain
the path; replacement authority is neither released early nor made public.

Four existing registered cases gain controls, without changing case names or the
886 CodingAgent registrations:

- RuntimeAttachmentGenerationTests.BoundBeforeExposure installs both observers
  during initial and replacement binding, exercises real metadata delivery, and
  rejects direct ordinary configuration, value-equal foreign attachments, stale
  attachments, and calls with execution-context flow suppressed while binding.
- NativeHostReloadTests.Durable installs both observers on initial and reload
  binding and requires metadata delivery on generations 1 and 2.
- SessionCompactObservationTests.HostPublisherFault installs its actual throwing
  compaction observer in the reserved binder, then requires the original failure
  after a durable compaction and continued writer usability.
- NativeExtensionSessionCommandTests.CheckpointChat retains the real create and
  cooked A-B-A checks; before frontend launch it also requires a complete durable
  log, startup marker and joined checkpoint activation disposal.

Static checks: existing net10.0 project references and implicit usings cover the
unchanged public observation types; no async method introduces a ref-struct local.
Future qualification requires a new source/product admission and finite grant.
The failed R378 report, physical publication pins and settlement remain unchanged.
