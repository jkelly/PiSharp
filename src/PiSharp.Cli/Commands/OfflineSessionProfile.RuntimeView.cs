using PiSharp.Cli.Extensions;
using PiSharp.Cli.Reloading;
using PiSharp.Cli.Prompts;
using PiSharp.Cli.Skills;
using PiSharp.CodingAgent;
using PiSharp.CodingAgent.Resources.Skills;
using System.Collections.Immutable;

namespace PiSharp.Cli.Commands;

internal sealed partial class OfflineSessionProfile
{
    private readonly object viewGate = new();
    private readonly AsyncLocal<object?> viewCleanupCallback = new();
    internal IDisposable EnterViewCleanupCallback()
    {
        var prior = viewCleanupCallback.Value;
        viewCleanupCallback.Value = new object();
        return new ViewCleanupScope(this, prior);
    }
    private sealed class ViewCleanupScope(OfflineSessionProfile profile, object? prior) : IDisposable
    {
        public void Dispose() => profile.viewCleanupCallback.Value = prior;
    }
    private void RefuseViewCleanupSelfWait()
    {
        if (viewCleanupCallback.Value is not null)
            throw new InvalidOperationException("Profile cleanup callbacks cannot join their owning profile close.");
    }
    private readonly Dictionary<AgentSessionAttachment, ProfileRuntimeView> runtimeViews = new(ReferenceEqualityComparer.Instance);
    private readonly List<ProfileRuntimeViewOwnership> viewOwnerships = [];
    private ProfileViewLifetime? startupViewLifetime;
    private ProfileViewLifetime.Hold? startupViewHold;
    private ProfileRuntimeView? frozenStartupView;
    private bool startupViewTransferred;
    private bool runtimeViewAttached;
    private ProfileOwnedHooks? profileHooks;
    // A later native binder may replace any individual callback. Retiring a profile
    // activation only releases the exact delegates that activation installed.
    private sealed class ProfileOwnedHooks
    {
        internal ReplaceableAgentSession Owner { get; }
        internal Func<bool> HasForeignHooks { get; }
        internal Action ClearOwned { get; }
        internal ProfileOwnedHooks(ReplaceableAgentSession owner)
        {
            Owner = owner;
            var before = owner.BeforeReplacement; var after = owner.AfterReplacement;
            var retire = owner.BeforeRetirement; var validate = owner.ValidateTargetAttachment;
            var create = owner.BeforeCreation;
            HasForeignHooks = () => Foreign(owner.BeforeReplacement, before) || Foreign(owner.AfterReplacement, after) ||
                Foreign(owner.BeforeRetirement, retire) || Foreign(owner.ValidateTargetAttachment, validate) ||
                Foreign(owner.BeforeCreation, create);
            ClearOwned = () =>
            {
                if (ReferenceEquals(owner.BeforeReplacement, before)) owner.BeforeReplacement = null;
                if (ReferenceEquals(owner.AfterReplacement, after)) owner.AfterReplacement = null;
                if (ReferenceEquals(owner.BeforeRetirement, retire)) owner.BeforeRetirement = null;
                if (ReferenceEquals(owner.ValidateTargetAttachment, validate)) owner.ValidateTargetAttachment = null;
                if (ReferenceEquals(owner.BeforeCreation, create)) owner.BeforeCreation = null;
            };
        }
        private static bool Foreign(Delegate? actual, Delegate? owned) => actual is not null && !ReferenceEquals(actual, owned);
    }
    private readonly HashSet<NativeExtensionActivation> preparedExtensions = new(ReferenceEqualityComparer.Instance);
    internal void BindPreparedExtension(NativeExtensionActivation extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        extension.Bind(_policy, _profileInvokerOptions);
        lock (viewGate) preparedExtensions.Add(extension);
    }
    private void RefuseViewReporterDisposal()
    {
        NativeExtensionActivation?[] extensions;
        lock (viewGate) extensions = runtimeViews.Values.Select(view => view.Extension).Append(_extension).Distinct().ToArray();
        foreach (var extension in extensions) extension?.RefuseReporterInitiatedDisposal();
    }

