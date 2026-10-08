using System.Collections.Immutable;
using PiSharp.Extensions.Abstractions.Reloading;

namespace PiSharp.Extensions.Runtime.Reloading;

/// <summary>Maps pinned Pi reload metadata and lifecycle callbacks onto ResourceReloadWorkflow. This adapter
/// does not acquire a host mutation reservation. Its candidate-only preparation precedes shutdown/invalidation,
/// deliberately differing from upstream's in-place preparation after invalidation to preserve old authority on
/// stage failure. Shared publication, real stale-context rejection and drain remain mandatory host operations.</summary>
public sealed class HostReloadPlanner<TPayload> where TPayload : class
{
    private readonly ResourceReloadWorkflow<HostReloadGeneration<TPayload>> workflow;

    public HostReloadPlanner(HostReloadGeneration<TPayload> current, long candidateId, ResourceReloadPlan catalog,
        HostReloadOperations<TPayload> operations, CancellationToken hostLifetime, IEnumerable<string>? allowedTools = null,
        IEnumerable<string>? excludedTools = null)
    {
        ArgumentNullException.ThrowIfNull(current); ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(operations);
        if (candidateId <= current.Id) throw new ArgumentOutOfRangeException(nameof(candidateId));
        // Validate all borrowed operations before entering any host callback.
        ArgumentNullException.ThrowIfNull(operations.StageSettingsAsync); ArgumentNullException.ThrowIfNull(operations.SyncQueueModesAsync);
        ArgumentNullException.ThrowIfNull(operations.ResetApiProvidersAsync); ArgumentNullException.ThrowIfNull(operations.ReloadResourcesAsync);
        ArgumentNullException.ThrowIfNull(operations.DescribeRuntimeAsync); ArgumentNullException.ThrowIfNull(operations.BuildRuntimeAsync);
        ArgumentNullException.ThrowIfNull(operations.SessionShutdownAsync); ArgumentNullException.ThrowIfNull(operations.InvalidateAndDrainAsync);
        ArgumentNullException.ThrowIfNull(operations.PublishAsync); ArgumentNullException.ThrowIfNull(operations.CleanupAsync);
        ArgumentNullException.ThrowIfNull(operations.BeforeSessionStartAsync); ArgumentNullException.ThrowIfNull(operations.SessionStartAsync);
        ArgumentNullException.ThrowIfNull(operations.ReportUnhandledMcpServersAsync); ArgumentNullException.ThrowIfNull(operations.ExtendResourcesAsync);
        foreach (var callback in new Delegate[]
        {
            operations.StageSettingsAsync, operations.SyncQueueModesAsync, operations.ResetApiProvidersAsync,
            operations.ReloadResourcesAsync, operations.DescribeRuntimeAsync, operations.BuildRuntimeAsync,
            operations.SessionShutdownAsync, operations.InvalidateAndDrainAsync, operations.PublishAsync,
            operations.CleanupAsync, operations.BeforeSessionStartAsync, operations.SessionStartAsync,
            operations.ReportUnhandledMcpServersAsync, operations.ExtendResourcesAsync
        })
            if (callback.GetInvocationList().Length != 1)
                throw new ArgumentException("Each host reload callback must have exactly one target.", nameof(operations));
        var allowed = allowedTools is null ? null : CaptureAllowlist(allowedTools);
        var excluded = excludedTools is null ? null : CaptureAllowlist(excludedTools);
        workflow = new(current, catalog, new()
        {
            StageAsync = async (_, token) =>
            {
                TPayload? payload = null;
                try
                {
                    payload = await JoinOriginal(operations.StageSettingsAsync(token)).ConfigureAwait(false);
                    if (payload is null || ReferenceEquals(payload, current.Payload))
                    {
                        payload = null; // Never clean the old authoritative payload.
                        throw new InvalidOperationException("Settings staging must return a distinct unexposed payload.");
                    }
                    await JoinOriginal(operations.SyncQueueModesAsync(payload, token)).ConfigureAwait(false);
                    await JoinOriginal(operations.ResetApiProvidersAsync(payload, token)).ConfigureAwait(false);
                    await JoinOriginal(operations.ReloadResourcesAsync(payload, token)).ConfigureAwait(false);
                    var runtime = await JoinOriginal(operations.DescribeRuntimeAsync(payload, token)).ConfigureAwait(false);
                    ArgumentNullException.ThrowIfNull(runtime);
                    var flags = runtime.FlagDefaults.SetItems(current.Flags);
                    var candidate = new HostReloadGeneration<TPayload>(candidateId, payload, flags,
                        SelectActiveTools(current.ActiveTools, runtime.Tools, allowed, excluded), current.HasBindings);
                    await JoinOriginal(operations.BuildRuntimeAsync(candidate, token)).ConfigureAwait(false);
                    return candidate;
                }
                catch (Exception cause)
                {
                    if (payload is not null)
                    {
                        try { await JoinOriginal(operations.CleanupAsync(payload)).ConfigureAwait(false); }
                        catch (Exception cleanup) { throw new AggregateException("Candidate staging and cleanup failed.", cause, cleanup); }
                    }
                    throw;
                }
            },
            ShutdownAsync = (old, token) => JoinOriginal(operations.SessionShutdownAsync(old, "reload", token)),
            InvalidateAndDrainAsync = old => JoinOriginal(operations.InvalidateAndDrainAsync(old)),
            PublishAsync = async (old, candidate) =>
            {
                var receipt = await JoinOriginal(operations.PublishAsync(old, candidate)).ConfigureAwait(false);
                if (receipt is null || receipt.ExpectedGeneration != old.Id || receipt.CandidateGeneration != candidate.Id ||
                    !Enum.IsDefined(receipt.Authority) || receipt.Authority != HostReloadPublicationAuthority.New && receipt.Failure is null)
                    throw new InvalidOperationException("Publication owner did not acknowledge this exact generation transition.");
                return new(receipt.Authority switch
                {
                    HostReloadPublicationAuthority.New => ResourceReloadAuthority.New,
                    HostReloadPublicationAuthority.None => ResourceReloadAuthority.None,
                    _ => ResourceReloadAuthority.Unknown
                }, receipt.Failure);
            },
            CleanupAsync = generation => JoinOriginal(operations.CleanupAsync(generation.Payload)),
            StartAndExtendAsync = async (candidate, token) =>
            {
                if (!candidate.HasBindings) return;
                await JoinOriginal(operations.BeforeSessionStartAsync(candidate, token)).ConfigureAwait(false);
                await JoinOriginal(operations.SessionStartAsync(candidate, "reload", token)).ConfigureAwait(false);
                await JoinOriginal(operations.ReportUnhandledMcpServersAsync(candidate, token)).ConfigureAwait(false);
                await JoinOriginal(operations.ExtendResourcesAsync(candidate, "reload", token)).ConfigureAwait(false);
            }
        }, hostLifetime);
    }

