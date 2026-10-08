using PiSharp.Extensions;
using PiSharp.Extensions.Mcp.Registration;
using PiSharp.Extensions.Runtime;

namespace PiSharp.Cli.Mcp;

/// <summary>One admitted native activation consumes the decorated extension. The native scope owns
/// its disposal; preparation itself neither activates a scope nor publishes MCP metadata.</summary>
public sealed class McpPreparedNativeInitialization
{
    private readonly Func<RegistrationScope, Task<RegistrationScope>, CancellationToken,
        Task<McpInitializedRegistrationOwner>> commit;
    private readonly Action abort;
    private int consumed;

    internal McpPreparedNativeInitialization(IPiSharpExtension decoratedExtension,
        Func<RegistrationScope, Task<RegistrationScope>, CancellationToken,
            Task<McpInitializedRegistrationOwner>> commit, Action abort)
        => (DecoratedExtension, this.commit, this.abort) = (decoratedExtension, commit, abort);

    public IPiSharpExtension DecoratedExtension { get; }

    public Task<McpInitializedRegistrationOwner> CommitAsync(RegistrationScope exactScope,
        Task<RegistrationScope> actualActivationOriginal, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(exactScope);
        ArgumentNullException.ThrowIfNull(actualActivationOriginal);
        if (Interlocked.CompareExchange(ref consumed, 1, 0) != 0)
            throw new InvalidOperationException("Prepared MCP initialization was already consumed.");
        return commit(exactScope, actualActivationOriginal, token);
    }

    public void Abort()
    {
        if (Interlocked.CompareExchange(ref consumed, 2, 0) == 0) abort();
    }
}

public sealed partial class McpExtensionRegistrationBridge
{
    /// <summary>Compose with the admitted native facade bridge's single activation. Its binder runs
    /// before this decorated extension initializer. No separate native activation is performed.</summary>
    public McpPreparedNativeInitialization PrepareNativeInitialization(IPiSharpExtension extension,
        string admittedExtensionPath, Action<IMcpServerRegistrationFacade> bindInitializerFacade)
    {
        ArgumentNullException.ThrowIfNull(extension);
        ArgumentNullException.ThrowIfNull(bindInitializerFacade);
        if (bindInitializerFacade.GetInvocationList().Length != 1)
            throw new ArgumentException("One explicit initializer binder required.");
        if (string.IsNullOrWhiteSpace(admittedExtensionPath) || !Path.IsPathFullyQualified(admittedExtensionPath))
            throw new ArgumentException("An admitted absolute extension path required.");
        var wrapper = new InitializingExtension(this, extension, admittedExtensionPath, bindInitializerFacade);
        var aborted = 0;
        var decorated = new PreparedExtension(wrapper, () => Volatile.Read(ref aborted) != 0);
        return new(decorated, Commit, () =>
        {
            Interlocked.Exchange(ref aborted, 1);
            wrapper.Stage?.Abort();
        });

        async Task<McpInitializedRegistrationOwner> Commit(RegistrationScope scope,
            Task<RegistrationScope> activation, CancellationToken token)
        {
            RegistrationScope activated;
            try { activated = await activation.ConfigureAwait(false); }
            catch (Exception error)
            {
                wrapper.Stage?.Abort();
                if (activation.IsFaulted)
                    throw new McpInitializationOriginalException("combined native activation", activation, activation.Exception!);
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw(); throw;
            }
            var stage = wrapper.Stage;
            // Reject a substituted scope before acquiring disposal authority over it.
            if (stage is null || !ReferenceEquals(scope, stage.Scope) || !ReferenceEquals(activated, scope))
            {
                stage?.Abort();
                throw new InvalidOperationException("Combined activation changed the owning initializer scope.");
            }
            try
            {
                token.ThrowIfCancellationRequested();
                var facade = BindOwner(scope, admittedExtensionPath);
                registrations.CommitInitialOwner(scope, admittedExtensionPath, stage.SealedRows);
                stage.Promote(facade);
                return new(scope, stage, activation);
            }
            catch (Exception rejected)
            {
                stage.Abort();
                var failures = new List<Exception> { rejected };
                Task? disposal = null;
                try
                {
                    disposal = scope.DisposeAsync().AsTask();
                    await disposal.ConfigureAwait(false);
                    await RetireClosedOwnerAsync(scope, disposal).ConfigureAwait(false);
                }
                catch (Exception cleanup)
                {
                    failures.Add(disposal?.IsFaulted == true
                        ? new McpInitializationOriginalException("combined scope disposal", disposal, disposal.Exception!) : cleanup);
                }
                McpSessionRuntimeFactory.Rethrow(failures); throw;
            }
        }
    }

    private sealed class PreparedExtension(IPiSharpExtension inner, Func<bool> aborted) : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token)
        {
            if (aborted()) throw new InvalidOperationException("Prepared MCP initialization was aborted.");
            return inner.InitializeAsync(registry, token);
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
