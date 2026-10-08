# CodingAgent exact test selection

This source-only runner change adds `--exact-name <full name>`. Matching uses `StringComparison.Ordinal`, and zero or multiple matches are rejected before the test execution loop. Repeating the option or omitting/blanking its argument fails argument parsing. Combining it with `--filter` is rejected before fixture configuration and case construction. Existing substring filtering and no-selector behavior remain unchanged.

The report retains `filter` and `availableTests`, adds `exactName`, and marks an exact singleton as `completeSuite: false`. Child/terminal-worker entry routes and package-mode registration are unchanged; exact selection applies to whichever test list the existing mode constructs. No case is added, removed or renamed. The original resource fixture's ten names and order remain unchanged.

The source basis is corrected resource fixture commit `7c0d59b49a09b5c384fa8d0d9ea14d5ca042e455`. This is a new candidate, not the historical exact10f binary. Existing R340 evidence, source and products must not be relabeled. Independent source review and fresh locked restore/build/product receipts plus a new finite grant are required before any execution.

Future bounded acceptance controls, all unexecuted here:

- An existing full resource case name selects one identical report name and `completeSuite: false`.
- A nonexistent full name and a case-changed full name reject before any case body.
- A strict substring of a real name rejects; the old `--filter` with that substring retains its previous semantics.
- Both selector options in either argument order, repeated exact-name, missing argument and whitespace-only argument reject.
- A separately admitted synthetic runner inventory containing duplicate full names rejects before either body. Do not add duplicate production cases merely to run this control.
- No-selector broad mode and existing child/package routes retain their previous inventory and dispatch behavior.

Only static source/diff inspection was performed. No compilation, test, AST, Node, native process or grant replay occurred.
