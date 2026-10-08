# Reviewed provider successor integration

This fresh worktree starts at preserved combined candidate 9d0047031ff68ee27dd66cb48d17af4d53191a7e and merges exact provider 9e39e22d051db76828ab2a55a29c214055cd75ab. Source merge: fee37521c9d3065217793e89f98b09710f21f0d1. All prior lane commits and both statically closed corrections remain ancestors.

The common ancestor is 16d779cb04c385135bb45d84f0bc17341d592593. The added provider commits are 7bd90a19a60ac3d7c5d5dfb54fdb75c69ca4b69d (tool choice), 6ffb8241e3ad3b45de40de5879f488081a130574 (service tier), and 9e39e22d051db76828ab2a55a29c214055cd75ab (Unicode correction). The alternative standalone Unicode commit was not applied.

There were no textual merge conflicts. All eight files in the provider delta have identical Git blobs in the merged tree. ResponsesToolChoiceTests.Cases and ResponsesServiceTierTests.Cases each appear once in the transport runner; existing reasoning, incomplete and declaration registrations remain. The transport test project already references AI and Agent, and its default compile glob includes both new test files. No reference or test-registration repair was required. Existing test bodies and historical assertions were not edited by integration.

The provider handoff reports four tool-choice groups and three service-tier groups as authored. Its source claims were reported independently statically clear by the coordinating reviewer. This integration performed only source, Git blob, registration and project-reference checks; no compilation, native tests, upstream execution, pricing calls or live provider requests. Fresh integrated runtime evidence remains required after authorization. Factory and mapper ServiceTier options remain explicit separate host configuration, as documented by the provider; integration does not infer one from the other.

The earlier oversized-signature caveat remains: those variants reject at the checkpoint, before EOF/read/completed endings. Nothing here upgrades them to runtime coverage of later paths.

The separate atomic queue API a8e62270869daf0f6a31bd0a4c720d9d5bd093d3 is preserved and not merged. Unfinished terminal restoration 1616eba436f9f81948572466c2e47e960d8072fc is not imported. No original phase gate is closed.
