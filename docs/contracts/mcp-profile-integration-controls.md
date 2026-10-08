# Ordinary profile MCP integration controls

Source-only controls in `tests/PiSharp.CodingAgent.Tests/McpProfileIntegrationTests.cs` exercise the ordinary command admission seam rather than a separately assembled test lifecycle.

Five groups are authored, unregistered and unexecuted:

1. Both public `SessionCommands.RunAsync` create overloads acquire a nonempty MCP capture using the actual native catalog and final policy. The durable log must acknowledge the captured direct tool declaration and its withdrawal before closing. Original channel, discovery and native resources must each close once. Final reported checkpoint, physical/selected leaf and message count must match the closed file after withdrawal.
2. A separate ordinary resume acquires fresh discovery before restoring the historical session, sends a synthetic Responses turn, and checks the actual provider request tool names and unchanged physical history prefix. Its acknowledged withdrawal and final report are also checked against the actual closed file.
3. An internal ordinary profile creates its actual lifecycle and replaces its attachment. Exposed generations, exact native executable identities, discovered tool presence, final policy identity and old ownership retirement are checked before the new attachment is used.
4. Independently substituting the native registry or final policy must reject acquisition. Discovery cleanup is held, proving the rejected operation remains pending and native cleanup has not started. Release then checks both original cleanup exception objects occur once and no writer remains.
5. Ordinary `RpcSessionCommand.RunAsync` restores a session with nonempty supplied discovery, acknowledges its supported `get_state` command, and retains resources until the synthetic input explicitly permits EOF. The test joins the original host task after EOF and checks cleanup.

The only MCP channel methods admitted by these fixtures are initialize, initialized notification and tools/list. Tools/call fails if invoked. The profile's existing final file policy is preserved; the tests supply no execution grant for a captured MCP tool. RPC has no fabricated get_tools endpoint in these controls.

The controls exposed a production startup-selection gap: native `InitialSystem` declarations precede MCP acquisition, and registry discovery alone does not activate a captured tool in provider requests. The coordinator supplied startup correction `55a24980b2a498a13f1e59bed49586c1f616119a`, owner-before-writer teardown/report correction `903dd7040f85439b0733bdb32ccde0679970be88`, and local-name correction `5971069dce48f295fb2ea6a0f75b3c65990cf76d`. This leaf is based on the exact last commit. Its production postimages belong to the coordinator; this leaf adds only the controls, this document and its evidence state.

Required integration: add `.Concat(McpProfileIntegrationTests.Cases())` once to the coordinator-owned runner, with directly awaited original tasks. Suggested filter: `mcp-profile.`. No builds, tests, package acquisitions, subprocesses, HTTP exchanges, credentials or live servers were executed while authoring. Shared production, project, lock and runner files are untouched by this leaf. This is neither native qualification nor complete MCP parity evidence.
