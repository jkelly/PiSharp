# MCP terminal resource evidence successor R299

Prepared named-server list/read failures now retain the exact joined leaf Task and its complete Exception aggregate in the bounded native evidence queue before the exception reaches prepared executor error projection. Nested request/progress/saver callback exceptions retain their physical Original tasks. Faulted OCEs remain fault originals; canceled leaf originals are excluded because the leaf already proves borrowed callback/token cancellation provenance. Prior callback faults followed by cancellation are faulted aggregates and therefore retained.

Three authored prepared-pipeline controls cover named list/read multifault originals and read faulted OCE originals. Controls require an error ToolResult plus native evidence containing both original exceptions and the exact physical channel Task. They are unregistered and unexecuted.

Static source audit: existing imports provide Task/Exception through net10 implicit usings, McpResourceCallbackException through PiSharp.Extensions.Mcp.Resources, and McpInvocationIdentity through PiSharp.Extensions.Mcp.Runtime. No new types/imports/project references, no ref-struct locals across await. Exact CLI/test SDK projects use default Compile globs, CLI references Runtime and Agent; test references CLI/CodingAgent; Directory.Build.props is net10.0, nullable/implicit usings enabled; global.json pins 10.0.401. Existing R298 dependency pins remain applicable. Whitespace diff checked. No compilation or execution claim.

Factory integration is a separate successor, absent here; immutable 4894 is preserved.
