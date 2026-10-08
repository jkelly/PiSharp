using System.Collections.Immutable;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Registration;
using PiSharp.Extensions.Runtime;
using PiSharp.Extensions.Runtime.Mcp.Registration;

namespace PiSharp.Cli.Mcp;

public sealed record McpInitializedRegistrationOwner(RegistrationScope Scope,
    IMcpServerRegistrationFacade Facade, Task<RegistrationScope> ActivationOriginal);
public sealed class McpInitializationOriginalException(string operation, Task original, Exception evidence)
    : IOException("MCP initializer original failed: " + operation, evidence)
{
    public Task Original { get; } = original;
}
public sealed class McpInitializationUnownedCancellationException(Task? original, OperationCanceledException cause)
    : IOException("MCP initializer cancellation was not requested by its owning token.", cause)
{
    public Task? Original { get; } = original;
    public OperationCanceledException Cause { get; } = cause;
}

public sealed partial class McpExtensionRegistrationBridge
{
    /// <summary>Own the genuine native activation. The binder injects an inert facade before the
    /// existing extension initializer runs. No MCP metadata publishes until native activation commits.
    /// Caller retains the extension if activation refuses before entering its initializer.</summary>
    public async Task<McpInitializedRegistrationOwner> ActivateOwnerAsync(string ownerId, string admittedExtensionPath,
        IPiSharpExtension extension, Action<IMcpServerRegistrationFacade> bindInitializerFacade,
        CancellationToken initializationToken = default)
    {
        ArgumentNullException.ThrowIfNull(extension); ArgumentNullException.ThrowIfNull(bindInitializerFacade);
        if (bindInitializerFacade.GetInvocationList().Length != 1)
            throw new ArgumentException("One explicit initializer binder required.");
        if (string.IsNullOrWhiteSpace(admittedExtensionPath) || !Path.IsPathFullyQualified(admittedExtensionPath))
            throw new ArgumentException("An admitted absolute extension path required.");
        var wrapper = new InitializingExtension(this, extension, admittedExtensionPath, bindInitializerFacade);
        var activation = registry.ActivateAsync(ownerId, wrapper, initializationToken);
        RegistrationScope active;
        try { active = await activation.ConfigureAwait(false); }
        catch (Exception error)
        {
            wrapper.Stage?.Abort();
            if (activation.IsFaulted)
                throw new McpInitializationOriginalException("native activation", activation, activation.Exception!);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw(); throw;
        }
        var stage = wrapper.Stage;
        try
        {
            if (stage is null) throw new InvalidOperationException("Native activation did not enter its owning initializer.");
            if (!ReferenceEquals(active, stage.Scope)) throw new InvalidOperationException("Native activation changed the captured scope.");
            initializationToken.ThrowIfCancellationRequested();
            var facade = BindOwner(active, admittedExtensionPath); // Genuine active registry assertion.
            registrations.CommitInitialOwner(active, admittedExtensionPath, stage.SealedRows);
            stage.Promote(facade);
            return new(active, stage, activation);
        }
        catch (Exception rejected)
        {
            stage?.Abort();
            var failures = new List<Exception> { rejected };
            Task? disposal = null;
            try { disposal = active.DisposeAsync().AsTask(); await disposal.ConfigureAwait(false); await RetireClosedOwnerAsync(active, disposal).ConfigureAwait(false); }
            catch (Exception cleanup)
            {
                failures.Add(disposal?.IsFaulted == true
                    ? new McpInitializationOriginalException("activated scope disposal", disposal, disposal.Exception!) : cleanup);
            }
            // No replay after uncertain publication; its catalog fence remains the host's reconciliation boundary.
            McpSessionRuntimeFactory.Rethrow(failures); throw;
        }
    }

    private sealed class InitializingExtension(McpExtensionRegistrationBridge bridge, IPiSharpExtension original,
        string path, Action<IMcpServerRegistrationFacade> binder) : IPiSharpExtension
    {
        internal InitializerFacade? Stage { get; private set; }
        public async ValueTask InitializeAsync(IExtensionRegistry native, CancellationToken token)
        {
            if (Stage is not null || native is not RegistrationScope scope)
                throw new InvalidOperationException("One genuine native initializer scope required.");
            var stage = new InitializerFacade(bridge, scope, path); Stage = stage;
            Task? initialization = null;
            try
            {
                binder(stage);
                initialization = original.InitializeAsync(native, token).AsTask(); // Capture exactly once.
                await initialization.ConfigureAwait(false);
                token.ThrowIfCancellationRequested(); stage.Seal();
            }
            catch (Exception error)
            {
                stage.Abort();
                if (initialization?.IsFaulted == true)
                    throw new McpInitializationOriginalException("extension initializer", initialization, initialization.Exception!);
                if (error is OperationCanceledException cancellation &&
                    !(token.IsCancellationRequested && cancellation.CancellationToken == token))
                    throw new McpInitializationUnownedCancellationException(initialization, cancellation);
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw(); throw;
            }
        }
        public ValueTask DisposeAsync() => original.DisposeAsync(); // Existing scope owns/captures/joins cleanup.
    }

