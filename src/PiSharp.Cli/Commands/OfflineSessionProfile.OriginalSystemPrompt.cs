using System.Runtime.CompilerServices;
using System.Collections.Immutable;
using PiSharp.Cli.Prompts;
using PiSharp.Cli.Reloading;
using PiSharp.CodingAgent;
using PiSharp.Contracts;

namespace PiSharp.Cli.Commands;

internal sealed partial class OfflineSessionProfile
{
    private OriginalSystemPromptSnapshot startupOriginalPrompt = null!;
    private readonly ConditionalWeakTable<ProfileRuntimeView, OriginalPromptSlot> originalPromptViews = new();
    private readonly Dictionary<AgentSessionAttachment, OriginalPromptSlot> originalPromptAttachments = new(ReferenceEqualityComparer.Instance);
    private readonly ConditionalWeakTable<NativeHostReloadPayload, OriginalSystemPromptAdmission> originalPromptReloadInputs = new();
    private sealed class OriginalPromptSlot(OriginalSystemPromptSnapshot snapshot, int state)
    { internal readonly OriginalSystemPromptSnapshot Snapshot = snapshot; internal int State = state; }

    // Separate typed carrier keyed to the exact host payload; never reads opaque payload.State.
    internal void AdmitOriginalSystemPromptReload(NativeHostReloadPayload payload, OriginalSystemPromptAdmission input)
    {
        ArgumentNullException.ThrowIfNull(payload); ArgumentNullException.ThrowIfNull(input);
        var frozen = OriginalSystemPromptBuilder.Capture(input, Workspace, []).Input;
        lock (viewGate)
        {
            if (_disposal is not null) throw new ObjectDisposedException(nameof(OfflineSessionProfile));
            if (originalPromptReloadInputs.TryGetValue(payload, out _)) throw new InvalidOperationException("Exact prompt payload already admitted.");
            originalPromptReloadInputs.Add(payload, frozen);
        }
    }
    private void BindOriginalPromptAttachment(AgentSessionAttachment attachment, ProfileRuntimeView view)
    {
        if (!originalPromptViews.TryGetValue(view, out var slot))
        { slot = new(startupOriginalPrompt, 2); originalPromptViews.Add(view, slot); }
        if (Volatile.Read(ref slot.State) != 2) throw new InvalidOperationException("Prompt view is not acknowledged.");
        if (originalPromptAttachments.TryGetValue(attachment, out var previous) && !ReferenceEquals(previous, slot))
            throw new InvalidOperationException("Prompt attachment already owns another view.");
        originalPromptAttachments[attachment] = slot;
    }
    private void ForgetOriginalPromptAttachment(AgentSessionAttachment attachment) => originalPromptAttachments.Remove(attachment);
    internal OriginalSystemPromptSnapshot CaptureOriginalSystemPrompt(AgentSessionAttachment expected)
    {
        var owner = Sessions ?? throw new InvalidOperationException("Prompt options require the actual attached owner.");
        if (!ReferenceEquals(owner.Current, expected)) throw new InvalidOperationException("Stale or foreign prompt attachment.");
        OriginalSystemPromptSnapshot snapshot;
        lock (viewGate)
        {
            if (!runtimeViews.TryGetValue(expected, out var view) || !originalPromptViews.TryGetValue(view, out var slot) ||
                !originalPromptAttachments.TryGetValue(expected, out var attached) || !ReferenceEquals(attached, slot) || Volatile.Read(ref slot.State) != 2)
                throw new InvalidOperationException("Prompt options require the exact acknowledged runtime view.");
            snapshot = slot.Snapshot;
        }
        // Read acknowledged executable selection, including while reload holds its
        // replacement reservation. Do not call ordinary mutation availability APIs.
        var tools = expected.Session.Snapshot.Agent.Tools.Select(tool => tool.Name).ToImmutableArray();
        if (!ReferenceEquals(owner.Current, expected)) throw new InvalidOperationException("Prompt capture crossed replacement.");
        return snapshot with { SelectedTools = tools, Input = snapshot.Input with { SelectedTools = tools } };
    }
    private void BindOriginalPromptReads(PiSharp.Cli.Extensions.NativeExtensionActivation activation)
        => activation.ConfigureOriginalSystemPromptReads(attachment => CaptureOriginalSystemPrompt(attachment).Options);

    private Action PrepareOriginalPromptCommit(AgentSessionAttachment previous, AgentSessionAttachment candidate,
        NativeHostReloadPayload payload, Action original)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (original.GetInvocationList().Length != 1 || candidate.Generation <= previous.Generation)
            throw new ArgumentException("One prompt commit and a later exact candidate required.");
        var inherited = CaptureOriginalSystemPrompt(previous); OriginalPromptSlot slot; ProfileRuntimeView stagedView;
        lock (viewGate)
        {
            if (!runtimeViews.TryGetValue(candidate, out var view) || originalPromptViews.TryGetValue(view, out _))
                throw new InvalidOperationException("Prompt publication requires a fresh staged view.");
            var snapshot = originalPromptReloadInputs.TryGetValue(payload, out var supplied)
                ? OriginalSystemPromptBuilder.Capture(supplied, Workspace, inherited.SelectedTools) : inherited;
            stagedView = view; slot = new(snapshot, 0); originalPromptViews.Add(view, slot); originalPromptAttachments.Add(candidate, slot);
        }
        return () =>
        {
            if (!ReferenceEquals(Sessions!.Current, previous)) throw new InvalidOperationException("Prompt publication predecessor changed.");
            lock (viewGate)
            {
                if (!runtimeViews.TryGetValue(candidate, out var actualView) || !ReferenceEquals(actualView, stagedView) ||
                    !originalPromptViews.TryGetValue(actualView, out var actualSlot) || !ReferenceEquals(actualSlot, slot) ||
                    !originalPromptAttachments.TryGetValue(candidate, out var attached) || !ReferenceEquals(attached, slot) ||
                    Interlocked.CompareExchange(ref slot.State, 1, 0) != 0)
                    throw new InvalidOperationException("Prompt commit is stale, foreign or already attempted.");
            }
            try { original(); } catch { Volatile.Write(ref slot.State, -1); throw; }
            Volatile.Write(ref slot.State, 2); // No allocation/callback/throw after acknowledged original registry commit.
        };
    }
}
