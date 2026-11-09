# Unix child-process boundary

`pisharp_env_native.c` owns `openpty`/pipes, `fork`, `setsid`, controlling-terminal setup, descriptor redirection, signal reset, `chdir`, and `execve`. No managed delegate or CLR code runs in the forked child. The child sends pre-exec failures back through a close-on-exec error pipe. The parent gets either an executed process with descriptors or a reaped failure, never an unacknowledged child.

The daemon waits with `waitid(..., WNOWAIT)`. Its leader stays unreaped until ownership cleanup kills the process group and calls `waitpid`, preventing reuse of the group id before cleanup. Polling I/O uses nonblocking descriptors and 100 ms intervals, with descriptor locking around close/read/write to prevent fd-reuse races. PTY output stays binary and Linux's PTY `EIO` is treated as EOF. The caller owns ordering and bounded wire backpressure.

`PiSharp.Env.Native.targets` builds the shim automatically on Linux/macOS. A C11 compiler (`cc`) and the platform development headers are required. Linux links `libutil`; macOS uses its system `openpty` implementation. Development builds copy the shared library into dependent output directories. Windows does not build the shim and the terminal operation returns `ENOTSUP`.

For remote deployment, publish on the target OS and architecture with NativeAOT. The target builds a static archive and resolves P/Invokes directly into the executable; the published daemon needs no adjacent native library:

```sh
dotnet publish src/PiSharp.Env.Daemon -c Release -r linux-x64 -p:PiSharpEnvNativePublish=true -o artifacts/env-linux-x64
dotnet run --project tests/PiSharp.Env.Terminal.Tests -- --external-daemon artifacts/env-linux-x64/PiSharp.Env.Daemon
```

On Apple Silicon, use `-r osx-arm64`; on Intel macOS, use `-r osx-x64`. Cross-compilation is not configured by this target. Do not deploy a framework-dependent development executable through the single-artifact SSH catalog.

`PiSharpEnvNativePublish` sets `PublishAot` locally on the daemon project. It does not assert that every unused API in referenced provider libraries supports AOT. The packaging helper keeps final ILC/trim diagnostics fatal for the executable's actual reachable closure.

`PiSharp.Env.Terminal.Tests` tests the wire client on every platform. Linux/macOS additionally exercise the real PTY: terminal identity, input ordering, resize, foreground jobs, raw non-UTF-8 output, default login shell, spawn failures, cancellation, and disconnect cleanup. Passing Windows tests proves the protocol client and explicit unsupported-platform contract; it does not qualify Unix execution.
