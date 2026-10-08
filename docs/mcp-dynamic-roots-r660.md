The pinned Pi 0.99.1 MCP client evaluates its optional asynchronous roots provider
separately on every incoming `roots/list` request and returns a copied roots array.
The existing native request channel only installs a static array before start.
This additive source slice implements an admitted per-request metadata handler,
without modifying that shared channel or the previously frozen registration facade.

`McpDynamicRootsHandler` invokes the single injected provider for each admitted
request, captures its `ValueTask.AsTask()` once and awaits the exact original. Its
response copies the roots JSON, preserving opaque metadata and number text. It
enforces UTF-8 response and cumulative request bounds. Root URIs are inert values;
the handler does not inspect files or acquire any filesystem capability.

The existing MCP operation owner fences new admission and joins admitted work on
close. Repeated close returns the same original Task. A borrowed roots callback
cannot join its own handler close. Fault wrappers retain the exact original Task
and full unflattened Task aggregate, including duplicate/nested/empty failures.
Synchronous throws retain a null original. A faulted OCE remains a fault; only an
actually canceled original with requested matching owned token becomes cancellation.
Close records and rethrows callback failures rather than silently dropping them.

The owning channel must still advertise roots capability, install this handler for
incoming `roots/list`, await its result, own the physical response/error send and
join handler close with channel close. Those shared channel/host files are reserved
to the coordinator. This source does not claim wire integration or implementation
of arbitrary incoming server-request handler registration. MCP OAuth remains open.

Nine authored controls cover current roots on repeated requests; held callback
join/fresh-admission fencing; one-consume ValueTask; nested/duplicate fault
identity; faulted OCE; genuine matching-token cancellation; empty synchronous
aggregate; callback close reentry; and array/UTF-8/request bounds. They use injected
metadata callbacks and held Task originals only. They are unregistered and
unexecuted; root must add `McpDynamicRootsTests.Cases()` once and its prefix to the
direct-original await branch before qualification. No compiler, native process,
Node, network, credential, or policy operation was performed by this author.
