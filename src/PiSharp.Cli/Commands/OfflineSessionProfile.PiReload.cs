// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/agent-session.ts (reload: session_shutdown with reason
// "reload", the extension runner invalidated and reloaded, settingsManager.reload(), resourceLoader.reload() for skills, prompt templates
// and context files, the system prompt rebuilt, then session_start with reason "reload") and packages/coding-agent/src/modes/
// interactive/interactive-mode.ts (handleReloadCommand).
using PiSharp.Cli.Extensions;
using PiSharp.Cli.Prompts;
using PiSharp.Cli.Skills;

namespace PiSharp.Cli.Commands;

/// <summary>The resources a Pi reload loads again: the system prompt admission (custom and appended prompts, context files, skills),
/// the skills and the prompt templates.</summary>
internal sealed record PiReloadedResources(OriginalSystemPromptAdmission SystemPrompt, SkillCliBinding? Skills, PromptTemplateCliConfiguration Prompts);

internal sealed partial class OfflineSessionProfile
{
    private readonly SemaphoreSlim piReload = new(1, 1);

    /// <summary>The Pi entry's resource loader for a reload (null outside the Pi entry).</summary>
    internal Func<NativeExtensionActivation?, CancellationToken, Task<PiReloadedResources>>? PiReloadResources { get; set; }

    /// <summary>Whether this profile runs Pi extensions or Pi resources that a reload refreshes.</summary>
    internal bool SupportsPiReload => PiReloadResources is not null || _extension?.Pi is not null;

    /// <summary>agent-session.ts reload(), in place: the session and its runtime stay; the Node extensions reload in a fresh runtime and
    /// their registrations replace the old ones, and the prompt templates, skills and system prompt inputs are read again. The next
    /// prompt carries the rebuilt system prompt.</summary>
    internal async Task PiReloadAsync(CancellationToken token)
    {
        await piReload.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var owner = Sessions ?? throw new InvalidOperationException("Reload requires an attached session.");
            var attachment = owner.Current;
            var view = CaptureRuntimeView(attachment);
            var activation = view.Extension;
            if (activation is not null)
                await activation.DispatchSessionShutdownAsync(activation.CaptureShutdownSessionSnapshot(attachment), "reload").ConfigureAwait(false);
            if (activation?.Pi is { } host) await host.ReloadExtensionsAsync(token).ConfigureAwait(false);
            if (PiReloadResources is { } load) await ApplyPiResourcesAsync(await load(activation, token).ConfigureAwait(false), attachment, view, token).ConfigureAwait(false);
            if (activation is not null) await activation.DispatchSessionStartAsync("reload", token).ConfigureAwait(false);
        }
        finally { piReload.Release(); }
    }

    private async Task ApplyPiResourcesAsync(PiReloadedResources resources, PiSharp.CodingAgent.AgentSessionAttachment attachment, ProfileRuntimeView view,
        CancellationToken token)
    {
        var extension = view.Extension;
        var prompts = resources.Prompts.Selections.IsEmpty ? null : await PromptTemplateCliBinding.LoadAsync(resources.Prompts, extension?.RawInputHandlers,
            extension, extension, PromptTemplateFrontendDecoder.Decode, token: token).ConfigureAwait(false);
        var inherited = CaptureOriginalSystemPrompt(attachment);
        var snapshot = OriginalSystemPromptBuilder.Capture(resources.SystemPrompt, Workspace, inherited.SelectedTools);
        lock (viewGate)
        {
            view.Prompts = prompts;
            view.Skills = resources.Skills;
            var slot = new OriginalPromptSlot(snapshot, 2);
            originalPromptViews.AddOrUpdate(view, slot);
            originalPromptAttachments[attachment] = slot;
        }
    }
}
