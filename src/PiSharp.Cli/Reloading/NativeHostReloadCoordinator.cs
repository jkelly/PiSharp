using System.Collections.Immutable;
using PiSharp.CodingAgent;
using PiSharp.CodingAgent.Configuration;
using PiSharp.Extensions.Abstractions.Reloading;
using PiSharp.Extensions.Runtime.Reloading;

namespace PiSharp.Cli.Reloading;

/// <summary>Host preparation state. Every acquired runtime resource must be owned by Prepared.Runtime
/// before BuildRuntimeAsync returns. State cleanup handles only preparation resources outside that lease.</summary>
public sealed class NativeHostReloadPayload
{
    public NativeHostReloadPayload(object state) : this(state, null) { }
    public NativeHostReloadPayload(object state, StartupSettingsSnapshot? settings)
    { State = state ?? throw new ArgumentNullException(nameof(state)); Settings = settings; }
    public object State { get; }
    public StartupSettingsSnapshot? Settings { get; }
    public PreparedNativeHostReload? Prepared { get; internal set; }
}

public sealed record PreparedNativeHostReload(SessionRuntimeLease Runtime, Action CommitPreparedRegistry);

/// <summary>Admitted native host dependencies. These prepare candidate-only state and emit lifecycle
/// events; session invalidation, durable publication and runtime ownership are supplied by the coordinator.</summary>
public sealed class NativeHostReloadOperations
{
    public required Func<AgentSessionAttachment, CancellationToken, ValueTask<NativeHostReloadPayload>> StageSettingsAsync { get; init; }
    public required Func<NativeHostReloadPayload, CancellationToken, ValueTask> SyncQueueModesAsync { get; init; }
    public required Func<NativeHostReloadPayload, CancellationToken, ValueTask> ResetApiProvidersAsync { get; init; }
    public required Func<NativeHostReloadPayload, CancellationToken, ValueTask> ReloadResourcesAsync { get; init; }
    public required Func<NativeHostReloadPayload, CancellationToken, ValueTask<HostReloadRuntime>> DescribeRuntimeAsync { get; init; }
    public required Func<AgentSessionAttachment, HostReloadGeneration<NativeHostReloadPayload>, CancellationToken, ValueTask<PreparedNativeHostReload>> BuildRuntimeAsync { get; init; }
    public required Func<HostReloadGeneration<NativeHostReloadPayload>, string, CancellationToken, ValueTask> SessionShutdownAsync { get; init; }
    public required Func<NativeHostReloadPayload, ValueTask> CleanupPreparationAsync { get; init; }
    public required Func<HostReloadGeneration<NativeHostReloadPayload>, CancellationToken, ValueTask> BeforeSessionStartAsync { get; init; }
    public required Func<HostReloadGeneration<NativeHostReloadPayload>, string, CancellationToken, ValueTask> SessionStartAsync { get; init; }
    public required Func<HostReloadGeneration<NativeHostReloadPayload>, CancellationToken, ValueTask> ReportUnhandledMcpServersAsync { get; init; }
    public required Func<HostReloadGeneration<NativeHostReloadPayload>, string, CancellationToken, ValueTask> ExtendResourcesAsync { get; init; }

    internal void Validate()
    {
        foreach (var callback in new Delegate?[] { StageSettingsAsync, SyncQueueModesAsync, ResetApiProvidersAsync,
            ReloadResourcesAsync, DescribeRuntimeAsync, BuildRuntimeAsync, SessionShutdownAsync, CleanupPreparationAsync,
            BeforeSessionStartAsync, SessionStartAsync, ReportUnhandledMcpServersAsync, ExtendResourcesAsync })
            if (callback is null || callback.GetInvocationList().Length != 1)
                throw new ArgumentException("Every native reload dependency requires exactly one callback.");
    }
}

public sealed record NativeHostReloadReceipt(AgentSessionAttachment Previous, AgentSessionAttachment? Current,
    ResourceReloadReceipt<HostReloadGeneration<NativeHostReloadPayload>> Workflow);

/// <summary>Concrete native-host adapter: one actual attachment, one host reservation, and the existing
/// HostReloadPlanner. Repeated callers join the owner's original task, including concurrent shutdown.</summary>
public sealed class NativeHostReloadCoordinator
{
    private readonly ReplaceableAgentSession owner;
    private readonly AgentSessionAttachment attachment;
    private readonly NativeHostReloadPayload current;
    private readonly ImmutableDictionary<string, HostReloadFlagValue> flags;
    private readonly bool hasBindings;
    private readonly ResourceReloadPlan plan;
    private readonly NativeHostReloadOperations operations;

