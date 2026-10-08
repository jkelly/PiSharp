# Accepted Mistral source composition

This source composition adds the accepted Mistral provider, CLI selection, model catalog and authored regressions onto the host source with safe diagnostic case progress. Selected Mistral postimages are copied exactly from `da0cae3dd354293a7fe6d240fd5bd9fcbd6b7d46`; the shared Coding runner receives only one `MistralLiveSelectionTests.Cases()` registration and preserves its exact-name and safe-progress behavior. Host/session/tools files from the other provider branch are not imported.

Passing results retain their original producers: transport55 passed on `f5b796b67a44721f765c4fda12342bf4bca0139e`; CLI3 passed on `da0cae3dd354293a7fe6d240fd5bd9fcbd6b7d46`. Neither result is a new run of this composition. Three added CLI cases change the composed source catalog membership and therefore safe-progress ordinals. The prior host result remains a timed-out Coding886 run with810 PASS,8 FAIL and68 missing outcomes; no Coding889 acceptance is claimed.

Source-only checks compare selected postimages, registration count and host-file preservation, plus `git diff --check`. No compiler, test, API call or credential operation was performed. Main approval, exact fresh preparation and bounded diagnostic qualification remain pending.
