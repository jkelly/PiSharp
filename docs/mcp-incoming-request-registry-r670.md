The pinned original Pi 0.99.1 client implements `setRequestHandler` with method
replacement and a remover that deletes only the same handler identity. Incoming
dispatch selects that handler, sends method-not-found for an unknown method,
creates incoming cancellation ownership, invokes the handler and awaits the
physical result/error send. Its cancellation notification cancels the incoming
request controller. The native shared channel currently has a fixed roots reply.

This additive registry implements the corresponding method table and exact
handler identity remover. Each dispatch snapshots its callback, owns a unique
incoming ID, and directly joins the actual callback Task and admitted response
sender Task. A null result becomes an empty object. Unknown methods produce
-32601; callback failures produce -32603. Opaque result/parameter JSON and string
IDs are copied. No callback exposes transport acquisition or new host authority.

Incoming cancellation requests stop but do not abandon the original callback.
Cancellation scheduling itself is joined before disposing its owner. Dispatch
keeps complete original Task failure evidence, including faulted OCE and actual
canceled originals, and sends an error before reporting a callback failure.
Both callback and physical-send faults remain in the dispatch and close evidence.
Close fences new admission and joins held callbacks, held physical sends and
cancellation callbacks. Borrowed callback/sender close reentry is refused.
Methods, inflight requests, cumulative requests and UTF-8 responses are bounded.

The owning shared channel must still install this registry, route arbitrary
incoming requests and notifications/cancelled IDs into it, provide its explicitly
admitted physical sender, and join its close. Dynamic roots may be installed as
a roots/list handler through that same owner. Those shared channel/Host/Program
files remain coordinator-reserved; this slice does not claim actual wire wiring.
MCP OAuth remains open. Register the eight authored Cases once and their prefix
in the direct-original await branch before root-owned qualification. No compiler,
test, Node, network, credential, or policy operation was performed by this author.
