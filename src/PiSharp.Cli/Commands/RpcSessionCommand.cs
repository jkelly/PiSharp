using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Globalization;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Rpc;
using PiSharp.Rpc.Protocol;
using PiSharp.Sessions.Storage;
using PiSharp.Cli.Extensions;
using PiSharp.Rpc.Ui;
using PiSharp.Sessions.Lifecycle;

namespace PiSharp.Cli.Commands;

/// <summary>Explicit-path offline durable RPC host. Stdout contains only the dispatcher's shared JSONL records.</summary>
public static class RpcSessionCommand
{
    public const string Usage = "session rpc --session <existing absolute JSONL> --workspace <existing absolute directory> " +
        "--offline-script <absolute JSON> [--offline-api openai-responses|anthropic-messages|openai-completions] [--offline-images true|false (anthropic-messages|openai-completions)] [--leaf <id>|--root] [--allow-read <absolute file>] [--allow-write <absolute file>] " +
        "[--bash-executable <absolute file> --bash-spill-root <existing workspace directory> --allow-bash-command <exact command> [--bash-timeout <seconds>]] " + NativeExtensionConfiguration.Flags + " " + SessionCatalogCommand.Flags + " " + CreationFlags;
    public const string CreationFlags = "[--session-mode open|new-memory|new-lazy]";
    private static readonly JsonlTransportOptions Framing = new(MaximumFrameBytes: 1_048_576, MaximumJsonDepth: 32, MaximumPendingWrites: 32);
    private sealed record Arguments(string Session, string Workspace, string Script, bool Latest, string? Leaf,
        ImmutableArray<string> Reads, ImmutableArray<string> Writes, string OfflineApi, OfflineBashAuthorization? Bash,
        NativeExtensionConfiguration? Extension, bool SupportsImages, ImmutableArray<SessionCatalogStore> Stores, string SessionMode);

    public static Task<int> RunAsync(string[] args, Stream stdin, Stream stdout, TextWriter stderr,
        CancellationToken cancellationToken = default) => RunCoreAsync(args, stdin, stdout, stderr, null, cancellationToken);

    internal static Task<int> RunWithPresentationAsync(string[] args, Stream stdin, Stream stdout, TextWriter stderr,
        IRpcExtensionUiPresentationObserver presentation, CancellationToken cancellationToken, Func<bool>? userShutdown = null,
        Func<RpcSessionShutdownSettlement, ValueTask<RpcTerminalStoppedAcknowledgment>>? stopTerminalAndJoin = null) =>
        RunCoreAsync(args, stdin, stdout, stderr, presentation, cancellationToken, userShutdown, stopTerminalAndJoin);

