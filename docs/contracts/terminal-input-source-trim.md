# Terminal editor submission and original Input callbacks

The native terminal expands paste markers and applies ECMAScript `trim()` before dispatching a submitted line. This matches the unchanged Pi v0.99.1 `Editor.submitValue()` implementation. A direct RPC prompt retains its supplied text.

The original `input-transform.ts` factory handles a raw `?quick ` by publishing its usage warning. The genuine editor submits `?quick`, and the same callback returns `continue` without a notification. That warning branch cannot be reached by an editor submission whose query contains only ECMAScript whitespace. The unchanged five Commands/Input process groups retain the raw-RPC warning assertion.

The real-terminal workflow therefore types ` PiNg ` and requires the genuine callback to receive exactly `PiNg`, publish `pong`, and return `handled`. Both original physical workflows retain their command, dialog, cancellation, policy, provider, durable save/reopen, native-cell and joined cleanup assertions. The unchanged 47-group trim probe separately retains the `U+FEFF` checks; the physical harness searches literal native cells while this preview renders that character as an escape.

`fixtures/native/terminal-input-submit-source.json` records two identical fresh captures of the unchanged Source editor and original callback. Constructor UI scheduling and callback notification sinks are fixture inputs. The editor methods and extension factory/callback run unchanged; this capture does not qualify the whole Source frontend or session recovery ordering.
