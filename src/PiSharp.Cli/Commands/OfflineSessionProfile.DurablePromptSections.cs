using PiSharp.Cli.Prompts;
using PiSharp.CodingAgent;

namespace PiSharp.Cli.Commands;

internal sealed partial class OfflineSessionProfile
{
    internal SessionRuntimeRegistry DecorateDurablePromptRegistry(SessionRuntimeRegistry registry)
        => registry.UsesPromptSectionPreparation(PrepareDurablePromptSections) ? registry :
            registry.WithPromptSectionPreparation(PrepareDurablePromptSections);

    private SessionPromptSectionPreparation? PrepareDurablePromptSections(SessionPromptSectionRequest request)
    {
        var owner = Sessions;
        var expected = owner?.Current;
        OriginalPromptSlot? slot = null;
        OriginalSystemPromptSnapshot snapshot;
        lock (viewGate)
        {
            if (_disposal is not null) throw new ObjectDisposedException(nameof(OfflineSessionProfile));
            if (expected is null) snapshot = startupOriginalPrompt;
            else
            {
                if (!runtimeViews.TryGetValue(expected, out var view) || !originalPromptViews.TryGetValue(view, out slot) ||
                    !originalPromptAttachments.TryGetValue(expected, out var attached) || !ReferenceEquals(slot, attached) ||
                    Volatile.Read(ref slot.State) != 2) throw new InvalidOperationException("Exact acknowledged prompt source required.");
                snapshot = slot.Snapshot;
            }
        }
        if (snapshot.NativeLiteralBaseline) return null;
        var selected = snapshot with { SelectedTools = request.SelectedTools,
            Input = snapshot.Input with { SelectedTools = request.SelectedTools, HiddenTools = request.HiddenTools } };
        return new(slot is null ? snapshot : slot, OriginalSystemPromptBuilder.Sections(selected), () =>
        {
            if (!ReferenceEquals(Sessions, owner) || !ReferenceEquals(owner?.Current, expected))
                throw new InvalidOperationException("Prompt source crossed attachment publication.");
            lock (viewGate)
            {
                if (_disposal is not null) throw new ObjectDisposedException(nameof(OfflineSessionProfile));
                if (expected is null)
                { if (!ReferenceEquals(startupOriginalPrompt, snapshot)) throw new InvalidOperationException("Startup prompt source changed."); }
                else if (!runtimeViews.TryGetValue(expected, out var view) || !originalPromptViews.TryGetValue(view, out var actual) ||
                    !ReferenceEquals(actual, slot) || Volatile.Read(ref actual.State) != 2)
                    throw new InvalidOperationException("Prompt view changed before durable write.");
            }
        });
    }
}
