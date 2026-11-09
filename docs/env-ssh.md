# SSH environments and verified daemon artifacts

`PiSharp.Env.SshConnection` is lazy: its first request runs platform detection, verifies/deploys a daemon, and waits for the protocol-1 token handshake. Each reconnect rechecks the remote SHA-256. The hello version and OS/architecture must agree with the trusted catalog before the connection is exposed. Calls that lost their connection are not replayed.

Supply a trusted `DaemonCatalog` containing artifacts produced by the native release build. These must be self-contained single executables; the Unix PTY shim must be linked statically for publication. A managed development build with adjacent runtime/native dependencies is not a deployable catalog artifact. Each entry identifies platform (`windows`, `linux`, `darwin`), architecture (`x64`, `arm64`), daemon version, relative executable path and SHA-256. Manifest paths cannot escape the artifact directory. The catalog verifies local bytes before uploading them. `Bootstrap.InstallAsync` provides the same verification and content-addressed atomic installation for local use. `SshConnection.ConnectAsync` eagerly starts and validates the connection.

```csharp
var catalog = await DaemonCatalog.LoadAsync("release/daemon-catalog.json");
await using var ssh = new SshConnection(new SshConnectOptions
{
    Host = "workstation", KnownHostsFile = "state/known_hosts",
    HostKeyAlias = "pi-env-workstation", Catalog = catalog,
});
var info = await ssh.Connection.InfoAsync();
// Pass ssh.Connection to RemoteExecutionEnv; keep ssh alive while using it.
```

SSH uses a structured argv, strict checking against the application's known-hosts file, a stable key alias, batch authentication and no forwarding, sharing, configured remote commands or local commands. ConnectTimeout defaults to 15 seconds; individual detection/check commands default to 60 seconds and upload to five minutes. The overall connection start also has a deadline. SSH config aliases, jump hosts and keys remain handled by the system `ssh`. Credentials are not logged. Startup error diagnostics are sanitized and bounded; applications should still treat arbitrary remote diagnostic content as untrusted.

`Ssh.ScanHostKeyAsync` captures keys into an isolated temporary file using the same SSH route and returns SHA-256 fingerprints. It does not trust the keys. Compare fingerprints independently before explicitly calling `Ssh.AcceptHostKeyAsync`. Changed keys fail until explicitly removed through `Ssh.ForgetHostKeyAsync`; there is no auto-accept during connection. Host-key writes are serialized in process and atomically replaced.

Remote commands use POSIX single-quote escaping or PowerShell UTF-16LE encoded scripts. Windows detection distinguishes cmd.exe and PowerShell. Unsafe cmd.exe executable paths containing expansion/metacharacters fail explicitly. POSIX `LoginShell` starts through the remote `$SHELL -lc`, so its startup output is handled by the token handshake. Binaries are uploaded into a temporary file and renamed only after a matching SHA-256; a POSIX host without sha256sum, shasum or openssl fails closed. Older content-addressed daemon files are retained to avoid interfering with active sessions; cache pruning is an explicit administrator operation.

## Android backend

The native .NET catalog does not invent Android support. An explicitly supplied `upstream-rust` Android entry must include a pinned source commit, MIT license, test status and SHA-256. Without such an entry Android selection throws `PlatformNotSupportedException`.

The pinned [upstream npm release](https://www.npmjs.com/package/@earendil-works/pi-env/v/1.5.0) contains actual arm64/x64 Android executables. `tools/Env/Get-UpstreamAndroidDaemon.ps1 -OutputDirectory artifacts/upstream-env-v1.5.0` downloads its fixed tarball, verifies the hard-coded npm SHA-512 before extracting only those two binaries, verifies their individual SHA-256 and ELF machine headers, and emits a catalog. It does not execute downloaded code or run npm scripts. Call `DaemonCatalog.LoadAsync` on the emitted `daemon-catalog.json` to opt into that backend.

Source tag: `f9a26365568127461c47a07a37dcb42c140a3ac5` (`v1.5.0`). The upstream `.github/workflows/env-daemons.yml` uses locked Cargo dependencies, Android API24 and NDK `27.2.12479018`; its Android builds are excluded from the native executable smoke step. Local evidence here verifies package integrity and ELF architecture, not Android runtime behavior. No live SSH test was run; the offline suite uses a subprocess fixture and never opens the network.
