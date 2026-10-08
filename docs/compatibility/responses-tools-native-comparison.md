# Native comparison with the genuine Responses tools SDK capture

The native comparison in [ResponsesToolDeclarationDifferentialTests.cs](../../tests/PiSharp.Transport.Tests/ResponsesToolDeclarationDifferentialTests.cs) constructs complete request bodies from the five immutable [Responses tools SDK inputs](../../fixtures/pi-v0.99.1/responses-tools-sdk/core.input.json) and compares them with the genuine captured SDK HTTP bodies. The independent capture and its unchanged-source/runtime/dependency provenance are described in [Responses tools SDK reference](responses-tools-sdk-reference.md). This comparison does not generate expected output from native code or alter the reference files.

Before parsing any fixture, the test checks all three SHA-256 byte identities:

| Fixture | SHA-256 |
| --- | --- |
| `core.input.json` | `ea1a88b031568f136cc288a0918a13f22c477b9efd54f2804a7cc5344fa451a7` |
| `core.expected.json` | `46c27eb741e3edbd38fbe2442545944f1de1d6209c073efce96dfb2208e2d37d` |
| `oracle.lock.json` | `b761486187f3172040d8d64fee0786888cd5dfca4dac5c5e169319af6f2ff74d` |

It also checks source identity `d86654abb8862e201933517d6f1fce9f88dd117f`, five captured SDK requests, and the capture's recorded unchanged-source and offline controls. Each raw captured body is verified against its recorded UTF-8 hash before JSON parsing, then compared with its separately retained `bodyJson` observation.

The native request factory receives every system/user message from each source input, including all declaration deltas. No historical declaration field is removed or rewritten. Model identity, session ID, temperature, maximum output tokens, and mid-conversation system-message capability come from the fixture. Each case explicitly supplies its `supportsStrictMode` value. The fixture's disabled additional-tool, tool-search and grammar capabilities are checked rather than inferred; this corpus observes the complete active standard function list.

| Genuine case | Expected active declaration order | Expected `strict` field |
| --- | --- | --- |
| Initial declaration | read, write | Present, false |
| Remove read and add edit | write, edit | Present, false |
| Re-add read | write, edit, read | Present, false |
| Replace write in place | write, edit, read | Present, false |
| Replace write with strict capability unavailable | write, edit, read | Missing |

The comparison includes the entire parsed SDK body: input messages, envelope options, declaration names/descriptions, full JSON parameters and opaque extensions. Only object property ordering is insignificant. Numbers retain their raw JSON token spelling; strings compare decoded ordinal values; array order and missing/null/false distinctions remain significant. Duplicate decoded object names are rejected, including escaped duplicates nested inside objects. Each canonical input body is checked for unchanged bytes after native construction.

Independent authored negative controls require differences for numeric spelling, wide integer tokens, absent/null/false fields, changed names/descriptions/schema fields, and reordered tools, required arrays, enums and opaque arrays. A deliberately reversed native strict capability must also differ from each complete captured body. These controls exercise the comparison boundary; the golden inputs themselves contain no unsafe-precision or fractional schema constraints and do not qualify broader JavaScript numeric precision behavior.

This is a candidate offline request-body comparison pending registration and execution in the isolated native test suite. It performs request construction only and acquires no HTTP connection. The SDK headers, response hooks, streamed events, tool choice and actual tool execution retained in the reference are outside this test's comparison. Strict constrained-sampling transformations, grammar/custom declarations, additional-tool anchors and tool search remain separate unfinished qualification work; the five observations do not establish general provider parity.