    public Task<ResourceReloadReceipt<HostReloadGeneration<TPayload>>> ReloadAsync(CancellationToken admissionToken = default)
        => workflow.ReloadAsync(admissionToken);

    private static async ValueTask JoinOriginal(ValueTask operation)
    {
        var original = operation.AsTask();
        try { await original.ConfigureAwait(false); }
        catch (Exception cause) { throw new HostReloadCallbackFailure(original.IsCanceled, original.Exception, cause); }
    }

    private static async ValueTask<TResult> JoinOriginal<TResult>(ValueTask<TResult> operation)
    {
        var original = operation.AsTask();
        try { return await original.ConfigureAwait(false); }
        catch (Exception cause) { throw new HostReloadCallbackFailure(original.IsCanceled, original.Exception, cause); }
    }

    private static ImmutableHashSet<string> CaptureAllowlist(IEnumerable<string> names)
    {
        var result = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal); var count = 0;
        foreach (var name in names)
        {
            if (++count > 4096 || string.IsNullOrWhiteSpace(name) || name.Length > 2048)
                throw new ArgumentException("Invalid or excessive tool allowlist.", nameof(names));
            result.Add(name);
        }
        return result.ToImmutable();
    }

    private static IEnumerable<string> SelectActiveTools(ImmutableArray<string> previous, ImmutableArray<HostReloadTool> tools,
        ImmutableHashSet<string>? allowed, ImmutableHashSet<string>? excluded)
    {
        var registry = tools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in previous)
            if (registry.TryGetValue(name, out var previousTool) &&
                (previousTool.Exposure is HostReloadToolExposure.Direct or HostReloadToolExposure.ModelOnly) &&
                (allowed is null || allowed.Contains(name)) && excluded?.Contains(name) != true && selected.Add(name)) yield return name;
        foreach (var tool in tools)
        {
            var declarable = tool.Exposure is HostReloadToolExposure.Direct or HostReloadToolExposure.ModelOnly;
            if (declarable && excluded?.Contains(tool.Name) != true &&
                (allowed is not null ? allowed.Contains(tool.Name) : tool.IsExtension && tool.DefaultActive) &&
                selected.Add(tool.Name)) yield return tool.Name;
        }
    }
}
