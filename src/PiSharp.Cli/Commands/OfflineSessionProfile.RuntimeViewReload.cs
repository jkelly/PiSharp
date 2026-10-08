using PiSharp.Cli.Reloading;
using PiSharp.CodingAgent;
using PiSharp.Extensions.Abstractions.Reloading;

namespace PiSharp.Cli.Commands;

internal sealed partial class OfflineSessionProfile
{
    private NativeHostReloadOperations ViewReloadOperations(AgentSessionAttachment previous, NativeHostReloadAdmission admission)
    {
        var old = CaptureRuntimeView(previous);
        var operations = admission.Operations;
        AgentSessionAttachment? candidate = null;
        return new()
        {
            StageSettingsAsync = operations.StageSettingsAsync,
            SyncQueueModesAsync = operations.SyncQueueModesAsync,
            ResetApiProvidersAsync = operations.ResetApiProvidersAsync,
            ReloadResourcesAsync = operations.ReloadResourcesAsync,
            DescribeRuntimeAsync = operations.DescribeRuntimeAsync,
            BuildRuntimeAsync = async (attachment, generation, token) =>
            {
                var prepared = await JoinViewCallback(operations.BuildRuntimeAsync(attachment, generation, token)).ConfigureAwait(false);
                ArgumentNullException.ThrowIfNull(prepared);
                ArgumentNullException.ThrowIfNull(prepared.Runtime);
                var cleanupRuntime = true;
                try
                {
                    ProfileRuntimeView? view;
                    lock (viewGate) runtimeViews.TryGetValue(attachment, out view);
                    if (view is null)
                    {
                        if (old.Extension is not null || old.Prompts is not null || old.Skills is not null)
                            throw new InvalidOperationException("Reload did not prepare the built-in profile runtime view.");
                        cleanupRuntime = false; // Adoption handles its own failures, including refusing a claimed lease.
                        await StageEmptyRuntimeViewAsync(attachment, prepared.Runtime).ConfigureAwait(false);
                        cleanupRuntime = true;
                    }
                    else if ((view.Extension is not null || view.Prompts is not null || view.Skills is not null) && !admission.HasBindings)
                        throw new InvalidOperationException("Built-in profile reload requires admitted lifecycle bindings.");
                    RequireOriginalPromptRegistry(prepared.Runtime.Registry);
                    var promptCommit = PrepareOriginalPromptCommit(previous, attachment, generation.Payload,
                        prepared.CommitPreparedRegistry);
                    var commit = PrepareEffectiveSettingsCommit(previous, attachment, generation.Payload.Settings, promptCommit);
                    candidate = attachment; return new(prepared.Runtime, commit);
                }
                catch (Exception error)
                {
                    if (!cleanupRuntime) throw;
                    try { await ProfileViewOriginal.Join(prepared.Runtime).ConfigureAwait(false); }
                    catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
                    throw;
                }
            },
            SessionShutdownAsync = async (generation, reason, token) =>
            {
                if (old.Extension is not null)
                {
                    using var use = old.Lifetime.Enter();
                    var snapshot = old.Extension.CaptureShutdownSessionSnapshot(previous);
                    await JoinViewCallback(old.Extension.DispatchSessionShutdownAsync(snapshot, reason)).ConfigureAwait(false);
                }
                await JoinViewCallback(operations.SessionShutdownAsync(generation, reason, token)).ConfigureAwait(false);
            },
            CleanupPreparationAsync = operations.CleanupPreparationAsync,
            BeforeSessionStartAsync = async (generation, token) =>
            {
                var attached = candidate ?? throw new InvalidOperationException("Candidate view was not admitted.");
                await ApplySkillsFromViewAsync(attached.Session, CaptureRuntimeView(attached), token).ConfigureAwait(false);
                await JoinViewCallback(operations.BeforeSessionStartAsync(generation, token)).ConfigureAwait(false);
            },
            SessionStartAsync = async (generation, reason, token) =>
            {
                var current = CaptureRuntimeView(candidate ?? throw new InvalidOperationException("Candidate view was not admitted."));
                using var use = current.Lifetime.Enter();
                if (current.Extension is not null)
                    await JoinViewCallback(current.Extension.DispatchSessionStartAsync(reason, token)).ConfigureAwait(false);
                await JoinViewCallback(operations.SessionStartAsync(generation, reason, token)).ConfigureAwait(false);
            },
            ReportUnhandledMcpServersAsync = operations.ReportUnhandledMcpServersAsync,
            ExtendResourcesAsync = operations.ExtendResourcesAsync
        };
    }
    private async ValueTask StageEmptyRuntimeViewAsync(AgentSessionAttachment attachment, SessionRuntimeLease runtime)
    {
        var lifetime = new ProfileViewLifetime(null);
        var hold = lifetime.Acquire();
        var view = new ProfileRuntimeView(null, null, null, null, lifetime, runtime.Registry);
        ProfileRuntimeViewOwnership? ownership = null;
        var adopted = false;
        try
        {
            runtime.WrapResourcesBeforeClaim(native =>
                ownership = new(this, native, hold, () => view, attachment.Generation,
                    () => ForgetRuntimeView(attachment, view)));
            adopted = true;
            // The exact existing candidate lease now owns the independent view and every
            // original native resource. Any later rejection disposes that same lease.
            lock (viewGate)
            {
                runtimeViews.Add(attachment, view);
                viewOwnerships.Add(ownership!);
            }
        }
        catch (Exception error)
        {
            // A rejected pre-claim transfer must not dispose a possibly already-claimed
            // lease. Its resources are still owned by that lease, not this view attempt.
            try { await ProfileViewOriginal.Join(adopted ? runtime : hold).ConfigureAwait(false); }
            catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
            throw;
        }
    }
    private static async ValueTask JoinViewCallback(ValueTask operation)
    {
        var original = operation.AsTask();
        try { await original.ConfigureAwait(false); }
        catch (Exception error) { throw new HostReloadCallbackFailure(original.IsCanceled, original.Exception, error); }
    }
    private static async ValueTask<T> JoinViewCallback<T>(ValueTask<T> operation)
    {
        var original = operation.AsTask();
        try { return await original.ConfigureAwait(false); }
        catch (Exception error) { throw new HostReloadCallbackFailure(original.IsCanceled, original.Exception, error); }
    }
}
