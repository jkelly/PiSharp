# Expanded terminal submission text

The retained host and the acknowledged-receipt host both expand registered paste markers, then trim the expanded string using ECMAScript WhiteSpace and LineTerminator characters. This follows the pinned Pi v0.99.1 `Editor.submitValue()` expression. U+FEFF is trimmed; U+0085 is retained. The acknowledged path reserves the already trimmed text, so its submit callback and accepted receipt carry the same value.

Draft input and snapshots are unchanged. Existing callback ownership, resets, cancellation, receipt identities, acknowledgment timing, recall and kill-ring behavior retain their existing boundaries. This change does not qualify integrated frontend history/map/layout wiring, Source dictionary word segmentation, escaped layout, IME, platform behavior or the complete P5-08 package.

The native key-alias fixture still checks every complete draft through `abc` followed by four logical LF characters, then checks the Source-trimmed submitted value `abc`. Its earlier untrimmed submission expectation and the resulting failure are preserved at candidate `f3c73ce13219a382d25aad98d2d9c550c9859c43`. This single expected-value migration follows the required Source submission behavior; no key, draft, reset, callback or cleanup assertion is removed.