    private sealed class InitializerFacade : IMcpServerRegistrationFacade
    {
        private readonly object stagingGate = new();
        private readonly McpExtensionRegistrationBridge bridge;
        private readonly McpRegistrationCatalog staging;
        private readonly IMcpServerRegistrationFacade stagedFacade;
        private ImmutableArray<McpRegisteredServer> rows = [];
        private IMcpServerRegistrationFacade? active;
        private long stagedRevision;
        private int phase; // 0 initializer-only,1 sealed pending native commit,2 active,3 discarded.
        internal RegistrationScope Scope { get; }
        internal InitializerFacade(McpExtensionRegistrationBridge bridge, RegistrationScope scope, string path)
        {
            this.bridge = bridge; Scope = scope;
            staging = new(McpConfigurationReader.Load(null, null, true), AssertInitializing, PublishInert);
            stagedFacade = staging.BindOwner(scope, path);
        }
        public string OwnerId => Scope.OwnerId;
        public long OwnerGeneration => Scope.OwnerGeneration;
        private void AssertInitializing(IExtensionRegistry owner)
        {
            lock (stagingGate)
            {
                if (phase != 0 || !ReferenceEquals(owner, Scope)) throw new InvalidOperationException("Initializer staging ended or changed scope.");
                Scope.ExtensionLifetimeCancellationToken.ThrowIfCancellationRequested();
            }
        }
        private McpRegistrationPublicationReceipt PublishInert(McpRegistrationPublication publication)
        {
            lock (stagingGate)
            {
                AssertInitializing(publication.Owner);
                if (publication.Revision != checked(stagedRevision + 1) ||
                    publication.Current.Length + bridge.CaptureSnapshot().RegisteredServers.Length > McpAdmittedActivationHost.MaximumServers)
                    throw new InvalidOperationException("Finite initializer staging bound/revision required.");
                rows = publication.Current; stagedRevision = publication.Revision;
                return new(Scope, stagedRevision, true); // Inert local transaction only, never bridge publication.
            }
        }
        public void RegisterMcpServer(string name, JsonData configuration)
        {
            lock (stagingGate)
            {
                if (phase == 2) { active!.RegisterMcpServer(name, configuration); return; }
                AssertInitializing(Scope);
                if (bridge.CaptureSnapshot().RegisteredServers.Any(server => server.Name == name))
                    throw new InvalidOperationException("Initializer cannot replace another active owner's server.");
                stagedFacade.RegisterMcpServer(name, configuration);
            }
        }
        public void UnregisterMcpServer(string name)
        {
            lock (stagingGate)
            {
                if (phase == 2) { active!.UnregisterMcpServer(name); return; }
                AssertInitializing(Scope); stagedFacade.UnregisterMcpServer(name);
            }
        }
        public ImmutableArray<McpRegisteredServer> GetMcpServers()
        {
            lock (stagingGate)
            {
                if (phase == 2) return active!.GetMcpServers();
                AssertInitializing(Scope);
                return bridge.CaptureSnapshot().RegisteredServers.AddRange(stagedFacade.GetMcpServers());
            }
        }
        internal void Seal() { lock (stagingGate) { AssertInitializing(Scope); phase = 1; } }
        internal ImmutableArray<McpRegisteredServer> SealedRows
        { get { lock (stagingGate) { if (phase != 1) throw new InvalidOperationException("Exact sealed staging transaction required."); return rows; } } }
        internal void Promote(IMcpServerRegistrationFacade facade)
        { lock (stagingGate) { if (phase != 1) throw new InvalidOperationException("One sealed staging promotion required."); active = facade; phase = 2; rows = []; } }
        internal void Abort() { lock (stagingGate) { phase = 3; rows = []; active = null; } }
    }
}
