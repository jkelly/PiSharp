# Pi 1.5.2 test runtime

This manifest and lock describe the current source candidate's exact extension runtime. They are separate from the preserved `runtime-fixture-150` evidence. No production cache-admission marker or package payload is committed here.

To reproduce the test installation from the repository root:

```powershell
New-Item -ItemType Directory -Force artifacts/node-runtime-152
Copy-Item tools/NodeBridge/runtime-fixture-152/package*.json artifacts/node-runtime-152/
npm ci --prefix artifacts/node-runtime-152 --ignore-scripts --no-audit --no-fund
node tools/NodeBridge/installed-runtime-abi.mjs artifacts/node-runtime-152/node_modules
```

ExtensionParity uses `PISHARP_TEST_PI152_NODE_MODULES` or the default `artifacts/node-runtime-152/node_modules`. The test harness compares the manifest, dependency lock and installed package versions before admitting the fixture. CI runs the standalone ABI check before .NET suites, so an absent or wrong runtime fails instead of silently skipping the required ABI check.

The production-installer job remains separate: it uses `PISHARP_TEST_INSTALL_PI_RUNTIME=1` in a fresh cache and verifies registry integrity through `PiNodeRuntime`. Installing this fixture does not satisfy that production provenance gate.
