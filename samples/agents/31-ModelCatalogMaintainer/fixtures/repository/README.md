# Example destination repository files

This folder is a source-controlled example of the files a maintenance PR proposes. It is not a nested Git checkout. `catalog-maintained/models.overlay.json` and `model-catalog-report.md` were generated from the fictional recorded data in `../../fixture.json`. They are not live model prices.

For a repeatable local no-change check, use this overlay with `--maintain --baseline <this-folder>/catalog-maintained/models.overlay.json` and the default fixture input.