    public NativeHostReloadCoordinator(ReplaceableAgentSession owner, AgentSessionAttachment attachment,
        NativeHostReloadPayload current, IEnumerable<KeyValuePair<string, HostReloadFlagValue>> flags,
        bool hasBindings, ResourceReloadPlan plan, NativeHostReloadOperations operations)
    {
        ArgumentNullException.ThrowIfNull(owner); ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(current); ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(operations);
        operations.Validate(); owner.ValidateAttachment(attachment);
        this.owner = owner; this.attachment = attachment; this.current = current;
        this.flags = new HostReloadGeneration<NativeHostReloadPayload>(attachment.Generation, current, flags, [], hasBindings).Flags;
        this.hasBindings = hasBindings; this.plan = plan; this.operations = operations;
    }

    public Task<NativeHostReloadReceipt> ReloadAsync(CancellationToken admissionToken = default)
        => owner.RunReloadAsync(attachment, ExecuteAsync, admissionToken, this);

    private async Task<NativeHostReloadReceipt> ExecuteAsync(ReplaceableAgentSession.ReloadReservation reservation,
        CancellationToken hostLifetime)
    {
        var old = new HostReloadGeneration<NativeHostReloadPayload>(attachment.Generation, current, flags,
            reservation.ActiveTools, hasBindings);
        var policy = reservation.CapturedRegistry.LifetimeToolSelection;
        var planner = new HostReloadPlanner<NativeHostReloadPayload>(old, reservation.Candidate.Generation, plan, new()
        {
            StageSettingsAsync = token => operations.StageSettingsAsync(reservation.Candidate, token),
            SyncQueueModesAsync = operations.SyncQueueModesAsync,
            ResetApiProvidersAsync = operations.ResetApiProvidersAsync,
            ReloadResourcesAsync = operations.ReloadResourcesAsync,
            DescribeRuntimeAsync = operations.DescribeRuntimeAsync,
            BuildRuntimeAsync = async (generation, token) =>
            {
                var prepared = await Join(operations.BuildRuntimeAsync(reservation.Candidate, generation, token)).ConfigureAwait(false);
                ArgumentNullException.ThrowIfNull(prepared); ArgumentNullException.ThrowIfNull(prepared.Runtime);
                reservation.AdmitRuntime(prepared.Runtime);
                // Store ownership before validation so a rejected commit still cleans its runtime.
                generation.Payload.Prepared = prepared;
                if (prepared.CommitPreparedRegistry is null || prepared.CommitPreparedRegistry.GetInvocationList().Length != 1)
                    throw new ArgumentException("A prepared registry requires one synchronous commit.");
            },
            SessionShutdownAsync = operations.SessionShutdownAsync,
            InvalidateAndDrainAsync = _ => new(reservation.InvalidateAndDrainAsync()),
            PublishAsync = async (_, candidate) =>
            {
                var prepared = candidate.Payload.Prepared ?? throw new InvalidOperationException("No staged native runtime.");
                try
                {
                    await Join(new ValueTask<SessionToolCatalogReceipt>(reservation.PublishAsync(prepared.Runtime,
                        candidate.ActiveTools, prepared.CommitPreparedRegistry))).ConfigureAwait(false);
                }
                catch (Exception error) when (reservation.IsPublished)
                {
                    return new(attachment.Generation, reservation.Candidate.Generation, HostReloadPublicationAuthority.New, error);
                }
                return new(attachment.Generation, reservation.Candidate.Generation, HostReloadPublicationAuthority.New);
            },
            CleanupAsync = async payload =>
            {
                var errors = new List<Exception>();
                try
                {
                    if (ReferenceEquals(payload, current)) await Join(new ValueTask(reservation.CleanupPreviousRuntimeAsync())).ConfigureAwait(false);
                    else if (payload.Prepared is { } prepared) await Join(prepared.Runtime.DisposeAsync()).ConfigureAwait(false);
                }
                catch (Exception error) { errors.Add(error); }
                try { await Join(operations.CleanupPreparationAsync(payload)).ConfigureAwait(false); }
                catch (Exception error) { errors.Add(error); }
                if (errors.Count != 0) throw new AggregateException("Native reload cleanup failed.", errors);
            },
            BeforeSessionStartAsync = operations.BeforeSessionStartAsync,
            SessionStartAsync = operations.SessionStartAsync,
            ReportUnhandledMcpServersAsync = operations.ReportUnhandledMcpServersAsync,
            ExtendResourcesAsync = operations.ExtendResourcesAsync
        }, hostLifetime, policy?.AllowedNames, policy?.ExcludedNames);
        var receipt = await planner.ReloadAsync().ConfigureAwait(false);
        return new(attachment, reservation.IsPublished ? reservation.Candidate : null, receipt);
    }

    private static async ValueTask Join(ValueTask operation)
    {
        var original = operation.AsTask();
        try { await original.ConfigureAwait(false); }
        catch (Exception error) { throw new HostReloadCallbackFailure(original.IsCanceled, original.Exception, error); }
    }
    private static async ValueTask<T> Join<T>(ValueTask<T> operation)
    {
        var original = operation.AsTask();
        try { return await original.ConfigureAwait(false); }
        catch (Exception error) { throw new HostReloadCallbackFailure(original.IsCanceled, original.Exception, error); }
    }
}