    private ProfileRuntimeView CaptureStartupRuntimeView()
    {
        lock (viewGate)
        {
            startupViewLifetime ??= new(_extension);
            startupViewHold ??= startupViewLifetime.Acquire();
            return frozenStartupView ??= new(_extension, _promptTemplates, _skills, _skillDiagnostic, startupViewLifetime, _startupRegistry);
        }
    }
    private ProfileRuntimeView CaptureRuntimeView(AgentSessionAttachment? expected = null)
    {
        var attachment = expected ?? Sessions?.Current;
        lock (viewGate)
        {
            if (attachment is not null && runtimeViews.TryGetValue(attachment, out var view)) return view;
            if (attachment is not null && runtimeViewAttached)
                throw new InvalidOperationException("Current attachment has no bound profile runtime view.");
        }
        return CaptureStartupRuntimeView();
    }
    private void RequireStartupViewMutable()
    { lock (viewGate) if (frozenStartupView is not null) throw new InvalidOperationException("Profile runtime view is already frozen."); }

    // Called after acquired native registry/policy validation, before handing resources
    // to the runtime factory. No borrowed callbacks execute during this transfer.
    internal ProfileRuntimeViewOwnership CaptureInitialRuntimeViewOwnership(IAsyncDisposable nativeResources, long generation)
    {
        ArgumentNullException.ThrowIfNull(nativeResources);
        return CaptureRuntimeViewOwnership(nativeResources, generation);
    }

    // Even without an MCP runtime, navigation needs a real view hold in each session
    // lease. Acquire it while the previous attachment is alive; freeze startup metadata
    // only on binding after the startup loaders. There is no synthetic native owner.
    private ValueTask<SessionRuntimeLease> AcquireProfileRuntimeAsync(string cwd, long generation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var registry = Registry;
        var ownership = CaptureRuntimeViewOwnership(null, generation);
        return ValueTask.FromResult(new SessionRuntimeLease(registry, ownership.Resources, ownership.BindOwner));
    }
    private ProfileRuntimeViewOwnership CaptureRuntimeViewOwnership(IAsyncDisposable? nativeResources, long generation)
    {
        if (generation < 1) throw new ArgumentOutOfRangeException(nameof(generation));
        var current = Sessions is null ? null : CaptureRuntimeView();
        lock (viewGate)
        {
            if (_disposal is not null) throw new ObjectDisposedException(nameof(OfflineSessionProfile));
            ProfileViewLifetime.Hold hold;
            Func<ProfileRuntimeView> capture;
            if (current is null)
            {
                if (startupViewTransferred) throw new InvalidOperationException("Initial runtime ownership was already transferred.");
                startupViewLifetime ??= new(_extension);
                startupViewHold ??= startupViewLifetime.Acquire();
                hold = startupViewHold; capture = CaptureStartupRuntimeView;
            }
            else { hold = current.Lifetime.Acquire(); capture = () => current; }
            var ownership = new ProfileRuntimeViewOwnership(this, nativeResources, hold, capture, generation);
            viewOwnerships.Add(ownership);
            if (current is null) startupViewTransferred = true;
            return ownership;
        }
    }

    // Used by synchronous attachment and by the parent-owned asynchronous MCP hook.
    // A runtime factory may already have bound this exact attachment.
    internal void AttachRuntimeView(ReplaceableAgentSession owner)
    {
        var attachment = owner.Current;
        lock (viewGate) if (runtimeViews.ContainsKey(attachment)) return;
        BindRuntimeView(owner, attachment, CaptureStartupRuntimeView());
    }
    internal void BindRuntimeView(ReplaceableAgentSession owner, AgentSessionAttachment attachment, ProfileRuntimeView view)
    {
        if (!ReferenceEquals(owner.Current, attachment)) throw new InvalidOperationException("View binding requires the exact current attachment.");
        lock (viewGate)
        {
            if (runtimeViews.TryGetValue(attachment, out var existing) && !ReferenceEquals(existing, view))
                throw new InvalidOperationException("Attachment already has another profile view.");
            BindEffectiveSettingsAttachment(attachment, view);
            BindOriginalPromptAttachment(attachment, view);
            runtimeViews[attachment] = view;
            runtimeViewAttached = true;
        }
        view.Lifetime.BindOnce(() =>
        {
            if (view.Extension is not null)
            {
                var foreign = profileHooks is { } priorHooks && ReferenceEquals(priorHooks.Owner, owner)
                    ? priorHooks.HasForeignHooks()
                    : owner.BeforeReplacement is not null || owner.AfterReplacement is not null ||
                      owner.BeforeRetirement is not null || owner.ValidateTargetAttachment is not null || owner.BeforeCreation is not null;
                if (foreign) throw new InvalidOperationException("Profile activation cannot replace another runtime's owner callbacks.");
                BindSettingsThinkingReads(view.Extension);
                BindProfileTreeRunner(view.Extension, view);
                view.Extension.BindLifecycleHandoffs(this);
                BindOriginalPromptReads(view.Extension);
                BindProfileSessionBehaviors(view.Extension, view);
                view.Extension.AttachOwner(owner);
                profileHooks = new(owner);
            }
            else if (profileHooks is { } retiringHooks && ReferenceEquals(retiringHooks.Owner, owner))
            {
                retiringHooks.ClearOwned();
                profileHooks = null;
            }
        });
    }
    internal void ForgetRuntimeView(AgentSessionAttachment attachment, ProfileRuntimeView view)
    {
        lock (viewGate)
            if (runtimeViews.TryGetValue(attachment, out var actual) && ReferenceEquals(actual, view))
            {
                ForgetEffectiveSettingsAttachment(attachment, view);
                ForgetOriginalPromptAttachment(attachment);
                runtimeViews.Remove(attachment);
            }
    }

