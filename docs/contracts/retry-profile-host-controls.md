# Ordinary retry profile host controls

`RetryProfileHostTests.Cases()` contains four source-only, unregistered and unexecuted groups based on coordinator commit `e2cf714a8de70cad3c432f56a272b9c27741acd4`.

The controls exercise actual ordinary public host APIs:

1. Both `SessionCommands.RunAsync` overloads create a durable fixture without calling the admitted persistence callback. Ordinary RPC defaults report retry enabled. `RunWithSettingsAsync` receives one explicit synthetic settings read with retry disabled and reports `autoRetryEnabled=false`. Idle `abort_retry` acknowledges without creating retry events or persistence work.
2. A real `set_auto_retry` command holds the exact callback task. Before release there is no command acknowledgment, and a concurrent `get_state` still reports the old preference. After the original acknowledges, the successful command response and next state expose the updated preference.
3. Explicit input EOF causes actual callback-token cancellation while the persistence original remains held. The host must remain pending until that original settles; the test then joins its original host task.
4. A failed persistence callback produces a failed command response and leaves the old live preference. Final owning cleanup must retain the failed original work, producing the host's cleanup failure result.

Fixtures use the existing bounded JSONL connection, synthetic settings text and an unused authored offline Responses script. No prompt or provider retry is admitted by these controls. They do not resolve credentials or supply real HTTP/process/server dependencies. No ambient settings write is authorized; persistence is supplied only through the explicit callback.

Actual source APIs were inspected: `set_auto_retry` requires a boolean `enabled`; `get_state` projects `autoRetryEnabled` and `isRetrying`; supported retry event names are `auto_retry_start` and `auto_retry_end`. Public JSONL exposes failure disposition rather than exception object identity. Core retry leaf controls remain responsible for original exception reference identity and cancellation cleanup aggregation.

Required coordinator registration: `.Concat(RetryProfileHostTests.Cases())`, once, with directly awaited tasks. Suggested filter: `retry-profile.`. No builds, tests, package acquisitions, subprocesses, HTTP exchanges, credentials or live API calls were executed. Shared production, project, lock and `Program.cs` files are untouched by this leaf. Independent review and admitted native qualification remain required.
