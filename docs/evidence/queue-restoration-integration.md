# Atomic queue restoration integration

This source-only successor starts at aca9ce52fc6611b94ce743b2d049014df7fb3869. It preserves that candidate and merges atomic API a8e62270869daf0f6a31bd0a4c720d9d5bd093d3 plus separate test correction aeec350c4a5c3f8c3f222aa30b4ce2ef630b604d, then exact terminal caller 51d208bb1580021cbd646212641c497612d62dbe. Source merge: 8044c539dc7686265f2fac4b9f2932dafb666ed7.

The correction replaces exactly two live-store byte reads in AtomicQueueTakeTests with an asynchronous FileStream reader using FileShare.ReadWrite. The active-run gate and all original direct joins are unchanged. No production behavior was changed by that correction. Its verification remains source-only.

The terminal branch contains cherry-picks of the queue ownership correction and atomic API. Merge conflicts arose in duplicated atomic test/metadata, status files and the CodingAgent/RPC test runners. Resolution kept the corrected atomic test, all provider/core history, the native before-start/request-context/nested-host direct-await branches, and the terminal restoration direct-await branch. No production source conflict required rewriting either implementation. The historical standalone API and caller commits remain preserved.

[Static inventory](queue-restoration-integration-inventory.json) records exact matches for four corrected core files and ten caller production/test files, eight registration checks, all 14 current held-I/O source pins, and existing project-reference targets. All checks matched; no pin was rewritten. Other historical source evidence remains tied to its original candidate. The aggregate has not been compiled, tested or used for source/runtime capture.

The coordinator reports atomic production and terminal caller production independently statically clear. Independent verification of the test correction and refreshed combined source is still required. Seven core queue groups, six RPC groups and eleven terminal restoration cases are authored in the participating lanes, not executed here.

Provider test-ownership P2 remains open: its outer transport timeout and service-tier cleanup can detach work. The provider worker owns that correction. Provider binding b2b24e9e is not imported pending that correction. Terminal active abort work is not imported. Combined admission and runtime preparation remain blocked on the provider correction and authorization. Eventual targets remain Transport, Extensions, CodingAgent, RPC, Terminal and held-I/O suites. No native build/test execution occurred and no original phase gate is closed.
