namespace PiSharp.Extensions.Runtime;

internal enum RegistrationScopeState { Initializing, Active, Quiescing, Quiescent, Closing, Disposed }

/// <summary>A single extension generation. Concurrent disposal shares the actual cleanup settlement.</summary>
public sealed partial class RegistrationScope : IExtensionSessionCreationRegistry, IExtensionEventBusRegistry, IExtensionSessionTreeLifecycleRegistry,
    IExtensionToolRendererRegistry, IAsyncDisposable
{
    public string OwnerId { get; }
    public long OwnerGeneration { get; }
    public string ContractProfile => ExperimentalExtensionContract.Profile;
    public System.Collections.Immutable.ImmutableArray<string> Features => Registry.AvailableFeatures;
    public CancellationToken ExtensionLifetimeCancellationToken { get; }
    internal ExtensionRegistry Registry { get; }
    internal StagedRegistrationSet Staged { get; set; } = new();
    internal RegistrationScopeState State { get; set; } = RegistrationScopeState.Initializing;
    internal int ChargedRegistrations { get; set; }
    internal int ActiveCallbacks { get; set; }
    internal TaskCompletionSource? Idle { get; set; }
    internal TaskCompletionSource? Disposal { get; set; }
    internal object? QuiescenceIdentity { get; set; }
    internal TaskCompletionSource InitializerSettled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource lifetime = new();
    private IPiSharpExtension? extension;

    internal RegistrationScope(ExtensionRegistry registry, string ownerId, long generation, IPiSharpExtension extension)
    {
        Registry = registry;
        OwnerId = ownerId;
        OwnerGeneration = generation;
        this.extension = extension;
        ExtensionLifetimeCancellationToken = lifetime.Token;
    }

    public IExtensionRegistration RegisterTool(ExtensionToolDescriptor descriptor) => Registry.Register(this, descriptor);
    public IExtensionRegistration RegisterCommand(ExtensionCommandDescriptor descriptor) => Registry.Register(this, descriptor);
    public IExtensionRegistration Observe(ExtensionObservationDescriptor descriptor) => Registry.Register(this, descriptor);
    public IExtensionRegistration RegisterBeforeAgentStartHandler(ExtensionBeforeAgentStartHandlerDescriptor descriptor) => Registry.Register(this, descriptor);
    public IExtensionRegistration RegisterContextHandler(ExtensionContextHandlerDescriptor descriptor) => Registry.Register(this, descriptor);
    public IExtensionRegistration RegisterContextWithSystemHandler(ExtensionContextWithSystemHandlerDescriptor descriptor) => Registry.Register(this, descriptor);
    public IExtensionRegistration RegisterInputHandler(ExtensionInputHandlerDescriptor descriptor) => Registry.Register(this, descriptor);
    public IExtensionRegistration RegisterToolCallHandler(ExtensionToolCallHandlerDescriptor descriptor) => Registry.Register(this, descriptor);
    public IExtensionRegistration RegisterToolResultHandler(ExtensionToolResultHandlerDescriptor descriptor) => Registry.Register(this, descriptor);
    public IExtensionRegistration RegisterSessionSwitchHandler(ExtensionSessionSwitchHandlerDescriptor descriptor) => Registry.Register(this, descriptor);
    public IExtensionRegistration RegisterSessionCreationHandler(ExtensionSessionCreationHandlerDescriptor descriptor) => Registry.Register(this, descriptor);
    public IExtensionRegistration RegisterToolRenderer(ExtensionToolRendererDescriptor descriptor) => Registry.Register(this, descriptor);

    /// <summary>Withdraws every registration of this owner in one publication, keeping its tools when <paramref name="keepTools"/> (they
    /// leave through a tool catalog replacement), as a rebuilt extension runtime drops the previous runtime's handlers and commands.
    /// Callbacks already admitted finish; disposal still settles the owner.</summary>
    public void WithdrawRegistrations(bool keepTools) => Registry.Withdraw(this, keepTools);

    /// <summary>Stops new callback admission and waits for all callbacks already leased to this owner.
    /// Cancellation rolls the pause back without cancelling those callbacks. Dispose the returned lease
    /// to resume admission, or dispose this scope while holding it to proceed with shutdown.</summary>
    public ValueTask<RegistrationQuiescenceLease> QuiesceAsync(CancellationToken cancellationToken = default)
    {
        if (CallbackFrame.IsExecuting(this))
            throw new ExtensionRegistrationException(ExtensionRegistrationFailure.ReentrantDisposal, OwnerId, "quiesce");
        return Registry.QuiesceScopeAsync(this, cancellationToken);
    }

    internal async Task<RegistrationScope> InitializeAsync(CancellationToken initializationToken)
    {
        Exception? failure = null;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(initializationToken, ExtensionLifetimeCancellationToken);
            linked.Token.ThrowIfCancellationRequested();
            using (new CallbackFrame(this)) await extension!.InitializeAsync(this, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            Registry.Commit(this, linked.Token);
        }
        catch (Exception error)
        {
            failure = error;
        }
        finally
        {
            InitializerSettled.TrySetResult();
        }
        if (failure is null) return this;

        try { await DisposeAsync().ConfigureAwait(false); }
        catch (Exception cleanup)
        {
            throw new ExtensionRegistrationException(ExtensionRegistrationFailure.CleanupFailed, OwnerId, "initialize",
                new AggregateException(failure, cleanup));
        }
        if (failure is OperationCanceledException or ExtensionRegistrationException)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        throw new ExtensionRegistrationException(ExtensionRegistrationFailure.InitializationFailed, OwnerId, "initialize", failure);
    }

    public ValueTask DisposeAsync()
    {
        if (CallbackFrame.IsExecuting(this))
            throw new ExtensionRegistrationException(ExtensionRegistrationFailure.ReentrantDisposal, OwnerId, "dispose");
        return Registry.DisposeScope(this);
    }

    internal void StartCleanup(TaskCompletionSource settlement) => _ = CleanupAsync(settlement);

    private async Task CleanupAsync(TaskCompletionSource settlement)
    {
        var errors = new List<Exception>();
        var shutdownCompleted = false;
        try
        {
            // A throwing cancellation listener must not skip initializer/callback joins or extension cleanup.
            try { await lifetime.CancelAsync().ConfigureAwait(false); }
            catch (Exception error) { errors.Add(error); }
            await InitializerSettled.Task.ConfigureAwait(false);
            await Registry.WaitForCallbacks(this).ConfigureAwait(false);
            try
            {
                using var frame = new CallbackFrame(this);
                await extension!.DisposeAsync().ConfigureAwait(false);
                shutdownCompleted = true;
            }
            catch (Exception error) { errors.Add(error); }
        }
        catch (Exception error) { errors.Add(error); }
        finally
        {
            extension = null;
            Registry.FinishCleanup(this, shutdownCompleted);
            lifetime.Dispose();
        }
        if (errors.Count == 0) settlement.TrySetResult();
        else settlement.TrySetException(new ExtensionRegistrationException(ExtensionRegistrationFailure.CleanupFailed,
            OwnerId, "dispose", new AggregateException(errors)));
    }
}

internal sealed class RegistrationHandle(RegistrationScope scope, RegistrationEntry entry) : IExtensionRegistration
{
    private int disposed;
    public string OwnerId => entry.OwnerId;
    public long OwnerGeneration => entry.OwnerGeneration;
    public string RegistrationId => entry.RegistrationId;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) scope.Registry.Remove(scope, entry);
    }
}
