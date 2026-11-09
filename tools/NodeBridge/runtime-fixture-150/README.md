# Installed Pi 1.5.0 ABI fixture

This is test input for `../installed-runtime-abi.mjs`, not a product installation or an admitted runtime cache. Its `package.json` is exactly `PiNodeRuntime.PackageJson()`, including the Pi telemetry override. `package-lock.json` records the published npm package URLs and integrity hashes used by the CI fixture. The `gap.runtime-committed-fixture-matches-production-manifest` regression detects manifest drift.

The lock started from the isolated package installation in `artifacts/node-runtime-150`, then was normalized with npm `install --package-lock-only --ignore-scripts --no-audit --no-fund` after replacing the manifest with the production value. No package payloads, `node_modules`, credentials, or fabricated production provenance are committed here.

To prepare the expected fixture location from the repository root:

```powershell
New-Item -ItemType Directory -Force artifacts/node-runtime-150
Copy-Item tools/NodeBridge/runtime-fixture-150/package*.json artifacts/node-runtime-150/
npm ci --prefix artifacts/node-runtime-150 --ignore-scripts --no-audit --no-fund
node tools/NodeBridge/installed-runtime-abi.mjs artifacts/node-runtime-150/node_modules
```

Use an empty destination for a clean-install qualification. `npm ci` checks the committed lock integrity; product installations separately use `PiNodeRuntime.VerifyAsync` and their own policy-qualified cache directory. The fixture does not claim product admission, and its existence does not bypass that process.

ExtensionParity consumes `PISHARP_TEST_PI150_NODE_MODULES` (or the repository's default `artifacts/node-runtime-150/node_modules`) only through its test host collaborators. It validates the reviewed manifest, complete lock package versions/URLs/integrities, and installed package manifests before use. Without a fixture, ordinary cases explicitly use compatibility modules and do not invoke npm. An explicitly configured `PISHARP_PI_RUNTIME_DIR` inside a sandbox still exercises production cache behavior.

Production installer qualification is a separate required CI gate: set `PISHARP_TEST_INSTALL_PI_RUNTIME=1` only with `EXTPARITY_FILTER=runtime.pi-packages-installed-with-verified-provenance`. The gate must require that case to execute and pass, with no skip. Without the explicit opt-in it reports `SKIP_NO_PRODUCTION_RUNTIME`; fixture replay and ABI tests must not be relabeled as production cache admission.
