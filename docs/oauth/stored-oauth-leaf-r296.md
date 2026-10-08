# Stored OAuth lifecycle leaf R296

Native base: `6170379555cc9d817cdf3a360857a319ab8ba31b`, tree `8405c976f4ded93746e6ff88532e26d6a4ce0cfc`. Original source pin: Pi v0.99.1 `d86654abb8862e201933517d6f1fce9f88dd117f`. The checkout contains no AGENTS.md or .agents directory. The task-root ORIGINAL_PORT_REMAINING_RECONCILIATION_AND_ASSIGNMENTS_R295.md assigns this bounded leaf. This is authored source, not runtime or parity acceptance.

The owner resolves already admitted stored OAuth snapshots through explicitly injected synthetic dependencies. `IAdmittedOAuthCredentialSource.ModifyAsync` owns the authoritative per-provider atomic mutation boundary; it must keep its original callback joined until settlement and publish only after successful completion with an active token. There is no implementation of a real credential store. Synthetic test sources implement this contract in memory.

`StoredOAuthLifecycle.ResolveAsync` reads, checks expiry, and refreshes within that atomic mutation after checking the authoritative snapshot again. The default window is 300000 milliseconds, equality refreshes, and an explicit requested window is clamped to at least the default. Rotation publishes before the owner returns its snapshot. Explicit minimum validity is checked after rotation; the implicit window only triggers refresh. Missing/logged-out credentials return null, with no environment fallback. Independent owners share concurrency protection through the injected source contract. One owner also holds a per-provider original-task lane across read, refresh and publication, so cancellation cannot detach noncooperative work or let reauthentication overwrite an active operation. Other providers can progress independently.

Each refresh receives a linked cancellation token and a fifteen-second TimeProvider deadline. Cancellation/deadline requests do not detach the original task. Successful late completion cannot publish. Original dependency rejection precedes a later caller cancellation, including faulted tasks containing an OperationCanceledException with an owned token. Foreign cancellation is retained as a refresh failure. OAuthLifecycleException retains both the exact observed original exception and the original task's AggregateException, including multiple original faults. Its ToString and JSON converter expose only the failure stage; tokens and provider metadata are omitted from snapshot diagnostics. Owners must avoid directly logging retained original exceptions.

`ReauthenticateAsync` installs an already admitted synthetic completion through the same provider lane and atomic source. Login state validation, PKCE generation/verification, callback listeners, actual token acquisition, provider auth derivation, and login-completion admission remain with the future provider/host owner. This method is not proof of those flows. No force-refresh API was invented: the pinned auth resolver uses expiry/minimum-validity semantics; Models.refresh(force) applies to catalog freshness rather than forcing OAuth rotation.

## Source links and physical source hashes

All paths below are relative to the pinned original upstream directory. SHA256 hashes describe the read-only files inspected in this task.

| Path | SHA256 | Evidence |
|---|---|---|
| packages/ai/src/oauth.ts | 1250a2a276dc310849e64a0fd306a896d42e47ad0fc8d9394279eaee4e11cdff | Type-only extension compatibility barrel; no built-in flow implementation. |
| packages/ai/src/auth/resolve.ts | d03ed5bf0abcbdc482df879f209156f18f8bfdc381869db77301a8273f990007 | resolveStoredOAuth: default window, atomic double-check, deadline, rotation, explicit postcheck and retained causes. |
| packages/ai/src/auth/credential-store.ts | 50ebde56b80b5862025e031719006ea303edd9aa80fec780fbc64319bb9e2847 | InMemoryCredentialStore.enqueue/modify keeps original callback work within serialization and leaves prior credential intact on rejection. |
| packages/ai/src/models.ts | 4739010c7e4f7596607b1dd495b9d5ab6cd921e29f55c5c331259a04b7253269 | resolveRefreshCredential uses zero window for catalog refresh; separate from request authentication. This leaf implements the request resolver window. |
| packages/ai/test/oauth-auth.test.ts | cf48a806cd38f76c5cb3a38921672acc8d665fab57d6beb7111ca51e4bc43991 | Stored OAuth resolution and provider-specific derivation/metadata witnesses. |
| packages/coding-agent/src/core/auth-storage.ts | 32d36165760959766807b8797042e69b65a65c7aad7d1b79a2621c41fe29b48d | AuthStorage.modify authoritative read/publication; reload preserves last valid in-memory snapshot. No physical storage effects ported. |

## Held controls and integration

OAuthLifecycleTests.cs is in the existing PiSharp.Authentication.Tests project (implicit source inclusion). The root owner registers `await OAuthLifecycleTests.RunAsync(Check);` after the existing local Check declaration in Program.Main. This leaf does not edit Program or project files.

Fifteen authored controls cover immutable/redacted metadata, missing/fresh snapshots, equality and metadata rotation, independent-owner concurrency, logout during authoritative recheck, explicit/implicit validity, multiple retained original task faults, cancelled noncooperative joins/lane retention, late fault priority, deterministic deadline/noncooperative joins, read fault authority, faulted owned-token OCE, foreign cancellation, independent-provider progress, and reauthentication publication ordering. All are held and unexecuted. No build, tests, Node/native execution, provider/network requests, credential reads/writes, project/package/lock changes or trust/security overrides occurred. Static source review and git diff --check are the only verification performed.

Remaining blockers: source-clear/compile and producer-specific synthetic runtime qualification; provider/host source admission; real credential source effects and actual login/PKCE/listener/refresh/toAuth integration; original differential OAuth corpus and authenticated inference acceptance. No original parity row or phase gate closes here.
