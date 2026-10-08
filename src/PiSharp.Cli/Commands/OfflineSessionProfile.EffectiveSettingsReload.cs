using System.Runtime.CompilerServices;
using PiSharp.CodingAgent;
using PiSharp.CodingAgent.Configuration;

namespace PiSharp.Cli.Commands;

internal sealed partial class OfflineSessionProfile
{
    private StartupSettingsSnapshot? startupEffectiveSettings;
    private bool effectiveSettingsConfigured;
    // An acknowledged view capture survives retirement of a navigation predecessor.
    // Weak keys do not extend the native view lifetime; attachment access is still separate.
    private readonly ConditionalWeakTable<ProfileRuntimeView, EffectiveSettingsSlot> effectiveSettingsByView = new();
    private readonly Dictionary<AgentSessionAttachment, EffectiveSettingsSlot> effectiveSettingsSlots = new(ReferenceEqualityComparer.Instance);

    private sealed class EffectiveSettingsSlot(AgentSessionAttachment attachment, ProfileRuntimeView view,
        StartupSettingsSnapshot? snapshot, bool supplied, int state)
    {
        internal readonly AgentSessionAttachment Attachment = attachment;
        internal readonly long Generation = attachment.Generation;
        internal readonly ProfileRuntimeView View = view;
        internal readonly StartupSettingsSnapshot? Snapshot = snapshot;
        internal readonly bool SuppliedByReload = supplied;
        // 0 staged, 1 original commit entered, 2 acknowledged, -1 original commit failed.
        internal int State = state;
    }

    internal void ConfigureEffectiveSettings(StartupSettingsSnapshot? settings)
    {
        lock (viewGate)
        {
            if (effectiveSettingsConfigured || runtimeViewAttached || Sessions is not null || _disposal is not null)
                throw new InvalidOperationException("Effective startup settings require the unattached live profile exactly once.");
            startupEffectiveSettings = settings;
            effectiveSettingsConfigured = true;
        }
    }

    internal StartupSettingsSnapshot? CaptureStartupEffectiveSettings()
    {
        lock (viewGate)
        {
            if (runtimeViewAttached || Sessions is not null || _disposal is not null)
                throw new InvalidOperationException("Startup settings are available only before actual attachment.");
            return startupEffectiveSettings;
        }
    }

    internal StartupSettingsSnapshot? CaptureEffectiveSettings(AgentSessionAttachment expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var owner = Sessions ?? throw new InvalidOperationException("Effective settings require an attached owner.");
        if (!ReferenceEquals(owner.Current, expected))
            throw new InvalidOperationException("Effective settings attachment is stale or foreign.");
        StartupSettingsSnapshot? result;
        lock (viewGate)
        {
            if (!runtimeViews.TryGetValue(expected, out var view) ||
                !effectiveSettingsSlots.TryGetValue(expected, out var slot) ||
                !ReferenceEquals(slot.Attachment, expected) || slot.Generation != expected.Generation ||
                !ReferenceEquals(slot.View, view) || Volatile.Read(ref slot.State) != 2)
                throw new InvalidOperationException("Effective settings have no acknowledged exact attachment slot.");
            result = slot.Snapshot;
        }
        if (!ReferenceEquals(owner.Current, expected))
            throw new InvalidOperationException("Effective settings attachment changed during capture.");
        return result;
    }

    // Called under viewGate as part of the existing exact view binding. Navigation may
    // share an acknowledged view, but it still receives its own attachment-keyed slot.
    private void BindEffectiveSettingsAttachment(AgentSessionAttachment attachment, ProfileRuntimeView view)
    {
        if (effectiveSettingsSlots.TryGetValue(attachment, out var staged))
        {
            if (!ReferenceEquals(staged.Attachment, attachment) || staged.Generation != attachment.Generation ||
                !ReferenceEquals(staged.View, view) || Volatile.Read(ref staged.State) != 2)
                throw new InvalidOperationException("Settings binding requires the acknowledged exact candidate.");
            return;
        }
        if (effectiveSettingsByView.TryGetValue(view, out var shared))
        {
            if (Volatile.Read(ref shared.State) != 2)
                throw new InvalidOperationException("Navigation requires an acknowledged exact view snapshot.");
            effectiveSettingsSlots.Add(attachment, new(attachment, view, shared.Snapshot, false, 2));
        }
        else
        {
            var initial = new EffectiveSettingsSlot(attachment, view, startupEffectiveSettings, false, 2);
            effectiveSettingsByView.Add(view, initial);
            effectiveSettingsSlots.Add(attachment, initial);
        }
    }
    private void ForgetEffectiveSettingsAttachment(AgentSessionAttachment attachment, ProfileRuntimeView view)
    {
        if (effectiveSettingsSlots.TryGetValue(attachment, out var slot) && ReferenceEquals(slot.View, view))
            effectiveSettingsSlots.Remove(attachment);
    }

    // The actual session transaction invokes the returned callback before switching
    // Current. All slot allocation is done here, before that callback can run.
    private Action PrepareEffectiveSettingsCommit(AgentSessionAttachment previous,
        AgentSessionAttachment candidate, StartupSettingsSnapshot? supplied, Action original)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (original.GetInvocationList().Length != 1 || candidate.Generation <= previous.Generation)
            throw new ArgumentException("Settings publication requires one commit and a later candidate generation.");
        var inherited = CaptureEffectiveSettings(previous);
        EffectiveSettingsSlot slot;
        lock (viewGate)
        {
            if (!runtimeViews.TryGetValue(candidate, out var view) || effectiveSettingsSlots.ContainsKey(candidate) ||
                effectiveSettingsByView.TryGetValue(view, out _))
                throw new InvalidOperationException("Settings candidate must have one freshly staged exact view.");
            slot = new(candidate, view, supplied ?? inherited, supplied is not null, 0);
            effectiveSettingsByView.Add(view, slot);
            effectiveSettingsSlots.Add(candidate, slot);
        }
        return () =>
        {
            var owner = Sessions ?? throw new InvalidOperationException("Settings publication lost its owner.");
            if (!ReferenceEquals(owner.Current, previous))
                throw new InvalidOperationException("Settings publication predecessor changed.");
            lock (viewGate)
            {
                if (!effectiveSettingsSlots.TryGetValue(candidate, out var current) || !ReferenceEquals(current, slot) ||
                    !runtimeViews.TryGetValue(candidate, out var currentView) || !ReferenceEquals(currentView, slot.View) ||
                    !ReferenceEquals(slot.Attachment, candidate) || slot.Generation != candidate.Generation ||
                    Interlocked.CompareExchange(ref slot.State, 1, 0) != 0)
                    throw new InvalidOperationException("Settings publication is stale, foreign or already attempted.");
            }
            try { original(); }
            catch { Volatile.Write(ref slot.State, -1); throw; }
            // No allocation, validation, callbacks or cancellation observation after
            // the acknowledged registry commit. The engine next switches Current.
            Volatile.Write(ref slot.State, 2);
        };
    }
}