    /// <summary>Prepare one fresh built-in view and its SAME runtime lease. Caller owns
    /// nativeResources/activation until this returns. No new activation is acquired here.</summary>
    internal PreparedNativeHostReload PrepareRuntimeView(AgentSessionAttachment candidate,
        NativeExtensionActivation? extension, PromptTemplateCliBinding? prompts, SkillCliBinding? skills,
        Func<SkillDiagnostic, CancellationToken, ValueTask>? report, SessionRuntimeRegistry registry,
        IAsyncDisposable nativeResources, Action<ReplaceableAgentSession, AgentSessionAttachment>? bindNative,
        Action commitPreparedRegistry)
    {
        ArgumentNullException.ThrowIfNull(candidate); ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(nativeResources); ArgumentNullException.ThrowIfNull(commitPreparedRegistry);
        if (bindNative?.GetInvocationList().Length > 1 || commitPreparedRegistry.GetInvocationList().Length != 1 ||
            report?.GetInvocationList().Length > 1) throw new ArgumentException("One callback per runtime-view operation is required.");
        var current = CaptureRuntimeView();
        if (extension is not null && ReferenceEquals(extension, current.Extension))
            throw new InvalidOperationException("Reload requires a fresh activation lifetime.");
        lock (viewGate)
        {
            if (extension is not null && !preparedExtensions.Contains(extension))
                throw new InvalidOperationException("Prepared extension must bind to this profile's exact final policy.");
            if (runtimeViews.ContainsKey(candidate)) throw new InvalidOperationException("Candidate view is already staged.");
            RequireOriginalPromptRegistry(registry);
            var lifetime = new ProfileViewLifetime(extension);
            var view = new ProfileRuntimeView(extension, prompts, skills, report, lifetime, registry);
            var ownership = new ProfileRuntimeViewOwnership(this, nativeResources, lifetime.Acquire(), () => view, candidate.Generation,
                () => ForgetRuntimeView(candidate, view));
            var runtime = new SessionRuntimeLease(registry, ownership, (owner, attachment) =>
            {
                if (!ReferenceEquals(attachment, candidate)) throw new InvalidOperationException("Prepared profile view attachment changed.");
                bindNative?.Invoke(owner, attachment); ownership.BindOwner(owner, attachment);
            });
            runtimeViews.Add(candidate, view); viewOwnerships.Add(ownership);
            return new(runtime, () =>
            {
                commitPreparedRegistry();
                _policy.ExtensionTargets = extension?.Targets ?? ImmutableDictionary<string, string>.Empty;
            });
        }
    }
    private async Task CloseProfileViewsAsync()
    {
        // Includes the startup activation when no runtime ever adopted its hold.
        using var callback = EnterViewCleanupCallback();
        ProfileRuntimeViewOwnership[] ownerships; ProfileViewLifetime.Hold? startup;
        lock (viewGate)
        {
            ownerships = viewOwnerships.ToArray();
            startup = startupViewTransferred ? null : startupViewHold;
        }
        var failures = new List<Exception>();
        foreach (var ownership in ownerships)
            try { await ProfileViewOriginal.Join(ownership).ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        if (startup is not null)
            try { await ProfileViewOriginal.Join(startup).ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        else if (ownerships.Length == 0 && _extension is not null)
            try { await ProfileViewOriginal.Join(_extension).ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        if (failures.Count != 0) throw new AggregateException(failures);
    }
}