    private static async Task<int> RunCoreAsync(string[] args, Stream stdin, Stream stdout, TextWriter stderr,
        IRpcExtensionUiPresentationObserver? presentation, CancellationToken cancellationToken, Func<bool>? userShutdown = null,
        Func<RpcSessionShutdownSettlement, ValueTask<RpcTerminalStoppedAcknowledgment>>? stopTerminalAndJoin = null)
    {
        ArgumentNullException.ThrowIfNull(stdin); ArgumentNullException.ThrowIfNull(stdout); ArgumentNullException.ThrowIfNull(stderr);
        OfflineSessionProfile? profile = null; PersistentAgentSession? session = null;
        RpcExtensionUiCoordinator? ui = null; RpcSessionDispatcher? dispatcher = null; JsonlReader? reader = null; JsonlWriter? writer = null;
        InputObservation? observedInput = null; OutputObservation? observedOutput = null; OfflineGate? gate = null;
        SessionStorageBackend? backend = null; Exception? operationFailure = null;
        var cleanupFailures = new List<Exception>();
        try
        {
            var parsed = Parse(args);
            cancellationToken.ThrowIfCancellationRequested();
            if (!stdin.CanRead || !stdout.CanWrite) throw Invalid();
            if (!Directory.Exists(parsed.Workspace)) throw new SessionCommandException(SessionCommandFailure.WorkspaceMissing);
            backend = parsed.SessionMode == "open" ? null : new SessionStorageBackend(Path.GetDirectoryName(parsed.Session)!,
                parsed.SessionMode == "new-memory" ? SessionStorageMode.InMemory : SessionStorageMode.LazyLocal,
                new(MaximumFileBytes: 8_388_608));
            var turns = await SessionCommands.ScriptAsync(parsed.Script, cancellationToken).ConfigureAwait(false);
            gate = OfflineGate.From(turns);
            ui = parsed.Extension is null ? null : new(presentationObserver: presentation);
            profile = await OfflineSessionProfile.CreateAsync(parsed.Workspace, parsed.Session, parsed.Script,
                turns, parsed.Reads, parsed.Writes, cancellationToken, gate.BeforeSendAsync, parsed.OfflineApi, parsed.Bash, parsed.Extension, ui,
                async (diagnostic, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    await stderr.WriteAsync((JsonSerializer.Serialize(new { schemaVersion = 1, type = "extension_input_diagnostic",
                        eventName = diagnostic.EventName, ownerId = diagnostic.OwnerId, ownerGeneration = diagnostic.OwnerGeneration,
                        registrationId = diagnostic.RegistrationId, failure = diagnostic.Failure.ToString() }) + "\n").AsMemory(), token).ConfigureAwait(false);
                    await stderr.FlushAsync(token).ConfigureAwait(false);
                }, modelSupportsImages: parsed.SupportsImages).ConfigureAwait(false);
            long ticks = 0; var started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long Clock() => started + Interlocked.Increment(ref ticks);
            var options = new PersistentAgentSessionOptions(UseLatestLeaf: parsed.Latest, SelectedLeafId: parsed.Leaf,
                AgentOptions: new(Loop: new(MaximumTurns: 64, MaximumTranscriptMessages: 1024)),
                SessionLogStoreOptions: new(ReaderOptions: new(MaximumInputBytes: 8_388_608, MaximumLines: 10_000, MaximumRecords: 10_000)));
            string NextId() => "rpc-" + Guid.NewGuid().ToString("N");
            var catalog = new SessionCatalog(parsed.Stores.IsEmpty ? [new("session-directory", Path.GetDirectoryName(parsed.Session)!)] : parsed.Stores,
                fileSystem: backend);
            var lifecycle = new PersistentSessionLifecycle(profile.Registry, Clock, NextId, options, catalog: catalog, backend: backend);
            session = parsed.SessionMode == "open"
                ? await lifecycle.OpenAsync(new(parsed.Session, parsed.Latest, parsed.Leaf), profile.SelectedModel, cancellationToken).ConfigureAwait(false)
                : await lifecycle.CreateAsync(parsed.Session, new PiSharp.Sessions.Serialization.SessionEntryCodec().Parse(JsonSerializer.Serialize(new
                    { type = "session", version = 3, id = NextId(), timestamp = DateTimeOffset.FromUnixTimeMilliseconds(Clock()).ToString("O", CultureInfo.InvariantCulture), cwd = profile.Workspace })),
                    profile.SelectedModel, cancellationToken).ConfigureAwait(false);
            if (parsed.SessionMode != "open") await session.ConfigureAsync(new(SystemMessage: new("system", profile.InitialSystem)), cancellationToken).ConfigureAwait(false);
            if (!string.Equals(SessionCommands.Absolute(session.WorkingDirectory), profile.Workspace,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new SessionCommandException(SessionCommandFailure.WorkspaceMismatch);
            profile.AttachOwner(session, options, Clock, NextId, parsed.Stores.IsEmpty ? null : parsed.Stores, lifecycle);
            observedInput = new InputObservation(stdin, gate);
            var outputFraming = parsed.Bash is null ? Framing : Framing with { MaximumFrameBytes = 8 * 1024 * 1024 };
            observedOutput = new OutputObservation(stdout, gate, outputFraming.MaximumFrameBytes);
            reader = new JsonlReader(observedInput, Framing);
            writer = new JsonlWriter(observedOutput, outputFraming);
            // The profile retains resource ownership across the terminal-stopped boundary. Dispatcher cleanup
            // already fences RPC/UI admission and joins its original run, reader, callbacks and writer.
            dispatcher = new(session, writer, Clock, [new(profile.SelectedModel, profile.SelectedModelWire)],
                options: new(MaximumOutputBytes: outputFraming.MaximumFrameBytes),
                sessionOwnership: RpcSessionOwnership.Borrowed, inputAdmission: profile.InputAdmission, extensionUi: ui,
                extensionCommandCatalog: profile, sessionOwner: profile.Sessions,
                sessionStartup: token => profile.StartLifecycleAsync(parsed.SessionMode == "open" ? "resume" : "new", token),
                summaryGenerator: profile.SummaryGenerator, recoveryDesiredMaxOutput: profile.OriginalDesiredMaxOutput);
            await dispatcher.RunAsync(reader, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) { operationFailure = error; }
        // A run/subscriber failure may poison session authority even when resource
        // cleanup succeeds. Retain that original fault separately from later cleanup.
        var operationFault = (profile?.Sessions?.Current.Session ?? session)?.Snapshot.Fault;
        // Phase one runs even after startup failure. Cleanup has no caller token and joins each original.
        gate?.Release();
        if (dispatcher is not null)
        {
            // RunAsync and DisposeAsync join the same original task. Its retained operation
            // failure is not a new cleanup failure merely because disposal observes it again.
            try
            {
                var dispatcherSettlement = await dispatcher.SettleAsync().ConfigureAwait(false);
                if (ReferenceEquals(operationFailure, dispatcherSettlement.CompletionFailure))
                    operationFailure = dispatcherSettlement.OperationFailure;
                else if (dispatcherSettlement.OperationFailure is { } dispatchFailure)
                    operationFailure = operationFailure is null ? dispatchFailure : new AggregateException(operationFailure, dispatchFailure);
                cleanupFailures.AddRange(dispatcherSettlement.CleanupFailures);
            }
            catch (Exception error) { cleanupFailures.Add(error); }
        }
        else if (writer is not null) await Cleanup(() => writer.DisposeAsync()).ConfigureAwait(false);
        if (reader is not null) await Cleanup(() => reader.DisposeAsync()).ConfigureAwait(false);
        if (profile?.Sessions is { } owner) await Cleanup(() => new(owner.StopAdmissionAndJoinAsync())).ConfigureAwait(false);
        else if (session is not null) await Cleanup(() => new(session.StopAdmissionAndJoinAsync())).ConfigureAwait(false);
        if (ui is not null) await Cleanup(() => ui.DisposeAsync()).ConfigureAwait(false);
        if (observedInput is not null) await Cleanup(() => observedInput.DisposeAsync()).ConfigureAwait(false);
        if (observedOutput is not null) await Cleanup(() => observedOutput.DisposeAsync()).ConfigureAwait(false);
        if (profile is not null) cleanupFailures.AddRange(profile.ProcessCleanupFailures);

        RpcSessionShutdownSettlement? settlement = null;
        if (stopTerminalAndJoin is not null)
        {
            settlement = new((operationFailure is null ? cleanupFailures : cleanupFailures.Prepend(operationFailure)).ToImmutableArray(),
                (profile?.Sessions?.Current.Session ?? session)?.Snapshot, profile?.ProcessCleanupReceipts ?? []);
            try
            {
                var acknowledgment = await stopTerminalAndJoin(settlement).ConfigureAwait(false);
                _ = settlement.Validate(acknowledgment);
            }
            catch (Exception error) { cleanupFailures.Add(error); }
            finally { cleanupFailures.AddRange(settlement.CaptureAcceptedTerminalFailures()); }
        }
        // Phase two: every owned resource close is attempted and joined, including an unattached session.
        // Retired UI scopes cannot repaint or admit input while lifecycle disposal is running.
        var runtimeFailures = new List<Exception>();
        if (profile is not null) await RuntimeCleanup(() => profile.DisposeAsync()).ConfigureAwait(false);
        if (session is not null) await RuntimeCleanup(() => session.DisposeAsync()).ConfigureAwait(false);
        cleanupFailures.AddRange(runtimeFailures);
        settlement?.CompleteRuntimeCleanup(runtimeFailures.ToImmutableArray());
        try
        {
            var finalSession = profile?.Sessions?.Current.Session ?? session;
            if (finalSession is not null)
            {
                var state = finalSession.Snapshot;
                var bytes = backend is null ? new FileInfo(finalSession.Path).Length : backend.GetMetadata(finalSession.Path).Bytes;
                var newFault = state.Fault is not null &&
                    (operationFailure is null || !ReferenceEquals(state.Fault, operationFault));
                if (newFault || !state.IsDisposed || bytes != state.Log.CommittedByteLength || backend?.ActiveWriterCount > 0)
                    throw new SessionCommandException(SessionCommandFailure.CommandFailed);
            }
        }
        catch (Exception error) { cleanupFailures.Add(error); }
        settlement?.Complete((operationFailure is null ? cleanupFailures : cleanupFailures.Prepend(operationFailure)).ToImmutableArray());
        if (cleanupFailures.Count > 0)
            return await Fail("CleanupFailed", "RPC host cleanup failed after joining owned work; inspect durable state.", 1).ConfigureAwait(false);
        if (operationFailure is null)
        {
            if (cancellationToken.IsCancellationRequested && userShutdown?.Invoke() != true)
                return await Fail("Canceled", "RPC host canceled after settling owned work; inspect durable state.", 1).ConfigureAwait(false);
            return 0;
        }
        // A user shutdown never suppresses a foreign OCE or an actual startup/run/cleanup error.
        if (operationFailure is OperationCanceledException canceled && cancellationToken.IsCancellationRequested && canceled.CancellationToken == cancellationToken)
            return await Fail("Canceled", "RPC host canceled after settling owned work; inspect durable state.", 1).ConfigureAwait(false);
        if (operationFailure is SessionRuntimeRegistryException registryError && registryError.Failure == SessionRuntimeRegistryFailure.UnknownModel)
        {
            var error = new SessionCommandException(SessionCommandFailure.OfflineProviderMismatch);
            return await Fail(error.Failure.ToString(), error.Message, 2).ConfigureAwait(false);
        }
        if (operationFailure is SessionCommandException commandError)
            return await Fail(commandError.Failure.ToString(), commandError.Message, commandError.Failure == SessionCommandFailure.CommandFailed ? 1 : 2).ConfigureAwait(false);
        if (operationFailure is NativeExtensionException extensionError)
            return await Fail(extensionError.Failure.ToString(), extensionError.Message, extensionError.Failure == NativeExtensionFailure.CleanupFailed ? 1 : 2).ConfigureAwait(false);
        return await Fail("RpcHostFailed", "RPC host failed after owned cleanup; inspect durable state before retrying.", 1).ConfigureAwait(false);

        async ValueTask Cleanup(Func<ValueTask> close)
        { try { await close().ConfigureAwait(false); } catch (Exception error) { cleanupFailures.Add(error); } }
        async ValueTask RuntimeCleanup(Func<ValueTask> close)
        { try { await close().ConfigureAwait(false); } catch (Exception error) { runtimeFailures.Add(error); } }
        async Task<int> Fail(string code, string message, int result)
        {
            await stderr.WriteAsync(JsonSerializer.Serialize(new { schemaVersion = 1, status = "failed", code, message,
                effectsMayHaveCompleted = true, cleanupFailureCount = cleanupFailures.Count }) + "\n").ConfigureAwait(false);
            await stderr.FlushAsync().ConfigureAwait(false); return result;
        }
    }

    // Startup UI settings must use the same validated workspace as the actual RPC composition.
    internal static string ResolveStartupWorkspace(string[] args) => Parse(args).Workspace;

    private static Arguments Parse(string[] args)
    {
        if (args is null || args.Length is < 8 or > 264 || args[0] != "session" || args[1] != "rpc") throw Invalid();
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var reads = ImmutableArray.CreateBuilder<string>(); var writes = ImmutableArray.CreateBuilder<string>(); var root = false;
        var bashCommands = ImmutableArray.CreateBuilder<string>();
        var extensionTools = ImmutableArray.CreateBuilder<string>();
        var deniedExtensionTools = ImmutableArray.CreateBuilder<string>();
        var extensionCommands = ImmutableArray.CreateBuilder<string>();
        var stores = ImmutableArray.CreateBuilder<SessionCatalogStore>();
        for (var index = 2; index < args.Length; index++)
        {
            var key = args[index];
            if (key == "--root") { if (root) throw Invalid(); root = true; continue; }
            if (key is not ("--session" or "--workspace" or "--offline-script" or "--offline-api" or "--offline-images" or "--leaf" or "--allow-read" or "--allow-write" or
                "--bash-executable" or "--bash-spill-root" or "--allow-bash-command" or "--bash-timeout" or "--session-store" or "--session-mode" or
                "--extension-package" or "--extension-manifest" or "--extension-approval" or "--extension-snapshot-root" or "--enable-extension-tool" or "--deny-extension-tool" or "--enable-extension-command") ||
                ++index >= args.Length) throw Invalid();
            var value = args[index];
            if (key == "--allow-read") reads.Add(SessionCommands.Absolute(value));
            else if (key == "--allow-write") writes.Add(SessionCommands.Absolute(value));
            else if (key == "--allow-bash-command") bashCommands.Add(value);
            else if (key == "--enable-extension-tool") extensionTools.Add(value);
            else if (key == "--deny-extension-tool") deniedExtensionTools.Add(value);
            else if (key == "--enable-extension-command") extensionCommands.Add(value);
            else if (key == "--session-store") { if (stores.Count >= 32) throw Invalid(); stores.Add(SessionCatalogCommand.ParseStore(value)); }
            else if (!options.TryAdd(key, value)) throw Invalid();
        }
        if (!options.TryGetValue("--session", out var session) || !options.TryGetValue("--workspace", out var workspace) ||
            !options.TryGetValue("--offline-script", out var script) || reads.Count > 128 || writes.Count > 128) throw Invalid();
        options.TryGetValue("--leaf", out var leaf);
        options.TryGetValue("--session-mode", out var sessionMode); sessionMode ??= "open";
        if (sessionMode is not ("open" or "new-memory" or "new-lazy") || sessionMode != "open" && (root || leaf is not null)) throw Invalid();
        options.TryGetValue("--offline-api", out var offlineApi);
        options.TryGetValue("--bash-executable", out var bashExecutable); options.TryGetValue("--bash-spill-root", out var bashSpillRoot);
        options.TryGetValue("--bash-timeout", out var bashTimeout);
        var bash = OfflineSessionProfile.BashOptions(bashExecutable, bashSpillRoot, bashCommands.ToImmutable(), bashTimeout);
        var model = OfflineSessionProfile.SelectModel(offlineApi);
        options.TryGetValue("--offline-images", out var imageInput);
        if (imageInput is not (null or "true" or "false") || imageInput == "true" && !OfflineSessionProfile.CanConfigureImageInput(model.Api)) throw Invalid();
        options.TryGetValue("--extension-package", out var extensionPackage); options.TryGetValue("--extension-manifest", out var extensionManifest);
        options.TryGetValue("--extension-approval", out var extensionApproval); options.TryGetValue("--extension-snapshot-root", out var extensionSnapshots);
        var extension = NativeExtensionConfiguration.Optional(extensionPackage, extensionManifest, extensionApproval, extensionSnapshots, extensionTools.ToImmutable(), deniedExtensionTools.ToImmutable(), extensionCommands.ToImmutable());
        if (root && leaf is not null || leaf is { Length: 0 } || leaf?.Length > 4096 ||
            leaf is not null && (!Unicode(leaf) || leaf.Any(char.IsControl))) throw Invalid();
        try { if (stores.Count > 0) _ = new SessionCatalog(stores); } // Validate before acquiring providers/plugins.
        catch (ArgumentException) { throw Invalid(); }
        var sessionPath = SessionCommands.Absolute(session);
        if (sessionMode != "open" && stores.Any(store => !string.Equals(Path.GetFullPath(store.Directory), Path.GetDirectoryName(sessionPath),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))) throw Invalid();
        return new(SessionCommands.Absolute(session), SessionCommands.Absolute(workspace), SessionCommands.Absolute(script),
            !root && leaf is null, leaf, reads.ToImmutable(), writes.ToImmutable(), model.Api, bash, extension, imageInput == "true", stores.ToImmutable(), sessionMode);
    }
    private static SessionCommandException Invalid() => new(SessionCommandFailure.InvalidArguments);
    private static bool Unicode(string value)
    {
        for (var index = 0; index < value.Length; index++)
            if (char.IsHighSurrogate(value[index])) { if (++index >= value.Length || !char.IsLowSurrogate(value[index])) return false; }
            else if (char.IsLowSurrogate(value[index])) return false;
        return true;
    }

    /// <summary>Authored offline pacing only. No gate text becomes code, authorization or an admitted command.</summary>
    private sealed class OfflineGate(string? releaseId)
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _entered;
        private int _faulted;
        public static OfflineGate From(ImmutableArray<JsonData> turns)
        {
            string? id = null;
            for (var index = 0; index < turns.Length; index++)
                if (turns[index].Value.TryGetProperty("rpcGate", out var spec))
                {
                    if (index != 0 || spec.ValueKind != JsonValueKind.Object || spec.EnumerateObject().Count() != 1 ||
                        !spec.TryGetProperty("releaseOnGetStateId", out var field) || field.ValueKind != JsonValueKind.String)
                        throw new SessionCommandException(SessionCommandFailure.InvalidScript);
                    id = field.GetString();
                    if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || !Unicode(id) || id.Any(char.IsControl))
                        throw new SessionCommandException(SessionCommandFailure.InvalidScript);
                }
            return new(id);
        }
        public async ValueTask BeforeSendAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfFaulted();
            if (releaseId is not null && Interlocked.Exchange(ref _entered, 1) == 0)
            {
                // Actual HTTP work cancellation releases and joins this trusted callback before response acquisition.
                using var canceled = cancellationToken.UnsafeRegister(_ => Release(), null);
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            // A failed write/flush wakes cleanup, but cannot authorize acquiring the scripted response.
            ThrowIfFaulted();
        }
        public void Release() => _release.TrySetResult();
        public void Fault()
        {
            Interlocked.Exchange(ref _faulted, 1);
            Release();
        }
        private void ThrowIfFaulted()
        {
            if (Volatile.Read(ref _faulted) != 0) throw new SessionCommandException(SessionCommandFailure.CommandFailed);
        }
        public void Flushed(JsonData record)
        {
            var body = record.Value;
            if (releaseId is not null && body.TryGetProperty("type", out var type) && type.GetString() == "response" &&
                body.TryGetProperty("command", out var command) && command.GetString() == "get_state" &&
                body.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == releaseId &&
                body.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True) Release();
        }
    }

    private abstract class BorrowedObservation(Stream inner, OfflineGate gate) : Stream
    {
        protected Stream Inner { get; } = inner;
        protected OfflineGate Gate { get; } = gate;
        public override bool CanRead => Inner.CanRead;
        public override bool CanWrite => Inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) Gate.Release(); base.Dispose(disposing); }
        public override ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class InputObservation(Stream inner, OfflineGate gate) : BorrowedObservation(inner, gate)
    {
        // Authoritative input is read/parsed only by JsonlReader. Abort, EOF and read failure cause dispatcher
        // shutdown to cancel the real handler token; observing them here must not release work into success.
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Inner.ReadAsync(buffer, cancellationToken);
    }

    private sealed class OutputObservation(Stream inner, OfflineGate gate, int maximumFrameBytes) : BorrowedObservation(inner, gate)
    {
        private byte[]? _written;
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                if (buffer.Length > maximumFrameBytes + 1) throw new SessionCommandException(SessionCommandFailure.ResourceLimit);
                await Inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                _written = buffer.ToArray();
            }
            catch { Gate.Fault(); throw; }
        }
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Inner.FlushAsync(cancellationToken).ConfigureAwait(false);
                var record = _written; _written = null;
                if (record is not null) Gate.Flushed(JsonData.Parse(Encoding.UTF8.GetString(record)));
            }
            catch { Gate.Fault(); throw; }
        }
    }
}
