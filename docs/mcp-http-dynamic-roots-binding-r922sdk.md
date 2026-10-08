# Admitted dynamic roots in the actual HTTP channel factory

`AdmittedMcpHttpChannelFactory.Create` now accepts one explicitly supplied
`McpAdmittedDynamicRootsProvider` as its final optional parameter. A multicast
provider and simultaneous `McpRuntimeOptions.Roots` are rejected before any HTTP
effect. Exact `McpServerEntry` reference admission remains unchanged. No default
provider, URI access, filesystem grant, network acquisition or token lookup is
introduced.

Each factory acquisition creates its own actual `McpJsonRpcRequestChannel` and
installs its existing dynamic handler before start. The existing channel adds the
roots capability to initialize, dispatches each incoming `roots/list` through the
provider, owns the physical response and joins the handler/incoming request/wire
originals on close. Existing per-channel limits and runtime generation admission
remain in force. The borrowed HttpClient and callback's external resources are
not disposed by this factory. If installation fails, the newly owned channel is
closed and its exact close original joins before admission failure is rethrown;
simultaneous cleanup faults retain the complete close aggregate.

Four new controls use a real HttpClient with a supplied in-memory HttpMessageHandler;
they create no socket or network listener. The real McpServerRuntime initialization
and refresh drive actual JSON-RPC/SSE incoming roots requests and changing response
values. Other vectors cover pre-effect admission, held callback retirement and
stale requests, callback close reentry, independent channel ownership, and exact
user-original multi-fault references in channel close. Each recorded Task is
directly joined, including held user work and unexpected reentry originals.

Channel close may report a failed physical response after wire retirement. The
held-close control retains that graph and requires the actual incoming response
phase; it does not relabel a failed send as successful protocol completion.

This closes the factory wiring gap of the earlier roots handler/channel slice.
It does not add automatic roots-change notifications or claim full MCP/OAuth
parity. Caller-admitted OAuth HTTP challenge/token integration is a separate next
slice; the existing six OAuth state controls do not qualify an HTTP flow.

Source only. Root must register `McpDynamicRootsHttpBindingTests.Cases()` once,
retain `CapturedOriginals` as subsidiary-original evidence, review/import the
three owned paths, and perform any admitted qualification. No compilation,
native test, Node, credential operation or network operation was performed here.
